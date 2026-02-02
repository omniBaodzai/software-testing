using Aura.Application.DTOs.Doctors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aura.Application.Services.Doctors;

/// <summary>
/// Service implementation for patient search and filtering
/// </summary>
public class PatientSearchService : IPatientSearchService
{
    private readonly string _connectionString;
    private readonly ILogger<PatientSearchService>? _logger;

    public PatientSearchService(
        IConfiguration configuration,
        ILogger<PatientSearchService>? logger = null)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection") 
            ?? throw new InvalidOperationException("Database connection string not configured");
        _logger = logger;
    }

    public async Task<PatientSearchResponseDto> SearchPatientsAsync(
        string doctorId,
        PatientSearchDto searchDto)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Build WHERE clause
            // NOTE: We intentionally do NOT filter by DoctorId here so that
            // doctors có thể tìm tất cả bệnh nhân trong hệ thống.
            // Quan hệ assign doctor-patient (patient_doctor_assignments)
            // vẫn được LEFT JOIN để lấy thông tin AssignedAt/Clinic, nhưng
            // không giới hạn kết quả chỉ những bệnh nhân đã được assign.
            // IMPORTANT: Exclude doctors from results - only return actual patients
            var whereConditions = new List<string>
            {
                "COALESCE(u.IsDeleted, false) = false",
                "NOT EXISTS (SELECT 1 FROM doctors d WHERE d.Id = u.Id AND COALESCE(d.IsDeleted, false) = false)"
            };

            var parameters = new List<NpgsqlParameter>
            {
                new NpgsqlParameter("DoctorId", doctorId)
            };

            // Search query filter (ID, name, email, phone)
            if (!string.IsNullOrWhiteSpace(searchDto.SearchQuery))
            {
                whereConditions.Add(@"
                    (
                        LOWER(u.Id) LIKE @SearchQuery OR
                        LOWER(u.FirstName) LIKE @SearchQuery OR
                        LOWER(u.LastName) LIKE @SearchQuery OR
                        LOWER(u.Email) LIKE @SearchQuery OR
                        LOWER(u.Phone) LIKE @SearchQuery OR
                        LOWER(CONCAT(u.FirstName, ' ', u.LastName)) LIKE @SearchQuery
                    )");
                parameters.Add(new NpgsqlParameter("SearchQuery", $"%{searchDto.SearchQuery.ToLower()}%"));
            }

            // Risk level filter (dùng latest_risk CTE)
            if (!string.IsNullOrWhiteSpace(searchDto.RiskLevel))
            {
                whereConditions.Add("latest_risk.OverallRiskLevel = @RiskLevel");
                parameters.Add(new NpgsqlParameter("RiskLevel", searchDto.RiskLevel));
            }

            // Clinic filter (lọc theo clinic trong assignment nếu có)
            if (!string.IsNullOrWhiteSpace(searchDto.ClinicId))
            {
                whereConditions.Add("pda.ClinicId = @ClinicId");
                parameters.Add(new NpgsqlParameter("ClinicId", searchDto.ClinicId));
            }

            var whereClause = string.Join(" AND ", whereConditions);

            // Validate sort fields
            var validSortFields = new[] { "AssignedAt", "FirstName", "LastName", "Email", "LatestAnalysisDate", "LatestRiskLevel" };
            var sortBy = validSortFields.Contains(searchDto.SortBy, StringComparer.OrdinalIgnoreCase) 
                ? searchDto.SortBy 
                : "AssignedAt";
            var sortDirection = searchDto.SortDirection?.ToLower() == "asc" ? "ASC" : "DESC";

            // Build SQL query with latest risk level subquery
            // latest_risk: phân tích thuộc patient khi ar.UserId=patient hoặc ri.UserId=patient (clinic làm cho patient)
            var sql = $@"
                WITH patient_analyses AS (
                    SELECT ar.Id, ar.OverallRiskLevel, ar.RiskScore, ar.AnalysisCompletedAt,
                           CASE WHEN ri.UserId IN (SELECT Id FROM users) THEN ri.UserId ELSE ar.UserId END as PatientId
                    FROM analysis_results ar
                    INNER JOIN retinal_images ri ON ri.Id = ar.ImageId AND COALESCE(ri.IsDeleted, false) = false
                    WHERE ar.AnalysisStatus = 'Completed' AND COALESCE(ar.IsDeleted, false) = false
                ),
                latest_risk AS (
                    SELECT DISTINCT ON (PatientId) PatientId as UserId, OverallRiskLevel, RiskScore, AnalysisCompletedAt
                    FROM patient_analyses
                    WHERE PatientId IN (SELECT Id FROM users)
                    ORDER BY PatientId, AnalysisCompletedAt DESC NULLS LAST
                )
                SELECT 
                    u.Id, 
                    u.FirstName, 
                    u.LastName, 
                    u.Email, 
                    u.Phone, 
                    u.Dob, 
                    u.Gender, 
                    u.ProfileImageUrl,
                    pda.AssignedAt, 
                    pda.ClinicId, 
                    c.ClinicName,
                    (SELECT COUNT(DISTINCT ar.Id) FROM analysis_results ar
                     INNER JOIN retinal_images ri ON ri.Id = ar.ImageId AND COALESCE(ri.IsDeleted, false) = false
                     WHERE (ar.UserId = u.Id OR ri.UserId = u.Id) AND COALESCE(ar.IsDeleted, false) = false) as AnalysisCount,
                    (SELECT COUNT(*) FROM medical_notes mn2 
                     WHERE mn2.DoctorId = @DoctorId 
                       AND (mn2.PatientUserId = u.Id OR mn2.ResultId IN (
                         SELECT ar2.Id FROM analysis_results ar2 
                         INNER JOIN retinal_images ri2 ON ri2.Id = ar2.ImageId 
                         WHERE (ar2.UserId = u.Id OR ri2.UserId = u.Id)
                       ))
                       AND COALESCE(mn2.IsDeleted, false) = false) as MedicalNotesCount,
                    latest_risk.OverallRiskLevel as LatestRiskLevel,
                    latest_risk.RiskScore as LatestRiskScore,
                    latest_risk.AnalysisCompletedAt as LatestAnalysisDate
                FROM users u
                LEFT JOIN patient_doctor_assignments pda 
                    ON pda.UserId = u.Id 
                    AND pda.DoctorId = @DoctorId 
                    AND COALESCE(pda.IsDeleted, false) = false
                LEFT JOIN clinics c ON c.Id = pda.ClinicId
                LEFT JOIN latest_risk ON latest_risk.UserId = u.Id
                WHERE {whereClause}";

            // Add sorting
            sql += $" ORDER BY {sortBy} {sortDirection}";

            // Get total count - rebuild where clause without latest_risk reference for count
            // IMPORTANT: Exclude doctors from count - only count actual patients
            var countWhereConditions = new List<string>
            {
                "COALESCE(u.IsDeleted, false) = false",
                "NOT EXISTS (SELECT 1 FROM doctors d WHERE d.Id = u.Id AND COALESCE(d.IsDeleted, false) = false)"
            };

            var countParameters = new List<NpgsqlParameter>
            {
                new NpgsqlParameter("DoctorId", doctorId)
            };

            // Search query filter (ID, name, email, phone)
            if (!string.IsNullOrWhiteSpace(searchDto.SearchQuery))
            {
                countWhereConditions.Add(@"
                    (
                        LOWER(u.Id) LIKE @SearchQuery OR
                        LOWER(u.FirstName) LIKE @SearchQuery OR
                        LOWER(u.LastName) LIKE @SearchQuery OR
                        LOWER(u.Email) LIKE @SearchQuery OR
                        LOWER(u.Phone) LIKE @SearchQuery OR
                        LOWER(CONCAT(u.FirstName, ' ', u.LastName)) LIKE @SearchQuery
                    )");
                countParameters.Add(new NpgsqlParameter("SearchQuery", $"%{searchDto.SearchQuery.ToLower()}%"));
            }

            // Risk level filter - need to join with latest_risk CTE
            if (!string.IsNullOrWhiteSpace(searchDto.RiskLevel))
            {
                countWhereConditions.Add("latest_risk.OverallRiskLevel = @RiskLevel");
                countParameters.Add(new NpgsqlParameter("RiskLevel", searchDto.RiskLevel));
            }

            // Clinic filter
            if (!string.IsNullOrWhiteSpace(searchDto.ClinicId))
            {
                countWhereConditions.Add("pda.ClinicId = @ClinicId");
                countParameters.Add(new NpgsqlParameter("ClinicId", searchDto.ClinicId));
            }

            var countWhereClause = string.Join(" AND ", countWhereConditions);

            var countSql = $@"
                WITH patient_analyses AS (
                    SELECT ar.Id, ar.OverallRiskLevel, ar.AnalysisCompletedAt,
                           CASE WHEN ri.UserId IN (SELECT Id FROM users) THEN ri.UserId ELSE ar.UserId END as PatientId
                    FROM analysis_results ar
                    INNER JOIN retinal_images ri ON ri.Id = ar.ImageId AND COALESCE(ri.IsDeleted, false) = false
                    WHERE ar.AnalysisStatus = 'Completed' AND COALESCE(ar.IsDeleted, false) = false
                ),
                latest_risk AS (
                    SELECT DISTINCT ON (PatientId) PatientId as UserId, OverallRiskLevel
                    FROM patient_analyses
                    WHERE PatientId IN (SELECT Id FROM users)
                    ORDER BY PatientId, AnalysisCompletedAt DESC NULLS LAST
                )
                SELECT COUNT(DISTINCT u.Id)
                FROM users u
                LEFT JOIN patient_doctor_assignments pda 
                    ON pda.UserId = u.Id 
                    AND pda.DoctorId = @DoctorId 
                    AND COALESCE(pda.IsDeleted, false) = false
                LEFT JOIN latest_risk ON latest_risk.UserId = u.Id
                WHERE {countWhereClause}";

            using var countCommand = new NpgsqlCommand(countSql, connection);
            foreach (var param in countParameters)
            {
                countCommand.Parameters.Add(param);
            }

            var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync() ?? 0);

            // Add pagination
            var offset = (searchDto.Page - 1) * searchDto.PageSize;
            sql += $" LIMIT @PageSize OFFSET @Offset";
            parameters.Add(new NpgsqlParameter("PageSize", searchDto.PageSize));
            parameters.Add(new NpgsqlParameter("Offset", offset));

            // Execute main query
            using var command = new NpgsqlCommand(sql, connection);
            foreach (var param in parameters)
            {
                command.Parameters.Add(param);
            }

            var patients = new List<PatientSearchResultDto>();
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                patients.Add(new PatientSearchResultDto
                {
                    UserId = reader.GetString(0),
                    FirstName = reader.IsDBNull(1) ? null : reader.GetString(1),
                    LastName = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Email = reader.GetString(3),
                    Phone = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Dob = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                    Gender = reader.IsDBNull(6) ? null : reader.GetString(6),
                    ProfileImageUrl = reader.IsDBNull(7) ? null : reader.GetString(7),
                    AssignedAt = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                    ClinicId = reader.IsDBNull(9) ? null : reader.GetString(9),
                    ClinicName = reader.IsDBNull(10) ? null : reader.GetString(10),
                    AnalysisCount = reader.GetInt32(11),
                    MedicalNotesCount = reader.GetInt32(12),
                    LatestRiskLevel = reader.IsDBNull(13) ? null : reader.GetString(13),
                    LatestRiskScore = reader.IsDBNull(14) ? null : reader.GetDecimal(14),
                    LatestAnalysisDate = reader.IsDBNull(15) ? null : reader.GetDateTime(15)
                });
            }

            var totalPages = (int)Math.Ceiling(totalCount / (double)searchDto.PageSize);

            return new PatientSearchResponseDto
            {
                Patients = patients,
                TotalCount = totalCount,
                Page = searchDto.Page,
                PageSize = searchDto.PageSize,
                TotalPages = totalPages
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error searching patients for doctor {DoctorId}", doctorId);
            throw;
        }
    }
}
