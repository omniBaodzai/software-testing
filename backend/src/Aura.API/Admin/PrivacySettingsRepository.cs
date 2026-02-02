using Npgsql;
using System.Text.Json;

namespace Aura.API.Admin;

/// <summary>
/// Repository để lưu/đọc Privacy Settings từ database (FR-37)
/// </summary>
public class PrivacySettingsRepository
{
    private readonly AdminDb _db;

    public PrivacySettingsRepository(AdminDb db)
    {
        _db = db;
    }

    /// <summary>
    /// Lấy privacy settings hiện tại (hoặc default nếu chưa có)
    /// </summary>
    public async Task<PrivacySettingsDto> GetSettingsAsync()
    {
        using var conn = _db.OpenConnection();

        var sql = @"
            SELECT 
                EnableAuditLogging, AuditLogRetentionDays, AnonymizeOldLogs,
                RequireConsentForDataSharing, EnableGdprCompliance, DataRetentionDays,
                AllowDataExport, RequireTwoFactorForSensitiveActions
            FROM privacy_settings
            WHERE IsActive = true
            ORDER BY UpdatedDate DESC NULLS LAST, CreatedDate DESC
            LIMIT 1";

        try
        {
            using var cmd = new NpgsqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                return new PrivacySettingsDto(
                    EnableAuditLogging: reader.GetBoolean(0),
                    AuditLogRetentionDays: reader.GetInt32(1),
                    AnonymizeOldLogs: reader.GetBoolean(2),
                    RequireConsentForDataSharing: reader.GetBoolean(3),
                    EnableGdprCompliance: reader.GetBoolean(4),
                    DataRetentionDays: reader.GetInt32(5),
                    AllowDataExport: reader.GetBoolean(6),
                    RequireTwoFactorForSensitiveActions: reader.GetBoolean(7)
                );
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            // Table doesn't exist, return defaults
        }

        // Return default settings nếu chưa có trong DB
        return new PrivacySettingsDto(
            EnableAuditLogging: true,
            AuditLogRetentionDays: 365,
            AnonymizeOldLogs: false,
            RequireConsentForDataSharing: true,
            EnableGdprCompliance: true,
            DataRetentionDays: 2555, // 7 years
            AllowDataExport: true,
            RequireTwoFactorForSensitiveActions: false
        );
    }

    /// <summary>
    /// Cập nhật privacy settings
    /// </summary>
    public async Task UpdateSettingsAsync(UpdatePrivacySettingsDto dto, string? updatedBy = null)
    {
        using var conn = _db.OpenConnection();

        // Deactivate old settings
        var deactivateSql = @"
            UPDATE privacy_settings
            SET IsActive = false, UpdatedDate = CURRENT_DATE, UpdatedBy = @UpdatedBy
            WHERE IsActive = true";

        using (var cmd = new NpgsqlCommand(deactivateSql, conn))
        {
            cmd.Parameters.AddWithValue("UpdatedBy", (object?)updatedBy ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        // Get current settings để merge với updates
        var current = await GetSettingsAsync();

        // Insert new settings
        var insertSql = @"
            INSERT INTO privacy_settings (
                Id, EnableAuditLogging, AuditLogRetentionDays, AnonymizeOldLogs,
                RequireConsentForDataSharing, EnableGdprCompliance, DataRetentionDays,
                AllowDataExport, RequireTwoFactorForSensitiveActions,
                IsActive, CreatedDate, CreatedBy, UpdatedDate, UpdatedBy
            ) VALUES (
                @Id, @EnableAuditLogging, @AuditLogRetentionDays, @AnonymizeOldLogs,
                @RequireConsentForDataSharing, @EnableGdprCompliance, @DataRetentionDays,
                @AllowDataExport, @RequireTwoFactorForSensitiveActions,
                true, CURRENT_DATE, @CreatedBy, CURRENT_DATE, @UpdatedBy
            )";

        using var insertCmd = new NpgsqlCommand(insertSql, conn);
        insertCmd.Parameters.AddWithValue("Id", Guid.NewGuid().ToString());
        insertCmd.Parameters.AddWithValue("EnableAuditLogging", (object?)dto.EnableAuditLogging ?? current.EnableAuditLogging);
        insertCmd.Parameters.AddWithValue("AuditLogRetentionDays", (object?)dto.AuditLogRetentionDays ?? current.AuditLogRetentionDays);
        insertCmd.Parameters.AddWithValue("AnonymizeOldLogs", (object?)dto.AnonymizeOldLogs ?? current.AnonymizeOldLogs);
        insertCmd.Parameters.AddWithValue("RequireConsentForDataSharing", (object?)dto.RequireConsentForDataSharing ?? current.RequireConsentForDataSharing);
        insertCmd.Parameters.AddWithValue("EnableGdprCompliance", (object?)dto.EnableGdprCompliance ?? current.EnableGdprCompliance);
        insertCmd.Parameters.AddWithValue("DataRetentionDays", (object?)dto.DataRetentionDays ?? current.DataRetentionDays);
        insertCmd.Parameters.AddWithValue("AllowDataExport", (object?)dto.AllowDataExport ?? current.AllowDataExport);
        insertCmd.Parameters.AddWithValue("RequireTwoFactorForSensitiveActions", (object?)dto.RequireTwoFactorForSensitiveActions ?? current.RequireTwoFactorForSensitiveActions);
        insertCmd.Parameters.AddWithValue("CreatedBy", (object?)updatedBy ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("UpdatedBy", (object?)updatedBy ?? DBNull.Value);

        await insertCmd.ExecuteNonQueryAsync();
    }
}
