using System.Security.Claims;
using System.Text.Json;
using Aura.Application.DTOs.Notifications;
using Aura.Application.Services.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Aura.API.Controllers;

[ApiController]
[Route("api")]
public class NotificationsController : ControllerBase
{
    private readonly INotificationService _notifications;

    public NotificationsController(INotificationService notifications)
    {
        _notifications = notifications;
    }

    /// <summary>Lấy userId từ JWT (sub / id / NameIdentifier).</summary>
    private string? GetCurrentUserId()
    {
        return User?.FindFirst("sub")?.Value
            ?? User?.FindFirst("id")?.Value
            ?? User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }

    // GET /api/notifications
    [HttpGet("notifications")]
    [Authorize]
    public async Task<IActionResult> Get()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { message = "Không xác định được người dùng. Vui lòng đăng nhập lại." });
        var arr = await _notifications.GetForUserAsync(userId);
        return Ok(arr);
    }

    // POST /api/notifications (create test notification)
    [HttpPost("notifications")]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] NotificationDto dto)
    {
        var userId = GetCurrentUserId();
        var created = await _notifications.CreateAsync(userId, dto.Title, dto.Message, dto.Type, dto.Data);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    // POST /api/notifications/{id}/read
    [HttpPost("notifications/{id}/read")]
    [Authorize]
    public async Task<IActionResult> MarkRead(string id)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { message = "Không xác định được người dùng. Vui lòng đăng nhập lại." });
        await _notifications.MarkReadAsync(userId, id);
        return NoContent();
    }

    // POST /api/notifications/mark-all-read
    [HttpPost("notifications/mark-all-read")]
    [Authorize]
    public async Task<IActionResult> MarkAllRead()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { message = "Không xác định được người dùng. Vui lòng đăng nhập lại." });
        await _notifications.MarkAllReadAsync(userId);
        return NoContent();
    }

    // GET /api/notifications/stream
    [HttpGet("notifications/stream")]
    [Authorize]
    public async Task Stream(CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
        {
            Response.StatusCode = 401;
            return;
        }
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Content-Type", "text/event-stream");

        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        await foreach (var n in _notifications.StreamForUserAsync(userId, ct))
        {
            var json = JsonSerializer.Serialize(n, jsonOptions);
            await Response.WriteAsync($"data: {json}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }
}
