using System.Text;
using Aura.Application.Services.Auth;
using Aura.Application.Services.Users;
using Aura.Application.Services.RBAC;
using Aura.Application.Services.Messages;
using Aura.Shared.Authorization;
using Aura.Shared.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Npgsql;
using Aura.API.Clinic;
using Aura.API.Hangfire;
using Aura.API.Hubs;
using Aura.API.Swagger;
using Aura.Infrastructure.Services.Payment;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel for larger file uploads (up to 50MB)
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 50 * 1024 * 1024; // 50MB
});

// Add SignalR for real-time messaging (FR-10)
builder.Services.AddSignalR();

// =============================================================================
// INFRASTRUCTURE: Redis Cache (Distributed Cache)
// =============================================================================
var redisConnectionString = builder.Configuration["Redis:ConnectionString"] ?? "redis:6379";
if (!string.IsNullOrWhiteSpace(redisConnectionString))
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConnectionString;
        options.InstanceName = "AURA:";
    });
}

// =============================================================================
// INFRASTRUCTURE: Memory Cache (In-Memory Cache)
// =============================================================================
builder.Services.AddMemoryCache(options =>
{
    options.SizeLimit = 1024; // Limit number of entries
});

// =============================================================================
// INFRASTRUCTURE: Hangfire Background Jobs (Worker Service)
// =============================================================================
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddHangfire(config =>
    {
        config.UsePostgreSqlStorage(connectionString, new PostgreSqlStorageOptions
        {
            SchemaName = "hangfire"
        });
        config.UseSimpleAssemblyNameTypeSerializer();
        config.UseRecommendedSerializerSettings();
    });
    builder.Services.AddHangfireServer(options =>
    {
        options.WorkerCount = Environment.ProcessorCount * 2;
        options.ServerName = "AURA-Worker";
    });
}

// Add services to the container
builder.Services.AddControllers(options =>
{
    // Configure multipart body length limit for file uploads
    options.MaxModelBindingCollectionSize = int.MaxValue;
})
    .AddJsonOptions(options =>
    {
        // Configure JSON serializer to use camelCase (matching frontend)
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    });
builder.Services.AddEndpointsApiExplorer();

// Configure form options for file uploads (up to 50MB)
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 50 * 1024 * 1024; // 50MB
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
});

// Configure Swagger with JWT authentication
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "AURA API",
        Version = "v1",
        Description = "API cho Hệ thống Sàng lọc Sức khỏe Mạch máu Võng mạc AURA"
    });

    // Use full namespace for schema IDs to avoid conflicts between DTOs with same name
    options.CustomSchemaIds(type => type.FullName?.Replace("+", ".") ?? type.Name);

    // Map IFormFile to file input in Swagger
    options.MapType<IFormFile>(() => new OpenApiSchema
    {
        Type = "string",
        Format = "binary"
    });

    // Add operation filter to handle file uploads properly
    options.OperationFilter<FileUploadOperationFilter>();

    // Add JWT authentication to Swagger
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Nhập token của bạn (Swagger sẽ tự động thêm 'Bearer ' prefix).\n\nVí dụ: eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9..."
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// Configure JWT Authentication
var jwtSettings = builder.Configuration.GetSection("Jwt");
var secretKey = jwtSettings["SecretKey"] ?? throw new InvalidOperationException("JWT SecretKey not configured");

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtSettings["Issuer"],
        ValidAudience = jwtSettings["Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey)),
        ClockSkew = TimeSpan.Zero // No tolerance for token expiration
    };

    // Configure SignalR JWT authentication
    options.Events = new JwtBearerEvents
    {
        OnAuthenticationFailed = context =>
        {
            if (context.Exception.GetType() == typeof(SecurityTokenExpiredException))
            {
                context.Response.Headers.Append("Token-Expired", "true");
            }
            return Task.CompletedTask;
        },
        OnMessageReceived = context =>
        {
            // Support JWT token in query string for SignalR (WebSocket doesn't support headers)
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            
            // Also check Authorization header (for LongPolling fallback)
            if (string.IsNullOrEmpty(accessToken))
            {
                var authHeader = context.Request.Headers["Authorization"].ToString();
                if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer "))
                {
                    accessToken = authHeader.Substring("Bearer ".Length).Trim();
                }
            }
            
            // SignalR: WebSocket không gửi được header; SSE notifications/stream cũng không gửi được Authorization
            if (!string.IsNullOrEmpty(accessToken) && (
                path.StartsWithSegments("/hubs") ||
                path.StartsWithSegments("/api/notifications/stream", StringComparison.OrdinalIgnoreCase)))
            {
                context.Token = accessToken;
            }
            return Task.CompletedTask;
        }
    };
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole("Admin", "SuperAdmin");
    });
});

// Add HttpContextAccessor for PermissionAuthorizationHandler
builder.Services.AddHttpContextAccessor();

// Register authorization handlers for RBAC
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins(
                builder.Configuration["App:FrontendUrl"] ?? "http://localhost:5173",
                "http://localhost:3000",
                "http://localhost:5173"
            )
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials() // Required for cookies
            .WithExposedHeaders("Token-Expired"); // Expose custom headers
    });
});

// Register application services
builder.Services.AddScoped<IJwtService, JwtService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IUserService, UserService>();

// FR-2: Image Services
builder.Services.AddScoped<Aura.Application.Services.Images.IImageService, Aura.Application.Services.Images.ImageService>();

// FR-3: Analysis Services
// HttpClient cho AnalysisService (để gọi analysis-service microservice)
builder.Services.AddHttpClient<Aura.Application.Services.Analysis.AnalysisService>(client =>
{
    var timeoutValue = builder.Configuration["AnalysisService:Timeout"] ?? "30000";
    client.Timeout = TimeSpan.FromMilliseconds(
        int.TryParse(timeoutValue, out var timeout) ? timeout : 30000);
});
builder.Services.AddScoped<Aura.Application.Services.Analysis.IAnalysisService, Aura.Application.Services.Analysis.AnalysisService>();

// FR-7: Export Services (PDF/CSV/JSON)
builder.Services.AddScoped<Aura.Application.Services.Export.IExportService, Aura.Application.Services.Export.ExportService>();

// HttpClient cho AnalysisServiceClient (để gọi analysis-service từ controllers nếu cần)
builder.Services.AddHttpClient<Aura.API.Services.AnalysisServiceClient>(client =>
{
    var baseUrl = builder.Configuration["AnalysisService:BaseUrl"] ?? "http://analysis-service:5004";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddScoped<Aura.API.Services.AnalysisServiceClient>();

// FR-24: Analysis Queue Service for batch processing (NFR-2: ≥100 images per batch)
builder.Services.AddScoped<Aura.Application.Services.Analysis.IAnalysisQueueService, Aura.Application.Services.Analysis.AnalysisQueueService>();

// FR-24: Bulk Upload Batch Services (commented out - service not implemented yet)
// builder.Services.AddScoped<Aura.Application.Services.Images.IBulkUploadBatchService, Aura.Application.Services.Images.BulkUploadBatchService>();

// FR-29: High-Risk Alert Service
builder.Services.AddScoped<Aura.Application.Services.Alerts.IHighRiskAlertService, Aura.Application.Services.Alerts.HighRiskAlertService>();

// FR-27: Usage Tracking Service
builder.Services.AddScoped<Aura.Application.Services.UsageTracking.IUsageTrackingService, Aura.Application.Services.UsageTracking.UsageTrackingService>();

// FR-26: Clinic Report Generation Service
builder.Services.AddScoped<Aura.Application.Services.Reports.IClinicReportService, Aura.Application.Services.Reports.ClinicReportService>();

// FR-18: Patient Search Service
builder.Services.AddScoped<Aura.Application.Services.Doctors.IPatientSearchService, Aura.Application.Services.Doctors.PatientSearchService>();

// Notifications (PostgreSQL backed with real-time streaming)
builder.Services.AddScoped<Aura.Application.Services.Notifications.INotificationService, Aura.Infrastructure.Services.Notifications.NotificationService>();

// =============================================================================
// INFRASTRUCTURE: RabbitMQ Message Queue Service
// =============================================================================
builder.Services.AddSingleton<Aura.Infrastructure.Services.RabbitMQ.IRabbitMQService, Aura.Infrastructure.Services.RabbitMQ.RabbitMQService>();

// =============================================================================
// INFRASTRUCTURE: Firebase Cloud Messaging (Push Notifications)
// =============================================================================
builder.Services.AddSingleton<Aura.Infrastructure.Services.Firebase.IFirebaseMessagingService, Aura.Infrastructure.Services.Firebase.FirebaseMessagingService>();

// INFRASTRUCTURE: Payment Gateway Services
// =============================================================================
builder.Services.AddScoped<IPaymentGatewayService, VNPayService>();

// FR-10: Messaging Services
builder.Services.AddScoped<IMessageService, MessageService>();

// FR-32: RBAC Services
builder.Services.AddScoped<Aura.Application.Repositories.IRbacRepository, Aura.API.Admin.RbacRepository>();
builder.Services.AddScoped<IRoleService, RoleService>();
builder.Services.AddScoped<IPermissionService, PermissionService>();

// FR-31: Admin Account Management (DB based)
builder.Services.AddScoped<Aura.API.Admin.AdminDb>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var logger = sp.GetService<ILogger<Aura.API.Admin.AdminDb>>();
    return new Aura.API.Admin.AdminDb(config, logger);
});
builder.Services.AddScoped<Aura.API.Admin.AdminJwtService>();
builder.Services.AddScoped<Aura.API.Admin.AdminAccountRepository>();
builder.Services.AddScoped<Aura.API.Admin.AnalyticsRepository>();
builder.Services.AddScoped<Aura.API.Admin.AIConfigurationRepository>();
builder.Services.AddScoped<Aura.API.Admin.ServicePackageRepository>();
builder.Services.AddScoped<Aura.API.Admin.AuditLogRepository>();
builder.Services.AddScoped<Aura.API.Admin.PrivacySettingsRepository>();
builder.Services.AddScoped<Aura.API.Admin.ClinicRepository>();
builder.Services.AddScoped<Aura.API.Admin.NotificationTemplateRepository>();

// FR-22: Clinic Management
builder.Services.AddScoped<ClinicDb>();
builder.Services.AddScoped<ClinicRepository>();

// FR-22: Clinic Authentication
builder.Services.AddScoped<Aura.Application.Services.Clinic.IClinicAuthService, Aura.Application.Services.Clinic.ClinicAuthService>();

// FR-23: Clinic Management (Doctors/Patients)
builder.Services.AddScoped<Aura.Application.Services.Clinic.IClinicManagementService, Aura.Application.Services.Clinic.ClinicManagementService>();

// NFR-11: Data Anonymization Service
builder.Services.AddScoped<Aura.Application.Services.Anonymization.IDataAnonymizationService, Aura.Application.Services.Anonymization.DataAnonymizationService>();

// Register background worker services
builder.Services.AddScoped<Aura.API.Services.BackgroundJobs.AnalysisQueueWorker>();
builder.Services.AddScoped<Aura.API.Services.BackgroundJobs.RiskAlertWorker>();
builder.Services.AddScoped<Aura.API.Services.BackgroundJobs.DatabaseBackupWorker>();
builder.Services.AddScoped<Aura.API.Services.BackgroundJobs.DataAnonymizationWorker>();

// Register database schema fixer (auto-fix missing columns on startup)
builder.Services.AddScoped<Aura.API.Services.DatabaseSchemaFixer>();

// TODO: Add database context when ready
// builder.Services.AddDbContext<AuraDbContext>(options =>
//     options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// TODO: Add repositories
// builder.Services.AddScoped<IUserRepository, UserRepository>();

var app = builder.Build();

// Auto-fix database schema (missing columns) on startup
try
{
    using (var scope = app.Services.CreateScope())
    {
        var schemaFixer = scope.ServiceProvider.GetRequiredService<Aura.API.Services.DatabaseSchemaFixer>();
        await schemaFixer.FixMissingColumnsAsync();
    }
}
catch (Exception ex)
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogWarning(ex, "Database schema fix failed on startup (non-critical, continuing)");
}

// Configure the HTTP request pipeline
// Enable Swagger and Swagger UI in all environments so admin can access the docs locally
if (app.Environment.IsDevelopment() || app.Environment.IsProduction())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "AURA API v1");
        options.RoutePrefix = "swagger";
        options.DisplayRequestDuration();
    });
}

// Mặc định không ép HTTPS để tránh lỗi "Network Error" khi local chỉ chạy http://localhost:5000
// Nếu deploy thật sự cần HTTPS redirect, bật cấu hình App:UseHttpsRedirection = true
if (app.Configuration.GetValue<bool>("App:UseHttpsRedirection"))
{
    app.UseHttpsRedirection();
}

// Use CORS before authentication
app.UseCors("AllowFrontend");

// Enable request buffering for file uploads
app.Use(async (context, next) =>
{
    context.Request.EnableBuffering(); // Enable request buffering for file uploads
    await next();
});

// Add headers to support OAuth popups (fix Cross-Origin-Opener-Policy warning)
app.Use(async (context, next) =>
{
    // Set Cross-Origin-Opener-Policy to allow OAuth popups
    context.Response.Headers.Append("Cross-Origin-Opener-Policy", "same-origin-allow-popups");
    context.Response.Headers.Append("Cross-Origin-Embedder-Policy", "unsafe-none");
    await next();
});

// NFR-1 & NFR-3: đo thời gian xử lý request để đánh giá hiệu năng API
app.UseMiddleware<RequestTimingMiddleware>();

// Authentication & Authorization middleware
app.UseAuthentication();

// FR-32: RBAC Authorization Middleware (loads user roles/permissions into context)
app.UseMiddleware<RbacAuthorizationMiddleware>();

app.UseAuthorization();

app.MapControllers();

// Map SignalR Hub for real-time messaging (FR-10)
// Note: SignalR negotiation can be anonymous, but OnConnectedAsync will check authentication
// This allows the connection to be established, then we verify token in the hub
app.MapHub<ChatHub>("/hubs/chat");

// =============================================================================
// INFRASTRUCTURE: Hangfire Dashboard (Background Jobs UI)
// =============================================================================
// Only enable in Development or if explicitly configured
if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Hangfire:EnableDashboard"))
{
    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = new[] { new HangfireAuthorizationFilter() },
        DashboardTitle = "AURA Background Jobs",
        StatsPollingInterval = 2000
    });
}

// =============================================================================
// INFRASTRUCTURE: Register Recurring Background Jobs (Worker Service)
// =============================================================================
using (var scope = app.Services.CreateScope())
{
    var recurringJobManager = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    
    // Process analysis queue every 5 minutes
    recurringJobManager.AddOrUpdate(
        "process-analysis-queue",
        () => scope.ServiceProvider.GetRequiredService<Aura.API.Services.BackgroundJobs.AnalysisQueueWorker>()
            .ProcessAnalysisQueueAsync(),
        "*/5 * * * *"); // Every 5 minutes

    // Cleanup expired exports daily at 2:00 AM
    recurringJobManager.AddOrUpdate(
        "cleanup-expired-exports",
        () => scope.ServiceProvider.GetRequiredService<Aura.API.Services.BackgroundJobs.AnalysisQueueWorker>()
            .CleanupExpiredExportsAsync(),
        "0 2 * * *"); // Daily at 2:00 AM

    // Process email queue every 10 minutes
    recurringJobManager.AddOrUpdate(
        "process-email-queue",
        () => scope.ServiceProvider.GetRequiredService<Aura.API.Services.BackgroundJobs.AnalysisQueueWorker>()
            .ProcessEmailQueueAsync(),
        "*/10 * * * *"); // Every 10 minutes

    // Check high-risk patients every hour (FR-29)
    recurringJobManager.AddOrUpdate(
        "check-high-risk-patients",
        () => scope.ServiceProvider.GetRequiredService<Aura.API.Services.BackgroundJobs.RiskAlertWorker>()
            .CheckHighRiskPatientsAsync(),
        "0 * * * *"); // Every hour

    // Check abnormal trends daily at 6:00 AM (FR-29)
    recurringJobManager.AddOrUpdate(
        "check-abnormal-trends",
        () => scope.ServiceProvider.GetRequiredService<Aura.API.Services.BackgroundJobs.RiskAlertWorker>()
            .CheckAbnormalTrendsAsync(),
        "0 6 * * *"); // Daily at 6:00 AM

    // Daily database backup at 3:00 AM (NFR-6)
    recurringJobManager.AddOrUpdate(
        "daily-database-backup",
        () => scope.ServiceProvider.GetRequiredService<Aura.API.Services.BackgroundJobs.DatabaseBackupWorker>()
            .PerformDailyBackupAsync(),
        "0 3 * * *"); // Daily at 3:00 AM

    // Anonymize old audit logs weekly on Sunday at 2:00 AM (NFR-11)
    recurringJobManager.AddOrUpdate(
        "anonymize-old-audit-logs",
        () => scope.ServiceProvider.GetRequiredService<Aura.API.Services.BackgroundJobs.DataAnonymizationWorker>()
            .AnonymizeOldAuditLogsAsync(),
        "0 2 * * 0"); // Weekly on Sunday at 2:00 AM
}

// Health check endpoint with database connection test
app.MapGet("/health", async (IConfiguration config) =>
{
    try
    {
        var cs = config.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrWhiteSpace(cs))
        {
            using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            using var cmd = new NpgsqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync();
        }
        return Results.Ok(new { status = "healthy", database = "connected", timestamp = DateTime.UtcNow });
    }
    catch
    {
        return Results.StatusCode(503);
    }
});

app.Run();
