using System.Text.Json;
using BookYourShow.BuildingBlocks.Messaging;
using BookYourShow.Contracts;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BookYourShow.BuildingBlocks.Outbox;

/// <summary>
/// Transactional outbox. The event row commits in the same transaction as the state change,
/// so an event is published if and only if the change committed.
/// Table: outbox(id uuid PK, event_type text, payload jsonb, created_at timestamptz, published_at timestamptz NULL).
/// </summary>
public static class OutboxWriter
{
    public static Task AddAsync<TEvent>(NpgsqlConnection conn, NpgsqlTransaction tx, TEvent evt) where TEvent : IIntegrationEvent =>
        conn.ExecuteAsync(
            """
            INSERT INTO outbox (id, event_type, payload, created_at)
            VALUES (@Id, @Type, CAST(@Payload AS jsonb), now())
            """,
            new
            {
                Id = Guid.CreateVersion7(),
                Type = EventTypes.NameOf<TEvent>(),
                Payload = JsonSerializer.Serialize(evt, MessagingJson.Options),
            }, tx);
}

/// <summary>
/// Publishes outbox rows. Several replicas can run it: FOR UPDATE SKIP LOCKED gives each a disjoint batch.
/// Delivery is at-least-once; consumers dedupe on messageId (= outbox id).
/// </summary>
public sealed class OutboxRelay(NpgsqlDataSource db, IEventPublisher publisher, ILogger<OutboxRelay> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(200);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int published;
            try
            {
                published = await PublishBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox relay batch failed");
                published = 0;
            }

            if (published == 0) await Task.Delay(IdleDelay, stoppingToken);
        }
    }

    private async Task<int> PublishBatchAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var rows = (await conn.QueryAsync<OutboxRow>(
            """
            SELECT id AS Id, event_type AS EventType, payload::text AS Payload
            FROM outbox
            WHERE published_at IS NULL
            ORDER BY created_at
            LIMIT 100
            FOR UPDATE SKIP LOCKED
            """, transaction: tx)).AsList();

        foreach (var row in rows)
        {
            await publisher.PublishRawAsync(row.EventType, row.Payload, row.Id.ToString(), ct);
            await conn.ExecuteAsync("UPDATE outbox SET published_at = now() WHERE id = @Id", new { row.Id }, tx);
        }

        await tx.CommitAsync(ct);
        return rows.Count;
    }

    private sealed record OutboxRow(Guid Id, string EventType, string Payload);
}
