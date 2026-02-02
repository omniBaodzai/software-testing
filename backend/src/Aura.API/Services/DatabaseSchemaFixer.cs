using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aura.API.Services;

/// <summary>
/// Service tự động fix các cột thiếu trong database khi backend khởi động
/// Không cần migration files, fix trực tiếp vào database
/// </summary>
public class DatabaseSchemaFixer
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<DatabaseSchemaFixer> _logger;

    public DatabaseSchemaFixer(IConfiguration configuration, ILogger<DatabaseSchemaFixer> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Tự động fix các cột thiếu trong database
    /// </summary>
    public async Task FixMissingColumnsAsync()
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogWarning("Database connection string not configured, skipping schema fix");
            return;
        }

        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            _logger.LogInformation("Checking and fixing missing database columns...");

            // Fix IsPrimary column in patient_doctor_assignments
            await FixColumnIfNotExistsAsync(connection, 
                "patient_doctor_assignments", 
                "IsPrimary", 
                "BOOLEAN DEFAULT FALSE",
                "idx_patient_doctor_assignments_isprimary");

            // Fix TotalAnalyses column in user_packages
            await FixColumnIfNotExistsAsync(connection,
                "user_packages",
                "TotalAnalyses",
                "INTEGER NOT NULL DEFAULT 0",
                null);

            // Fix Features column in service_packages
            await FixColumnIfNotExistsAsync(connection,
                "service_packages",
                "Features",
                "TEXT",
                null);

            // Fix PatientUserId column in medical_notes (used by MedicalNotesController for patient-specific notes)
            await FixColumnIfNotExistsAsync(connection,
                "medical_notes",
                "PatientUserId",
                "VARCHAR(255)",
                null);

            // Fix IsPrivate column in medical_notes (used for filtering notes visible to patient)
            await FixColumnIfNotExistsAsync(connection,
                "medical_notes",
                "IsPrivate",
                "BOOLEAN DEFAULT FALSE",
                null);

            // Fix ViewedByPatientAt column in medical_notes (used to detect unread notes)
            await FixColumnIfNotExistsAsync(connection,
                "medical_notes",
                "ViewedByPatientAt",
                "TIMESTAMP",
                null);

            // Fix TreatmentPlan column in medical_notes (used by MedicalNotesController GetMyMedicalNotes)
            await FixColumnIfNotExistsAsync(connection,
                "medical_notes",
                "TreatmentPlan",
                "TEXT",
                null);

            // Fix ClinicalObservations column in medical_notes (used by MedicalNotesController GetMyMedicalNotes)
            await FixColumnIfNotExistsAsync(connection,
                "medical_notes",
                "ClinicalObservations",
                "TEXT",
                null);

            // Fix Description column in notifications (used by RiskAlertWorker abnormal trends alerts)
            await FixColumnIfNotExistsAsync(connection,
                "notifications",
                "Description",
                "TEXT NOT NULL DEFAULT ''",
                null);

            // Update existing user_packages to set TotalAnalyses from service_packages
            await UpdateTotalAnalysesFromServicePackagesAsync(connection);

            // Create privacy_settings table if not exists (FR-37)
            await CreatePrivacySettingsTableIfNotExistsAsync(connection);

            _logger.LogInformation("Database schema check completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fixing database schema (non-critical, continuing startup)");
            // Don't throw - allow application to start even if schema fix fails
        }
    }

    private async Task FixColumnIfNotExistsAsync(NpgsqlConnection connection, string tableName, string columnName, string columnDefinition, string? indexName)
    {
        try
        {
            // Check if column exists (case-insensitive)
            var checkSql = @"
                SELECT COUNT(*) 
                FROM information_schema.columns 
                WHERE table_name = LOWER(@TableName) AND LOWER(column_name) = LOWER(@ColumnName)";

            using var checkCmd = new NpgsqlCommand(checkSql, connection);
            checkCmd.Parameters.AddWithValue("TableName", tableName);
            checkCmd.Parameters.AddWithValue("ColumnName", columnName);

            var exists = (long)(await checkCmd.ExecuteScalarAsync() ?? 0L) > 0;

            if (!exists)
            {
                // Add column
                var addColumnSql = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition}";
                using var addCmd = new NpgsqlCommand(addColumnSql, connection);
                await addCmd.ExecuteNonQueryAsync();
                _logger.LogInformation("Added column {ColumnName} to table {TableName}", columnName, tableName);

                // Create index if specified
                if (!string.IsNullOrEmpty(indexName))
                {
                    try
                    {
                        var createIndexSql = $"CREATE INDEX IF NOT EXISTS {indexName} ON {tableName}({columnName})";
                        using var indexCmd = new NpgsqlCommand(createIndexSql, connection);
                        await indexCmd.ExecuteNonQueryAsync();
                        _logger.LogInformation("Created index {IndexName} on {TableName}.{ColumnName}", indexName, tableName, columnName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to create index {IndexName} (non-critical)", indexName);
                    }
                }
            }
            else
            {
                _logger.LogDebug("Column {ColumnName} already exists in table {TableName}", columnName, tableName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking/fixing column {ColumnName} in table {TableName} (non-critical)", columnName, tableName);
        }
    }

    private async Task UpdateTotalAnalysesFromServicePackagesAsync(NpgsqlConnection connection)
    {
        try
        {
            var updateSql = @"
                UPDATE user_packages up
                SET TotalAnalyses = sp.NumberOfAnalyses
                FROM service_packages sp
                WHERE up.PackageId = sp.Id 
                    AND (up.TotalAnalyses = 0 OR up.TotalAnalyses IS NULL)";

            using var cmd = new NpgsqlCommand(updateSql, connection);
            var rowsAffected = await cmd.ExecuteNonQueryAsync();
            
            if (rowsAffected > 0)
            {
                _logger.LogInformation("Updated TotalAnalyses for {Count} user_packages records", rowsAffected);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error updating TotalAnalyses from service_packages (non-critical)");
        }
    }

    private async Task CreatePrivacySettingsTableIfNotExistsAsync(NpgsqlConnection connection)
    {
        try
        {
            var createTableSql = @"
                CREATE TABLE IF NOT EXISTS privacy_settings (
                    Id VARCHAR(255) PRIMARY KEY,
                    EnableAuditLogging BOOLEAN DEFAULT TRUE,
                    AuditLogRetentionDays INTEGER DEFAULT 365,
                    AnonymizeOldLogs BOOLEAN DEFAULT FALSE,
                    RequireConsentForDataSharing BOOLEAN DEFAULT TRUE,
                    EnableGdprCompliance BOOLEAN DEFAULT TRUE,
                    DataRetentionDays INTEGER DEFAULT 2555,
                    AllowDataExport BOOLEAN DEFAULT TRUE,
                    RequireTwoFactorForSensitiveActions BOOLEAN DEFAULT FALSE,
                    IsActive BOOLEAN DEFAULT TRUE,
                    CreatedDate DATE DEFAULT CURRENT_DATE,
                    CreatedBy VARCHAR(255),
                    UpdatedDate DATE DEFAULT CURRENT_DATE,
                    UpdatedBy VARCHAR(255),
                    IsDeleted BOOLEAN DEFAULT FALSE
                )";

            using var cmd = new NpgsqlCommand(createTableSql, connection);
            await cmd.ExecuteNonQueryAsync();
            _logger.LogInformation("Created privacy_settings table (if not exists)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create privacy_settings table (non-critical)");
        }
    }
}
