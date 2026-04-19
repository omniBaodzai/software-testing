using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aura.Application.DTOs.Auth;
using Aura.Core.Entities;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aura.Application.Services.Auth;

public class AuthService : IAuthService
{
    private readonly IJwtService _jwtService;
    private readonly IEmailService _emailService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthService> _logger;
    private readonly IDistributedCache? _distributedCache;
    
    // TODO: Inject actual repositories when database is set up
    // private readonly IUserRepository _userRepository;
    // private readonly IRefreshTokenRepository _refreshTokenRepository;
    // private readonly IEmailVerificationTokenRepository _emailVerificationTokenRepository;
    // private readonly IPasswordResetTokenRepository _passwordResetTokenRepository;

    // In-memory storage for development (replace with actual database)
    private static readonly List<User> _users = new();
    private static readonly List<RefreshToken> _refreshTokens = new();
    private static readonly List<EmailVerificationToken> _emailVerificationTokens = new();
    private static readonly List<PasswordResetToken> _passwordResetTokens = new();

    public AuthService(
        IJwtService jwtService,
        IEmailService emailService,
        IConfiguration configuration,
        ILogger<AuthService> logger,
        IDistributedCache? distributedCache = null)
    {
        _jwtService = jwtService;
        _emailService = emailService;
        _configuration = configuration;
        _logger = logger;
        _distributedCache = distributedCache;
    }

    public async Task<AuthResponseDto> RegisterAsync(RegisterDto registerDto)
    {
        try
        {
            using var conn = OpenConnection();
            var isDoctorRegistration = registerDto.UserType?.ToLower() == "doctor";
            
            // Check if email already exists in database
            var checkEmailQuery = @"
                SELECT id, email 
                FROM users 
                WHERE email = @email AND isdeleted = false";
            
            using (var cmd = new NpgsqlCommand(checkEmailQuery, conn))
            {
                cmd.Parameters.AddWithValue("email", registerDto.Email.ToLower());
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    return new AuthResponseDto
                    {
                        Success = false,
                        Message = "Email đã được sử dụng"
                    };
                }
            }

            // Transaction để đảm bảo: đăng ký bác sĩ thì phải có cả users + doctors (nếu fail -> rollback)
            await using var tx = await conn.BeginTransactionAsync();

            _logger.LogInformation("Register: bắt đầu đăng ký, UserType={UserType}, Email={Email}", registerDto.UserType, registerDto.Email);

            // Create new user
            var userId = Guid.NewGuid().ToString();
            var hashedPassword = HashPassword(registerDto.Password);
            var username = registerDto.Email.Split('@')[0]; // Generate username from email
            
            var insertQuery = @"
                INSERT INTO users (id, email, password, firstname, lastname, phone, 
                                 authenticationprovider, isemailverified, isactive, 
                                 createddate, username, country)
                VALUES (@id, @email, @password, @firstname, @lastname, @phone, 
                       @provider, @isemailverified, @isactive, 
                       @createddate, @username, @country)";
            
            using (var cmd = new NpgsqlCommand(insertQuery, conn))
            {
                cmd.Transaction = tx;
                cmd.Parameters.AddWithValue("id", userId);
                cmd.Parameters.AddWithValue("email", registerDto.Email.ToLower());
                cmd.Parameters.AddWithValue("password", hashedPassword);
                cmd.Parameters.AddWithValue("firstname", (object?)registerDto.FirstName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("lastname", (object?)registerDto.LastName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("phone", (object?)registerDto.Phone ?? DBNull.Value);
                cmd.Parameters.AddWithValue("provider", "email");
                cmd.Parameters.AddWithValue("isemailverified", false);
                cmd.Parameters.AddWithValue("isactive", true);
                cmd.Parameters.AddWithValue("createddate", DateTime.UtcNow.Date);
                cmd.Parameters.AddWithValue("username", username);
                cmd.Parameters.AddWithValue("country", "Vietnam");
                
                cmd.ExecuteNonQuery();
            }

            _logger.LogInformation("Register: đã insert vào bảng users, UserId={UserId}", userId);

            // Read the newly created user
            var getUserQuery = @"
                SELECT id, email, password, firstname, lastname, phone, authenticationprovider, 
                       isemailverified, isactive, lastloginat, createddate, profileimageurl,
                       provideruserid, username, country, dob, gender, address
                FROM users 
                WHERE id = @id AND isdeleted = false";
            
            User? user = null;
            using (var cmd = new NpgsqlCommand(getUserQuery, conn))
            {
                cmd.Transaction = tx;
                cmd.Parameters.AddWithValue("id", userId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    user = MapUserFromReader(reader);
                }
            }

            if (user == null)
            {
                await tx.RollbackAsync();
                _logger.LogError("Failed to retrieve user after registration: {UserId}", userId);
                return new AuthResponseDto
                {
                    Success = false,
                    Message = "Đăng ký thất bại. Không thể tạo tài khoản."
                };
            }

            // If registering as doctor, create doctor record (BẮT BUỘC phải thành công)
            if (isDoctorRegistration)
            {
                try
                {
                    // Check if doctor already exists
                    var checkDoctorQuery = @"
                        SELECT id FROM doctors 
                        WHERE id = @userId OR email = @email";
                    
                    bool doctorExists = false;
                    using (var checkCmd = new NpgsqlCommand(checkDoctorQuery, conn))
                    {
                        checkCmd.Transaction = tx;
                        checkCmd.Parameters.AddWithValue("userId", userId);
                        checkCmd.Parameters.AddWithValue("email", registerDto.Email.ToLower());
                        using var checkReader = checkCmd.ExecuteReader();
                        doctorExists = checkReader.Read();
                    }

                    if (!doctorExists)
                    {
                        // Generate license number if not provided
                        var licenseNumber = !string.IsNullOrWhiteSpace(registerDto.LicenseNumber)
                            ? registerDto.LicenseNumber
                            : $"DR-{userId.Substring(0, Math.Min(8, userId.Length)).ToUpper()}-{DateTime.UtcNow:yyyyMMdd}";

                        var insertDoctorQuery = @"
                            INSERT INTO doctors (
                                id, email, username, firstname, lastname, phone,
                                licensenumber, specialization, yearsofexperience,
                                qualification, hospitalaffiliation,
                                isverified, isactive, createddate, isdeleted
                            )
                            VALUES (
                                @id, @email, @username, @firstname, @lastname, @phone,
                                @licensenumber, @specialization, @yearsofexperience,
                                @qualification, @hospitalaffiliation,
                                false, true, @createddate, false
                            )";

                        using (var doctorCmd = new NpgsqlCommand(insertDoctorQuery, conn))
                        {
                            doctorCmd.Transaction = tx;
                            doctorCmd.Parameters.AddWithValue("id", userId);
                            doctorCmd.Parameters.AddWithValue("email", registerDto.Email.ToLower());
                            doctorCmd.Parameters.AddWithValue("username", (object?)username ?? DBNull.Value);
                            doctorCmd.Parameters.AddWithValue("firstname", (object?)registerDto.FirstName ?? DBNull.Value);
                            doctorCmd.Parameters.AddWithValue("lastname", (object?)registerDto.LastName ?? DBNull.Value);
                            doctorCmd.Parameters.AddWithValue("phone", (object?)registerDto.Phone ?? DBNull.Value);
                            doctorCmd.Parameters.AddWithValue("licensenumber", licenseNumber);
                            doctorCmd.Parameters.AddWithValue("specialization", (object?)registerDto.Specialization ?? DBNull.Value);
                            doctorCmd.Parameters.AddWithValue("yearsofexperience", (object?)registerDto.YearsOfExperience ?? DBNull.Value);
                            doctorCmd.Parameters.AddWithValue("qualification", (object?)registerDto.Qualification ?? DBNull.Value);
                            doctorCmd.Parameters.AddWithValue("hospitalaffiliation", (object?)registerDto.HospitalAffiliation ?? DBNull.Value);
                            doctorCmd.Parameters.AddWithValue("createddate", DateTime.UtcNow.Date);
                            
                            doctorCmd.ExecuteNonQuery();
                        }

                        _logger.LogInformation("Register: đã insert vào bảng doctors, DoctorId={UserId}, Email={Email}", userId, registerDto.Email);

                        // Try to assign Doctor role (if role exists)
                        try
                        {
                            // Schema: roles table có cột RoleName (PostgreSQL lưu lowercase: rolename)
                            var getDoctorRoleQuery = @"
                                SELECT id FROM roles 
                                WHERE LOWER(rolename) = 'doctor' AND COALESCE(isdeleted, false) = false 
                                LIMIT 1";
                            
                            string? doctorRoleId = null;
                            using (var roleCmd = new NpgsqlCommand(getDoctorRoleQuery, conn))
                            {
                                roleCmd.Transaction = tx;
                                var roleResult = roleCmd.ExecuteScalar();
                                doctorRoleId = roleResult?.ToString();
                            }

                            if (!string.IsNullOrEmpty(doctorRoleId))
                            {
                                var assignRoleQuery = @"
                                    INSERT INTO user_roles (id, userid, roleid, isprimary, createddate, isdeleted)
                                    VALUES (@id, @userid, @roleid, true, @createddate, false)
                                    ON CONFLICT (userid, roleid) DO NOTHING";
                                
                                using (var assignCmd = new NpgsqlCommand(assignRoleQuery, conn))
                                {
                                    assignCmd.Transaction = tx;
                                    assignCmd.Parameters.AddWithValue("id", Guid.NewGuid().ToString());
                                    assignCmd.Parameters.AddWithValue("userid", userId);
                                    assignCmd.Parameters.AddWithValue("roleid", doctorRoleId);
                                    assignCmd.Parameters.AddWithValue("createddate", DateTime.UtcNow.Date);
                                    assignCmd.ExecuteNonQuery();
                                }
                            }
                        }
                        catch (Exception roleEx)
                        {
                            // Log but don't fail registration if role assignment fails
                            _logger.LogWarning(roleEx, "Failed to assign Doctor role during registration for {UserId}", userId);
                        }

                        _logger.LogInformation("Doctor record created during registration: {Email}, ID: {UserId}", user.Email, userId);
                    }
                }
                catch (Exception doctorEx)
                {
                    // Bắt buộc: đăng ký bác sĩ mà không tạo được doctor record -> rollback
                    await tx.RollbackAsync();
                    _logger.LogError(doctorEx, "Failed to create doctor record during registration for {UserId}", userId);
                    return new AuthResponseDto
                    {
                        Success = false,
                        Message = "Đăng ký bác sĩ thất bại. Không thể tạo hồ sơ bác sĩ. Vui lòng thử lại."
                    };
                }
            }

            // Commit transaction sau khi đã insert users (+ doctors nếu là bác sĩ)
            await tx.CommitAsync();
            _logger.LogInformation("Register: đã commit transaction. Users + Doctors (nếu bác sĩ) đã lưu DB. UserId={UserId}, Email={Email}, IsDoctor={IsDoctor}", userId, registerDto.Email, isDoctorRegistration);

            // Create email verification token (still in-memory for now)
            var verificationToken = new EmailVerificationToken
            {
                UserId = user.Id,
                Token = GenerateRandomToken(),
                ExpiresAt = DateTime.UtcNow.AddHours(24)
            };
            _emailVerificationTokens.Add(verificationToken);

            // Send verification email
            await _emailService.SendVerificationEmailAsync(user.Email, verificationToken.Token, user.FirstName);

            _logger.LogInformation("User registered successfully and saved to database: {Email}, ID: {UserId}, Type: {UserType}", 
                user.Email, userId, registerDto.UserType ?? "patient");

            return new AuthResponseDto
            {
                Success = true,
                Message = "Đăng ký thành công. Vui lòng kiểm tra email để xác thực tài khoản.",
                User = MapToUserInfo(user)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Registration failed for {Email}", registerDto.Email);
            return new AuthResponseDto
            {
                Success = false,
                Message = "Đăng ký thất bại. Vui lòng thử lại."
            };
        }
    }

    public Task<AuthResponseDto> LoginAsync(LoginDto loginDto)
    {
        try
        {
            using var conn = OpenConnection();
            
            // Get user from database
            var getUserQuery = @"
                SELECT id, email, password, firstname, lastname, phone, authenticationprovider, 
                       isemailverified, isactive, lastloginat, createddate, profileimageurl,
                       provideruserid, username, country, dob, gender, address
                FROM users 
                WHERE email = @email AND isdeleted = false";
            
            User? user = null;
            string? hashedPassword = null;
            using (var cmd = new NpgsqlCommand(getUserQuery, conn))
            {
                cmd.Parameters.AddWithValue("email", loginDto.Email.ToLower());
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    hashedPassword = reader.IsDBNull(reader.GetOrdinal("password")) ? null : reader.GetString(reader.GetOrdinal("password"));
                    user = MapUserFromReader(reader);
                    user.Password = hashedPassword; // Store password for verification
                }
            }

            if (user == null || string.IsNullOrEmpty(hashedPassword) || !VerifyPassword(loginDto.Password, hashedPassword))
            {
                return Task.FromResult(new AuthResponseDto
                {
                    Success = false,
                    Message = "Email hoặc mật khẩu không chính xác"
                });
            }

            if (!user.IsActive)
            {
                return Task.FromResult(new AuthResponseDto
                {
                    Success = false,
                    Message = "Tài khoản đã bị vô hiệu hóa"
                });
            }

            // Check if user is a doctor (theo id hoặc email)
            string userType = "User";
            string? doctorId = null;
            var checkDoctorQuery = @"
                SELECT id FROM doctors 
                WHERE (id = @userId OR email = @email)
                  AND isdeleted = false AND isactive = true
                LIMIT 1";
            
            using (var doctorCmd = new NpgsqlCommand(checkDoctorQuery, conn))
            {
                doctorCmd.Parameters.AddWithValue("userId", user.Id);
                doctorCmd.Parameters.AddWithValue("email", user.Email.ToLower());
                var doctorResult = doctorCmd.ExecuteScalar();
                if (doctorResult != null)
                {
                    userType = "Doctor";
                    doctorId = doctorResult.ToString();
                }
            }

            // Generate tokens with user type và doctorId
            var accessToken = _jwtService.GenerateAccessToken(user, userType, doctorId);
            var refreshToken = CreateRefreshToken(user.Id);

            // Update last login in database
            var updateLastLoginQuery = @"
                UPDATE users 
                SET lastloginat = @lastloginat 
                WHERE id = @id AND isdeleted = false";
            
            using (var cmd = new NpgsqlCommand(updateLastLoginQuery, conn))
            {
                cmd.Parameters.AddWithValue("id", user.Id);
                cmd.Parameters.AddWithValue("lastloginat", DateTime.UtcNow);
                cmd.ExecuteNonQuery();
            }

            _logger.LogInformation("User logged in: {Email}, UserType: {UserType}", user.Email, userType);

            return Task.FromResult(new AuthResponseDto
            {
                Success = true,
                Message = "Đăng nhập thành công",
                AccessToken = accessToken,
                RefreshToken = refreshToken.Token,
                ExpiresAt = DateTime.UtcNow.AddMinutes(int.Parse(_configuration["Jwt:ExpirationMinutes"] ?? "60")),
                User = MapToUserInfo(user, userType)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login failed for {Email}", loginDto.Email);
            return Task.FromResult(new AuthResponseDto
            {
                Success = false,
                Message = "Đăng nhập thất bại. Vui lòng thử lại."
            });
        }
    }

    public async Task<AuthResponseDto> RefreshTokenAsync(string refreshToken, string? ipAddress = null)
    {
        try
        {
            // 1. Check refresh token trong memory
            var token = _refreshTokens.FirstOrDefault(t => t.Token == refreshToken);

            if (token == null || !token.IsActive)
            {
                return new AuthResponseDto
                {
                    Success = false,
                    Message = "Token không hợp lệ hoặc đã hết hạn"
                };
            }

            // 2. LẤY USER TỪ DATABASE (FIX LỖI CHÍNH)
            User? user = null;

            using var conn = OpenConnection();
            var query = @"
                SELECT id, email, password, firstname, lastname, phone, authenticationprovider, 
                    isemailverified, isactive, lastloginat, createddate, profileimageurl,
                    provideruserid, username, country, dob, gender, address
                FROM users 
                WHERE id = @userId AND isdeleted = false";

            using (var cmd = new NpgsqlCommand(query, conn))
            {
                cmd.Parameters.AddWithValue("userId", token.UserId);

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    user = MapUserFromReader(reader);
                }
            }

            if (user == null)
            {
                return new AuthResponseDto
                {
                    Success = false,
                    Message = "Người dùng không tồn tại"
                };
            }

            // 3. Revoke token cũ
            token.RevokedAt = DateTime.UtcNow;
            token.RevokedByIp = ipAddress;
            token.ReasonRevoked = "Replaced by new token";

            // 4. Generate token mới
            var newAccessToken = _jwtService.GenerateAccessToken(user, "User", null);
            var newRefreshToken = CreateRefreshToken(user.Id, ipAddress);

            token.ReplacedByToken = newRefreshToken.Token;

            // 5. Return kết quả
            return new AuthResponseDto
            {
                Success = true,
                Message = "Refresh thành công",
                AccessToken = newAccessToken,
                RefreshToken = newRefreshToken.Token,
                ExpiresAt = DateTime.UtcNow.AddMinutes(
                    int.Parse(_configuration["Jwt:ExpirationMinutes"] ?? "60")
                ),
                User = MapToUserInfo(user)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh token failed");

            return new AuthResponseDto
            {
                Success = false,
                Message = "Làm mới token thất bại"
            };
        }
    }

    public Task<bool> RevokeTokenAsync(string refreshToken, string? ipAddress = null)
    {
        var token = _refreshTokens.FirstOrDefault(t => t.Token == refreshToken);
        if (token == null || !token.IsActive)
            return Task.FromResult(false);

        token.RevokedAt = DateTime.UtcNow;
        token.RevokedByIp = ipAddress;
        token.ReasonRevoked = "Revoked by user";

        return Task.FromResult(true);
    }

    public async Task<bool> VerifyEmailAsync(string token)
    {
        var verificationToken = _emailVerificationTokens.FirstOrDefault(t => t.Token == token);
        
        if (verificationToken == null || !verificationToken.IsValid)
            return false;

        var user = _users.FirstOrDefault(u => u.Id == verificationToken.UserId);
        if (user == null)
            return false;

        user.IsEmailVerified = true;
        verificationToken.IsUsed = true;
        verificationToken.UsedAt = DateTime.UtcNow;

        // Send welcome email
        await _emailService.SendWelcomeEmailAsync(user.Email, user.FirstName);

        _logger.LogInformation("Email verified for user: {Email}", user.Email);
        return true;
    }

    public async Task<bool> ResendVerificationEmailAsync(string email)
    {
        var user = _users.FirstOrDefault(u => u.Email.ToLower() == email.ToLower());
        if (user == null || user.IsEmailVerified)
            return false;

        // Invalidate old tokens
        var oldTokens = _emailVerificationTokens.Where(t => t.UserId == user.Id && !t.IsUsed);
        foreach (var t in oldTokens)
        {
            t.IsUsed = true;
        }

        // Create new verification token
        var verificationToken = new EmailVerificationToken
        {
            UserId = user.Id,
            Token = GenerateRandomToken(),
            ExpiresAt = DateTime.UtcNow.AddHours(24)
        };
        _emailVerificationTokens.Add(verificationToken);

        await _emailService.SendVerificationEmailAsync(user.Email, verificationToken.Token, user.FirstName);
        return true;
    }

    public async Task<bool> ForgotPasswordAsync(string email)
    {
        var user = _users.FirstOrDefault(u => u.Email.ToLower() == email.ToLower());
        if (user == null)
        {
            // Return true anyway to prevent email enumeration
            return true;
        }

        // Invalidate old tokens
        var oldTokens = _passwordResetTokens.Where(t => t.UserId == user.Id && !t.IsUsed);
        foreach (var t in oldTokens)
        {
            t.IsUsed = true;
        }

        // Create new reset token
        var resetToken = new PasswordResetToken
        {
            UserId = user.Id,
            Token = GenerateRandomToken(),
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        };
        _passwordResetTokens.Add(resetToken);

        await _emailService.SendPasswordResetEmailAsync(user.Email, resetToken.Token, user.FirstName);
        return true;
    }

    public Task<bool> ResetPasswordAsync(string token, string newPassword)
    {
        var resetToken = _passwordResetTokens.FirstOrDefault(t => t.Token == token);
        if (resetToken == null || !resetToken.IsValid)
            return Task.FromResult(false);

        var user = _users.FirstOrDefault(u => u.Id == resetToken.UserId);
        if (user == null)
            return Task.FromResult(false);

        user.Password = HashPassword(newPassword);
        user.UpdatedDate = DateTime.UtcNow;

        resetToken.IsUsed = true;
        resetToken.UsedAt = DateTime.UtcNow;

        // Revoke all refresh tokens for this user
        var userRefreshTokens = _refreshTokens.Where(t => t.UserId == user.Id && t.IsActive);
        foreach (var t in userRefreshTokens)
        {
            t.RevokedAt = DateTime.UtcNow;
            t.ReasonRevoked = "Password reset";
        }

        _logger.LogInformation("Password reset for user: {Email}", user.Email);
        return Task.FromResult(true);
    }

    public async Task<AuthResponseDto> GoogleLoginAsync(string accessToken, string? ipAddress = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                _logger.LogWarning("Google login attempted with empty access token");
                return new AuthResponseDto
                {
                    Success = false,
                    Message = "Google token không hợp lệ"
                };
            }

            var googleUser = await VerifyGoogleTokenAsync(accessToken);
            if (googleUser == null)
            {
                _logger.LogWarning("Google token verification failed for token: {TokenPrefix}...", 
                    accessToken.Length > 10 ? accessToken.Substring(0, 10) : "invalid");
                return new AuthResponseDto
                {
                    Success = false,
                    Message = "Google token không hợp lệ hoặc đã hết hạn"
                };
            }

            _logger.LogInformation("Google login successful for user: {Email}", googleUser.Email);
            return ProcessSocialLogin(googleUser, "google", ipAddress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Google login failed with exception");
            return new AuthResponseDto
            {
                Success = false,
                Message = "Đăng nhập Google thất bại. Vui lòng thử lại."
            };
        }
    }

    public async Task<AuthResponseDto> FacebookLoginAsync(string accessToken, string? ipAddress = null)
    {
        try
        {
            // TODO: Verify Facebook access token with Facebook API
            var facebookUser = await VerifyFacebookTokenAsync(accessToken);
            if (facebookUser == null)
            {
                return new AuthResponseDto
                {
                    Success = false,
                    Message = "Facebook token không hợp lệ"
                };
            }

            return ProcessSocialLogin(facebookUser, "facebook", ipAddress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Facebook login failed");
            return new AuthResponseDto
            {
                Success = false,
                Message = "Đăng nhập Facebook thất bại"
            };
        }
    }

    public async Task<UserInfoDto?> GetCurrentUserAsync(string userId)
    {
        try
        {
            // =====================================================================
            // REDIS CACHE: Check cache first to reduce database queries
            // =====================================================================
            string cacheKey = $"user:{userId}";
            var cachedUser = await GetCachedUserAsync(cacheKey);
            if (cachedUser != null)
            {
                _logger.LogDebug("User {UserId} retrieved from cache", userId);
                return cachedUser;
            }

            // Cache miss - query database
            using var conn = OpenConnection();
            
            var getUserQuery = @"
                SELECT id, email, password, firstname, lastname, phone, authenticationprovider, 
                       isemailverified, isactive, lastloginat, createddate, profileimageurl,
                       provideruserid, username, country, dob, gender, address
                FROM users 
                WHERE id = @userId AND isdeleted = false";
            
            User? user = null;
            using (var cmd = new NpgsqlCommand(getUserQuery, conn))
            {
                cmd.Parameters.AddWithValue("userId", userId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    user = MapUserFromReader(reader);
                }
            }
            
            if (user == null)
            {
                _logger.LogWarning("User not found: {UserId}", userId);
                return null;
            }
            
            // Check if user is a doctor (theo id hoặc email)
            string userType = "User";
            var checkDoctorQuery = @"
                SELECT id FROM doctors 
                WHERE (id = @userId OR email = @email)
                  AND isdeleted = false AND isactive = true
                LIMIT 1";
            
            using (var doctorCmd = new NpgsqlCommand(checkDoctorQuery, conn))
            {
                doctorCmd.Parameters.AddWithValue("userId", userId);
                doctorCmd.Parameters.AddWithValue("email", user.Email.ToLower());
                var doctorResult = doctorCmd.ExecuteScalar();
                if (doctorResult != null)
                {
                    userType = "Doctor";
                }
            }
            
            var userInfo = MapToUserInfo(user, userType);
            
            // Cache user for 5 minutes to reduce DB load
            await SetCachedUserAsync(cacheKey, userInfo, TimeSpan.FromMinutes(5));
            _logger.LogDebug("User {UserId} cached for 5 minutes, UserType: {UserType}", userId, userType);
            
            return userInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting current user: {UserId}", userId);
            return null;
        }
    }

    public Task<bool> LogoutAsync(string userId, string? refreshToken = null)
    {
        if (!string.IsNullOrEmpty(refreshToken))
        {
            var token = _refreshTokens.FirstOrDefault(t => t.Token == refreshToken);
            if (token != null && token.IsActive)
            {
                token.RevokedAt = DateTime.UtcNow;
                token.ReasonRevoked = "User logged out";
            }
        }
        else
        {
            // Revoke all refresh tokens for this user
            var userTokens = _refreshTokens.Where(t => t.UserId == userId && t.IsActive);
            foreach (var token in userTokens)
            {
                token.RevokedAt = DateTime.UtcNow;
                token.ReasonRevoked = "User logged out";
            }
        }

        return Task.FromResult(true);
    }

    public async Task<UserInfoDto?> UpdateProfileAsync(string userId, string? firstName, string? lastName, string? phone, string? gender, string? address, string? profileImageUrl, DateTime? dob)
    {
        try
        {
            using var conn = OpenConnection();
            
            // First, check if user exists
            var checkUserQuery = @"
                SELECT id FROM users WHERE id = @userId AND isdeleted = false";
            
            using (var checkCmd = new NpgsqlCommand(checkUserQuery, conn))
            {
                checkCmd.Parameters.AddWithValue("userId", userId);
                var exists = await checkCmd.ExecuteScalarAsync();
                if (exists == null)
                {
                    _logger.LogWarning("User not found for profile update: {UserId}", userId);
                    return null;
                }
            }
            
            // Build update query dynamically based on provided fields
            var updateFields = new List<string>();
            var parameters = new List<NpgsqlParameter>();
            
            if (!string.IsNullOrEmpty(firstName))
            {
                updateFields.Add("firstname = @firstname");
                parameters.Add(new NpgsqlParameter("firstname", firstName));
            }
            if (!string.IsNullOrEmpty(lastName))
            {
                updateFields.Add("lastname = @lastname");
                parameters.Add(new NpgsqlParameter("lastname", lastName));
            }
            if (!string.IsNullOrEmpty(phone))
            {
                updateFields.Add("phone = @phone");
                parameters.Add(new NpgsqlParameter("phone", phone));
            }
            if (!string.IsNullOrEmpty(gender))
            {
                updateFields.Add("gender = @gender");
                parameters.Add(new NpgsqlParameter("gender", gender));
            }
            if (!string.IsNullOrEmpty(address))
            {
                updateFields.Add("address = @address");
                parameters.Add(new NpgsqlParameter("address", address));
            }
            if (!string.IsNullOrEmpty(profileImageUrl))
            {
                updateFields.Add("profileimageurl = @profileimageurl");
                parameters.Add(new NpgsqlParameter("profileimageurl", profileImageUrl));
            }
            if (dob.HasValue)
            {
                updateFields.Add("dob = @dob");
                parameters.Add(new NpgsqlParameter("dob", dob.Value));
            }
            
            // Always update UpdatedDate
            updateFields.Add("updateddate = @updateddate");
            parameters.Add(new NpgsqlParameter("updateddate", DateTime.UtcNow.Date));
            
            if (updateFields.Count == 1) // Only UpdatedDate
            {
                _logger.LogWarning("No fields to update for user: {UserId}", userId);
                // Still return the user info
            }
            else
            {
                var updateQuery = $@"
                    UPDATE users 
                    SET {string.Join(", ", updateFields)}
                    WHERE id = @userId AND isdeleted = false";
                
                using (var updateCmd = new NpgsqlCommand(updateQuery, conn))
                {
                    updateCmd.Parameters.AddWithValue("userId", userId);
                    foreach (var param in parameters)
                    {
                        updateCmd.Parameters.Add(param);
                    }
                    await updateCmd.ExecuteNonQueryAsync();
                }
            }
            
            // Read updated user
            var getUserQuery = @"
                SELECT id, email, password, firstname, lastname, phone, authenticationprovider, 
                       isemailverified, isactive, lastloginat, createddate, profileimageurl,
                       provideruserid, username, country, dob, gender, address
                FROM users 
                WHERE id = @userId AND isdeleted = false";
            
            User? user = null;
            using (var cmd = new NpgsqlCommand(getUserQuery, conn))
            {
                cmd.Parameters.AddWithValue("userId", userId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    user = MapUserFromReader(reader);
                }
            }
            
            if (user == null)
            {
                _logger.LogWarning("User not found after update: {UserId}", userId);
                return null;
            }
            
            _logger.LogInformation("Profile updated for user: {UserId}", userId);
            var updatedUserInfo = MapToUserInfo(user);
            
            // Invalidate cache after update to ensure fresh data
            await InvalidateUserCacheAsync(userId);
            
            return updatedUserInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating profile for user: {UserId}", userId);
            return null;
        }
    }

    #region Private Methods

    private AuthResponseDto ProcessSocialLogin(SocialUserInfo socialUser, string provider, string? ipAddress)
    {
        try
        {
            using var conn = OpenConnection();
            
            // Check if user exists with this provider
            var checkUserQuery = @"
                SELECT id, email, password, firstname, lastname, phone, authenticationprovider, 
                       isemailverified, isactive, lastloginat, createddate, profileimageurl,
                       provideruserid, username, country, dob, gender, address
                FROM users 
                WHERE authenticationprovider = @provider AND provideruserid = @provideruserid AND isdeleted = false";
            
            User? user = null;
            using (var cmd = new NpgsqlCommand(checkUserQuery, conn))
            {
                cmd.Parameters.AddWithValue("provider", provider);
                cmd.Parameters.AddWithValue("provideruserid", socialUser.ProviderId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    user = MapUserFromReader(reader);
                }
            }

            if (user == null)
            {
                // Check if email is already registered with different provider
                var checkEmailQuery = @"
                    SELECT id, email, firstname, lastname, phone, authenticationprovider, 
                           isemailverified, isactive, lastloginat, createddate, profileimageurl,
                           provideruserid, username, country
                    FROM users 
                    WHERE email = @email AND isdeleted = false";
                
                using (var cmd = new NpgsqlCommand(checkEmailQuery, conn))
                {
                    cmd.Parameters.AddWithValue("email", socialUser.Email.ToLower());
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                    {
                        var existingUser = MapUserFromReader(reader);
                        return new AuthResponseDto
                        {
                            Success = false,
                            Message = $"Email đã được đăng ký với {existingUser.AuthenticationProvider}. Vui lòng đăng nhập bằng {existingUser.AuthenticationProvider}."
                        };
                    }
                }

                // Create new user
                var userId = Guid.NewGuid().ToString();
                var username = socialUser.Email.Split('@')[0]; // Generate username from email
                
                var insertQuery = @"
                    INSERT INTO users (id, email, firstname, lastname, profileimageurl, 
                                     authenticationprovider, provideruserid, isemailverified, 
                                     isactive, createddate, username, country)
                    VALUES (@id, @email, @firstname, @lastname, @profileimageurl, 
                           @provider, @provideruserid, @isemailverified, 
                           @isactive, @createddate, @username, @country)";
                
                using (var cmd = new NpgsqlCommand(insertQuery, conn))
                {
                    cmd.Parameters.AddWithValue("id", userId);
                    cmd.Parameters.AddWithValue("email", socialUser.Email.ToLower());
                    cmd.Parameters.AddWithValue("firstname", (object?)socialUser.FirstName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("lastname", (object?)socialUser.LastName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("profileimageurl", (object?)socialUser.ProfileImageUrl ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("provider", provider);
                    cmd.Parameters.AddWithValue("provideruserid", socialUser.ProviderId);
                    cmd.Parameters.AddWithValue("isemailverified", true); // Social login emails are verified
                    cmd.Parameters.AddWithValue("isactive", true);
                    cmd.Parameters.AddWithValue("createddate", DateTime.UtcNow.Date);
                    cmd.Parameters.AddWithValue("username", username);
                    cmd.Parameters.AddWithValue("country", "Vietnam");
                    
                    cmd.ExecuteNonQuery();
                }

                // Read the newly created user
                using (var cmd = new NpgsqlCommand(checkUserQuery, conn))
                {
                    cmd.Parameters.AddWithValue("provider", provider);
                    cmd.Parameters.AddWithValue("provideruserid", socialUser.ProviderId);
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                    {
                        user = MapUserFromReader(reader);
                    }
                }

                _logger.LogInformation("User saved to database: {Email}, ID: {UserId}", user?.Email, userId);
                _logger.LogInformation("New user registered via {Provider}: {Email}", provider, user?.Email);
            }
            else
            {
                // Update user info if provided (avatar, name might have changed)
                // Only update ProfileImageUrl if it's provided and not empty
                var updateFields = new List<string>();
                var parameters = new List<NpgsqlParameter>();
                
                // Only update ProfileImageUrl if social provider has a new one
                if (!string.IsNullOrEmpty(socialUser.ProfileImageUrl))
                {
                    updateFields.Add("profileimageurl = @profileimageurl");
                    parameters.Add(new NpgsqlParameter("profileimageurl", socialUser.ProfileImageUrl));
                }
                
                if (!string.IsNullOrEmpty(socialUser.FirstName))
                {
                    updateFields.Add("firstname = @firstname");
                    parameters.Add(new NpgsqlParameter("firstname", socialUser.FirstName));
                }
                
                if (!string.IsNullOrEmpty(socialUser.LastName))
                {
                    updateFields.Add("lastname = @lastname");
                    parameters.Add(new NpgsqlParameter("lastname", socialUser.LastName));
                }
                
                // Always update last login and updated date
                updateFields.Add("lastloginat = @lastloginat");
                parameters.Add(new NpgsqlParameter("lastloginat", DateTime.UtcNow));
                updateFields.Add("updateddate = @updateddate");
                parameters.Add(new NpgsqlParameter("updateddate", DateTime.UtcNow.Date));
                
                if (updateFields.Count > 0)
                {
                    var updateQuery = $@"
                        UPDATE users 
                        SET {string.Join(", ", updateFields)}
                        WHERE id = @id";
                    
                    using (var cmd = new NpgsqlCommand(updateQuery, conn))
                    {
                        cmd.Parameters.AddWithValue("id", user.Id);
                        foreach (var param in parameters)
                        {
                            cmd.Parameters.Add(param);
                        }
                        cmd.ExecuteNonQuery();
                    }
                }

                // Update local user object
                if (!string.IsNullOrEmpty(socialUser.ProfileImageUrl))
                    user.ProfileImageUrl = socialUser.ProfileImageUrl;
                if (!string.IsNullOrEmpty(socialUser.FirstName))
                    user.FirstName = socialUser.FirstName;
                if (!string.IsNullOrEmpty(socialUser.LastName))
                    user.LastName = socialUser.LastName;
                user.LastLoginAt = DateTime.UtcNow;
            }

            if (user == null)
            {
                return new AuthResponseDto
                {
                    Success = false,
                    Message = "Không thể tạo hoặc tìm thấy người dùng"
                };
            }

            // Check if user is a doctor (theo id hoặc email)
            string userType = "User";
            string? doctorId = null;
            var checkDoctorQuery = @"
                SELECT id FROM doctors 
                WHERE (id = @userId OR email = @email)
                  AND isdeleted = false AND isactive = true
                LIMIT 1";
            
            using (var doctorCmd = new NpgsqlCommand(checkDoctorQuery, conn))
            {
                doctorCmd.Parameters.AddWithValue("userId", user.Id);
                doctorCmd.Parameters.AddWithValue("email", user.Email.ToLower());
                var doctorResult = doctorCmd.ExecuteScalar();
                if (doctorResult != null)
                {
                    userType = "Doctor";
                    doctorId = doctorResult.ToString();
                }
            }

            // Generate tokens with user type và doctorId
            var accessToken = _jwtService.GenerateAccessToken(user, userType, doctorId);
            var refreshToken = CreateRefreshToken(user.Id, ipAddress);

            return new AuthResponseDto
            {
                Success = true,
                Message = "Đăng nhập thành công",
                AccessToken = accessToken,
                RefreshToken = refreshToken.Token,
                ExpiresAt = DateTime.UtcNow.AddMinutes(int.Parse(_configuration["Jwt:ExpirationMinutes"] ?? "60")),
                User = MapToUserInfo(user, userType)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing social login for provider: {Provider}", provider);
            return new AuthResponseDto
            {
                Success = false,
                Message = "Đăng nhập thất bại. Vui lòng thử lại."
            };
        }
    }

    private RefreshToken CreateRefreshToken(string userId, string? ipAddress = null)
    {
        var refreshToken = new RefreshToken
        {
            UserId = userId,
            Token = _jwtService.GenerateRefreshToken(),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedByIp = ipAddress
        };

        _refreshTokens.Add(refreshToken);
        return refreshToken;
    }

    private static string HashPassword(string password)
    {
        using var sha256 = SHA256.Create();
        var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
        return Convert.ToBase64String(hashedBytes);
    }

    private static bool VerifyPassword(string password, string hashedPassword)
    {
        if (string.IsNullOrWhiteSpace(hashedPassword)) return false;

        // Check SHA256 hash (legacy)
        var hash = HashPassword(password);
        if (hash == hashedPassword) return true;

        // Check BCrypt hash (new accounts created by clinic)
        if (hashedPassword.StartsWith("$2") || hashedPassword.StartsWith("$2a") || hashedPassword.StartsWith("$2b"))
        {
            try
            {
                return BCrypt.Net.BCrypt.Verify(password, hashedPassword);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private static string GenerateRandomToken()
    {
        var randomBytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        return Convert.ToBase64String(randomBytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private static UserInfoDto MapToUserInfo(User user, string userType = "User")
    {
        return new UserInfoDto
        {
            Id = user.Id,
            Email = user.Email,
            FirstName = user.FirstName,
            LastName = user.LastName,
            ProfileImageUrl = user.ProfileImageUrl,
            IsEmailVerified = user.IsEmailVerified,
            AuthenticationProvider = user.AuthenticationProvider,
            UserType = userType
        };
    }

    // Verify Google access token by calling Google's userinfo API
    private async Task<SocialUserInfo?> VerifyGoogleTokenAsync(string accessToken)
    {
        try
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10); // Set timeout
            httpClient.DefaultRequestHeaders.Authorization = 
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            
            // Thử dùng v2 endpoint trước (vẫn còn hoạt động), nếu fail thì thử v3
            var endpoints = new[]
            {
                "https://www.googleapis.com/oauth2/v2/userinfo",
                "https://www.googleapis.com/oauth2/v3/userinfo"
            };
            
            HttpResponseMessage? response = null;
            string? errorContent = null;
            
            foreach (var endpoint in endpoints)
            {
                try
                {
                    response = await httpClient.GetAsync(endpoint);
                    if (response.IsSuccessStatusCode)
                    {
                        break;
                    }
                    errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("Google API {Endpoint} failed: {StatusCode}, Response: {Error}", 
                        endpoint, response.StatusCode, errorContent);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error calling Google API {Endpoint}", endpoint);
                }
            }
            
            if (response == null || !response.IsSuccessStatusCode)
            {
                _logger.LogWarning("All Google API endpoints failed. Last error: {Error}", errorContent);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            var googleUser = System.Text.Json.JsonSerializer.Deserialize<GoogleUserInfo>(content, 
                new System.Text.Json.JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true 
                });

            if (googleUser == null || string.IsNullOrEmpty(googleUser.Email))
            {
                _logger.LogWarning("Failed to parse Google user info");
                return null;
            }

            // Xử lý tên từ Google: ưu tiên GivenName/FamilyName, nếu không có thì split từ Name
            string? firstName = googleUser.GivenName;
            string? lastName = googleUser.FamilyName;
            
            if (string.IsNullOrEmpty(firstName) && string.IsNullOrEmpty(lastName) && !string.IsNullOrEmpty(googleUser.Name))
            {
                // Split full name thành first name và last name
                var nameParts = googleUser.Name.Trim().Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                firstName = nameParts.Length > 0 ? nameParts[0] : null;
                lastName = nameParts.Length > 1 ? nameParts[1] : null;
            }

            return new SocialUserInfo
            {
                ProviderId = googleUser.Id ?? "",
                Email = googleUser.Email,
                FirstName = firstName,
                LastName = lastName,
                ProfileImageUrl = googleUser.Picture,
                Provider = "google"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying Google token");
            return null;
        }
    }

    // Google userinfo response model
    private class GoogleUserInfo
    {
        public string? Id { get; set; }
        public string? Email { get; set; }
        public bool VerifiedEmail { get; set; }
        public string? Name { get; set; }
        public string? GivenName { get; set; }
        public string? FamilyName { get; set; }
        public string? Picture { get; set; }
    }

    // Verify Facebook access token by calling Facebook's Graph API
    // FR-1: Verify Facebook token với Facebook API để đảm bảo token hợp lệ và thuộc về app của chúng ta
    private async Task<SocialUserInfo?> VerifyFacebookTokenAsync(string accessToken)
    {
        try
        {
            using var httpClient = new HttpClient();
            
            // Bước 1: Verify token với app_id và app_secret (nếu có) để đảm bảo token thuộc về app của chúng ta
            var appId = _configuration["OAuth:Facebook:AppId"];
            var appSecret = _configuration["OAuth:Facebook:AppSecret"];
            
            if (!string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(appSecret))
            {
                // Debug token endpoint để verify token thuộc về app của chúng ta
                var debugUrl = $"https://graph.facebook.com/debug_token?input_token={accessToken}&access_token={appId}|{appSecret}";
                var debugResponse = await httpClient.GetAsync(debugUrl);
                
                if (debugResponse.IsSuccessStatusCode)
                {
                    var debugContent = await debugResponse.Content.ReadAsStringAsync();
                    var debugResult = System.Text.Json.JsonSerializer.Deserialize<FacebookDebugTokenResponse>(debugContent,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    
                    // Kiểm tra token có hợp lệ và thuộc về app của chúng ta không
                    if (debugResult?.Data == null || 
                        !debugResult.Data.IsValid || 
                        debugResult.Data.AppId != appId)
                    {
                        _logger.LogWarning("Facebook token verification failed: Invalid token or wrong app_id. Expected: {AppId}, Got: {TokenAppId}",
                            appId, debugResult?.Data?.AppId);
                        return null;
                    }
                }
                else
                {
                    _logger.LogWarning("Facebook debug token API failed: {StatusCode}", debugResponse.StatusCode);
                    // Tiếp tục với /me endpoint như fallback
                }
            }
            
            // Bước 2: Gọi Facebook Graph API để lấy thông tin user (bao gồm name để fallback)
            var response = await httpClient.GetAsync(
                $"https://graph.facebook.com/me?fields=id,email,first_name,last_name,name,picture&access_token={accessToken}");
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Facebook token verification failed: {StatusCode}", response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            var facebookUser = System.Text.Json.JsonSerializer.Deserialize<FacebookUserInfo>(content, 
                new System.Text.Json.JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true 
                });

            if (facebookUser == null || string.IsNullOrEmpty(facebookUser.Id))
            {
                _logger.LogWarning("Failed to parse Facebook user info");
                return null;
            }

            // Facebook có thể không trả về email nếu user không cấp quyền
            var email = facebookUser.Email ?? $"{facebookUser.Id}@facebook.com";

            // Xử lý tên từ Facebook: ưu tiên FirstName/LastName, nếu không có thì split từ Name
            string? firstName = facebookUser.FirstName;
            string? lastName = facebookUser.LastName;
            
            if (string.IsNullOrEmpty(firstName) && string.IsNullOrEmpty(lastName) && !string.IsNullOrEmpty(facebookUser.Name))
            {
                // Split full name thành first name và last name
                var nameParts = facebookUser.Name.Trim().Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                firstName = nameParts.Length > 0 ? nameParts[0] : null;
                lastName = nameParts.Length > 1 ? nameParts[1] : null;
            }

            return new SocialUserInfo
            {
                ProviderId = facebookUser.Id,
                Email = email,
                FirstName = firstName,
                LastName = lastName,
                ProfileImageUrl = facebookUser.Picture?.Data?.Url,
                Provider = "facebook"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying Facebook token");
            return null;
        }
    }

    // Facebook debug token response model (for token verification)
    private class FacebookDebugTokenResponse
    {
        public FacebookDebugTokenData? Data { get; set; }
    }

    private class FacebookDebugTokenData
    {
        public string? AppId { get; set; }
        public bool IsValid { get; set; }
        public string? UserId { get; set; }
    }

    // Facebook userinfo response model
    private class FacebookUserInfo
    {
        public string? Id { get; set; }
        public string? Email { get; set; }
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? Name { get; set; }
        public FacebookPicture? Picture { get; set; }
    }

    private class FacebookPicture
    {
        public FacebookPictureData? Data { get; set; }
    }

    private class FacebookPictureData
    {
        public string? Url { get; set; }
    }

    private NpgsqlConnection OpenConnection()
    {
        var cs = _configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("ConnectionStrings:DefaultConnection chưa được cấu hình.");

        var conn = new NpgsqlConnection(cs);
        conn.Open();
        return conn;
    }

    // =====================================================================
    // REDIS CACHE: Helper methods for user caching
    // =====================================================================
    private async Task<UserInfoDto?> GetCachedUserAsync(string cacheKey)
    {
        if (_distributedCache == null) return null;

        try
        {
            var cachedBytes = await _distributedCache.GetAsync(cacheKey);
            if (cachedBytes == null || cachedBytes.Length == 0)
                return null;

            var json = Encoding.UTF8.GetString(cachedBytes);
            return JsonSerializer.Deserialize<UserInfoDto>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reading from cache for key {CacheKey}", cacheKey);
            return null;
        }
    }

    private async Task SetCachedUserAsync(string cacheKey, UserInfoDto userInfo, TimeSpan expiration)
    {
        if (_distributedCache == null) return;

        try
        {
            var json = JsonSerializer.Serialize(userInfo);
            var bytes = Encoding.UTF8.GetBytes(json);
            
            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = expiration
            };
            
            await _distributedCache.SetAsync(cacheKey, bytes, options);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error writing to cache for key {CacheKey}", cacheKey);
            // Don't throw - caching is best effort
        }
    }

    private async Task InvalidateUserCacheAsync(string userId)
    {
        if (_distributedCache == null) return;

        try
        {
            string cacheKey = $"user:{userId}";
            await _distributedCache.RemoveAsync(cacheKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error invalidating cache for user {UserId}", userId);
        }
    }

    private User MapUserFromReader(NpgsqlDataReader reader)
    {
        return new User
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Email = reader.GetString(reader.GetOrdinal("email")),
            FirstName = reader.IsDBNull(reader.GetOrdinal("firstname")) ? null : reader.GetString(reader.GetOrdinal("firstname")),
            LastName = reader.IsDBNull(reader.GetOrdinal("lastname")) ? null : reader.GetString(reader.GetOrdinal("lastname")),
            Phone = reader.IsDBNull(reader.GetOrdinal("phone")) ? null : reader.GetString(reader.GetOrdinal("phone")),
            AuthenticationProvider = reader.GetString(reader.GetOrdinal("authenticationprovider")),
            IsEmailVerified = reader.GetBoolean(reader.GetOrdinal("isemailverified")),
            IsActive = reader.GetBoolean(reader.GetOrdinal("isactive")),
            LastLoginAt = reader.IsDBNull(reader.GetOrdinal("lastloginat")) ? null : reader.GetDateTime(reader.GetOrdinal("lastloginat")),
            CreatedDate = reader.IsDBNull(reader.GetOrdinal("createddate")) ? DateTime.UtcNow : reader.GetDateTime(reader.GetOrdinal("createddate")),
            ProfileImageUrl = reader.IsDBNull(reader.GetOrdinal("profileimageurl")) ? null : reader.GetString(reader.GetOrdinal("profileimageurl")),
            ProviderUserId = reader.IsDBNull(reader.GetOrdinal("provideruserid")) ? null : reader.GetString(reader.GetOrdinal("provideruserid")),
            Username = reader.IsDBNull(reader.GetOrdinal("username")) ? null : reader.GetString(reader.GetOrdinal("username")),
            Dob = reader.IsDBNull(reader.GetOrdinal("dob")) ? null : reader.GetDateTime(reader.GetOrdinal("dob")),
            Gender = reader.IsDBNull(reader.GetOrdinal("gender")) ? null : reader.GetString(reader.GetOrdinal("gender")),
            Address = reader.IsDBNull(reader.GetOrdinal("address")) ? null : reader.GetString(reader.GetOrdinal("address")),
            Password = null // Not needed for social login
        };
    }

    #endregion
}
