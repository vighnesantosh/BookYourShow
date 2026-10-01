using System.Security.Cryptography;
using System.Text;
using BookYourShow.BuildingBlocks.Security;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BookYourShow.BuildingBlocks.Idempotency;

/// <summary>
/// Endpoint filter for mutating calls. Requires an Idempotency-Key header; the first response
/// for a (user, key) pair is stored and replayed on every retry, so a double-click or a mobile
/// retry never creates a second hold or booking.
/// Table: idempotency_key(user_id, key, request_hash, status_code NULL, response_body, created_at, PRIMARY KEY (user_id, key)).
/// On AWS this table can move to DynamoDB with a 24 h TTL; the contract is the same.
/// </summary>
public sealed class IdempotencyFilter(NpgsqlDataSource db, ICurrentUser user) : IEndpointFilter
{
    public const string HeaderName = "Idempotency-Key";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var key = http.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100)
            return Results.Problem($"{HeaderName} header (max 100 chars) is required.", statusCode: StatusCodes.Status400BadRequest);

        var requestHash = await HashRequestAsync(http);
        await using var conn = await db.OpenConnectionAsync(http.RequestAborted);

        var claimed = await conn.ExecuteAsync(
            """
            INSERT INTO idempotency_key (user_id, key, request_hash, created_at)
            VALUES (@UserId, @Key, @Hash, now())
            ON CONFLICT DO NOTHING
            """, new { user.UserId, Key = key, Hash = requestHash });

        if (claimed == 0)
        {
            var existing = await conn.QuerySingleAsync<StoredResponse>(
                "SELECT request_hash AS RequestHash, status_code AS StatusCode, response_body AS ResponseBody FROM idempotency_key WHERE user_id = @UserId AND key = @Key",
                new { user.UserId, Key = key });

            if (existing.RequestHash != requestHash)
                return Results.Problem("Idempotency-Key was already used with a different request.", statusCode: StatusCodes.Status422UnprocessableEntity);
            if (existing.StatusCode is null)
                return Results.Problem("The original request is still in progress.", statusCode: StatusCodes.Status409Conflict);

            return Results.Text(existing.ResponseBody ?? "", "application/json", Encoding.UTF8, existing.StatusCode);
        }

        object? result;
        try
        {
            result = await next(context);
        }
        catch
        {
            // Release the key so the client can retry a request that crashed.
            await conn.ExecuteAsync("DELETE FROM idempotency_key WHERE user_id = @UserId AND key = @Key", new { user.UserId, Key = key });
            throw;
        }

        if (result is not IResult r) return result;

        // Execute the result into a buffer so the exact response can be stored and replayed.
        var original = http.Response.Body;
        using var buffer = new MemoryStream();
        http.Response.Body = buffer;
        try
        {
            await r.ExecuteAsync(http);
        }
        finally
        {
            http.Response.Body = original;
        }

        var body = Encoding.UTF8.GetString(buffer.ToArray());
        var status = http.Response.StatusCode;

        if (status >= 500)
            await conn.ExecuteAsync("DELETE FROM idempotency_key WHERE user_id = @UserId AND key = @Key", new { user.UserId, Key = key });
        else
            await conn.ExecuteAsync(
                "UPDATE idempotency_key SET status_code = @Status, response_body = @Body WHERE user_id = @UserId AND key = @Key",
                new { Status = status, Body = body, user.UserId, Key = key });

        buffer.Position = 0;
        await buffer.CopyToAsync(original, http.RequestAborted);
        return Results.Empty;
    }

    private static async Task<string> HashRequestAsync(HttpContext http)
    {
        http.Request.EnableBuffering();
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(http.Request.Body, http.RequestAborted);
        http.Request.Body.Position = 0;
        return $"{http.Request.Method} {http.Request.Path}:{Convert.ToHexString(hash)}";
    }

    private sealed record StoredResponse(string RequestHash, int? StatusCode, string? ResponseBody);
}

public static class IdempotencyExtensions
{
    public static TBuilder RequireIdempotencyKey<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter<TBuilder, IdempotencyFilter>();

    public static IServiceCollection AddIdempotency(this IServiceCollection services) =>
        services.AddScoped<IdempotencyFilter>();
}
