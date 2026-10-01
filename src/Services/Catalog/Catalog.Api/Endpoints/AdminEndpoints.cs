using System.Text.Json;
using BookYourShow.BuildingBlocks.Messaging;
using BookYourShow.BuildingBlocks.Outbox;
using BookYourShow.BuildingBlocks.Security;
using BookYourShow.Contracts;
using Dapper;
using Npgsql;

namespace Catalog.Api.Endpoints;

public sealed record CreateVenueRequest(string Name, string City, string Timezone);
public sealed record CreateHallRequest(string Name, SeatLayout? Layout, GridSpec? Grid);
public sealed record CreateEventRequest(string Title, string Category, string? Language, int DurationMin, string? Description);
public sealed record CreateShowRequest(Guid EventId, Guid HallId, DateTimeOffset StartsAt, DateTimeOffset SaleOpensAt, string Currency, Dictionary<string, long> Prices);

/// <summary>Organiser / venue-admin APIs (admin portal). Writes are low volume; reads are cached downstream.</summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin").WithTags("Admin")
            .AddEndpointFilter(async (ctx, next) =>
                ctx.HttpContext.RequestServices.GetRequiredService<ICurrentUser>().IsAdmin
                    ? await next(ctx)
                    : Results.Problem("Organiser or venue-admin role required.", statusCode: StatusCodes.Status403Forbidden));

        admin.MapPost("/venues", async (CreateVenueRequest body, NpgsqlDataSource db) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.City))
                return Results.Problem("Name and City are required.", statusCode: 400);

            var id = Guid.CreateVersion7();
            await using var conn = await db.OpenConnectionAsync();
            await conn.ExecuteAsync(
                "INSERT INTO venue (venue_id, name, city, timezone) VALUES (@Id, @Name, @City, @Timezone)",
                new { Id = id, body.Name, body.City, Timezone = string.IsNullOrWhiteSpace(body.Timezone) ? "Asia/Kolkata" : body.Timezone });
            return Results.Created($"/admin/venues/{id}", new { venueId = id });
        });

        admin.MapPost("/venues/{venueId:guid}/halls", async (Guid venueId, CreateHallRequest body, NpgsqlDataSource db) =>
        {
            if (body.Grid?.Validate() is { } gridError) return Results.Problem(gridError, statusCode: 400);
            var layout = body.Layout ?? (body.Grid is not null ? SeatLayout.Grid(body.Grid) : null);
            if (layout is null) return Results.Problem("Provide either Layout or Grid.", statusCode: 400);
            if (layout.Validate() is { } layoutError) return Results.Problem(layoutError, statusCode: 400);

            var id = Guid.CreateVersion7();
            await using var conn = await db.OpenConnectionAsync();
            await conn.ExecuteAsync(
                "INSERT INTO hall (hall_id, venue_id, name, layout) VALUES (@Id, @VenueId, @Name, CAST(@Layout AS jsonb))",
                new { Id = id, VenueId = venueId, body.Name, Layout = JsonSerializer.Serialize(layout, MessagingJson.Options) });
            return Results.Created($"/admin/halls/{id}", new { hallId = id, seats = layout.Seats().Count() });
        });

        admin.MapPost("/events", async (CreateEventRequest body, NpgsqlDataSource db) =>
        {
            if (string.IsNullOrWhiteSpace(body.Title) || body.DurationMin <= 0)
                return Results.Problem("Title and a positive DurationMin are required.", statusCode: 400);

            var id = Guid.CreateVersion7();
            await using var conn = await db.OpenConnectionAsync();
            await conn.ExecuteAsync(
                """
                INSERT INTO event (event_id, title, category, language, duration_min, description)
                VALUES (@Id, @Title, @Category, @Language, @DurationMin, @Description)
                """, new { Id = id, body.Title, body.Category, body.Language, body.DurationMin, body.Description });
            return Results.Created($"/events/{id}", new { eventId = id });
        });

        admin.MapPost("/shows", async (CreateShowRequest body, NpgsqlDataSource db) =>
        {
            if (body.Currency?.Length != 3) return Results.Problem("Currency must be an ISO 4217 code.", statusCode: 400);
            if (body.Prices is null || body.Prices.Count == 0 || body.Prices.Values.Any(p => p < 0))
                return Results.Problem("Prices per seat category are required (minor units, >= 0).", statusCode: 400);
            if (body.SaleOpensAt >= body.StartsAt) return Results.Problem("Sale must open before the show starts.", statusCode: 400);

            var id = Guid.CreateVersion7();
            await using var conn = await db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync(
                """
                INSERT INTO show (show_id, event_id, hall_id, starts_at, sale_opens_at, currency, status)
                VALUES (@Id, @EventId, @HallId, @StartsAt, @SaleOpensAt, @Currency, 'DRAFT')
                """,
                new { Id = id, body.EventId, body.HallId, StartsAt = body.StartsAt.UtcDateTime, SaleOpensAt = body.SaleOpensAt.UtcDateTime, Currency = body.Currency.ToUpperInvariant() }, tx);
            foreach (var (category, price) in body.Prices)
                await conn.ExecuteAsync(
                    "INSERT INTO price_tier (show_id, category, price_minor) VALUES (@Id, @Category, @Price)",
                    new { Id = id, Category = category, Price = price }, tx);
            await tx.CommitAsync();
            return Results.Created($"/shows/{id}", new { showId = id, status = "DRAFT" });
        });

        // Publishing opens the show for sale: Seat Inventory receives ShowPublished via the outbox.
        admin.MapPost("/shows/{showId:guid}/publish", async (Guid showId, NpgsqlDataSource db) =>
        {
            await using var conn = await db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            var show = await conn.QuerySingleOrDefaultAsync<ShowForPublish>(
                """
                SELECT s.show_id AS ShowId, s.event_id AS EventId, s.hall_id AS HallId, s.starts_at AS StartsAt,
                       s.sale_opens_at AS SaleOpensAt, s.status AS Status, h.layout::text AS LayoutJson
                FROM show s JOIN hall h ON h.hall_id = s.hall_id
                WHERE s.show_id = @ShowId
                FOR UPDATE OF s
                """, new { ShowId = showId }, tx);
            if (show is null) return Results.NotFound();
            if (show.Status == "PUBLISHED") return Results.Ok(new { showId, status = "PUBLISHED" });
            if (show.Status != "DRAFT") return Results.Problem($"Show is {show.Status}.", statusCode: 409);

            var layout = JsonSerializer.Deserialize<SeatLayout>(show.LayoutJson, MessagingJson.Options)!;
            var priced = (await conn.QueryAsync<string>("SELECT category FROM price_tier WHERE show_id = @ShowId", new { ShowId = showId }, tx)).ToHashSet();
            var missing = layout.Categories().Except(priced).ToList();
            if (missing.Count > 0) return Results.Problem($"No price for categories: {string.Join(", ", missing)}.", statusCode: 422);

            await conn.ExecuteAsync("UPDATE show SET status = 'PUBLISHED' WHERE show_id = @ShowId", new { ShowId = showId }, tx);
            await OutboxWriter.AddAsync(conn, tx, new ShowPublished(
                show.ShowId, show.EventId, show.HallId,
                ToUtc(show.StartsAt), ToUtc(show.SaleOpensAt),
                layout.Seats().ToList()));
            await tx.CommitAsync();
            return Results.Ok(new { showId, status = "PUBLISHED" });
        });

        return app;
    }

    private static DateTimeOffset ToUtc(DateTime dt) => new(DateTime.SpecifyKind(dt, DateTimeKind.Utc));

    private sealed record ShowForPublish(Guid ShowId, Guid EventId, Guid HallId, DateTime StartsAt, DateTime SaleOpensAt, string Status, string LayoutJson);
}
