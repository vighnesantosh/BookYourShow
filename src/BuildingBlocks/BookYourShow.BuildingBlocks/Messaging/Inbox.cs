using Dapper;
using Npgsql;

namespace BookYourShow.BuildingBlocks.Messaging;

/// <summary>
/// Idempotent-consumer guard. Call inside the handler's own transaction:
/// returns false when this message was already processed, so the handler can skip it.
/// Table: processed_message(message_id text, consumer text, processed_at timestamptz, PRIMARY KEY (message_id, consumer)).
/// </summary>
public static class Inbox
{
    public static async Task<bool> TryMarkProcessedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, MessageContext ctx, string consumer)
    {
        var inserted = await conn.ExecuteAsync(
            """
            INSERT INTO processed_message (message_id, consumer, processed_at)
            VALUES (@MessageId, @Consumer, now())
            ON CONFLICT DO NOTHING
            """,
            new { ctx.MessageId, Consumer = consumer }, tx);
        return inserted == 1;
    }
}
