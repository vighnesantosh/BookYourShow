using Booking.Api.Domain;
using Xunit;

namespace Booking.Tests;

public sealed class BookingDomainTests
{
    [Theory]
    [InlineData(BookingStatus.PendingPayment, BookingStatus.Confirmed, true)]
    [InlineData(BookingStatus.PendingPayment, BookingStatus.Failed, true)]
    [InlineData(BookingStatus.PendingPayment, BookingStatus.Expired, true)]
    [InlineData(BookingStatus.Expired, BookingStatus.Confirmed, true)]      // late payment, seats still free
    [InlineData(BookingStatus.Confirmed, BookingStatus.Cancelled, true)]
    [InlineData(BookingStatus.Confirmed, BookingStatus.Failed, false)]
    [InlineData(BookingStatus.Failed, BookingStatus.Confirmed, false)]
    [InlineData(BookingStatus.Cancelled, BookingStatus.Confirmed, false)]
    public void Transitions_follow_the_saga(string from, string to, bool allowed) =>
        Assert.Equal(allowed, BookingStatus.CanTransition(from, to));

    [Fact]
    public void Prices_each_seat_by_its_category()
    {
        var (lines, total) = Pricing.PriceSeats(
            ["A1", "C4"],
            new Dictionary<string, string> { ["A1"] = "RECLINER", ["C4"] = "GOLD" },
            new Dictionary<string, long> { ["RECLINER"] = 90_000, ["GOLD"] = 50_000 });

        Assert.Equal(140_000, total);
        Assert.Equal("RECLINER", lines.Single(l => l.SeatId == "A1").Category);
    }

    [Fact]
    public void Pricing_rejects_a_seat_outside_the_layout() =>
        Assert.Throws<InvalidOperationException>(() => Pricing.PriceSeats(
            ["Z1"], new Dictionary<string, string>(), new Dictionary<string, long>()));
}
