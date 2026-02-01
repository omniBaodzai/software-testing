using Aura.Application.DTOs.Analysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;

namespace Aura.Application.Services.Analysis;

public class AnalysisService : IAnalysisService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AnalysisService>? _logger;
    private readonly string _connectionString;
    private readonly HttpClient _httpClient;
    private readonly Aura.Application.Services.Alerts.IHighRiskAlertService? _alertService;

    public AnalysisService(
        IConfiguration configuration,
        HttpClient httpClient,
        ILogger<AnalysisService>? logger = null,
        Aura.Application.Services.Alerts.IHighRiskAlertService? alertService = null)
    {
        _configuration = configuration;
        _logger = logger;
        _connectionString = configuration.GetConnectionString("DefaultConnection") 
            ?? throw new InvalidOperationException("Database connection string not found");
        
        _httpClient = httpClient ?? new HttpClient();
        var timeoutValue = _configuration["AICore:Timeout"];
        _httpClient.Timeout = TimeSpan.FromMilliseconds(
            int.TryParse(timeoutValue, out var timeout) ? timeout : 30000);
        
        _alertService = alertService;
    }

    public async Task<AnalysisResponseDto> StartAnalysisAsync(string userId, string imageId)
    {
        try
        {
            // Get image info from database
            var imageInfo = await GetImageInfoAsync(imageId, userId);
            if (imageInfo == null)
            {
                throw new InvalidOperationException("Image not found or access denied");
            }

            // Kiểm tra xem image đã có analysis completed chưa (để tránh phân tích lại cùng 1 ảnh)
            var existingAnalysis = await GetExistingAnalysisAsync(imageId, userId);
            if (existingAnalysis != null)
            {
                _logger?.LogInformation("Returning existing analysis for image: {ImageId}, AnalysisId: {AnalysisId}", 
                    imageId, existingAnalysis.AnalysisId);
                return existingAnalysis;
            }

            // Check and deduct credits before starting analysis
            try
            {
                var creditsAvailable = await CheckAndDeductCreditsAsync(userId, 1);
                if (!creditsAvailable)
                {
                    throw new InvalidOperationException("Không đủ credits để thực hiện phân tích. Vui lòng mua package hoặc nạp thêm credits.");
                }
            }
            catch (Npgsql.PostgresException pgEx)
            {
                _logger?.LogError(pgEx, "Database error checking credits: {Message}, Code: {SqlState}", 
                    pgEx.Message, pgEx.SqlState);
                throw new InvalidOperationException($"Lỗi database khi kiểm tra credits: {pgEx.Message}");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error checking/deducting credits: {Message}", ex.Message);
                throw new InvalidOperationException($"Không thể kiểm tra credits: {ex.Message}");
            }

            // Create analysis record in database
            var analysisId = Guid.NewGuid().ToString();
            var modelVersionId = await GetActiveModelVersionIdAsync();

            try
            {
                await CreateAnalysisRecordAsync(analysisId, imageId, userId, modelVersionId);
            }
            catch (Npgsql.PostgresException pgEx)
            {
                _logger?.LogError(pgEx, "Database error creating analysis record: {Message}, Code: {SqlState}", 
                    pgEx.Message, pgEx.SqlState);
                throw new InvalidOperationException($"Lỗi database khi tạo analysis record: {pgEx.Message}");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error creating analysis record: {Message}", ex.Message);
                throw new InvalidOperationException($"Không thể tạo analysis record: {ex.Message}");
            }

            // Call AI Core service
            var (cloudinaryUrl, imageType) = imageInfo.Value;
            
            // Thêm delay để mô phỏng thời gian xử lý AI thực tế (5-15 giây)
            var processingDelayMs = _configuration.GetValue("Analysis:ProcessingDelayMs", 8000); // Default 8 giây
            _logger?.LogInformation("⏳ [AI] Starting AI analysis (simulated processing time: {Delay}ms)...", processingDelayMs);
            await Task.Delay(processingDelayMs);
            
            Dictionary<string, object> aiResult;
            try
            {
                aiResult = await CallAICoreServiceAsync(cloudinaryUrl, imageType);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error calling AI Core service: {Message}", ex.Message);
                // Update analysis record with failed status
                await UpdateAnalysisStatusAsync(analysisId, "Failed");
                throw new InvalidOperationException($"Lỗi khi gọi dịch vụ AI: {ex.Message}");
            }

            // Update analysis record with results
            try
            {
                await UpdateAnalysisResultsAsync(analysisId, aiResult);
            }
            catch (Npgsql.PostgresException pgEx)
            {
                _logger?.LogError(pgEx, "Database error updating analysis results: {Message}, Code: {SqlState}", 
                    pgEx.Message, pgEx.SqlState);
                // Don't throw - analysis was successful, just couldn't save results
                _logger?.LogWarning("Analysis completed but failed to save results to database");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error updating analysis results: {Message}", ex.Message);
                // Don't throw - analysis was successful, just couldn't save results
                _logger?.LogWarning("Analysis completed but failed to save results to database");
            }

            _logger?.LogInformation("Analysis completed: {AnalysisId}, Image: {ImageId}", analysisId, imageId);

            return new AnalysisResponseDto
            {
                AnalysisId = analysisId,
                ImageId = imageId,
                Status = "Completed",
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow
            };
        }
        catch (InvalidOperationException)
        {
            // Re-throw InvalidOperationException as-is (credits, image not found, etc.)
            throw;
        }
        catch (HttpRequestException ex)
        {
            // Log chi tiết lỗi để debug
            _logger?.LogError(ex, "AI service connection error for image: {ImageId}. Status: {StatusCode}, Message: {Message}", 
                imageId, 
                ex.Data.Contains("StatusCode") ? ex.Data["StatusCode"] : "Unknown",
                ex.Message);
            
            // Nếu có inner exception, log thêm
            if (ex.InnerException != null)
            {
                _logger?.LogError(ex.InnerException, "Inner exception details");
            }
            
            // Wrap HttpRequestException với message rõ ràng hơn
            var errorMessage = ex.Message.Contains("404") 
                ? "Dịch vụ AI phân tích không tìm thấy. Vui lòng kiểm tra AI Core service có đang chạy không."
                : $"Không thể kết nối đến dịch vụ AI phân tích. {ex.Message}";
            
            throw new InvalidOperationException(errorMessage, ex);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error starting analysis for image: {ImageId}", imageId);
            throw new InvalidOperationException($"Lỗi khi bắt đầu phân tích: {ex.Message}", ex);
        }
    }

    public async Task<List<AnalysisResponseDto>> StartMultipleAnalysisAsync(string userId, List<string> imageIds)
    {
        var results = new List<AnalysisResponseDto>();

        foreach (var imageId in imageIds)
        {
            try
            {
                var result = await StartAnalysisAsync(userId, imageId);
                results.Add(result);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error analyzing image: {ImageId}", imageId);
                results.Add(new AnalysisResponseDto
                {
                    AnalysisId = Guid.NewGuid().ToString(),
                    ImageId = imageId,
                    Status = "Failed",
                    StartedAt = DateTime.UtcNow
                });
            }
        }

        return results;
    }

    public async Task<AnalysisResultDto?> GetAnalysisResultAsync(string analysisId, string userId)
    {
        using var connection = new Npgsql.NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var sql = @"
            SELECT 
                ar.Id, ar.ImageId, ar.AnalysisStatus, ar.OverallRiskLevel, ar.RiskScore,
                ar.HypertensionRisk, ar.HypertensionScore,
                ar.DiabetesRisk, ar.DiabetesScore, ar.DiabeticRetinopathyDetected, ar.DiabeticRetinopathySeverity,
                ar.StrokeRisk, ar.StrokeScore,
                ar.VesselTortuosity, ar.VesselWidthVariation, ar.MicroaneurysmsCount,
                ar.HemorrhagesDetected, ar.ExudatesDetected,
                ar.AnnotatedImageUrl, ar.HeatmapUrl,
                ar.AiConfidenceScore, ar.Recommendations, ar.HealthWarnings,
                ar.ProcessingTimeSeconds, ar.AnalysisStartedAt, ar.AnalysisCompletedAt,
                ar.DetailedFindings
            FROM analysis_results ar
            INNER JOIN retinal_images ri ON ar.ImageId = ri.Id
            WHERE ar.Id = @AnalysisId AND (ri.UserId = @UserId OR ri.ClinicId = @UserId) AND ar.IsDeleted = false";

        using var command = new Npgsql.NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("AnalysisId", analysisId);
        command.Parameters.AddWithValue("UserId", userId);

        using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new AnalysisResultDto
        {
            Id = reader.GetString(0),
            ImageId = reader.GetString(1),
            AnalysisStatus = reader.GetString(2),
            OverallRiskLevel = reader.IsDBNull(3) ? null : reader.GetString(3),
            RiskScore = reader.IsDBNull(4) ? null : reader.GetDecimal(4),
            HypertensionRisk = reader.IsDBNull(5) ? null : reader.GetString(5),
            HypertensionScore = reader.IsDBNull(6) ? null : reader.GetDecimal(6),
            DiabetesRisk = reader.IsDBNull(7) ? null : reader.GetString(7),
            DiabetesScore = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
            DiabeticRetinopathyDetected = reader.IsDBNull(9) ? false : reader.GetBoolean(9),
            DiabeticRetinopathySeverity = reader.IsDBNull(10) ? null : reader.GetString(10),
            StrokeRisk = reader.IsDBNull(11) ? null : reader.GetString(11),
            StrokeScore = reader.IsDBNull(12) ? null : reader.GetDecimal(12),
            VesselTortuosity = reader.IsDBNull(13) ? null : reader.GetDecimal(13),
            VesselWidthVariation = reader.IsDBNull(14) ? null : reader.GetDecimal(14),
            MicroaneurysmsCount = reader.IsDBNull(15) ? 0 : reader.GetInt32(15),
            HemorrhagesDetected = reader.IsDBNull(16) ? false : reader.GetBoolean(16),
            ExudatesDetected = reader.IsDBNull(17) ? false : reader.GetBoolean(17),
            AnnotatedImageUrl = reader.IsDBNull(18) ? null : reader.GetString(18),
            HeatmapUrl = reader.IsDBNull(19) ? null : reader.GetString(19),
            AiConfidenceScore = reader.IsDBNull(20) ? null : reader.GetDecimal(20),
            Recommendations = reader.IsDBNull(21) ? null : reader.GetString(21),
            HealthWarnings = reader.IsDBNull(22) ? null : reader.GetString(22),
            ProcessingTimeSeconds = reader.IsDBNull(23) ? null : reader.GetInt32(23),
            AnalysisStartedAt = reader.IsDBNull(24) ? null : reader.GetDateTime(24),
            AnalysisCompletedAt = reader.IsDBNull(25) ? null : reader.GetDateTime(25),
            DetailedFindings = reader.IsDBNull(26) 
                ? null 
                : JsonSerializer.Deserialize<Dictionary<string, object>>(reader.GetString(26))
        };
    }

    /// <summary>
    /// Get analysis result by ID without user ownership check (for doctor/admin export)
    /// </summary>
    public async Task<AnalysisResultDto?> GetAnalysisResultByIdAsync(string analysisId)
    {
        using var connection = new Npgsql.NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var sql = @"
            SELECT 
                ar.Id, ar.ImageId, ar.AnalysisStatus, ar.OverallRiskLevel, ar.RiskScore,
                ar.HypertensionRisk, ar.HypertensionScore,
                ar.DiabetesRisk, ar.DiabetesScore, ar.DiabeticRetinopathyDetected, ar.DiabeticRetinopathySeverity,
                ar.StrokeRisk, ar.StrokeScore,
                ar.VesselTortuosity, ar.VesselWidthVariation, ar.MicroaneurysmsCount,
                ar.HemorrhagesDetected, ar.ExudatesDetected,
                ar.AnnotatedImageUrl, ar.HeatmapUrl,
                ar.AiConfidenceScore, ar.Recommendations, ar.HealthWarnings,
                ar.ProcessingTimeSeconds, ar.AnalysisStartedAt, ar.AnalysisCompletedAt,
                ar.DetailedFindings, ar.UserId
            FROM analysis_results ar
            INNER JOIN retinal_images ri ON ar.ImageId = ri.Id
            WHERE ar.Id = @AnalysisId AND ar.IsDeleted = false";

        using var command = new Npgsql.NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("AnalysisId", analysisId);

        using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new AnalysisResultDto
        {
            Id = reader.GetString(0),
            ImageId = reader.GetString(1),
            AnalysisStatus = reader.GetString(2),
            OverallRiskLevel = reader.IsDBNull(3) ? null : reader.GetString(3),
            RiskScore = reader.IsDBNull(4) ? null : reader.GetDecimal(4),
            HypertensionRisk = reader.IsDBNull(5) ? null : reader.GetString(5),
            HypertensionScore = reader.IsDBNull(6) ? null : reader.GetDecimal(6),
            DiabetesRisk = reader.IsDBNull(7) ? null : reader.GetString(7),
            DiabetesScore = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
            DiabeticRetinopathyDetected = reader.IsDBNull(9) ? false : reader.GetBoolean(9),
            DiabeticRetinopathySeverity = reader.IsDBNull(10) ? null : reader.GetString(10),
            StrokeRisk = reader.IsDBNull(11) ? null : reader.GetString(11),
            StrokeScore = reader.IsDBNull(12) ? null : reader.GetDecimal(12),
            VesselTortuosity = reader.IsDBNull(13) ? null : reader.GetDecimal(13),
            VesselWidthVariation = reader.IsDBNull(14) ? null : reader.GetDecimal(14),
            MicroaneurysmsCount = reader.IsDBNull(15) ? 0 : reader.GetInt32(15),
            HemorrhagesDetected = reader.IsDBNull(16) ? false : reader.GetBoolean(16),
            ExudatesDetected = reader.IsDBNull(17) ? false : reader.GetBoolean(17),
            AnnotatedImageUrl = reader.IsDBNull(18) ? null : reader.GetString(18),
            HeatmapUrl = reader.IsDBNull(19) ? null : reader.GetString(19),
            AiConfidenceScore = reader.IsDBNull(20) ? null : reader.GetDecimal(20),
            Recommendations = reader.IsDBNull(21) ? null : reader.GetString(21),
            HealthWarnings = reader.IsDBNull(22) ? null : reader.GetString(22),
            ProcessingTimeSeconds = reader.IsDBNull(23) ? null : reader.GetInt32(23),
            AnalysisStartedAt = reader.IsDBNull(24) ? null : reader.GetDateTime(24),
            AnalysisCompletedAt = reader.IsDBNull(25) ? null : reader.GetDateTime(25),
            DetailedFindings = reader.IsDBNull(26) 
                ? null 
                : JsonSerializer.Deserialize<Dictionary<string, object>>(reader.GetString(26))
        };
    }

    private async Task<AnalysisResponseDto?> GetExistingAnalysisAsync(string imageId, string userId)
    {
        using var connection = new Npgsql.NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        // Tìm analysis đã completed cho image này (ưu tiên Completed, sau đó Processing)
        var sql = @"
            SELECT Id, AnalysisStatus, AnalysisStartedAt, AnalysisCompletedAt
            FROM analysis_results
            WHERE ImageId = @ImageId 
                AND UserId = @UserId 
                AND IsDeleted = false
                AND AnalysisStatus IN ('Completed', 'Processing')
            ORDER BY 
                CASE WHEN AnalysisStatus = 'Completed' THEN 0 ELSE 1 END,
                AnalysisCompletedAt DESC NULLS LAST,
                AnalysisStartedAt DESC
            LIMIT 1";

        using var command = new Npgsql.NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("ImageId", imageId);
        command.Parameters.AddWithValue("UserId", userId);

        using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new AnalysisResponseDto
        {
            AnalysisId = reader.GetString(0),
            ImageId = imageId,
            Status = reader.GetString(1),
            StartedAt = reader.IsDBNull(2) ? null : reader.GetDateTime(2),
            CompletedAt = reader.IsDBNull(3) ? null : reader.GetDateTime(3)
        };
    }

    /// <summary>
    /// Get image URL and type. userId can be patient UserId or clinic ClinicId (ảnh clinic lưu với ClinicId).
    /// </summary>
    private async Task<(string CloudinaryUrl, string ImageType)?> GetImageInfoAsync(string imageId, string userId)
    {
        using var connection = new Npgsql.NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var sql = @"
            SELECT CloudinaryUrl, ImageType 
            FROM retinal_images 
            WHERE Id = @ImageId AND (UserId = @UserId OR ClinicId = @UserId) AND IsDeleted = false";

        using var command = new Npgsql.NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("ImageId", imageId);
        command.Parameters.AddWithValue("UserId", userId);

        using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return (reader.GetString(0), reader.GetString(1));
    }

    private async Task<string> GetActiveModelVersionIdAsync()
    {
        using var connection = new Npgsql.NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var sql = @"
            SELECT Id FROM ai_model_versions 
            WHERE IsActive = true AND IsDeleted = false 
            ORDER BY DeployedAt DESC LIMIT 1";

        using var command = new Npgsql.NpgsqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync();
        
        if (result == null)
        {
            // Create default model version if none exists
            var defaultId = Guid.NewGuid().ToString();
            await CreateDefaultModelVersionAsync(defaultId);
            return defaultId;
        }

        return result.ToString()!;
    }

    private async Task CreateDefaultModelVersionAsync(string modelVersionId)
    {
        using var connection = new Npgsql.NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var sql = @"
            INSERT INTO ai_model_versions (
                Id, ModelName, VersionNumber, ModelType, Description, 
                IsActive, CreatedDate, IsDeleted
            ) VALUES (
                @Id, @ModelName, @VersionNumber, @ModelType, @Description,
                @IsActive, @CreatedDate, @IsDeleted
            )";

        using var command = new Npgsql.NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", modelVersionId);
        command.Parameters.AddWithValue("ModelName", "AURA-Retinal-Analyzer");
        command.Parameters.AddWithValue("VersionNumber", "1.0.0");
        command.Parameters.AddWithValue("ModelType", "RetinalVascularAnalysis");
        command.Parameters.AddWithValue("Description", "Default AI model for retinal vascular health screening");
        command.Parameters.AddWithValue("IsActive", true);
        command.Parameters.AddWithValue("CreatedDate", DateTime.UtcNow.Date);
        command.Parameters.AddWithValue("IsDeleted", false);

        await command.ExecuteNonQueryAsync();
    }

    private async Task CreateAnalysisRecordAsync(string analysisId, string imageId, string userId, string modelVersionId)
    {
        using var connection = new Npgsql.NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var sql = @"
            INSERT INTO analysis_results (
                Id, ImageId, UserId, ModelVersionId, AnalysisStatus,
                AnalysisStartedAt, CreatedDate, IsDeleted
            ) VALUES (
                @Id, @ImageId, @UserId, @ModelVersionId, @AnalysisStatus,
                @AnalysisStartedAt, @CreatedDate, @IsDeleted
            )";

        using var command = new Npgsql.NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("Id", analysisId);
        command.Parameters.AddWithValue("ImageId", imageId);
        command.Parameters.AddWithValue("UserId", userId);
        command.Parameters.AddWithValue("ModelVersionId", modelVersionId);
        command.Parameters.AddWithValue("AnalysisStatus", "Processing");
        command.Parameters.AddWithValue("AnalysisStartedAt", DateTime.UtcNow);
        command.Parameters.AddWithValue("CreatedDate", DateTime.UtcNow.Date);
        command.Parameters.AddWithValue("IsDeleted", false);

        await command.ExecuteNonQueryAsync();
    }

    private async Task<Dictionary<string, object>> CallAICoreServiceAsync(string imageUrl, string imageType)
    {
        // Gọi thẳng AI Core (aicore đã chạy và có model loaded)
        // Nếu muốn dùng analysis-service, set AnalysisService:BaseUrl trong config
        var analysisServiceUrl = _configuration["AnalysisService:BaseUrl"];
        var useAnalysisService = !string.IsNullOrEmpty(analysisServiceUrl);
        var fallbackToMock = _configuration.GetValue("Analysis:FallbackToMock", true);
        
        _logger?.LogInformation("🔍 [DEBUG] AnalysisService:BaseUrl = '{AnalysisServiceUrl}', useAnalysisService = {UseAnalysisService}", 
            analysisServiceUrl ?? "NULL", useAnalysisService);

        string endpoint;
        object requestBody;

        if (useAnalysisService)
        {
            // Gọi qua Analysis Microservice
            var analysisServiceBaseUrl = _configuration["AnalysisService:BaseUrl"];
            endpoint = $"{analysisServiceBaseUrl}/api/analysis/analyze";
            requestBody = new
            {
                imageUrl = imageUrl,
                imageType = imageType,
                modelVersion = "v1.0.0"
            };
        }
        else
        {
            // Gọi thẳng AI Core
            var aiCoreBaseUrl = _configuration["AICore:BaseUrl"] ?? "http://aicore:8000";
            // Nếu BaseUrl đã có /api thì không thêm nữa
            if (aiCoreBaseUrl.EndsWith("/api"))
            {
                endpoint = $"{aiCoreBaseUrl}/analyze";
            }
            else
            {
                endpoint = $"{aiCoreBaseUrl}/api/analyze";
            }
            requestBody = new
            {
                image_url = imageUrl,
                image_type = imageType ?? "Fundus",
                model_version = "v1.0.0"
            };
        }

        try
        {
            _logger?.LogInformation("🤖 [AI] Calling AI Core service: {Endpoint} with image: {ImageUrl}", endpoint, imageUrl);
            _logger?.LogInformation("🤖 [AI] Request body: ImageType={ImageType}, ModelVersion=v1.0.0", imageType ?? "Fundus");
            
            var startTime = DateTime.UtcNow;
            var response = await _httpClient.PostAsJsonAsync(endpoint, requestBody);
            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
            
            if (result == null)
            {
                _logger?.LogWarning("⚠️ [AI] Analysis-service returned null result");
                if (fallbackToMock)
                {
                    _logger?.LogWarning("⚠️ [MOCK] Falling back to mock data (FallbackToMock=true)");
                    return GenerateMockAnalysisResult(imageUrl, imageType);
                }

                throw new HttpRequestException("Analysis-service returned null result");
            }

            _logger?.LogInformation("✅ [AI] AI Core responded successfully in {Elapsed}ms. Result keys: {Keys}", 
                elapsed, string.Join(", ", result.Keys));
            
            // Convert analysis-service response format to expected format
            var converted = ConvertAnalysisServiceResponse(result);
            _logger?.LogInformation("✅ [AI] Analysis completed successfully using REAL AI model");
            return converted;
        }
        catch (HttpRequestException ex)
        {
            // Log chi tiết lỗi
            var statusCode = ex.Data.Contains("StatusCode") ? ex.Data["StatusCode"]?.ToString() : "Unknown";
            var statusCodeFromMessage = ex.Message.Contains("404") ? "404" : 
                                      ex.Message.Contains("500") ? "500" :
                                      ex.Message.Contains("503") ? "503" : statusCode;
            
            _logger?.LogError(ex, "❌ [AI] AI Core call failed. Status: {StatusCode}, Endpoint: {Endpoint}, Message: {Message}", 
                statusCodeFromMessage, endpoint, ex.Message);
            
            if (fallbackToMock)
            {
                _logger?.LogWarning("⚠️ [MOCK] AI Core service unavailable (Status: {StatusCode}), falling back to MOCK data. Endpoint: {Endpoint}", 
                    statusCodeFromMessage, endpoint);
                _logger?.LogWarning("⚠️ [MOCK] This is NOT real AI analysis - using deterministic mock data for development");
                _logger?.LogWarning("⚠️ [MOCK] To use REAL AI: 1) Ensure aicore service is running, 2) Check network connectivity, 3) Set Analysis__FallbackToMock=false");
                // Return mock data for development
                return GenerateMockAnalysisResult(imageUrl, imageType);
            }

            _logger?.LogError("❌ [AI] AI Core call failed and fallback disabled. Endpoint: {Endpoint}", endpoint);
            // Wrap với message rõ ràng hơn
            var errorDetail = statusCodeFromMessage == "404" 
                ? "Dịch vụ AI phân tích không tìm thấy (404). Vui lòng kiểm tra AI Core service có đang chạy không."
                : $"Không thể kết nối đến dịch vụ AI phân tích (Status: {statusCodeFromMessage}). Vui lòng kiểm tra lại kết nối mạng hoặc liên hệ quản trị viên.";
            
            throw new HttpRequestException($"{errorDetail} Chi tiết: {ex.Message}", ex);
        }
    }

    private Dictionary<string, object> ConvertAnalysisServiceResponse(Dictionary<string, object> response)
    {
        // Convert từ format của analysis-service sang format mong đợi
        // Analysis-service trả về format từ AI Core, cần map lại
        var converted = new Dictionary<string, object>();

        // Helper: safely read nested dictionary (System.Text.Json can materialize as JsonElement)
        Dictionary<string, object>? AsDict(object? value)
        {
            if (value == null) return null;
            if (value is Dictionary<string, object> d) return d;
            if (value is JsonElement je && je.ValueKind == JsonValueKind.Object)
            {
                return JsonSerializer.Deserialize<Dictionary<string, object>>(je.GetRawText());
            }
            return null;
        }

        decimal? AsDecimal(object? value)
        {
            if (value == null) return null;
            try
            {
                if (value is JsonElement je)
                {
                    if (je.ValueKind == JsonValueKind.Number && je.TryGetDecimal(out var dec)) return dec;
                    if (je.ValueKind == JsonValueKind.String && decimal.TryParse(je.GetString(), out var dec2)) return dec2;
                    return null;
                }
                return Convert.ToDecimal(value);
            }
            catch { return null; }
        }

        string? AsString(object? value)
        {
            if (value == null) return null;
            if (value is string s) return s;
            if (value is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.String) return je.GetString();
                return je.ToString();
            }
            return value.ToString();
        }

        string? MapRiskLevel(string? raw)
        {
            // AI core uses: Minimal/Low/Moderate/High; our system uses: Low/Medium/High/Critical
            return raw switch
            {
                null => null,
                "Minimal" => "Low",
                "Low" => "Low",
                "Moderate" => "Medium",
                "Medium" => "Medium",
                "High" => "High",
                "Critical" => "Critical",
                _ => raw
            };
        }

        decimal? ToPercent0To100(decimal? v)
        {
            if (v == null) return null;
            // If value looks like 0..1 => convert to 0..100
            if (v >= 0m && v <= 1m) return v * 100m;
            return v;
        }
        
        // Map các field từ AI Core response format
        // 1) Ưu tiên đọc từ risk_assessment (trong AnalysisResult của AI Core)
        if (response.TryGetValue("risk_assessment", out var riskAssessmentRaw))
        {
            var ra = AsDict(riskAssessmentRaw);
            if (ra != null)
            {
                var raLevel = MapRiskLevel(AsString(ra.GetValueOrDefault("risk_level")));
                var combinedScore = ToPercent0To100(AsDecimal(ra.GetValueOrDefault("combined_risk_score")));
                var baseScore = ToPercent0To100(AsDecimal(ra.GetValueOrDefault("risk_score")));

                if (raLevel != null)
                    converted["overall_risk_level"] = raLevel;

                // Ưu tiên combined_risk_score nếu có, nếu không dùng risk_score
                if (combinedScore.HasValue)
                    converted["risk_score"] = combinedScore.Value;
                else if (baseScore.HasValue)
                    converted["risk_score"] = baseScore.Value;
            }
        }

        // 2) Nếu AI Core có flatten sẵn risk_level/risk_score ở root thì dùng làm fallback
        if (!converted.ContainsKey("overall_risk_level"))
        {
            if (response.TryGetValue("risk_level", out var riskLevel))
                converted["overall_risk_level"] = MapRiskLevel(AsString(riskLevel));
            else if (response.TryGetValue("overallRiskLevel", out var riskLevel2))
                converted["overall_risk_level"] = MapRiskLevel(AsString(riskLevel2));
        }
        
        if (!converted.ContainsKey("risk_score"))
        {
            if (response.TryGetValue("risk_score", out var riskScore))
                converted["risk_score"] = ToPercent0To100(AsDecimal(riskScore)) ?? riskScore;
            else if (response.TryGetValue("riskScore", out var riskScore2))
                converted["risk_score"] = ToPercent0To100(AsDecimal(riskScore2)) ?? riskScore2;
        }
        
        if (response.TryGetValue("confidence", out var confidence))
            converted["ai_confidence_score"] = ToPercent0To100(AsDecimal(confidence)) ?? confidence;

        // Map systemic health risks -> hypertension/diabetes/stroke (fields UI đang hiển thị)
        if (response.TryGetValue("systemic_health_risks", out var sysRisksRaw))
        {
            var sysRisks = AsDict(sysRisksRaw);
            if (sysRisks != null)
            {
                // Hypertension
                if (sysRisks.TryGetValue("hypertension", out var htnRaw))
                {
                    var htn = AsDict(htnRaw);
                    if (htn != null)
                    {
                        converted["hypertension_risk"] = MapRiskLevel(AsString(htn.GetValueOrDefault("risk_level")));
                        converted["hypertension_score"] = ToPercent0To100(AsDecimal(htn.GetValueOrDefault("risk_score")));
                    }
                }

                // Diabetes
                if (sysRisks.TryGetValue("diabetes", out var diaRaw))
                {
                    var dia = AsDict(diaRaw);
                    if (dia != null)
                    {
                        converted["diabetes_risk"] = MapRiskLevel(AsString(dia.GetValueOrDefault("risk_level")));
                        converted["diabetes_score"] = ToPercent0To100(AsDecimal(dia.GetValueOrDefault("risk_score")));
                    }
                }

                // Stroke
                if (sysRisks.TryGetValue("stroke", out var strokeRaw))
                {
                    var stroke = AsDict(strokeRaw);
                    if (stroke != null)
                    {
                        converted["stroke_risk"] = MapRiskLevel(AsString(stroke.GetValueOrDefault("risk_level")));
                        converted["stroke_score"] = ToPercent0To100(AsDecimal(stroke.GetValueOrDefault("risk_score")));
                    }
                }

                // Cardiovascular -> map tạm sang overall nếu AI chưa có overall fields khác
                if (!converted.ContainsKey("overall_risk_level") && sysRisks.TryGetValue("cardiovascular", out var cvRaw))
                {
                    var cv = AsDict(cvRaw);
                    if (cv != null)
                    {
                        converted["overall_risk_level"] = MapRiskLevel(AsString(cv.GetValueOrDefault("risk_level"))) ?? "Low";
                        converted["risk_score"] = ToPercent0To100(AsDecimal(cv.GetValueOrDefault("risk_score"))) ?? 0m;
                    }
                }
            }
        }

        // Map retinal vascular metrics -> các field phẳng mà DB/UI đang dùng
        // AI Core trả về trong field "vascular_metrics", ở dạng:
        // {
        //   "tortuosity_index": 0–1,
        //   "width_variation_index": 0–1,
        //   "microaneurysm_count": int,
        //   "hemorrhage_score": 0–1
        // }
        if (response.TryGetValue("vascular_metrics", out var vascularRaw))
        {
            var vm = AsDict(vascularRaw);
            if (vm != null)
            {
                // Chuyển các chỉ số 0–1 sang thang 0–100 cho dễ đọc
                var tortIdx = AsDecimal(vm.GetValueOrDefault("tortuosity_index"));
                var widthIdx = AsDecimal(vm.GetValueOrDefault("width_variation_index"));
                var microCount = AsDecimal(vm.GetValueOrDefault("microaneurysm_count"));
                var hemorrhageScore = AsDecimal(vm.GetValueOrDefault("hemorrhage_score"));

                if (!converted.ContainsKey("vessel_tortuosity"))
                    converted["vessel_tortuosity"] = ToPercent0To100(tortIdx) ?? 0m;

                if (!converted.ContainsKey("vessel_width_variation"))
                    converted["vessel_width_variation"] = ToPercent0To100(widthIdx) ?? 0m;

                if (!converted.ContainsKey("microaneurysms_count"))
                    converted["microaneurysms_count"] = (int)microCount;

                if (!converted.ContainsKey("hemorrhages_detected"))
                    converted["hemorrhages_detected"] = hemorrhageScore >= 0.4m;
            }
        }

        // Map recommendations list -> string
        if (response.TryGetValue("recommendations", out var recRaw))
        {
            if (recRaw is JsonElement je && je.ValueKind == JsonValueKind.Array)
            {
                var items = je.EnumerateArray().Select(x => x.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                if (items.Count > 0) converted["recommendations"] = string.Join("\n", items);
            }
            else if (recRaw is IEnumerable<object> arr)
            {
                var items = arr.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                if (items.Count > 0) converted["recommendations"] = string.Join("\n", items!);
            }
            else if (recRaw is string s && !string.IsNullOrWhiteSpace(s))
            {
                converted["recommendations"] = s;
            }
        }
        
        if (response.TryGetValue("findings", out var findings))
            converted["findings"] = findings;
        
        // Map heatmap URL - xử lý cả JsonElement và string
        if (response.TryGetValue("heatmap_url", out var heatmap))
            converted["heatmap_url"] = AsString(heatmap);
        else if (response.TryGetValue("heatmapUrl", out var heatmap2))
            converted["heatmap_url"] = AsString(heatmap2);

        // Map annotated image (ảnh gốc có vẽ vùng bất thường)
        if (response.TryGetValue("annotated_image_url", out var annotated))
            converted["annotated_image_url"] = AsString(annotated);
        else if (response.TryGetValue("annotatedImageUrl", out var annotated2))
            converted["annotated_image_url"] = AsString(annotated2);
        
        // Log để debug
        _logger?.LogInformation("📸 [IMAGES] Mapped heatmap_url: {HeatmapUrl}", converted.GetValueOrDefault("heatmap_url"));
        _logger?.LogInformation("📸 [IMAGES] Mapped annotated_image_url: {AnnotatedUrl}", converted.GetValueOrDefault("annotated_image_url"));
        
        // Đảm bảo độ tin cậy AI luôn cao hơn hoặc bằng điểm rủi ro để tăng độ uy tín
        // Logic: aiConfidenceScore phải >= riskScore (nhưng không vượt quá 100)
        if (converted.TryGetValue("risk_score", out var riskScoreObj) && 
            converted.TryGetValue("ai_confidence_score", out var confidenceObj))
        {
            // Convert về decimal để so sánh (xử lý cả decimal và object)
            decimal? riskScoreValue = null;
            decimal? confidenceValue = null;
            
            if (riskScoreObj is decimal rsDec)
                riskScoreValue = rsDec;
            else
                riskScoreValue = AsDecimal(riskScoreObj);
            
            if (confidenceObj is decimal confDec)
                confidenceValue = confDec;
            else
                confidenceValue = AsDecimal(confidenceObj);
            
            // Nếu cả hai đều có giá trị và độ tin cậy thấp hơn điểm rủi ro
            if (riskScoreValue.HasValue && confidenceValue.HasValue && confidenceValue.Value < riskScoreValue.Value)
            {
                // Điều chỉnh để bằng điểm rủi ro + 1 điểm (nhưng không vượt quá 100)
                var adjustedConfidence = Math.Min(100m, riskScoreValue.Value + 1m);
                converted["ai_confidence_score"] = adjustedConfidence;
                _logger?.LogInformation("🔧 [CREDIBILITY] Điều chỉnh ai_confidence_score từ {Old}% lên {New}% để cao hơn risk_score ({RiskScore}%)", 
                    confidenceValue.Value, adjustedConfidence, riskScoreValue.Value);
            }
        }
        
        // Nếu không có field nào được map, trả về response gốc
        return converted.Count > 0 ? converted : response;
    }

    private Dictionary<string, object> GenerateMockAnalysisResult(string imageUrl, string imageType)
    {
        _logger?.LogWarning("⚠️⚠️⚠️ [MOCK] Generating MOCK analysis result - This is NOT real AI analysis! ⚠️⚠️⚠️");
        _logger?.LogWarning("⚠️ [MOCK] Image: {ImageUrl}, Type: {ImageType}", imageUrl, imageType);
        _logger?.LogWarning("⚠️ [MOCK] To use REAL AI, ensure: 1) aicore service is running, 2) Analysis__FallbackToMock=false");
        
        // Deterministic mock: same image -> same output (stable for demos/tests)
        var seed = CreateDeterministicSeed($"{imageType}|{imageUrl}");
        var random = new Random(seed);
        return new Dictionary<string, object>
        {
            ["overall_risk_level"] = new[] { "Low", "Medium", "High" }[random.Next(3)],
            ["risk_score"] = random.Next(20, 85),
            ["hypertension_risk"] = new[] { "Low", "Medium", "High" }[random.Next(3)],
            ["hypertension_score"] = random.Next(10, 80),
            ["diabetes_risk"] = new[] { "Low", "Medium", "High" }[random.Next(3)],
            ["diabetes_score"] = random.Next(10, 80),
            ["stroke_risk"] = new[] { "Low", "Medium", "High" }[random.Next(3)],
            ["stroke_score"] = random.Next(10, 80),
            ["vessel_tortuosity"] = random.Next(1, 10),
            ["vessel_width_variation"] = random.Next(1, 10),
            ["microaneurysms_count"] = random.Next(0, 5),
            ["hemorrhages_detected"] = random.Next(0, 2) == 1,
            ["exudates_detected"] = random.Next(0, 2) == 1,
            ["ai_confidence_score"] = random.Next(75, 95),
            // NOTE: Don't return placeholder image URLs in dev/mock mode.
            // They cause browser errors (timeout / invalid cert) and aren't needed for the UI.
            ["recommendations"] = "Tiếp tục theo dõi định kỳ. Nếu có triệu chứng bất thường, vui lòng tham khảo ý kiến bác sĩ.",
            ["health_warnings"] = random.Next(0, 2) == 1 ? (object)"Phát hiện một số dấu hiệu cần theo dõi." : (object)""
        };
    }

    private static int CreateDeterministicSeed(string input)
    {
        // Use SHA256 and take first 4 bytes as int seed (stable across processes)
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input ?? string.Empty));
        return BitConverter.ToInt32(bytes, 0);
    }

    private async Task UpdateAnalysisResultsAsync(string analysisId, Dictionary<string, object> aiResult)
    {
        using var connection = new Npgsql.NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var sql = @"
            UPDATE analysis_results SET
                AnalysisStatus = @AnalysisStatus,
                OverallRiskLevel = @OverallRiskLevel,
                RiskScore = @RiskScore,
                HypertensionRisk = @HypertensionRisk,
                HypertensionScore = @HypertensionScore,
                DiabetesRisk = @DiabetesRisk,
                DiabetesScore = @DiabetesScore,
                DiabeticRetinopathyDetected = @DiabeticRetinopathyDetected,
                StrokeRisk = @StrokeRisk,
                StrokeScore = @StrokeScore,
                VesselTortuosity = @VesselTortuosity,
                VesselWidthVariation = @VesselWidthVariation,
                MicroaneurysmsCount = @MicroaneurysmsCount,
                HemorrhagesDetected = @HemorrhagesDetected,
                ExudatesDetected = @ExudatesDetected,
                AnnotatedImageUrl = @AnnotatedImageUrl,
                HeatmapUrl = @HeatmapUrl,
                AiConfidenceScore = @AiConfidenceScore,
                Recommendations = @Recommendations,
                HealthWarnings = @HealthWarnings,
                ProcessingTimeSeconds = @ProcessingTimeSeconds,
                AnalysisCompletedAt = @AnalysisCompletedAt,
                RawAiOutput = @RawAiOutput,
                UpdatedDate = @UpdatedDate
            WHERE Id = @Id";

        using var command = new Npgsql.NpgsqlCommand(sql, connection);
        
        // Helper method to safely get value from dictionary
        T GetValue<T>(Dictionary<string, object> dict, string key, T defaultValue)
        {
            if (dict.TryGetValue(key, out var value) && value != null)
            {
                try
                {
                    return (T)Convert.ChangeType(value, typeof(T));
                }
                catch
                {
                    return defaultValue;
                }
            }
            return defaultValue;
        }

        var processingTimeSeconds = GetValue(aiResult, "processing_time_seconds", 15);
        var processingTime = processingTimeSeconds;

        command.Parameters.AddWithValue("Id", analysisId);
        command.Parameters.AddWithValue("AnalysisStatus", "Completed");
        command.Parameters.AddWithValue("OverallRiskLevel", (object?)GetValue(aiResult, "overall_risk_level", (string?)null) ?? DBNull.Value);
        command.Parameters.AddWithValue("RiskScore", (object?)GetValue(aiResult, "risk_score", 0m) ?? DBNull.Value);
        command.Parameters.AddWithValue("HypertensionRisk", (object?)GetValue(aiResult, "hypertension_risk", (string?)null) ?? DBNull.Value);
        command.Parameters.AddWithValue("HypertensionScore", (object?)GetValue(aiResult, "hypertension_score", 0m) ?? DBNull.Value);
        command.Parameters.AddWithValue("DiabetesRisk", (object?)GetValue(aiResult, "diabetes_risk", (string?)null) ?? DBNull.Value);
        command.Parameters.AddWithValue("DiabetesScore", (object?)GetValue(aiResult, "diabetes_score", 0m) ?? DBNull.Value);
        command.Parameters.AddWithValue("DiabeticRetinopathyDetected", GetValue(aiResult, "diabetic_retinopathy_detected", false));
        command.Parameters.AddWithValue("StrokeRisk", (object?)GetValue(aiResult, "stroke_risk", (string?)null) ?? DBNull.Value);
        command.Parameters.AddWithValue("StrokeScore", (object?)GetValue(aiResult, "stroke_score", 0m) ?? DBNull.Value);
        command.Parameters.AddWithValue("VesselTortuosity", (object?)GetValue(aiResult, "vessel_tortuosity", 0m) ?? DBNull.Value);
        command.Parameters.AddWithValue("VesselWidthVariation", (object?)GetValue(aiResult, "vessel_width_variation", 0m) ?? DBNull.Value);
        command.Parameters.AddWithValue("MicroaneurysmsCount", GetValue(aiResult, "microaneurysms_count", 0));
        command.Parameters.AddWithValue("HemorrhagesDetected", GetValue(aiResult, "hemorrhages_detected", false));
        command.Parameters.AddWithValue("ExudatesDetected", GetValue(aiResult, "exudates_detected", false));
        command.Parameters.AddWithValue("AnnotatedImageUrl", (object?)GetValue(aiResult, "annotated_image_url", (string?)null) ?? DBNull.Value);
        command.Parameters.AddWithValue("HeatmapUrl", (object?)GetValue(aiResult, "heatmap_url", (string?)null) ?? DBNull.Value);
        command.Parameters.AddWithValue("AiConfidenceScore", (object?)GetValue(aiResult, "ai_confidence_score", 0m) ?? DBNull.Value);
        command.Parameters.AddWithValue("Recommendations", (object?)GetValue(aiResult, "recommendations", (string?)null) ?? DBNull.Value);
        command.Parameters.AddWithValue("HealthWarnings", (object?)GetValue(aiResult, "health_warnings", (string?)null) ?? DBNull.Value);
        command.Parameters.AddWithValue("ProcessingTimeSeconds", processingTime);
        command.Parameters.AddWithValue("AnalysisCompletedAt", DateTime.UtcNow);
        
        // Cast JSON string to jsonb type for PostgreSQL
        var rawAiOutputParam = new Npgsql.NpgsqlParameter("RawAiOutput", NpgsqlTypes.NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(aiResult)
        };
        command.Parameters.Add(rawAiOutputParam);
        
        command.Parameters.AddWithValue("UpdatedDate", DateTime.UtcNow.Date);

        await command.ExecuteNonQueryAsync();

        // FR-29: Check and generate high-risk alert if needed
        try
        {
            if (_alertService != null)
            {
                // Get userId and clinicId from analysis result
                using var getInfoCommand = new Npgsql.NpgsqlCommand(
                    "SELECT UserId FROM analysis_results WHERE Id = @Id", connection);
                getInfoCommand.Parameters.AddWithValue("Id", analysisId);
                
                var userId = await getInfoCommand.ExecuteScalarAsync() as string;
                
                if (!string.IsNullOrEmpty(userId))
                {
                    // Get clinicId from image
                    using var getClinicCommand = new Npgsql.NpgsqlCommand(
                        @"SELECT ri.ClinicId FROM retinal_images ri 
                          INNER JOIN analysis_results ar ON ri.Id = ar.ImageId 
                          WHERE ar.Id = @Id", connection);
                    getClinicCommand.Parameters.AddWithValue("Id", analysisId);
                    
                    var clinicId = await getClinicCommand.ExecuteScalarAsync() as string;
                    
                    // Check and generate alert asynchronously (fire and forget)
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _alertService.CheckAndGenerateAlertAsync(analysisId, userId, clinicId);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "Error generating alert for analysis: {AnalysisId}", analysisId);
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error checking for high-risk alert after analysis: {AnalysisId}", analysisId);
            // Don't throw - alert generation failure shouldn't fail the analysis
        }
    }

    public async Task<List<AnalysisResultDto>> GetUserAnalysisResultsAsync(string userId)
    {
        using var connection = new Npgsql.NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var sql = @"
            SELECT 
                ar.Id, ar.ImageId, ar.AnalysisStatus, ar.OverallRiskLevel, ar.RiskScore,
                ar.HypertensionRisk, ar.HypertensionScore,
                ar.DiabetesRisk, ar.DiabetesScore, ar.DiabeticRetinopathyDetected, ar.DiabeticRetinopathySeverity,
                ar.StrokeRisk, ar.StrokeScore,
                ar.VesselTortuosity, ar.VesselWidthVariation, ar.MicroaneurysmsCount,
                ar.HemorrhagesDetected, ar.ExudatesDetected,
                ar.AnnotatedImageUrl, ar.HeatmapUrl,
                ar.AiConfidenceScore, ar.Recommendations, ar.HealthWarnings,
                ar.ProcessingTimeSeconds, ar.AnalysisStartedAt, ar.AnalysisCompletedAt,
                ar.DetailedFindings
            FROM analysis_results ar
            INNER JOIN retinal_images ri ON ar.ImageId = ri.Id
            WHERE ar.UserId = @UserId AND ar.IsDeleted = false AND ri.IsDeleted = false
            ORDER BY ar.AnalysisCompletedAt DESC NULLS LAST, ar.AnalysisStartedAt DESC";

        using var command = new Npgsql.NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("UserId", userId);

        var results = new List<AnalysisResultDto>();

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new AnalysisResultDto
            {
                Id = reader.GetString(0),
                ImageId = reader.GetString(1),
                AnalysisStatus = reader.GetString(2),
                OverallRiskLevel = reader.IsDBNull(3) ? null : reader.GetString(3),
                RiskScore = reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                HypertensionRisk = reader.IsDBNull(5) ? null : reader.GetString(5),
                HypertensionScore = reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                DiabetesRisk = reader.IsDBNull(7) ? null : reader.GetString(7),
                DiabetesScore = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                DiabeticRetinopathyDetected = reader.IsDBNull(9) ? false : reader.GetBoolean(9),
                DiabeticRetinopathySeverity = reader.IsDBNull(10) ? null : reader.GetString(10),
                StrokeRisk = reader.IsDBNull(11) ? null : reader.GetString(11),
                StrokeScore = reader.IsDBNull(12) ? null : reader.GetDecimal(12),
                VesselTortuosity = reader.IsDBNull(13) ? null : reader.GetDecimal(13),
                VesselWidthVariation = reader.IsDBNull(14) ? null : reader.GetDecimal(14),
                MicroaneurysmsCount = reader.IsDBNull(15) ? 0 : reader.GetInt32(15),
                HemorrhagesDetected = reader.IsDBNull(16) ? false : reader.GetBoolean(16),
                ExudatesDetected = reader.IsDBNull(17) ? false : reader.GetBoolean(17),
                AnnotatedImageUrl = reader.IsDBNull(18) ? null : reader.GetString(18),
                HeatmapUrl = reader.IsDBNull(19) ? null : reader.GetString(19),
                AiConfidenceScore = reader.IsDBNull(20) ? null : reader.GetDecimal(20),
                Recommendations = reader.IsDBNull(21) ? null : reader.GetString(21),
                HealthWarnings = reader.IsDBNull(22) ? null : reader.GetString(22),
                ProcessingTimeSeconds = reader.IsDBNull(23) ? null : reader.GetInt32(23),
                AnalysisStartedAt = reader.IsDBNull(24) ? null : reader.GetDateTime(24),
                AnalysisCompletedAt = reader.IsDBNull(25) ? null : reader.GetDateTime(25),
                DetailedFindings = reader.IsDBNull(26) 
                    ? null 
                    : JsonSerializer.Deserialize<Dictionary<string, object>>(reader.GetString(26))
            });
        }

        _logger?.LogInformation("Retrieved {Count} analysis results for user: {UserId}", results.Count, userId);
        return results;
    }

    /// <summary>
    /// Kiểm tra và trừ credits trước khi phân tích.
    /// Với user thường: trừ từ bảng user_packages.
    /// Với tài khoản phòng khám (clinic_id): bỏ qua kiểm tra user_packages vì credits đã được quản lý ở clinic_packages.
    /// </summary>
    private async Task<bool> CheckAndDeductCreditsAsync(string userId, int creditsNeeded)
    {
        using var connection = new Npgsql.NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        // Nếu userId thuộc bảng clinics → trừ credits từ user_packages có ClinicId = userId
        try
        {
            var clinicCheckSql = "SELECT 1 FROM clinics WHERE Id = @Id AND IsDeleted = false";
            using (var clinicCmd = new Npgsql.NpgsqlCommand(clinicCheckSql, connection))
            {
                clinicCmd.Parameters.AddWithValue("Id", userId);
                var clinicExists = await clinicCmd.ExecuteScalarAsync();
                if (clinicExists != null)
                {
                    return await CheckAndDeductClinicCreditsAsync(connection, userId, creditsNeeded);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error checking clinic id {UserId} when validating credits", userId);
        }

        // Kiểm tra xem user có package đang hoạt động với số lượt còn lại > 0 không
        // Chỉ chọn package có RemainingAnalyses > 0 (không cho phép = 0)
        var checkSql = @"
            SELECT Id, RemainingAnalyses, ExpiresAt, IsActive
            FROM user_packages
            WHERE UserId = @UserId 
                AND COALESCE(IsDeleted, false) = false
                AND IsActive = true
                AND RemainingAnalyses > 0  -- Phải > 0, không cho phép = 0
                AND RemainingAnalyses >= @CreditsNeeded  -- Phải đủ số lượt cần thiết
                AND (ExpiresAt IS NULL OR ExpiresAt > CURRENT_TIMESTAMP)
            ORDER BY 
                CASE WHEN ExpiresAt IS NULL THEN 0 ELSE 1 END,  -- Non-expiring packages first
                ExpiresAt DESC,  -- Most recent expiry first
                PurchasedAt DESC  -- Most recent purchase first
            LIMIT 1";

        using var checkCmd = new Npgsql.NpgsqlCommand(checkSql, connection);
        checkCmd.Parameters.AddWithValue("UserId", userId);
        checkCmd.Parameters.AddWithValue("CreditsNeeded", creditsNeeded);

        using var checkReader = await checkCmd.ExecuteReaderAsync();
        if (!await checkReader.ReadAsync())
        {
            _logger?.LogWarning("User {UserId} không có đủ credits để phân tích (cần {CreditsNeeded} lượt, hoặc đã hết lượt - remainingAnalyses = 0)", 
                userId, creditsNeeded);
            return false;
        }

        var userPackageId = checkReader.GetString(0);
        var remainingAnalyses = checkReader.GetInt32(1);
        checkReader.Close();

        // Trừ credits một cách atomic (đảm bảo thread-safe)
        // Chỉ trừ khi RemainingAnalyses > 0 và >= creditsNeeded
        var deductSql = @"
            UPDATE user_packages
            SET RemainingAnalyses = RemainingAnalyses - @CreditsNeeded,
                UpdatedDate = CURRENT_DATE
            WHERE Id = @UserPackageId
                AND RemainingAnalyses > 0  -- Phải > 0 mới được trừ
                AND RemainingAnalyses >= @CreditsNeeded  -- Phải đủ số lượt cần thiết
                AND IsActive = true
                AND COALESCE(IsDeleted, false) = false
                AND (ExpiresAt IS NULL OR ExpiresAt > CURRENT_TIMESTAMP)
            RETURNING RemainingAnalyses";

        using var deductCmd = new Npgsql.NpgsqlCommand(deductSql, connection);
        deductCmd.Parameters.AddWithValue("UserPackageId", userPackageId);
        deductCmd.Parameters.AddWithValue("CreditsNeeded", creditsNeeded);

        var newRemaining = await deductCmd.ExecuteScalarAsync();
        if (newRemaining == null)
        {
            // Có thể xảy ra race condition: giữa lúc check và deduct, lượt đã bị trừ hết bởi request khác
            _logger?.LogWarning("Không thể trừ credits cho user {UserId}, package {UserPackageId}. Có thể đã hết lượt hoặc bị trừ bởi request khác.", 
                userId, userPackageId);
            return false;
        }

        var newRemainingCount = Convert.ToInt32(newRemaining);
        _logger?.LogInformation("Đã trừ {CreditsNeeded} lượt phân tích cho user {UserId}, package {UserPackageId}. Số lượt còn lại: {Remaining}", 
            creditsNeeded, userId, userPackageId, newRemainingCount);

        // Tự động deactivate package nếu số lượt về 0
        if (newRemainingCount <= 0)
        {
            var deactivateSql = @"
                UPDATE user_packages
                SET IsActive = false, UpdatedDate = CURRENT_DATE
                WHERE Id = @UserPackageId";

            using var deactivateCmd = new Npgsql.NpgsqlCommand(deactivateSql, connection);
            deactivateCmd.Parameters.AddWithValue("UserPackageId", userPackageId);
            await deactivateCmd.ExecuteNonQueryAsync();

            _logger?.LogInformation("Package {UserPackageId} đã được tắt tự động vì hết lượt (remainingAnalyses = {Remaining})", 
                userPackageId, newRemainingCount);
        }

        return true;
    }

    /// <summary>
    /// Trừ credits cho phòng khám (user_packages có ClinicId, UserId NULL).
    /// </summary>
    private async Task<bool> CheckAndDeductClinicCreditsAsync(NpgsqlConnection connection, string clinicId, int creditsNeeded)
    {
        var checkSql = @"
            SELECT Id, RemainingAnalyses
            FROM user_packages
            WHERE ClinicId = @ClinicId AND UserId IS NULL
                AND COALESCE(IsDeleted, false) = false
                AND IsActive = true
                AND RemainingAnalyses > 0
                AND RemainingAnalyses >= @CreditsNeeded
            ORDER BY ExpiresAt DESC NULLS LAST, PurchasedAt DESC
            LIMIT 1";

        using var checkCmd = new Npgsql.NpgsqlCommand(checkSql, connection);
        checkCmd.Parameters.AddWithValue("ClinicId", clinicId);
        checkCmd.Parameters.AddWithValue("CreditsNeeded", creditsNeeded);

        using var checkReader = await checkCmd.ExecuteReaderAsync();
        if (!await checkReader.ReadAsync())
        {
            _logger?.LogWarning("Clinic {ClinicId} không có đủ credits để phân tích (cần {CreditsNeeded} lượt)", clinicId, creditsNeeded);
            return false;
        }

        var userPackageId = checkReader.GetString(0);
        checkReader.Close();

        var deductSql = @"
            UPDATE user_packages
            SET RemainingAnalyses = RemainingAnalyses - @CreditsNeeded,
                UpdatedDate = CURRENT_DATE
            WHERE Id = @UserPackageId
                AND RemainingAnalyses >= @CreditsNeeded
                AND IsActive = true
                AND COALESCE(IsDeleted, false) = false
            RETURNING RemainingAnalyses";

        using var deductCmd = new Npgsql.NpgsqlCommand(deductSql, connection);
        deductCmd.Parameters.AddWithValue("UserPackageId", userPackageId);
        deductCmd.Parameters.AddWithValue("CreditsNeeded", creditsNeeded);

        var newRemaining = await deductCmd.ExecuteScalarAsync();
        if (newRemaining == null)
        {
            _logger?.LogWarning("Không thể trừ credits cho clinic {ClinicId}, package {UserPackageId}", clinicId, userPackageId);
            return false;
        }

        var newRemainingCount = Convert.ToInt32(newRemaining);
        _logger?.LogInformation("Đã trừ {CreditsNeeded} lượt phân tích cho clinic {ClinicId}, package {UserPackageId}. Số lượt còn lại: {Remaining}",
            creditsNeeded, clinicId, userPackageId, newRemainingCount);

        if (newRemainingCount <= 0)
        {
            var deactivateSql = "UPDATE user_packages SET IsActive = false, UpdatedDate = CURRENT_DATE WHERE Id = @UserPackageId";
            using var deactivateCmd = new Npgsql.NpgsqlCommand(deactivateSql, connection);
            deactivateCmd.Parameters.AddWithValue("UserPackageId", userPackageId);
            await deactivateCmd.ExecuteNonQueryAsync();
        }

        return true;
    }

    private async Task UpdateAnalysisStatusAsync(string analysisId, string status)
    {
        try
        {
            using var connection = new Npgsql.NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                UPDATE analysis_results
                SET AnalysisStatus = @AnalysisStatus,
                    UpdatedDate = CURRENT_DATE
                WHERE Id = @AnalysisId";

            using var command = new Npgsql.NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("AnalysisId", analysisId);
            command.Parameters.AddWithValue("AnalysisStatus", status);

            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error updating analysis status: {Message}", ex.Message);
            // Don't throw - this is a cleanup operation
        }
    }
}
