using Aura.Application.Services.Anonymization;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aura.API.Services.BackgroundJobs;

/// <summary>
/// Background Worker cho Data Anonymization (NFR-11)
/// Anonymize dữ liệu cũ theo retention policy
/// </summary>
public class DataAnonymizationWorker
{
    private readonly IDataAnonymizationService _anonymizationService;
    private readonly ILogger<DataAnonymizationWorker> _logger;
    private readonly IConfiguration _configuration;

    public DataAnonymizationWorker(
        IDataAnonymizationService anonymizationService,
        ILogger<DataAnonymizationWorker> logger,
        IConfiguration configuration)
    {
        _anonymizationService = anonymizationService;
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// Anonymize old audit logs theo retention policy
    /// Recurring job chạy mỗi tuần vào Chủ nhật lúc 2:00 AM
    /// 
    /// NFR-11: Anonymize sensitive data trước khi dùng cho retraining hoặc sau retention period
    /// </summary>
    [AutomaticRetry(Attempts = 2)]
    public async Task AnonymizeOldAuditLogsAsync()
    {
        _logger.LogInformation("[Hangfire] Starting anonymization of old audit logs...");

        try
        {
            var retentionDays = _configuration.GetValue<int>("Compliance:AuditLogRetentionDays", 365);
            
            var anonymizedCount = await _anonymizationService.AnonymizeOldAuditLogsAsync(retentionDays);
            
            _logger.LogInformation("[Hangfire] Anonymization completed. Anonymized {Count} audit log entries", anonymizedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Hangfire] Error anonymizing old audit logs");
            throw; // Hangfire will retry automatically
        }
    }
}
