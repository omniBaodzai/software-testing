using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Aura.API.Services.BackgroundJobs;

/// <summary>
/// Background Worker Service cho Database Backup (NFR-6)
/// Tự động backup database hằng ngày
/// </summary>
public class DatabaseBackupWorker
{
    private readonly ILogger<DatabaseBackupWorker> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _backupDirectory;

    public DatabaseBackupWorker(
        ILogger<DatabaseBackupWorker> logger,
        IConfiguration configuration)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        
        // Get backup directory from config or use default
        _backupDirectory = _configuration["Backup:Directory"] ?? "database/backups";
        
        // Ensure backup directory exists
        if (!Directory.Exists(_backupDirectory))
        {
            Directory.CreateDirectory(_backupDirectory);
            _logger.LogInformation("Created backup directory: {Directory}", _backupDirectory);
        }
    }

    /// <summary>
    /// Daily database backup job (NFR-6)
    /// Recurring job chạy mỗi ngày lúc 3:00 AM
    /// 
    /// Giá trị: Automated daily backup, đảm bảo data safety và recovery
    /// </summary>
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = new[] { 300, 600 })] // Retry after 5 min, 10 min
    public async Task PerformDailyBackupAsync()
    {
        _logger.LogInformation("[Hangfire] Starting daily database backup...");

        try
        {
            var connectionString = _configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException("Database connection string not configured");
            }

            // Parse connection string to extract database info
            var dbHost = ExtractConnectionStringValue(connectionString, "Host") ?? "localhost";
            var dbPort = ExtractConnectionStringValue(connectionString, "Port") ?? "5432";
            var dbName = ExtractConnectionStringValue(connectionString, "Database") ?? "aura_db";
            var dbUser = ExtractConnectionStringValue(connectionString, "Username") ?? "aura_user";
            var dbPassword = ExtractConnectionStringValue(connectionString, "Password") ?? "";

            // Check if running in Docker environment
            var isDockerEnvironment = _configuration.GetValue<bool>("Backup:UseDocker", true);
            
            string backupFilePath;
            if (isDockerEnvironment)
            {
                // Use docker exec to run pg_dump inside postgres container
                backupFilePath = await BackupUsingDockerAsync(dbName, dbUser);
            }
            else
            {
                // Use pg_dump directly (requires pg_dump installed on host)
                backupFilePath = await BackupUsingPgDumpAsync(dbHost, dbPort, dbName, dbUser, dbPassword);
            }

            // Only cleanup if backup was successful
            if (!string.IsNullOrEmpty(backupFilePath))
            {
                // Cleanup old backups (keep last 30 days)
                await CleanupOldBackupsAsync();
                _logger.LogInformation("[Hangfire] Daily database backup completed successfully. File: {FilePath}", backupFilePath);
            }
            else
            {
                _logger.LogWarning("[Hangfire] Backup skipped (pg_dump not available in container). This is non-critical.");
            }
        }
        catch (Exception ex)
        {
            // Log error but don't throw - backup is non-critical for system operation
            _logger.LogWarning(ex, "[Hangfire] Error performing daily database backup (non-critical, continuing)");
            // Don't throw - allow system to continue operating
        }
    }

    private async Task<string> BackupUsingDockerAsync(string dbName, string dbUser)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var fileName = $"backup_{timestamp}.sql";
        var backupFilePath = Path.Combine(_backupDirectory, fileName);

        try
        {
            // Try to use pg_dump directly first (if available in container)
            // If running inside Docker container, pg_dump might be available via PATH
            var pgDumpPath = _configuration["Backup:PgDumpPath"] ?? "pg_dump";
            
            // Check if we're inside a Docker container (common indicators)
            var isInsideContainer = File.Exists("/.dockerenv") || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKER_CONTAINER"));
            
            if (isInsideContainer)
            {
                // Inside container: try direct pg_dump connection to postgres service
                _logger.LogInformation("Running inside Docker container, using direct pg_dump connection...");
                return await BackupUsingPgDumpDirectAsync(dbName, dbUser);
            }
            else
            {
                // On host: try docker-compose exec
                var dockerComposeFile = _configuration["Backup:DockerComposeFile"] ?? "docker-compose.yml";
                var postgresService = _configuration["Backup:PostgresService"] ?? "postgres";

                var processInfo = new ProcessStartInfo
                {
                    FileName = "docker-compose",
                    Arguments = $"-f {dockerComposeFile} exec -T {postgresService} pg_dump -U {dbUser} {dbName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = processInfo };
                
                _logger.LogInformation("Executing docker-compose backup command...");
                process.Start();

                // Read output and write to file
                using var fileStream = new FileStream(backupFilePath, FileMode.Create, FileAccess.Write);
                await process.StandardOutput.BaseStream.CopyToAsync(fileStream);

                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    var error = await process.StandardError.ReadToEndAsync();
                    throw new InvalidOperationException($"pg_dump failed with exit code {process.ExitCode}: {error}");
                }

                var fileInfo = new FileInfo(backupFilePath);
                _logger.LogInformation("Backup file created: {FilePath}, Size: {Size} bytes", backupFilePath, fileInfo.Length);

                return backupFilePath;
            }
        }
        catch (Win32Exception ex) when (ex.Message.Contains("No such file or directory"))
        {
            // docker-compose not found, fallback to direct pg_dump
            _logger.LogWarning("docker-compose not found, falling back to direct pg_dump connection");
            return await BackupUsingPgDumpDirectAsync(dbName, dbUser);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error creating backup using Docker (non-critical). Falling back to direct pg_dump.");
            // Fallback to direct pg_dump instead of throwing
            return await BackupUsingPgDumpDirectAsync(dbName, dbUser);
        }
    }

    private async Task<string> BackupUsingPgDumpDirectAsync(string dbName, string dbUser)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var fileName = $"backup_{timestamp}.sql";
        var backupFilePath = Path.Combine(_backupDirectory, fileName);

        try
        {
            var connectionString = _configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException("Database connection string not configured");
            }

            // Extract connection info
            var dbHost = ExtractConnectionStringValue(connectionString, "Host") ?? "postgres";
            var dbPort = ExtractConnectionStringValue(connectionString, "Port") ?? "5432";
            var dbPassword = ExtractConnectionStringValue(connectionString, "Password") ?? "";

            // Use pg_dump with connection string
            var pgDumpPath = _configuration["Backup:PgDumpPath"] ?? "pg_dump";
            var connectionUri = $"postgresql://{dbUser}:{dbPassword}@{dbHost}:{dbPort}/{dbName}";

            var processInfo = new ProcessStartInfo
            {
                FileName = pgDumpPath,
                Arguments = $"--dbname=\"{connectionUri}\" --format=plain --file=\"{backupFilePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                EnvironmentVariables = { ["PGPASSWORD"] = dbPassword }
            };

            using var process = new Process { StartInfo = processInfo };
            
            _logger.LogInformation("Executing pg_dump directly...");
            process.Start();

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                _logger.LogWarning("pg_dump failed: {Error}. Skipping backup (non-critical).", error);
                // Don't throw - backup is non-critical, just log warning
                return string.Empty; // Return empty to indicate skip
            }

            var fileInfo = new FileInfo(backupFilePath);
            _logger.LogInformation("Backup file created: {FilePath}, Size: {Size} bytes", backupFilePath, fileInfo.Length);

            return backupFilePath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Direct pg_dump backup failed (non-critical). Skipping backup.");
            // Don't throw - backup is non-critical for system operation
            return string.Empty; // Return empty to indicate skip
        }
    }

    private async Task<string> BackupUsingPgDumpAsync(string dbHost, string dbPort, string dbName, string dbUser, string dbPassword)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var fileName = $"backup_{timestamp}.sql";
        var backupFilePath = Path.Combine(_backupDirectory, fileName);

        try
        {
            // Set PGPASSWORD environment variable for pg_dump
            var processInfo = new ProcessStartInfo
            {
                FileName = "pg_dump",
                Arguments = $"-h {dbHost} -p {dbPort} -U {dbUser} -d {dbName} -F c -f \"{backupFilePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                EnvironmentVariables = { ["PGPASSWORD"] = dbPassword }
            };

            using var process = new Process { StartInfo = processInfo };
            
            _logger.LogInformation("Executing pg_dump backup command...");
            process.Start();

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException($"pg_dump failed with exit code {process.ExitCode}: {error}");
            }

            var fileInfo = new FileInfo(backupFilePath);
            _logger.LogInformation("Backup file created: {FilePath}, Size: {Size} bytes", backupFilePath, fileInfo.Length);

            return backupFilePath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating backup using pg_dump");
            throw;
        }
    }

    private async Task CleanupOldBackupsAsync()
    {
        try
        {
            var retentionDays = _configuration.GetValue<int>("Backup:RetentionDays", 30);
            var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);

            var backupFiles = Directory.GetFiles(_backupDirectory, "backup_*.sql");
            var deletedCount = 0;

            foreach (var file in backupFiles)
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.CreationTimeUtc < cutoffDate)
                {
                    File.Delete(file);
                    deletedCount++;
                    _logger.LogDebug("Deleted old backup file: {FileName}", fileInfo.Name);
                }
            }

            if (deletedCount > 0)
            {
                _logger.LogInformation("Cleaned up {Count} old backup files (older than {Days} days)", deletedCount, retentionDays);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error cleaning up old backups (non-critical)");
            // Don't throw - cleanup failure shouldn't fail the backup job
        }
    }

    private string? ExtractConnectionStringValue(string connectionString, string key)
    {
        var parts = connectionString.Split(';');
        foreach (var part in parts)
        {
            var kvp = part.Split('=', 2);
            if (kvp.Length == 2 && kvp[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return kvp[1].Trim();
            }
        }
        return null;
    }
}
