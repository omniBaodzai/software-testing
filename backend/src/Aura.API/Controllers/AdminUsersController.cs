using System.Security.Claims;
using Aura.API.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Aura.API.Controllers;

[ApiController]
[Route("api/admin/users")]
[Authorize(Policy = "AdminOnly")]
public class AdminUsersController : ControllerBase
{
    private readonly AdminAccountRepository _repo;
    private readonly AuditLogRepository _auditRepo;
    private readonly IConfiguration _config;
    private readonly ILogger<AdminUsersController>? _logger;

    public AdminUsersController(AdminAccountRepository repo, AuditLogRepository auditRepo, IConfiguration config, ILogger<AdminUsersController>? logger = null)
    {
        _repo = repo;
        _auditRepo = auditRepo;
        _config = config;
        _logger = logger;
    }

    private bool UseDemoMode => _config.GetValue<bool>("Admin:UseDemoMode", false);

    private static List<AdminUserRowDto> DemoUsers => new()
    {
        new("user-1", "nguyen.van.a@gmail.com", "nguyenvana", "Nguyễn", "Văn A", true, true),
        new("user-2", "tran.thi.b@gmail.com", "tranthib", "Trần", "Thị B", true, true),
        new("user-3", "le.van.c@gmail.com", "levanc", "Lê", "Văn C", false, false),
    };

    [HttpGet]
    public async Task<ActionResult<List<AdminUserRowDto>>> List([FromQuery] string? search, [FromQuery] bool? isActive)
    {
        try
        {
            var result = await _repo.ListUsersAsync(search, isActive);
            return Ok(result);
        }
        catch (Exception ex)
        {
            // Log error for debugging
            _logger?.LogError(ex, "Error listing users from database");
            
            // Only use demo mode if explicitly enabled AND database connection failed
            if (UseDemoMode)
            {
                _logger?.LogWarning("Using demo data due to database connection failure and UseDemoMode=true");
                return Ok(DemoUsers);
            }
            
            return StatusCode(500, new { message = $"Không kết nối được database: {ex.Message}" });
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] AdminUpdateUserDto dto)
    {
        try
        {
            var n = await _repo.UpdateUserAsync(id, dto);
            if (n == 0) return NotFound(new { message = "Không tìm thấy user" });
            var adminId = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            try
            {
                var newValuesJson = System.Text.Json.JsonSerializer.Serialize(dto);
                await _auditRepo.InsertAsync(adminId, "UpdateUser", "User", id, null, newValuesJson, ip);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Không ghi được audit log khi cập nhật user {UserId}", id);
            }
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error updating user {UserId}", id);
            if (UseDemoMode)
            {
                _logger?.LogWarning("Using demo mode for update operation");
                return NoContent();
            }
            return StatusCode(500, new { message = $"Không kết nối được database: {ex.Message}" });
        }
    }

    [HttpPatch("{id}/status")]
    public async Task<IActionResult> SetStatus(string id, [FromBody] AdminUpdateUserDto dto)
    {
        if (!dto.IsActive.HasValue) return BadRequest(new { message = "Thiếu IsActive" });
        try
        {
            var n = await _repo.SetUserActiveAsync(id, dto.IsActive.Value);
            if (n == 0) return NotFound(new { message = "Không tìm thấy user" });
            var adminId = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            try
            {
                var newValuesJson = System.Text.Json.JsonSerializer.Serialize(new { dto.IsActive });
                await _auditRepo.InsertAsync(adminId, "SetUserStatus", "User", id, null, newValuesJson, ip);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Không ghi được audit log khi đổi trạng thái user {UserId}", id);
            }
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error setting user status {UserId}", id);
            if (UseDemoMode)
            {
                _logger?.LogWarning("Using demo mode for set status operation");
                return NoContent();
            }
            return StatusCode(500, new { message = $"Không kết nối được database: {ex.Message}" });
        }
    }
}


