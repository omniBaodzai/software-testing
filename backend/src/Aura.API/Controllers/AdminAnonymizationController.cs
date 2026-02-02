using Aura.Application.Services.Anonymization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Aura.API.Controllers;

/// <summary>
/// Controller để export anonymized data cho AI retraining (NFR-11)
/// </summary>
[ApiController]
[Route("api/admin/anonymization")]
[Authorize(Policy = "AdminOnly")]
public class AdminAnonymizationController : ControllerBase
{
    private readonly IDataAnonymizationService _anonymizationService;
    private readonly ILogger<AdminAnonymizationController> _logger;

    public AdminAnonymizationController(
        IDataAnonymizationService anonymizationService,
        ILogger<AdminAnonymizationController> logger)
    {
        _anonymizationService = anonymizationService;
        _logger = logger;
    }

    /// <summary>
    /// Export anonymized training data cho AI retraining (NFR-11)
    /// </summary>
    [HttpGet("export-training-data")]
    public async Task<ActionResult> ExportTrainingData(
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate,
        [FromQuery] int? limit,
        [FromQuery] string format = "json")
    {
        try
        {
            var data = await _anonymizationService.ExportAnonymizedTrainingDataAsync(
                startDate,
                endDate,
                limit);

            if (format.ToLower() == "csv")
            {
                // Convert to CSV format
                var csv = new System.Text.StringBuilder();
                csv.AppendLine("AnonymousId,ImageUrl,RiskLevel,ConfidenceScore,DetectedConditions,Recommendations,CreatedDate,HasFeedback,FeedbackType,OriginalRiskLevel,CorrectedRiskLevel");

                foreach (var result in data.AnalysisResults)
                {
                    csv.AppendLine($"{result.AnonymousId},{result.ImageUrl},{result.RiskLevel},{result.ConfidenceScore},{result.DetectedConditions},{result.Recommendations},{result.CreatedDate},{result.HasFeedback},{result.FeedbackType},{result.OriginalRiskLevel},{result.CorrectedRiskLevel}");
                }

                return File(
                    System.Text.Encoding.UTF8.GetBytes(csv.ToString()),
                    "text/csv",
                    $"anonymized-training-data-{DateTime.UtcNow:yyyyMMdd}.csv");
            }

            return Ok(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting anonymized training data");
            return StatusCode(500, new { message = $"Lỗi khi export anonymized data: {ex.Message}" });
        }
    }

    /// <summary>
    /// Anonymize một analysis result cụ thể
    /// </summary>
    [HttpGet("anonymize-result/{resultId}")]
    public async Task<ActionResult<AnonymizedAnalysisResultDto>> AnonymizeResult(string resultId)
    {
        try
        {
            var anonymized = await _anonymizationService.AnonymizeAnalysisResultAsync(resultId);
            
            if (anonymized == null)
            {
                return NotFound(new { message = "Không tìm thấy analysis result" });
            }

            return Ok(anonymized);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error anonymizing result {ResultId}", resultId);
            return StatusCode(500, new { message = $"Lỗi khi anonymize result: {ex.Message}" });
        }
    }
}
