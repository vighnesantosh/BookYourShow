using System.Net;
using BookYourShow.Contracts;

namespace Booking.Api.Clients;

// Typed HTTP clients for the synchronous hot path. Each is wrapped by the standard
// resilience pipeline (retry with jitter, timeout, circuit breaker) in Program.cs.

public sealed class InventoryClient(HttpClient http)
{
    /// <summary>Returns the hold if it is active, null if it is gone or expired.</summary>
    public async Task<HoldView?> GetActiveHoldAsync(Guid showId, Guid holdId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"/internal/shows/{showId}/holds/{holdId}", ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HoldView>(ct);
    }

    /// <summary>Extends the hold for the payment window; false if the hold is no longer active.</summary>
    public async Task<bool> ExtendHoldAsync(Guid showId, Guid holdId, string userId, DateTimeOffset expiresAt, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"/internal/shows/{showId}/holds/{holdId}/extend", new { userId, expiresAt }, ct);
        if (response.StatusCode == HttpStatusCode.Gone) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }
}

public sealed class CatalogClient(HttpClient http)
{
    public async Task<ShowPricing?> GetPricingAsync(Guid showId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"/internal/shows/{showId}/pricing", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ShowPricing>(ct);
    }
}

public sealed class PaymentClient(HttpClient http)
{
    /// <summary>Idempotent: Payment keys the intent on bookingId, so a retry returns the same intent.</summary>
    public async Task<CreatePaymentResponse> CreateAsync(CreatePaymentRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("/internal/payments", request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreatePaymentResponse>(ct))!;
    }

    public async Task<PaymentStatusView?> GetByBookingAsync(Guid bookingId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"/internal/payments/by-booking/{bookingId}?refresh=true", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PaymentStatusView>(ct);
    }
}
