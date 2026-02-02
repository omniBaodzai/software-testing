using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aura.Shared.Middleware;

/// <summary>
/// NFR-1 & NFR-3: Middleware đo thời gian xử lý cho mỗi HTTP request.
/// Ghi log thời gian xử lý (ms) để phục vụ đánh giá hiệu năng các API chính.
/// </summary>
public class RequestTimingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestTimingMiddleware> _logger;

    public RequestTimingMiddleware(RequestDelegate next, ILogger<RequestTimingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();

            var path = context.Request.Path.Value ?? string.Empty;
            var method = context.Request.Method;
            var statusCode = context.Response?.StatusCode ?? 0;
            var elapsedMs = stopwatch.ElapsedMilliseconds;

            // Chỉ log chi tiết cho các API chính hoặc request chậm (ví dụ > 500 ms)
            if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) || elapsedMs > 500)
            {
                _logger.LogInformation(
                    "Request timing: {Method} {Path} responded {StatusCode} in {ElapsedMilliseconds} ms",
                    method,
                    path,
                    statusCode,
                    elapsedMs);
            }
        }
    }
}

