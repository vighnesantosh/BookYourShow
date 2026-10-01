using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace Realtime.Api;

/// <summary>
/// Reads the "seat-deltas" stream written by Seat Inventory and pushes coalesced updates
/// to SignalR groups every CoalesceMilliseconds (design §7). Each gateway node reads the whole
/// stream from "now" (no consumer group): every node must see every delta, because each node
/// holds its own subset of connections. The Valkey backplane fans out between nodes for groups.
/// </summary>
public sealed class SeatDeltaBroadcaster(
    IConnectionMultiplexer redis,
    IHubContext<SeatHub> hub,
    IConfiguration config,
    ILogger<SeatDeltaBroadcaster> logger) : BackgroundService
{
    private const string StreamKey = "seat-deltas";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(config.GetValue("Realtime:CoalesceMilliseconds", 250));
        var db = redis.GetDatabase();
        RedisValue lastId = "$";

        // Resolve "$" to a concrete id so no entry between polls is skipped.
        var latest = await db.StreamRangeAsync(StreamKey, "-", "+", count: 1, messageOrder: Order.Descending);
        lastId = latest.Length > 0 ? latest[0].Id : "0-0";

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var entries = await db.StreamReadAsync(StreamKey, lastId, count: 5000);
                if (entries.Length == 0) continue;
                lastId = entries[^1].Id;

                foreach (var show in entries.Select(Parse).Where(d => d is not null).GroupBy(d => d!.ShowId))
                {
                    var changes = show.Select(d => d!.Change).OrderBy(c => c.Seq).ToList();
                    var message = new SeatsChanged(show.Key, changes[0].Seq, changes[^1].Seq, changes);
                    await hub.Clients.Group(SeatHub.Group(show.Key)).SendAsync("seatsChanged", message, stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Seat delta broadcast failed; continuing from {LastId}", lastId);
            }
        }
    }

    private static Delta? Parse(StreamEntry e)
    {
        var map = e.Values.ToDictionary(v => (string)v.Name!, v => (string)v.Value!);
        return Guid.TryParse(map.GetValueOrDefault("showId"), out var showId) && long.TryParse(map.GetValueOrDefault("seq"), out var seq)
            ? new Delta(showId, new SeatChange(seq, map.GetValueOrDefault("change") ?? "", (map.GetValueOrDefault("seats") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)))
            : null;
    }

    private sealed record Delta(Guid ShowId, SeatChange Change);
}
