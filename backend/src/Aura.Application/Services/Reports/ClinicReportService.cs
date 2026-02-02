using Aura.Application.DTOs.Clinic;
using Aura.Application.Services.Export;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Text.Json;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using System.Text;
using System.IO;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Aura.Application.Services.Reports;

/// <summary>
/// Service implementation cho Clinic Report Generation (FR-26)
/// </summary>
public class ClinicReportService : IClinicReportService
{
    private readonly string _connectionString;
    private readonly ILogger<ClinicReportService>? _logger;
    private readonly IExportService _exportService;
    private readonly IConfiguration _configuration;
    private readonly Cloudinary? _cloudinary;

    public ClinicReportService(
        IConfiguration configuration,
        IExportService exportService,
        ILogger<ClinicReportService>? logger = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Database connection string not configured");
        _exportService = exportService ?? throw new ArgumentNullException(nameof(exportService));
        _logger = logger;
        
        // Initialize Cloudinary for file uploads
        var cloudName = configuration["Cloudinary:CloudName"];
        var apiKey = configuration["Cloudinary:ApiKey"];
        var apiSecret = configuration["Cloudinary:ApiSecret"];
        
        if (!string.IsNullOrWhiteSpace(cloudName) && 
            !string.IsNullOrWhiteSpace(apiKey) && 
            !string.IsNullOrWhiteSpace(apiSecret))
        {
            var account = new Account(cloudName, apiKey, apiSecret);
            _cloudinary = new Cloudinary(account);
            QuestPDF.Settings.License = LicenseType.Community;
        }
    }

    public async Task<ClinicReportDto> GenerateReportAsync(CreateClinicReportDto dto, string userId)
    {
        if (string.IsNullOrWhiteSpace(dto.ClinicId))
            throw new ArgumentException("ClinicId is required", nameof(dto));

        if (!new[] { "ScreeningCampaign", "RiskDistribution", "MonthlySummary", "AnnualReport", "Custom" }.Contains(dto.ReportType))
            throw new ArgumentException("Invalid ReportType", nameof(dto));

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Verify user has access to clinic
            await VerifyClinicAccessAsync(connection, dto.ClinicId, userId);

            // Calculate period if not provided
            var periodStart = dto.PeriodStart ?? DateTime.UtcNow.AddMonths(-1).Date;
            var periodEnd = dto.PeriodEnd ?? DateTime.UtcNow.Date;

            // Generate report statistics
            var stats = await GetReportStatisticsAsync(connection, dto.ClinicId, periodStart, periodEnd);

            // Get detailed statistics
            var dailyStats = await GetDailyStatisticsAsync(connection, dto.ClinicId, periodStart, periodEnd);

            // Prepare report data
            var reportData = new Dictionary<string, object>
            {
                ["periodStart"] = periodStart.ToString("yyyy-MM-dd"),
                ["periodEnd"] = periodEnd.ToString("yyyy-MM-dd"),
                ["totalPatients"] = stats.TotalPatients,
                ["totalAnalyses"] = stats.TotalAnalyses,
                ["highRiskCount"] = stats.HighRiskCount,
                ["mediumRiskCount"] = stats.MediumRiskCount,
                ["lowRiskCount"] = stats.LowRiskCount,
                ["riskDistribution"] = new Dictionary<string, object>
                {
                    ["high"] = stats.TotalAnalyses > 0 ? Math.Round((double)stats.HighRiskCount / stats.TotalAnalyses * 100, 2) : 0,
                    ["medium"] = stats.TotalAnalyses > 0 ? Math.Round((double)stats.MediumRiskCount / stats.TotalAnalyses * 100, 2) : 0,
                    ["low"] = stats.TotalAnalyses > 0 ? Math.Round((double)stats.LowRiskCount / stats.TotalAnalyses * 100, 2) : 0
                },
                ["dailyStatistics"] = dailyStats
            };

            // Add type-specific data
            if (dto.ReportType == "ScreeningCampaign")
            {
                reportData["campaignData"] = await GetCampaignDataAsync(connection, dto.ClinicId, periodStart, periodEnd);
            }
            else if (dto.ReportType == "RiskDistribution")
            {
                reportData["riskBreakdown"] = await GetRiskBreakdownAsync(connection, dto.ClinicId, periodStart, periodEnd);
            }

            // Create clinic report record
            var reportId = Guid.NewGuid().ToString();
            var report = await SaveReportAsync(connection, reportId, dto, stats, reportData, periodStart, periodEnd, userId);

            _logger?.LogInformation("Clinic report generated: {ReportId} for clinic {ClinicId}, Type: {ReportType}",
                reportId, dto.ClinicId, dto.ReportType);

            return report;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error generating clinic report for clinic {ClinicId}", dto.ClinicId);
            throw;
        }
    }

    public async Task<ClinicReportDto?> GetReportByIdAsync(string reportId, string userId)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT cr.Id, cr.ClinicId, cr.ReportName, cr.ReportType, cr.PeriodStart, cr.PeriodEnd,
                       cr.TotalPatients, cr.TotalAnalyses, cr.HighRiskCount, cr.MediumRiskCount, cr.LowRiskCount,
                       cr.ReportData, cr.ReportFileUrl, cr.GeneratedAt
                FROM clinic_reports cr
                WHERE cr.Id = @Id
                    AND COALESCE(cr.IsDeleted, false) = false
                    AND (cr.ClinicId IN (SELECT ClinicId FROM clinic_users WHERE UserId = @UserId AND IsDeleted = false)
                         OR cr.ClinicId IN (SELECT ClinicId FROM clinic_doctors WHERE DoctorId = @UserId AND IsDeleted = false)
                         OR cr.ClinicId IN (SELECT ClinicId FROM clinic_admins WHERE Id = @UserId AND IsDeleted = false))";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("Id", reportId);
            command.Parameters.AddWithValue("UserId", userId);

            using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return null;

            return MapReaderToReportDto(reader);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting clinic report {ReportId}", reportId);
            throw;
        }
    }

    public async Task<List<ClinicReportDto>> GetReportsAsync(string userId, string? clinicId = null, string? reportType = null)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT cr.Id, cr.ClinicId, cr.ReportName, cr.ReportType, cr.PeriodStart, cr.PeriodEnd,
                       cr.TotalPatients, cr.TotalAnalyses, cr.HighRiskCount, cr.MediumRiskCount, cr.LowRiskCount,
                       cr.ReportData, cr.ReportFileUrl, cr.GeneratedAt
                FROM clinic_reports cr
                WHERE COALESCE(cr.IsDeleted, false) = false
                    AND (cr.ClinicId IN (SELECT ClinicId FROM clinic_users WHERE UserId = @UserId AND IsDeleted = false)
                         OR cr.ClinicId IN (SELECT ClinicId FROM clinic_doctors WHERE DoctorId = @UserId AND IsDeleted = false)
                         OR cr.ClinicId IN (SELECT ClinicId FROM clinic_admins WHERE Id = @UserId AND IsDeleted = false))
                    AND (@ClinicId IS NULL OR cr.ClinicId = @ClinicId)
                    AND (@ReportType IS NULL OR cr.ReportType = @ReportType)
                ORDER BY cr.GeneratedAt DESC";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("UserId", userId);
            command.Parameters.AddWithValue("ClinicId", (object?)clinicId ?? DBNull.Value);
            command.Parameters.AddWithValue("ReportType", (object?)reportType ?? DBNull.Value);

            var reports = new List<ClinicReportDto>();
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                reports.Add(MapReaderToReportDto(reader));
            }

            return reports;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting clinic reports");
            throw;
        }
    }

    public async Task<string?> ExportReportAsync(string reportId, string format, string userId)
    {
        var report = await GetReportByIdAsync(reportId, userId);
        if (report == null)
            throw new InvalidOperationException("Report not found");

        try
        {
            _logger?.LogInformation("Exporting clinic report {ReportId} to {Format}", reportId, format);

            byte[] fileBytes;
            string fileExtension;
            string contentType;

            if (format.ToUpperInvariant() == "CSV")
            {
                fileBytes = await GenerateClinicReportCsvAsync(report);
                fileExtension = "csv";
                contentType = "text/csv";
            }
            else if (format.ToUpperInvariant() == "PDF")
            {
                fileBytes = await GenerateClinicReportPdfAsync(report);
                fileExtension = "pdf";
                contentType = "application/pdf";
            }
            else if (format.ToUpperInvariant() == "JSON")
            {
                var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
                fileBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(report, jsonOptions));
                fileExtension = "json";
                contentType = "application/json";
            }
            else
            {
                throw new ArgumentException($"Unsupported export format: {format}. Supported formats: CSV, PDF, JSON");
            }

            // Upload to Cloudinary
            var fileName = $"clinic_report_{reportId}_{DateTime.UtcNow:yyyyMMddHHmmss}.{fileExtension}";
            var fileUrl = await UploadClinicReportFileAsync(fileBytes, fileName, contentType);

            // Update report with file URL
            await UpdateReportFileUrlAsync(reportId, fileUrl);

            _logger?.LogInformation("Clinic report exported successfully. ReportId: {ReportId}, Format: {Format}, FileUrl: {FileUrl}", 
                reportId, format, fileUrl);

            return fileUrl;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error exporting clinic report {ReportId} to {Format}", reportId, format);
            throw;
        }
    }

    public async Task<ClinicInfoDto?> GetClinicInfoAsync(string clinicId, string userId)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Verify access
            await VerifyClinicAccessAsync(connection, clinicId, userId);

            var sql = @"
                SELECT Id, ClinicName, Email, Phone, Address
                FROM clinics
                WHERE Id = @ClinicId AND COALESCE(IsDeleted, false) = false";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("ClinicId", clinicId);

            using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return null;

            return new ClinicInfoDto
            {
                Id = reader.GetString(0),
                ClinicName = reader.GetString(1),
                Email = reader.GetString(2),
                Phone = reader.IsDBNull(3) ? null : reader.GetString(3),
                Address = reader.IsDBNull(4) ? string.Empty : reader.GetString(4)
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting clinic info for clinic {ClinicId}", clinicId);
            throw;
        }
    }

    public List<ReportTemplateDto> GetReportTemplates()
    {
        return new List<ReportTemplateDto>
        {
            new ReportTemplateDto
            {
                Type = "ScreeningCampaign",
                Name = "Báo cáo Chiến dịch Sàng lọc",
                Description = "Báo cáo tổng hợp về chiến dịch sàng lọc trong khoảng thời gian",
                Icon = "campaign",
                RequiresPeriod = true
            },
            new ReportTemplateDto
            {
                Type = "RiskDistribution",
                Name = "Báo cáo Phân bố Rủi ro",
                Description = "Phân tích phân bố mức độ rủi ro của bệnh nhân",
                Icon = "risk",
                RequiresPeriod = true
            },
            new ReportTemplateDto
            {
                Type = "MonthlySummary",
                Name = "Báo cáo Tổng hợp Tháng",
                Description = "Báo cáo tổng hợp hoạt động trong tháng",
                Icon = "calendar",
                RequiresPeriod = false
            },
            new ReportTemplateDto
            {
                Type = "AnnualReport",
                Name = "Báo cáo Tổng hợp Năm",
                Description = "Báo cáo tổng hợp hoạt động trong năm",
                Icon = "year",
                RequiresPeriod = false
            },
            new ReportTemplateDto
            {
                Type = "Custom",
                Name = "Báo cáo Tùy chỉnh",
                Description = "Tạo báo cáo với khoảng thời gian tùy chỉnh",
                Icon = "custom",
                RequiresPeriod = true
            }
        };
    }

    #region Private Methods

    private async Task VerifyClinicAccessAsync(NpgsqlConnection connection, string clinicId, string userId)
    {
        // Allow: clinic_users (patient), clinic_doctors (doctor), or clinic_admins (admin of this clinic)
        var verifySql = @"
            SELECT Id FROM clinics 
            WHERE Id = @ClinicId 
                AND (Id IN (SELECT ClinicId FROM clinic_users WHERE UserId = @UserId AND IsDeleted = false) 
                     OR Id IN (SELECT ClinicId FROM clinic_doctors WHERE DoctorId = @UserId AND IsDeleted = false)
                     OR Id IN (SELECT ClinicId FROM clinic_admins WHERE Id = @UserId AND IsDeleted = false))
                AND COALESCE(IsDeleted, false) = false";

        using var verifyCmd = new NpgsqlCommand(verifySql, connection);
        verifyCmd.Parameters.AddWithValue("ClinicId", clinicId);
        verifyCmd.Parameters.AddWithValue("UserId", userId);

        var hasAccess = await verifyCmd.ExecuteScalarAsync();
        if (hasAccess == null)
            throw new UnauthorizedAccessException("Không có quyền truy cập clinic này");
    }

    private async Task<(int TotalPatients, int TotalAnalyses, int HighRiskCount, int MediumRiskCount, int LowRiskCount)> 
        GetReportStatisticsAsync(NpgsqlConnection connection, string clinicId, DateTime periodStart, DateTime periodEnd)
    {
        var statsSql = @"
            SELECT 
                COUNT(DISTINCT ar.UserId) as TotalPatients,
                COUNT(ar.Id) as TotalAnalyses,
                COUNT(CASE WHEN ar.OverallRiskLevel = 'High' OR ar.OverallRiskLevel = 'Critical' THEN 1 END) as HighRiskCount,
                COUNT(CASE WHEN ar.OverallRiskLevel = 'Medium' THEN 1 END) as MediumRiskCount,
                COUNT(CASE WHEN ar.OverallRiskLevel = 'Low' THEN 1 END) as LowRiskCount
            FROM analysis_results ar
            INNER JOIN retinal_images ri ON ar.ImageId = ri.Id
            WHERE ri.ClinicId = @ClinicId
                AND ar.AnalysisCompletedAt >= @PeriodStart
                AND ar.AnalysisCompletedAt <= @PeriodEnd
                AND COALESCE(ar.IsDeleted, false) = false
                AND ar.AnalysisStatus = 'Completed'";

        using var statsCmd = new NpgsqlCommand(statsSql, connection);
        statsCmd.Parameters.AddWithValue("ClinicId", clinicId);
        statsCmd.Parameters.AddWithValue("PeriodStart", periodStart);
        statsCmd.Parameters.AddWithValue("PeriodEnd", periodEnd.AddDays(1).AddSeconds(-1));

        using var statsReader = await statsCmd.ExecuteReaderAsync();
        if (!await statsReader.ReadAsync())
            return (0, 0, 0, 0, 0);

        return (
            statsReader.GetInt32(0),
            statsReader.GetInt32(1),
            statsReader.GetInt32(2),
            statsReader.GetInt32(3),
            statsReader.GetInt32(4)
        );
    }

    private async Task<List<Dictionary<string, object>>> GetDailyStatisticsAsync(
        NpgsqlConnection connection, string clinicId, DateTime periodStart, DateTime periodEnd)
    {
        var detailedStatsSql = @"
            SELECT 
                DATE_TRUNC('day', ar.AnalysisCompletedAt) as AnalysisDate,
                COUNT(*) as DailyCount,
                COUNT(CASE WHEN ar.OverallRiskLevel IN ('High', 'Critical') THEN 1 END) as DailyHighRisk
            FROM analysis_results ar
            INNER JOIN retinal_images ri ON ar.ImageId = ri.Id
            WHERE ri.ClinicId = @ClinicId
                AND ar.AnalysisCompletedAt >= @PeriodStart
                AND ar.AnalysisCompletedAt <= @PeriodEnd
                AND COALESCE(ar.IsDeleted, false) = false
                AND ar.AnalysisStatus = 'Completed'
            GROUP BY DATE_TRUNC('day', ar.AnalysisCompletedAt)
            ORDER BY AnalysisDate";

        using var detailedStatsCmd = new NpgsqlCommand(detailedStatsSql, connection);
        detailedStatsCmd.Parameters.AddWithValue("ClinicId", clinicId);
        detailedStatsCmd.Parameters.AddWithValue("PeriodStart", periodStart);
        detailedStatsCmd.Parameters.AddWithValue("PeriodEnd", periodEnd.AddDays(1).AddSeconds(-1));

        var dailyStats = new List<Dictionary<string, object>>();
        using var detailedStatsReader = await detailedStatsCmd.ExecuteReaderAsync();
        while (await detailedStatsReader.ReadAsync())
        {
            dailyStats.Add(new Dictionary<string, object>
            {
                ["date"] = detailedStatsReader.GetDateTime(0).ToString("yyyy-MM-dd"),
                ["count"] = detailedStatsReader.GetInt32(1),
                ["highRiskCount"] = detailedStatsReader.GetInt32(2)
            });
        }

        return dailyStats;
    }

    private async Task<Dictionary<string, object>> GetCampaignDataAsync(
        NpgsqlConnection connection, string clinicId, DateTime periodStart, DateTime periodEnd)
    {
        // Additional campaign-specific data
        return new Dictionary<string, object>
        {
            ["campaignPeriod"] = $"{periodStart:yyyy-MM-dd} to {periodEnd:yyyy-MM-dd}",
            ["totalScreened"] = 0 // Can be enhanced with actual campaign data
        };
    }

    private async Task<Dictionary<string, object>> GetRiskBreakdownAsync(
        NpgsqlConnection connection, string clinicId, DateTime periodStart, DateTime periodEnd)
    {
        var riskBreakdownSql = @"
            SELECT 
                ar.OverallRiskLevel,
                COUNT(*) as Count
            FROM analysis_results ar
            INNER JOIN retinal_images ri ON ar.ImageId = ri.Id
            WHERE ri.ClinicId = @ClinicId
                AND ar.AnalysisCompletedAt >= @PeriodStart
                AND ar.AnalysisCompletedAt <= @PeriodEnd
                AND COALESCE(ar.IsDeleted, false) = false
                AND ar.AnalysisStatus = 'Completed'
            GROUP BY ar.OverallRiskLevel";

        using var riskCmd = new NpgsqlCommand(riskBreakdownSql, connection);
        riskCmd.Parameters.AddWithValue("ClinicId", clinicId);
        riskCmd.Parameters.AddWithValue("PeriodStart", periodStart);
        riskCmd.Parameters.AddWithValue("PeriodEnd", periodEnd.AddDays(1).AddSeconds(-1));

        var breakdown = new Dictionary<string, object>();
        using var reader = await riskCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            breakdown[reader.GetString(0)] = reader.GetInt32(1);
        }

        return breakdown;
    }

    private async Task<ClinicReportDto> SaveReportAsync(
        NpgsqlConnection connection,
        string reportId,
        CreateClinicReportDto dto,
        (int TotalPatients, int TotalAnalyses, int HighRiskCount, int MediumRiskCount, int LowRiskCount) stats,
        Dictionary<string, object> reportData,
        DateTime periodStart,
        DateTime periodEnd,
        string userId)
    {
        var reportSql = @"
            INSERT INTO clinic_reports
            (Id, ClinicId, ReportName, ReportType, PeriodStart, PeriodEnd,
             TotalPatients, TotalAnalyses, HighRiskCount, MediumRiskCount, LowRiskCount,
             ReportData, GeneratedBy, GeneratedAt, CreatedDate, CreatedBy, IsDeleted)
            VALUES
            (@Id, @ClinicId, @ReportName, @ReportType, @PeriodStart, @PeriodEnd,
             @TotalPatients, @TotalAnalyses, @HighRiskCount, @MediumRiskCount, @LowRiskCount,
             @ReportData, @GeneratedBy, @GeneratedAt, @CreatedDate, @CreatedBy, false)
            RETURNING Id, ClinicId, ReportName, ReportType, PeriodStart, PeriodEnd,
                      TotalPatients, TotalAnalyses, HighRiskCount, MediumRiskCount, LowRiskCount,
                      ReportData, ReportFileUrl, GeneratedAt";

        using var reportCmd = new NpgsqlCommand(reportSql, connection);
        reportCmd.Parameters.AddWithValue("Id", reportId);
        reportCmd.Parameters.AddWithValue("ClinicId", dto.ClinicId);
        reportCmd.Parameters.AddWithValue("ReportName", dto.ReportName);
        reportCmd.Parameters.AddWithValue("ReportType", dto.ReportType);
        reportCmd.Parameters.AddWithValue("PeriodStart", (object?)periodStart ?? DBNull.Value);
        reportCmd.Parameters.AddWithValue("PeriodEnd", (object?)periodEnd ?? DBNull.Value);
        reportCmd.Parameters.AddWithValue("TotalPatients", stats.TotalPatients);
        reportCmd.Parameters.AddWithValue("TotalAnalyses", stats.TotalAnalyses);
        reportCmd.Parameters.AddWithValue("HighRiskCount", stats.HighRiskCount);
        reportCmd.Parameters.AddWithValue("MediumRiskCount", stats.MediumRiskCount);
        reportCmd.Parameters.AddWithValue("LowRiskCount", stats.LowRiskCount);

        var reportDataParam = new NpgsqlParameter("ReportData", NpgsqlTypes.NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(reportData)
        };
        reportCmd.Parameters.Add(reportDataParam);

        // GeneratedBy FK references admins(Id); clinic admins are in clinic_admins, so use NULL to avoid FK violation
        reportCmd.Parameters.AddWithValue("GeneratedBy", DBNull.Value);
        reportCmd.Parameters.AddWithValue("GeneratedAt", DateTime.UtcNow);
        reportCmd.Parameters.AddWithValue("CreatedDate", DateTime.UtcNow.Date);
        reportCmd.Parameters.AddWithValue("CreatedBy", userId);

        using var reportReader = await reportCmd.ExecuteReaderAsync();
        if (!await reportReader.ReadAsync())
            throw new InvalidOperationException("Failed to create clinic report");

        return MapReaderToReportDto(reportReader);
    }

    private ClinicReportDto MapReaderToReportDto(NpgsqlDataReader reader)
    {
        return new ClinicReportDto
        {
            Id = reader.GetString(0),
            ClinicId = reader.GetString(1),
            ReportName = reader.GetString(2),
            ReportType = reader.GetString(3),
            PeriodStart = reader.IsDBNull(4) ? null : reader.GetDateTime(4),
            PeriodEnd = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
            TotalPatients = reader.GetInt32(6),
            TotalAnalyses = reader.GetInt32(7),
            HighRiskCount = reader.GetInt32(8),
            MediumRiskCount = reader.GetInt32(9),
            LowRiskCount = reader.GetInt32(10),
            ReportData = reader.IsDBNull(11)
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, object>>(reader.GetString(11)),
            ReportFileUrl = reader.IsDBNull(12) ? null : reader.GetString(12),
            GeneratedAt = reader.GetDateTime(13)
        };
    }

    private async Task<byte[]> GenerateClinicReportCsvAsync(ClinicReportDto report)
    {
        using var memoryStream = new MemoryStream();
        using var writer = new StreamWriter(memoryStream, new UTF8Encoding(true));
        using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true
        });

        // Write report summary section
        csv.WriteField("Báo cáo Clinic");
        csv.NextRecord();
        csv.WriteField("Tên báo cáo");
        csv.WriteField(report.ReportName);
        csv.NextRecord();
        csv.WriteField("Loại báo cáo");
        csv.WriteField(report.ReportType);
        csv.NextRecord();
        csv.WriteField("Thời gian bắt đầu");
        csv.WriteField(report.PeriodStart?.ToString("yyyy-MM-dd") ?? "N/A");
        csv.NextRecord();
        csv.WriteField("Thời gian kết thúc");
        csv.WriteField(report.PeriodEnd?.ToString("yyyy-MM-dd") ?? "N/A");
        csv.NextRecord();
        csv.WriteField("Tổng số bệnh nhân");
        csv.WriteField(report.TotalPatients);
        csv.NextRecord();
        csv.WriteField("Tổng số phân tích");
        csv.WriteField(report.TotalAnalyses);
        csv.NextRecord();
        csv.WriteField("Số ca rủi ro cao");
        csv.WriteField(report.HighRiskCount);
        csv.NextRecord();
        csv.WriteField("Số ca rủi ro trung bình");
        csv.WriteField(report.MediumRiskCount);
        csv.NextRecord();
        csv.WriteField("Số ca rủi ro thấp");
        csv.WriteField(report.LowRiskCount);
        csv.NextRecord();
        csv.NextRecord();

        // Write detailed statistics if available
        if (report.ReportData != null && report.ReportData.ContainsKey("dailyStatistics"))
        {
            csv.WriteField("Thống kê theo ngày");
            csv.NextRecord();
            csv.WriteField("Ngày");
            csv.WriteField("Số lượng phân tích");
            csv.WriteField("Số ca rủi ro cao");
            csv.NextRecord();

            if (report.ReportData["dailyStatistics"] is JsonElement dailyStatsElement &&
                dailyStatsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var dayStat in dailyStatsElement.EnumerateArray())
                {
                    csv.WriteField(dayStat.GetProperty("date").GetString() ?? "N/A");
                    csv.WriteField(dayStat.GetProperty("count").GetInt32());
                    csv.WriteField(dayStat.GetProperty("highRiskCount").GetInt32());
                    csv.NextRecord();
                }
            }
        }

        writer.Flush();
        return memoryStream.ToArray();
    }

    private async Task<byte[]> GenerateClinicReportPdfAsync(ClinicReportDto report)
    {
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.5f, Unit.Centimetre);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontSize(11));

                // Header
                page.Header().Element(c => ComposeClinicReportHeader(c, report));

                // Content
                page.Content().Element(c => ComposeClinicReportContent(c, report));

                // Footer
                page.Footer().Element(c => ComposeClinicReportFooter(c));
            });
        });

        return document.GeneratePdf();
    }

    private void ComposeClinicReportHeader(IContainer container, ClinicReportDto report)
    {
        container.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text("AURA")
                        .FontSize(32)
                        .Bold()
                        .FontColor(Colors.Blue.Darken3);
                    c.Item().Text("Hệ Thống Sàng Lọc Sức Khỏe Mạch Máu Võng Mạc")
                        .FontSize(11)
                        .FontColor(Colors.Grey.Darken2);
                });
                row.ConstantItem(180).AlignRight().Column(c =>
                {
                    c.Item().Text(report.ReportName)
                        .FontSize(16)
                        .Bold()
                        .FontColor(Colors.Blue.Darken2);
                    c.Item().PaddingTop(3)
                        .Text($"Ngày tạo: {report.GeneratedAt:dd/MM/yyyy HH:mm} UTC")
                        .FontSize(9)
                        .FontColor(Colors.Grey.Darken1);
                });
            });
            col.Item().PaddingTop(12).LineHorizontal(1.5f).LineColor(Colors.Blue.Darken2);
        });
    }

    private void ComposeClinicReportContent(IContainer container, ClinicReportDto report)
    {
        container.PaddingVertical(15).Column(column =>
        {
            column.Spacing(12);

            // Summary Section
            column.Item().Element(c => ComposeSummarySection(c, report));

            // Statistics Section
            column.Item().Element(c => ComposeStatisticsSection(c, report));

            // Daily Statistics if available
            if (report.ReportData != null && report.ReportData.ContainsKey("dailyStatistics"))
            {
                column.Item().Element(c => ComposeDailyStatisticsSection(c, report));
            }
        });
    }

    private void ComposeSummarySection(IContainer container, ClinicReportDto report)
    {
        container.Background(Colors.Grey.Lighten4).Padding(10).Column(col =>
        {
            col.Item().Text("Thông tin báo cáo").FontSize(14).Bold().FontColor(Colors.Blue.Darken2);
            col.Item().PaddingTop(5).Row(row =>
            {
                row.RelativeItem().Text($"Loại báo cáo: {report.ReportType}");
                row.RelativeItem().Text($"Thời gian: {report.PeriodStart:yyyy-MM-dd} - {report.PeriodEnd:yyyy-MM-dd}");
            });
        });
    }

    private void ComposeStatisticsSection(IContainer container, ClinicReportDto report)
    {
        container.Column(col =>
        {
            col.Item().Text("Thống kê tổng hợp").FontSize(14).Bold().FontColor(Colors.Blue.Darken2);
            col.Item().PaddingTop(5).Row(row =>
            {
                row.RelativeItem().Background(Colors.Blue.Lighten5).Padding(15).Column(c =>
                {
                    c.Item().AlignCenter().Text("Tổng số bệnh nhân").FontSize(10);
                    c.Item().AlignCenter().Text(report.TotalPatients.ToString()).FontSize(20).Bold();
                });
                row.ConstantItem(10);
                row.RelativeItem().Background(Colors.Green.Lighten5).Padding(15).Column(c =>
                {
                    c.Item().AlignCenter().Text("Tổng số phân tích").FontSize(10);
                    c.Item().AlignCenter().Text(report.TotalAnalyses.ToString()).FontSize(20).Bold();
                });
                row.ConstantItem(10);
                row.RelativeItem().Background(Colors.Red.Lighten5).Padding(15).Column(c =>
                {
                    c.Item().AlignCenter().Text("Rủi ro cao").FontSize(10);
                    c.Item().AlignCenter().Text(report.HighRiskCount.ToString()).FontSize(20).Bold();
                });
            });
            col.Item().PaddingTop(10).Row(row =>
            {
                row.RelativeItem().Background(Colors.Orange.Lighten5).Padding(15).Column(c =>
                {
                    c.Item().AlignCenter().Text("Rủi ro trung bình").FontSize(10);
                    c.Item().AlignCenter().Text(report.MediumRiskCount.ToString()).FontSize(20).Bold();
                });
                row.ConstantItem(10);
                row.RelativeItem().Background(Colors.Green.Lighten5).Padding(15).Column(c =>
                {
                    c.Item().AlignCenter().Text("Rủi ro thấp").FontSize(10);
                    c.Item().AlignCenter().Text(report.LowRiskCount.ToString()).FontSize(20).Bold();
                });
            });
        });
    }

    private void ComposeDailyStatisticsSection(IContainer container, ClinicReportDto report)
    {
        container.Column(col =>
        {
            col.Item().Text("Thống kê theo ngày").FontSize(14).Bold().FontColor(Colors.Blue.Darken2);
            col.Item().PaddingTop(5).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(2);
                    columns.RelativeColumn(1);
                    columns.RelativeColumn(1);
                });

                table.Header(header =>
                {
                    header.Cell().Background(Colors.Blue.Darken2).Padding(8)
                        .Text("Ngày").FontColor(Colors.White).Bold();
                    header.Cell().Background(Colors.Blue.Darken2).Padding(8)
                        .Text("Số lượng").FontColor(Colors.White).Bold();
                    header.Cell().Background(Colors.Blue.Darken2).Padding(8)
                        .Text("Rủi ro cao").FontColor(Colors.White).Bold();
                });

                if (report.ReportData != null && report.ReportData["dailyStatistics"] is JsonElement dailyStatsElement &&
                    dailyStatsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var dayStat in dailyStatsElement.EnumerateArray())
                    {
                        table.Cell().Padding(5).Text(dayStat.GetProperty("date").GetString() ?? "N/A");
                        table.Cell().Padding(5).Text(dayStat.GetProperty("count").GetInt32().ToString());
                        table.Cell().Padding(5).Text(dayStat.GetProperty("highRiskCount").GetInt32().ToString());
                    }
                }
            });
        });
    }

    private void ComposeClinicReportFooter(IContainer container)
    {
        container.Column(col =>
        {
            col.Item().LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
            col.Item().PaddingTop(5).Row(row =>
            {
                row.RelativeItem().Text("Báo cáo được tạo bởi AURA Retinal Screening System").FontSize(7).FontColor(Colors.Grey.Medium);
                row.ConstantItem(100).AlignRight().Text(x =>
                {
                    x.Span("Trang ").FontSize(8);
                    x.CurrentPageNumber().FontSize(8);
                    x.Span(" / ").FontSize(8);
                    x.TotalPages().FontSize(8);
                });
            });
        });
    }

    private async Task<string> UploadClinicReportFileAsync(byte[] fileBytes, string fileName, string contentType)
    {
        if (_cloudinary == null)
        {
            _logger?.LogWarning("Cloudinary not configured, returning placeholder URL");
            return $"https://storage.aura-health.com/clinic-reports/{fileName}";
        }

        try
        {
            using var stream = new MemoryStream(fileBytes);
            var uploadParams = new RawUploadParams
            {
                File = new FileDescription(fileName, stream),
                Folder = "aura/clinic-reports",
                PublicId = Path.GetFileNameWithoutExtension(fileName)
            };

            var uploadResult = await _cloudinary.UploadAsync(uploadParams);

            if (uploadResult.StatusCode == System.Net.HttpStatusCode.OK)
            {
                _logger?.LogInformation("Successfully uploaded clinic report to Cloudinary: {Url}", uploadResult.SecureUrl);
                return uploadResult.SecureUrl.ToString();
            }

            throw new InvalidOperationException($"Failed to upload file to Cloudinary: {uploadResult.Error?.Message}");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error uploading clinic report file to Cloudinary");
            throw;
        }
    }

    private async Task UpdateReportFileUrlAsync(string reportId, string fileUrl)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                UPDATE clinic_reports
                SET ReportFileUrl = @FileUrl
                WHERE Id = @ReportId";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("FileUrl", fileUrl);
            command.Parameters.AddWithValue("ReportId", reportId);

            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error updating report file URL for report {ReportId}", reportId);
            throw;
        }
    }

    #endregion
}
