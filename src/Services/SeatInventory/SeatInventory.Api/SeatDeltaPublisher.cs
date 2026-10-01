using StackExchange.Redis;

namespace SeatInventory.Api;

/// <summary>
/// Appends every seat change to the "seat-deltas" stream. The Realtime gateway reads it,
/// coalesces per show every 250 ms and pushes to SignalR groups (design §7).
/// The stream lives on the realtime Valkey cluster in production; one connection locally.
/// </summary>
public sealed class SeatDeltaPublisher(IConnectionMultiplexer redis, ILogger<SeatDeltaPublisher> logger)
{
    public const string StreamKey = "seat-deltas";

    public async Task PublishAsync(Guid showId, long sequence, string change, IReadOnlyCollection<string> seatIds)
    {
        if (seatIds.Count == 0) return;
        try
        {
            await redis.GetDatabase().StreamAddAsync(StreamKey,
            [
                new NameValueEntry("showId", showId.ToString()),
                new NameValueEntry("seq", sequence),
                new NameValueEntry("change", change),       // held | released | sold | unsold
                new NameValueEntry("seats", string.Join(',', seatIds)),
            ], maxLength: 100_000, useApproximateMaxLength: true);
        }
        catch (Exception ex)
        {
            // A lost delta is self-healing: clients see a sequence gap and re-fetch the snapshot.
            logger.LogWarning(ex, "Could not publish seat delta for show {ShowId}", showId);
        }
    }
}
