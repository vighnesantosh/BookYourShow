namespace Booking.Api.Domain;

/// <summary>
/// Booking lifecycle (design §6):
/// PENDING_PAYMENT → CONFIRMED → CANCELLED, with PENDING_PAYMENT → FAILED | EXPIRED as the exits.
/// EXPIRED → CONFIRMED is allowed: a payment that lands after the hold timed out still wins
/// if the seats are free (otherwise → FAILED and refund).
/// </summary>
public static class BookingStatus
{
    public const string PendingPayment = "PENDING_PAYMENT";
    public const string Confirmed = "CONFIRMED";
    public const string Failed = "FAILED";
    public const string Expired = "EXPIRED";
    public const string Cancelled = "CANCELLED";

    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        [PendingPayment] = [Confirmed, Failed, Expired],
        [Expired] = [Confirmed, Failed],
        [Confirmed] = [Cancelled],
        [Failed] = [],
        [Cancelled] = [],
    };

    public static bool CanTransition(string from, string to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static void EnsureTransition(string from, string to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException($"Booking cannot move from {from} to {to}.");
    }

    /// <summary>States in which a successful payment should try to confirm the sale.</summary>
    public static bool AcceptsPayment(string status) => status is PendingPayment or Expired;
}

public sealed record BookingLine(string SeatId, string Category, long PriceMinor);

public static class Pricing
{
    /// <summary>Prices each held seat from the show's tiers. Throws if a seat or category is unknown.</summary>
    public static (IReadOnlyList<BookingLine> Lines, long TotalMinor) PriceSeats(
        IReadOnlyList<string> seatIds,
        IReadOnlyDictionary<string, string> categoryBySeat,
        IReadOnlyDictionary<string, long> priceByCategory)
    {
        var lines = seatIds.Select(seat =>
        {
            if (!categoryBySeat.TryGetValue(seat, out var category))
                throw new InvalidOperationException($"Seat {seat} is not in the layout.");
            if (!priceByCategory.TryGetValue(category, out var price))
                throw new InvalidOperationException($"No price for category {category}.");
            return new BookingLine(seat, category, price);
        }).ToList();

        return (lines, lines.Sum(l => l.PriceMinor));
    }
}
