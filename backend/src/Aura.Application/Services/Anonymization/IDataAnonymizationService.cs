namespace Aura.Application.Services.Anonymization;

/// <summary>
/// NFR-11: Service để anonymize dữ liệu nhạy cảm trước khi dùng cho AI model retraining
/// Xóa/thay thế PII (Personally Identifiable Information) như email, name, phone, address, user ID
/// Giữ lại dữ liệu y tế cần thiết (image URL, analysis results, annotations, risk levels)
/// </summary>
public interface IDataAnonymizationService
{
    /// <summary>
    /// Anonymize analysis results và feedback data để export cho AI retraining
    /// </summary>
    Task<AnonymizedTrainingDataDto> ExportAnonymizedTrainingDataAsync(
        DateTime? startDate = null,
        DateTime? endDate = null,
        int? limit = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Anonymize một analysis result cụ thể
    /// </summary>
    Task<AnonymizedAnalysisResultDto?> AnonymizeAnalysisResultAsync(
        string resultId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Anonymize old audit logs theo retention policy (background job)
    /// </summary>
    Task<int> AnonymizeOldAuditLogsAsync(
        int retentionDays = 365,
        CancellationToken cancellationToken = default);
}
