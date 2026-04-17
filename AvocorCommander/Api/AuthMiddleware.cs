using AvocorCommander.Services;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace AvocorCommander.Api;

/// <summary>
/// ASP.NET Core middleware that enforces authentication on all /api/ routes
/// except /api/auth/login. Supports two auth methods:
///   1. X-API-Key header → validated via ServerDbService
///   2. Authorization: Bearer {token} → validated via JwtService
/// </summary>
public sealed class AuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ServerDbService _serverDb;
    private readonly JwtService      _jwt;

    public AuthMiddleware(RequestDelegate next, ServerDbService serverDb, JwtService jwt)
    {
        _next     = next;
        _serverDb = serverDb;
        _jwt      = jwt;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // Skip auth for non-API routes and the login endpoint
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // 1. Try X-API-Key header
        if (context.Request.Headers.TryGetValue("X-API-Key", out var apiKeyValues))
        {
            string? apiKey = apiKeyValues.FirstOrDefault();
            if (!string.IsNullOrEmpty(apiKey) && _serverDb.ValidateApiKey(apiKey))
            {
                // API key is valid — set a synthetic identity
                context.Items["AuthMethod"]  = "ApiKey";
                context.Items["AuthUser"]    = "api-key";
                context.Items["AuthRole"]    = "Admin";
                await _next(context);
                return;
            }
        }

        // 2. Try Authorization: Bearer <token>
        if (context.Request.Headers.TryGetValue("Authorization", out var authValues))
        {
            string? authHeader = authValues.FirstOrDefault();
            if (!string.IsNullOrEmpty(authHeader) &&
                authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                string token = authHeader["Bearer ".Length..].Trim();
                var claims = _jwt.ValidateToken(token);
                if (claims != null)
                {
                    context.Items["AuthMethod"]  = "JWT";
                    context.Items["AuthUser"]    = claims.Username;
                    context.Items["AuthRole"]    = claims.Role;
                    await _next(context);
                    return;
                }
            }
        }

        // 3. Neither — 401
        context.Response.StatusCode = 401;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            JsonSerializer.Serialize(new { error = "Unauthorized", message = "Valid API key or Bearer token required." }));
    }
}
