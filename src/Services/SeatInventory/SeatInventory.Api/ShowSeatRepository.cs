using BookYourShow.Contracts;
using Dapper;
using Npgsql;

namespace SeatInventory.Api;

/// <summary>Durable seat state in Aurora; the source Valkey is rebuilt from after a failover.</summary>
public sealed class ShowSeatRepository(NpgsqlDataSource db)
{
    public async Task CreateSeatsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid showId, IReadOnlyList<SeatDefinition> seats)
    {
        await conn.ExecuteAsync(
            """
            INSERT INTO show_seat (show_id, seat_id, category, status)
            SELECT @ShowId, s.seat_id, s.category, 'AVAILABLE'
            FROM unnest(@SeatIds, @Categories) AS s(seat_id, category)
            ON CONFLICT (show_id, seat_id) DO NOTHING
            """,
            new { ShowId = showId, SeatIds = seats.Select(s => s.SeatId).ToArray(), Categories = seats.Select(s => s.Category).ToArray() }, tx);
    }

    public Task MarkBookedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid showId, Guid bookingId, IReadOnlyList<string> seatIds) =>
        conn.ExecuteAsync(
            """
            UPDATE show_seat SET status = 'BOOKED', booking_id = @BookingId, version = version + 1, updated_at = now()
            WHERE show_id = @ShowId AND seat_id = ANY(@SeatIds)
            """, new { ShowId = showId, BookingId = bookingId, SeatIds = seatIds.ToArray() }, tx);

    public Task MarkAvailableAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid showId, Guid bookingId, IReadOnlyList<string> seatIds) =>
        conn.ExecuteAsync(
            """
            UPDATE show_seat SET status = 'AVAILABLE', booking_id = NULL, version = version + 1, updated_at = now()
            WHERE show_id = @ShowId AND seat_id = ANY(@SeatIds) AND booking_id = @BookingId
            """, new { ShowId = showId, BookingId = bookingId, SeatIds = seatIds.ToArray() }, tx);

    public async Task<(IReadOnlyList<string> All, IReadOnlyList<string> Booked)> LoadAsync(Guid showId)
    {
        await using var conn = await db.OpenConnectionAsync();
        var rows = (await conn.QueryAsync<(string SeatId, string Status)>(
            "SELECT seat_id, status FROM show_seat WHERE show_id = @ShowId", new { ShowId = showId })).AsList();
        return (rows.Select(r => r.SeatId).ToList(), rows.Where(r => r.Status == "BOOKED").Select(r => r.SeatId).ToList());
    }
}
