using Aura.Application.DTOs.Analysis;
using Aura.Application.DTOs.Export;
using Aura.Application.Services.Analysis;
using Aura.Application.Services.Export;
using Aura.Application.Services.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Npgsql;
using Aura.Infrastructure.Services.RabbitMQ;

namespace Aura.API.Controllers;

/// <summary>
/// Controller xử lý phân tích hình ảnh võng mạc và export báo cáo (FR-3, FR-7)
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
[Produces("application/json")]
public class AnalysisController : ControllerBase
{
    private readonly IAnalysisService _analysisService;
    private readonly IExportService _exportService;
    private readonly INotificationService _notificationService;
    private readonly ILogger<AnalysisController> _logger;
    private readonly IRabbitMQService _rabbitMqService;

    public AnalysisController(
        IAnalysisService analysisService, 
        IExportService exportService,
        INotificationService notificationService,
        ILogger<AnalysisController> logger,
        IRabbitMQService rabbitMqService)
    {
        _analysisService = analysisService ?? throw new ArgumentNullException(nameof(analysisService));
        _exportService = exportService ?? throw new ArgumentNullException(nameof(exportService));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _rabbitMqService = rabbitMqService ?? throw new ArgumentNullException(nameof(rabbitMqService));
    }

    #region Analysis Endpoints

    /// <summary>
    /// Bắt đầu phân tích một hoặc nhiều hình ảnh (FR-3)
    /// </summary>
    /// <param name="request">Danh sách ID hình ảnh cần phân tích</param>
    /// <returns>Thông tin phân tích</returns>
    [HttpPost("start")]
    [ProducesResponseType(typeof(AnalysisResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(List<AnalysisResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> StartAnalysis([FromBody] AnalysisRequestDto request)
    {
        if (request.ImageIds == null || request.ImageIds.Count == 0)
        {
            return BadRequest(new { message = "Cần ít nhất một ID hình ảnh" });
        }

        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized(new { message = "Chưa xác thực người dùng" });

            try
            {
                if (request.ImageIds.Count == 1)
                {
                    var result = await _analysisService.StartAnalysisAsync(userId, request.ImageIds[0]);

                    // Publish event analysis.completed cho 1 ảnh
                    PublishAnalysisCompletedEvent(userId, result);

                    // Gửi thông báo cho bệnh nhân khi phân tích hoàn thành
                    if (string.Equals(result.Status, "Completed", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            await _notificationService.CreateAsync(userId,
                                "Phân tích hoàn thành",
                                "Kết quả phân tích võng mạc của bạn đã sẵn sàng. Vào Báo cáo để xem chi tiết.",
                                "AnalysisComplete",
                                new { analysisId = result.AnalysisId });
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Could not create notification for analysis {AnalysisId}", result.AnalysisId);
                        }
                    }

                    return Ok(result);
                }
                else
                {
                    var results = await _analysisService.StartMultipleAnalysisAsync(userId, request.ImageIds);

                    // Publish event analysis.completed và gửi thông báo cho từng ảnh (nếu Completed)
                    foreach (var r in results)
                    {
                        PublishAnalysisCompletedEvent(userId, r);
                        if (string.Equals(r.Status, "Completed", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                await _notificationService.CreateAsync(userId,
                                    "Phân tích hoàn thành",
                                    "Kết quả phân tích võng mạc của bạn đã sẵn sàng. Vào Báo cáo để xem chi tiết.",
                                    "AnalysisComplete",
                                    new { analysisId = r.AnalysisId });
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Could not create notification for analysis {AnalysisId}", r.AnalysisId);
                            }
                        }
                    }

                    return Ok(results);
                }
            }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid analysis request from user {UserId}: {Message}", userId, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
        catch (Npgsql.PostgresException pgEx)
        {
            _logger.LogError(pgEx, "PostgreSQL error starting analysis: {Message}, Code: {SqlState}", 
                pgEx.Message, pgEx.SqlState);
            return StatusCode(500, new { 
                message = "Lỗi database khi bắt đầu phân tích", 
                error = pgEx.Message,
                sqlState = pgEx.SqlState
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting analysis for user {UserId}: {Error}", userId, ex.Message);
            _logger.LogError(ex, "Stack trace: {StackTrace}", ex.StackTrace);
            return StatusCode(500, new { 
                message = "Không thể bắt đầu phân tích", 
                error = ex.Message,
                innerException = ex.InnerException?.Message
            });
        }
    }

    /// <summary>
    /// Lấy kết quả phân tích theo ID (FR-3)
    /// </summary>
    /// <param name="analysisId">ID của kết quả phân tích</param>
    /// <returns>Chi tiết kết quả phân tích</returns>
    [HttpGet("{analysisId}")]
    [ProducesResponseType(typeof(AnalysisResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAnalysisResult(string analysisId)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized(new { message = "Chưa xác thực người dùng" });

        try
        {
            var result = await _analysisService.GetAnalysisResultAsync(analysisId, userId);
            
            if (result == null)
            {
                return NotFound(new { message = "Không tìm thấy kết quả phân tích" });
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting analysis result {AnalysisId} for user {UserId}", analysisId, userId);
            return StatusCode(500, new { message = "Không thể lấy kết quả phân tích" });
        }
    }

    /// <summary>
    /// Lấy tất cả kết quả phân tích của người dùng hiện tại (FR-6)
    /// </summary>
    /// <returns>Danh sách kết quả phân tích</returns>
    [HttpGet]
    [ProducesResponseType(typeof(List<AnalysisResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetUserAnalysisResults()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized(new { message = "Chưa xác thực người dùng" });

        try
        {
            var results = await _analysisService.GetUserAnalysisResultsAsync(userId);
            return Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting analysis results for user {UserId}", userId);
            return StatusCode(500, new { message = "Không thể lấy danh sách kết quả phân tích" });
        }
    }

    #endregion

    #region Export Endpoints (FR-7)

    /// <summary>
    /// Export kết quả phân tích sang PDF
    /// </summary>
    /// <param name="analysisId">ID của kết quả phân tích</param>
    /// <param name="includeImages">Có bao gồm hình ảnh không (mặc định: có)</param>
    /// <param name="includePatientInfo">Có bao gồm thông tin bệnh nhân không (mặc định: có)</param>
    /// <param name="language">Ngôn ngữ: vi (Tiếng Việt) hoặc en (English)</param>
    /// <returns>Thông tin file PDF đã export</returns>
    [HttpPost("{analysisId}/export/pdf")]
    [ProducesResponseType(typeof(ExportResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportToPdf(
        string analysisId, 
        [FromQuery] bool includeImages = true, 
        [FromQuery] bool includePatientInfo = true,
        [FromQuery] string language = "vi")
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized(new { message = "Chưa xác thực người dùng" });

        // Validate language
        if (!IsValidLanguage(language))
        {
            return BadRequest(new { message = "Ngôn ngữ không hợp lệ. Chọn 'vi' hoặc 'en'" });
        }

        try
        {
            var userType = GetCurrentUserType();
            var result = await _exportService.ExportToPdfAsync(
                analysisId, userId, userType, includeImages, includePatientInfo, language);
            
            _logger.LogInformation("PDF exported successfully: {ExportId} for analysis {AnalysisId} by {UserType}", 
                result.ExportId, analysisId, userType);
            
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Export PDF failed for analysis {AnalysisId}", analysisId);
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting PDF for analysis {AnalysisId}", analysisId);
            return StatusCode(500, new { message = "Không thể export PDF" });
        }
    }

    /// <summary>
    /// Export kết quả phân tích sang CSV
    /// </summary>
    /// <param name="analysisId">ID của kết quả phân tích</param>
    /// <param name="language">Ngôn ngữ: vi (Tiếng Việt) hoặc en (English)</param>
    /// <returns>Thông tin file CSV đã export</returns>
    [HttpPost("{analysisId}/export/csv")]
    [ProducesResponseType(typeof(ExportResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportToCsv(string analysisId, [FromQuery] string language = "vi")
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized(new { message = "Chưa xác thực người dùng" });

        if (!IsValidLanguage(language))
        {
            return BadRequest(new { message = "Ngôn ngữ không hợp lệ. Chọn 'vi' hoặc 'en'" });
        }

        try
        {
            var userType = GetCurrentUserType();
            var result = await _exportService.ExportToCsvAsync(analysisId, userId, userType, language);
            
            _logger.LogInformation("CSV exported successfully: {ExportId} for analysis {AnalysisId} by {UserType}", 
                result.ExportId, analysisId, userType);
            
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Export CSV failed for analysis {AnalysisId}", analysisId);
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting CSV for analysis {AnalysisId}", analysisId);
            return StatusCode(500, new { message = "Không thể export CSV" });
        }
    }

    /// <summary>
    /// Export kết quả phân tích sang JSON
    /// </summary>
    /// <param name="analysisId">ID của kết quả phân tích</param>
    /// <returns>Thông tin file JSON đã export</returns>
    [HttpPost("{analysisId}/export/json")]
    [ProducesResponseType(typeof(ExportResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportToJson(string analysisId)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized(new { message = "Chưa xác thực người dùng" });

        try
        {
            var userType = GetCurrentUserType();
            var result = await _exportService.ExportToJsonAsync(analysisId, userId, userType);
            
            _logger.LogInformation("JSON exported successfully: {ExportId} for analysis {AnalysisId} by {UserType}", 
                result.ExportId, analysisId, userType);
            
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Export JSON failed for analysis {AnalysisId}", analysisId);
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting JSON for analysis {AnalysisId}", analysisId);
            return StatusCode(500, new { message = "Không thể export JSON" });
        }
    }

    /// <summary>
    /// Export nhiều kết quả phân tích sang CSV
    /// </summary>
    /// <param name="request">Danh sách ID kết quả phân tích cần export</param>
    /// <returns>Thông tin các file đã export</returns>
    [HttpPost("export/batch")]
    [ProducesResponseType(typeof(BatchExportResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ExportBatch([FromBody] BatchExportRequestDto request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized(new { message = "Chưa xác thực người dùng" });

        if (request.AnalysisResultIds == null || request.AnalysisResultIds.Count == 0)
        {
            return BadRequest(new { message = "Cần ít nhất một ID kết quả phân tích" });
        }

        if (!IsValidLanguage(request.Language))
        {
            return BadRequest(new { message = "Ngôn ngữ không hợp lệ. Chọn 'vi' hoặc 'en'" });
        }

        try
        {
            var userType = GetCurrentUserType();
            var result = await _exportService.ExportBatchToCsvAsync(
                request.AnalysisResultIds, userId, userType, request.Language);
            
            _logger.LogInformation("Batch export completed: {SuccessCount}/{TotalRequested} for user {UserId} ({UserType})", 
                result.SuccessCount, result.TotalRequested, userId, userType);
            
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error batch exporting for user {UserId}", userId);
            return StatusCode(500, new { message = "Không thể export batch" });
        }
    }

    #endregion

    #region Export History Endpoints (FR-7)

    /// <summary>
    /// Lấy lịch sử export của người dùng hiện tại
    /// </summary>
    /// <param name="limit">Số lượng tối đa (mặc định: 50, tối đa: 100)</param>
    /// <param name="offset">Vị trí bắt đầu (mặc định: 0)</param>
    /// <returns>Danh sách các báo cáo đã export</returns>
    [HttpGet("exports")]
    [ProducesResponseType(typeof(List<ExportResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetExportHistory([FromQuery] int limit = 50, [FromQuery] int offset = 0)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized(new { message = "Chưa xác thực người dùng" });

        try
        {
            var exports = await _exportService.GetExportHistoryAsync(userId, limit, offset);
            return Ok(exports);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting export history for user {UserId}", userId);
            return StatusCode(500, new { message = "Không thể lấy lịch sử export" });
        }
    }

    /// <summary>
    /// Lấy thông tin chi tiết của một báo cáo đã export
    /// </summary>
    /// <param name="exportId">ID của báo cáo</param>
    /// <returns>Chi tiết báo cáo</returns>
    [HttpGet("exports/{exportId}")]
    [ProducesResponseType(typeof(ExportResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetExportById(string exportId)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized(new { message = "Chưa xác thực người dùng" });

        try
        {
            var export = await _exportService.GetExportByIdAsync(exportId, userId);
            
            if (export == null)
            {
                return NotFound(new { message = "Không tìm thấy báo cáo" });
            }

            return Ok(export);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting export {ExportId} for user {UserId}", exportId, userId);
            return StatusCode(500, new { message = "Không thể lấy thông tin báo cáo" });
        }
    }

    /// <summary>
    /// Download file export (proxy từ Cloudinary để tránh authentication issues)
    /// </summary>
    /// <param name="exportId">ID của báo cáo</param>
    /// <returns>File download</returns>
    [HttpGet("exports/{exportId}/download")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> DownloadExportFile(string exportId)
    {
        var userId = GetCurrentUserId();
        if (userId == null) 
        {
            _logger.LogWarning("Unauthorized download request: ExportId={ExportId}", exportId);
            return Unauthorized(new { message = "Chưa xác thực người dùng" });
        }

        try
        {
            _logger.LogInformation("Download request received: ExportId={ExportId}, UserId={UserId}", exportId, userId);
            
            // Get export details
            var export = await _exportService.GetExportByIdAsync(exportId, userId);
            if (export == null)
            {
                _logger.LogWarning("Export not found: ExportId={ExportId}, UserId={UserId}. Possible reasons: export doesn't exist, user mismatch, or export deleted.", exportId, userId);
                return NotFound(new { message = $"Không tìm thấy báo cáo với mã {exportId}. Có thể báo cáo không tồn tại hoặc bạn không có quyền truy cập." });
            }
            
            _logger.LogInformation("Export found: ExportId={ExportId}, ReportType={ReportType}, FileUrl={FileUrl}", 
                export.ExportId, export.ReportType, export.FileUrl);

            // Check if export is expired
            if (export.ExpiresAt.HasValue && export.ExpiresAt.Value < DateTime.UtcNow)
            {
                _logger.LogWarning("Export expired: ExportId={ExportId}, ExpiresAt={ExpiresAt}", exportId, export.ExpiresAt);
                return NotFound(new { message = "Báo cáo đã hết hạn. Vui lòng xuất báo cáo mới." });
            }

            // Download file from Cloudinary
            _logger.LogInformation("Attempting to download file from Cloudinary: ExportId={ExportId}", exportId);
            var fileBytes = await _exportService.DownloadExportFileAsync(exportId, userId);
            
            if (fileBytes == null || fileBytes.Length == 0)
            {
                _logger.LogError("File download returned null or empty: ExportId={ExportId}, FileUrl={FileUrl}", exportId, export.FileUrl);
                return NotFound(new { message = "Không tìm thấy file trên server. File có thể đã bị xóa hoặc không thể truy cập từ Cloudinary." });
            }

            _logger.LogInformation("File downloaded successfully: ExportId={ExportId}, Size={Size} bytes", exportId, fileBytes.Length);

            // Determine content type
            var contentType = export.ReportType.ToUpper() switch
            {
                "PDF" => "application/pdf",
                "CSV" => "text/csv",
                "JSON" => "application/json",
                _ => "application/octet-stream"
            };

            // Track download
            try
            {
                await _exportService.IncrementDownloadCountAsync(exportId, userId);
            }
            catch (Exception trackEx)
            {
                // Don't fail download if tracking fails
                _logger.LogWarning(trackEx, "Failed to track download count: ExportId={ExportId}", exportId);
            }

            // Return file with proper headers
            var fileName = export.FileName ?? $"analysis-report-{exportId}.{export.ReportType.ToLower()}";
            return File(fileBytes, contentType, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading export file {ExportId}: {Message}", exportId, ex.Message);
            
            // Return more detailed error message
            var errorMessage = ex.InnerException != null 
                ? $"Không thể tải file: {ex.Message} ({ex.InnerException.Message})"
                : $"Không thể tải file: {ex.Message}";
            
            return StatusCode(500, new { message = errorMessage });
        }
    }

    /// <summary>
    /// Đánh dấu đã download báo cáo (tăng download count) - DEPRECATED, dùng GET /exports/{exportId}/download thay thế
    /// </summary>
    /// <param name="exportId">ID của báo cáo</param>
    /// <returns>Kết quả</returns>
    [HttpPost("exports/{exportId}/track-download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> TrackDownload(string exportId)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized(new { message = "Chưa xác thực người dùng" });

        try
        {
            var success = await _exportService.IncrementDownloadCountAsync(exportId, userId);
            
            if (!success)
            {
                return NotFound(new { message = "Không tìm thấy báo cáo" });
            }

            return Ok(new { message = "Đã ghi nhận download" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error tracking download for export {ExportId}", exportId);
            return StatusCode(500, new { message = "Không thể ghi nhận download" });
        }
    }

    /// <summary>
    /// Xóa báo cáo đã export
    /// </summary>
    /// <param name="exportId">ID của báo cáo</param>
    /// <returns>Kết quả</returns>
    [HttpDelete("exports/{exportId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteExport(string exportId)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized(new { message = "Chưa xác thực người dùng" });

        try
        {
            var success = await _exportService.DeleteExportAsync(exportId, userId);
            
            if (!success)
            {
                return NotFound(new { message = "Không tìm thấy báo cáo" });
            }

            _logger.LogInformation("Export {ExportId} deleted by user {UserId}", exportId, userId);
            return Ok(new { message = "Đã xóa báo cáo" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting export {ExportId} for user {UserId}", exportId, userId);
            return StatusCode(500, new { message = "Không thể xóa báo cáo" });
        }
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Lấy User ID của người dùng hiện tại từ JWT token
    /// </summary>
    private string? GetCurrentUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    /// <summary>
    /// Lấy loại người dùng từ JWT token (User/Doctor/Admin)
    /// </summary>
    private string GetCurrentUserType()
    {
        var userType = User.FindFirstValue("userType") 
            ?? User.FindFirstValue("user_type")
            ?? User.FindFirstValue(ClaimTypes.Role);
        
        // Log for debugging
        _logger.LogInformation("GetCurrentUserType: Raw claim value = {RawUserType}", userType ?? "NULL");
        
        // Map to RequesterTypes
        var mappedType = userType?.ToLower() switch
        {
            "doctor" => RequesterTypes.Doctor,
            "admin" => RequesterTypes.Admin,
            _ => RequesterTypes.User
        };
        
        _logger.LogInformation("GetCurrentUserType: Mapped to {MappedType}", mappedType);
        return mappedType;
    }

    /// <summary>
    /// Gửi message analysis.completed lên RabbitMQ (analysis.exchange) để các service khác/NiFi xử lý
    /// </summary>
    private void PublishAnalysisCompletedEvent(string userId, AnalysisResponseDto result)
    {
        try
        {
            if (!string.Equals(result.Status, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var message = new
            {
                Type = "analysis.completed",
                AnalysisId = result.AnalysisId,
                ImageId = result.ImageId,
                UserId = userId,
                StartedAt = result.StartedAt,
                CompletedAt = result.CompletedAt ?? DateTime.UtcNow,
                PublishedAt = DateTime.UtcNow
            };

            _rabbitMqService.Publish("analysis.exchange", "analysis.start", message);
            _logger.LogInformation("Published analysis.completed event to RabbitMQ: {@Message}", message);
        }
        catch (Exception ex)
        {
            // Không làm hỏng luồng chính nếu RabbitMQ có vấn đề
            _logger.LogError(ex, "Failed to publish analysis.completed event to RabbitMQ for AnalysisId {AnalysisId}", 
                result.AnalysisId);
        }
    }

    /// <summary>
    /// Kiểm tra ngôn ngữ có hợp lệ không
    /// </summary>
    private static bool IsValidLanguage(string language)
    {
        return language == "vi" || language == "en";
    }

    #endregion
}
