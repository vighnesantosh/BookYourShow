using Booking.Api.Clients;
using Dapper;
using Npgsql;

namespace Booking.Api.Application;

/// <summary>
/// Safety net for lost or late webhooks (design §6): bookings still PENDING_PAYMENT after
/// N seconds are checked against the gateway via Payment, and driven to their final state.
/// </summary>
public sealed class ReconciliationWorker(
    NpgsqlDataSource db,
    IServiceScopeFactory scopes,
    IConfiguration config,
    ILogger<ReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pendingAfter = TimeSpan.FromSeconds(config.GetValue("Reconciliation:PendingAfterSeconds", 120));
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(config.GetValue("Reconciliation:IntervalSeconds", 30)));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ReconcileAsync(pendingAfter, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Reconciliation pass failed");
            }
        }
    }

    private async Task ReconcileAsync(TimeSpan pendingAfter, CancellationToken ct)
    {
        IReadOnlyList<Guid> stale;
        await using (var conn = await db.OpenConnectionAsync(ct))
        {
            stale = (await conn.QueryAsync<Guid>(
                """
                SELECT booking_id FROM booking
                WHERE status = 'PENDING_PAYMENT' AND payment_id IS NOT NULL
                  AND created_at < now() - make_interval(secs => @Secs)
                ORDER BY created_at
                LIMIT 100
                """, new { Secs = pendingAfter.TotalSeconds })).AsList();
        }

        if (stale.Count == 0) return;

        await using var scope = scopes.CreateAsyncScope();
        var payments = scope.ServiceProvider.GetRequiredService<PaymentClient>();
        var saga = scope.ServiceProvider.GetRequiredService<BookingSaga>();

        foreach (var bookingId in stale)
        {
            var status = await payments.GetByBookingAsync(bookingId, ct);
            switch (status?.Status)
            {
                case "SUCCEEDED":
                    logger.LogInformation("Reconciled booking {BookingId}: payment succeeded without a webhook", bookingId);
                    await saga.ConfirmAsync(bookingId, status.PaymentId, status.AmountMinor, ct);
                    break;
                case "FAILED" or "CANCELLED":
                    await saga.FailAsync(bookingId, $"Payment {status.Status.ToLowerInvariant()} (reconciled)", ct);
                    break;
                // Still REQUIRES_ACTION: the hold-expiry path will close it.
            }
        }
    }
}
