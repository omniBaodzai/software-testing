using Aura.Application.DTOs.Clinic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aura.Application.Services.Clinic;

public class ClinicManagementService : IClinicManagementService
{
    private readonly string _connectionString;
    private readonly ILogger<ClinicManagementService>? _logger;

    public ClinicManagementService(IConfiguration configuration, ILogger<ClinicManagementService>? logger = null)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new ArgumentNullException("DefaultConnection not configured");
        _logger = logger;
    }

    #region Dashboard

    public async Task<ClinicDashboardStatsDto> GetDashboardStatsAsync(string clinicId)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var stats = new ClinicDashboardStatsDto();

        // Get doctor counts
        var doctorSql = @"
            SELECT 
                COUNT(*) FILTER (WHERE IsDeleted = false) as Total,
                COUNT(*) FILTER (WHERE IsDeleted = false AND IsActive = true) as Active
            FROM clinic_doctors WHERE ClinicId = @ClinicId";
        using (var cmd = new NpgsqlCommand(doctorSql, connection))
        {
            cmd.Parameters.AddWithValue("ClinicId", clinicId);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                stats.TotalDoctors = reader.GetInt32(0);
                stats.ActiveDoctors = reader.GetInt32(1);
            }
        }

        // Get patient counts
        var patientSql = @"
            SELECT 
                COUNT(*) FILTER (WHERE IsDeleted = false) as Total,
                COUNT(*) FILTER (WHERE IsDeleted = false AND IsActive = true) as Active
            FROM clinic_users WHERE ClinicId = @ClinicId";
        using (var cmd = new NpgsqlCommand(patientSql, connection))
        {
            cmd.Parameters.AddWithValue("ClinicId", clinicId);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                stats.TotalPatients = reader.GetInt32(0);
                stats.ActivePatients = reader.GetInt32(1);
            }
        }

        // Get analysis stats
        var analysisSql = @"
            SELECT 
                COUNT(*) as Total,
                COUNT(*) FILTER (WHERE ar.AnalysisStatus = 'Pending' OR ar.AnalysisStatus = 'Processing') as Pending,
                COUNT(*) FILTER (WHERE ar.AnalysisStatus = 'Completed') as Completed,
                COUNT(*) FILTER (WHERE ar.OverallRiskLevel = 'High' OR ar.OverallRiskLevel = 'Critical') as HighRisk,
                COUNT(*) FILTER (WHERE ar.OverallRiskLevel = 'Medium') as MediumRisk,
                COUNT(*) FILTER (WHERE ar.OverallRiskLevel = 'Low') as LowRisk
            FROM analysis_results ar
            INNER JOIN retinal_images ri ON ri.Id = ar.ImageId
            WHERE ri.ClinicId = @ClinicId AND ar.IsDeleted = false";
        using (var cmd = new NpgsqlCommand(analysisSql, connection))
        {
            cmd.Parameters.AddWithValue("ClinicId", clinicId);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                stats.TotalAnalyses = reader.GetInt32(0);
                stats.PendingAnalyses = reader.GetInt32(1);
                stats.CompletedAnalyses = reader.GetInt32(2);
                stats.HighRiskCount = reader.GetInt32(3);
                stats.MediumRiskCount = reader.GetInt32(4);
                stats.LowRiskCount = reader.GetInt32(5);
            }
        }

        // Get package info
        var packageSql = @"
            SELECT RemainingAnalyses, ExpiresAt
            FROM user_packages
            WHERE ClinicId = @ClinicId AND IsActive = true AND IsDeleted = false
            ORDER BY ExpiresAt DESC LIMIT 1";
        using (var cmd = new NpgsqlCommand(packageSql, connection))
        {
            cmd.Parameters.AddWithValue("ClinicId", clinicId);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                stats.RemainingAnalyses = reader.GetInt32(0);
                stats.PackageExpiresAt = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
            }
        }

        return stats;
    }

    public async Task<List<ClinicActivityDto>> GetRecentActivityAsync(string clinicId, int limit = 10, string? search = null)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var activities = new List<ClinicActivityDto>();

        // Patient name comes from image owner (ri.UserId). Title uses same for display.
        var sql = @"
            SELECT 
                ar.Id,
                'Analysis' as Type,
                CASE 
                    WHEN up.Id IS NOT NULL THEN 'Phân tích hoàn thành cho ' || COALESCE(TRIM(up.FirstName || ' ' || up.LastName), up.Email, 'Bệnh nhân')
                    ELSE 'Phân tích ảnh võng mạc hoàn thành'
                END as Title,
                COALESCE(TRIM(up.FirstName || ' ' || up.LastName), up.Email, NULL) as PatientName,
                ar.OverallRiskLevel as Description,
                ar.Id as RelatedEntityId,
                COALESCE(ar.AnalysisCompletedAt, ar.CreatedDate) as CreatedAt
            FROM analysis_results ar
            INNER JOIN retinal_images ri ON ri.Id = ar.ImageId AND ri.ClinicId = @ClinicId AND ri.IsDeleted = false
            LEFT JOIN users up ON up.Id = ri.UserId AND up.IsDeleted = false
            WHERE ar.IsDeleted = false 
                AND ar.AnalysisStatus = 'Completed'
                AND (@Search IS NULL OR @Search = '' OR up.FirstName ILIKE '%' || @Search || '%' OR up.LastName ILIKE '%' || @Search || '%' OR up.Email ILIKE '%' || @Search || '%')
            ORDER BY COALESCE(ar.AnalysisCompletedAt, ar.CreatedDate) DESC NULLS LAST
            LIMIT @Limit";

        using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("ClinicId", clinicId);
        cmd.Parameters.AddWithValue("Limit", limit);
        // Fix parameter type inference issue: explicitly set type for nullable string
        var searchParam = new NpgsqlParameter("Search", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = (object?)search ?? DBNull.Value
        };
        cmd.Parameters.Add(searchParam);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            activities.Add(new ClinicActivityDto
            {
                Id = reader.GetString(0),
                Type = reader.GetString(1),
                Title = reader.GetString(2),
                PatientName = reader.IsDBNull(3) ? null : reader.GetString(3),
                Description = reader.IsDBNull(4) ? null : reader.GetString(4),
                RelatedEntityId = reader.IsDBNull(5) ? null : reader.GetString(5),
                CreatedAt = reader.IsDBNull(6) ? DateTime.UtcNow : reader.GetDateTime(6)
            });
        }

        return activities;
    }

    #endregion

    #region Doctor Management

    public async Task<List<ClinicDoctorDto>> GetDoctorsAsync(string clinicId, string? search = null, bool? isActive = null)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var sql = @"
            SELECT 
                cd.Id, cd.DoctorId, d.FirstName, d.LastName, d.Email, d.Phone,
                d.Specialization, d.LicenseNumber, cd.IsPrimary, cd.IsActive, cd.JoinedAt,
                (SELECT COUNT(*) FROM patient_doctor_assignments pda 
                 WHERE pda.DoctorId = d.Id AND pda.ClinicId = @ClinicId AND pda.IsDeleted = false) as PatientCount,
                (SELECT COUNT(*) FROM analysis_results ar 
                 INNER JOIN retinal_images ri ON ri.Id = ar.ImageId
                 INNER JOIN patient_doctor_assignments pda ON pda.UserId = ar.UserId AND pda.DoctorId = d.Id
                 WHERE ri.ClinicId = @ClinicId AND ar.IsDeleted = false) as AnalysisCount
            FROM clinic_doctors cd
            INNER JOIN doctors d ON d.Id = cd.DoctorId
            WHERE cd.ClinicId = @ClinicId AND cd.IsDeleted = false AND d.IsDeleted = false";

        if (!string.IsNullOrEmpty(search))
        {
            sql += @" AND (LOWER(d.FirstName || ' ' || d.LastName) LIKE @Search 
                      OR LOWER(d.Email) LIKE @Search)";
        }

        if (isActive.HasValue)
        {
            sql += " AND cd.IsActive = @IsActive";
        }

        sql += " ORDER BY cd.IsPrimary DESC, d.FirstName, d.LastName";

        using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("ClinicId", clinicId);
        if (!string.IsNullOrEmpty(search))
            cmd.Parameters.AddWithValue("Search", $"%{search.ToLower()}%");
        if (isActive.HasValue)
            cmd.Parameters.AddWithValue("IsActive", isActive.Value);

        var doctors = new List<ClinicDoctorDto>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            doctors.Add(new ClinicDoctorDto
            {
                Id = reader.GetString(0),
                DoctorId = reader.GetString(1),
                FullName = $"{reader.GetString(2)} {reader.GetString(3)}".Trim(),
                Email = reader.GetString(4),
                Phone = reader.IsDBNull(5) ? null : reader.GetString(5),
                Specialization = reader.IsDBNull(6) ? null : reader.GetString(6),
                LicenseNumber = reader.IsDBNull(7) ? null : reader.GetString(7),
                IsPrimary = reader.GetBoolean(8),
                IsActive = reader.GetBoolean(9),
                JoinedAt = reader.GetDateTime(10),
                PatientCount = reader.GetInt32(11),
                AnalysisCount = reader.GetInt32(12)
            });
        }

        return doctors;
    }

    public async Task<ClinicDoctorDto?> GetDoctorByIdAsync(string clinicId, string doctorId)
    {
        var doctors = await GetDoctorsAsync(clinicId);
        return doctors.FirstOrDefault(d => d.DoctorId == doctorId || d.Id == doctorId);
    }

    public async Task<(bool Success, string Message, ClinicDoctorDto? Doctor)> AddDoctorAsync(string clinicId, AddClinicDoctorDto dto)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Check if doctor with email exists
            var checkSql = "SELECT Id FROM doctors WHERE Email = @Email AND IsDeleted = false";
            string? existingDoctorId = null;
            using (var checkCmd = new NpgsqlCommand(checkSql, connection))
            {
                checkCmd.Parameters.AddWithValue("Email", dto.Email);
                existingDoctorId = await checkCmd.ExecuteScalarAsync() as string;
            }

            // Check if password is provided (for creating account)
            bool hasPassword = !string.IsNullOrWhiteSpace(dto.Password);

            using var transaction = await connection.BeginTransactionAsync();
            try
            {
                string doctorId;

                if (existingDoctorId != null)
                {
                    // Check if already in clinic
                    var checkClinicSql = "SELECT Id FROM clinic_doctors WHERE ClinicId = @ClinicId AND DoctorId = @DoctorId AND IsDeleted = false";
                    using (var checkCmd = new NpgsqlCommand(checkClinicSql, connection, transaction))
                    {
                        checkCmd.Parameters.AddWithValue("ClinicId", clinicId);
                        checkCmd.Parameters.AddWithValue("DoctorId", existingDoctorId);
                        var existing = await checkCmd.ExecuteScalarAsync();
                        if (existing != null)
                        {
                            return (false, "Bác sĩ đã có trong phòng khám", null);
                        }
                    }

                    // Đảm bảo bác sĩ cũ cũng có bản ghi trong bảng users để có thể login
                    var checkUserSql = "SELECT Id FROM users WHERE Id = @Id AND IsDeleted = false";
                    using (var checkUserCmd = new NpgsqlCommand(checkUserSql, connection, transaction))
                    {
                        checkUserCmd.Parameters.AddWithValue("Id", existingDoctorId);
                        var userExisting = await checkUserCmd.ExecuteScalarAsync();
                        if (userExisting == null)
                        {
                            // Lấy thông tin từ bảng doctors để tạo user tương ứng
                            var getDoctorSql = @"
                                SELECT FirstName, LastName, Email, Phone, Password 
                                FROM doctors 
                                WHERE Id = @Id AND IsDeleted = false
                                LIMIT 1";

                            string? firstNameFromDoctor = null;
                            string? lastNameFromDoctor = null;
                            string? emailFromDoctor = null;
                            string? phoneFromDoctor = null;
                            string? passwordFromDoctor = null;

                            using (var getDoctorCmd = new NpgsqlCommand(getDoctorSql, connection, transaction))
                            {
                                getDoctorCmd.Parameters.AddWithValue("Id", existingDoctorId);
                                using var reader = await getDoctorCmd.ExecuteReaderAsync();
                                if (await reader.ReadAsync())
                                {
                                    firstNameFromDoctor = reader.IsDBNull(0) ? null : reader.GetString(0);
                                    lastNameFromDoctor = reader.IsDBNull(1) ? null : reader.GetString(1);
                                    emailFromDoctor = reader.IsDBNull(2) ? null : reader.GetString(2);
                                    phoneFromDoctor = reader.IsDBNull(3) ? null : reader.GetString(3);
                                    passwordFromDoctor = reader.IsDBNull(4) ? null : reader.GetString(4);
                                }
                            }

                            if (!string.IsNullOrWhiteSpace(emailFromDoctor))
                            {
                                var userPassword = !string.IsNullOrEmpty(passwordFromDoctor)
                                    ? passwordFromDoctor
                                    : BCrypt.Net.BCrypt.HashPassword(dto.Password ?? "Aura@123");

                                var createUserFromExistingDoctorSql = @"
                                    INSERT INTO users (
                                        Id, FirstName, LastName, Email, Password, Phone,
                                        AuthenticationProvider, IsEmailVerified, IsActive, CreatedDate, IsDeleted
                                    )
                                    VALUES (
                                        @Id, @FirstName, @LastName, @Email, @Password, @Phone,
                                        'email', false, true, @Now, false
                                    )";

                                using (var userCmd = new NpgsqlCommand(createUserFromExistingDoctorSql, connection, transaction))
                                {
                                    userCmd.Parameters.AddWithValue("Id", existingDoctorId);
                                    userCmd.Parameters.AddWithValue("FirstName", (object?)firstNameFromDoctor ?? DBNull.Value);
                                    userCmd.Parameters.AddWithValue("LastName", (object?)lastNameFromDoctor ?? DBNull.Value);
                                    userCmd.Parameters.AddWithValue("Email", emailFromDoctor.ToLower());
                                    userCmd.Parameters.AddWithValue("Password", userPassword);
                                    userCmd.Parameters.AddWithValue("Phone", (object?)phoneFromDoctor ?? DBNull.Value);
                                    userCmd.Parameters.AddWithValue("Now", DateTime.UtcNow);
                                    await userCmd.ExecuteNonQueryAsync();
                                }
                            }
                        }
                    }

                    doctorId = existingDoctorId;
                }
                else
                {
                    // Create new doctor account (hasPassword đã được khai báo ở đầu hàm)
                    doctorId = Guid.NewGuid().ToString();
                    var names = dto.FullName.Split(' ');
                    var firstName = names.Length > 0 ? names[0] : dto.FullName;
                    var lastName = names.Length > 1 ? string.Join(" ", names.Skip(1)) : "";
                    // doctors.LicenseNumber is NOT NULL UNIQUE in schema
                    var licenseNumber = !string.IsNullOrWhiteSpace(dto.LicenseNumber)
                        ? dto.LicenseNumber.Trim()
                        : ("PENDING-" + doctorId);

                    string passwordHash;
                    if (hasPassword)
                    {
                        passwordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password);
                    }
                    else
                    {
                        // Không có password: tạo password random và IsActive=false để không thể login
                        passwordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString());
                    }

                    // Also create a corresponding user account so doctor có thể login qua /login (nếu có password)
                    var now = DateTime.UtcNow;
                    var createUserSql = @"
                        INSERT INTO users (
                            Id, FirstName, LastName, Email, Password, Phone,
                            AuthenticationProvider, IsEmailVerified, IsActive, CreatedDate, IsDeleted
                        )
                        VALUES (
                            @Id, @FirstName, @LastName, @Email, @Password, @Phone,
                            'email', false, @IsActive, @Now, false
                        )";

                    using (var userCmd = new NpgsqlCommand(createUserSql, connection, transaction))
                    {
                        userCmd.Parameters.AddWithValue("Id", doctorId);
                        userCmd.Parameters.AddWithValue("FirstName", firstName);
                        userCmd.Parameters.AddWithValue("LastName", lastName);
                        userCmd.Parameters.AddWithValue("Email", dto.Email.ToLower());
                        userCmd.Parameters.AddWithValue("Password", passwordHash);
                        userCmd.Parameters.AddWithValue("Phone", (object?)dto.Phone ?? DBNull.Value);
                        userCmd.Parameters.AddWithValue("IsActive", hasPassword); // Chỉ active nếu có password
                        userCmd.Parameters.AddWithValue("Now", now);
                        await userCmd.ExecuteNonQueryAsync();
                    }

                    var createDoctorSql = @"
                        INSERT INTO doctors (Id, FirstName, LastName, Email, Password, Phone, Specialization, LicenseNumber, IsActive, CreatedDate, IsDeleted)
                        VALUES (@Id, @FirstName, @LastName, @Email, @Password, @Phone, @Specialization, @LicenseNumber, true, @Now, false)";
                    
                    using (var cmd = new NpgsqlCommand(createDoctorSql, connection, transaction))
                    {
                        cmd.Parameters.AddWithValue("Id", doctorId);
                        cmd.Parameters.AddWithValue("FirstName", firstName);
                        cmd.Parameters.AddWithValue("LastName", lastName);
                        cmd.Parameters.AddWithValue("Email", dto.Email.ToLower());
                        cmd.Parameters.AddWithValue("Password", passwordHash);
                        cmd.Parameters.AddWithValue("Phone", (object?)dto.Phone ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("Specialization", (object?)dto.Specialization ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("LicenseNumber", licenseNumber);
                        cmd.Parameters.AddWithValue("Now", now);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    // Assign Doctor role nếu có password
                    if (hasPassword)
                    {
                        try
                        {
                            var getDoctorRoleSql = @"
                                SELECT Id FROM roles 
                                WHERE LOWER(RoleName) = 'doctor' AND COALESCE(IsDeleted, false) = false 
                                LIMIT 1";
                            
                            string? doctorRoleId = null;
                            using (var roleCmd = new NpgsqlCommand(getDoctorRoleSql, connection, transaction))
                            {
                                var roleResult = await roleCmd.ExecuteScalarAsync();
                                doctorRoleId = roleResult?.ToString();
                            }

                            if (!string.IsNullOrEmpty(doctorRoleId))
                            {
                                var assignRoleSql = @"
                                    INSERT INTO user_roles (Id, UserId, RoleId, IsPrimary, CreatedDate, IsDeleted)
                                    VALUES (@Id, @UserId, @RoleId, true, @CreatedDate, false)
                                    ON CONFLICT (UserId, RoleId) DO NOTHING";
                                
                                using (var assignCmd = new NpgsqlCommand(assignRoleSql, connection, transaction))
                                {
                                    assignCmd.Parameters.AddWithValue("Id", Guid.NewGuid().ToString());
                                    assignCmd.Parameters.AddWithValue("UserId", doctorId);
                                    assignCmd.Parameters.AddWithValue("RoleId", doctorRoleId);
                                    assignCmd.Parameters.AddWithValue("CreatedDate", DateTime.UtcNow.Date);
                                    await assignCmd.ExecuteNonQueryAsync();
                                }
                            }
                        }
                        catch (Exception roleEx)
                        {
                            // Log but don't fail registration if role assignment fails
                            _logger?.LogWarning(roleEx, "Failed to assign Doctor role during clinic doctor registration for {DoctorId}", doctorId);
                        }
                    }
                }

                // Add to clinic_doctors
                var clinicDoctorId = Guid.NewGuid().ToString();
                var addToClinicSql = @"
                    INSERT INTO clinic_doctors (Id, ClinicId, DoctorId, IsPrimary, JoinedAt, IsActive, CreatedDate, IsDeleted)
                    VALUES (@Id, @ClinicId, @DoctorId, @IsPrimary, @Now, true, @Now, false)";
                
                using (var cmd = new NpgsqlCommand(addToClinicSql, connection, transaction))
                {
                    cmd.Parameters.AddWithValue("Id", clinicDoctorId);
                    cmd.Parameters.AddWithValue("ClinicId", clinicId);
                    cmd.Parameters.AddWithValue("DoctorId", doctorId);
                    cmd.Parameters.AddWithValue("IsPrimary", dto.IsPrimary);
                    cmd.Parameters.AddWithValue("Now", DateTime.UtcNow);
                    await cmd.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();

                var doctor = await GetDoctorByIdAsync(clinicId, doctorId);
                string message = existingDoctorId != null 
                    ? "Đã thêm bác sĩ vào phòng khám" 
                    : (hasPassword 
                        ? "Đã tạo tài khoản và thêm bác sĩ vào phòng khám. Bác sĩ có thể đăng nhập bằng email và mật khẩu đã cung cấp." 
                        : "Đã thêm bác sĩ vào phòng khám (chưa có tài khoản đăng nhập)");
                return (true, message, doctor);
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error adding doctor to clinic {ClinicId}", clinicId);
            return (false, "Đã xảy ra lỗi khi thêm bác sĩ", null);
        }
    }

    public async Task<(bool Success, string Message)> UpdateDoctorAsync(string clinicId, string doctorId, UpdateClinicDoctorDto dto)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var updates = new List<string>();
            var parameters = new List<NpgsqlParameter>
            {
                new("ClinicId", clinicId),
                new("DoctorId", doctorId),
                new("Now", DateTime.UtcNow)
            };

            if (dto.IsPrimary.HasValue)
            {
                updates.Add("IsPrimary = @IsPrimary");
                parameters.Add(new("IsPrimary", dto.IsPrimary.Value));
            }
            if (dto.IsActive.HasValue)
            {
                updates.Add("IsActive = @IsActive");
                parameters.Add(new("IsActive", dto.IsActive.Value));
            }

            if (updates.Count == 0)
                return (true, "Không có thay đổi");

            updates.Add("UpdatedDate = @Now");

            var sql = $@"UPDATE clinic_doctors SET {string.Join(", ", updates)}
                        WHERE ClinicId = @ClinicId AND DoctorId = @DoctorId AND IsDeleted = false";

            using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddRange(parameters.ToArray());
            var rows = await cmd.ExecuteNonQueryAsync();

            return rows > 0 ? (true, "Đã cập nhật thông tin bác sĩ") : (false, "Không tìm thấy bác sĩ");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error updating doctor {DoctorId} in clinic {ClinicId}", doctorId, clinicId);
            return (false, "Đã xảy ra lỗi khi cập nhật");
        }
    }

    public async Task<(bool Success, string Message)> RemoveDoctorAsync(string clinicId, string doctorId)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"UPDATE clinic_doctors SET IsActive = false, IsDeleted = true, UpdatedDate = @Now
                       WHERE ClinicId = @ClinicId AND DoctorId = @DoctorId AND IsDeleted = false";

            using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("ClinicId", clinicId);
            cmd.Parameters.AddWithValue("DoctorId", doctorId);
            cmd.Parameters.AddWithValue("Now", DateTime.UtcNow);
            var rows = await cmd.ExecuteNonQueryAsync();

            return rows > 0 ? (true, "Đã xóa bác sĩ khỏi phòng khám") : (false, "Không tìm thấy bác sĩ");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error removing doctor {DoctorId} from clinic {ClinicId}", doctorId, clinicId);
            return (false, "Đã xảy ra lỗi khi xóa bác sĩ");
        }
    }

    public async Task<(bool Success, string Message)> SetPrimaryDoctorAsync(string clinicId, string doctorId)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var transaction = await connection.BeginTransactionAsync();
            try
            {
                // Remove primary from all doctors
                var removePrimarySql = @"UPDATE clinic_doctors SET IsPrimary = false, UpdatedDate = @Now
                                        WHERE ClinicId = @ClinicId AND IsDeleted = false";
                using (var cmd = new NpgsqlCommand(removePrimarySql, connection, transaction))
                {
                    cmd.Parameters.AddWithValue("ClinicId", clinicId);
                    cmd.Parameters.AddWithValue("Now", DateTime.UtcNow);
                    await cmd.ExecuteNonQueryAsync();
                }

                // Set this doctor as primary
                var setPrimarySql = @"UPDATE clinic_doctors SET IsPrimary = true, UpdatedDate = @Now
                                     WHERE ClinicId = @ClinicId AND DoctorId = @DoctorId AND IsDeleted = false";
                using (var cmd = new NpgsqlCommand(setPrimarySql, connection, transaction))
                {
                    cmd.Parameters.AddWithValue("ClinicId", clinicId);
                    cmd.Parameters.AddWithValue("DoctorId", doctorId);
                    cmd.Parameters.AddWithValue("Now", DateTime.UtcNow);
                    var rows = await cmd.ExecuteNonQueryAsync();
                    if (rows == 0)
                    {
                        await transaction.RollbackAsync();
                        return (false, "Không tìm thấy bác sĩ");
                    }
                }

                await transaction.CommitAsync();
                return (true, "Đã đặt làm bác sĩ chính");
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error setting primary doctor {DoctorId} in clinic {ClinicId}", doctorId, clinicId);
            return (false, "Đã xảy ra lỗi");
        }
    }

    #endregion

    #region Patient Management

    public async Task<List<ClinicPatientDto>> GetPatientsAsync(string clinicId, string? search = null, string? doctorId = null, string? riskLevel = null, bool? isActive = null)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        var sql = @"
            SELECT 
                cu.Id, cu.UserId, u.FirstName, u.LastName, u.Email, u.Phone,
                u.Dob, u.Gender, u.Address, cu.IsActive, cu.RegisteredAt,
                (SELECT pda.DoctorId FROM patient_doctor_assignments pda 
                 WHERE pda.UserId = u.Id AND pda.ClinicId = @ClinicId AND pda.IsPrimary = true AND pda.IsDeleted = false
                 LIMIT 1) as AssignedDoctorId,
                (SELECT d.FirstName || ' ' || d.LastName FROM patient_doctor_assignments pda 
                 INNER JOIN doctors d ON d.Id = pda.DoctorId
                 WHERE pda.UserId = u.Id AND pda.ClinicId = @ClinicId AND pda.IsPrimary = true AND pda.IsDeleted = false
                 LIMIT 1) as AssignedDoctorName,
                (SELECT COUNT(*) FROM analysis_results ar 
                 INNER JOIN retinal_images ri ON ri.Id = ar.ImageId
                 WHERE ri.UserId = u.Id AND ri.ClinicId = @ClinicId AND ar.IsDeleted = false) as AnalysisCount,
                (SELECT ar.OverallRiskLevel FROM analysis_results ar 
                 INNER JOIN retinal_images ri ON ri.Id = ar.ImageId
                 WHERE ri.UserId = u.Id AND ri.ClinicId = @ClinicId AND ar.IsDeleted = false AND ar.AnalysisStatus = 'Completed'
                 ORDER BY ar.AnalysisCompletedAt DESC NULLS LAST LIMIT 1) as LatestRiskLevel,
                (SELECT ar.AnalysisCompletedAt FROM analysis_results ar 
                 INNER JOIN retinal_images ri ON ri.Id = ar.ImageId
                 WHERE ri.UserId = u.Id AND ri.ClinicId = @ClinicId AND ar.IsDeleted = false AND ar.AnalysisStatus = 'Completed'
                 ORDER BY ar.AnalysisCompletedAt DESC NULLS LAST LIMIT 1) as LastAnalysisDate
            FROM clinic_users cu
            INNER JOIN users u ON u.Id = cu.UserId
            WHERE cu.ClinicId = @ClinicId AND cu.IsDeleted = false AND u.IsDeleted = false";

        if (!string.IsNullOrEmpty(search))
        {
            sql += @" AND (LOWER(u.FirstName || ' ' || u.LastName) LIKE @Search 
                      OR LOWER(u.Email) LIKE @Search
                      OR u.Phone LIKE @Search)";
        }

        if (!string.IsNullOrEmpty(doctorId))
        {
            sql += @" AND EXISTS (SELECT 1 FROM patient_doctor_assignments pda 
                      WHERE pda.UserId = u.Id AND pda.DoctorId = @DoctorId AND pda.ClinicId = @ClinicId AND pda.IsDeleted = false)";
        }

        if (isActive.HasValue)
        {
            sql += " AND cu.IsActive = @IsActive";
        }

        sql += " ORDER BY cu.RegisteredAt DESC";

        using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("ClinicId", clinicId);
        if (!string.IsNullOrEmpty(search))
            cmd.Parameters.AddWithValue("Search", $"%{search.ToLower()}%");
        if (!string.IsNullOrEmpty(doctorId))
            cmd.Parameters.AddWithValue("DoctorId", doctorId);
        if (isActive.HasValue)
            cmd.Parameters.AddWithValue("IsActive", isActive.Value);

        var patients = new List<ClinicPatientDto>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var patient = new ClinicPatientDto
            {
                Id = reader.GetString(0),
                UserId = reader.GetString(1),
                FullName = $"{reader.GetString(2)} {reader.GetString(3)}".Trim(),
                Email = reader.GetString(4),
                Phone = reader.IsDBNull(5) ? null : reader.GetString(5),
                DateOfBirth = reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                Gender = reader.IsDBNull(7) ? null : reader.GetString(7),
                Address = reader.IsDBNull(8) ? null : reader.GetString(8),
                IsActive = reader.GetBoolean(9),
                RegisteredAt = reader.GetDateTime(10),
                AssignedDoctorId = reader.IsDBNull(11) ? null : reader.GetString(11),
                AssignedDoctorName = reader.IsDBNull(12) ? null : reader.GetString(12),
                AnalysisCount = reader.GetInt32(13),
                LatestRiskLevel = reader.IsDBNull(14) ? null : reader.GetString(14),
                LastAnalysisDate = reader.IsDBNull(15) ? null : reader.GetDateTime(15)
            };

            // Filter by risk level if specified
            if (!string.IsNullOrEmpty(riskLevel) && patient.LatestRiskLevel != riskLevel)
                continue;

            patients.Add(patient);
        }

        return patients;
    }

    public async Task<ClinicPatientDto?> GetPatientByIdAsync(string clinicId, string patientId)
    {
        var patients = await GetPatientsAsync(clinicId);
        return patients.FirstOrDefault(p => p.UserId == patientId || p.Id == patientId);
    }

    public async Task<(bool Success, string Message, ClinicPatientDto? Patient)> RegisterPatientAsync(string clinicId, RegisterClinicPatientDto dto)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Check if user with email exists
            var checkSql = "SELECT Id FROM users WHERE Email = @Email AND IsDeleted = false";
            string? existingUserId = null;
            using (var checkCmd = new NpgsqlCommand(checkSql, connection))
            {
                checkCmd.Parameters.AddWithValue("Email", dto.Email);
                existingUserId = await checkCmd.ExecuteScalarAsync() as string;
            }

            // Check if password is provided (for creating account)
            bool hasPassword = !string.IsNullOrWhiteSpace(dto.Password);

            using var transaction = await connection.BeginTransactionAsync();
            try
            {
                string userId;

                if (existingUserId != null)
                {
                    // Check if already in clinic
                    var checkClinicSql = "SELECT Id FROM clinic_users WHERE ClinicId = @ClinicId AND UserId = @UserId AND IsDeleted = false";
                    using (var checkCmd = new NpgsqlCommand(checkClinicSql, connection, transaction))
                    {
                        checkCmd.Parameters.AddWithValue("ClinicId", clinicId);
                        checkCmd.Parameters.AddWithValue("UserId", existingUserId);
                        var existing = await checkCmd.ExecuteScalarAsync();
                        if (existing != null)
                        {
                            return (false, "Bệnh nhân đã có trong phòng khám", null);
                        }
                    }
                    userId = existingUserId;
                }
                else
                {
                    // Chỉ tạo user account nếu có password (hasPassword đã được khai báo ở đầu hàm)
                    if (!hasPassword)
                    {
                        // Không có password: không tạo account, chỉ thêm vào clinic_users sau khi tạo user placeholder
                        // Tạo user với password random và IsActive=false để không thể login
                        userId = Guid.NewGuid().ToString();
                        var randomPassword = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString());
                        var names = dto.FullName.Split(' ');
                        var firstName = names.Length > 0 ? names[0] : dto.FullName;
                        var lastName = names.Length > 1 ? string.Join(" ", names.Skip(1)) : "";

                        var createUserSql = @"
                            INSERT INTO users (Id, FirstName, LastName, Email, Password, Phone, Dob, Gender, Address, IsActive, AuthenticationProvider, CreatedDate, IsDeleted)
                            VALUES (@Id, @FirstName, @LastName, @Email, @Password, @Phone, @Dob, @Gender, @Address, false, 'email', @Now, false)";
                        
                        using (var cmd = new NpgsqlCommand(createUserSql, connection, transaction))
                        {
                            cmd.Parameters.AddWithValue("Id", userId);
                            cmd.Parameters.AddWithValue("FirstName", firstName);
                            cmd.Parameters.AddWithValue("LastName", lastName);
                            cmd.Parameters.AddWithValue("Email", dto.Email);
                            cmd.Parameters.AddWithValue("Password", randomPassword);
                            cmd.Parameters.AddWithValue("Phone", (object?)dto.Phone ?? DBNull.Value);
                            cmd.Parameters.AddWithValue("Dob", (object?)dto.DateOfBirth ?? DBNull.Value);
                            cmd.Parameters.AddWithValue("Gender", (object?)dto.Gender ?? DBNull.Value);
                            cmd.Parameters.AddWithValue("Address", (object?)dto.Address ?? DBNull.Value);
                            cmd.Parameters.AddWithValue("Now", DateTime.UtcNow);
                            await cmd.ExecuteNonQueryAsync();
                        }
                    }
                    else
                    {
                        // Có password: tạo user account với password và assign Patient role
                        userId = Guid.NewGuid().ToString();
                        var passwordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password);
                        var names = dto.FullName.Split(' ');
                        var firstName = names.Length > 0 ? names[0] : dto.FullName;
                        var lastName = names.Length > 1 ? string.Join(" ", names.Skip(1)) : "";

                        var createUserSql = @"
                            INSERT INTO users (Id, FirstName, LastName, Email, Password, Phone, Dob, Gender, Address, IsActive, AuthenticationProvider, CreatedDate, IsDeleted)
                            VALUES (@Id, @FirstName, @LastName, @Email, @Password, @Phone, @Dob, @Gender, @Address, true, 'email', @Now, false)";
                        
                        using (var cmd = new NpgsqlCommand(createUserSql, connection, transaction))
                        {
                            cmd.Parameters.AddWithValue("Id", userId);
                            cmd.Parameters.AddWithValue("FirstName", firstName);
                            cmd.Parameters.AddWithValue("LastName", lastName);
                            cmd.Parameters.AddWithValue("Email", dto.Email);
                            cmd.Parameters.AddWithValue("Password", passwordHash);
                            cmd.Parameters.AddWithValue("Phone", (object?)dto.Phone ?? DBNull.Value);
                            cmd.Parameters.AddWithValue("Dob", (object?)dto.DateOfBirth ?? DBNull.Value);
                            cmd.Parameters.AddWithValue("Gender", (object?)dto.Gender ?? DBNull.Value);
                            cmd.Parameters.AddWithValue("Address", (object?)dto.Address ?? DBNull.Value);
                            cmd.Parameters.AddWithValue("Now", DateTime.UtcNow);
                            await cmd.ExecuteNonQueryAsync();
                        }

                        // Assign Patient role
                        try
                        {
                            var getPatientRoleSql = @"
                                SELECT Id FROM roles 
                                WHERE LOWER(RoleName) = 'patient' AND COALESCE(IsDeleted, false) = false 
                                LIMIT 1";
                            
                            string? patientRoleId = null;
                            using (var roleCmd = new NpgsqlCommand(getPatientRoleSql, connection, transaction))
                            {
                                var roleResult = await roleCmd.ExecuteScalarAsync();
                                patientRoleId = roleResult?.ToString();
                            }

                            if (!string.IsNullOrEmpty(patientRoleId))
                            {
                                var assignRoleSql = @"
                                    INSERT INTO user_roles (Id, UserId, RoleId, IsPrimary, CreatedDate, IsDeleted)
                                    VALUES (@Id, @UserId, @RoleId, true, @CreatedDate, false)
                                    ON CONFLICT (UserId, RoleId) DO NOTHING";
                                
                                using (var assignCmd = new NpgsqlCommand(assignRoleSql, connection, transaction))
                                {
                                    assignCmd.Parameters.AddWithValue("Id", Guid.NewGuid().ToString());
                                    assignCmd.Parameters.AddWithValue("UserId", userId);
                                    assignCmd.Parameters.AddWithValue("RoleId", patientRoleId);
                                    assignCmd.Parameters.AddWithValue("CreatedDate", DateTime.UtcNow.Date);
                                    await assignCmd.ExecuteNonQueryAsync();
                                }
                            }
                        }
                        catch (Exception roleEx)
                        {
                            // Log but don't fail registration if role assignment fails
                            _logger?.LogWarning(roleEx, "Failed to assign Patient role during clinic patient registration for {UserId}", userId);
                        }
                    }
                }

                // Add to clinic_users
                var clinicUserId = Guid.NewGuid().ToString();
                var addToClinicSql = @"
                    INSERT INTO clinic_users (Id, ClinicId, UserId, RegisteredAt, IsActive, CreatedDate, IsDeleted)
                    VALUES (@Id, @ClinicId, @UserId, @Now, true, @Now, false)";
                
                using (var cmd = new NpgsqlCommand(addToClinicSql, connection, transaction))
                {
                    cmd.Parameters.AddWithValue("Id", clinicUserId);
                    cmd.Parameters.AddWithValue("ClinicId", clinicId);
                    cmd.Parameters.AddWithValue("UserId", userId);
                    cmd.Parameters.AddWithValue("Now", DateTime.UtcNow);
                    await cmd.ExecuteNonQueryAsync();
                }

                // Assign doctor if specified
                if (!string.IsNullOrEmpty(dto.AssignedDoctorId))
                {
                    var assignmentId = Guid.NewGuid().ToString();
                    var assignSql = @"
                        INSERT INTO patient_doctor_assignments (Id, UserId, DoctorId, ClinicId, IsPrimary, IsActive, CreatedDate, IsDeleted)
                        VALUES (@Id, @UserId, @DoctorId, @ClinicId, true, true, @Now, false)";
                    
                    using (var cmd = new NpgsqlCommand(assignSql, connection, transaction))
                    {
                        cmd.Parameters.AddWithValue("Id", assignmentId);
                        cmd.Parameters.AddWithValue("UserId", userId);
                        cmd.Parameters.AddWithValue("DoctorId", dto.AssignedDoctorId);
                        cmd.Parameters.AddWithValue("ClinicId", clinicId);
                        cmd.Parameters.AddWithValue("Now", DateTime.UtcNow);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                await transaction.CommitAsync();

                var patient = await GetPatientByIdAsync(clinicId, userId);
                string message = existingUserId != null 
                    ? "Đã thêm bệnh nhân vào phòng khám" 
                    : (hasPassword 
                        ? "Đã tạo tài khoản và đăng ký bệnh nhân. Bệnh nhân có thể đăng nhập bằng email và mật khẩu đã cung cấp." 
                        : "Đã đăng ký bệnh nhân vào phòng khám (chưa có tài khoản đăng nhập)");
                return (true, message, patient);
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error registering patient to clinic {ClinicId}", clinicId);
            return (false, "Đã xảy ra lỗi khi đăng ký bệnh nhân", null);
        }
    }

    public async Task<(bool Success, string Message)> UpdatePatientAsync(string clinicId, string patientId, UpdateClinicPatientDto dto)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var updates = new List<string>();
            var parameters = new List<NpgsqlParameter>
            {
                new("ClinicId", clinicId),
                new("UserId", patientId),
                new("Now", DateTime.UtcNow)
            };

            if (dto.IsActive.HasValue)
            {
                updates.Add("IsActive = @IsActive");
                parameters.Add(new("IsActive", dto.IsActive.Value));
            }

            if (updates.Count == 0)
                return (true, "Không có thay đổi");

            updates.Add("UpdatedDate = @Now");

            var sql = $@"UPDATE clinic_users SET {string.Join(", ", updates)}
                        WHERE ClinicId = @ClinicId AND UserId = @UserId AND IsDeleted = false";

            using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddRange(parameters.ToArray());
            var rows = await cmd.ExecuteNonQueryAsync();

            return rows > 0 ? (true, "Đã cập nhật thông tin bệnh nhân") : (false, "Không tìm thấy bệnh nhân");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error updating patient {PatientId} in clinic {ClinicId}", patientId, clinicId);
            return (false, "Đã xảy ra lỗi khi cập nhật");
        }
    }

    public async Task<(bool Success, string Message)> RemovePatientAsync(string clinicId, string patientId)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"UPDATE clinic_users SET IsActive = false, IsDeleted = true, UpdatedDate = @Now
                       WHERE ClinicId = @ClinicId AND UserId = @UserId AND IsDeleted = false";

            using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("ClinicId", clinicId);
            cmd.Parameters.AddWithValue("UserId", patientId);
            cmd.Parameters.AddWithValue("Now", DateTime.UtcNow);
            var rows = await cmd.ExecuteNonQueryAsync();

            return rows > 0 ? (true, "Đã xóa bệnh nhân khỏi phòng khám") : (false, "Không tìm thấy bệnh nhân");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error removing patient {PatientId} from clinic {ClinicId}", patientId, clinicId);
            return (false, "Đã xảy ra lỗi khi xóa bệnh nhân");
        }
    }

    public async Task<(bool Success, string Message)> AssignDoctorToPatientAsync(string clinicId, string patientId, AssignDoctorDto dto)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var transaction = await connection.BeginTransactionAsync();
            try
            {
                // If primary, remove existing primary assignment
                if (dto.IsPrimary)
                {
                    var removePrimarySql = @"UPDATE patient_doctor_assignments SET IsPrimary = false, UpdatedDate = @Now
                                            WHERE UserId = @UserId AND ClinicId = @ClinicId AND IsPrimary = true AND IsDeleted = false";
                    using (var cmd = new NpgsqlCommand(removePrimarySql, connection, transaction))
                    {
                        cmd.Parameters.AddWithValue("UserId", patientId);
                        cmd.Parameters.AddWithValue("ClinicId", clinicId);
                        cmd.Parameters.AddWithValue("Now", DateTime.UtcNow);
                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                // Check if assignment already exists
                var checkSql = @"SELECT Id FROM patient_doctor_assignments 
                                WHERE UserId = @UserId AND DoctorId = @DoctorId AND ClinicId = @ClinicId AND IsDeleted = false";
                using (var checkCmd = new NpgsqlCommand(checkSql, connection, transaction))
                {
                    checkCmd.Parameters.AddWithValue("UserId", patientId);
                    checkCmd.Parameters.AddWithValue("DoctorId", dto.DoctorId);
                    checkCmd.Parameters.AddWithValue("ClinicId", clinicId);
                    var existingId = await checkCmd.ExecuteScalarAsync() as string;

                    if (existingId != null)
                    {
                        // Update existing
                        var updateSql = @"UPDATE patient_doctor_assignments SET IsPrimary = @IsPrimary, IsActive = true, UpdatedDate = @Now
                                         WHERE Id = @Id";
                        using var updateCmd = new NpgsqlCommand(updateSql, connection, transaction);
                        updateCmd.Parameters.AddWithValue("Id", existingId);
                        updateCmd.Parameters.AddWithValue("IsPrimary", dto.IsPrimary);
                        updateCmd.Parameters.AddWithValue("Now", DateTime.UtcNow);
                        await updateCmd.ExecuteNonQueryAsync();
                    }
                    else
                    {
                        // Create new
                        var assignmentId = Guid.NewGuid().ToString();
                        var insertSql = @"
                            INSERT INTO patient_doctor_assignments (Id, UserId, DoctorId, ClinicId, IsPrimary, IsActive, CreatedDate, IsDeleted)
                            VALUES (@Id, @UserId, @DoctorId, @ClinicId, @IsPrimary, true, @Now, false)";
                        using var insertCmd = new NpgsqlCommand(insertSql, connection, transaction);
                        insertCmd.Parameters.AddWithValue("Id", assignmentId);
                        insertCmd.Parameters.AddWithValue("UserId", patientId);
                        insertCmd.Parameters.AddWithValue("DoctorId", dto.DoctorId);
                        insertCmd.Parameters.AddWithValue("ClinicId", clinicId);
                        insertCmd.Parameters.AddWithValue("IsPrimary", dto.IsPrimary);
                        insertCmd.Parameters.AddWithValue("Now", DateTime.UtcNow);
                        await insertCmd.ExecuteNonQueryAsync();
                    }
                }

                await transaction.CommitAsync();
                return (true, "Đã gán bác sĩ cho bệnh nhân");
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error assigning doctor to patient {PatientId} in clinic {ClinicId}", patientId, clinicId);
            return (false, "Đã xảy ra lỗi khi gán bác sĩ");
        }
    }

    #endregion
}
