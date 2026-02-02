using Aura.Application.Services.Analysis;
using Aura.Application.Services.Auth;
using Aura.Infrastructure.Services.RabbitMQ;
using Aura.Application.Services.Export;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aura.API.Services.BackgroundJobs;

/// <summary>
/// Background Worker Service cho Analysis Queue Processing
/// Sử dụng Hangfire để xử lý analysis jobs từ RabbitMQ
/// </summary>
public class AnalysisQueueWorker
{
    private readonly IAnalysisQueueService _queueService;
    private readonly IExportService? _exportService;
    private readonly ILogger<AnalysisQueueWorker> _logger;
    private readonly IConfiguration _configuration;
        private readonly IRabbitMQService _rabbitMqService;
        private readonly IEmailService _emailService;

        // Đảm bảo chỉ khởi tạo consumer email một lần
        private static bool _emailConsumerStarted;

        public AnalysisQueueWorker(
            IAnalysisQueueService queueService,
            ILogger<AnalysisQueueWorker> logger,
            IConfiguration configuration,
            IRabbitMQService rabbitMqService,
            IEmailService emailService,
            IExportService? exportService = null)
        {
            _queueService = queueService ?? throw new ArgumentNullException(nameof(queueService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _rabbitMqService = rabbitMqService ?? throw new ArgumentNullException(nameof(rabbitMqService));
            _emailService = emailService ?? throw new ArgumentNullException(nameof(emailService));
            _exportService = exportService;
        }

    /// <summary>
    /// Process queued analysis jobs from RabbitMQ
    /// Được gọi bởi Hangfire recurring job (mỗi 5 phút)
    /// 
    /// Giá trị: Xử lý async jobs, giảm blocking cho API
    /// </summary>
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 30, 60, 120 })]
    public async Task ProcessAnalysisQueueAsync()
    {
        _logger.LogInformation("[Hangfire] Starting analysis queue processing...");

        try
        {
            // =====================================================================
            // HANGFIRE + RABBITMQ: Process queued analysis jobs
            // Giá trị: Async processing, không block API, reliable retry
            // =====================================================================
            await _queueService.ProcessQueuedJobsAsync();

            var processedCount = 0; // TODO: Return from ProcessQueuedJobsAsync
            _logger.LogInformation("[Hangfire] Analysis queue processing completed. Processed: {Count}", processedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Hangfire] Error processing analysis queue");
            throw; // Hangfire will retry automatically
        }
    }

    /// <summary>
    /// Cleanup expired export files
    /// Recurring job chạy mỗi ngày lúc 2:00 AM
    /// 
    /// Giá trị: Tự động cleanup, tiết kiệm storage costs
    /// </summary>
    [AutomaticRetry(Attempts = 2)]
    public async Task CleanupExpiredExportsAsync()
    {
        _logger.LogInformation("[Hangfire] Starting cleanup of expired exports...");

        try
        {
            if (_exportService == null)
            {
                _logger.LogWarning("[Hangfire] ExportService not available, skipping cleanup");
                return;
            }

            // =====================================================================
            // HANGFIRE: Automated cleanup job
            // Giá trị: Auto cleanup, không cần manual intervention
            // =====================================================================
            var deletedCount = await _exportService.CleanupExpiredExportsAsync();
            
            _logger.LogInformation("[Hangfire] Cleanup completed. Deleted {Count} expired exports", deletedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Hangfire] Error cleaning up expired exports");
            throw; // Hangfire will retry automatically
        }
    }

    /// <summary>
    /// Process email queue from RabbitMQ
    /// Recurring job chạy mỗi 10 phút
    /// 
    /// Giá trị: Async email sending, không block API, reliable delivery
    /// </summary>
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 10, 30, 60 })]
    public async Task ProcessEmailQueueAsync()
    {
        _logger.LogInformation("[Hangfire] Processing email queue...");

        try
        {
            // =====================================================================
            // HANGFIRE + RABBITMQ: Khởi tạo consumer cho email.queue (chỉ một lần)
            // Giá trị: Các email jobs sẽ được consume nền, gửi email async, có retry
            // =====================================================================
            if (_emailConsumerStarted)
            {
                _logger.LogInformation("[Hangfire] Email consumer already running, skipping initialization.");
                return;
            }

            _rabbitMqService.Consume<EmailQueueMessage>("email.queue", async message =>
            {
                if (string.IsNullOrWhiteSpace(message.ToEmail) ||
                    string.IsNullOrWhiteSpace(message.Subject) ||
                    string.IsNullOrWhiteSpace(message.HtmlBody))
                {
                    _logger.LogWarning("Invalid email job payload: {@Message}", message);
                    return;
                }

                var success = await _emailService.SendCustomEmailAsync(
                    message.ToEmail,
                    message.Subject,
                    message.HtmlBody);

                if (!success)
                {
                    _logger.LogWarning("Failed to send queued email to {Email}", message.ToEmail);
                }
                else
                {
                    _logger.LogInformation("Queued email sent successfully to {Email}", message.ToEmail);
                }
            });

            _emailConsumerStarted = true;
            _logger.LogInformation("[Hangfire] Email queue consumer started and listening on 'email.queue'.");

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Hangfire] Error processing email queue");
            throw; // Hangfire will retry automatically
        }
    }

    /// <summary>
    /// Payload cho email jobs trong RabbitMQ "email.queue"
    /// </summary>
    private class EmailQueueMessage
    {
        public string ToEmail { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string HtmlBody { get; set; } = string.Empty;
    }
}
