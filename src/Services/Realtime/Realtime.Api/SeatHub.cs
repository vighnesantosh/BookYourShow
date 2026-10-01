using Microsoft.AspNetCore.SignalR;

namespace Realtime.Api;

/// <summary>Client-to-server calls. Server pushes "seatsChanged" to group show:{showId}.</summary>
public sealed class SeatHub : Hub
{
    public static string Group(Guid showId) => $"show:{showId}";

    public Task JoinShow(Guid showId) => Groups.AddToGroupAsync(Context.ConnectionId, Group(showId));

    public Task LeaveShow(Guid showId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(showId));
}

/// <summary>One coalesced update for a show. Clients apply changes in order and re-fetch the snapshot on a sequence gap.</summary>
public sealed record SeatsChanged(Guid ShowId, long FromSeq, long ToSeq, IReadOnlyList<SeatChange> Changes);

public sealed record SeatChange(long Seq, string Change, IReadOnlyList<string> Seats);
