using Aura.Application.DTOs.Images;
using Aura.Application.DTOs.Analysis;
using Aura.Application.Services.Analysis;
using Aura.Application.Services.Images;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Security.Claims;
using System.Text.Json;

namespace Aura.API.Controllers;

/// <summary>
/// Controller for clinic bulk image upload and management (FR-24)
/// </summary>
[ApiController]
[Route("api/clinic/images")]
[Authorize] // Require authentication
public class ClinicImagesController : ControllerBase
{
    private readonly IImageService _imageService;
    private readonly IAnalysisQueueService _analysisQueueService;
    private readonly ILogger<ClinicImagesController> _logger;
    private readonly IConfiguration _configuration;

    public ClinicImagesController(
        IImageService imageService,
        IAnalysisQueueService analysisQueueService,
        ILogger<ClinicImagesController> logger,
        IConfiguration configuration)
    {
        _imageService = imageService;
        _analysisQueueService = analysisQueueService;
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// Bulk upload retinal images for clinic (FR-24)
    /// Supports uploading multiple images (≥100 images per batch as per NFR-2)
    /// </summary>
    [HttpPost("bulk-upload")]
    [ProducesResponseType(typeof(ClinicBulkUploadResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [RequestSizeLimit(500_000_000)] // 500MB max request size
    public async Task<IActionResult> BulkUploadImages(
        [FromForm] List<IFormFile> files,
        [FromForm] string? patientUserId = null,
        [FromForm] string? doctorId = null,
        [FromForm] string? batchName = null,
        [FromForm] bool autoStartAnalysis = true,
        [FromForm] string? imageType = null,
        [FromForm] string? eyeSide = null,
        [FromForm] string? captureDevice = null,
        [FromForm] DateTime? captureDate = null)
    {
        // Get clinic ID from claims (assuming it's stored in a claim)
        var clinicId = User.FindFirstValue("clinic_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        
        if (string.IsNullOrEmpty(clinicId))
        {
            // Try to get from role or other claim
            clinicId = User.FindFirstValue("ClinicId");
        }

        if (string.IsNullOrEmpty(clinicId))
        {
            _logger.LogWarning("Clinic ID not found in user claims");
            return Unauthorized(new { message = "Clinic ID not found. Please ensure you are logged in as a clinic account." });
        }

        if (files == null || files.Count == 0)
        {
            return BadRequest(new { message = "No files uploaded" });
        }

        // Validate file count (NFR-2: ≥100 images per batch)
        if (files.Count > 1000)
        {
            return BadRequest(new { message = "Maximum 1000 images per batch. Please split into multiple batches." });
        }

        _logger.LogInformation("Bulk upload request from clinic {ClinicId}, Files: {Count}", clinicId, files.Count);

        try
        {
            // Prepare file data
            var fileData = new List<(Stream FileStream, string Filename, ImageUploadDto? Metadata)>();

            // Prepare common metadata
            var commonMetadata = new ImageUploadDto
            {
                ImageType = imageType,
                EyeSide = eyeSide,
                CaptureDevice = captureDevice,
                CaptureDate = captureDate
            };

            foreach (var file in files)
            {
                if (file.Length > 0)
                {
                    var stream = file.OpenReadStream();
                    fileData.Add((stream, file.FileName, null)); // Individual metadata can be added later if needed
                }
            }

            // Prepare bulk upload options
            var options = new ClinicBulkUploadDto
            {
                PatientUserId = patientUserId,
                DoctorId = doctorId,
                BatchName = batchName,
                CommonMetadata = commonMetadata,
                AutoStartAnalysis = autoStartAnalysis
            };

            // Perform bulk upload
            var result = await _imageService.BulkUploadForClinicAsync(
                clinicId,
                fileData,
                options);

            // Dispose streams
            foreach (var (stream, _, _) in fileData)
            {
                await stream.DisposeAsync();
            }

            // If auto-start analysis is enabled, queue the batch for analysis
            if (autoStartAnalysis && result.SuccessfullyUploaded.Count > 0)
            {
                try
                {
                    var imageIds = result.SuccessfullyUploaded.Select(img => img.Id).ToList();
                    var jobId = await _analysisQueueService.QueueBatchAnalysisAsync(clinicId, imageIds, result.BatchId);
                    result.AnalysisJobId = jobId;

                    _logger.LogInformation("Queued batch analysis job {JobId} for batch {BatchId}, clinic {ClinicId}", jobId, result.BatchId, clinicId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to queue batch analysis for batch {BatchId}, clinic {ClinicId}. Error: {Message}", result.BatchId, clinicId, ex.Message);
                    // Don't fail the upload if analysis queueing fails - frontend can use "Kiểm tra & tải kết quả" with batchId
                }
            }

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid bulk upload request from clinic {ClinicId}", clinicId);
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during bulk upload for clinic {ClinicId}", clinicId);
            return StatusCode(500, new { message = "Failed to upload images", error = ex.Message });
        }
    }

    /// <summary>
    /// Lấy tất cả kết quả phân tích của phòng khám (Lịch Sử Phân Tích - giống user reports)
    /// </summary>
    [HttpGet("analyses")]
    [ProducesResponseType(typeof(List<ClinicAnalysisListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllClinicAnalyses()
    {
        var clinicId = User.FindFirstValue("clinic_id") ??
                       User.FindFirstValue("ClinicId") ??
                       User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(clinicId))
            return Unauthorized(new { message = "Clinic ID not found" });

        try
        {
            var connStr = _configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrEmpty(connStr))
                return StatusCode(500, new { message = "DefaultConnection not configured" });

            using var connection = new NpgsqlConnection(connStr);
            await connection.OpenAsync();

            var sql = @"
                SELECT 
                    ar.Id, ar.ImageId, ar.AnalysisStatus, ar.OverallRiskLevel, ar.RiskScore,
                    ar.HypertensionRisk, ar.HypertensionScore,
                    ar.DiabetesRisk, ar.DiabetesScore, ar.DiabeticRetinopathyDetected, ar.DiabeticRetinopathySeverity,
                    ar.StrokeRisk, ar.StrokeScore,
                    ar.VesselTortuosity, ar.VesselWidthVariation, ar.MicroaneurysmsCount, ar.HemorrhagesDetected, ar.ExudatesDetected,
                    ar.AnnotatedImageUrl, ar.HeatmapUrl,
                    ar.AiConfidenceScore,
                    ar.Recommendations, ar.HealthWarnings,
                    ar.ProcessingTimeSeconds, ar.AnalysisStartedAt, ar.AnalysisCompletedAt,
                    ar.DetailedFindings,
                    COALESCE(up.FirstName || ' ' || up.LastName, up.Email, 'Không xác định') as PatientName,
                    ri.UserId as PatientUserId
                FROM analysis_results ar
                INNER JOIN retinal_images ri ON ri.Id = ar.ImageId AND COALESCE(ri.IsDeleted, false) = false
                LEFT JOIN users up ON up.Id = ri.UserId AND COALESCE(up.IsDeleted, false) = false
                WHERE (ri.ClinicId = @ClinicId OR ar.UserId = @ClinicId)
                  AND COALESCE(ar.IsDeleted, false) = false
                ORDER BY ar.AnalysisCompletedAt DESC NULLS LAST, ar.AnalysisStartedAt DESC NULLS LAST, ar.CreatedDate DESC NULLS LAST";

            using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("ClinicId", clinicId);

            var results = new List<ClinicAnalysisListItemDto>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                Dictionary<string, object>? detailed = null;
                if (!reader.IsDBNull(26))
                {
                    try
                    {
                        var json = reader.GetString(26);
                        detailed = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                    }
                    catch { detailed = null; }
                }

                results.Add(new ClinicAnalysisListItemDto
                {
                    Id = reader.GetString(0),
                    ImageId = reader.GetString(1),
                    AnalysisStatus = reader.GetString(2),
                    OverallRiskLevel = reader.IsDBNull(3) ? null : reader.GetString(3),
                    RiskScore = reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                    HypertensionRisk = reader.IsDBNull(5) ? null : reader.GetString(5),
                    HypertensionScore = reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                    DiabetesRisk = reader.IsDBNull(7) ? null : reader.GetString(7),
                    DiabetesScore = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                    DiabeticRetinopathyDetected = !reader.IsDBNull(9) && reader.GetBoolean(9),
                    DiabeticRetinopathySeverity = reader.IsDBNull(10) ? null : reader.GetString(10),
                    StrokeRisk = reader.IsDBNull(11) ? null : reader.GetString(11),
                    StrokeScore = reader.IsDBNull(12) ? null : reader.GetDecimal(12),
                    VesselTortuosity = reader.IsDBNull(13) ? null : reader.GetDecimal(13),
                    VesselWidthVariation = reader.IsDBNull(14) ? null : reader.GetDecimal(14),
                    MicroaneurysmsCount = reader.IsDBNull(15) ? 0 : reader.GetInt32(15),
                    HemorrhagesDetected = !reader.IsDBNull(16) && reader.GetBoolean(16),
                    ExudatesDetected = !reader.IsDBNull(17) && reader.GetBoolean(17),
                    AnnotatedImageUrl = reader.IsDBNull(18) ? null : reader.GetString(18),
                    HeatmapUrl = reader.IsDBNull(19) ? null : reader.GetString(19),
                    AiConfidenceScore = reader.IsDBNull(20) ? null : reader.GetDecimal(20),
                    Recommendations = reader.IsDBNull(21) ? null : reader.GetString(21),
                    HealthWarnings = reader.IsDBNull(22) ? null : reader.GetString(22),
                    ProcessingTimeSeconds = reader.IsDBNull(23) ? null : reader.GetInt32(23),
                    AnalysisStartedAt = reader.IsDBNull(24) ? null : reader.GetDateTime(24),
                    AnalysisCompletedAt = reader.IsDBNull(25) ? null : reader.GetDateTime(25),
                    DetailedFindings = detailed,
                    PatientName = reader.IsDBNull(27) ? null : reader.GetString(27),
                    PatientUserId = reader.IsDBNull(28) ? null : reader.GetString(28)
                });
            }

            return Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting clinic analyses for clinic {ClinicId}", clinicId);
            return StatusCode(500, new { message = "Không thể lấy danh sách phân tích" });
        }
    }

    /// <summary>
    /// List recent analysis jobs for the current clinic (dashboard)
    /// </summary>
    [HttpGet("analysis/jobs")]
    [ProducesResponseType(typeof(List<BatchAnalysisStatusDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListAnalysisJobs([FromQuery] int limit = 10)
    {
        var clinicId = User.FindFirstValue("clinic_id") ??
                       User.FindFirstValue("ClinicId") ??
                       User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(clinicId))
            return Unauthorized(new { message = "Clinic ID not found" });

        try
        {
            var jobs = await _analysisQueueService.ListJobsForClinicAsync(clinicId, Math.Min(limit, 50));
            return Ok(jobs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing analysis jobs for clinic {ClinicId}", clinicId);
            return StatusCode(500, new { message = "Failed to list analysis jobs" });
        }
    }

    /// <summary>
    /// Get status of a batch analysis job
    /// </summary>
    [HttpGet("analysis/{jobId}/status")]
    [ProducesResponseType(typeof(BatchAnalysisStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBatchAnalysisStatus(string jobId)
    {
        var clinicId = User.FindFirstValue("clinic_id") ?? 
                       User.FindFirstValue("ClinicId") ?? 
                       User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(clinicId))
        {
            return Unauthorized(new { message = "Clinic ID not found" });
        }

        try
        {
            var status = await _analysisQueueService.GetBatchAnalysisStatusAsync(jobId);
            if (status == null)
            {
                return NotFound(new { message = "Analysis job not found" });
            }

            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting batch analysis status for job {JobId}", jobId);
            return StatusCode(500, new { message = "Failed to get analysis status", error = ex.Message });
        }
    }

    /// <summary>
    /// Queue a batch of already-uploaded images for AI analysis
    /// </summary>
    [HttpPost("queue-analysis")]
    [ProducesResponseType(typeof(BatchAnalysisStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> QueueBatchAnalysis([FromBody] QueueAnalysisRequest request)
    {
        var clinicId = User.FindFirstValue("clinic_id") ?? 
                       User.FindFirstValue("ClinicId") ?? 
                       User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(clinicId))
        {
            return Unauthorized(new { message = "Clinic ID not found" });
        }

        if (request.ImageIds == null || request.ImageIds.Count == 0)
        {
            return BadRequest(new { message = "Image IDs list cannot be empty" });
        }

        try
        {
            var jobId = await _analysisQueueService.QueueBatchAnalysisAsync(
                clinicId, 
                request.ImageIds, 
                request.BatchId);

            var status = await _analysisQueueService.GetBatchAnalysisStatusAsync(jobId);
            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error queueing batch analysis for clinic {ClinicId}", clinicId);
            return StatusCode(500, new { message = "Failed to queue analysis", error = ex.Message });
        }
    }

    /// <summary>
    /// Get AI analysis results for a batch analysis job (clinic scope)
    /// </summary>
    [HttpGet("analysis/{jobId}/results")]
    [ProducesResponseType(typeof(List<AnalysisResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBatchAnalysisResults(string jobId)
    {
        var clinicId = User.FindFirstValue("clinic_id") ??
                       User.FindFirstValue("ClinicId") ??
                       User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(clinicId))
            return Unauthorized(new { message = "Clinic ID not found" });

        try
        {
            var status = await _analysisQueueService.GetBatchAnalysisStatusAsync(jobId);
            if (status == null)
                return NotFound(new { message = "Analysis job not found" });

            var imageIds = status.ImageIds?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray() ?? Array.Empty<string>();
            if (imageIds.Length == 0)
                return Ok(new List<AnalysisResultDto>());

            var connStr = _configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrEmpty(connStr))
                return StatusCode(500, new { message = "DefaultConnection not configured" });

            using var connection = new NpgsqlConnection(connStr);
            await connection.OpenAsync();

            var sql = @"
                SELECT 
                    ar.Id, ar.ImageId, ar.AnalysisStatus, ar.OverallRiskLevel, ar.RiskScore,
                    ar.HypertensionRisk, ar.HypertensionScore,
                    ar.DiabetesRisk, ar.DiabetesScore, ar.DiabeticRetinopathyDetected, ar.DiabeticRetinopathySeverity,
                    ar.StrokeRisk, ar.StrokeScore,
                    ar.VesselTortuosity, ar.VesselWidthVariation, ar.MicroaneurysmsCount, ar.HemorrhagesDetected, ar.ExudatesDetected,
                    ar.AnnotatedImageUrl, ar.HeatmapUrl,
                    ar.AiConfidenceScore,
                    ar.Recommendations, ar.HealthWarnings,
                    ar.ProcessingTimeSeconds, ar.AnalysisStartedAt, ar.AnalysisCompletedAt,
                    ar.DetailedFindings
                FROM analysis_results ar
                INNER JOIN retinal_images ri ON ri.Id = ar.ImageId
                WHERE ri.ClinicId = @ClinicId
                  AND ri.IsDeleted = false
                  AND ar.IsDeleted = false
                  AND ar.ImageId = ANY(@ImageIds)
                ORDER BY ar.AnalysisCompletedAt DESC NULLS LAST";

            using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("ClinicId", clinicId);
            cmd.Parameters.AddWithValue("ImageIds", imageIds);

            var results = new List<AnalysisResultDto>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                Dictionary<string, object>? detailed = null;
                if (!reader.IsDBNull(29))
                {
                    try
                    {
                        var json = reader.GetString(29);
                        detailed = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                    }
                    catch
                    {
                        detailed = null;
                    }
                }

                results.Add(new AnalysisResultDto
                {
                    Id = reader.GetString(0),
                    ImageId = reader.GetString(1),
                    AnalysisStatus = reader.GetString(2),
                    OverallRiskLevel = reader.IsDBNull(3) ? null : reader.GetString(3),
                    RiskScore = reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                    HypertensionRisk = reader.IsDBNull(5) ? null : reader.GetString(5),
                    HypertensionScore = reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                    DiabetesRisk = reader.IsDBNull(7) ? null : reader.GetString(7),
                    DiabetesScore = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                    DiabeticRetinopathyDetected = !reader.IsDBNull(9) && reader.GetBoolean(9),
                    DiabeticRetinopathySeverity = reader.IsDBNull(10) ? null : reader.GetString(10),
                    StrokeRisk = reader.IsDBNull(11) ? null : reader.GetString(11),
                    StrokeScore = reader.IsDBNull(12) ? null : reader.GetDecimal(12),
                    VesselTortuosity = reader.IsDBNull(13) ? null : reader.GetDecimal(13),
                    VesselWidthVariation = reader.IsDBNull(14) ? null : reader.GetDecimal(14),
                    MicroaneurysmsCount = reader.IsDBNull(15) ? 0 : reader.GetInt32(15),
                    HemorrhagesDetected = !reader.IsDBNull(16) && reader.GetBoolean(16),
                    ExudatesDetected = !reader.IsDBNull(17) && reader.GetBoolean(17),
                    AnnotatedImageUrl = reader.IsDBNull(18) ? null : reader.GetString(18),
                    HeatmapUrl = reader.IsDBNull(19) ? null : reader.GetString(19),
                    AiConfidenceScore = reader.IsDBNull(20) ? null : reader.GetDecimal(20),
                    Recommendations = reader.IsDBNull(21) ? null : reader.GetString(21),
                    HealthWarnings = reader.IsDBNull(22) ? null : reader.GetString(22),
                    ProcessingTimeSeconds = reader.IsDBNull(23) ? null : reader.GetInt32(23),
                    AnalysisStartedAt = reader.IsDBNull(24) ? null : reader.GetDateTime(24),
                    AnalysisCompletedAt = reader.IsDBNull(25) ? null : reader.GetDateTime(25),
                    DetailedFindings = detailed
                });
            }

            return Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting batch analysis results for job {JobId}", jobId);
            return StatusCode(500, new { message = "Failed to get analysis results" });
        }
    }

    /// <summary>
    /// Get status of a bulk upload batch
    /// </summary>
    [HttpGet("batches/{batchId}/status")]
    [ProducesResponseType(typeof(BulkUploadBatchStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBatchStatus(string batchId)
    {
        var clinicId = User.FindFirstValue("clinic_id") ?? 
                       User.FindFirstValue("ClinicId") ?? 
                       User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(clinicId))
        {
            return Unauthorized(new { message = "Clinic ID not found" });
        }

        try
        {
            var batchStatus = await GetBulkUploadBatchStatusAsync(batchId, clinicId);
            if (batchStatus == null)
            {
                return NotFound(new { message = "Batch not found" });
            }

            return Ok(batchStatus);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting batch status for batch {BatchId}", batchId);
            return StatusCode(500, new { message = "Failed to get batch status", error = ex.Message });
        }
    }

    /// <summary>
    /// List all bulk upload batches for the clinic
    /// </summary>
    [HttpGet("batches")]
    [ProducesResponseType(typeof(List<BulkUploadBatchStatusDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListBatches(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null)
    {
        var clinicId = User.FindFirstValue("clinic_id") ?? 
                       User.FindFirstValue("ClinicId") ?? 
                       User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(clinicId))
        {
            return Unauthorized(new { message = "Clinic ID not found" });
        }

        try
        {
            var batches = await ListBulkUploadBatchesAsync(clinicId, page, pageSize, status);
            return Ok(batches);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing batches for clinic {ClinicId}", clinicId);
            return StatusCode(500, new { message = "Failed to list batches", error = ex.Message });
        }
    }

    private async Task<BulkUploadBatchStatusDto?> GetBulkUploadBatchStatusAsync(string batchId, string clinicId)
    {
        using var connection = new Npgsql.NpgsqlConnection(
            _configuration.GetConnectionString("DefaultConnection") 
            ?? throw new InvalidOperationException("Database connection string not found"));
        await connection.OpenAsync();

        var sql = @"
            SELECT Id, ClinicId, UploadedBy, UploadedByType, BatchName, TotalImages,
                   ProcessedImages, FailedImages, ProcessingImages, UploadStatus,
                   StartedAt, CompletedAt, FailureReason, Metadata, CreatedDate
            FROM bulk_upload_batches
            WHERE Id = @BatchId AND ClinicId = @ClinicId AND IsDeleted = false";

        using var command = new Npgsql.NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("BatchId", batchId);
        command.Parameters.AddWithValue("ClinicId", clinicId);

        using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new BulkUploadBatchStatusDto
        {
            BatchId = reader.GetString(0),
            ClinicId = reader.GetString(1),
            UploadedBy = reader.GetString(2),
            UploadedByType = reader.GetString(3),
            BatchName = reader.IsDBNull(4) ? null : reader.GetString(4),
            TotalImages = reader.GetInt32(5),
            ProcessedImages = reader.GetInt32(6),
            FailedImages = reader.GetInt32(7),
            ProcessingImages = reader.GetInt32(8),
            UploadStatus = reader.GetString(9),
            StartedAt = reader.IsDBNull(10) ? null : reader.GetDateTime(10),
            CompletedAt = reader.IsDBNull(11) ? null : reader.GetDateTime(11),
            FailureReason = reader.IsDBNull(12) ? null : reader.GetString(12),
            Metadata = reader.IsDBNull(13) ? null : reader.GetString(13),
            CreatedDate = reader.IsDBNull(14) ? null : reader.GetDateTime(14)
        };
    }

    private async Task<List<BulkUploadBatchStatusDto>> ListBulkUploadBatchesAsync(
        string clinicId, int page, int pageSize, string? status)
    {
        using var connection = new Npgsql.NpgsqlConnection(
            _configuration.GetConnectionString("DefaultConnection") 
            ?? throw new InvalidOperationException("Database connection string not found"));
        await connection.OpenAsync();

        var whereClause = "ClinicId = @ClinicId AND IsDeleted = false";
        if (!string.IsNullOrEmpty(status))
        {
            whereClause += " AND UploadStatus = @Status";
        }

        var sql = $@"
            SELECT Id, ClinicId, UploadedBy, UploadedByType, BatchName, TotalImages,
                   ProcessedImages, FailedImages, ProcessingImages, UploadStatus,
                   StartedAt, CompletedAt, FailureReason, Metadata, CreatedDate
            FROM bulk_upload_batches
            WHERE {whereClause}
            ORDER BY StartedAt DESC
            LIMIT @PageSize OFFSET @Offset";

        using var command = new Npgsql.NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("ClinicId", clinicId);
        command.Parameters.AddWithValue("PageSize", pageSize);
        command.Parameters.AddWithValue("Offset", (page - 1) * pageSize);
        if (!string.IsNullOrEmpty(status))
        {
            command.Parameters.AddWithValue("Status", status);
        }

        var batches = new List<BulkUploadBatchStatusDto>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            batches.Add(new BulkUploadBatchStatusDto
            {
                BatchId = reader.GetString(0),
                ClinicId = reader.GetString(1),
                UploadedBy = reader.GetString(2),
                UploadedByType = reader.GetString(3),
                BatchName = reader.IsDBNull(4) ? null : reader.GetString(4),
                TotalImages = reader.GetInt32(5),
                ProcessedImages = reader.GetInt32(6),
                FailedImages = reader.GetInt32(7),
                ProcessingImages = reader.GetInt32(8),
                UploadStatus = reader.GetString(9),
                StartedAt = reader.IsDBNull(10) ? null : reader.GetDateTime(10),
                CompletedAt = reader.IsDBNull(11) ? null : reader.GetDateTime(11),
                FailureReason = reader.IsDBNull(12) ? null : reader.GetString(12),
                Metadata = reader.IsDBNull(13) ? null : reader.GetString(13),
                CreatedDate = reader.IsDBNull(14) ? null : reader.GetDateTime(14)
            });
        }

        return batches;
    }

    public class QueueAnalysisRequest
    {
        public List<string> ImageIds { get; set; } = new();
        public string? BatchId { get; set; }
    }
}

public class ClinicAnalysisListItemDto
{
    public string Id { get; set; } = string.Empty;
    public string ImageId { get; set; } = string.Empty;
    public string AnalysisStatus { get; set; } = string.Empty;
    public string? OverallRiskLevel { get; set; }
    public decimal? RiskScore { get; set; }
    public string? HypertensionRisk { get; set; }
    public decimal? HypertensionScore { get; set; }
    public string? DiabetesRisk { get; set; }
    public decimal? DiabetesScore { get; set; }
    public bool DiabeticRetinopathyDetected { get; set; }
    public string? DiabeticRetinopathySeverity { get; set; }
    public string? StrokeRisk { get; set; }
    public decimal? StrokeScore { get; set; }
    public decimal? VesselTortuosity { get; set; }
    public decimal? VesselWidthVariation { get; set; }
    public int MicroaneurysmsCount { get; set; }
    public bool HemorrhagesDetected { get; set; }
    public bool ExudatesDetected { get; set; }
    public string? AnnotatedImageUrl { get; set; }
    public string? HeatmapUrl { get; set; }
    public decimal? AiConfidenceScore { get; set; }
    public string? Recommendations { get; set; }
    public string? HealthWarnings { get; set; }
    public int? ProcessingTimeSeconds { get; set; }
    public DateTime? AnalysisStartedAt { get; set; }
    public DateTime? AnalysisCompletedAt { get; set; }
    public Dictionary<string, object>? DetailedFindings { get; set; }
    public string? PatientName { get; set; }
    public string? PatientUserId { get; set; }
}

public class BulkUploadBatchStatusDto
{
    public string BatchId { get; set; } = string.Empty;
    public string ClinicId { get; set; } = string.Empty;
    public string UploadedBy { get; set; } = string.Empty;
    public string UploadedByType { get; set; } = string.Empty;
    public string? BatchName { get; set; }
    public int TotalImages { get; set; }
    public int ProcessedImages { get; set; }
    public int FailedImages { get; set; }
    public int ProcessingImages { get; set; }
    public string UploadStatus { get; set; } = string.Empty;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? FailureReason { get; set; }
    public string? Metadata { get; set; }
    public DateTime? CreatedDate { get; set; }
}

