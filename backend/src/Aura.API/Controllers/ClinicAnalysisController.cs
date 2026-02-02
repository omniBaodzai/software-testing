using Aura.Application.DTOs.Analysis;
using Aura.Application.DTOs.Export;
using Aura.Application.Services.Analysis;
using Aura.Application.Services.Export;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Aura.API.Controllers;

/// <summary>
/// Clinic analysis: giống patient - start analysis với imageIds, lấy kết quả theo analysisId.
/// Hỗ trợ export PDF/CSV/JSON giống user/doctor (FR-7).
/// </summary>
[ApiController]
[Route("api/clinic/analysis")]
[Authorize]
public class ClinicAnalysisController : ControllerBase
{
    private readonly IAnalysisService _analysisService;
    private readonly IExportService _exportService;
    private readonly ILogger<ClinicAnalysisController> _logger;

    public ClinicAnalysisController(IAnalysisService analysisService, IExportService exportService, ILogger<ClinicAnalysisController> logger)
    {
        _analysisService = analysisService;
        _exportService = exportService;
        _logger = logger;
    }

    private string? GetClinicId()
    {
        return User.FindFirstValue("clinic_id")
            ?? User.FindFirstValue("ClinicId")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    /// <summary>
    /// Bắt đầu phân tích (giống patient POST /api/analysis/start). Trả về analysisId để frontend redirect.
    /// </summary>
    [HttpPost("start")]
    [ProducesResponseType(typeof(AnalysisResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(List<AnalysisResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> StartAnalysis([FromBody] AnalysisRequestDto request)
    {
        if (request.ImageIds == null || request.ImageIds.Count == 0)
            return BadRequest(new { message = "Cần ít nhất một ID hình ảnh" });

        var clinicId = GetClinicId();
        if (string.IsNullOrEmpty(clinicId))
            return Unauthorized(new { message = "Chưa xác thực phòng khám" });

        try
        {
            if (request.ImageIds.Count == 1)
            {
                var result = await _analysisService.StartAnalysisAsync(clinicId, request.ImageIds[0]);
                _logger.LogInformation("Clinic {ClinicId} started analysis {AnalysisId}", clinicId, result.AnalysisId);
                return Ok(result);
            }

            var results = await _analysisService.StartMultipleAnalysisAsync(clinicId, request.ImageIds);
            _logger.LogInformation("Clinic {ClinicId} started {Count} analyses", clinicId, results.Count);
            return Ok(results);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Clinic {ClinicId} start analysis: {Message}", clinicId, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clinic {ClinicId} start analysis failed", clinicId);
            return StatusCode(500, new { message = "Không thể bắt đầu phân tích", error = ex.Message });
        }
    }

    /// <summary>
    /// Lấy kết quả phân tích theo analysisId (giống patient GET /api/analysis/:id). Verify ảnh thuộc clinic.
    /// </summary>
    [HttpGet("result/{analysisId}")]
    [ProducesResponseType(typeof(AnalysisResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAnalysisResult(string analysisId)
    {
        var clinicId = GetClinicId();
        if (string.IsNullOrEmpty(clinicId))
            return Unauthorized(new { message = "Chưa xác thực phòng khám" });

        try
        {
            var result = await _analysisService.GetAnalysisResultAsync(analysisId, clinicId);
            if (result == null)
                return NotFound(new { message = "Không tìm thấy kết quả phân tích" });
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clinic {ClinicId} get result {AnalysisId} failed", clinicId, analysisId);
            return StatusCode(500, new { message = "Không thể lấy kết quả phân tích" });
        }
    }

    #region Export (FR-7 - giống user/doctor)

    /// <summary>
    /// Export kết quả phân tích sang PDF (báo cáo đầy đủ như user/doctor)
    /// </summary>
    [HttpPost("result/{analysisId}/export/pdf")]
    [ProducesResponseType(typeof(ExportResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportToPdf(string analysisId, [FromQuery] bool includeImages = true, [FromQuery] bool includePatientInfo = true, [FromQuery] string language = "vi")
    {
        var clinicId = GetClinicId();
        if (string.IsNullOrEmpty(clinicId))
            return Unauthorized(new { message = "Chưa xác thực phòng khám" });
        if (language != "vi" && language != "en")
            return BadRequest(new { message = "Ngôn ngữ không hợp lệ" });
        try
        {
            var result = await _exportService.ExportToPdfAsync(analysisId, clinicId, RequesterTypes.Clinic, includeImages, includePatientInfo, language);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clinic {ClinicId} export PDF for {AnalysisId} failed", clinicId, analysisId);
            return StatusCode(500, new { message = "Không thể export PDF" });
        }
    }

    /// <summary>
    /// Export kết quả phân tích sang CSV (báo cáo đầy đủ như user/doctor)
    /// </summary>
    [HttpPost("result/{analysisId}/export/csv")]
    [ProducesResponseType(typeof(ExportResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportToCsv(string analysisId, [FromQuery] string language = "vi")
    {
        var clinicId = GetClinicId();
        if (string.IsNullOrEmpty(clinicId))
            return Unauthorized(new { message = "Chưa xác thực phòng khám" });
        if (language != "vi" && language != "en")
            return BadRequest(new { message = "Ngôn ngữ không hợp lệ" });
        try
        {
            var result = await _exportService.ExportToCsvAsync(analysisId, clinicId, RequesterTypes.Clinic, language);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clinic {ClinicId} export CSV for {AnalysisId} failed", clinicId, analysisId);
            return StatusCode(500, new { message = "Không thể export CSV" });
        }
    }

    /// <summary>
    /// Export kết quả phân tích sang JSON
    /// </summary>
    [HttpPost("result/{analysisId}/export/json")]
    [ProducesResponseType(typeof(ExportResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportToJson(string analysisId)
    {
        var clinicId = GetClinicId();
        if (string.IsNullOrEmpty(clinicId))
            return Unauthorized(new { message = "Chưa xác thực phòng khám" });
        try
        {
            var result = await _exportService.ExportToJsonAsync(analysisId, clinicId, RequesterTypes.Clinic);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clinic {ClinicId} export JSON for {AnalysisId} failed", clinicId, analysisId);
            return StatusCode(500, new { message = "Không thể export JSON" });
        }
    }

    /// <summary>
    /// Download file export (PDF/CSV/JSON)
    /// </summary>
    [HttpGet("exports/{exportId}/download")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadExport(string exportId)
    {
        var clinicId = GetClinicId();
        if (string.IsNullOrEmpty(clinicId))
            return Unauthorized(new { message = "Chưa xác thực phòng khám" });
        try
        {
            var fileBytes = await _exportService.DownloadExportFileAsync(exportId, clinicId);
            if (fileBytes == null || fileBytes.Length == 0)
                return NotFound(new { message = "Không tìm thấy file hoặc file đã hết hạn" });
            var export = await _exportService.GetExportByIdAsync(exportId, clinicId);
            var contentType = export?.ReportType?.ToUpperInvariant() switch
            {
                "PDF" => "application/pdf",
                "CSV" => "text/csv",
                "JSON" => "application/json",
                _ => "application/octet-stream"
            };
            var fileName = export?.FileName ?? $"export_{exportId}";
            return File(fileBytes, contentType, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clinic {ClinicId} download export {ExportId} failed", clinicId, exportId);
            return NotFound(new { message = "Không thể tải file" });
        }
    }

    #endregion
}
