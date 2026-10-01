using System.Text.Json;
using BookYourShow.BuildingBlocks.Messaging;
using BookYourShow.Contracts;
using Dapper;
using Npgsql;

namespace Catalog.Api.Endpoints;

public sealed record EventSummary(Guid EventId, string Title, string Category, string? Language, int DurationMin);
public sealed record ShowSummary(Guid ShowId, Guid EventId, DateTime StartsAt, DateTime SaleOpensAt, string Currency, string Status, string VenueName, string City, string HallName);
public sealed record PriceRow(string Category, long PriceMinor);

/// <summary>
/// Browse and show-detail reads. In production, list/search moves to the Search service (OpenSearch)
/// and these responses are cached at CloudFront; Postgres is enough to run the system end to end.
/// </summary>
public static class PublicEndpoints
{
    public static IEndpointRouteBuilder MapPublicEndpoints(this IEndpointRouteBuilder app)
    {
        var events = app.MapGroup("/events").WithTags("Browse");

        events.MapGet("/", async (string? city, string? q, int? page, int? pageSize, NpgsqlDataSource db) =>
        {
            var size = Math.Clamp(pageSize ?? 20, 1, 100);
            var offset = (Math.Max(page ?? 1, 1) - 1) * size;
            await using var conn = await db.OpenConnectionAsync();
            var rows = await conn.QueryAsync<EventSummary>(
                """
                SELECT DISTINCT e.event_id AS EventId, e.title AS Title, e.category AS Category,
                       e.language AS Language, e.duration_min AS DurationMin
                FROM event e
                JOIN show s  ON s.event_id = e.event_id AND s.status = 'PUBLISHED' AND s.starts_at > now()
                JOIN hall h  ON h.hall_id = s.hall_id
                JOIN venue v ON v.venue_id = h.venue_id
                WHERE (@City::text IS NULL OR v.city ILIKE @City)
                  AND (@Q::text IS NULL OR e.title ILIKE '%' || @Q || '%')
                ORDER BY e.title
                LIMIT @Size OFFSET @Offset
                """, new { City = city, Q = q, Size = size, Offset = offset });
            return Results.Ok(rows);
        });

        events.MapGet("/{eventId:guid}", async (Guid eventId, NpgsqlDataSource db) =>
        {
            await using var conn = await db.OpenConnectionAsync();
            var evt = await conn.QuerySingleOrDefaultAsync<EventSummary>(
                "SELECT event_id AS EventId, title AS Title, category AS Category, language AS Language, duration_min AS DurationMin FROM event WHERE event_id = @EventId",
                new { EventId = eventId });
            if (evt is null) return Results.Problem("Event not found", statusCode: 404);

            var shows = await conn.QueryAsync<ShowSummary>(ShowSql + " WHERE s.event_id = @EventId AND s.status = 'PUBLISHED' ORDER BY s.starts_at", new { EventId = eventId });
            return Results.Ok(new { evt, shows });
        });

        var shows = app.MapGroup("/shows/{showId:guid}").WithTags("Browse");

        shows.MapGet("/", async (Guid showId, NpgsqlDataSource db) =>
        {
            await using var conn = await db.OpenConnectionAsync();
            var show = await conn.QuerySingleOrDefaultAsync<ShowSummary>(ShowSql + " WHERE s.show_id = @ShowId", new { ShowId = showId });
            if (show is null) return Results.NotFound();
            var prices = await conn.QueryAsync<PriceRow>(
                "SELECT category AS Category, price_minor AS PriceMinor FROM price_tier WHERE show_id = @ShowId ORDER BY price_minor DESC",
                new { ShowId = showId });
            return Results.Ok(new { show, prices });
        });

        // Static per layout version: S3 + CloudFront with a long cache in production.
        shows.MapGet("/layout", async (Guid showId, NpgsqlDataSource db, HttpResponse response) =>
        {
            await using var conn = await db.OpenConnectionAsync();
            var json = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT h.layout::text FROM show s JOIN hall h ON h.hall_id = s.hall_id WHERE s.show_id = @ShowId", new { ShowId = showId });
            if (json is null) return Results.NotFound();
            response.Headers.CacheControl = "public, max-age=3600";
            return Results.Text(json, "application/json");
        });

        // ---------- internal: Booking prices a hold from here ----------
        app.MapGet("/internal/shows/{showId:guid}/pricing", async (Guid showId, NpgsqlDataSource db) =>
        {
            await using var conn = await db.OpenConnectionAsync();
            var row = await conn.QuerySingleOrDefaultAsync<(string Currency, string LayoutJson)>(
                "SELECT s.currency, h.layout::text FROM show s JOIN hall h ON h.hall_id = s.hall_id WHERE s.show_id = @ShowId AND s.status = 'PUBLISHED'",
                new { ShowId = showId });
            if (row.LayoutJson is null) return Results.NotFound();

            var prices = (await conn.QueryAsync<PriceRow>(
                "SELECT category AS Category, price_minor AS PriceMinor FROM price_tier WHERE show_id = @ShowId", new { ShowId = showId }))
                .ToDictionary(p => p.Category, p => p.PriceMinor);
            var layout = JsonSerializer.Deserialize<SeatLayout>(row.LayoutJson, MessagingJson.Options)!;

            return Results.Ok(new ShowPricing(showId, row.Currency.Trim(), prices,
                layout.Seats().ToDictionary(s => s.SeatId, s => s.Category)));
        }).WithTags("Internal");

        return app;
    }

    private const string ShowSql =
        """
        SELECT s.show_id AS ShowId, s.event_id AS EventId, s.starts_at AS StartsAt, s.sale_opens_at AS SaleOpensAt,
               s.currency AS Currency, s.status AS Status, v.name AS VenueName, v.city AS City, h.name AS HallName
        FROM show s
        JOIN hall h  ON h.hall_id = s.hall_id
        JOIN venue v ON v.venue_id = h.venue_id
        """;
}
