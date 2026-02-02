using Aura.Application.DTOs.Images;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Collections.Concurrent;

namespace Aura.Application.Services.Analysis;

/// <summary>
/// Service for queuing and processing batch AI analysis jobs
/// Implements NFR-2: Support bulk processing (≥100 images per batch) with queued or parallel execution
/// Uses RabbitMQ for distributed job queue processing
/// </summary>
public class AnalysisQueueService : IAnalysisQueueService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AnalysisQueueService>? _logger;
    private readonly string _connectionString;
    private readonly IAnalysisService _analysisService;
    private readonly object? _rabbitMQService; // IRabbitMQService from Infrastructure
    private readonly ConcurrentDictionary<string, BatchAnalysisStatusDto> _activeJobs = new();

    public AnalysisQueueService(
        IConfiguration configuration,
        IAnalysisService analysisService,
        ILogger<AnalysisQueueService>? logger = null,
        object? rabbitMQService = null)
    {
        _configuration = configuration;
        _analysisService = analysisService;
        _logger = logger;
        _rabbitMQService = rabbitMQService;
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Database connection string not found");
    }

    public async Task<string> QueueBatchAnalysisAsync(string clinicId, List<string> imageIds, string? batchId = null)
    {
        if (imageIds == null || imageIds.Count == 0)
        {
            throw new ArgumentException("Image IDs list cannot be empty", nameof(imageIds));
        }

        var jobId = Guid.NewGuid().ToString();
        var actualBatchId = batchId ?? Guid.NewGuid().ToString();

        _logger?.LogInformation("Queueing batch analysis job {JobId} for clinic {ClinicId}, Images: {Count}",
            jobId, clinicId, imageIds.Count);

        // Create job record in database
        await CreateJobRecordAsync(jobId, actualBatchId, clinicId, imageIds);

        // Initialize status
        var status = new BatchAnalysisStatusDto
        {
            JobId = jobId,
            BatchId = actualBatchId,
            Status = "Queued",
            TotalImages = imageIds.Count,
            ProcessedCount = 0,
            SuccessCount = 0,
            FailedCount = 0,
            CreatedAt = DateTime.UtcNow,
            ImageIds = imageIds
        };

        _activeJobs[jobId] = status;

        // =====================================================================
        // RABBITMQ: Publish analysis job to queue for async processing
        // Benefits: Scalable, reliable, distributed processing
        // Note: RabbitMQ service is injected from Infrastructure layer via DI
        // =====================================================================
        // Fallback to fire-and-forget processing
        // RabbitMQ integration will be handled at API layer where IRabbitMQService is available
        _ = Task.Run(async () => await ProcessJobAsync(jobId, clinicId, imageIds));
        _logger?.LogInformation("Analysis job {JobId} queued for processing", jobId);

        return jobId;
    }

    public async Task<BatchAnalysisStatusDto?> GetBatchAnalysisStatusAsync(string jobId)
    {
        // Check in-memory cache first
        if (_activeJobs.TryGetValue(jobId, out var cachedStatus))
        {
            return cachedStatus;
        }

        // Load from database
        return await LoadJobStatusFromDatabaseAsync(jobId);
    }

    /// <summary>
    /// FR-24, NFR-2: Process queued batch analysis jobs from database
    /// Được gọi bởi Hangfire recurring job để xử lý các jobs đang chờ trong analysis_jobs table
    /// Hỗ trợ bulk processing ≥100 images per batch với parallel execution
    /// </summary>
    public async Task<int> ProcessQueuedJobsAsync(CancellationToken cancellationToken = default)
    {
        var processedCount = 0;
        
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            // Lấy các jobs có Status = 'Queued' và chưa bị xóa
            var sql = @"
                SELECT Id, BatchId, ClinicId, ImageIds, TotalImages
                FROM analysis_jobs
                WHERE Status = 'Queued' AND IsDeleted = false
                ORDER BY CreatedAt ASC
                LIMIT 10"; // Xử lý tối đa 10 jobs mỗi lần để tránh overload

            using var command = new NpgsqlCommand(sql, connection);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);

            var jobsToProcess = new List<(string JobId, string BatchId, string ClinicId, List<string> ImageIds)>();

            while (await reader.ReadAsync(cancellationToken))
            {
                var jobId = reader.GetString(0);
                var batchId = reader.GetString(1);
                var clinicId = reader.GetString(2);
                var imageIdsJson = reader.IsDBNull(3) ? "[]" : reader.GetString(3);
                var imageIds = System.Text.Json.JsonSerializer.Deserialize<List<string>>(imageIdsJson) ?? new List<string>();

                jobsToProcess.Add((jobId, batchId, clinicId, imageIds));
            }

            await reader.CloseAsync();

            if (jobsToProcess.Count == 0)
            {
                _logger?.LogDebug("No queued analysis jobs found");
                return 0;
            }

            _logger?.LogInformation("Found {Count} queued analysis jobs to process", jobsToProcess.Count);

            // Xử lý từng job (có thể parallel nếu cần, nhưng để sequential để tránh overload AI service)
            foreach (var (jobId, batchId, clinicId, imageIds) in jobsToProcess)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger?.LogWarning("Cancellation requested, stopping job processing");
                    break;
                }

                try
                {
                    // Khởi tạo status trong memory nếu chưa có
                    if (!_activeJobs.ContainsKey(jobId))
                    {
                        _activeJobs[jobId] = new BatchAnalysisStatusDto
                        {
                            JobId = jobId,
                            BatchId = batchId,
                            Status = "Queued",
                            TotalImages = imageIds.Count,
                            ProcessedCount = 0,
                            SuccessCount = 0,
                            FailedCount = 0,
                            CreatedAt = DateTime.UtcNow,
                            ImageIds = imageIds
                        };
                    }

                    // Process job (sẽ update status trong DB và memory)
                    await ProcessJobAsync(jobId, clinicId, imageIds);
                    processedCount++;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error processing queued job {JobId}", jobId);
                    
                    // Mark job as failed
                    try
                    {
                        await UpdateJobStatusAsync(jobId, "Failed", null, null, null);
                        await UpdateJobErrorMessageAsync(jobId, ex.Message);
                    }
                    catch (Exception updateEx)
                    {
                        _logger?.LogError(updateEx, "Failed to update job {JobId} status to Failed", jobId);
                    }
                }
            }

            _logger?.LogInformation("Processed {Count} queued analysis jobs", processedCount);
            return processedCount;
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            // Table analysis_jobs does not exist yet
            _logger?.LogWarning("Table analysis_jobs does not exist, skipping job processing");
            return 0;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in ProcessQueuedJobsAsync");
            throw;
        }
    }

    private async Task UpdateJobErrorMessageAsync(string jobId, string errorMessage)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                UPDATE analysis_jobs 
                SET ErrorMessage = @ErrorMessage
                WHERE Id = @Id";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("Id", jobId);
            command.Parameters.AddWithValue("ErrorMessage", errorMessage);

            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to update error message for job {JobId}", jobId);
        }
    }

    private async Task ProcessJobAsync(string jobId, string clinicId, List<string> imageIds)
    {
        if (!_activeJobs.TryGetValue(jobId, out var status))
        {
            _logger?.LogWarning("Job {JobId} not found in active jobs", jobId);
            return;
        }

        status.Status = "Processing";
        status.StartedAt = DateTime.UtcNow;
        await UpdateJobStatusAsync(jobId, "Processing", status.StartedAt);

        _logger?.LogInformation("Processing batch analysis job {JobId}, Images: {Count}", jobId, imageIds.Count);

        // Process images in batches to avoid overwhelming the AI service
        const int batchSize = 10; // Process 10 images at a time
        var semaphore = new SemaphoreSlim(batchSize);

        var processTasks = imageIds.Select(async (imageId, index) =>
        {
            await semaphore.WaitAsync();
            try
            {
                // Use clinicId as userId for clinic-uploaded images
                var result = await _analysisService.StartAnalysisAsync(clinicId, imageId);

                lock (status)
                {
                    status.ProcessedCount++;
                    if (result.Status == "Completed")
                    {
                        status.SuccessCount++;
                    }
                    else
                    {
                        status.FailedCount++;
                    }
                }

                _logger?.LogDebug("Processed image {ImageId} in job {JobId}, Status: {Status}",
                    imageId, jobId, result.Status);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error processing image {ImageId} in job {JobId}", imageId, jobId);
                lock (status)
                {
                    status.ProcessedCount++;
                    status.FailedCount++;
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(processTasks);

        status.Status = "Completed";
        status.CompletedAt = DateTime.UtcNow;
        await UpdateJobStatusAsync(jobId, "Completed", status.CompletedAt, status.SuccessCount, status.FailedCount);

        _logger?.LogInformation("Completed batch analysis job {JobId}, Success: {Success}, Failed: {Failed}",
            jobId, status.SuccessCount, status.FailedCount);
    }

    private async Task CreateJobRecordAsync(string jobId, string batchId, string clinicId, List<string> imageIds)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        // Create analysis_jobs table if it doesn't exist (we'll use a simple approach)
        // In production, you might want to create a proper table schema
        var sql = @"
            INSERT INTO analysis_jobs (
                Id, BatchId, ClinicId, Status, TotalImages, ProcessedCount, 
                SuccessCount, FailedCount, CreatedAt, ImageIds
            ) VALUES (
                @Id, @BatchId, @ClinicId, @Status, @TotalImages, @ProcessedCount,
                @SuccessCount, @FailedCount, @CreatedAt, @ImageIds::jsonb
            )";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", jobId);
        command.Parameters.AddWithValue("BatchId", batchId);
        command.Parameters.AddWithValue("ClinicId", clinicId);
        command.Parameters.AddWithValue("Status", "Queued");
        command.Parameters.AddWithValue("TotalImages", imageIds.Count);
        command.Parameters.AddWithValue("ProcessedCount", 0);
        command.Parameters.AddWithValue("SuccessCount", 0);
        command.Parameters.AddWithValue("FailedCount", 0);
        command.Parameters.AddWithValue("CreatedAt", DateTime.UtcNow);
        command.Parameters.AddWithValue("ImageIds", System.Text.Json.JsonSerializer.Serialize(imageIds));

        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01") // Table doesn't exist
        {
            await CreateAnalysisJobsTableAsync(connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task CreateAnalysisJobsTableAsync(NpgsqlConnection connection)
    {
        // Create table without FK on ClinicId so INSERT always succeeds (clinics table may not exist or Id format may differ)
        var createTableSql = @"
            CREATE TABLE IF NOT EXISTS analysis_jobs (
                Id VARCHAR(255) PRIMARY KEY,
                BatchId VARCHAR(255) NOT NULL,
                ClinicId VARCHAR(255) NOT NULL,
                Status VARCHAR(50) DEFAULT 'Queued' CHECK (Status IN ('Queued', 'Processing', 'Completed', 'Failed')),
                TotalImages INTEGER NOT NULL,
                ProcessedCount INTEGER DEFAULT 0,
                SuccessCount INTEGER DEFAULT 0,
                FailedCount INTEGER DEFAULT 0,
                CreatedAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                StartedAt TIMESTAMP,
                CompletedAt TIMESTAMP,
                ImageIds JSONB,
                ErrorMessage TEXT,
                CreatedDate DATE DEFAULT CURRENT_DATE,
                IsDeleted BOOLEAN DEFAULT FALSE
            )";

        using var command = new NpgsqlCommand(createTableSql, connection);
        await command.ExecuteNonQueryAsync();
        _logger?.LogInformation("Created analysis_jobs table");
    }

    private async Task UpdateJobStatusAsync(string jobId, string status, DateTime? startedAt = null,
        int? successCount = null, int? failedCount = null)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var sql = @"
            UPDATE analysis_jobs 
            SET Status = @Status,
                StartedAt = COALESCE(@StartedAt, StartedAt),
                CompletedAt = CASE WHEN @Status = 'Completed' THEN CURRENT_TIMESTAMP ELSE CompletedAt END,
                ProcessedCount = COALESCE(@ProcessedCount, ProcessedCount),
                SuccessCount = COALESCE(@SuccessCount, SuccessCount),
                FailedCount = COALESCE(@FailedCount, FailedCount)
            WHERE Id = @Id";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", jobId);
        command.Parameters.AddWithValue("Status", status);
        command.Parameters.AddWithValue("StartedAt", (object?)startedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("ProcessedCount", (successCount.HasValue && failedCount.HasValue) ? (object?)(successCount.Value + failedCount.Value) : DBNull.Value);
        command.Parameters.AddWithValue("SuccessCount", (object?)successCount ?? DBNull.Value);
        command.Parameters.AddWithValue("FailedCount", (object?)failedCount ?? DBNull.Value);

        await command.ExecuteNonQueryAsync();
    }

    private async Task<BatchAnalysisStatusDto?> LoadJobStatusFromDatabaseAsync(string jobId)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var sql = @"
            SELECT Id, BatchId, Status, TotalImages, ProcessedCount, SuccessCount, FailedCount,
                   CreatedAt, StartedAt, CompletedAt, ImageIds
            FROM analysis_jobs
            WHERE Id = @Id AND IsDeleted = false";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", jobId);

        using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        var imageIdsJson = reader.IsDBNull(10) ? "[]" : reader.GetString(10);
        var imageIds = System.Text.Json.JsonSerializer.Deserialize<List<string>>(imageIdsJson) ?? new List<string>();

        return new BatchAnalysisStatusDto
        {
            JobId = reader.GetString(0),
            BatchId = reader.GetString(1),
            Status = reader.GetString(2),
            TotalImages = reader.GetInt32(3),
            ProcessedCount = reader.GetInt32(4),
            SuccessCount = reader.GetInt32(5),
            FailedCount = reader.GetInt32(6),
            CreatedAt = reader.GetDateTime(7),
            StartedAt = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
            CompletedAt = reader.IsDBNull(9) ? null : reader.GetDateTime(9),
            ImageIds = imageIds
        };
    }

    public async Task<List<BatchAnalysisStatusDto>> ListJobsForClinicAsync(string clinicId, int limit = 10)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT Id, BatchId, Status, TotalImages, ProcessedCount, SuccessCount, FailedCount,
                       CreatedAt, StartedAt, CompletedAt, ImageIds
                FROM analysis_jobs
                WHERE ClinicId = @ClinicId AND IsDeleted = false
                ORDER BY CreatedAt DESC
                LIMIT @Limit";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("ClinicId", clinicId);
            command.Parameters.AddWithValue("Limit", limit);

            var list = new List<BatchAnalysisStatusDto>();
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var imageIdsJson = reader.IsDBNull(10) ? "[]" : reader.GetString(10);
                var imageIds = System.Text.Json.JsonSerializer.Deserialize<List<string>>(imageIdsJson) ?? new List<string>();
                list.Add(new BatchAnalysisStatusDto
                {
                    JobId = reader.GetString(0),
                    BatchId = reader.GetString(1),
                    Status = reader.GetString(2),
                    TotalImages = reader.GetInt32(3),
                    ProcessedCount = reader.GetInt32(4),
                    SuccessCount = reader.GetInt32(5),
                    FailedCount = reader.GetInt32(6),
                    CreatedAt = reader.GetDateTime(7),
                    StartedAt = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                    CompletedAt = reader.IsDBNull(9) ? null : reader.GetDateTime(9),
                    ImageIds = imageIds
                });
            }
            return list;
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            // Table analysis_jobs does not exist yet (e.g. schema not applied or no job ever created)
            _logger?.LogWarning("Table analysis_jobs does not exist, returning empty list. Run schema or create a job first.");
            return new List<BatchAnalysisStatusDto>();
        }
    }
}

