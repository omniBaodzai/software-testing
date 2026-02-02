using Npgsql;
using System.Text.Json;

namespace Aura.API.Admin;

public class AuditLogRepository
{
    private readonly AdminDb _db;

    public AuditLogRepository(AdminDb db)
    {
        _db = db;
    }

    public async Task<List<AuditLogRowDto>> ListAsync(AuditLogFilterDto? filter = null, int page = 1, int pageSize = 100)
    {
        using var conn = _db.OpenConnection();
        
        var whereConditions = new List<string>();
        var parameters = new List<NpgsqlParameter>();

        if (filter != null)
        {
            if (!string.IsNullOrWhiteSpace(filter.UserId))
            {
                whereConditions.Add("(userid = @userId OR (resourcetype = 'User' AND resourceid = @userId))");
                parameters.Add(new NpgsqlParameter("userId", filter.UserId));
            }
            if (!string.IsNullOrWhiteSpace(filter.DoctorId))
            {
                whereConditions.Add("(doctorid = @doctorId OR (resourcetype = 'Doctor' AND resourceid = @doctorId))");
                parameters.Add(new NpgsqlParameter("doctorId", filter.DoctorId));
            }
            if (!string.IsNullOrWhiteSpace(filter.AdminId))
            {
                whereConditions.Add("adminid = @adminId");
                parameters.Add(new NpgsqlParameter("adminId", filter.AdminId));
            }
            if (!string.IsNullOrWhiteSpace(filter.ActionType))
            {
                whereConditions.Add("actiontype = @actionType");
                parameters.Add(new NpgsqlParameter("actionType", filter.ActionType));
            }
            if (!string.IsNullOrWhiteSpace(filter.ResourceType))
            {
                whereConditions.Add("resourcetype = @resourceType");
                parameters.Add(new NpgsqlParameter("resourceType", filter.ResourceType));
            }
            if (!string.IsNullOrWhiteSpace(filter.ResourceId))
            {
                whereConditions.Add("resourceid = @resourceId");
                parameters.Add(new NpgsqlParameter("resourceId", filter.ResourceId));
            }
            if (filter.StartDate.HasValue)
            {
                whereConditions.Add("createddate >= @startDate");
                parameters.Add(new NpgsqlParameter("startDate", filter.StartDate.Value.Date));
            }
            if (filter.EndDate.HasValue)
            {
                whereConditions.Add("createddate <= @endDate");
                parameters.Add(new NpgsqlParameter("endDate", filter.EndDate.Value.Date));
            }
            if (!string.IsNullOrWhiteSpace(filter.IpAddress))
            {
                whereConditions.Add("ipaddress LIKE @ipAddress");
                parameters.Add(new NpgsqlParameter("ipAddress", $"%{filter.IpAddress}%"));
            }
        }

        var whereClause = whereConditions.Count > 0 
            ? "WHERE " + string.Join(" AND ", whereConditions)
            : "";

        var offset = (page - 1) * pageSize;

        using var cmd = new NpgsqlCommand($@"
SELECT
    id,
    userid,
    doctorid,
    adminid,
    actiontype,
    resourcetype,
    resourceid,
    oldvalues::text,
    newvalues::text,
    ipaddress,
    useragent,
    createddate,
    createdby
FROM audit_logs
{whereClause}
ORDER BY createddate DESC NULLS LAST
LIMIT @pageSize OFFSET @offset;", conn);

        cmd.Parameters.AddWithValue("pageSize", pageSize);
        cmd.Parameters.AddWithValue("offset", offset);
        foreach (var p in parameters)
        {
            cmd.Parameters.Add(p);
        }

        var list = new List<AuditLogRowDto>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new AuditLogRowDto(
                r.GetString(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetString(4),
                r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetString(8),
                r.IsDBNull(9) ? null : r.GetString(9),
                r.IsDBNull(10) ? null : r.GetString(10),
                r.IsDBNull(11) ? null : r.GetDateTime(11),
                r.IsDBNull(12) ? null : r.GetString(12)
            ));
        }

        return list;
    }

    public async Task<int> CountAsync(AuditLogFilterDto? filter = null)
    {
        using var conn = _db.OpenConnection();
        
        var whereConditions = new List<string>();
        var parameters = new List<NpgsqlParameter>();

        if (filter != null)
        {
            if (!string.IsNullOrWhiteSpace(filter.UserId))
            {
                whereConditions.Add("(userid = @userId OR (resourcetype = 'User' AND resourceid = @userId))");
                parameters.Add(new NpgsqlParameter("userId", filter.UserId));
            }
            if (!string.IsNullOrWhiteSpace(filter.DoctorId))
            {
                whereConditions.Add("(doctorid = @doctorId OR (resourcetype = 'Doctor' AND resourceid = @doctorId))");
                parameters.Add(new NpgsqlParameter("doctorId", filter.DoctorId));
            }
            if (!string.IsNullOrWhiteSpace(filter.AdminId))
            {
                whereConditions.Add("adminid = @adminId");
                parameters.Add(new NpgsqlParameter("adminId", filter.AdminId));
            }
            if (!string.IsNullOrWhiteSpace(filter.ActionType))
            {
                whereConditions.Add("actiontype = @actionType");
                parameters.Add(new NpgsqlParameter("actionType", filter.ActionType));
            }
            if (!string.IsNullOrWhiteSpace(filter.ResourceType))
            {
                whereConditions.Add("resourcetype = @resourceType");
                parameters.Add(new NpgsqlParameter("resourceType", filter.ResourceType));
            }
            if (!string.IsNullOrWhiteSpace(filter.ResourceId))
            {
                whereConditions.Add("resourceid = @resourceId");
                parameters.Add(new NpgsqlParameter("resourceId", filter.ResourceId));
            }
            if (filter.StartDate.HasValue)
            {
                whereConditions.Add("createddate >= @startDate");
                parameters.Add(new NpgsqlParameter("startDate", filter.StartDate.Value.Date));
            }
            if (filter.EndDate.HasValue)
            {
                whereConditions.Add("createddate <= @endDate");
                parameters.Add(new NpgsqlParameter("endDate", filter.EndDate.Value.Date));
            }
            if (!string.IsNullOrWhiteSpace(filter.IpAddress))
            {
                whereConditions.Add("ipaddress LIKE @ipAddress");
                parameters.Add(new NpgsqlParameter("ipAddress", $"%{filter.IpAddress}%"));
            }
        }

        var whereClause = whereConditions.Count > 0 
            ? "WHERE " + string.Join(" AND ", whereConditions)
            : "";

        using var cmd = new NpgsqlCommand($@"
SELECT COUNT(*)
FROM audit_logs
{whereClause};", conn);

        foreach (var p in parameters)
        {
            cmd.Parameters.Add(p);
        }

        var result = await cmd.ExecuteScalarAsync();
        return result != null ? Convert.ToInt32(result) : 0;
    }

    public async Task<AuditLogRowDto?> GetByIdAsync(string id)
    {
        using var conn = _db.OpenConnection();
        using var cmd = new NpgsqlCommand(@"
SELECT
    id,
    userid,
    doctorid,
    adminid,
    actiontype,
    resourcetype,
    resourceid,
    oldvalues::text,
    newvalues::text,
    ipaddress,
    useragent,
    createddate,
    createdby
FROM audit_logs
WHERE id = @id
LIMIT 1;", conn);

        cmd.Parameters.AddWithValue("id", id);
        using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;

        return new AuditLogRowDto(
            r.GetString(0),
            r.IsDBNull(1) ? null : r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3),
            r.GetString(4),
            r.GetString(5),
            r.IsDBNull(6) ? null : r.GetString(6),
            r.IsDBNull(7) ? null : r.GetString(7),
            r.IsDBNull(8) ? null : r.GetString(8),
            r.IsDBNull(9) ? null : r.GetString(9),
            r.IsDBNull(10) ? null : r.GetString(10),
            r.IsDBNull(11) ? null : r.GetDateTime(11),
            r.IsDBNull(12) ? null : r.GetString(12)
        );
    }

    public async Task<ComplianceReportDto> GetComplianceReportAsync(DateTime? startDate = null, DateTime? endDate = null)
    {
        using var conn = _db.OpenConnection();
        
        var dateFilter = "";
        var parameters = new List<NpgsqlParameter>();
        
        if (startDate.HasValue || endDate.HasValue)
        {
            var conditions = new List<string>();
            if (startDate.HasValue)
            {
                conditions.Add("createddate >= @startDate");
                parameters.Add(new NpgsqlParameter("startDate", startDate.Value.Date));
            }
            if (endDate.HasValue)
            {
                conditions.Add("createddate <= @endDate");
                parameters.Add(new NpgsqlParameter("endDate", endDate.Value.Date));
            }
            dateFilter = "WHERE " + string.Join(" AND ", conditions);
        }

        // Total logs
        using var cmdTotal = new NpgsqlCommand($@"
SELECT COUNT(*) FROM audit_logs {dateFilter};", conn);
        foreach (var p in parameters)
        {
            cmdTotal.Parameters.Add(p);
        }
        var total = Convert.ToInt32(await cmdTotal.ExecuteScalarAsync() ?? 0);

        // Last 30 days
        using var cmd30 = new NpgsqlCommand(@"
SELECT COUNT(*) FROM audit_logs WHERE createddate >= (CURRENT_DATE - INTERVAL '30 days')::DATE;", conn);
        var last30Days = Convert.ToInt32(await cmd30.ExecuteScalarAsync() ?? 0);

        // Last 7 days
        using var cmd7 = new NpgsqlCommand(@"
SELECT COUNT(*) FROM audit_logs WHERE createddate >= (CURRENT_DATE - INTERVAL '7 days')::DATE;", conn);
        var last7Days = Convert.ToInt32(await cmd7.ExecuteScalarAsync() ?? 0);

        // Unique users
        using var cmdUsers = new NpgsqlCommand(@"
SELECT COUNT(DISTINCT userid) FROM audit_logs WHERE userid IS NOT NULL;", conn);
        var uniqueUsers = Convert.ToInt32(await cmdUsers.ExecuteScalarAsync() ?? 0);

        // Unique admins
        using var cmdAdmins = new NpgsqlCommand(@"
SELECT COUNT(DISTINCT adminid) FROM audit_logs WHERE adminid IS NOT NULL;", conn);
        var uniqueAdmins = Convert.ToInt32(await cmdAdmins.ExecuteScalarAsync() ?? 0);

        // Action type counts
        using var cmdActions = new NpgsqlCommand($@"
SELECT actiontype, COUNT(*) as cnt
FROM audit_logs
{dateFilter}
GROUP BY actiontype
ORDER BY cnt DESC;", conn);
        foreach (var p in parameters)
        {
            cmdActions.Parameters.Add(p);
        }
        var actionTypeCounts = new Dictionary<string, int>();
        using var rActions = await cmdActions.ExecuteReaderAsync();
        while (await rActions.ReadAsync())
        {
            if (!rActions.IsDBNull(0) && !rActions.IsDBNull(1))
            {
                actionTypeCounts[rActions.GetString(0)] = rActions.GetInt32(1);
            }
        }

        // Resource type counts
        using var cmdResources = new NpgsqlCommand($@"
SELECT resourcetype, COUNT(*) as cnt
FROM audit_logs
{dateFilter}
GROUP BY resourcetype
ORDER BY cnt DESC;", conn);
        foreach (var p in parameters)
        {
            cmdResources.Parameters.Add(p);
        }
        var resourceTypeCounts = new Dictionary<string, int>();
        using var rResources = await cmdResources.ExecuteReaderAsync();
        while (await rResources.ReadAsync())
        {
            if (!rResources.IsDBNull(0) && !rResources.IsDBNull(1))
            {
                resourceTypeCounts[rResources.GetString(0)] = rResources.GetInt32(1);
            }
        }

        // Compliance issues (simplified - can be expanded)
        var issues = new List<ComplianceIssueDto>();

        // Check for suspicious patterns (example)
        using var cmdSuspicious = new NpgsqlCommand(@"
SELECT COUNT(*) FROM audit_logs 
WHERE actiontype IN ('Delete', 'Update') 
AND createddate >= (CURRENT_DATE - INTERVAL '7 days')::DATE;", conn);
        var suspiciousCount = Convert.ToInt32(await cmdSuspicious.ExecuteScalarAsync() ?? 0);
        if (suspiciousCount > 100)
        {
            issues.Add(new ComplianceIssueDto(
                "HighActivity",
                $"Nhiều thao tác Delete/Update trong 7 ngày qua ({suspiciousCount})",
                "Medium",
                suspiciousCount,
                DateTime.UtcNow
            ));
        }

        return new ComplianceReportDto(
            total,
            last30Days,
            last7Days,
            uniqueUsers,
            uniqueAdmins,
            actionTypeCounts,
            resourceTypeCounts,
            issues
        );
    }

    /// <summary>Ghi một bản ghi audit log.</summary>
    public async Task<int> InsertAsync(
        string? adminId,
        string actionType,
        string resourceType,
        string? resourceId,
        string? oldValues,
        string? newValues,
        string? ipAddress,
        string? userAgent = null)
    {
        var id = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;
        using var conn = _db.OpenConnection();
        using var cmd = new NpgsqlCommand(@"
INSERT INTO audit_logs (id, userid, doctorid, adminid, actiontype, resourcetype, resourceid, oldvalues, newvalues, ipaddress, useragent, createddate, createdby)
VALUES (@id, NULL, NULL, @adminId, @actionType, @resourceType, @resourceId, @oldValues::jsonb, @newValues::jsonb, @ipAddress, @userAgent, @createdDate, @createdBy);", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("adminId", (object?)adminId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("actionType", actionType);
        cmd.Parameters.AddWithValue("resourceType", resourceType);
        cmd.Parameters.AddWithValue("resourceId", (object?)resourceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("oldValues", (object?)(string.IsNullOrWhiteSpace(oldValues) ? null : oldValues) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("newValues", (object?)(string.IsNullOrWhiteSpace(newValues) ? null : newValues) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ipAddress", (object?)ipAddress ?? DBNull.Value);
        cmd.Parameters.AddWithValue("userAgent", (object?)userAgent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("createdDate", now);
        cmd.Parameters.AddWithValue("createdBy", (object?)adminId ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Chèn audit log mẫu (test/demo).</summary>
    public async Task<int> InsertSampleLogsAsync(string? adminId, string? ipAddress = null)
    {
        ipAddress ??= "127.0.0.1";
        var samples = new[]
        {
            ("CreatePackage", "ServicePackage", "pkg-sample-1"),
            ("UpdatePackage", "ServicePackage", "pkg-sample-1"),
            ("CreateAIConfig", "AIConfiguration", "ai-sample-1"),
            ("UpdateUser", "User", (string?)null),
            ("ApproveClinic", "Clinic", "clinic-sample-1"),
        };
        var inserted = 0;
        foreach (var (actionType, resourceType, resourceId) in samples)
        {
            inserted += await InsertAsync(adminId, actionType, resourceType, resourceId, null, null, ipAddress);
        }
        return inserted;
    }
}
