using Aura.Application.DTOs.Images;

namespace Aura.Application.Services.Analysis;

/// <summary>
/// Service for queuing and processing batch AI analysis jobs (NFR-2: ≥100 images per batch)
/// </summary>
public interface IAnalysisQueueService
{
    /// <summary>
    /// Queue a batch of images for AI analysis
    /// </summary>
    Task<string> QueueBatchAnalysisAsync(string clinicId, List<string> imageIds, string? batchId = null);

    /// <summary>
    /// Get status of a batch analysis job
    /// </summary>
    Task<BatchAnalysisStatusDto?> GetBatchAnalysisStatusAsync(string jobId);

    /// <summary>
    /// Process queued analysis jobs (should be called by background worker)
    /// Returns the number of jobs processed
    /// </summary>
    Task<int> ProcessQueuedJobsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// List recent analysis jobs for a clinic (for dashboard)
    /// </summary>
    Task<List<BatchAnalysisStatusDto>> ListJobsForClinicAsync(string clinicId, int limit = 10);
}

