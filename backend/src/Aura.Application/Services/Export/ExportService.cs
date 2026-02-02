using Aura.Application.DTOs.Analysis;
using Aura.Application.DTOs.Export;
using Aura.Application.Services.Analysis;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Aura.Application.Services.Export;

/// <summary>
/// Service xử lý export báo cáo phân tích (FR-7)
/// Hỗ trợ export PDF, CSV, JSON với đa ngôn ngữ (vi/en)
/// </summary>
public class ExportService : IExportService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ExportService>? _logger;
    private readonly string _connectionString;
    private readonly IAnalysisService _analysisService;
    private readonly Cloudinary? _cloudinary;

    // Constants
    private const int DefaultExpirationDays = 30;
    private const string CloudinaryFolder = "aura/exported-reports";

    public ExportService(
        IConfiguration configuration,
        IAnalysisService analysisService,
        ILogger<ExportService>? logger = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _analysisService = analysisService ?? throw new ArgumentNullException(nameof(analysisService));
        _logger = logger;
        
        _connectionString = configuration.GetConnectionString("DefaultConnection") 
            ?? throw new InvalidOperationException("Database connection string 'DefaultConnection' not found");

        // Initialize Cloudinary
        _cloudinary = CreateCloudinaryClient();

        // Set QuestPDF license (free for Community use)
        QuestPDF.Settings.License = LicenseType.Community;
    }

    private Cloudinary? CreateCloudinaryClient()
    {
        var cloudName = _configuration["Cloudinary:CloudName"];
        var apiKey = _configuration["Cloudinary:ApiKey"];
        var apiSecret = _configuration["Cloudinary:ApiSecret"];

        if (!string.IsNullOrWhiteSpace(cloudName) && 
            !string.IsNullOrWhiteSpace(apiKey) && 
            !string.IsNullOrWhiteSpace(apiSecret))
        {
            var account = new Account(cloudName, apiKey, apiSecret);
            _logger?.LogInformation("Cloudinary initialized successfully for export service");
            return new Cloudinary(account);
        }
        
        _logger?.LogWarning("Cloudinary credentials not configured. Exports will use placeholder URLs.");
        return null;
    }

    #region Single Export

    public async Task<ExportResponseDto> ExportToPdfAsync(
        string analysisResultId, 
        string userId, 
        string requestedByType, 
        bool includeImages = true,
        bool includePatientInfo = true,
        string language = "vi")
    {
        ValidateExportRequest(analysisResultId, userId, requestedByType);

        try
        {
            _logger?.LogInformation("Starting PDF export for analysis {AnalysisId} by user {UserId}, Type: {Type}", 
                analysisResultId, userId, requestedByType);

            // Get analysis result (doctors can access any analysis)
            var analysisResult = await GetAnalysisResultOrThrowAsync(analysisResultId, userId, requestedByType);
            
            // Get user info - for doctors/clinic, get the patient's info from the analysis
            UserInfoForExport? userInfo = null;
            if (includePatientInfo)
            {
                // If doctor/admin/clinic, get patient info from analysis (ri.UserId = patient)
                if (requestedByType == RequesterTypes.Doctor || requestedByType == RequesterTypes.Admin || requestedByType == RequesterTypes.Clinic)
                {
                    var patientUserId = await GetPatientUserIdForAnalysisAsync(analysisResultId);
                    if (!string.IsNullOrEmpty(patientUserId))
                    {
                        userInfo = await GetUserInfoAsync(patientUserId);
                    }
                }
                else
                {
                    userInfo = await GetUserInfoAsync(userId);
                }
            }

            // Download images if needed (before PDF generation)
            byte[]? annotatedImageBytes = null;
            byte[]? heatmapImageBytes = null;
            
            if (includeImages)
            {
                // Get AI Core base URL from configuration
                var aiCoreBaseUrl = _configuration["AICore:BaseUrl"] ?? "http://aicore:8000";
                
                if (!string.IsNullOrEmpty(analysisResult.AnnotatedImageUrl))
                {
                    try
                    {
                        // Resolve relative URLs to full URLs
                        var imageUrl = ResolveImageUrl(analysisResult.AnnotatedImageUrl, aiCoreBaseUrl);
                        
                        using var httpClient = new HttpClient();
                        httpClient.Timeout = TimeSpan.FromSeconds(20);
                        annotatedImageBytes = await httpClient.GetByteArrayAsync(imageUrl);
                        _logger?.LogInformation("Downloaded annotated image for PDF export: {Size} bytes from {Url}", 
                            annotatedImageBytes.Length, imageUrl);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Could not download annotated image for PDF: {Url}", 
                            analysisResult.AnnotatedImageUrl);
                    }
                }
                
                if (!string.IsNullOrEmpty(analysisResult.HeatmapUrl))
                {
                    try
                    {
                        // Resolve relative URLs to full URLs
                        var imageUrl = ResolveImageUrl(analysisResult.HeatmapUrl, aiCoreBaseUrl);
                        
                        using var httpClient = new HttpClient();
                        httpClient.Timeout = TimeSpan.FromSeconds(20);
                        heatmapImageBytes = await httpClient.GetByteArrayAsync(imageUrl);
                        _logger?.LogInformation("Downloaded heatmap image for PDF export: {Size} bytes from {Url}", 
                            heatmapImageBytes.Length, imageUrl);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Could not download heatmap image for PDF: {Url}", 
                            analysisResult.HeatmapUrl);
                    }
                }
            }

            // Generate PDF
            var pdfBytes = GeneratePdf(analysisResult, userInfo, includeImages, language, annotatedImageBytes, heatmapImageBytes);
            var fileName = GenerateFileName(analysisResultId, "pdf");

            // Upload to cloud storage
            var fileUrl = await UploadExportFileAsync(pdfBytes, fileName, "PDF");

            // Save to database
            var exportId = await SaveExportRecordAsync(
                analysisResultId, "PDF", fileName, fileUrl, pdfBytes.Length, userId, requestedByType);

            _logger?.LogInformation("PDF export completed successfully. ExportId: {ExportId}, Size: {Size} bytes", 
                exportId, pdfBytes.Length);

            return CreateExportResponse(exportId, analysisResultId, "PDF", fileName, fileUrl, pdfBytes.Length);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error exporting to PDF for analysis {AnalysisId}", analysisResultId);
            throw new InvalidOperationException($"Failed to export PDF: {ex.Message}", ex);
        }
    }

    public async Task<ExportResponseDto> ExportToCsvAsync(
        string analysisResultId, 
        string userId, 
        string requestedByType,
        string language = "vi")
    {
        ValidateExportRequest(analysisResultId, userId, requestedByType);

        try
        {
            _logger?.LogInformation("Starting CSV export for analysis {AnalysisId} by user {UserId}, Type: {Type}", 
                analysisResultId, userId, requestedByType);

            var analysisResult = await GetAnalysisResultOrThrowAsync(analysisResultId, userId, requestedByType);
            
            var csvBytes = GenerateCsv(new[] { analysisResult }, language);
            var fileName = GenerateFileName(analysisResultId, "csv");

            var fileUrl = await UploadExportFileAsync(csvBytes, fileName, "CSV");

            var exportId = await SaveExportRecordAsync(
                analysisResultId, "CSV", fileName, fileUrl, csvBytes.Length, userId, requestedByType);

            _logger?.LogInformation("CSV export completed. ExportId: {ExportId}", exportId);

            return CreateExportResponse(exportId, analysisResultId, "CSV", fileName, fileUrl, csvBytes.Length);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error exporting to CSV for analysis {AnalysisId}", analysisResultId);
            throw new InvalidOperationException($"Failed to export CSV: {ex.Message}", ex);
        }
    }

    public async Task<ExportResponseDto> ExportToJsonAsync(
        string analysisResultId, 
        string userId, 
        string requestedByType)
    {
        ValidateExportRequest(analysisResultId, userId, requestedByType);

        try
        {
            _logger?.LogInformation("Starting JSON export for analysis {AnalysisId} by user {UserId}, Type: {Type}", 
                analysisResultId, userId, requestedByType);

            var analysisResult = await GetAnalysisResultOrThrowAsync(analysisResultId, userId, requestedByType);
            
            var jsonOptions = new JsonSerializerOptions 
            { 
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(analysisResult, jsonOptions));
            var fileName = GenerateFileName(analysisResultId, "json");

            var fileUrl = await UploadExportFileAsync(jsonBytes, fileName, "JSON");

            var exportId = await SaveExportRecordAsync(
                analysisResultId, "JSON", fileName, fileUrl, jsonBytes.Length, userId, requestedByType);

            _logger?.LogInformation("JSON export completed. ExportId: {ExportId}", exportId);

            return CreateExportResponse(exportId, analysisResultId, "JSON", fileName, fileUrl, jsonBytes.Length);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error exporting to JSON for analysis {AnalysisId}", analysisResultId);
            throw new InvalidOperationException($"Failed to export JSON: {ex.Message}", ex);
        }
    }

    #endregion

    #region Batch Export

    public async Task<BatchExportResponseDto> ExportBatchToCsvAsync(
        List<string> analysisResultIds,
        string userId,
        string requestedByType,
        string language = "vi")
    {
        if (analysisResultIds == null || analysisResultIds.Count == 0)
            throw new ArgumentException("At least one analysis result ID is required", nameof(analysisResultIds));

        var response = new BatchExportResponseDto
        {
            TotalRequested = analysisResultIds.Count
        };

        var validResults = new List<AnalysisResultDto>();
        
        // Collect all valid results
        foreach (var resultId in analysisResultIds.Distinct())
        {
            try
            {
                var result = await _analysisService.GetAnalysisResultAsync(resultId, userId);
                if (result != null)
                {
                    validResults.Add(result);
                }
                else
                {
                    response.FailedExports.Add(new ExportErrorDto
                    {
                        AnalysisResultId = resultId,
                        ErrorMessage = "Analysis result not found"
                    });
                }
            }
            catch (Exception ex)
            {
                response.FailedExports.Add(new ExportErrorDto
                {
                    AnalysisResultId = resultId,
                    ErrorMessage = ex.Message
                });
            }
        }

        if (validResults.Count > 0)
        {
            try
            {
                var csvBytes = GenerateCsv(validResults, language);
                var batchId = Guid.NewGuid().ToString("N")[..8];
                var fileName = $"batch_export_{batchId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv";

                var fileUrl = await UploadExportFileAsync(csvBytes, fileName, "CSV");

                var exportId = await SaveExportRecordAsync(
                    null, "CSV", fileName, fileUrl, csvBytes.Length, userId, requestedByType);

                response.SuccessfulExports.Add(CreateExportResponse(
                    exportId, null, "CSV", fileName, fileUrl, csvBytes.Length));
                response.SuccessCount = validResults.Count;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error creating batch CSV export");
                foreach (var result in validResults)
                {
                    response.FailedExports.Add(new ExportErrorDto
                    {
                        AnalysisResultId = result.Id,
                        ErrorMessage = "Batch export failed: " + ex.Message
                    });
                }
            }
        }

        response.FailedCount = response.FailedExports.Count;
        return response;
    }

    #endregion

    #region Export History

    public async Task<List<ExportResponseDto>> GetExportHistoryAsync(string userId, int limit = 50, int offset = 0)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("UserId is required", nameof(userId));

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT Id, ResultId, ReportType, FilePath, FileUrl, FileSize, 
                       ExportedAt, ExpiresAt, DownloadCount, RequestedByType
                FROM exported_reports
                WHERE RequestedBy = @UserId AND IsDeleted = false
                ORDER BY ExportedAt DESC
                LIMIT @Limit OFFSET @Offset";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("UserId", userId);
            command.Parameters.AddWithValue("Limit", Math.Min(limit, 100)); // Max 100
            command.Parameters.AddWithValue("Offset", Math.Max(offset, 0));

            var exports = new List<ExportResponseDto>();
            using var reader = await command.ExecuteReaderAsync();
            
            while (await reader.ReadAsync())
            {
                exports.Add(new ExportResponseDto
                {
                    ExportId = reader.GetString(0),
                    AnalysisResultId = reader.IsDBNull(1) ? null : reader.GetString(1),
                    ReportType = reader.GetString(2),
                    FileName = reader.IsDBNull(3) ? string.Empty : Path.GetFileName(reader.GetString(3)),
                    FileUrl = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    FileSize = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                    ExportedAt = reader.GetDateTime(6),
                    ExpiresAt = reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                    DownloadCount = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                    RequestedByType = reader.IsDBNull(9) ? "User" : reader.GetString(9)
                });
            }

            return exports;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting export history for user {UserId}", userId);
            throw;
        }
    }

    public async Task<ExportResponseDto?> GetExportByIdAsync(string exportId, string userId)
    {
        if (string.IsNullOrWhiteSpace(exportId))
            throw new ArgumentException("ExportId is required", nameof(exportId));

        try
        {
            _logger?.LogInformation("Querying export: ExportId={ExportId}, UserId={UserId}", exportId, userId);
            
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT Id, ResultId, ReportType, FilePath, FileUrl, FileSize, 
                       ExportedAt, ExpiresAt, DownloadCount, RequestedByType
                FROM exported_reports
                WHERE Id = @ExportId AND RequestedBy = @UserId AND IsDeleted = false";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("ExportId", exportId);
            command.Parameters.AddWithValue("UserId", userId);

            using var reader = await command.ExecuteReaderAsync();
            
            if (!await reader.ReadAsync())
            {
                _logger?.LogWarning("Export not found in database: ExportId={ExportId}, UserId={UserId}. Checking if export exists without user filter...", exportId, userId);
                
                // Check if export exists at all (for debugging)
                await reader.CloseAsync();
                var checkSql = @"SELECT Id, RequestedBy, IsDeleted FROM exported_reports WHERE Id = @ExportId";
                using var checkCommand = new NpgsqlCommand(checkSql, connection);
                checkCommand.Parameters.AddWithValue("ExportId", exportId);
                using var checkReader = await checkCommand.ExecuteReaderAsync();
                if (await checkReader.ReadAsync())
                {
                    var foundRequestedBy = checkReader.IsDBNull(1) ? "NULL" : checkReader.GetString(1);
                    var foundIsDeleted = checkReader.GetBoolean(2);
                    _logger?.LogWarning("Export exists but: RequestedBy={RequestedBy} (expected {UserId}), IsDeleted={IsDeleted}", 
                        foundRequestedBy, userId, foundIsDeleted);
                }
                else
                {
                    _logger?.LogWarning("Export does not exist in database: ExportId={ExportId}", exportId);
                }
                
                return null;
            }
            
            _logger?.LogInformation("Export found successfully: ExportId={ExportId}", exportId);

            return new ExportResponseDto
            {
                ExportId = reader.GetString(0),
                AnalysisResultId = reader.IsDBNull(1) ? null : reader.GetString(1),
                ReportType = reader.GetString(2),
                FileName = reader.IsDBNull(3) ? string.Empty : Path.GetFileName(reader.GetString(3)),
                FileUrl = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                FileSize = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                ExportedAt = reader.GetDateTime(6),
                ExpiresAt = reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                DownloadCount = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                RequestedByType = reader.IsDBNull(9) ? "User" : reader.GetString(9)
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting export {ExportId}", exportId);
            throw;
        }
    }

    public async Task<bool> IncrementDownloadCountAsync(string exportId, string userId)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                UPDATE exported_reports
                SET DownloadCount = DownloadCount + 1,
                    LastDownloadedAt = @Now
                WHERE Id = @ExportId AND RequestedBy = @UserId AND IsDeleted = false";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("ExportId", exportId);
            command.Parameters.AddWithValue("UserId", userId);
            command.Parameters.AddWithValue("Now", DateTime.UtcNow);

            var rowsAffected = await command.ExecuteNonQueryAsync();
            return rowsAffected > 0;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error incrementing download count for export {ExportId}", exportId);
            return false;
        }
    }

    public async Task<bool> DeleteExportAsync(string exportId, string userId)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                UPDATE exported_reports
                SET IsDeleted = true, 
                    UpdatedDate = @UpdatedDate,
                    UpdatedBy = @UserId
                WHERE Id = @ExportId AND RequestedBy = @UserId AND IsDeleted = false";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("ExportId", exportId);
            command.Parameters.AddWithValue("UserId", userId);
            command.Parameters.AddWithValue("UpdatedDate", DateTime.UtcNow.Date);

            var rowsAffected = await command.ExecuteNonQueryAsync();
            
            if (rowsAffected > 0)
            {
                _logger?.LogInformation("Export {ExportId} deleted by user {UserId}", exportId, userId);
            }
            
            return rowsAffected > 0;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error deleting export {ExportId}", exportId);
            throw;
        }
    }

    public async Task<int> CleanupExpiredExportsAsync()
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                UPDATE exported_reports
                SET IsDeleted = true, 
                    UpdatedDate = @Now
                WHERE ExpiresAt < @Now AND IsDeleted = false";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("Now", DateTime.UtcNow);

            var rowsAffected = await command.ExecuteNonQueryAsync();
            
            if (rowsAffected > 0)
            {
                _logger?.LogInformation("Cleaned up {Count} expired exports", rowsAffected);
            }
            
            return rowsAffected;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error cleaning up expired exports");
            return 0;
        }
    }

    public async Task<byte[]?> DownloadExportFileAsync(string exportId, string userId)
    {
        try
        {
            // Get export details
            var export = await GetExportByIdAsync(exportId, userId);
            if (export == null || string.IsNullOrEmpty(export.FileUrl))
            {
                _logger?.LogWarning("Export not found or file URL is empty: {ExportId}", exportId);
                return null;
            }

            // Try using Cloudinary Admin API first if available
            if (_cloudinary != null)
            {
                try
                {
                    // Extract public_id from Cloudinary URL
                    // Format: https://res.cloudinary.com/{cloud_name}/image/upload/{version}/{folder}/{public_id}.{ext}
                    // Example: https://res.cloudinary.com/dylia0tle/image/upload/v1769536594/aura/exported-reports/aura_report_222fb7e4_20260127_175634.pdf
                    var uri = new Uri(export.FileUrl);
                    var pathParts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    
                    // Find the "upload" index
                    // Path format: /image/upload/v{version}/{folder}/{public_id}.{ext}
                    var uploadIndex = Array.IndexOf(pathParts, "upload");
                    if (uploadIndex >= 0 && uploadIndex + 3 < pathParts.Length)
                    {
                        // Skip version (v{number}), then get folder path and filename
                        // pathParts[uploadIndex + 1] = "v1769536594" (version)
                        // pathParts[uploadIndex + 2] = "aura" (first folder)
                        // pathParts[uploadIndex + 3] = "exported-reports" (second folder)
                        // pathParts[uploadIndex + 4] = "aura_report_222fb7e4_20260127_175634.pdf" (filename)
                        
                        // Build public_id: "aura/exported-reports/aura_report_222fb7e4_20260127_175634" (without extension)
                        var publicIdParts = new List<string>();
                        for (int i = uploadIndex + 2; i < pathParts.Length; i++)
                        {
                            if (i == pathParts.Length - 1)
                            {
                                // Last part is filename, remove extension
                                publicIdParts.Add(Path.GetFileNameWithoutExtension(pathParts[i]));
                            }
                            else
                            {
                                publicIdParts.Add(pathParts[i]);
                            }
                        }
                        
                        var publicId = string.Join("/", publicIdParts);
                        
                        _logger?.LogInformation("Extracted public_id from URL: {PublicId}", publicId);
                        
                        // Use Admin API to download file
                        var getResourceParams = new GetResourceParams(publicId)
                        {
                            ResourceType = ResourceType.Raw // PDF/CSV/JSON are raw files
                        };
                        
                        var getResourceResult = await _cloudinary.GetResourceAsync(getResourceParams);
                        
                        if (getResourceResult.StatusCode == System.Net.HttpStatusCode.OK && 
                            !string.IsNullOrEmpty(getResourceResult.SecureUrl))
                        {
                            // Download from secure URL using HttpClient
                            using var httpClient = new HttpClient();
                            httpClient.Timeout = TimeSpan.FromMinutes(5);
                            httpClient.DefaultRequestHeaders.Add("User-Agent", "AURA-Backend/1.0");
                            
                            var response = await httpClient.GetAsync(getResourceResult.SecureUrl);
                            if (response.IsSuccessStatusCode)
                            {
                                var fileBytes = await response.Content.ReadAsByteArrayAsync();
                                _logger?.LogInformation("Successfully downloaded export file via Admin API: {ExportId}, Size: {Size} bytes", 
                                    exportId, fileBytes.Length);
                                return fileBytes;
                            }
                            else
                            {
                                _logger?.LogWarning("Failed to download from SecureUrl: {StatusCode}, {Url}", 
                                    response.StatusCode, getResourceResult.SecureUrl);
                            }
                        }
                        else
                        {
                            _logger?.LogWarning("GetResourceAsync failed: {StatusCode}, Error: {Error}", 
                                getResourceResult.StatusCode, getResourceResult.Error?.Message);
                        }
                    }
                    else
                    {
                        _logger?.LogWarning("Could not parse Cloudinary URL to extract public_id: {Url}", export.FileUrl);
                    }
                }
                catch (Exception cloudinaryEx)
                {
                    _logger?.LogWarning(cloudinaryEx, "Failed to download via Cloudinary Admin API, falling back to direct URL: {Url}", export.FileUrl);
                }
            }

            // Fallback: Try direct download from URL (may work if file is public)
            using var fallbackHttpClient = new HttpClient();
            fallbackHttpClient.Timeout = TimeSpan.FromMinutes(5);
            
            // Add User-Agent header to avoid blocking
            fallbackHttpClient.DefaultRequestHeaders.Add("User-Agent", "AURA-Backend/1.0");
            
            var fallbackResponse = await fallbackHttpClient.GetAsync(export.FileUrl);
            
            if (!fallbackResponse.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Failed to download file from Cloudinary URL: {StatusCode}, {Url}. " +
                                    "Will attempt to regenerate export on-the-fly to avoid Cloudinary auth issues.",
                    fallbackResponse.StatusCode, export.FileUrl);

                // FINAL FALLBACK (MOST RELIABLE): regenerate file bytes instead of proxying Cloudinary
                var regenerated = await RegenerateExportBytesAsync(export, userId);
                if (regenerated != null && regenerated.Length > 0)
                {
                    _logger?.LogInformation("Regenerated export bytes successfully: ExportId={ExportId}, Size={Size} bytes",
                        exportId, regenerated.Length);
                    return regenerated;
                }

                _logger?.LogError("Could not download from Cloudinary AND could not regenerate export. ExportId={ExportId}", exportId);
                return null;
            }

            var fallbackFileBytes = await fallbackResponse.Content.ReadAsByteArrayAsync();
            _logger?.LogInformation("Successfully downloaded export file: {ExportId}, Size: {Size} bytes", 
                exportId, fallbackFileBytes.Length);
            
            return fallbackFileBytes;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error downloading export file: {ExportId}", exportId);
            return null;
        }
    }

    #endregion

    #region Private Methods - Validation

    private static void ValidateExportRequest(string analysisResultId, string userId, string requestedByType)
    {
        if (string.IsNullOrWhiteSpace(analysisResultId))
            throw new ArgumentException("AnalysisResultId is required", nameof(analysisResultId));
        
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("UserId is required", nameof(userId));
        
        if (!RequesterTypes.IsValid(requestedByType))
            throw new ArgumentException($"Invalid requester type: {requestedByType}", nameof(requestedByType));
    }

    private async Task<AnalysisResultDto> GetAnalysisResultOrThrowAsync(string analysisResultId, string userId, string? requestedByType = null)
    {
        AnalysisResultDto? result = null;
        
        // For doctors/admins, allow access without strict user ownership check
        if (requestedByType == RequesterTypes.Doctor || requestedByType == RequesterTypes.Admin)
        {
            result = await _analysisService.GetAnalysisResultByIdAsync(analysisResultId);
        }
        else if (requestedByType == RequesterTypes.Clinic)
        {
            // For clinic, check ri.ClinicId = clinicId (handled by GetAnalysisResultAsync)
            result = await _analysisService.GetAnalysisResultAsync(analysisResultId, userId);
        }
        else
        {
            // For regular users, check ownership
            result = await _analysisService.GetAnalysisResultAsync(analysisResultId, userId);
        }
        
        if (result == null)
        {
            throw new InvalidOperationException($"Analysis result '{analysisResultId}' not found or access denied");
        }
        
        return result;
    }

    #endregion

    #region Private Methods - PDF Generation

    private byte[] GeneratePdf(AnalysisResultDto result, UserInfoForExport? userInfo, bool includeImages, string language, byte[]? annotatedImageBytes = null, byte[]? heatmapImageBytes = null)
    {
        var labels = GetLabels(language);
        
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.5f, Unit.Centimetre);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontSize(11));

                // Header with logo and title
                page.Header().Element(c => ComposeHeader(c, labels));

                // Main content
                page.Content().Element(c => ComposeContent(c, result, userInfo, labels, includeImages, annotatedImageBytes, heatmapImageBytes));

                // Footer
                page.Footer().Element(c => ComposeFooter(c, labels));
            });
        });

        return document.GeneratePdf();
    }

    private void ComposeHeader(IContainer container, ExportLabels labels)
    {
        container.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Column(col2 =>
                {
                    col2.Item()
                        .Text("AURA AI")
                        .FontSize(32)
                        .Bold()
                        .FontColor(Colors.Blue.Darken3);
                        
                    col2.Item().PaddingTop(2)
                        .Text(labels.SystemName)
                        .FontSize(11)
                        .FontColor(Colors.Grey.Darken2);
                        
                    col2.Item().PaddingTop(3)
                        .Text("Clinical Decision Support System")
                        .FontSize(9)
                        .Italic()
                        .FontColor(Colors.Grey.Medium);
                });
                
                row.ConstantItem(180).AlignRight().Column(col2 =>
                {
                    col2.Item()
                        .Text(labels.ReportTitle)
                        .FontSize(16)
                        .Bold()
                        .FontColor(Colors.Blue.Darken2);
                        
                    col2.Item().PaddingTop(3)
                        .Text($"{labels.ExportDate}: {DateTime.UtcNow:dd/MM/yyyy HH:mm} UTC")
                        .FontSize(9)
                        .FontColor(Colors.Grey.Darken1);
                        
                    col2.Item().PaddingTop(2)
                        .Text($"Version: 2.0.0")
                        .FontSize(8)
                        .FontColor(Colors.Grey.Medium);
                });
            });
            
            col.Item().PaddingTop(12).LineHorizontal(1.5f).LineColor(Colors.Blue.Darken2);
        });
    }

    private void ComposeContent(IContainer container, AnalysisResultDto result, UserInfoForExport? userInfo, ExportLabels labels, bool includeImages, byte[]? annotatedImageBytes = null, byte[]? heatmapImageBytes = null)
    {
        container.PaddingVertical(15).Column(column =>
        {
            column.Spacing(12);

            // Analysis Info Section
            column.Item().Element(c => ComposeAnalysisInfo(c, result, labels));
            
            // Patient Info Section (if available)
            if (userInfo != null)
            {
                column.Item().Element(c => ComposePatientInfo(c, userInfo, labels));
            }

            // Risk Assessment Section
            column.Item().Element(c => ComposeRiskAssessment(c, result, labels));

            // Detailed Findings Section
            column.Item().Element(c => ComposeDetailedFindings(c, result, labels));

            // Vascular Abnormalities Section
            column.Item().Element(c => ComposeVascularFindings(c, result, labels));

            // Images Section (Heatmap & Annotated Image) - FR-4
            if (includeImages)
            {
                var hasAnnotated = annotatedImageBytes != null && annotatedImageBytes.Length > 0;
                var hasHeatmap = heatmapImageBytes != null && heatmapImageBytes.Length > 0;
                
                if (hasAnnotated || hasHeatmap)
                {
                    column.Item().Element(c => ComposeImagesSection(c, result, labels, annotatedImageBytes, heatmapImageBytes));
                }
                else
                {
                    // Log warning nếu không có images nhưng includeImages = true
                    _logger?.LogWarning("PDF export: includeImages=true nhưng không có images nào được download. AnnotatedImageUrl: {AnnotatedUrl}, HeatmapUrl: {HeatmapUrl}", 
                        result.AnnotatedImageUrl ?? "null", result.HeatmapUrl ?? "null");
                }
            }

            // Recommendations Section
            if (!string.IsNullOrEmpty(result.Recommendations) || !string.IsNullOrEmpty(result.HealthWarnings))
            {
                column.Item().Element(c => ComposeRecommendations(c, result, labels));
            }
        });
    }

    private void ComposeAnalysisInfo(IContainer container, AnalysisResultDto result, ExportLabels labels)
    {
        container.Background(Colors.Grey.Lighten4).Padding(10).Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Text($"{labels.AnalysisId}: {result.Id}").FontSize(10);
                col.Item().Text($"{labels.ImageId}: {result.ImageId}").FontSize(10);
                col.Item().Text($"{labels.AnalysisDate}: {result.AnalysisCompletedAt?.ToString("dd/MM/yyyy HH:mm:ss") ?? "N/A"}").FontSize(10);
            });
            
            row.RelativeItem().Column(col =>
            {
                col.Item().Text($"{labels.ProcessingTime}: {result.ProcessingTimeSeconds ?? 0}s").FontSize(10);
                col.Item().Text($"{labels.Status}: {result.AnalysisStatus}").FontSize(10);
                if (result.AiConfidenceScore.HasValue)
                {
                    col.Item().Text($"{labels.AiConfidence}: {result.AiConfidenceScore.Value:F1}%").FontSize(10).Bold();
                }
            });
        });
    }

    private void ComposePatientInfo(IContainer container, UserInfoForExport userInfo, ExportLabels labels)
    {
        container.Column(col =>
        {
            col.Item().Text(labels.PatientInfo).FontSize(14).Bold().FontColor(Colors.Blue.Darken2);
            col.Item().PaddingTop(5).Background(Colors.Blue.Lighten5).Padding(10).Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text($"{labels.PatientName}: {userInfo.FullName}").FontSize(10);
                    c.Item().Text($"{labels.Email}: {userInfo.Email}").FontSize(10);
                });
                row.RelativeItem().Column(c =>
                {
                    if (!string.IsNullOrEmpty(userInfo.Phone))
                        c.Item().Text($"{labels.Phone}: {userInfo.Phone}").FontSize(10);
                    if (userInfo.DateOfBirth.HasValue)
                        c.Item().Text($"{labels.DateOfBirth}: {userInfo.DateOfBirth:dd/MM/yyyy}").FontSize(10);
                });
            });
        });
    }

    private void ComposeRiskAssessment(IContainer container, AnalysisResultDto result, ExportLabels labels)
    {
        var riskColor = GetRiskColor(result.OverallRiskLevel);
        
        container.Column(col =>
        {
            col.Item().Text(labels.RiskAssessment).FontSize(14).Bold().FontColor(Colors.Blue.Darken2);
            
            col.Item().PaddingTop(5).Row(row =>
            {
                row.RelativeItem().Background(riskColor).Padding(15).Column(c =>
                {
                    c.Item().AlignCenter().Text(labels.OverallRisk).FontSize(10).FontColor(Colors.White);
                    c.Item().AlignCenter().Text(result.OverallRiskLevel ?? "N/A").FontSize(20).Bold().FontColor(Colors.White);
                });
                
                row.ConstantItem(20);
                
                row.RelativeItem().Background(Colors.Grey.Lighten3).Padding(15).Column(c =>
                {
                    c.Item().AlignCenter().Text(labels.RiskScore).FontSize(10);
                    c.Item().AlignCenter().Text($"{result.RiskScore?.ToString("F1") ?? "N/A"}/100").FontSize(20).Bold();
                });
                
                row.ConstantItem(20);
                
                row.RelativeItem().Background(Colors.Grey.Lighten3).Padding(15).Column(c =>
                {
                    c.Item().AlignCenter().Text(labels.AiConfidence).FontSize(10);
                    c.Item().AlignCenter().Text($"{result.AiConfidenceScore?.ToString("F1") ?? "N/A"}%").FontSize(20).Bold();
                });
            });
        });
    }

    private void ComposeDetailedFindings(IContainer container, AnalysisResultDto result, ExportLabels labels)
    {
        container.Column(col =>
        {
            col.Item().Text(labels.DetailedFindings).FontSize(14).Bold().FontColor(Colors.Blue.Darken2);
            
            col.Item().PaddingTop(5).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(2);
                    columns.RelativeColumn(1);
                    columns.RelativeColumn(1);
                });
                
                // Header
                table.Header(header =>
                {
                    header.Cell().Background(Colors.Blue.Darken2).Padding(8)
                        .Text(labels.Condition).FontColor(Colors.White).Bold().FontSize(11);
                    header.Cell().Background(Colors.Blue.Darken2).Padding(8)
                        .Text(labels.RiskLevel).FontColor(Colors.White).Bold().FontSize(11);
                    header.Cell().Background(Colors.Blue.Darken2).Padding(8)
                        .Text(labels.Score).FontColor(Colors.White).Bold().FontSize(11);
                });
                
                // Hypertension
                AddTableRow(table, labels.Hypertension, result.HypertensionRisk, result.HypertensionScore);
                
                // Diabetes
                AddTableRow(table, labels.Diabetes, result.DiabetesRisk, result.DiabetesScore);
                
                // Stroke
                AddTableRow(table, labels.Stroke, result.StrokeRisk, result.StrokeScore);
                
                // Diabetic Retinopathy
                if (result.DiabeticRetinopathyDetected)
                {
                    AddTableRow(table, labels.DiabeticRetinopathy, 
                        result.DiabeticRetinopathySeverity ?? labels.Detected, null);
                }
            });
        });
    }

    private void AddTableRow(TableDescriptor table, string condition, string? risk, decimal? score)
    {
        var bgColor = Colors.White;
        var riskColor = GetRiskColorForText(risk);
        
        table.Cell().Background(bgColor).BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Padding(8)
            .Text(condition).FontSize(10);
        table.Cell().Background(bgColor).BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Padding(8)
            .Text(risk ?? "N/A").FontSize(10).FontColor(riskColor);
        table.Cell().Background(bgColor).BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Padding(8)
            .Text(score?.ToString("F1") ?? "N/A").FontSize(10);
    }
    
    private static string GetRiskColorForText(string? riskLevel)
    {
        return riskLevel?.ToLower() switch
        {
            "low" => Colors.Green.Darken2,
            "medium" => Colors.Orange.Darken2,
            "high" => Colors.Red.Medium,
            "critical" => Colors.Red.Darken3,
            _ => Colors.Grey.Darken2
        };
    }

    private void ComposeVascularFindings(IContainer container, AnalysisResultDto result, ExportLabels labels)
    {
        container.Column(col =>
        {
            col.Item().Text(labels.VascularFindings).FontSize(14).Bold().FontColor(Colors.Blue.Darken2);
            
            col.Item().PaddingTop(5).Background(Colors.Grey.Lighten4).Padding(10).Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text($"• {labels.VesselTortuosity}: {result.VesselTortuosity?.ToString("F2") ?? "N/A"}").FontSize(10);
                    c.Item().Text($"• {labels.VesselWidthVariation}: {result.VesselWidthVariation?.ToString("F2") ?? "N/A"}").FontSize(10);
                });
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text($"• {labels.MicroaneurysmsCount}: {result.MicroaneurysmsCount}").FontSize(10);
                    c.Item().Text($"• {labels.HemorrhagesDetected}: {(result.HemorrhagesDetected ? labels.Yes : labels.No)}").FontSize(10);
                    c.Item().Text($"• {labels.ExudatesDetected}: {(result.ExudatesDetected ? labels.Yes : labels.No)}").FontSize(10);
                });
            });
        });
    }

    private void ComposeImagesSection(IContainer container, AnalysisResultDto result, ExportLabels labels, byte[]? annotatedImageBytes, byte[]? heatmapImageBytes)
    {
        container.Column(col =>
        {
            col.Item().Text("HÌNH ẢNH PHÂN TÍCH").FontSize(14).Bold().FontColor(Colors.Blue.Darken2);
            
            col.Item().PaddingTop(10).Column(imageCol =>
            {
                // Annotated Image - Full width, much larger size
                if (annotatedImageBytes != null && annotatedImageBytes.Length > 0)
                {
                    try
                    {
                        imageCol.Item().Column(c =>
                        {
                            c.Item().PaddingBottom(10).Text("Ảnh đã chú thích").FontSize(13).Bold().FontColor(Colors.Blue.Darken2);
                            // Container với full width và chiều cao lớn hơn đáng kể
                            c.Item().Border(2f).BorderColor(Colors.Grey.Darken1)
                                .Padding(8)
                                .Background(Colors.White)
                                .Height(550) // Chiều cao cố định lớn hơn nhiều
                                .AlignCenter()
                                .Image(annotatedImageBytes)
                                .FitArea(); // Fit image vào container với tỷ lệ phù hợp
                        });
                        _logger?.LogInformation("PDF: Đã embed annotated image ({Size} bytes)", annotatedImageBytes.Length);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "PDF: Lỗi khi embed annotated image vào PDF");
                        // Hiển thị placeholder text nếu không thể embed image
                        imageCol.Item().Column(c =>
                        {
                            c.Item().PaddingBottom(10).Text("Ảnh đã chú thích").FontSize(13).Bold();
                            c.Item().Background(Colors.Grey.Lighten3).Padding(30)
                                .Text("Không thể tải hình ảnh").FontSize(11).FontColor(Colors.Grey.Darken2);
                        });
                    }
                }
                
                // Spacing between images
                if (annotatedImageBytes != null && annotatedImageBytes.Length > 0 && 
                    heatmapImageBytes != null && heatmapImageBytes.Length > 0)
                {
                    imageCol.Item().Height(20); // Khoảng cách lớn hơn giữa 2 hình
                }
                
                // Heatmap Image - Full width, much larger size
                if (heatmapImageBytes != null && heatmapImageBytes.Length > 0)
                {
                    try
                    {
                        imageCol.Item().Column(c =>
                        {
                            c.Item().PaddingBottom(10).Text("Heatmap").FontSize(13).Bold().FontColor(Colors.Blue.Darken2);
                            // Container với full width và chiều cao lớn hơn đáng kể
                            c.Item().Border(2f).BorderColor(Colors.Grey.Darken1)
                                .Padding(8)
                                .Background(Colors.White)
                                .Height(550) // Chiều cao cố định lớn hơn nhiều
                                .AlignCenter()
                                .Image(heatmapImageBytes)
                                .FitArea(); // Fit image vào container với tỷ lệ phù hợp
                        });
                        _logger?.LogInformation("PDF: Đã embed heatmap image ({Size} bytes)", heatmapImageBytes.Length);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "PDF: Lỗi khi embed heatmap image vào PDF");
                        // Hiển thị placeholder text nếu không thể embed image
                        imageCol.Item().Column(c =>
                        {
                            c.Item().PaddingBottom(10).Text("Heatmap").FontSize(13).Bold();
                            c.Item().Background(Colors.Grey.Lighten3).Padding(30)
                                .Text("Không thể tải hình ảnh").FontSize(11).FontColor(Colors.Grey.Darken2);
                        });
                    }
                }
            });
        });
    }

    private void ComposeRecommendations(IContainer container, AnalysisResultDto result, ExportLabels labels)
    {
        container.Column(col =>
        {
            if (!string.IsNullOrEmpty(result.Recommendations))
            {
                col.Item().Text(labels.Recommendations).FontSize(14).Bold().FontColor(Colors.Blue.Darken2);
                
                // Split recommendations by newlines and format nicely
                var recommendations = result.Recommendations.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                col.Item().PaddingTop(5).Background(Colors.Blue.Lighten5).Padding(12).Column(recCol =>
                {
                    foreach (var rec in recommendations)
                    {
                        var trimmedRec = rec.Trim();
                        if (!string.IsNullOrEmpty(trimmedRec))
                        {
                            // Loại bỏ ký tự bullet và các ký tự đặc biệt có thể gây lỗi hiển thị "?"
                            // Giữ nguyên emoji và các ký tự Unicode khác
                            var displayText = trimmedRec;
                            
                            // Loại bỏ các ký tự bullet đơn giản ở đầu chuỗi
                            var charsToRemove = new[] { '•', '?', '-', '*', '·', '\u2022', '\u25CF', '\u25E6' };
                            displayText = displayText.TrimStart(charsToRemove);
                            
                            // Loại bỏ khoảng trắng thừa sau khi trim
                            displayText = displayText.TrimStart();
                            
                            // Thêm padding và hiển thị với font rõ ràng
                            recCol.Item().PaddingBottom(4).PaddingLeft(2)
                                .Text(displayText).FontSize(10).FontColor(Colors.Grey.Darken3);
                        }
                    }
                });
            }
            
            if (!string.IsNullOrEmpty(result.HealthWarnings))
            {
                col.Item().PaddingTop(10).Text(labels.HealthWarnings).FontSize(14).Bold().FontColor(Colors.Red.Darken2);
                
                var warnings = result.HealthWarnings.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                col.Item().PaddingTop(5).Background(Colors.Red.Lighten5).Padding(12).Column(warnCol =>
                {
                    foreach (var warning in warnings)
                    {
                        var trimmedWarning = warning.Trim();
                        if (!string.IsNullOrEmpty(trimmedWarning))
                        {
                            // Loại bỏ ký tự bullet và các ký tự đặc biệt có thể gây lỗi hiển thị "?"
                            var charsToRemove = new[] { '•', '?', '-', '*', '·', '\u2022', '\u25CF', '\u25E6' };
                            var displayWarning = trimmedWarning.TrimStart(charsToRemove).TrimStart();
                            
                            // Thêm emoji warning (trong string literal thì emoji hoạt động tốt)
                            warnCol.Item().PaddingBottom(4).PaddingLeft(2)
                                .Text($"⚠ {displayWarning}").FontSize(10).FontColor(Colors.Red.Darken3);
                        }
                    }
                });
            }
        });
    }

    private void ComposeFooter(IContainer container, ExportLabels labels)
    {
        container.Column(col =>
        {
            col.Item().LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
            col.Item().PaddingTop(5).Row(row =>
            {
                row.RelativeItem().Text(labels.Disclaimer).FontSize(7).FontColor(Colors.Grey.Medium);
                row.ConstantItem(100).AlignRight().Text(x =>
                {
                    x.Span(labels.Page).FontSize(8);
                    x.CurrentPageNumber().FontSize(8);
                    x.Span(" / ").FontSize(8);
                    x.TotalPages().FontSize(8);
                });
            });
        });
    }

    private static string GetRiskColor(string? riskLevel)
    {
        return riskLevel?.ToLower() switch
        {
            "low" => Colors.Green.Darken1,
            "medium" => Colors.Orange.Darken1,
            "high" => Colors.Red.Medium,
            "critical" => Colors.Red.Darken3,
            _ => Colors.Grey.Medium
        };
    }

    #endregion

    #region Private Methods - CSV Generation

    private byte[] GenerateCsv(IEnumerable<AnalysisResultDto> results, string language)
    {
        var labels = GetLabels(language);
        
        using var memoryStream = new MemoryStream();
        using var writer = new StreamWriter(memoryStream, new UTF8Encoding(true)); // UTF-8 with BOM for Excel
        using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true
        });

        // Write headers
        var headers = new[]
        {
            labels.AnalysisId, labels.ImageId, labels.Status, labels.OverallRisk, labels.RiskScore,
            labels.Hypertension, "Hypertension Score", labels.Diabetes, "Diabetes Score",
            labels.DiabeticRetinopathy, "DR Severity", labels.Stroke, "Stroke Score",
            labels.VesselTortuosity, labels.VesselWidthVariation, labels.MicroaneurysmsCount,
            labels.HemorrhagesDetected, labels.ExudatesDetected, labels.AiConfidence,
            "Annotated Image URL", "Heatmap URL",
            labels.AnalysisDate, labels.ProcessingTime, labels.Recommendations, labels.HealthWarnings
        };

        foreach (var header in headers)
        {
            csv.WriteField(header);
        }
        csv.NextRecord();

        // Write data rows
        foreach (var result in results)
        {
            csv.WriteField(result.Id);
            csv.WriteField(result.ImageId);
            csv.WriteField(result.AnalysisStatus);
            csv.WriteField(result.OverallRiskLevel ?? "N/A");
            csv.WriteField(result.RiskScore?.ToString("F2") ?? "N/A");
            csv.WriteField(result.HypertensionRisk ?? "N/A");
            csv.WriteField(result.HypertensionScore?.ToString("F2") ?? "N/A");
            csv.WriteField(result.DiabetesRisk ?? "N/A");
            csv.WriteField(result.DiabetesScore?.ToString("F2") ?? "N/A");
            csv.WriteField(result.DiabeticRetinopathyDetected ? labels.Yes : labels.No);
            csv.WriteField(result.DiabeticRetinopathySeverity ?? "N/A");
            csv.WriteField(result.StrokeRisk ?? "N/A");
            csv.WriteField(result.StrokeScore?.ToString("F2") ?? "N/A");
            csv.WriteField(result.VesselTortuosity?.ToString("F2") ?? "N/A");
            csv.WriteField(result.VesselWidthVariation?.ToString("F2") ?? "N/A");
            csv.WriteField(result.MicroaneurysmsCount);
            csv.WriteField(result.HemorrhagesDetected ? labels.Yes : labels.No);
            csv.WriteField(result.ExudatesDetected ? labels.Yes : labels.No);
            csv.WriteField(result.AiConfidenceScore?.ToString("F2") ?? "N/A");
            csv.WriteField(result.AnnotatedImageUrl ?? "");
            csv.WriteField(result.HeatmapUrl ?? "");
            csv.WriteField(result.AnalysisCompletedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A");
            csv.WriteField(result.ProcessingTimeSeconds?.ToString() ?? "N/A");
            // Escape newlines in recommendations and warnings for CSV
            csv.WriteField((result.Recommendations ?? "").Replace("\n", " | "));
            csv.WriteField((result.HealthWarnings ?? "").Replace("\n", " | "));
            csv.NextRecord();
        }

        writer.Flush();
        return memoryStream.ToArray();
    }

    #endregion

    #region Private Methods - File Upload

    private async Task<string> UploadExportFileAsync(byte[] fileBytes, string fileName, string reportType)
    {
        if (_cloudinary == null)
        {
            _logger?.LogWarning("Cloudinary not configured, returning placeholder URL");
            return $"https://storage.aura-health.com/exports/{fileName}";
        }

        try
        {
            using var stream = new MemoryStream(fileBytes);

            // RawUploadParams tự động dùng ResourceType.Raw cho các file không phải image
            // Không cần set ResourceType vì nó là read-only và được tự động detect từ file extension
            var uploadParams = new RawUploadParams
            {
                File = new FileDescription(fileName, stream),
                Folder = CloudinaryFolder,
                PublicId = Path.GetFileNameWithoutExtension(fileName)
            };

            var uploadResult = await _cloudinary.UploadAsync(uploadParams);

            if (uploadResult.StatusCode == System.Net.HttpStatusCode.OK)
            {
                _logger?.LogInformation("Successfully uploaded {ReportType} to Cloudinary: {Url}", 
                    reportType, uploadResult.SecureUrl);
                return uploadResult.SecureUrl.ToString();
            }

            _logger?.LogError("Cloudinary upload failed: {Error}", uploadResult.Error?.Message);
            throw new InvalidOperationException($"Cloud upload failed: {uploadResult.Error?.Message}");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _logger?.LogError(ex, "Error uploading file to cloud storage");
            throw new InvalidOperationException("Failed to upload export file to cloud storage", ex);
        }
    }

    #endregion

    #region Private Methods - Database

    private async Task<string> SaveExportRecordAsync(
        string? resultId, 
        string reportType, 
        string fileName,
        string fileUrl, 
        long fileSize, 
        string requestedBy, 
        string requestedByType)
    {
        var exportId = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;
        var expiresAt = now.AddDays(DefaultExpirationDays);

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                INSERT INTO exported_reports 
                (Id, ResultId, ReportType, FilePath, FileUrl, FileSize, RequestedBy, RequestedByType, 
                 ExportedAt, ExpiresAt, DownloadCount, CreatedDate, CreatedBy, IsDeleted)
                VALUES 
                (@Id, @ResultId, @ReportType, @FilePath, @FileUrl, @FileSize, @RequestedBy, @RequestedByType,
                 @ExportedAt, @ExpiresAt, 0, @CreatedDate, @CreatedBy, false)";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("Id", exportId);
            command.Parameters.AddWithValue("ResultId", resultId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("ReportType", reportType);
            command.Parameters.AddWithValue("FilePath", fileName);
            command.Parameters.AddWithValue("FileUrl", fileUrl);
            command.Parameters.AddWithValue("FileSize", fileSize);
            command.Parameters.AddWithValue("RequestedBy", requestedBy);
            command.Parameters.AddWithValue("RequestedByType", requestedByType);
            command.Parameters.AddWithValue("ExportedAt", now);
            command.Parameters.AddWithValue("ExpiresAt", expiresAt);
            command.Parameters.AddWithValue("CreatedDate", now.Date);
            command.Parameters.AddWithValue("CreatedBy", requestedBy);

            await command.ExecuteNonQueryAsync();
            
            return exportId;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error saving export record to database");
            throw new InvalidOperationException("Failed to save export record", ex);
        }
    }

    private async Task<UserInfoForExport?> GetUserInfoAsync(string userId)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT FirstName, LastName, Email, Phone, Dob 
                FROM users 
                WHERE Id = @UserId AND IsActive = true";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("UserId", userId);

            using var reader = await command.ExecuteReaderAsync();
            
            if (!await reader.ReadAsync())
                return null;

            var firstName = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var lastName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var fullName = $"{firstName} {lastName}".Trim();
            if (string.IsNullOrWhiteSpace(fullName))
                fullName = "N/A";

            return new UserInfoForExport
            {
                FullName = fullName,
                Email = reader.IsDBNull(2) ? "N/A" : reader.GetString(2),
                Phone = reader.IsDBNull(3) ? null : reader.GetString(3),
                DateOfBirth = reader.IsDBNull(4) ? null : reader.GetDateTime(4)
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not retrieve user info for export");
            return null;
        }
    }

    /// <summary>
    /// Fallback “chắc ăn”: tái tạo nội dung export trực tiếp từ DB (không phụ thuộc Cloudinary).
    /// Dùng khi Cloudinary bật chế độ private/authenticated khiến backend không thể GET file qua URL.
    /// </summary>
    private async Task<byte[]?> RegenerateExportBytesAsync(ExportResponseDto export, string userId)
    {
        try
        {
            if (export.AnalysisResultId == null)
            {
                // Batch export hoặc export không gắn với 1 analysis -> không thể regenerate chắc chắn
                _logger?.LogWarning("Cannot regenerate export without AnalysisResultId. ExportId={ExportId}", export.ExportId);
                return null;
            }

            // Default language: Vietnamese (phù hợp yêu cầu dự án)
            var language = "vi";

            // Lấy lại kết quả phân tích - sử dụng RequestedByType từ export record
            var requestedByType = export.RequestedByType ?? RequesterTypes.User;
            var analysisResult = await GetAnalysisResultOrThrowAsync(export.AnalysisResultId, userId, requestedByType);

            // Lấy thông tin bệnh nhân (nếu doctor/admin thì lấy thông tin owner của analysis)
            string userIdForInfo = userId;
            if (requestedByType == RequesterTypes.Doctor || requestedByType == RequesterTypes.Admin)
            {
                var ownerUserId = await GetAnalysisOwnerUserIdAsync(export.AnalysisResultId);
                if (!string.IsNullOrEmpty(ownerUserId))
                {
                    userIdForInfo = ownerUserId;
                }
            }
            var userInfo = await GetUserInfoAsync(userIdForInfo);

            var reportType = (export.ReportType ?? string.Empty).Trim().ToUpperInvariant();
            switch (reportType)
            {
                case "PDF":
                {
                    // Khi regenerate PDF, cố gắng nhúng ảnh nếu có
                    var includeImages = true;
                    byte[]? annotatedImageBytes = null;
                    byte[]? heatmapImageBytes = null;

                    try
                    {
                        var aiCoreBaseUrl = _configuration["AICore:BaseUrl"] ?? "http://aicore:8000";

                        if (!string.IsNullOrEmpty(analysisResult.AnnotatedImageUrl))
                        {
                            var imageUrl = ResolveImageUrl(analysisResult.AnnotatedImageUrl, aiCoreBaseUrl);
                            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                            annotatedImageBytes = await http.GetByteArrayAsync(imageUrl);
                        }

                        if (!string.IsNullOrEmpty(analysisResult.HeatmapUrl))
                        {
                            var imageUrl = ResolveImageUrl(analysisResult.HeatmapUrl, aiCoreBaseUrl);
                            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                            heatmapImageBytes = await http.GetByteArrayAsync(imageUrl);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Regenerate PDF: could not download embedded images. ExportId={ExportId}", export.ExportId);
                    }

                    return GeneratePdf(analysisResult, userInfo, includeImages, language, annotatedImageBytes, heatmapImageBytes);
                }
                case "CSV":
                    return GenerateCsv(new[] { analysisResult }, language);
                case "JSON":
                {
                    var jsonOptions = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    };
                    return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(analysisResult, jsonOptions));
                }
                default:
                    _logger?.LogWarning("Unsupported report type for regeneration: {ReportType}, ExportId={ExportId}", export.ReportType, export.ExportId);
                    return null;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to regenerate export bytes. ExportId={ExportId}", export.ExportId);
            return null;
        }
    }

    #endregion

    #region Private Methods - Helpers

    private static string GenerateFileName(string analysisId, string extension)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var shortId = analysisId.Length > 8 ? analysisId[..8] : analysisId;
        return $"aura_report_{shortId}_{timestamp}.{extension}";
    }

    private static ExportResponseDto CreateExportResponse(
        string exportId, string? analysisResultId, string reportType, 
        string fileName, string fileUrl, long fileSize)
    {
        return new ExportResponseDto
        {
            ExportId = exportId,
            AnalysisResultId = analysisResultId,
            ReportType = reportType,
            FileName = fileName,
            FileUrl = fileUrl,
            FileSize = fileSize,
            ExportedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(DefaultExpirationDays),
            DownloadCount = 0
        };
    }

    private static ExportLabels GetLabels(string language)
    {
        return language.ToLower() == "en" ? ExportLabels.English : ExportLabels.Vietnamese;
    }

    /// <summary>
    /// Resolve image URL từ relative path sang full URL
    /// </summary>
    private static string ResolveImageUrl(string imageUrl, string aiCoreBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
            return imageUrl;

        // Nếu đã là full URL (http/https), trả về nguyên
        if (imageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
            imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return imageUrl;
        }

        // Nếu là relative path (bắt đầu bằng /), thêm base URL
        if (imageUrl.StartsWith("/"))
        {
            // Loại bỏ trailing slash từ baseUrl nếu có
            var baseUrl = aiCoreBaseUrl.TrimEnd('/');
            return $"{baseUrl}{imageUrl}";
        }

        // Nếu không có / ở đầu, thêm /
        return $"{aiCoreBaseUrl.TrimEnd('/')}/{imageUrl}";
    }

    #endregion

    #region Helper Classes

    private class UserInfoForExport
    {
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? Phone { get; set; }
        public DateTime? DateOfBirth { get; set; }
    }

    private class ExportLabels
    {
        // System
        public string SystemName { get; set; } = string.Empty;
        public string ReportTitle { get; set; } = string.Empty;
        public string ExportDate { get; set; } = string.Empty;
        public string Page { get; set; } = string.Empty;
        public string Disclaimer { get; set; } = string.Empty;

        // Analysis Info
        public string AnalysisId { get; set; } = string.Empty;
        public string ImageId { get; set; } = string.Empty;
        public string AnalysisDate { get; set; } = string.Empty;
        public string ProcessingTime { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;

        // Patient Info
        public string PatientInfo { get; set; } = string.Empty;
        public string PatientName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string DateOfBirth { get; set; } = string.Empty;

        // Risk Assessment
        public string RiskAssessment { get; set; } = string.Empty;
        public string OverallRisk { get; set; } = string.Empty;
        public string RiskLevel { get; set; } = string.Empty;
        public string RiskScore { get; set; } = string.Empty;
        public string AiConfidence { get; set; } = string.Empty;

        // Detailed Findings
        public string DetailedFindings { get; set; } = string.Empty;
        public string Condition { get; set; } = string.Empty;
        public string Score { get; set; } = string.Empty;
        public string Hypertension { get; set; } = string.Empty;
        public string Diabetes { get; set; } = string.Empty;
        public string Stroke { get; set; } = string.Empty;
        public string DiabeticRetinopathy { get; set; } = string.Empty;
        public string Detected { get; set; } = string.Empty;

        // Vascular Findings
        public string VascularFindings { get; set; } = string.Empty;
        public string VesselTortuosity { get; set; } = string.Empty;
        public string VesselWidthVariation { get; set; } = string.Empty;
        public string MicroaneurysmsCount { get; set; } = string.Empty;
        public string HemorrhagesDetected { get; set; } = string.Empty;
        public string ExudatesDetected { get; set; } = string.Empty;

        // Recommendations
        public string Recommendations { get; set; } = string.Empty;
        public string HealthWarnings { get; set; } = string.Empty;

        // Common
        public string Yes { get; set; } = string.Empty;
        public string No { get; set; } = string.Empty;

        public static ExportLabels Vietnamese => new()
        {
            SystemName = "Hệ thống Sàng lọc Sức khỏe Võng mạc",
            ReportTitle = "BÁO CÁO PHÂN TÍCH",
            ExportDate = "Ngày xuất",
            Page = "Trang ",
            Disclaimer = "Báo cáo này được tạo tự động bởi hệ thống AURA. Kết quả chỉ mang tính tham khảo và không thay thế chẩn đoán của bác sĩ chuyên khoa.",
            
            AnalysisId = "Mã phân tích",
            ImageId = "Mã hình ảnh",
            AnalysisDate = "Ngày phân tích",
            ProcessingTime = "Thời gian xử lý",
            Status = "Trạng thái",
            
            PatientInfo = "THÔNG TIN BỆNH NHÂN",
            PatientName = "Họ tên",
            Email = "Email",
            Phone = "Điện thoại",
            DateOfBirth = "Ngày sinh",
            
            RiskAssessment = "ĐÁNH GIÁ RỦI RO",
            OverallRisk = "Rủi ro tổng thể",
            RiskLevel = "Mức độ",
            RiskScore = "Điểm rủi ro",
            AiConfidence = "Độ tin cậy AI",
            
            DetailedFindings = "CHI TIẾT KẾT QUẢ",
            Condition = "Tình trạng",
            Score = "Điểm",
            Hypertension = "Tăng huyết áp",
            Diabetes = "Tiểu đường",
            Stroke = "Đột quỵ",
            DiabeticRetinopathy = "Bệnh võng mạc ĐTĐ",
            Detected = "Đã phát hiện",
            
            VascularFindings = "BẤT THƯỜNG MẠCH MÁU",
            VesselTortuosity = "Độ xoắn mạch máu",
            VesselWidthVariation = "Biến thiên độ rộng",
            MicroaneurysmsCount = "Số phình vi mạch",
            HemorrhagesDetected = "Xuất huyết",
            ExudatesDetected = "Xuất tiết",
            
            Recommendations = "KHUYẾN NGHỊ",
            HealthWarnings = "CẢNH BÁO SỨC KHỎE",
            
            Yes = "Có",
            No = "Không"
        };

        public static ExportLabels English => new()
        {
            SystemName = "Retinal Health Screening System",
            ReportTitle = "ANALYSIS REPORT",
            ExportDate = "Export Date",
            Page = "Page ",
            Disclaimer = "This report is automatically generated by AURA system. Results are for reference only and do not replace diagnosis by medical specialists.",
            
            AnalysisId = "Analysis ID",
            ImageId = "Image ID",
            AnalysisDate = "Analysis Date",
            ProcessingTime = "Processing Time",
            Status = "Status",
            
            PatientInfo = "PATIENT INFORMATION",
            PatientName = "Full Name",
            Email = "Email",
            Phone = "Phone",
            DateOfBirth = "Date of Birth",
            
            RiskAssessment = "RISK ASSESSMENT",
            OverallRisk = "Overall Risk",
            RiskLevel = "Risk Level",
            RiskScore = "Risk Score",
            AiConfidence = "AI Confidence",
            
            DetailedFindings = "DETAILED FINDINGS",
            Condition = "Condition",
            Score = "Score",
            Hypertension = "Hypertension",
            Diabetes = "Diabetes",
            Stroke = "Stroke",
            DiabeticRetinopathy = "Diabetic Retinopathy",
            Detected = "Detected",
            
            VascularFindings = "VASCULAR ABNORMALITIES",
            VesselTortuosity = "Vessel Tortuosity",
            VesselWidthVariation = "Vessel Width Variation",
            MicroaneurysmsCount = "Microaneurysms Count",
            HemorrhagesDetected = "Hemorrhages",
            ExudatesDetected = "Exudates",
            
            Recommendations = "RECOMMENDATIONS",
            HealthWarnings = "HEALTH WARNINGS",
            
            Yes = "Yes",
            No = "No"
        };
    }

    #endregion

    #region Additional Helper Methods

    /// <summary>
    /// Get the owner userId of an analysis result (for doctor exports to get patient info)
    /// </summary>
    private async Task<string?> GetAnalysisOwnerUserIdAsync(string analysisResultId)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT ar.UserId 
                FROM analysis_results ar
                WHERE ar.Id = @AnalysisId AND ar.IsDeleted = false";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("AnalysisId", analysisResultId);

            var result = await command.ExecuteScalarAsync();
            return result as string;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not retrieve analysis owner for {AnalysisId}", analysisResultId);
            return null;
        }
    }

    /// <summary>
    /// Get patient userId for an analysis (ri.UserId - the patient whose scan was analyzed).
    /// For clinic analyses, ar.UserId may be clinicId; ri.UserId is the patient.
    /// </summary>
    private async Task<string?> GetPatientUserIdForAnalysisAsync(string analysisResultId)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT ri.UserId 
                FROM analysis_results ar
                INNER JOIN retinal_images ri ON ar.ImageId = ri.Id
                WHERE ar.Id = @AnalysisId AND ar.IsDeleted = false";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("AnalysisId", analysisResultId);

            var result = await command.ExecuteScalarAsync();
            return result as string;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not retrieve patient for analysis {AnalysisId}", analysisResultId);
            return null;
        }
    }

    #endregion
}
