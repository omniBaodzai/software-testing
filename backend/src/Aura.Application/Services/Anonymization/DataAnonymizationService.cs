using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Security.Cryptography;
using System.Text;

namespace Aura.Application.Services.Anonymization;

/// <summary>
/// NFR-11: Implementation của Data Anonymization Service
/// Anonymize dữ liệu nhạy cảm (PII) trước khi export cho AI retraining
/// </summary>
public class DataAnonymizationService : IDataAnonymizationService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<DataAnonymizationService> _logger;
    private readonly string _connectionString;

    public DataAnonymizationService(
        IConfiguration configuration,
        ILogger<DataAnonymizationService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Database connection string not found");
    }

    /// <summary>
    /// Hash một giá trị để tạo identifier ẩn danh
    /// </summary>
    private string HashValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "anonymous";

        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(value));
        return Convert.ToBase64String(hashBytes)[..16]; // Lấy 16 ký tự đầu
    }

    public async Task<AnonymizedTrainingDataDto> ExportAnonymizedTrainingDataAsync(
        DateTime? startDate = null,
        DateTime? endDate = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var whereConditions = new List<string> { "ar.IsDeleted = false" };
            var parameters = new List<NpgsqlParameter>();

            if (startDate.HasValue)
            {
                whereConditions.Add("ar.CreatedDate >= @StartDate");
                parameters.Add(new NpgsqlParameter("StartDate", startDate.Value.Date));
            }

            if (endDate.HasValue)
            {
                whereConditions.Add("ar.CreatedDate <= @EndDate");
                parameters.Add(new NpgsqlParameter("EndDate", endDate.Value.Date));
            }

            var whereClause = string.Join(" AND ", whereConditions);
            var limitClause = limit.HasValue ? $"LIMIT {limit.Value}" : "";

            // Query analysis results với feedback (chỉ lấy những có IsUsedForTraining = true hoặc null)
            var sql = $@"
                SELECT 
                    ar.Id,
                    ar.ImageUrl,
                    ar.RiskLevel,
                    ar.ConfidenceScore,
                    ar.DetectedConditions,
                    ar.Recommendations,
                    ar.DetailedFindings,
                    ar.RawAiOutput,
                    ar.CreatedDate,
                    af.Id as FeedbackId,
                    af.FeedbackType,
                    af.OriginalRiskLevel,
                    af.CorrectedRiskLevel,
                    af.FeedbackNotes
                FROM analysis_results ar
                LEFT JOIN ai_feedback af ON af.ResultId = ar.Id AND (af.IsUsedForTraining = true OR af.IsUsedForTraining IS NULL)
                WHERE {whereClause}
                ORDER BY ar.CreatedDate DESC
                {limitClause}";

            using var command = new NpgsqlCommand(sql, connection);
            foreach (var param in parameters)
            {
                command.Parameters.Add(param);
            }

            var anonymizedResults = new List<AnonymizedAnalysisResultDto>();

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var resultId = reader.GetString(0);
                var imageUrl = reader.IsDBNull(1) ? null : reader.GetString(1);
                var riskLevel = reader.IsDBNull(2) ? null : reader.GetString(2);
                var confidenceScore = reader.IsDBNull(3) ? (decimal?)null : reader.GetDecimal(3);
                var detectedConditions = reader.IsDBNull(4) ? null : reader.GetString(4);
                var recommendations = reader.IsDBNull(5) ? null : reader.GetString(5);
                var detailedFindings = reader.IsDBNull(6) ? null : reader.GetString(6);
                var rawAiOutput = reader.IsDBNull(7) ? null : reader.GetString(7);
                var createdDate = reader.IsDBNull(8) ? (DateTime?)null : reader.GetDateTime(8);

                // Anonymize: Tạo anonymous ID từ resultId
                var anonymousId = HashValue(resultId);

                anonymizedResults.Add(new AnonymizedAnalysisResultDto
                {
                    AnonymousId = anonymousId,
                    ImageUrl = imageUrl, // Giữ lại image URL (không chứa PII)
                    RiskLevel = riskLevel,
                    ConfidenceScore = confidenceScore,
                    DetectedConditions = detectedConditions,
                    Recommendations = recommendations,
                    DetailedFindings = detailedFindings,
                    RawAiOutput = rawAiOutput,
                    CreatedDate = createdDate,
                    HasFeedback = !reader.IsDBNull(9),
                    FeedbackType = reader.IsDBNull(10) ? null : reader.GetString(10),
                    OriginalRiskLevel = reader.IsDBNull(12) ? null : reader.GetString(12),
                    CorrectedRiskLevel = reader.IsDBNull(13) ? null : reader.GetString(13),
                    FeedbackNotes = reader.IsDBNull(14) ? null : reader.GetString(14)
                });
            }

            _logger.LogInformation("Exported {Count} anonymized analysis results for AI training", anonymizedResults.Count);

            return new AnonymizedTrainingDataDto
            {
                ExportDate = DateTime.UtcNow,
                TotalRecords = anonymizedResults.Count,
                StartDate = startDate,
                EndDate = endDate,
                AnalysisResults = anonymizedResults
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting anonymized training data");
            throw;
        }
    }

    public async Task<AnonymizedAnalysisResultDto?> AnonymizeAnalysisResultAsync(
        string resultId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var sql = @"
                SELECT 
                    ar.Id,
                    ar.ImageUrl,
                    ar.RiskLevel,
                    ar.ConfidenceScore,
                    ar.DetectedConditions,
                    ar.Recommendations,
                    ar.DetailedFindings,
                    ar.RawAiOutput,
                    ar.CreatedDate
                FROM analysis_results ar
                WHERE ar.Id = @ResultId AND ar.IsDeleted = false";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("ResultId", resultId);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var anonymousId = HashValue(resultId);

            return new AnonymizedAnalysisResultDto
            {
                AnonymousId = anonymousId,
                ImageUrl = reader.IsDBNull(1) ? null : reader.GetString(1),
                RiskLevel = reader.IsDBNull(2) ? null : reader.GetString(2),
                ConfidenceScore = reader.IsDBNull(3) ? (decimal?)null : reader.GetDecimal(3),
                DetectedConditions = reader.IsDBNull(4) ? null : reader.GetString(4),
                Recommendations = reader.IsDBNull(5) ? null : reader.GetString(5),
                DetailedFindings = reader.IsDBNull(6) ? null : reader.GetString(6),
                RawAiOutput = reader.IsDBNull(7) ? null : reader.GetString(7),
                CreatedDate = reader.IsDBNull(8) ? (DateTime?)null : reader.GetDateTime(8),
                HasFeedback = false
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error anonymizing analysis result {ResultId}", resultId);
            throw;
        }
    }

    public async Task<int> AnonymizeOldAuditLogsAsync(
        int retentionDays = 365,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);

            // Anonymize các trường PII trong audit_logs
            var sql = @"
                UPDATE audit_logs
                SET 
                    UserId = CASE WHEN UserId IS NOT NULL THEN 'anon_' || SUBSTRING(MD5(UserId), 1, 8) ELSE UserId END,
                    DoctorId = CASE WHEN DoctorId IS NOT NULL THEN 'anon_' || SUBSTRING(MD5(DoctorId), 1, 8) ELSE DoctorId END,
                    AdminId = CASE WHEN AdminId IS NOT NULL THEN 'anon_' || SUBSTRING(MD5(AdminId), 1, 8) ELSE AdminId END,
                    IpAddress = CASE WHEN IpAddress IS NOT NULL THEN 'xxx.xxx.xxx.xxx' ELSE IpAddress END,
                    UserAgent = CASE WHEN UserAgent IS NOT NULL THEN 'Anonymized' ELSE UserAgent END,
                    OldValues = CASE WHEN OldValues::text LIKE '%email%' OR OldValues::text LIKE '%phone%' OR OldValues::text LIKE '%name%' THEN '{}'::jsonb ELSE OldValues END,
                    NewValues = CASE WHEN NewValues::text LIKE '%email%' OR NewValues::text LIKE '%phone%' OR NewValues::text LIKE '%name%' THEN '{}'::jsonb ELSE NewValues END
                WHERE CreatedDate < @CutoffDate
                  AND (UserId IS NOT NULL OR DoctorId IS NOT NULL OR AdminId IS NOT NULL OR IpAddress IS NOT NULL)";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("CutoffDate", cutoffDate.Date);

            var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);

            _logger.LogInformation("Anonymized {Count} old audit logs older than {Days} days", affectedRows, retentionDays);

            return affectedRows;
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            _logger.LogWarning("Table audit_logs does not exist, skipping anonymization");
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error anonymizing old audit logs");
            throw;
        }
    }
}
