using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Collections.Concurrent;
using Aura.Application.DTOs.Notifications;
using Aura.Application.Services.Notifications;
using Aura.Infrastructure.Services.Firebase;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Aura.Infrastructure.Services.Notifications;

/// <summary>
/// NotificationService - Kết nối trực tiếp với PostgreSQL database
/// Với real-time streaming qua Channel và Firebase Cloud Messaging push notifications
/// </summary>
public class NotificationService : INotificationService
{
    private readonly string _connectionString;
    private readonly ILogger<NotificationService>? _logger;
    private readonly IFirebaseMessagingService? _fcmService;
    
    // Per-user channel for real-time delivery
    private static readonly ConcurrentDictionary<string, Channel<NotificationDto>> _channels = new();

    public NotificationService(
        IConfiguration configuration, 
        ILogger<NotificationService>? logger = null,
        IFirebaseMessagingService? fcmService = null)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Database connection string not configured");
        _logger = logger;
        _fcmService = fcmService;
    }

    private static Channel<NotificationDto> GetOrCreateChannel(string userId)
    {
        return _channels.GetOrAdd(userId ?? "__global__", _ => Channel.CreateUnbounded<NotificationDto>());
    }

    public async Task<NotificationDto> CreateAsync(string? userId, string title, string message, string? type = null, object? data = null)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var notificationId = Guid.NewGuid().ToString();
            var dataJson = data != null ? System.Text.Json.JsonSerializer.Serialize(data) : null;

            var sql = @"
                INSERT INTO notifications (Id, UserId, Title, Description, NotificationType, Note, IsRead, CreatedDate, IsDeleted)
                VALUES (@Id, @UserId, @Title, @Description, @NotificationType, @Data, false, CURRENT_DATE, false)
                RETURNING Id, UserId, Title, Description, NotificationType, Note, IsRead, CreatedDate";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("Id", notificationId);
            command.Parameters.AddWithValue("UserId", (object?)userId ?? DBNull.Value);
            command.Parameters.AddWithValue("Title", title);
            command.Parameters.AddWithValue("Description", message);
            command.Parameters.AddWithValue("NotificationType", (object?)type ?? DBNull.Value);
            command.Parameters.AddWithValue("Data", (object?)dataJson ?? DBNull.Value);

            using var reader = await command.ExecuteReaderAsync();
            NotificationDto? dto = null;
            
            if (await reader.ReadAsync())
            {
                dto = MapFromReader(reader);
            }

            if (dto != null)
            {
                // Push to channel for real-time delivery (SignalR)
                var ch = GetOrCreateChannel(userId ?? "__global__");
                await ch.Writer.WriteAsync(dto);
                
                // Send Firebase Cloud Messaging push notification (if user has registered device)
                if (!string.IsNullOrEmpty(userId) && _fcmService != null)
                {
                    try
                    {
                        // Send to user's topic (all devices subscribed to user_{userId})
                        var fcmData = new Dictionary<string, string>
                        {
                            { "notificationId", notificationId },
                            { "type", type ?? "general" },
                            { "userId", userId }
                        };
                        
                        if (data != null)
                        {
                            // Reuse dataJson from outer scope or serialize if needed
                            var fcmDataJson = dataJson ?? System.Text.Json.JsonSerializer.Serialize(data);
                            fcmData["data"] = fcmDataJson;
                        }

                        await _fcmService.SendToTopicAsync($"user_{userId}", title, message, fcmData);
                        _logger?.LogDebug("FCM push notification sent for user {UserId}", userId);
                    }
                    catch (Exception ex)
                    {
                        // Don't fail notification creation if FCM fails
                        _logger?.LogWarning(ex, "Failed to send FCM notification for user {UserId}", userId);
                    }
                }
                
                _logger?.LogInformation("Notification created: {NotificationId} for user {UserId}", notificationId, userId);
            }

            return dto ?? new NotificationDto
            {
                Id = notificationId,
                UserId = userId,
                Title = title,
                Message = message,
                Type = type,
                Read = false,
                CreatedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error creating notification for user {UserId}", userId);
            
            // Return a minimal notification object
            return new NotificationDto
            {
                Id = Guid.NewGuid().ToString(),
                UserId = userId,
                Title = title,
                Message = message,
                Type = type,
                Read = false,
                CreatedAt = DateTime.UtcNow
            };
        }
    }

    public async Task<IEnumerable<NotificationDto>> GetForUserAsync(string? userId)
    {
        var notifications = new List<NotificationDto>();
        
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT Id, UserId, Title, Description, NotificationType, Note, IsRead, CreatedDate
                FROM notifications
                WHERE (UserId = @UserId OR (@UserId IS NULL AND UserId IS NULL))
                    AND COALESCE(IsDeleted, false) = false
                ORDER BY CreatedDate DESC
                LIMIT 100";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("UserId", (object?)userId ?? DBNull.Value);

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                notifications.Add(MapFromReader(reader));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting notifications for user {UserId}", userId);
        }

        return notifications;
    }

    public async Task MarkReadAsync(string? userId, string id)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                UPDATE notifications 
                SET IsRead = true, UpdatedDate = CURRENT_DATE
                WHERE Id = @Id 
                    AND (UserId = @UserId OR (@UserId IS NULL AND UserId IS NULL))
                    AND COALESCE(IsDeleted, false) = false";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("Id", id);
            command.Parameters.AddWithValue("UserId", (object?)userId ?? DBNull.Value);

            await command.ExecuteNonQueryAsync();
            
            _logger?.LogDebug("Notification marked as read: {NotificationId}", id);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error marking notification as read {NotificationId}", id);
        }
    }

    public async Task MarkAllReadAsync(string? userId)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                UPDATE notifications 
                SET IsRead = true, UpdatedDate = CURRENT_DATE
                WHERE (UserId = @UserId OR (@UserId IS NULL AND UserId IS NULL))
                    AND IsRead = false
                    AND COALESCE(IsDeleted, false) = false";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("UserId", (object?)userId ?? DBNull.Value);

            var count = await command.ExecuteNonQueryAsync();
            
            _logger?.LogInformation("Marked {Count} notifications as read for user {UserId}", count, userId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error marking all notifications as read for user {UserId}", userId);
        }
    }

    public async IAsyncEnumerable<NotificationDto> StreamForUserAsync(string? userId, [EnumeratorCancellation] CancellationToken ct)
    {
        // Get channel for this user
        var ch = GetOrCreateChannel(userId ?? "__global__");
        var reader = ch.Reader;

        // First, send existing notifications
        var initial = await GetForUserAsync(userId);
        foreach (var n in initial)
        {
            yield return n;
        }

        // Then stream new notifications as they arrive
        while (!ct.IsCancellationRequested)
        {
            // Wait and read without try-catch wrapping yield
            var canRead = false;
            
            try
            {
                canRead = await reader.WaitToReadAsync(ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (!canRead) continue;

            while (reader.TryRead(out var n))
            {
                // Filter by user
                if ((n.UserId ?? string.Empty) == (userId ?? string.Empty) || 
                    (n.UserId == null && userId == null))
                {
                    yield return n;
                }
            }
        }
    }

    private static NotificationDto MapFromReader(NpgsqlDataReader reader)
    {
        var dataJson = reader.IsDBNull(5) ? null : reader.GetString(5);
        object? data = null;
        
        if (!string.IsNullOrEmpty(dataJson))
        {
            try
            {
                data = System.Text.Json.JsonSerializer.Deserialize<object>(dataJson);
            }
            catch
            {
                data = dataJson;
            }
        }

        return new NotificationDto
        {
            Id = reader.GetString(0),
            UserId = reader.IsDBNull(1) ? null : reader.GetString(1),
            Title = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            Message = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            Type = reader.IsDBNull(4) ? null : reader.GetString(4),
            Data = data,
            Read = !reader.IsDBNull(6) && reader.GetBoolean(6),
            CreatedAt = reader.IsDBNull(7) ? DateTime.UtcNow : reader.GetDateTime(7)
        };
    }
}
