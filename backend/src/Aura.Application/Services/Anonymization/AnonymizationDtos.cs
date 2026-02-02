namespace Aura.Application.Services.Anonymization;

/// <summary>
/// DTO cho anonymized training data (NFR-11)
/// </summary>
public class AnonymizedTrainingDataDto
{
    public DateTime ExportDate { get; set; }
    public int TotalRecords { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public List<AnonymizedAnalysisResultDto> AnalysisResults { get; set; } = new();
}

/// <summary>
/// DTO cho một anonymized analysis result
/// </summary>
public class AnonymizedAnalysisResultDto
{
    public string AnonymousId { get; set; } = string.Empty; // Hash của ResultId, không thể reverse
    public string? ImageUrl { get; set; } // Giữ lại (không chứa PII)
    public string? RiskLevel { get; set; }
    public decimal? ConfidenceScore { get; set; }
    public string? DetectedConditions { get; set; }
    public string? Recommendations { get; set; }
    public string? DetailedFindings { get; set; }
    public string? RawAiOutput { get; set; }
    public DateTime? CreatedDate { get; set; }
    
    // Feedback data (nếu có)
    public bool HasFeedback { get; set; }
    public string? FeedbackType { get; set; }
    public string? OriginalRiskLevel { get; set; }
    public string? CorrectedRiskLevel { get; set; }
    public string? FeedbackNotes { get; set; }
}
