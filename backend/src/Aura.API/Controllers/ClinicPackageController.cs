using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Aura.API.Controllers;

/// <summary>
/// Controller for clinic package management (FR-28)
/// </summary>
[ApiController]
[Route("api/clinic/packages")]
[Authorize]
public class ClinicPackageController : ControllerBase
{
    private readonly string _connectionString;
    private readonly ILogger<ClinicPackageController> _logger;

    public ClinicPackageController(IConfiguration configuration, ILogger<ClinicPackageController> logger)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new ArgumentNullException("DefaultConnection not configured");
        _logger = logger;
    }

    /// <summary>
    /// Get available service packages for clinics
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAvailablePackages()
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT 
                    Id, PackageName, Description, NumberOfAnalyses, Price, 
                    ValidityDays, PackageType, Features, IsActive
                FROM service_packages
                WHERE IsActive = true AND IsDeleted = false AND PackageType = 'Clinic'
                ORDER BY Price";

            using var cmd = new NpgsqlCommand(sql, connection);
            var packages = new List<object>();
            
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                packages.Add(new
                {
                    id = reader.GetString(0),
                    packageName = reader.GetString(1),
                    description = reader.IsDBNull(2) ? null : reader.GetString(2),
                    analysesIncluded = reader.GetInt32(3),
                    price = reader.GetDecimal(4),
                    validityDays = reader.IsDBNull(5) ? 365 : reader.GetInt32(5),
                    isClinicPackage = reader.GetString(6) == "Clinic",
                    features = reader.IsDBNull(7) ? null : reader.GetString(7),
                    isActive = reader.GetBoolean(8)
                });
            }

            return Ok(packages);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting available packages");
            return StatusCode(500, new { message = "Lỗi khi tải danh sách gói dịch vụ" });
        }
    }

    /// <summary>
    /// Get current active package for clinic
    /// </summary>
    [HttpGet("current")]
    public async Task<IActionResult> GetCurrentPackage()
    {
        var clinicId = GetCurrentClinicId();
        if (clinicId == null)
            return Forbid();

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT 
                    up.Id, up.PackageId, sp.PackageName, up.TotalAnalyses, up.UsedAnalyses,
                    up.RemainingAnalyses, up.PurchasedAt, up.ExpiresAt, up.IsActive,
                    sp.Price, sp.Features
                FROM user_packages up
                INNER JOIN service_packages sp ON sp.Id = up.PackageId
                WHERE up.ClinicId = @ClinicId AND up.IsActive = true AND up.IsDeleted = false
                ORDER BY up.ExpiresAt DESC NULLS LAST
                LIMIT 1";

            using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("ClinicId", clinicId);

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var totalAnalyses = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                var usedAnalyses = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
                var remainingAnalyses = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
                
                return Ok(new
                {
                    id = reader.GetString(0),
                    packageId = reader.GetString(1),
                    packageName = reader.GetString(2),
                    totalAnalyses = totalAnalyses > 0 ? totalAnalyses : remainingAnalyses,
                    usedAnalyses = usedAnalyses,
                    remainingAnalyses = remainingAnalyses,
                    purchasedAt = reader.GetDateTime(6),
                    expiresAt = reader.IsDBNull(7) ? null : reader.GetDateTime(7).ToString("o"),
                    isActive = reader.GetBoolean(8),
                    price = reader.GetDecimal(9),
                    features = reader.IsDBNull(10) ? null : reader.GetString(10)
                });
            }

            return Ok(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting current package for clinic {ClinicId}", clinicId);
            return StatusCode(500, new { message = "Lỗi khi tải thông tin gói dịch vụ" });
        }
    }

    /// <summary>
    /// Purchase a package
    /// </summary>
    [HttpPost("purchase")]
    public async Task<IActionResult> PurchasePackage([FromBody] PurchasePackageRequest request)
    {
        var clinicId = GetCurrentClinicId();
        if (clinicId == null)
            return Forbid();

        if (string.IsNullOrEmpty(request.PackageId))
            return BadRequest(new { message = "Vui lòng chọn gói dịch vụ" });

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Get package info
            var getPackageSql = @"
                SELECT Id, PackageName, NumberOfAnalyses, Price, COALESCE(ValidityDays, 365)
                FROM service_packages
                WHERE Id = @PackageId AND IsActive = true AND IsDeleted = false AND PackageType = 'Clinic'";

            string packageName = "";
            int analysesIncluded = 0;
            decimal price = 0;
            int validityDays = 365;

            using (var cmd = new NpgsqlCommand(getPackageSql, connection))
            {
                cmd.Parameters.AddWithValue("PackageId", request.PackageId);
                using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    return NotFound(new { message = "Không tìm thấy gói dịch vụ" });
                }
                packageName = reader.GetString(1);
                analysesIncluded = reader.GetInt32(2);
                price = reader.GetDecimal(3);
                validityDays = reader.GetInt32(4);
            }

            using var transaction = await connection.BeginTransactionAsync();
            try
            {
                // Cộng lượt còn lại từ gói cũ vào gói mới (mua thêm / nâng cấp)
                var sumOldSql = @"
                    SELECT COALESCE(SUM(RemainingAnalyses), 0)
                    FROM user_packages
                    WHERE ClinicId = @ClinicId AND UserId IS NULL
                      AND IsActive = true AND COALESCE(IsDeleted, false) = false";
                int oldRemaining = 0;
                using (var sumCmd = new NpgsqlCommand(sumOldSql, connection, transaction))
                {
                    sumCmd.Parameters.AddWithValue("ClinicId", clinicId);
                    var sumVal = await sumCmd.ExecuteScalarAsync();
                    oldRemaining = sumVal != null && sumVal != DBNull.Value ? Convert.ToInt32(sumVal) : 0;
                }

                // Tắt gói cũ (chỉ giữ một gói active)
                var deactivateSql = @"
                    UPDATE user_packages
                    SET IsActive = false, UpdatedDate = @UpdatedDate, UpdatedBy = @UpdatedBy
                    WHERE ClinicId = @ClinicId AND UserId IS NULL
                      AND IsActive = true AND COALESCE(IsDeleted, false) = false";
                using (var deactivateCmd = new NpgsqlCommand(deactivateSql, connection, transaction))
                {
                    deactivateCmd.Parameters.AddWithValue("ClinicId", clinicId);
                    deactivateCmd.Parameters.AddWithValue("UpdatedDate", DateTime.UtcNow.Date);
                    deactivateCmd.Parameters.AddWithValue("UpdatedBy", clinicId);
                    await deactivateCmd.ExecuteNonQueryAsync();
                }

                var totalRemaining = analysesIncluded + oldRemaining;

                // Create user_package record (lượt mới = lượt gói mới + lượt còn từ gói cũ)
                var userPackageId = Guid.NewGuid().ToString();
                var now = DateTime.UtcNow;
                var expiresAt = now.AddDays(validityDays);

                var createPackageSql = @"
                    INSERT INTO user_packages (
                        Id, UserId, PackageId, ClinicId, TotalAnalyses, UsedAnalyses, RemainingAnalyses,
                        PurchasedAt, ExpiresAt, IsActive, CreatedDate, CreatedBy, IsDeleted
                    ) VALUES (
                        @Id, NULL, @PackageId, @ClinicId, @TotalAnalyses, 0, @RemainingAnalyses,
                        @PurchasedAt, @ExpiresAt, true, @Now, @CreatedBy, false
                    )";

                using (var cmd = new NpgsqlCommand(createPackageSql, connection, transaction))
                {
                    cmd.Parameters.AddWithValue("Id", userPackageId);
                    cmd.Parameters.AddWithValue("PackageId", request.PackageId);
                    cmd.Parameters.AddWithValue("ClinicId", clinicId);
                    cmd.Parameters.AddWithValue("TotalAnalyses", totalRemaining);
                    cmd.Parameters.AddWithValue("RemainingAnalyses", totalRemaining);
                    cmd.Parameters.AddWithValue("PurchasedAt", now);
                    cmd.Parameters.AddWithValue("ExpiresAt", expiresAt);
                    cmd.Parameters.AddWithValue("Now", now);
                    cmd.Parameters.AddWithValue("CreatedBy", clinicId);
                    await cmd.ExecuteNonQueryAsync();
                }

                // Create payment history record
                var paymentId = Guid.NewGuid().ToString();
                var createPaymentSql = @"
                    INSERT INTO payment_history (
                        Id, PackageId, ClinicId, Amount, PaymentMethod, PaymentStatus,
                        TransactionId, PaymentDate, CreatedDate, IsDeleted
                    ) VALUES (
                        @Id, @PackageId, @ClinicId, @Amount, @PaymentMethod, 'Completed',
                        @TransactionId, @PaymentDate, @Now, false
                    )";

                using (var cmd = new NpgsqlCommand(createPaymentSql, connection, transaction))
                {
                    cmd.Parameters.AddWithValue("Id", paymentId);
                    cmd.Parameters.AddWithValue("PackageId", request.PackageId);
                    cmd.Parameters.AddWithValue("ClinicId", clinicId);
                    cmd.Parameters.AddWithValue("Amount", price);
                    cmd.Parameters.AddWithValue("PaymentMethod", request.PaymentMethod ?? "BankTransfer");
                    cmd.Parameters.AddWithValue("TransactionId", $"TXN-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString()[..8].ToUpper()}");
                    cmd.Parameters.AddWithValue("PaymentDate", now);
                    cmd.Parameters.AddWithValue("Now", now);
                    await cmd.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();

                _logger.LogInformation("Clinic {ClinicId} purchased package {PackageId}", clinicId, request.PackageId);

                return Ok(new
                {
                    success = true,
                    message = $"Đã mua thành công gói {packageName}. Tổng lượt còn lại: {totalRemaining}",
                    userPackageId = userPackageId,
                    paymentId = paymentId,
                    analysesIncluded = analysesIncluded,
                    remainingAnalyses = totalRemaining,
                    expiresAt = expiresAt.ToString("o")
                });
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error purchasing package for clinic {ClinicId}", clinicId);
            return StatusCode(500, new { message = "Lỗi khi mua gói dịch vụ" });
        }
    }

    /// <summary>
    /// Get purchase history
    /// </summary>
    [HttpGet("history")]
    public async Task<IActionResult> GetPurchaseHistory()
    {
        var clinicId = GetCurrentClinicId();
        if (clinicId == null)
            return Forbid();

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT 
                    ph.Id, ph.Amount, ph.PaymentMethod, ph.PaymentStatus,
                    ph.TransactionId, ph.PaymentDate, sp.PackageName, sp.NumberOfAnalyses
                FROM payment_history ph
                INNER JOIN service_packages sp ON sp.Id = ph.PackageId
                WHERE ph.ClinicId = @ClinicId AND ph.IsDeleted = false
                ORDER BY ph.PaymentDate DESC
                LIMIT 50";

            using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("ClinicId", clinicId);

            var history = new List<object>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                history.Add(new
                {
                    id = reader.GetString(0),
                    amount = reader.GetDecimal(1),
                    paymentMethod = reader.IsDBNull(2) ? null : reader.GetString(2),
                    paymentStatus = reader.GetString(3),
                    transactionId = reader.IsDBNull(4) ? null : reader.GetString(4),
                    paidAt = reader.IsDBNull(5) ? null : reader.GetDateTime(5).ToString("o"),
                    packageName = reader.GetString(6),
                    analysesIncluded = reader.GetInt32(7)
                });
            }

            return Ok(history);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting purchase history for clinic {ClinicId}", clinicId);
            return StatusCode(500, new { message = "Lỗi khi tải lịch sử mua hàng" });
        }
    }

    /// <summary>
    /// Get usage statistics
    /// </summary>
    [HttpGet("usage")]
    public async Task<IActionResult> GetUsageStats()
    {
        var clinicId = GetCurrentClinicId();
        if (clinicId == null)
            return Forbid();

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Get monthly usage
            var sql = @"
                SELECT 
                    DATE_TRUNC('month', ar.AnalysisCompletedAt) as Month,
                    COUNT(*) as Count
                FROM analysis_results ar
                INNER JOIN retinal_images ri ON ri.Id = ar.ImageId
                WHERE ri.ClinicId = @ClinicId 
                    AND ar.IsDeleted = false 
                    AND ar.AnalysisStatus = 'Completed'
                    AND ar.AnalysisCompletedAt >= DATE_TRUNC('month', CURRENT_DATE - INTERVAL '11 months')
                GROUP BY DATE_TRUNC('month', ar.AnalysisCompletedAt)
                ORDER BY Month";

            using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("ClinicId", clinicId);

            var monthlyUsage = new List<object>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                monthlyUsage.Add(new
                {
                    month = reader.GetDateTime(0).ToString("yyyy-MM"),
                    count = reader.GetInt32(1)
                });
            }

            return Ok(monthlyUsage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting usage stats for clinic {ClinicId}", clinicId);
            return StatusCode(500, new { message = "Lỗi khi tải thống kê sử dụng" });
        }
    }

    private string? GetCurrentClinicId()
    {
        return User.FindFirstValue("clinic_id");
    }
}

public class PurchasePackageRequest
{
    public string PackageId { get; set; } = string.Empty;
    public string? PaymentMethod { get; set; }
}
