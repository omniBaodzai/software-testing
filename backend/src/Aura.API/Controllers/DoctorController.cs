using Aura.Application.DTOs.Analysis;
using Aura.Application.DTOs.Doctors;
using Aura.Application.Services.Analysis;
using Aura.Application.Services.Doctors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Security.Claims;

namespace Aura.API.Controllers;

/// <summary>
/// Controller cho Doctor Dashboard và quản lý bệnh nhân (FR-13-21)
/// </summary>
[ApiController]
[Route("api/doctors")]
[Authorize]
[Produces("application/json")]
public class DoctorController : ControllerBase
{
    private readonly IAnalysisService _analysisService;
    private readonly IPatientSearchService _patientSearchService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DoctorController> _logger;
    private readonly string _connectionString;

    public DoctorController(
        IAnalysisService analysisService,
        IPatientSearchService patientSearchService,
        IConfiguration configuration,
        ILogger<DoctorController> logger)
    {
        _analysisService = analysisService ?? throw new ArgumentNullException(nameof(analysisService));
        _patientSearchService = patientSearchService ?? throw new ArgumentNullException(nameof(patientSearchService));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connectionString = _configuration.GetConnectionString("DefaultConnection") 
            ?? throw new InvalidOperationException("Database connection string not configured");
    }

    #region Doctor Profile

    /// <summary>
    /// Lấy thông tin profile của bác sĩ hiện tại
    /// </summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(DoctorDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetCurrentDoctor()
    {
        var doctorId = GetCurrentDoctorId();
        if (doctorId == null) return Unauthorized(new { message = "Chưa xác thực bác sĩ" });

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT Id, Username, FirstName, LastName, Email, Phone, Gender, 
                       LicenseNumber, Specialization, YearsOfExperience, Qualification,
                       HospitalAffiliation, ProfileImageUrl, Bio, IsVerified, IsActive, LastLoginAt
                FROM doctors
                WHERE Id = @DoctorId AND COALESCE(IsDeleted, false) = false";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("DoctorId", doctorId);

            using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return NotFound(new { message = "Không tìm thấy thông tin bác sĩ" });
            }

            var doctor = new DoctorDto
            {
                Id = reader.GetString(0),
                Username = reader.IsDBNull(1) ? null : reader.GetString(1),
                FirstName = reader.IsDBNull(2) ? null : reader.GetString(2),
                LastName = reader.IsDBNull(3) ? null : reader.GetString(3),
                Email = reader.GetString(4),
                Phone = reader.IsDBNull(5) ? null : reader.GetString(5),
                Gender = reader.IsDBNull(6) ? null : reader.GetString(6),
                LicenseNumber = reader.GetString(7),
                Specialization = reader.IsDBNull(8) ? null : reader.GetString(8),
                YearsOfExperience = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                Qualification = reader.IsDBNull(10) ? null : reader.GetString(10),
                HospitalAffiliation = reader.IsDBNull(11) ? null : reader.GetString(11),
                ProfileImageUrl = reader.IsDBNull(12) ? null : reader.GetString(12),
                Bio = reader.IsDBNull(13) ? null : reader.GetString(13),
                IsVerified = reader.GetBoolean(14),
                IsActive = reader.GetBoolean(15),
                LastLoginAt = reader.IsDBNull(16) ? null : reader.GetDateTime(16)
            };

            return Ok(doctor);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting doctor profile {DoctorId}", doctorId);
            return StatusCode(500, new { message = "Không thể lấy thông tin bác sĩ" });
        }
    }

    /// <summary>
    /// Cập nhật profile của bác sĩ hiện tại
    /// </summary>
    [HttpPut("me")]
    [ProducesResponseType(typeof(DoctorDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateCurrentDoctor([FromBody] UpdateDoctorProfileDto dto)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var doctorId = GetCurrentDoctorId();
        if (doctorId == null) return Unauthorized(new { message = "Chưa xác thực bác sĩ" });

        // Validate gender if provided
        if (!string.IsNullOrWhiteSpace(dto.Gender) && 
            !new[] { "Male", "Female", "Other" }.Contains(dto.Gender))
        {
            return BadRequest(new { message = "Gender phải là: Male, Female, hoặc Other" });
        }

        // Validate years of experience if provided
        if (dto.YearsOfExperience.HasValue && dto.YearsOfExperience.Value < 0)
        {
            return BadRequest(new { message = "YearsOfExperience không được âm" });
        }

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                UPDATE doctors
                SET FirstName = COALESCE(@FirstName, FirstName),
                    LastName = COALESCE(@LastName, LastName),
                    Phone = COALESCE(@Phone, Phone),
                    Gender = COALESCE(@Gender, Gender),
                    Specialization = COALESCE(@Specialization, Specialization),
                    YearsOfExperience = COALESCE(@YearsOfExperience, YearsOfExperience),
                    Qualification = COALESCE(@Qualification, Qualification),
                    HospitalAffiliation = COALESCE(@HospitalAffiliation, HospitalAffiliation),
                    ProfileImageUrl = COALESCE(@ProfileImageUrl, ProfileImageUrl),
                    Bio = COALESCE(@Bio, Bio),
                    UpdatedDate = CURRENT_DATE,
                    UpdatedBy = @DoctorId
                WHERE Id = @DoctorId AND COALESCE(IsDeleted, false) = false
                RETURNING Id, Username, FirstName, LastName, Email, Phone, Gender, 
                          LicenseNumber, Specialization, YearsOfExperience, Qualification,
                          HospitalAffiliation, ProfileImageUrl, Bio, IsVerified, IsActive, LastLoginAt";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("DoctorId", doctorId);
            command.Parameters.AddWithValue("FirstName", (object?)dto.FirstName ?? DBNull.Value);
            command.Parameters.AddWithValue("LastName", (object?)dto.LastName ?? DBNull.Value);
            command.Parameters.AddWithValue("Phone", (object?)dto.Phone ?? DBNull.Value);
            command.Parameters.AddWithValue("Gender", (object?)dto.Gender ?? DBNull.Value);
            command.Parameters.AddWithValue("Specialization", (object?)dto.Specialization ?? DBNull.Value);
            command.Parameters.AddWithValue("YearsOfExperience", (object?)dto.YearsOfExperience ?? DBNull.Value);
            command.Parameters.AddWithValue("Qualification", (object?)dto.Qualification ?? DBNull.Value);
            command.Parameters.AddWithValue("HospitalAffiliation", (object?)dto.HospitalAffiliation ?? DBNull.Value);
            command.Parameters.AddWithValue("ProfileImageUrl", (object?)dto.ProfileImageUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("Bio", (object?)dto.Bio ?? DBNull.Value);

            using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return NotFound(new { message = "Không tìm thấy thông tin bác sĩ" });
            }

            var doctor = new DoctorDto
            {
                Id = reader.GetString(0),
                Username = reader.IsDBNull(1) ? null : reader.GetString(1),
                FirstName = reader.IsDBNull(2) ? null : reader.GetString(2),
                LastName = reader.IsDBNull(3) ? null : reader.GetString(3),
                Email = reader.GetString(4),
                Phone = reader.IsDBNull(5) ? null : reader.GetString(5),
                Gender = reader.IsDBNull(6) ? null : reader.GetString(6),
                LicenseNumber = reader.GetString(7),
                Specialization = reader.IsDBNull(8) ? null : reader.GetString(8),
                YearsOfExperience = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                Qualification = reader.IsDBNull(10) ? null : reader.GetString(10),
                HospitalAffiliation = reader.IsDBNull(11) ? null : reader.GetString(11),
                ProfileImageUrl = reader.IsDBNull(12) ? null : reader.GetString(12),
                Bio = reader.IsDBNull(13) ? null : reader.GetString(13),
                IsVerified = reader.GetBoolean(14),
                IsActive = reader.GetBoolean(15),
                LastLoginAt = reader.IsDBNull(16) ? null : reader.GetDateTime(16)
            };

            _logger.LogInformation("Doctor profile updated: {DoctorId}", doctorId);
            return Ok(doctor);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating doctor profile {DoctorId}", doctorId);
            return StatusCode(500, new { message = "Không thể cập nhật thông tin bác sĩ" });
        }
    }

    /// <summary>
    /// Lấy thống kê của bác sĩ
    /// </summary>
    [HttpGet("statistics")]
    [ProducesResponseType(typeof(DoctorStatisticsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetStatistics()
    {
        var doctorId = GetCurrentDoctorId();
        if (doctorId == null) return Unauthorized(new { message = "Chưa xác thực bác sĩ" });

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Cải thiện SQL query để tính chính xác hơn:
            // 1. TotalPatients: Đếm số bệnh nhân được assign cho bác sĩ này
            // 2. ActiveAssignments: Đếm số bệnh nhân đang active
            // 3. TotalAnalyses: Đếm tất cả phân tích của các bệnh nhân được assign
            //    - ar.UserId = patient (patient tự phân tích) HOẶC ri.UserId = patient (clinic phân tích cho patient)
            // 4. PendingAnalyses: Đếm phân tích có status = 'Pending'
            // 5. MedicalNotesCount: Đếm tất cả ghi chú y tế của bác sĩ này
            var sql = @"
                WITH assigned_patients AS (
                    SELECT DISTINCT UserId
                    FROM patient_doctor_assignments
                    WHERE DoctorId = @DoctorId 
                        AND COALESCE(IsDeleted, false) = false
                ),
                active_assigned_patients AS (
                    SELECT DISTINCT UserId
                    FROM patient_doctor_assignments
                    WHERE DoctorId = @DoctorId 
                        AND IsActive = true
                        AND COALESCE(IsDeleted, false) = false
                )
                SELECT 
                    (SELECT COUNT(*) FROM assigned_patients) as TotalPatients,
                    (SELECT COUNT(*) FROM active_assigned_patients) as ActiveAssignments,
                    COALESCE((
                        SELECT COUNT(DISTINCT ar.Id)
                        FROM analysis_results ar
                        INNER JOIN retinal_images ri ON ri.Id = ar.ImageId AND COALESCE(ri.IsDeleted, false) = false
                        INNER JOIN assigned_patients ap ON (ap.UserId = ar.UserId OR ap.UserId = ri.UserId)
                        WHERE COALESCE(ar.IsDeleted, false) = false
                    ), 0) as TotalAnalyses,
                    COALESCE((
                        SELECT COUNT(DISTINCT ar.Id)
                        FROM analysis_results ar
                        INNER JOIN retinal_images ri ON ri.Id = ar.ImageId AND COALESCE(ri.IsDeleted, false) = false
                        INNER JOIN assigned_patients ap ON (ap.UserId = ar.UserId OR ap.UserId = ri.UserId)
                        WHERE ar.AnalysisStatus = 'Pending'
                            AND COALESCE(ar.IsDeleted, false) = false
                    ), 0) as PendingAnalyses,
                    COALESCE((
                        SELECT COUNT(DISTINCT mn.Id)
                        FROM medical_notes mn
                        WHERE mn.DoctorId = @DoctorId
                            AND COALESCE(mn.IsDeleted, false) = false
                    ), 0) as MedicalNotesCount,
                    GREATEST(
                        COALESCE((
                            SELECT MAX(pda.AssignedAt)
                            FROM patient_doctor_assignments pda
                            WHERE pda.DoctorId = @DoctorId
                                AND COALESCE(pda.IsDeleted, false) = false
                        ), '1970-01-01'::timestamp),
                        COALESCE((
                            SELECT MAX(ar.CreatedDate)
                            FROM analysis_results ar
                            INNER JOIN retinal_images ri ON ri.Id = ar.ImageId AND COALESCE(ri.IsDeleted, false) = false
                            INNER JOIN assigned_patients ap ON (ap.UserId = ar.UserId OR ap.UserId = ri.UserId)
                            WHERE COALESCE(ar.IsDeleted, false) = false
                        ), '1970-01-01'::timestamp),
                        COALESCE((
                            SELECT MAX(mn.CreatedDate)
                            FROM medical_notes mn
                            WHERE mn.DoctorId = @DoctorId
                                AND COALESCE(mn.IsDeleted, false) = false
                        ), '1970-01-01'::timestamp)
                    ) as LastActivityDate";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("DoctorId", doctorId);

            using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return Ok(new DoctorStatisticsDto());
            }

            var statistics = new DoctorStatisticsDto
            {
                TotalPatients = reader.GetInt32(0),
                ActiveAssignments = reader.GetInt32(1),
                TotalAnalyses = reader.GetInt32(2),
                PendingAnalyses = reader.GetInt32(3),
                MedicalNotesCount = reader.GetInt32(4),
                LastActivityDate = reader.IsDBNull(5) ? null : reader.GetDateTime(5)
            };

            _logger.LogInformation("Doctor statistics retrieved for {DoctorId}: Patients={TotalPatients}, Analyses={TotalAnalyses}, Pending={PendingAnalyses}, Notes={MedicalNotesCount}",
                doctorId, statistics.TotalPatients, statistics.TotalAnalyses, statistics.PendingAnalyses, statistics.MedicalNotesCount);

            return Ok(statistics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting doctor statistics {DoctorId}", doctorId);
            return StatusCode(500, new { message = "Không thể lấy thống kê" });
        }
    }

    #endregion

    #region Patients Management

    /// <summary>
    /// Tìm kiếm và lọc bệnh nhân theo ID, tên, email và mức độ rủi ro (FR-18)
    /// </summary>
    /// <param name="searchQuery">Từ khóa tìm kiếm (ID, tên, email)</param>
    /// <param name="riskLevel">Lọc theo mức độ rủi ro (Low, Medium, High, Critical)</param>
    /// <param name="clinicId">Lọc theo clinic ID</param>
    /// <param name="page">Số trang (mặc định: 1)</param>
    /// <param name="pageSize">Số lượng mỗi trang (mặc định: 20)</param>
    /// <param name="sortBy">Sắp xếp theo (AssignedAt, FirstName, LastName, Email, LatestAnalysisDate, LatestRiskLevel)</param>
    /// <param name="sortDirection">Hướng sắp xếp (asc/desc, mặc định: desc)</param>
    /// <returns>Danh sách bệnh nhân với phân trang</returns>
    [HttpGet("patients/search")]
    [ProducesResponseType(typeof(PatientSearchResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SearchPatients(
        [FromQuery] string? searchQuery = null,
        [FromQuery] string? riskLevel = null,
        [FromQuery] string? clinicId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDirection = null)
    {
        var doctorId = GetCurrentDoctorId();
        if (doctorId == null) return Unauthorized(new { message = "Chưa xác thực bác sĩ" });

        try
        {
            var searchDto = new PatientSearchDto
            {
                SearchQuery = searchQuery,
                RiskLevel = riskLevel,
                ClinicId = clinicId,
                Page = page > 0 ? page : 1,
                PageSize = pageSize > 0 && pageSize <= 100 ? pageSize : 20,
                SortBy = sortBy,
                SortDirection = sortDirection
            };

            var result = await _patientSearchService.SearchPatientsAsync(doctorId, searchDto);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching patients for doctor {DoctorId}", doctorId);
            return StatusCode(500, new { message = "Không thể tìm kiếm bệnh nhân" });
        }
    }

    /// <summary>
    /// Lấy danh sách bệnh nhân được assign cho bác sĩ
    /// </summary>
    /// <param name="activeOnly">Chỉ lấy bệnh nhân đang active (mặc định: true)</param>
    /// <returns>Danh sách bệnh nhân</returns>
    [HttpGet("patients")]
    [ProducesResponseType(typeof(List<PatientListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetPatients([FromQuery] bool? activeOnly = true)
    {
        var doctorId = GetCurrentDoctorId();
        if (doctorId == null) return Unauthorized(new { message = "Chưa xác thực bác sĩ" });

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT 
                    u.Id, u.FirstName, u.LastName, u.Email, u.Phone, u.Dob, u.Gender, u.ProfileImageUrl,
                    pda.AssignedAt, pda.ClinicId, c.ClinicName,
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
                       AND COALESCE(mn2.IsDeleted, false) = false) as MedicalNotesCount
                FROM patient_doctor_assignments pda
                INNER JOIN users u ON u.Id = pda.UserId
                LEFT JOIN clinics c ON c.Id = pda.ClinicId
                WHERE pda.DoctorId = @DoctorId 
                    AND COALESCE(pda.IsDeleted, false) = false
                    AND COALESCE(u.IsDeleted, false) = false
                    AND (@ActiveOnly IS NULL OR pda.IsActive = @ActiveOnly)
                ORDER BY pda.AssignedAt DESC";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("DoctorId", doctorId);
            command.Parameters.AddWithValue("ActiveOnly", (object?)activeOnly ?? DBNull.Value);

            var patients = new List<PatientListItemDto>();
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                patients.Add(new PatientListItemDto
                {
                    UserId = reader.GetString(0),
                    FirstName = reader.IsDBNull(1) ? null : reader.GetString(1),
                    LastName = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Email = reader.GetString(3),
                    Phone = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Dob = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                    Gender = reader.IsDBNull(6) ? null : reader.GetString(6),
                    ProfileImageUrl = reader.IsDBNull(7) ? null : reader.GetString(7),
                    AssignedAt = reader.GetDateTime(8),
                    ClinicId = reader.IsDBNull(9) ? null : reader.GetString(9),
                    ClinicName = reader.IsDBNull(10) ? null : reader.GetString(10),
                    AnalysisCount = reader.GetInt32(11),
                    MedicalNotesCount = reader.GetInt32(12)
                });
            }

            return Ok(patients);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting patients for doctor {DoctorId}", doctorId);
            return StatusCode(500, new { message = "Không thể lấy danh sách bệnh nhân" });
        }
    }

    /// <summary>
    /// Lấy thông tin chi tiết của một bệnh nhân
    /// </summary>
    [HttpGet("patients/{patientId}")]
    [ProducesResponseType(typeof(PatientListItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetPatient(string patientId)
    {
        var doctorId = GetCurrentDoctorId();
        if (doctorId == null) return Unauthorized(new { message = "Chưa xác thực bác sĩ" });

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Cho phép bác sĩ xem thông tin bất kỳ bệnh nhân nào,
            // nhưng chỉ hiển thị thông tin assign/ghi chú liên quan tới chính bác sĩ đó (nếu có).
            // AnalysisCount: phân tích của patient (ar.UserId=patient hoặc ri.UserId=patient khi clinic làm)
            var sql = @"
                SELECT 
                    u.Id, u.FirstName, u.LastName, u.Email, u.Phone, u.Dob, u.Gender, u.ProfileImageUrl,
                    pda.AssignedAt, pda.ClinicId, c.ClinicName,
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
                       AND COALESCE(mn2.IsDeleted, false) = false) as MedicalNotesCount
                FROM users u
                LEFT JOIN patient_doctor_assignments pda 
                    ON pda.UserId = u.Id 
                    AND pda.DoctorId = @DoctorId
                    AND COALESCE(pda.IsDeleted, false) = false
                LEFT JOIN clinics c ON c.Id = pda.ClinicId
                WHERE u.Id = @PatientId
                    AND COALESCE(u.IsDeleted, false) = false";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("DoctorId", doctorId);
            command.Parameters.AddWithValue("PatientId", patientId);

            using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return NotFound(new { message = "Không tìm thấy bệnh nhân" });
            }

            var patient = new PatientListItemDto
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
                MedicalNotesCount = reader.GetInt32(12)
            };

            return Ok(patient);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting patient {PatientId} for doctor {DoctorId}", patientId, doctorId);
            return StatusCode(500, new { message = "Không thể lấy thông tin bệnh nhân" });
        }
    }

    #endregion

    #region Analyses Management

    /// <summary>
    /// Lấy danh sách kết quả phân tích của các bệnh nhân được assign.
    /// Khi bác sĩ chưa có assignment nào: fallback lấy tất cả phân tích từ analysis_results.
    /// UI Quản lý phân tích tính: Tổng phân tích, Chờ xác nhận, Đã xác nhận, Rủi ro cao.
    /// </summary>
    [HttpGet("analyses")]
    [ProducesResponseType(typeof(List<DoctorAnalysisListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAnalyses([FromQuery] string? patientId = null)
    {
        var doctorId = GetCurrentDoctorId();
        if (doctorId == null) return Unauthorized(new { message = "Chưa xác thực bác sĩ" });

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            string sql;
            var hasSpecificPatient = !string.IsNullOrEmpty(patientId);

            if (hasSpecificPatient)
            {
                // When specific patientId is provided, filter by that patient only
                // Include analyses where ar.UserId=patient (patient's own) OR ri.UserId=patient (clinic did for patient)
                sql = @"
                    SELECT ar.Id, ar.ImageId, @PatientId AS UserId,
                           (SELECT COALESCE(FirstName || ' ' || LastName, Email) FROM users WHERE Id = @PatientId) AS PatientName,
                           ar.AnalysisStatus, ar.OverallRiskLevel, ar.RiskScore,
                           COALESCE(ar.DiabeticRetinopathyDetected, false),
                           ar.AiConfidenceScore, ar.AnalysisCompletedAt,
                           COALESCE(ar.AnalysisStartedAt, ar.CreatedDate::timestamp) AS CreatedAt,
                           (af.Id IS NOT NULL) AS IsValidated,
                           af.CreatedBy AS ValidatedBy,
                           af.CreatedDate::timestamp AS ValidatedAt
                    FROM analysis_results ar
                    INNER JOIN retinal_images ri ON ri.Id = ar.ImageId AND COALESCE(ri.IsDeleted, false) = false
                    LEFT JOIN ai_feedback af ON af.ResultId = ar.Id AND af.DoctorId = @DoctorId AND COALESCE(af.IsDeleted, false) = false
                    WHERE COALESCE(ar.IsDeleted, false) = false
                      AND (ar.UserId = @PatientId OR ri.UserId = @PatientId)
                    ORDER BY ar.AnalysisCompletedAt DESC NULLS LAST, ar.AnalysisStartedAt DESC NULLS LAST, ar.CreatedDate DESC NULLS LAST";
            }
            else
            {
                // When no patientId, get assigned patients first
                var patientIds = new List<string>();
                var patientsSql = @"
                    SELECT DISTINCT UserId FROM patient_doctor_assignments
                    WHERE DoctorId = @DoctorId 
                        AND COALESCE(IsDeleted, false) = false AND IsActive = true";
                using var patientsCmd = new NpgsqlCommand(patientsSql, connection);
                patientsCmd.Parameters.AddWithValue("DoctorId", doctorId);
                using var r = await patientsCmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    patientIds.Add(r.GetString(0));
                await r.CloseAsync();

                if (patientIds.Count > 0)
                {
                    sql = @"
                        SELECT ar.Id, ar.ImageId, 
                               (CASE WHEN ri.UserId = ANY(@PatientIds) THEN ri.UserId ELSE ar.UserId END) AS UserId,
                               COALESCE(up.FirstName || ' ' || up.LastName, up.Email) AS PatientName,
                               ar.AnalysisStatus, ar.OverallRiskLevel, ar.RiskScore,
                               COALESCE(ar.DiabeticRetinopathyDetected, false),
                               ar.AiConfidenceScore, ar.AnalysisCompletedAt,
                               COALESCE(ar.AnalysisStartedAt, ar.CreatedDate::timestamp) AS CreatedAt,
                               (af.Id IS NOT NULL) AS IsValidated,
                               af.CreatedBy AS ValidatedBy,
                               af.CreatedDate::timestamp AS ValidatedAt
                        FROM analysis_results ar
                        INNER JOIN retinal_images ri ON ri.Id = ar.ImageId AND COALESCE(ri.IsDeleted, false) = false
                        LEFT JOIN users up ON up.Id = CASE WHEN ri.UserId = ANY(@PatientIds) THEN ri.UserId ELSE ar.UserId END AND COALESCE(up.IsDeleted, false) = false
                        LEFT JOIN ai_feedback af ON af.ResultId = ar.Id AND af.DoctorId = @DoctorId AND COALESCE(af.IsDeleted, false) = false
                        WHERE COALESCE(ar.IsDeleted, false) = false
                          AND (ar.UserId = ANY(@PatientIds) OR ri.UserId = ANY(@PatientIds))
                        ORDER BY ar.AnalysisCompletedAt DESC NULLS LAST, ar.AnalysisStartedAt DESC NULLS LAST, ar.CreatedDate DESC NULLS LAST";
                }
                else
                {
                    _logger.LogInformation("Doctor {DoctorId} has no assignments; fallback to all analyses.", doctorId);
                    sql = @"
                        SELECT ar.Id, ar.ImageId, ar.UserId,
                               COALESCE(u.FirstName || ' ' || u.LastName, u.Email) AS PatientName,
                               ar.AnalysisStatus, ar.OverallRiskLevel, ar.RiskScore,
                               COALESCE(ar.DiabeticRetinopathyDetected, false),
                               ar.AiConfidenceScore, ar.AnalysisCompletedAt,
                               COALESCE(ar.AnalysisStartedAt, ar.CreatedDate::timestamp) AS CreatedAt,
                               (af.Id IS NOT NULL) AS IsValidated,
                               af.CreatedBy AS ValidatedBy,
                               af.CreatedDate::timestamp AS ValidatedAt
                        FROM analysis_results ar
                        INNER JOIN retinal_images ri ON ri.Id = ar.ImageId AND COALESCE(ri.IsDeleted, false) = false
                        INNER JOIN users u ON u.Id = ar.UserId AND COALESCE(u.IsDeleted, false) = false
                        LEFT JOIN ai_feedback af ON af.ResultId = ar.Id AND af.DoctorId = @DoctorId AND COALESCE(af.IsDeleted, false) = false
                        WHERE COALESCE(ar.IsDeleted, false) = false
                        ORDER BY ar.AnalysisCompletedAt DESC NULLS LAST, ar.AnalysisStartedAt DESC NULLS LAST, ar.CreatedDate DESC NULLS LAST";
                }
            }

            using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("DoctorId", doctorId);
            if (hasSpecificPatient)
            {
                cmd.Parameters.AddWithValue("PatientId", patientId!);
            }
            else
            {
                // Re-query patient IDs for parameter (need to do this again since reader was closed)
                var patientIds = new List<string>();
                var patientsSql = @"
                    SELECT DISTINCT UserId FROM patient_doctor_assignments
                    WHERE DoctorId = @DoctorId 
                        AND COALESCE(IsDeleted, false) = false AND IsActive = true";
                using var patientsCmd = new NpgsqlCommand(patientsSql, connection);
                patientsCmd.Parameters.AddWithValue("DoctorId", doctorId);
                using var r = await patientsCmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    patientIds.Add(r.GetString(0));
                await r.CloseAsync();

                if (patientIds.Count > 0)
                {
                    var patientIdsParam = new NpgsqlParameter("PatientIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text);
                    patientIdsParam.Value = patientIds.ToArray();
                    cmd.Parameters.Add(patientIdsParam);
                }
            }

            var list = new List<DoctorAnalysisListItemDto>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new DoctorAnalysisListItemDto
                {
                    Id = reader.GetString(0),
                    ImageId = reader.GetString(1),
                    PatientUserId = reader.GetString(2),
                    PatientName = reader.IsDBNull(3) ? null : reader.GetString(3),
                    AnalysisStatus = reader.GetString(4),
                    OverallRiskLevel = reader.IsDBNull(5) ? null : reader.GetString(5),
                    RiskScore = reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                    DiabeticRetinopathyDetected = reader.GetBoolean(7),
                    AiConfidenceScore = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                    AnalysisCompletedAt = reader.IsDBNull(9) ? null : reader.GetDateTime(9),
                    CreatedAt = reader.IsDBNull(10) ? null : reader.GetDateTime(10),
                    IsValidated = reader.GetBoolean(11),
                    ValidatedBy = reader.IsDBNull(12) ? null : reader.GetString(12),
                    ValidatedAt = reader.IsDBNull(13) ? null : reader.GetDateTime(13)
                });
            }

            return Ok(list);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting analyses for doctor {DoctorId}", doctorId);
            return StatusCode(500, new { message = "Không thể lấy danh sách phân tích" });
        }
    }

    /// <summary>
    /// Lấy chi tiết một kết quả phân tích.
    /// Khi bác sĩ chưa có assignment: cho phép xem bất kỳ phân tích nào (fallback).
    /// </summary>
    [HttpGet("analyses/{analysisId}")]
    [ProducesResponseType(typeof(AnalysisResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAnalysis(string analysisId)
    {
        var doctorId = GetCurrentDoctorId();
        if (doctorId == null) return Unauthorized(new { message = "Chưa xác thực bác sĩ" });

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            string? userId = null;
            var hasAssignments = false;

            var countSql = @"
                SELECT EXISTS(
                    SELECT 1 FROM patient_doctor_assignments
                    WHERE DoctorId = @DoctorId AND COALESCE(IsDeleted, false) = false AND IsActive = true
                )";
            using (var countCmd = new NpgsqlCommand(countSql, connection))
            {
                countCmd.Parameters.AddWithValue("DoctorId", doctorId);
                hasAssignments = (bool)(await countCmd.ExecuteScalarAsync() ?? false);
            }

            if (hasAssignments)
            {
                var sql = @"
                    SELECT COALESCE(ri.UserId, ar.UserId) FROM analysis_results ar
                    INNER JOIN retinal_images ri ON ri.Id = ar.ImageId AND COALESCE(ri.IsDeleted, false) = false
                    INNER JOIN patient_doctor_assignments pda ON (pda.UserId = ar.UserId OR pda.UserId = ri.UserId)
                    WHERE ar.Id = @AnalysisId 
                        AND pda.DoctorId = @DoctorId
                        AND COALESCE(pda.IsDeleted, false) = false
                        AND pda.IsActive = true
                        AND COALESCE(ar.IsDeleted, false) = false";
                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("AnalysisId", analysisId);
                cmd.Parameters.AddWithValue("DoctorId", doctorId);
                userId = await cmd.ExecuteScalarAsync() as string;
            }
            else
            {
                var fallbackSql = @"
                    SELECT ar.UserId FROM analysis_results ar
                    INNER JOIN retinal_images ri ON ri.Id = ar.ImageId AND COALESCE(ri.IsDeleted, false) = false
                    WHERE ar.Id = @AnalysisId AND COALESCE(ar.IsDeleted, false) = false";
                using var cmd = new NpgsqlCommand(fallbackSql, connection);
                cmd.Parameters.AddWithValue("AnalysisId", analysisId);
                userId = await cmd.ExecuteScalarAsync() as string;
            }

            if (string.IsNullOrEmpty(userId))
            {
                return NotFound(new { message = "Không tìm thấy kết quả phân tích hoặc không có quyền truy cập" });
            }

            var analysis = await _analysisService.GetAnalysisResultAsync(analysisId, userId);
            if (analysis == null)
                return NotFound(new { message = "Không tìm thấy kết quả phân tích" });

            return Ok(analysis);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting analysis {AnalysisId} for doctor {DoctorId}", analysisId, doctorId);
            return StatusCode(500, new { message = "Không thể lấy thông tin phân tích" });
        }
    }

    #endregion

    #region Validate/Correct AI Findings (FR-15)

    /// <summary>
    /// Validate hoặc correct AI findings (FR-15)
    /// </summary>
    [HttpPost("analyses/{analysisId}/validate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ValidateFindings(string analysisId, [FromBody] ValidateFindingsDto dto)
    {
        if (dto.AnalysisId != analysisId)
        {
            return BadRequest(new { message = "AnalysisId không khớp" });
        }

        var doctorId = GetCurrentDoctorId();
        if (doctorId == null) return Unauthorized(new { message = "Chưa xác thực bác sĩ" });

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Verify analysis exists and doctor can access
            // Allow access if: doctor is assigned to patient OR doctor is in same clinic as analysis OR analysis exists (for general doctor access)
            var verifySql = @"
                SELECT ar.Id FROM analysis_results ar
                INNER JOIN retinal_images ri ON ri.Id = ar.ImageId AND COALESCE(ri.IsDeleted, false) = false
                WHERE ar.Id = @AnalysisId 
                    AND COALESCE(ar.IsDeleted, false) = false
                    AND (
                        -- Doctor is assigned to the patient
                        EXISTS (
                            SELECT 1 FROM patient_doctor_assignments pda 
                            WHERE pda.UserId = ar.UserId 
                            AND pda.DoctorId = @DoctorId
                            AND COALESCE(pda.IsDeleted, false) = false
                            AND pda.IsActive = true
                        )
                        OR 
                        -- Doctor is in the same clinic as the image/analysis (via clinic_doctors table)
                        EXISTS (
                            SELECT 1 FROM clinic_doctors cd 
                            WHERE cd.DoctorId = @DoctorId 
                            AND cd.ClinicId = ri.ClinicId
                            AND COALESCE(cd.IsDeleted, false) = false
                            AND cd.IsActive = true
                        )
                        OR
                        -- Any authenticated doctor can validate (for demo/testing)
                        EXISTS (SELECT 1 FROM doctors WHERE Id = @DoctorId AND COALESCE(IsDeleted, false) = false)
                    )";

            using var verifyCmd = new NpgsqlCommand(verifySql, connection);
            verifyCmd.Parameters.AddWithValue("AnalysisId", analysisId);
            verifyCmd.Parameters.AddWithValue("DoctorId", doctorId);

            var hasAccess = await verifyCmd.ExecuteScalarAsync();
            if (hasAccess == null)
            {
                return NotFound(new { message = "Không tìm thấy kết quả phân tích hoặc không có quyền truy cập" });
            }

            // Cập nhật trạng thái phân tích theo lựa chọn bác sĩ
            if (dto.ValidationStatus == "Corrected")
            {
                // "Cần sửa đổi" -> Đang chờ xử lý (Pending)
                var statusUpdateSql = @"
                    UPDATE analysis_results
                    SET AnalysisStatus = 'Pending',
                        Note = COALESCE(@ValidationNotes, Note),
                        UpdatedDate = CURRENT_DATE
                    WHERE Id = @AnalysisId";
                using var statusUpdateCmd = new NpgsqlCommand(statusUpdateSql, connection);
                statusUpdateCmd.Parameters.AddWithValue("AnalysisId", analysisId);
                statusUpdateCmd.Parameters.AddWithValue("ValidationNotes", (object?)dto.ValidationNotes ?? DBNull.Value);
                await statusUpdateCmd.ExecuteNonQueryAsync();
            }
            else if (dto.ValidationStatus == "Validated")
            {
                // "Kết quả chính xác" -> Đã xác nhận (Completed)
                var statusUpdateSql = @"
                    UPDATE analysis_results
                    SET AnalysisStatus = 'Completed',
                        Note = COALESCE(@ValidationNotes, Note),
                        UpdatedDate = CURRENT_DATE
                    WHERE Id = @AnalysisId";
                using var statusUpdateCmd = new NpgsqlCommand(statusUpdateSql, connection);
                statusUpdateCmd.Parameters.AddWithValue("AnalysisId", analysisId);
                statusUpdateCmd.Parameters.AddWithValue("ValidationNotes", (object?)dto.ValidationNotes ?? DBNull.Value);
                await statusUpdateCmd.ExecuteNonQueryAsync();
            }

            // Update analysis_results with corrected risk/note data if provided (chỉ khi Cần sửa đổi + có sửa mức rủi ro)
            if (dto.ValidationStatus == "Corrected" && 
                (dto.CorrectedRiskLevel != null || dto.CorrectedRiskScore.HasValue))
            {
                var updateSql = @"
                    UPDATE analysis_results
                    SET OverallRiskLevel = COALESCE(@CorrectedRiskLevel, OverallRiskLevel),
                        RiskScore = COALESCE(@CorrectedRiskScore, RiskScore),
                        HypertensionRisk = COALESCE(@CorrectedHypertensionRisk, HypertensionRisk),
                        DiabetesRisk = COALESCE(@CorrectedDiabetesRisk, DiabetesRisk),
                        StrokeRisk = COALESCE(@CorrectedStrokeRisk, StrokeRisk),
                        DiabeticRetinopathyDetected = COALESCE(@CorrectedDiabeticRetinopathyDetected, DiabeticRetinopathyDetected),
                        DiabeticRetinopathySeverity = COALESCE(@CorrectedDiabeticRetinopathySeverity, DiabeticRetinopathySeverity),
                        UpdatedDate = CURRENT_DATE,
                        Note = COALESCE(@ValidationNotes, Note)
                    WHERE Id = @AnalysisId";

                using var updateCmd = new NpgsqlCommand(updateSql, connection);
                updateCmd.Parameters.AddWithValue("AnalysisId", analysisId);
                updateCmd.Parameters.AddWithValue("CorrectedRiskLevel", (object?)dto.CorrectedRiskLevel ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("CorrectedRiskScore", (object?)dto.CorrectedRiskScore ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("CorrectedHypertensionRisk", (object?)dto.CorrectedHypertensionRisk ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("CorrectedDiabetesRisk", (object?)dto.CorrectedDiabetesRisk ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("CorrectedStrokeRisk", (object?)dto.CorrectedStrokeRisk ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("CorrectedDiabeticRetinopathyDetected", (object?)dto.CorrectedDiabeticRetinopathyDetected ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("CorrectedDiabeticRetinopathySeverity", (object?)dto.CorrectedDiabeticRetinopathySeverity ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("ValidationNotes", (object?)dto.ValidationNotes ?? DBNull.Value);

                await updateCmd.ExecuteNonQueryAsync();
            }

            // Create or update AI feedback record
            var feedbackSql = @"
                SELECT Id FROM ai_feedback 
                WHERE ResultId = @ResultId AND DoctorId = @DoctorId AND COALESCE(IsDeleted, false) = false
                LIMIT 1";

            using var feedbackCheckCmd = new NpgsqlCommand(feedbackSql, connection);
            feedbackCheckCmd.Parameters.AddWithValue("ResultId", analysisId);
            feedbackCheckCmd.Parameters.AddWithValue("DoctorId", doctorId);

            var existingFeedbackId = await feedbackCheckCmd.ExecuteScalarAsync() as string;

            if (string.IsNullOrEmpty(existingFeedbackId))
            {
                // Create new feedback
                var feedbackId = Guid.NewGuid().ToString();
                var insertFeedbackSql = @"
                    INSERT INTO ai_feedback
                    (Id, ResultId, DoctorId, FeedbackType, OriginalRiskLevel, CorrectedRiskLevel, 
                     FeedbackNotes, IsUsedForTraining, CreatedDate, CreatedBy, IsDeleted)
                    VALUES
                    (@Id, @ResultId, @DoctorId, @FeedbackType, @OriginalRiskLevel, @CorrectedRiskLevel,
                     @FeedbackNotes, @IsUsedForTraining, @CreatedDate, @CreatedBy, false)";

                using var insertFeedbackCmd = new NpgsqlCommand(insertFeedbackSql, connection);
                insertFeedbackCmd.Parameters.AddWithValue("Id", feedbackId);
                insertFeedbackCmd.Parameters.AddWithValue("ResultId", analysisId);
                insertFeedbackCmd.Parameters.AddWithValue("DoctorId", doctorId);
                insertFeedbackCmd.Parameters.AddWithValue("FeedbackType", dto.ValidationStatus == "Corrected" ? "Incorrect" : "Correct");
                insertFeedbackCmd.Parameters.AddWithValue("OriginalRiskLevel", DBNull.Value); // Can be populated from analysis_results
                insertFeedbackCmd.Parameters.AddWithValue("CorrectedRiskLevel", (object?)dto.CorrectedRiskLevel ?? DBNull.Value);
                insertFeedbackCmd.Parameters.AddWithValue("FeedbackNotes", (object?)dto.ValidationNotes ?? DBNull.Value);
                insertFeedbackCmd.Parameters.AddWithValue("IsUsedForTraining", dto.ValidationStatus == "Corrected");
                insertFeedbackCmd.Parameters.AddWithValue("CreatedDate", DateTime.UtcNow.Date);
                insertFeedbackCmd.Parameters.AddWithValue("CreatedBy", doctorId);

                await insertFeedbackCmd.ExecuteNonQueryAsync();
            }
            else
            {
                // Update existing feedback
                var updateFeedbackSql = @"
                    UPDATE ai_feedback
                    SET FeedbackType = @FeedbackType,
                        CorrectedRiskLevel = COALESCE(@CorrectedRiskLevel, CorrectedRiskLevel),
                        FeedbackNotes = COALESCE(@FeedbackNotes, FeedbackNotes),
                        IsUsedForTraining = @IsUsedForTraining,
                        UpdatedDate = CURRENT_DATE
                    WHERE Id = @FeedbackId";

                using var updateFeedbackCmd = new NpgsqlCommand(updateFeedbackSql, connection);
                updateFeedbackCmd.Parameters.AddWithValue("FeedbackId", existingFeedbackId);
                updateFeedbackCmd.Parameters.AddWithValue("FeedbackType", dto.ValidationStatus == "Corrected" ? "Incorrect" : "Correct");
                updateFeedbackCmd.Parameters.AddWithValue("CorrectedRiskLevel", (object?)dto.CorrectedRiskLevel ?? DBNull.Value);
                updateFeedbackCmd.Parameters.AddWithValue("FeedbackNotes", (object?)dto.ValidationNotes ?? DBNull.Value);
                updateFeedbackCmd.Parameters.AddWithValue("IsUsedForTraining", dto.ValidationStatus == "Corrected");

                await updateFeedbackCmd.ExecuteNonQueryAsync();
            }

            _logger.LogInformation("Findings validated by doctor {DoctorId} for analysis {AnalysisId}, Status: {Status}", 
                doctorId, analysisId, dto.ValidationStatus);

            return Ok(new { message = "Findings validated successfully", analysisId, status = dto.ValidationStatus });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating findings for analysis {AnalysisId}", analysisId);
            return StatusCode(500, new { message = "Không thể validate findings" });
        }
    }

    #endregion

    #region AI Feedback (FR-19)

    /// <summary>
    /// Submit AI feedback (FR-19)
    /// </summary>
    [HttpPost("ai-feedback")]
    [ProducesResponseType(typeof(AIFeedbackDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SubmitAIFeedback([FromBody] CreateAIFeedbackDto dto)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var doctorId = GetCurrentDoctorId();
        if (doctorId == null) return Unauthorized(new { message = "Chưa xác thực bác sĩ" });

        if (string.IsNullOrWhiteSpace(dto.ResultId))
        {
            return BadRequest(new { message = "ResultId là bắt buộc" });
        }

        if (!new[] { "Correct", "Incorrect", "PartiallyCorrect", "NeedsReview" }.Contains(dto.FeedbackType))
        {
            return BadRequest(new { message = "FeedbackType không hợp lệ" });
        }

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Verify analysis exists and doctor can access
            var verifySql = @"
                SELECT ar.Id, ar.OverallRiskLevel FROM analysis_results ar
                INNER JOIN retinal_images ri ON ri.Id = ar.ImageId AND COALESCE(ri.IsDeleted, false) = false
                WHERE ar.Id = @ResultId 
                    AND COALESCE(ar.IsDeleted, false) = false
                    AND (
                        -- Doctor is assigned to the patient
                        EXISTS (
                            SELECT 1 FROM patient_doctor_assignments pda 
                            WHERE pda.UserId = ar.UserId 
                            AND pda.DoctorId = @DoctorId
                            AND COALESCE(pda.IsDeleted, false) = false
                            AND pda.IsActive = true
                        )
                        OR 
                        -- Doctor is in the same clinic (via clinic_doctors table)
                        EXISTS (
                            SELECT 1 FROM clinic_doctors cd 
                            WHERE cd.DoctorId = @DoctorId 
                            AND cd.ClinicId = ri.ClinicId
                            AND COALESCE(cd.IsDeleted, false) = false
                            AND cd.IsActive = true
                        )
                        OR
                        -- Any authenticated doctor can give feedback
                        EXISTS (SELECT 1 FROM doctors WHERE Id = @DoctorId AND COALESCE(IsDeleted, false) = false)
                    )";

            using var verifyCmd = new NpgsqlCommand(verifySql, connection);
            verifyCmd.Parameters.AddWithValue("ResultId", dto.ResultId);
            verifyCmd.Parameters.AddWithValue("DoctorId", doctorId);

            using var verifyReader = await verifyCmd.ExecuteReaderAsync();
            if (!await verifyReader.ReadAsync())
            {
                return NotFound(new { message = "Không tìm thấy kết quả phân tích hoặc không có quyền truy cập" });
            }

            var originalRiskLevel = verifyReader.IsDBNull(1) ? null : verifyReader.GetString(1);
            verifyReader.Close();

            // Check if feedback already exists
            var checkSql = @"
                SELECT Id FROM ai_feedback 
                WHERE ResultId = @ResultId AND DoctorId = @DoctorId AND COALESCE(IsDeleted, false) = false
                LIMIT 1";

            using var checkCmd = new NpgsqlCommand(checkSql, connection);
            checkCmd.Parameters.AddWithValue("ResultId", dto.ResultId);
            checkCmd.Parameters.AddWithValue("DoctorId", doctorId);

            var existingFeedbackId = await checkCmd.ExecuteScalarAsync() as string;

            string feedbackId;
            if (string.IsNullOrEmpty(existingFeedbackId))
            {
                // Create new feedback
                feedbackId = Guid.NewGuid().ToString();
                var insertSql = @"
                    INSERT INTO ai_feedback
                    (Id, ResultId, DoctorId, FeedbackType, OriginalRiskLevel, CorrectedRiskLevel, 
                     FeedbackNotes, IsUsedForTraining, CreatedDate, CreatedBy, IsDeleted)
                    VALUES
                    (@Id, @ResultId, @DoctorId, @FeedbackType, @OriginalRiskLevel, @CorrectedRiskLevel,
                     @FeedbackNotes, @IsUsedForTraining, @CreatedDate, @CreatedBy, false)
                    RETURNING Id";

                using var insertCmd = new NpgsqlCommand(insertSql, connection);
                insertCmd.Parameters.AddWithValue("Id", feedbackId);
                insertCmd.Parameters.AddWithValue("ResultId", dto.ResultId);
                insertCmd.Parameters.AddWithValue("DoctorId", doctorId);
                insertCmd.Parameters.AddWithValue("FeedbackType", dto.FeedbackType);
                insertCmd.Parameters.AddWithValue("OriginalRiskLevel", (object?)dto.OriginalRiskLevel ?? (object?)originalRiskLevel ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("CorrectedRiskLevel", (object?)dto.CorrectedRiskLevel ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("FeedbackNotes", (object?)dto.FeedbackNotes ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("IsUsedForTraining", dto.UseForTraining);
                insertCmd.Parameters.AddWithValue("CreatedDate", DateTime.UtcNow.Date);
                insertCmd.Parameters.AddWithValue("CreatedBy", doctorId);

                feedbackId = (await insertCmd.ExecuteScalarAsync() as string) ?? feedbackId;
            }
            else
            {
                // Update existing feedback
                feedbackId = existingFeedbackId;
                var updateSql = @"
                    UPDATE ai_feedback
                    SET FeedbackType = @FeedbackType,
                        OriginalRiskLevel = COALESCE(@OriginalRiskLevel, OriginalRiskLevel),
                        CorrectedRiskLevel = COALESCE(@CorrectedRiskLevel, CorrectedRiskLevel),
                        FeedbackNotes = COALESCE(@FeedbackNotes, FeedbackNotes),
                        IsUsedForTraining = @IsUsedForTraining,
                        UpdatedDate = CURRENT_DATE
                    WHERE Id = @FeedbackId";

                using var updateCmd = new NpgsqlCommand(updateSql, connection);
                updateCmd.Parameters.AddWithValue("FeedbackId", feedbackId);
                updateCmd.Parameters.AddWithValue("FeedbackType", dto.FeedbackType);
                updateCmd.Parameters.AddWithValue("OriginalRiskLevel", (object?)dto.OriginalRiskLevel ?? (object?)originalRiskLevel ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("CorrectedRiskLevel", (object?)dto.CorrectedRiskLevel ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("FeedbackNotes", (object?)dto.FeedbackNotes ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("IsUsedForTraining", dto.UseForTraining);

                await updateCmd.ExecuteNonQueryAsync();
            }

            // Get created/updated feedback
            var getFeedbackSql = @"
                SELECT Id, ResultId, DoctorId, FeedbackType, OriginalRiskLevel, CorrectedRiskLevel,
                       FeedbackNotes, IsUsedForTraining, CreatedDate
                FROM ai_feedback
                WHERE Id = @FeedbackId";

            using var getFeedbackCmd = new NpgsqlCommand(getFeedbackSql, connection);
            getFeedbackCmd.Parameters.AddWithValue("FeedbackId", feedbackId);

            using var feedbackReader = await getFeedbackCmd.ExecuteReaderAsync();
            if (!await feedbackReader.ReadAsync())
            {
                return StatusCode(500, new { message = "Không thể lấy thông tin feedback" });
            }

            var feedback = new AIFeedbackDto
            {
                Id = feedbackReader.GetString(0),
                ResultId = feedbackReader.GetString(1),
                DoctorId = feedbackReader.GetString(2),
                FeedbackType = feedbackReader.GetString(3),
                OriginalRiskLevel = feedbackReader.IsDBNull(4) ? null : feedbackReader.GetString(4),
                CorrectedRiskLevel = feedbackReader.IsDBNull(5) ? null : feedbackReader.GetString(5),
                FeedbackNotes = feedbackReader.IsDBNull(6) ? null : feedbackReader.GetString(6),
                IsUsedForTraining = feedbackReader.GetBoolean(7),
                CreatedDate = feedbackReader.GetDateTime(8)
            };

            _logger.LogInformation("AI feedback submitted by doctor {DoctorId} for analysis {ResultId}, Type: {FeedbackType}", 
                doctorId, dto.ResultId, dto.FeedbackType);

            return CreatedAtAction(nameof(SubmitAIFeedback), new { id = feedbackId }, feedback);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error submitting AI feedback for result {ResultId}", dto.ResultId);
            return StatusCode(500, new { message = "Không thể submit AI feedback" });
        }
    }

    #endregion

    #region Private Methods

    private string? GetCurrentDoctorId()
    {
        // Ưu tiên lấy từ claim "doctor_id" (nếu có)
        var doctorId = User.FindFirstValue("doctor_id");
        if (!string.IsNullOrEmpty(doctorId))
        {
            return doctorId;
        }
        
        // Fallback về NameIdentifier nếu không có doctor_id claim
        return User.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    #endregion
}
