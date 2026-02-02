using Aura.Application.DTOs.UsageTracking;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Aura.Application.Services.UsageTracking;

/// <summary>
/// FR-27: Image Analysis and Package Usage Tracking System
/// Tracks image counts, analysis counts, and package usage for clinics and users
/// </summary>
public class UsageTrackingService : IUsageTrackingService
{
    private readonly string _connectionString;
    private readonly ILogger<UsageTrackingService>? _logger;

    public UsageTrackingService(
        IConfiguration configuration,
        ILogger<UsageTrackingService>? logger = null)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Database connection string not configured");
        _logger = logger;
    }

    public async Task<ClinicUsageStatisticsDto> GetClinicUsageStatisticsAsync(
        string clinicId, 
        DateTime? startDate = null, 
        DateTime? endDate = null)
    {
        try
        {
            var start = startDate?.Date ?? DateTime.UtcNow.AddDays(-30).Date;
            var end = endDate?.Date ?? DateTime.UtcNow.Date;

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Get clinic name
            var clinicName = await GetClinicNameAsync(connection, clinicId);

            var usageStats = await GetUsageStatisticsAsync(connection, userId: null, clinicId: clinicId, startDate: start, endDate: end);
            var imageAnalysisTracking = await GetImageAnalysisTrackingAsync(connection, userId: null, clinicId: clinicId, startDate: start, endDate: end);

            return new ClinicUsageStatisticsDto
            {
                ClinicId = clinicId,
                ClinicName = clinicName,
                UsageStatistics = usageStats,
                ImageAnalysisTracking = imageAnalysisTracking,
                GeneratedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting clinic usage statistics for clinic: {ClinicId}", clinicId);
            throw;
        }
    }

    public async Task<UserUsageStatisticsDto> GetUserUsageStatisticsAsync(
        string userId, 
        DateTime? startDate = null, 
        DateTime? endDate = null)
    {
        try
        {
            var start = startDate?.Date ?? DateTime.UtcNow.AddDays(-30).Date;
            var end = endDate?.Date ?? DateTime.UtcNow.Date;

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Get user name
            var userName = await GetUserNameAsync(connection, userId);

            var usageStats = await GetUsageStatisticsAsync(connection, userId: userId, clinicId: null, startDate: start, endDate: end);
            var imageAnalysisTracking = await GetImageAnalysisTrackingAsync(connection, userId: userId, clinicId: null, startDate: start, endDate: end);

            return new UserUsageStatisticsDto
            {
                UserId = userId,
                UserName = userName,
                UsageStatistics = usageStats,
                ImageAnalysisTracking = imageAnalysisTracking,
                GeneratedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting user usage statistics for user: {UserId}", userId);
            throw;
        }
    }

    public async Task<List<PackageUsageDto>> GetClinicPackageUsageAsync(string clinicId)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT 
                    up.Id, up.PackageId, sp.PackageName, sp.PackageType,
                    sp.NumberOfAnalyses, up.RemainingAnalyses,
                    (sp.NumberOfAnalyses - up.RemainingAnalyses) as UsedAnalyses,
                    up.PurchasedAt, up.ExpiresAt, up.IsActive,
                    CASE WHEN up.ExpiresAt IS NOT NULL AND up.ExpiresAt < CURRENT_TIMESTAMP THEN true ELSE false END as IsExpired
                FROM user_packages up
                INNER JOIN service_packages sp ON up.PackageId = sp.Id
                WHERE up.ClinicId = @ClinicId
                    AND COALESCE(up.IsDeleted, false) = false
                    AND COALESCE(sp.IsDeleted, false) = false
                ORDER BY up.PurchasedAt DESC";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("ClinicId", clinicId);

            var packages = new List<PackageUsageDto>();

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var totalAnalyses = reader.GetInt32(4);
                var remainingAnalyses = reader.GetInt32(5);
                var usedAnalyses = reader.GetInt32(6);
                var usagePercentage = totalAnalyses > 0 
                    ? (decimal)usedAnalyses / totalAnalyses * 100 
                    : 0;

                packages.Add(new PackageUsageDto
                {
                    PackageId = reader.GetString(1),
                    PackageName = reader.IsDBNull(2) ? "Unknown" : reader.GetString(2),
                    PackageType = reader.IsDBNull(3) ? "Unknown" : reader.GetString(3),
                    TotalAnalyses = totalAnalyses,
                    RemainingAnalyses = remainingAnalyses,
                    UsedAnalyses = usedAnalyses,
                    UsagePercentage = usagePercentage,
                    PurchasedAt = reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                    ExpiresAt = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                    IsActive = reader.GetBoolean(9),
                    IsExpired = reader.GetBoolean(10)
                });
            }

            return packages;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting clinic package usage for clinic: {ClinicId}", clinicId);
            return new List<PackageUsageDto>();
        }
    }

    public async Task<List<PackageUsageDto>> GetUserPackageUsageAsync(string userId)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT 
                    up.Id, up.PackageId, sp.PackageName, sp.PackageType,
                    sp.NumberOfAnalyses, up.RemainingAnalyses,
                    (sp.NumberOfAnalyses - up.RemainingAnalyses) as UsedAnalyses,
                    up.PurchasedAt, up.ExpiresAt, up.IsActive,
                    CASE WHEN up.ExpiresAt IS NOT NULL AND up.ExpiresAt < CURRENT_TIMESTAMP THEN true ELSE false END as IsExpired
                FROM user_packages up
                INNER JOIN service_packages sp ON up.PackageId = sp.Id
                WHERE up.UserId = @UserId
                    AND COALESCE(up.IsDeleted, false) = false
                    AND COALESCE(sp.IsDeleted, false) = false
                ORDER BY up.PurchasedAt DESC";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("UserId", userId);

            var packages = new List<PackageUsageDto>();

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var totalAnalyses = reader.GetInt32(4);
                var remainingAnalyses = reader.GetInt32(5);
                var usedAnalyses = reader.GetInt32(6);
                var usagePercentage = totalAnalyses > 0 
                    ? (decimal)usedAnalyses / totalAnalyses * 100 
                    : 0;

                packages.Add(new PackageUsageDto
                {
                    PackageId = reader.GetString(1),
                    PackageName = reader.IsDBNull(2) ? "Unknown" : reader.GetString(2),
                    PackageType = reader.IsDBNull(3) ? "Unknown" : reader.GetString(3),
                    TotalAnalyses = totalAnalyses,
                    RemainingAnalyses = remainingAnalyses,
                    UsedAnalyses = usedAnalyses,
                    UsagePercentage = usagePercentage,
                    PurchasedAt = reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                    ExpiresAt = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                    IsActive = reader.GetBoolean(9),
                    IsExpired = reader.GetBoolean(10)
                });
            }

            return packages;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting user package usage for user: {UserId}", userId);
            return new List<PackageUsageDto>();
        }
    }

    public async Task TrackImageUploadAsync(string userId, string? clinicId = null)
    {
        // Image upload is already tracked in retinal_images table
        // This method can be extended for additional tracking if needed
        _logger?.LogDebug("Image upload tracked for user: {UserId}, clinic: {ClinicId}", userId, clinicId);
        await Task.CompletedTask;
    }

    public async Task TrackAnalysisCompletionAsync(string userId, string? clinicId = null, bool success = true)
    {
        // Analysis completion is already tracked in analysis_results table
        // Package credits are already decremented in AnalysisService
        // This method can be extended for additional tracking if needed
        _logger?.LogDebug("Analysis completion tracked for user: {UserId}, clinic: {ClinicId}, success: {Success}", 
            userId, clinicId, success);
        await Task.CompletedTask;
    }

    // Private helper methods
    private async Task<string> GetClinicNameAsync(NpgsqlConnection connection, string clinicId)
    {
        var sql = "SELECT ClinicName FROM clinics WHERE Id = @ClinicId AND COALESCE(IsDeleted, false) = false";
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("ClinicId", clinicId);
        var result = await command.ExecuteScalarAsync();
        return result?.ToString() ?? "Unknown Clinic";
    }

    private async Task<string?> GetUserNameAsync(NpgsqlConnection connection, string userId)
    {
        var sql = "SELECT FirstName || ' ' || LastName FROM users WHERE Id = @UserId AND COALESCE(IsDeleted, false) = false";
        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("UserId", userId);
        var result = await command.ExecuteScalarAsync();
        return result?.ToString();
    }

    private async Task<UsageStatisticsDto> GetUsageStatisticsAsync(
        NpgsqlConnection connection, 
        string? userId = null, 
        string? clinicId = null,
        DateTime startDate = default,
        DateTime endDate = default)
    {
        var stats = new UsageStatisticsDto();

        // Build WHERE clause
        var whereClause = "WHERE COALESCE(ri.IsDeleted, false) = false";
        if (userId != null)
        {
            whereClause += " AND ri.UserId = @UserId";
        }
        if (clinicId != null)
        {
            whereClause += " AND ri.ClinicId = @ClinicId";
        }
        if (startDate != default && endDate != default)
        {
            whereClause += " AND ri.UploadedAt >= @StartDate AND ri.UploadedAt <= @EndDate";
        }

        // Image counts
        var imageSql = $@"
            SELECT 
                COUNT(*) as TotalImages,
                COUNT(*) FILTER (WHERE ri.UploadStatus = 'Processed') as ProcessedImages,
                COUNT(*) FILTER (WHERE ri.UploadStatus IN ('Uploaded', 'Processing')) as PendingImages,
                COUNT(*) FILTER (WHERE ri.UploadStatus = 'Failed') as FailedImages
            FROM retinal_images ri
            {whereClause}";

        using var imageCommand = new NpgsqlCommand(imageSql, connection);
        if (userId != null) imageCommand.Parameters.AddWithValue("UserId", userId);
        if (clinicId != null) imageCommand.Parameters.AddWithValue("ClinicId", clinicId);
        if (startDate != default) imageCommand.Parameters.AddWithValue("StartDate", startDate);
        if (endDate != default) imageCommand.Parameters.AddWithValue("EndDate", endDate.AddDays(1).AddSeconds(-1));

        using var imageReader = await imageCommand.ExecuteReaderAsync();
        if (await imageReader.ReadAsync())
        {
            stats.TotalImages = imageReader.GetInt32(0);
            stats.ProcessedImages = imageReader.GetInt32(1);
            stats.PendingImages = imageReader.GetInt32(2);
            stats.FailedImages = imageReader.GetInt32(3);
        }
        await imageReader.CloseAsync();

        // Analysis counts (userId: include analyses where patient owns image ri.UserId, or did analysis ar.UserId)
        var analysisFromJoin = userId != null
            ? "FROM analysis_results ar INNER JOIN retinal_images ri ON ar.ImageId = ri.Id AND COALESCE(ri.IsDeleted, false) = false"
            : "FROM analysis_results ar";
        var analysisWhereClause = "WHERE COALESCE(ar.IsDeleted, false) = false";
        if (userId != null)
        {
            analysisWhereClause += " AND (ar.UserId = @UserId OR ri.UserId = @UserId)";
        }
        if (clinicId != null)
        {
            analysisWhereClause += @" AND ar.ImageId IN (
                SELECT Id FROM retinal_images WHERE ClinicId = @ClinicId
            )";
        }
        if (startDate != default && endDate != default)
        {
            analysisWhereClause += " AND ar.AnalysisCompletedAt >= @StartDate AND ar.AnalysisCompletedAt <= @EndDate";
        }

        var analysisSql = $@"
            SELECT 
                COUNT(*) as TotalAnalyses,
                COUNT(*) FILTER (WHERE ar.AnalysisStatus = 'Completed') as CompletedAnalyses,
                COUNT(*) FILTER (WHERE ar.AnalysisStatus = 'Processing') as ProcessingAnalyses,
                COUNT(*) FILTER (WHERE ar.AnalysisStatus = 'Failed') as FailedAnalyses
            {analysisFromJoin}
            {analysisWhereClause}";

        using var analysisCommand = new NpgsqlCommand(analysisSql, connection);
        if (userId != null) analysisCommand.Parameters.AddWithValue("UserId", userId);
        if (clinicId != null) analysisCommand.Parameters.AddWithValue("ClinicId", clinicId);
        if (startDate != default) analysisCommand.Parameters.AddWithValue("StartDate", startDate);
        if (endDate != default) analysisCommand.Parameters.AddWithValue("EndDate", endDate.AddDays(1).AddSeconds(-1));

        using var analysisReader = await analysisCommand.ExecuteReaderAsync();
        if (await analysisReader.ReadAsync())
        {
            stats.TotalAnalyses = analysisReader.GetInt32(0);
            stats.CompletedAnalyses = analysisReader.GetInt32(1);
            stats.ProcessingAnalyses = analysisReader.GetInt32(2);
            stats.FailedAnalyses = analysisReader.GetInt32(3);
        }
        await analysisReader.CloseAsync();

        // Package statistics
        var packageWhereClause = "WHERE COALESCE(up.IsDeleted, false) = false";
        if (userId != null)
        {
            packageWhereClause += " AND up.UserId = @UserId";
        }
        if (clinicId != null)
        {
            packageWhereClause += " AND up.ClinicId = @ClinicId";
        }

        var packageSql = $@"
            SELECT 
                COUNT(*) as TotalPackages,
                COUNT(*) FILTER (WHERE up.IsActive = true AND (up.ExpiresAt IS NULL OR up.ExpiresAt > CURRENT_TIMESTAMP)) as ActivePackages,
                COUNT(*) FILTER (WHERE up.ExpiresAt IS NOT NULL AND up.ExpiresAt <= CURRENT_TIMESTAMP) as ExpiredPackages,
                COALESCE(SUM(up.RemainingAnalyses), 0) as TotalRemainingAnalyses,
                COALESCE(SUM(sp.NumberOfAnalyses - up.RemainingAnalyses), 0) as TotalUsedAnalyses
            FROM user_packages up
            INNER JOIN service_packages sp ON up.PackageId = sp.Id
            {packageWhereClause}";

        using var packageCommand = new NpgsqlCommand(packageSql, connection);
        if (userId != null) packageCommand.Parameters.AddWithValue("UserId", userId);
        if (clinicId != null) packageCommand.Parameters.AddWithValue("ClinicId", clinicId);

        using var packageReader = await packageCommand.ExecuteReaderAsync();
        if (await packageReader.ReadAsync())
        {
            stats.TotalPackages = packageReader.GetInt32(0);
            stats.ActivePackages = packageReader.GetInt32(1);
            stats.ExpiredPackages = packageReader.GetInt32(2);
            stats.TotalRemainingAnalyses = packageReader.GetInt32(3);
            stats.TotalUsedAnalyses = packageReader.GetInt32(4);
        }
        await packageReader.CloseAsync();

        // Daily usage
        stats.DailyUsage = await GetDailyUsageAsync(connection, userId, clinicId, startDate, endDate);

        // Package usage details
        if (clinicId != null)
        {
            stats.PackageUsage = await GetClinicPackageUsageAsync(clinicId);
        }
        else if (userId != null)
        {
            stats.PackageUsage = await GetUserPackageUsageAsync(userId);
        }

        return stats;
    }

    private async Task<List<DailyUsageDto>> GetDailyUsageAsync(
        NpgsqlConnection connection,
        string? userId = null,
        string? clinicId = null,
        DateTime startDate = default,
        DateTime endDate = default)
    {
        var dailyUsage = new List<DailyUsageDto>();

        if (startDate == default || endDate == default)
        {
            return dailyUsage;
        }

        var whereClause = "WHERE DATE(ri.UploadedAt) >= @StartDate AND DATE(ri.UploadedAt) <= @EndDate";
        if (userId != null)
        {
            whereClause += " AND ri.UserId = @UserId";
        }
        if (clinicId != null)
        {
            whereClause += " AND ri.ClinicId = @ClinicId";
        }

        var sql = $@"
            SELECT 
                DATE(ri.UploadedAt) as Date,
                COUNT(DISTINCT ri.Id) as ImageCount,
                COUNT(DISTINCT ar.Id) as AnalysisCount,
                COUNT(DISTINCT ar.Id) as UsedCredits
            FROM retinal_images ri
            LEFT JOIN analysis_results ar ON ar.ImageId = ri.Id AND ar.AnalysisStatus = 'Completed'
            {whereClause}
            GROUP BY DATE(ri.UploadedAt)
            ORDER BY DATE(ri.UploadedAt)";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("StartDate", startDate);
        command.Parameters.AddWithValue("EndDate", endDate);
        if (userId != null) command.Parameters.AddWithValue("UserId", userId);
        if (clinicId != null) command.Parameters.AddWithValue("ClinicId", clinicId);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            dailyUsage.Add(new DailyUsageDto
            {
                Date = reader.GetDateTime(0),
                ImageCount = reader.GetInt32(1),
                AnalysisCount = reader.GetInt32(2),
                UsedCredits = reader.GetInt32(3)
            });
        }

        return dailyUsage;
    }

    private async Task<ImageAnalysisTrackingDto> GetImageAnalysisTrackingAsync(
        NpgsqlConnection connection,
        string? userId = null,
        string? clinicId = null,
        DateTime startDate = default,
        DateTime endDate = default)
    {
        var tracking = new ImageAnalysisTrackingDto();

        var whereClause = "WHERE COALESCE(ri.IsDeleted, false) = false";
        if (userId != null)
        {
            whereClause += " AND ri.UserId = @UserId";
        }
        if (clinicId != null)
        {
            whereClause += " AND ri.ClinicId = @ClinicId";
        }
        if (startDate != default && endDate != default)
        {
            whereClause += " AND ri.UploadedAt >= @StartDate AND ri.UploadedAt <= @EndDate";
        }

        // Image counts by type and status
        var imageSql = $@"
            SELECT 
                COUNT(*) as TotalImages,
                COUNT(*) FILTER (WHERE ri.ImageType = 'Fundus') as FundusCount,
                COUNT(*) FILTER (WHERE ri.ImageType = 'OCT') as OctCount,
                COUNT(*) FILTER (WHERE ri.UploadStatus = 'Processed') as ProcessedCount
            FROM retinal_images ri
            {whereClause}";

        using var imageCommand = new NpgsqlCommand(imageSql, connection);
        if (userId != null) imageCommand.Parameters.AddWithValue("UserId", userId);
        if (clinicId != null) imageCommand.Parameters.AddWithValue("ClinicId", clinicId);
        if (startDate != default) imageCommand.Parameters.AddWithValue("StartDate", startDate);
        if (endDate != default) imageCommand.Parameters.AddWithValue("EndDate", endDate.AddDays(1).AddSeconds(-1));

        using var imageReader = await imageCommand.ExecuteReaderAsync();
        if (await imageReader.ReadAsync())
        {
            tracking.TotalImages = imageReader.GetInt32(0);
            tracking.ImagesByType = imageReader.GetInt32(1) + imageReader.GetInt32(2); // Fundus + OCT
            tracking.ImagesByStatus = imageReader.GetInt32(3);
        }
        await imageReader.CloseAsync();

        // Image count by date
        if (startDate != default && endDate != default)
        {
            tracking.ImageCountByDate = await GetImageCountByDateAsync(connection, userId, clinicId, startDate, endDate);
            tracking.AnalysisCountByDate = await GetAnalysisCountByDateAsync(connection, userId, clinicId, startDate, endDate);
        }

        return tracking;
    }

    private async Task<List<ImageCountByDateDto>> GetImageCountByDateAsync(
        NpgsqlConnection connection,
        string? userId = null,
        string? clinicId = null,
        DateTime startDate = default,
        DateTime endDate = default)
    {
        var counts = new List<ImageCountByDateDto>();

        var whereClause = "WHERE DATE(ri.UploadedAt) >= @StartDate AND DATE(ri.UploadedAt) <= @EndDate";
        if (userId != null)
        {
            whereClause += " AND ri.UserId = @UserId";
        }
        if (clinicId != null)
        {
            whereClause += " AND ri.ClinicId = @ClinicId";
        }

        var sql = $@"
            SELECT 
                DATE(ri.UploadedAt) as Date,
                COUNT(*) as Count,
                COUNT(*) FILTER (WHERE ri.ImageType = 'Fundus') as FundusCount,
                COUNT(*) FILTER (WHERE ri.ImageType = 'OCT') as OctCount
            FROM retinal_images ri
            {whereClause}
            GROUP BY DATE(ri.UploadedAt)
            ORDER BY DATE(ri.UploadedAt)";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("StartDate", startDate);
        command.Parameters.AddWithValue("EndDate", endDate);
        if (userId != null) command.Parameters.AddWithValue("UserId", userId);
        if (clinicId != null) command.Parameters.AddWithValue("ClinicId", clinicId);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            counts.Add(new ImageCountByDateDto
            {
                Date = reader.GetDateTime(0),
                Count = reader.GetInt32(1),
                FundusCount = reader.GetInt32(2),
                OctCount = reader.GetInt32(3)
            });
        }

        return counts;
    }

    private async Task<List<AnalysisCountByDateDto>> GetAnalysisCountByDateAsync(
        NpgsqlConnection connection,
        string? userId = null,
        string? clinicId = null,
        DateTime startDate = default,
        DateTime endDate = default)
    {
        var counts = new List<AnalysisCountByDateDto>();

        var fromClause = userId != null
            ? "FROM analysis_results ar INNER JOIN retinal_images ri ON ar.ImageId = ri.Id AND COALESCE(ri.IsDeleted, false) = false"
            : "FROM analysis_results ar";
        var whereClause = "WHERE DATE(ar.AnalysisCompletedAt) >= @StartDate AND DATE(ar.AnalysisCompletedAt) <= @EndDate";
        if (userId != null)
        {
            whereClause += " AND (ar.UserId = @UserId OR ri.UserId = @UserId)";
        }
        if (clinicId != null)
        {
            whereClause += @" AND ar.ImageId IN (
                SELECT Id FROM retinal_images WHERE ClinicId = @ClinicId
            )";
        }

        var sql = $@"
            SELECT 
                DATE(ar.AnalysisCompletedAt) as Date,
                COUNT(*) as Count,
                COUNT(*) FILTER (WHERE ar.AnalysisStatus = 'Completed') as CompletedCount,
                COUNT(*) FILTER (WHERE ar.AnalysisStatus = 'Failed') as FailedCount
            {fromClause}
            {whereClause}
            GROUP BY DATE(ar.AnalysisCompletedAt)
            ORDER BY DATE(ar.AnalysisCompletedAt)";

        using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("StartDate", startDate);
        command.Parameters.AddWithValue("EndDate", endDate);
        if (userId != null) command.Parameters.AddWithValue("UserId", userId);
        if (clinicId != null) command.Parameters.AddWithValue("ClinicId", clinicId);

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            counts.Add(new AnalysisCountByDateDto
            {
                Date = reader.GetDateTime(0),
                Count = reader.GetInt32(1),
                CompletedCount = reader.GetInt32(2),
                FailedCount = reader.GetInt32(3)
            });
        }

        return counts;
    }
}
