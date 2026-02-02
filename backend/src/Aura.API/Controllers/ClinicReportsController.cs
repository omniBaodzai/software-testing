using Aura.Application.DTOs.Clinic;
using Aura.Application.Services.Reports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Aura.API.Controllers;

/// <summary>
/// Controller cho Clinic Reports Generation (FR-26, FR-30)
/// </summary>
[ApiController]
[Route("api/clinic/reports")]
[Authorize]
[Produces("application/json")]
public class ClinicReportsController : ControllerBase
{
    private readonly IClinicReportService _reportService;
    private readonly ILogger<ClinicReportsController> _logger;

    public ClinicReportsController(
        IClinicReportService reportService,
        ILogger<ClinicReportsController> logger)
    {
        _reportService = reportService ?? throw new ArgumentNullException(nameof(reportService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Generate clinic-wide report (FR-26)
    /// </summary>
    [HttpPost("generate")]
    [ProducesResponseType(typeof(ClinicReportDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GenerateReport([FromBody] CreateClinicReportDto dto)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized(new { message = "Chưa xác thực người dùng" });

        try
        {
            var report = await _reportService.GenerateReportAsync(dto, userId);
            
            // Export to file if requested
            if (dto.ExportToFile && !string.IsNullOrWhiteSpace(dto.ExportFormat))
            {
                var fileUrl = await _reportService.ExportReportAsync(report.Id, dto.ExportFormat, userId);
                if (!string.IsNullOrWhiteSpace(fileUrl))
                {
                    report.ReportFileUrl = fileUrl;
                }
            }

            return CreatedAtAction(nameof(GetReport), new { id = report.Id }, report);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating clinic report for clinic {ClinicId}", dto.ClinicId);
            return StatusCode(500, new { message = "Không thể generate clinic report" });
        }
    }

    /// <summary>
    /// Get clinic report by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ClinicReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetReport(string id)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized(new { message = "Chưa xác thực người dùng" });

        try
        {
            var report = await _reportService.GetReportByIdAsync(id, userId);
            if (report == null)
            {
                return NotFound(new { message = "Không tìm thấy clinic report" });
            }

            return Ok(report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting clinic report {ReportId}", id);
            return StatusCode(500, new { message = "Không thể lấy clinic report" });
        }
    }

    /// <summary>
    /// Get all clinic reports
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<ClinicReportDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetReports([FromQuery] string? clinicId = null, [FromQuery] string? reportType = null)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized(new { message = "Chưa xác thực người dùng" });

        try
        {
            var reports = await _reportService.GetReportsAsync(userId, clinicId, reportType);
            return Ok(reports);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting clinic reports");
            return StatusCode(500, new { message = "Không thể lấy danh sách clinic reports" });
        }
    }

    /// <summary>
    /// Get available report templates
    /// </summary>
    [HttpGet("templates")]
    [ProducesResponseType(typeof(List<ReportTemplateDto>), StatusCodes.Status200OK)]
    public IActionResult GetTemplates()
    {
        try
        {
            var templates = _reportService.GetReportTemplates();
            return Ok(templates);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting report templates");
            return StatusCode(500, new { message = "Không thể lấy danh sách templates" });
        }
    }

    /// <summary>
    /// Get clinic information
    /// </summary>
    [HttpGet("clinic/{clinicId}")]
    [ProducesResponseType(typeof(Aura.Application.Services.Reports.ClinicInfoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetClinicInfo(string clinicId)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized(new { message = "Chưa xác thực người dùng" });

        try
        {
            var clinicInfo = await _reportService.GetClinicInfoAsync(clinicId, userId);
            if (clinicInfo == null)
            {
                return NotFound(new { message = "Không tìm thấy clinic" });
            }

            return Ok(clinicInfo);
        }
        catch (UnauthorizedAccessException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting clinic info for clinic {ClinicId}", clinicId);
            return StatusCode(500, new { message = "Không thể lấy thông tin clinic" });
        }
    }

    /// <summary>
    /// Export clinic report to file (PDF/CSV/JSON)
    /// </summary>
    [HttpPost("{reportId}/export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportReport(string reportId, [FromQuery] string format = "PDF")
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized(new { message = "Chưa xác thực người dùng" });

        if (!new[] { "PDF", "CSV", "JSON" }.Contains(format.ToUpper()))
        {
            return BadRequest(new { message = "Format phải là PDF, CSV hoặc JSON" });
        }

        try
        {
            var fileUrl = await _reportService.ExportReportAsync(reportId, format.ToUpper(), userId);
            if (string.IsNullOrWhiteSpace(fileUrl))
            {
                return StatusCode(500, new { message = "Không thể export report" });
            }

            return Ok(new { fileUrl, format });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting report {ReportId}", reportId);
            return StatusCode(500, new { message = "Không thể export report" });
        }
    }

    #region Private Methods

    private string? GetCurrentUserId()
    {
        // Clinic admin: NameIdentifier = adminId
        return User.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    #endregion
}
