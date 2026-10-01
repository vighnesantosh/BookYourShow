using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace BookYourShow.BuildingBlocks.Security;

public interface ICurrentUser
{
    /// <summary>Cognito "sub" of the caller.</summary>
    string UserId { get; }
    bool IsAdmin { get; }
}

/// <summary>
/// Reads the caller from the validated JWT (Cognito). In Development only, an
/// X-User-Id header is accepted so the system can be exercised with curl or the demo page.
/// </summary>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor, IWebHostEnvironment env) : ICurrentUser
{
    public string UserId
    {
        get
        {
            var http = accessor.HttpContext ?? throw new InvalidOperationException("No HTTP context");
            var sub = http.User.FindFirstValue("sub") ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!string.IsNullOrEmpty(sub)) return sub;

            if (env.IsDevelopment() && http.Request.Headers.TryGetValue("X-User-Id", out var devUser) && !string.IsNullOrWhiteSpace(devUser))
                return devUser.ToString();

            throw new UnauthorizedAccessException("Caller is not authenticated.");
        }
    }

    public bool IsAdmin
    {
        get
        {
            var http = accessor.HttpContext!;
            if (http.User.HasClaim("cognito:groups", "organiser") || http.User.HasClaim("cognito:groups", "venue-admin")) return true;
            return env.IsDevelopment() && http.Request.Headers.TryGetValue("X-User-Role", out var role) && role == "admin";
        }
    }
}
