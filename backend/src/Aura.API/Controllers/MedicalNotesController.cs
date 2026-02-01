using Aura.Application.DTOs.MedicalNotes;
using Aura.Application.Services.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Security.Claims;

namespace Aura.API.Controllers;

/// <summary>
/// Controller quản lý Medical Notes (FR-21)
/// </summary>
[ApiController]
[Route("api/medical-notes")]
[Authorize]
[Produces("application/json")]
public class MedicalNotesController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly INotificationService _notificationService;
    private readonly ILogger<MedicalNotesController> _logger;
    private readonly string _connectionString;

    public MedicalNotesController(
        IConfiguration configuration,
        INotificationService notificationService,
        ILogger<MedicalNotesController> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connectionString = _configuration.GetConnectionString("DefaultConnection") 
            ?? throw new InvalidOperationException("Database connection string not configured");
    }

    /// <summary>
    /// Tạo medical note mới
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(MedicalNoteDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateMedicalNote([FromBody] CreateMedicalNoteDto dto)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var doctorId = GetCurrentDoctorId();
        if (doctorId == null) return Unauthorized(new { message = "Chưa xác thực bác sĩ" });

        var resultIdOrAnalysis = !string.IsNullOrWhiteSpace(dto.ResultId) ? dto.ResultId : dto.AnalysisId;
        // Must have either ResultId/AnalysisId or PatientUserId
        if (string.IsNullOrWhiteSpace(resultIdOrAnalysis) && string.IsNullOrWhiteSpace(dto.PatientUserId))
        {
            return BadRequest(new { message = "Phải cung cấp ResultId/AnalysisId hoặc PatientUserId" });
        }

        if (string.IsNullOrWhiteSpace(dto.NoteContent))
        {
            return BadRequest(new { message = "NoteContent là bắt buộc" });
        }

        if (dto.NoteContent.Length > 5000)
        {
            return BadRequest(new { message = "NoteContent không được vượt quá 5000 ký tự" });
        }

        if (!IsValidNoteType(dto.NoteType))
        {
            return BadRequest(new { message = "NoteType không hợp lệ. Chọn: Diagnosis, Recommendation, FollowUp, General, Prescription, Treatment, Observation, Other" });
        }

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            string? patientUserId = dto.PatientUserId;
            string? resultId = resultIdOrAnalysis;

            // If ResultId is provided, verify access and get patient
            if (!string.IsNullOrWhiteSpace(resultId))
            {
                var verifySql = @"
                    SELECT ar.UserId FROM analysis_results ar
                    LEFT JOIN patient_doctor_assignments pda ON pda.UserId = ar.UserId AND pda.DoctorId = @DoctorId
                    WHERE ar.Id = @ResultId 
                        AND (pda.DoctorId IS NOT NULL OR EXISTS (
                            SELECT 1 FROM ai_feedback af WHERE af.ResultId = ar.Id AND af.DoctorId = @DoctorId
                        ))";

                using var verifyCmd = new NpgsqlCommand(verifySql, connection);
                verifyCmd.Parameters.AddWithValue("ResultId", resultId);
                verifyCmd.Parameters.AddWithValue("DoctorId", doctorId);

                patientUserId = await verifyCmd.ExecuteScalarAsync() as string;
                if (patientUserId == null)
                {
                    return NotFound(new { message = "Không tìm thấy kết quả phân tích hoặc không có quyền truy cập" });
                }
            }
            else if (!string.IsNullOrWhiteSpace(dto.PatientUserId))
            {
                // Verify doctor has access to this patient
                var verifyPatientSql = @"
                    SELECT u.Id FROM users u
                    LEFT JOIN patient_doctor_assignments pda ON pda.UserId = u.Id AND pda.DoctorId = @DoctorId
                    LEFT JOIN ai_feedback af ON EXISTS (
                        SELECT 1 FROM analysis_results ar 
                        WHERE ar.UserId = u.Id 
                        AND af.ResultId = ar.Id 
                        AND af.DoctorId = @DoctorId
                    )
                    WHERE u.Id = @PatientUserId
                        AND COALESCE(u.IsDeleted, false) = false
                        AND (pda.DoctorId IS NOT NULL OR af.DoctorId IS NOT NULL)
                    LIMIT 1";

                using var verifyPatientCmd = new NpgsqlCommand(verifyPatientSql, connection);
                verifyPatientCmd.Parameters.AddWithValue("PatientUserId", dto.PatientUserId);
                verifyPatientCmd.Parameters.AddWithValue("DoctorId", doctorId);

                var verifiedPatientId = await verifyPatientCmd.ExecuteScalarAsync() as string;
                if (verifiedPatientId == null)
                {
                    // Allow creating note for any patient (doctor can search for patients)
                    patientUserId = dto.PatientUserId;
                }
                else
                {
                    patientUserId = verifiedPatientId;
                }
            }

            // PatientUserId must reference users(Id); if friend changed DB or ar.UserId is clinicId, use NULL
            if (!string.IsNullOrWhiteSpace(patientUserId))
            {
                var userExistsSql = "SELECT 1 FROM users WHERE Id = @Id AND COALESCE(IsDeleted, false) = false LIMIT 1";
                using var userExistsCmd = new NpgsqlCommand(userExistsSql, connection);
                userExistsCmd.Parameters.AddWithValue("Id", patientUserId);
                var exists = await userExistsCmd.ExecuteScalarAsync();
                if (exists == null || exists == DBNull.Value)
                {
                    patientUserId = null;
                }
            }

            // Create medical note
            var noteId = Guid.NewGuid().ToString();
            var now = DateTime.UtcNow;

            var insertSql = @"
                INSERT INTO medical_notes 
                (Id, ResultId, PatientUserId, DoctorId, NoteType, NoteContent, Diagnosis, Prescription, 
                 TreatmentPlan, ClinicalObservations, Severity, FollowUpDate, IsImportant, IsPrivate,
                 CreatedDate, CreatedBy, IsDeleted)
                VALUES 
                (@Id, @ResultId, @PatientUserId, @DoctorId, @NoteType, @NoteContent, @Diagnosis, @Prescription,
                 @TreatmentPlan, @ClinicalObservations, @Severity, @FollowUpDate, @IsImportant, @IsPrivate,
                 @CreatedDate, @CreatedBy, false)";

            using var insertCmd = new NpgsqlCommand(insertSql, connection);
            insertCmd.Parameters.AddWithValue("Id", noteId);
            insertCmd.Parameters.AddWithValue("ResultId", (object?)resultId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("PatientUserId", (object?)patientUserId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("DoctorId", doctorId);
            insertCmd.Parameters.AddWithValue("NoteType", dto.NoteType);
            insertCmd.Parameters.AddWithValue("NoteContent", dto.NoteContent);
            insertCmd.Parameters.AddWithValue("Diagnosis", (object?)dto.Diagnosis ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("Prescription", (object?)dto.Prescription ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("TreatmentPlan", (object?)dto.TreatmentPlan ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("ClinicalObservations", (object?)dto.ClinicalObservations ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("Severity", (object?)dto.Severity ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("FollowUpDate", (object?)dto.FollowUpDate ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("IsImportant", dto.IsImportant);
            insertCmd.Parameters.AddWithValue("IsPrivate", dto.IsPrivate);
            insertCmd.Parameters.AddWithValue("CreatedDate", now);
            insertCmd.Parameters.AddWithValue("CreatedBy", doctorId);

            await insertCmd.ExecuteNonQueryAsync();
            _logger.LogInformation("Medical note saved to database: Id={NoteId}, DoctorId={DoctorId}, PatientUserId={PatientUserId}", noteId, doctorId, patientUserId);

            // Gửi thông báo cho bệnh nhân khi bác sĩ thêm ghi chú y tế
            if (!string.IsNullOrEmpty(patientUserId))
            {
                try
                {
                    await _notificationService.CreateAsync(patientUserId,
                        "Ghi chú y tế mới",
                        "Bác sĩ đã thêm ghi chú y tế cho bạn. Vào mục Ghi chú y tế để xem chi tiết.",
                        "Other",
                        new { noteId });
                }
                catch (Exception notifEx)
                {
                    _logger.LogWarning(notifEx, "Could not create notification for medical note {NoteId}", noteId);
                }
            }

            // Get the created note with patient info
            var note = await GetNoteByIdInternal(connection, noteId, doctorId);
            if (note == null)
            {
                _logger.LogWarning("Medical note inserted but GetNoteByIdInternal returned null for Id={NoteId}", noteId);
                return StatusCode(500, new { message = "Không thể tạo medical note" });
            }

            _logger.LogInformation("Medical note created: {NoteId} by doctor {DoctorId} for patient {PatientId}", 
                noteId, doctorId, patientUserId);

            return CreatedAtAction(nameof(GetMedicalNote), new { id = noteId }, note);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating medical note for doctor {DoctorId}: {Message}", doctorId, ex.Message);
            return StatusCode(500, new { message = "Không thể tạo medical note", detail = ex.Message });
        }
    }

    /// <summary>
    /// Lấy danh sách medical notes
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<MedicalNoteDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMedicalNotes(
        [FromQuery] string? resultId = null,
        [FromQuery] string? patientUserId = null,
        [FromQuery] string? noteType = null,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0)
    {
        var doctorId = GetCurrentDoctorId();
        if (doctorId == null) return Unauthorized(new { message = "Chưa xác thực bác sĩ" });

        // Validate pagination parameters
        if (limit < 1 || limit > 100)
        {
            return BadRequest(new { message = "Limit phải từ 1 đến 100" });
        }
        if (offset < 0)
        {
            return BadRequest(new { message = "Offset phải >= 0" });
        }

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Build dynamic SQL based on provided filters
            var whereClauses = new List<string> { "mn.DoctorId = @DoctorId", "COALESCE(mn.IsDeleted, false) = false" };
            var parameters = new List<NpgsqlParameter>
            {
                new NpgsqlParameter("DoctorId", doctorId),
                new NpgsqlParameter("Limit", limit),
                new NpgsqlParameter("Offset", offset)
            };

            if (!string.IsNullOrEmpty(resultId))
            {
                whereClauses.Add("mn.ResultId = @ResultId");
                parameters.Add(new NpgsqlParameter("ResultId", resultId));
            }
            if (!string.IsNullOrEmpty(patientUserId))
            {
                whereClauses.Add("mn.PatientUserId = @PatientUserId");
                parameters.Add(new NpgsqlParameter("PatientUserId", patientUserId));
            }
            if (!string.IsNullOrEmpty(noteType))
            {
                whereClauses.Add("mn.NoteType = @NoteType");
                parameters.Add(new NpgsqlParameter("NoteType", noteType));
            }

            var sql = $@"
                SELECT mn.Id, mn.ResultId, mn.PatientUserId, mn.DoctorId, 
                       COALESCE(d.FirstName || ' ' || d.LastName, d.Email) as DoctorName,
                       COALESCE(u.FirstName || ' ' || u.LastName, u.Email) as PatientName,
                       mn.NoteType, mn.NoteContent, mn.Diagnosis, mn.Prescription,
                       mn.TreatmentPlan, mn.ClinicalObservations, mn.Severity,
                       mn.FollowUpDate, mn.IsImportant, mn.IsPrivate, mn.CreatedDate, mn.CreatedBy,
                       mn.UpdatedDate, mn.UpdatedBy
                FROM medical_notes mn
                INNER JOIN doctors d ON d.Id = mn.DoctorId
                LEFT JOIN users u ON u.Id = mn.PatientUserId
                WHERE {string.Join(" AND ", whereClauses)}
                ORDER BY mn.CreatedDate DESC, mn.IsImportant DESC
                LIMIT @Limit OFFSET @Offset";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddRange(parameters.ToArray());

            var notes = new List<MedicalNoteDto>();
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                notes.Add(MapToDto(reader));
            }

            return Ok(notes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting medical notes for doctor {DoctorId}", doctorId);
            return StatusCode(500, new { message = "Không thể lấy danh sách medical notes" });
        }
    }

    /// <summary>
    /// Lấy medical notes của bệnh nhân hiện tại (cho patient view)
    /// </summary>
    [HttpGet("my-notes")]
    [ProducesResponseType(typeof(List<MedicalNoteDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMyMedicalNotes()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized(new { message = "Chưa xác thực" });

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Get non-private notes for this patient (ViewedByPatientAt optional if DB was changed)
            var sqlWithViewed = @"
                SELECT mn.Id, mn.ResultId, mn.PatientUserId, mn.DoctorId, 
                       COALESCE(d.FirstName || ' ' || d.LastName, d.Email) as DoctorName,
                       COALESCE(u.FirstName || ' ' || u.LastName, u.Email) as PatientName,
                       mn.NoteType, mn.NoteContent, mn.Diagnosis, mn.Prescription,
                       mn.TreatmentPlan, mn.ClinicalObservations, mn.Severity,
                       mn.FollowUpDate, mn.IsImportant, mn.IsPrivate, mn.CreatedDate, mn.CreatedBy,
                       mn.UpdatedDate, mn.UpdatedBy, mn.ViewedByPatientAt
                FROM medical_notes mn
                INNER JOIN doctors d ON d.Id = mn.DoctorId
                LEFT JOIN users u ON u.Id = mn.PatientUserId
                WHERE mn.PatientUserId = @UserId
                    AND COALESCE(mn.IsDeleted, false) = false
                    AND COALESCE(mn.IsPrivate, false) = false
                ORDER BY mn.CreatedDate DESC
                LIMIT 100";

            var sqlWithoutViewed = @"
                SELECT mn.Id, mn.ResultId, mn.PatientUserId, mn.DoctorId, 
                       COALESCE(d.FirstName || ' ' || d.LastName, d.Email) as DoctorName,
                       COALESCE(u.FirstName || ' ' || u.LastName, u.Email) as PatientName,
                       mn.NoteType, mn.NoteContent, mn.Diagnosis, mn.Prescription,
                       mn.TreatmentPlan, mn.ClinicalObservations, mn.Severity,
                       mn.FollowUpDate, mn.IsImportant, mn.IsPrivate, mn.CreatedDate, mn.CreatedBy,
                       mn.UpdatedDate, mn.UpdatedBy
                FROM medical_notes mn
                INNER JOIN doctors d ON d.Id = mn.DoctorId
                LEFT JOIN users u ON u.Id = mn.PatientUserId
                WHERE mn.PatientUserId = @UserId
                    AND COALESCE(mn.IsDeleted, false) = false
                    AND COALESCE(mn.IsPrivate, false) = false
                ORDER BY mn.CreatedDate DESC
                LIMIT 100";

            try
            {
                using var command = new NpgsqlCommand(sqlWithViewed, connection);
                command.Parameters.AddWithValue("UserId", userId);
                var notes = new List<MedicalNoteDto>();
                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        notes.Add(MapToDto(reader));
                    }
                }
                return Ok(notes);
            }
            catch (PostgresException pgEx) when (pgEx.SqlState == "42703")
            {
                _logger.LogWarning(pgEx, "Column ViewedByPatientAt may be missing in medical_notes, using fallback query");
                using var fallbackCmd = new NpgsqlCommand(sqlWithoutViewed, connection);
                fallbackCmd.Parameters.AddWithValue("UserId", userId);
                var notesList = new List<MedicalNoteDto>();
                using (var reader = await fallbackCmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        notesList.Add(MapToDto(reader));
                    }
                }
                return Ok(notesList);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting medical notes for patient {UserId}", userId);
            return StatusCode(500, new { message = "Không thể lấy danh sách ghi chú" });
        }
    }

    /// <summary>
    /// Số ghi chú chưa xem (cho badge menu bệnh nhân).
    /// </summary>
    [HttpGet("my-notes/unread-count")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMyNotesUnreadCount()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized(new { message = "Chưa xác thực" });

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT COUNT(*) FROM medical_notes
                WHERE PatientUserId = @UserId
                    AND COALESCE(IsDeleted, false) = false
                    AND COALESCE(IsPrivate, false) = false
                    AND ViewedByPatientAt IS NULL";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("UserId", userId);
            var count = Convert.ToInt32(await command.ExecuteScalarAsync());
            return Ok(new { count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting unread notes count for patient {UserId}", userId);
            return Ok(new { count = 0 });
        }
    }

    /// <summary>
    /// Lấy chi tiết một ghi chú y tế của bệnh nhân (theo id, dùng cho modal xem nhanh từ thông báo).
    /// </summary>
    [HttpGet("my-notes/{id}")]
    [ProducesResponseType(typeof(MedicalNoteDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMyMedicalNoteById(string id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized(new { message = "Chưa xác thực" });

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sqlWithViewed = @"
                SELECT mn.Id, mn.ResultId, mn.PatientUserId, mn.DoctorId, 
                       COALESCE(d.FirstName || ' ' || d.LastName, d.Email) as DoctorName,
                       COALESCE(u.FirstName || ' ' || u.LastName, u.Email) as PatientName,
                       mn.NoteType, mn.NoteContent, mn.Diagnosis, mn.Prescription,
                       mn.TreatmentPlan, mn.ClinicalObservations, mn.Severity,
                       mn.FollowUpDate, mn.IsImportant, mn.IsPrivate, mn.CreatedDate, mn.CreatedBy,
                       mn.UpdatedDate, mn.UpdatedBy, mn.ViewedByPatientAt
                FROM medical_notes mn
                INNER JOIN doctors d ON d.Id = mn.DoctorId
                LEFT JOIN users u ON u.Id = mn.PatientUserId
                WHERE mn.Id = @Id AND mn.PatientUserId = @UserId
                    AND COALESCE(mn.IsDeleted, false) = false
                    AND COALESCE(mn.IsPrivate, false) = false";

            var sqlWithoutViewed = @"
                SELECT mn.Id, mn.ResultId, mn.PatientUserId, mn.DoctorId, 
                       COALESCE(d.FirstName || ' ' || d.LastName, d.Email) as DoctorName,
                       COALESCE(u.FirstName || ' ' || u.LastName, u.Email) as PatientName,
                       mn.NoteType, mn.NoteContent, mn.Diagnosis, mn.Prescription,
                       mn.TreatmentPlan, mn.ClinicalObservations, mn.Severity,
                       mn.FollowUpDate, mn.IsImportant, mn.IsPrivate, mn.CreatedDate, mn.CreatedBy,
                       mn.UpdatedDate, mn.UpdatedBy
                FROM medical_notes mn
                INNER JOIN doctors d ON d.Id = mn.DoctorId
                LEFT JOIN users u ON u.Id = mn.PatientUserId
                WHERE mn.Id = @Id AND mn.PatientUserId = @UserId
                    AND COALESCE(mn.IsDeleted, false) = false
                    AND COALESCE(mn.IsPrivate, false) = false";

            try
            {
                using var command = new NpgsqlCommand(sqlWithViewed, connection);
                command.Parameters.AddWithValue("Id", id);
                command.Parameters.AddWithValue("UserId", userId);
                using var reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                    return NotFound(new { message = "Không tìm thấy ghi chú" });
                return Ok(MapToDto(reader));
            }
            catch (PostgresException pgEx) when (pgEx.SqlState == "42703")
            {
                _logger.LogWarning(pgEx, "Column ViewedByPatientAt may be missing, using fallback for GetMyMedicalNoteById");
                using var fallbackCmd = new NpgsqlCommand(sqlWithoutViewed, connection);
                fallbackCmd.Parameters.AddWithValue("Id", id);
                fallbackCmd.Parameters.AddWithValue("UserId", userId);
                using var reader2 = await fallbackCmd.ExecuteReaderAsync();
                if (!await reader2.ReadAsync())
                    return NotFound(new { message = "Không tìm thấy ghi chú" });
                return Ok(MapToDto(reader2));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting medical note {NoteId} for patient {UserId}", id, userId);
            return StatusCode(500, new { message = "Không thể lấy ghi chú" });
        }
    }

    /// <summary>
    /// Bệnh nhân đánh dấu ghi chú đã xem (giảm badge đỏ trên menu).
    /// </summary>
    [HttpPost("my-notes/{id}/mark-viewed")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> MarkNoteViewed(string id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Unauthorized(new { message = "Chưa xác thực" });

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                UPDATE medical_notes
                SET ViewedByPatientAt = CURRENT_TIMESTAMP, UpdatedDate = CURRENT_TIMESTAMP
                WHERE Id = @Id AND PatientUserId = @UserId
                    AND COALESCE(IsDeleted, false) = false";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("Id", id);
            command.Parameters.AddWithValue("UserId", userId);
            var rows = await command.ExecuteNonQueryAsync();
            if (rows == 0)
                return NotFound(new { message = "Không tìm thấy ghi chú hoặc không có quyền" });
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking note {NoteId} as viewed for patient {UserId}", id, userId);
            return StatusCode(500, new { message = "Không thể đánh dấu đã xem" });
        }
    }

    /// <summary>
    /// Lấy chi tiết medical note
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(MedicalNoteDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMedicalNote(string id)
    {
        var doctorId = GetCurrentDoctorId();
        if (doctorId == null) return Unauthorized(new { message = "Chưa xác thực bác sĩ" });

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var note = await GetNoteByIdInternal(connection, id, doctorId);
            if (note == null)
            {
                return NotFound(new { message = "Không tìm thấy medical note" });
            }

            return Ok(note);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting medical note {NoteId} for doctor {DoctorId}", id, doctorId);
            return StatusCode(500, new { message = "Không thể lấy thông tin medical note" });
        }
    }

    /// <summary>
    /// Cập nhật medical note
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(MedicalNoteDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateMedicalNote(string id, [FromBody] UpdateMedicalNoteDto dto)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var doctorId = GetCurrentDoctorId();
        if (doctorId == null) return Unauthorized(new { message = "Chưa xác thực bác sĩ" });

        // Validate NoteContent length if provided
        if (!string.IsNullOrWhiteSpace(dto.NoteContent) && dto.NoteContent.Length > 5000)
        {
            return BadRequest(new { message = "NoteContent không được vượt quá 5000 ký tự" });
        }

        if (!string.IsNullOrWhiteSpace(dto.NoteType) && !IsValidNoteType(dto.NoteType))
        {
            return BadRequest(new { message = "NoteType không hợp lệ" });
        }

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                UPDATE medical_notes
                SET NoteType = COALESCE(@NoteType, NoteType),
                    NoteContent = COALESCE(@NoteContent, NoteContent),
                    Diagnosis = COALESCE(@Diagnosis, Diagnosis),
                    Prescription = COALESCE(@Prescription, Prescription),
                    TreatmentPlan = COALESCE(@TreatmentPlan, TreatmentPlan),
                    ClinicalObservations = COALESCE(@ClinicalObservations, ClinicalObservations),
                    Severity = COALESCE(@Severity, Severity),
                    FollowUpDate = COALESCE(@FollowUpDate, FollowUpDate),
                    IsImportant = COALESCE(@IsImportant, IsImportant),
                    IsPrivate = COALESCE(@IsPrivate, IsPrivate),
                    UpdatedDate = CURRENT_TIMESTAMP,
                    UpdatedBy = @DoctorId
                WHERE Id = @Id
                    AND DoctorId = @DoctorId
                    AND COALESCE(IsDeleted, false) = false";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("Id", id);
            command.Parameters.AddWithValue("DoctorId", doctorId);
            command.Parameters.AddWithValue("NoteType", (object?)dto.NoteType ?? DBNull.Value);
            command.Parameters.AddWithValue("NoteContent", (object?)dto.NoteContent ?? DBNull.Value);
            command.Parameters.AddWithValue("Diagnosis", (object?)dto.Diagnosis ?? DBNull.Value);
            command.Parameters.AddWithValue("Prescription", (object?)dto.Prescription ?? DBNull.Value);
            command.Parameters.AddWithValue("TreatmentPlan", (object?)dto.TreatmentPlan ?? DBNull.Value);
            command.Parameters.AddWithValue("ClinicalObservations", (object?)dto.ClinicalObservations ?? DBNull.Value);
            command.Parameters.AddWithValue("Severity", (object?)dto.Severity ?? DBNull.Value);
            command.Parameters.AddWithValue("FollowUpDate", (object?)dto.FollowUpDate ?? DBNull.Value);
            command.Parameters.AddWithValue("IsImportant", (object?)dto.IsImportant ?? DBNull.Value);
            command.Parameters.AddWithValue("IsPrivate", (object?)dto.IsPrivate ?? DBNull.Value);

            var rowsAffected = await command.ExecuteNonQueryAsync();
            if (rowsAffected == 0)
            {
                return NotFound(new { message = "Không tìm thấy medical note hoặc không có quyền cập nhật" });
            }

            var note = await GetNoteByIdInternal(connection, id, doctorId);
            _logger.LogInformation("Medical note updated: {NoteId} by doctor {DoctorId}", id, doctorId);

            return Ok(note);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating medical note {NoteId} for doctor {DoctorId}", id, doctorId);
            return StatusCode(500, new { message = "Không thể cập nhật medical note" });
        }
    }

    /// <summary>
    /// Xóa medical note (soft delete)
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteMedicalNote(string id)
    {
        var doctorId = GetCurrentDoctorId();
        if (doctorId == null) return Unauthorized(new { message = "Chưa xác thực bác sĩ" });

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                UPDATE medical_notes
                SET IsDeleted = true,
                    UpdatedDate = CURRENT_TIMESTAMP,
                    UpdatedBy = @DoctorId
                WHERE Id = @Id
                    AND DoctorId = @DoctorId
                    AND COALESCE(IsDeleted, false) = false";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("Id", id);
            command.Parameters.AddWithValue("DoctorId", doctorId);

            var rowsAffected = await command.ExecuteNonQueryAsync();
            if (rowsAffected == 0)
            {
                return NotFound(new { message = "Không tìm thấy medical note hoặc không có quyền xóa" });
            }

            _logger.LogInformation("Medical note deleted: {NoteId} by doctor {DoctorId}", id, doctorId);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting medical note {NoteId} for doctor {DoctorId}", id, doctorId);
            return StatusCode(500, new { message = "Không thể xóa medical note" });
        }
    }

    #region Private Methods

    private string? GetCurrentDoctorId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    private static bool IsValidNoteType(string? noteType)
    {
        return noteType != null && new[] { 
            "Diagnosis", "Recommendation", "FollowUp", "General", "Prescription", 
            "Treatment", "Observation", "Referral", "Other" 
        }.Contains(noteType);
    }

    private async Task<MedicalNoteDto?> GetNoteByIdInternal(NpgsqlConnection connection, string noteId, string doctorId)
    {
        var sqlWithViewed = @"
            SELECT mn.Id, mn.ResultId, mn.PatientUserId, mn.DoctorId, 
                   COALESCE(d.FirstName || ' ' || d.LastName, d.Email, 'Bác sĩ') as DoctorName,
                   COALESCE(u.FirstName || ' ' || u.LastName, u.Email, '') as PatientName,
                   mn.NoteType, mn.NoteContent, mn.Diagnosis, mn.Prescription,
                   mn.TreatmentPlan, mn.ClinicalObservations, mn.Severity,
                   mn.FollowUpDate, mn.IsImportant, mn.IsPrivate, mn.CreatedDate, mn.CreatedBy,
                   mn.UpdatedDate, mn.UpdatedBy, mn.ViewedByPatientAt
            FROM medical_notes mn
            LEFT JOIN doctors d ON d.Id = mn.DoctorId AND COALESCE(d.IsDeleted, false) = false
            LEFT JOIN users u ON u.Id = mn.PatientUserId AND COALESCE(u.IsDeleted, false) = false
            WHERE mn.Id = @Id
                AND mn.DoctorId = @DoctorId
                AND COALESCE(mn.IsDeleted, false) = false";

        var sqlWithoutViewed = @"
            SELECT mn.Id, mn.ResultId, mn.PatientUserId, mn.DoctorId, 
                   COALESCE(d.FirstName || ' ' || d.LastName, d.Email, 'Bác sĩ') as DoctorName,
                   COALESCE(u.FirstName || ' ' || u.LastName, u.Email, '') as PatientName,
                   mn.NoteType, mn.NoteContent, mn.Diagnosis, mn.Prescription,
                   mn.TreatmentPlan, mn.ClinicalObservations, mn.Severity,
                   mn.FollowUpDate, mn.IsImportant, mn.IsPrivate, mn.CreatedDate, mn.CreatedBy,
                   mn.UpdatedDate, mn.UpdatedBy
            FROM medical_notes mn
            LEFT JOIN doctors d ON d.Id = mn.DoctorId AND COALESCE(d.IsDeleted, false) = false
            LEFT JOIN users u ON u.Id = mn.PatientUserId AND COALESCE(u.IsDeleted, false) = false
            WHERE mn.Id = @Id
                AND mn.DoctorId = @DoctorId
                AND COALESCE(mn.IsDeleted, false) = false";

        try
        {
            using var command = new NpgsqlCommand(sqlWithViewed, connection);
            command.Parameters.AddWithValue("Id", noteId);
            command.Parameters.AddWithValue("DoctorId", doctorId);
            using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return null;
            return MapToDto(reader);
        }
        catch (PostgresException pgEx) when (pgEx.SqlState == "42703")
        {
            _logger.LogWarning(pgEx, "Column ViewedByPatientAt may be missing, using fallback for GetNoteByIdInternal");
        }

        using var fallbackCmd = new NpgsqlCommand(sqlWithoutViewed, connection);
        fallbackCmd.Parameters.AddWithValue("Id", noteId);
        fallbackCmd.Parameters.AddWithValue("DoctorId", doctorId);
        using var reader2 = await fallbackCmd.ExecuteReaderAsync();
        if (!await reader2.ReadAsync())
            return null;
        return MapToDto(reader2);
    }

    private static MedicalNoteDto MapToDto(NpgsqlDataReader reader)
    {
        var createdDate = reader.IsDBNull(16) ? DateTime.UtcNow : reader.GetDateTime(16);
        return new MedicalNoteDto
        {
            Id = reader.GetString(0),
            ResultId = reader.IsDBNull(1) ? null : reader.GetString(1),
            AnalysisId = reader.IsDBNull(1) ? null : reader.GetString(1),
            PatientUserId = reader.IsDBNull(2) ? null : reader.GetString(2),
            DoctorId = reader.GetString(3),
            DoctorName = reader.IsDBNull(4) ? null : reader.GetString(4),
            PatientName = reader.IsDBNull(5) ? null : reader.GetString(5),
            NoteType = reader.GetString(6),
            NoteContent = reader.GetString(7),
            Diagnosis = reader.IsDBNull(8) ? null : reader.GetString(8),
            Prescription = reader.IsDBNull(9) ? null : reader.GetString(9),
            TreatmentPlan = reader.IsDBNull(10) ? null : reader.GetString(10),
            ClinicalObservations = reader.IsDBNull(11) ? null : reader.GetString(11),
            Severity = reader.IsDBNull(12) ? null : reader.GetString(12),
            FollowUpDate = reader.IsDBNull(13) ? null : reader.GetDateTime(13),
            IsImportant = reader.IsDBNull(14) ? false : reader.GetBoolean(14),
            IsPrivate = reader.IsDBNull(15) ? false : reader.GetBoolean(15),
            CreatedDate = createdDate,
            CreatedAt = createdDate.ToString("o"),
            CreatedBy = reader.IsDBNull(17) ? null : reader.GetString(17),
            UpdatedDate = reader.IsDBNull(18) ? null : reader.GetDateTime(18),
            UpdatedBy = reader.IsDBNull(19) ? null : reader.GetString(19),
            ViewedByPatientAt = reader.FieldCount > 20 && !reader.IsDBNull(20) ? reader.GetDateTime(20) : null
        };
    }

    #endregion
}
