using BookYourShow.BuildingBlocks.Outbox;
using BookYourShow.Contracts;
using Dapper;
using Npgsql;
using Payment.Api.Gateways;

namespace Payment.Api.Application;

public static class PaymentStatus
{
    public const string RequiresAction = "REQUIRES_ACTION";
    public const string Succeeded = "SUCCEEDED";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";
    public const string Refunded = "REFUNDED";
}

internal sealed record PaymentRow(
    Guid PaymentId, Guid BookingId, string UserId, string Gateway, string GatewayRef, string ClientSecret,
    string Status, long AmountMinor, string Currency);

public sealed class PaymentProcessor(NpgsqlDataSource db, IPaymentGateway gateway, ILogger<PaymentProcessor> logger)
{
    private const string SelectPayment =
        """
        SELECT payment_id AS PaymentId, booking_id AS BookingId, user_id AS UserId, gateway AS Gateway,
               gateway_ref AS GatewayRef, client_secret AS ClientSecret, status AS Status,
               amount_minor AS AmountMinor, currency AS Currency
        FROM payment
        """;

    /// <summary>One intent per booking: a retry from Booking returns the existing intent.</summary>
    public async Task<CreatePaymentResponse> CreateAsync(CreatePaymentRequest request, CancellationToken ct)
    {
        var existing = await FindByBookingAsync(request.BookingId, ct);
        if (existing is not null) return ToResponse(existing);

        var paymentId = Guid.CreateVersion7();
        var intent = await gateway.CreateIntentAsync(paymentId, request.AmountMinor, request.Currency, request.BookingId.ToString("N"), ct);

        await using var conn = await db.OpenConnectionAsync(ct);
        var inserted = await conn.ExecuteAsync(
            """
            INSERT INTO payment (payment_id, booking_id, user_id, gateway, gateway_ref, client_secret, status, amount_minor, currency)
            VALUES (@PaymentId, @BookingId, @UserId, @Gateway, @GatewayRef, @ClientSecret, 'REQUIRES_ACTION', @Amount, @Currency)
            ON CONFLICT (booking_id) DO NOTHING
            """,
            new
            {
                PaymentId = paymentId, request.BookingId, request.UserId, Gateway = gateway.Name,
                intent.GatewayRef, intent.ClientSecret, Amount = request.AmountMinor, request.Currency,
            });

        return inserted == 1
            ? new CreatePaymentResponse(paymentId, gateway.Name, intent.GatewayRef, intent.ClientSecret, PaymentStatus.RequiresAction)
            : ToResponse((await FindByBookingAsync(request.BookingId, ct))!);   // lost a concurrent create
    }

    /// <summary>
    /// Applies a verified gateway event. Exactly-once per gateway event id (payment_event PK);
    /// the payment row lock serializes a webhook racing with reconciliation.
    /// </summary>
    public async Task<bool> ApplyGatewayEventAsync(GatewayWebhook webhook, string rawPayload, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var payment = await conn.QuerySingleOrDefaultAsync<PaymentRow>(SelectPayment + " WHERE gateway_ref = @Ref FOR UPDATE", new { Ref = webhook.GatewayRef }, tx);
        if (payment is null)
        {
            logger.LogWarning("Webhook {EventId} for unknown gateway ref {Ref}", webhook.EventId, webhook.GatewayRef);
            return false;
        }

        var fresh = await conn.ExecuteAsync(
            """
            INSERT INTO payment_event (gateway_event_id, payment_id, type, payload)
            VALUES (@EventId, @PaymentId, @Type, CAST(@Payload AS jsonb))
            ON CONFLICT (gateway_event_id) DO NOTHING
            """, new { webhook.EventId, payment.PaymentId, Type = webhook.RawType, Payload = rawPayload }, tx);
        if (fresh == 0) return true;   // duplicate delivery

        switch (webhook.Status)
        {
            // Success is accepted even after we cancelled the intent: Booking then confirms or refunds.
            case GatewayStatus.Succeeded when payment.Status is PaymentStatus.RequiresAction or PaymentStatus.Cancelled:
                await SetStatusAsync(conn, tx, payment.PaymentId, PaymentStatus.Succeeded);
                await OutboxWriter.AddAsync(conn, tx, new PaymentSucceeded(payment.PaymentId, payment.BookingId, webhook.AmountMinor, payment.Currency.Trim(), payment.GatewayRef));
                break;

            case GatewayStatus.Failed when payment.Status == PaymentStatus.RequiresAction:
                await SetStatusAsync(conn, tx, payment.PaymentId, PaymentStatus.Failed);
                await OutboxWriter.AddAsync(conn, tx, new PaymentFailed(payment.PaymentId, payment.BookingId, webhook.RawType));
                break;

            default:
                logger.LogInformation("Ignoring {Type} for payment {PaymentId} in status {Status}", webhook.RawType, payment.PaymentId, payment.Status);
                break;
        }

        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>Reconciliation: ask the gateway directly when a webhook may have been lost.</summary>
    public async Task<PaymentStatusView?> GetByBookingAsync(Guid bookingId, bool refresh, CancellationToken ct)
    {
        var payment = await FindByBookingAsync(bookingId, ct);
        if (payment is null) return null;

        if (refresh && payment.Status == PaymentStatus.RequiresAction)
        {
            var status = await gateway.GetStatusAsync(payment.GatewayRef, ct);
            if (status is GatewayStatus.Succeeded or GatewayStatus.Failed)
            {
                var synthetic = new GatewayWebhook($"poll:{payment.GatewayRef}:{status}", payment.GatewayRef, status, payment.AmountMinor, $"poll.{status}".ToLowerInvariant());
                await ApplyGatewayEventAsync(synthetic, "{}", ct);
                payment = (await FindByBookingAsync(bookingId, ct))!;
            }
        }

        return new PaymentStatusView(payment.PaymentId, payment.BookingId, payment.Status, payment.AmountMinor, payment.Currency.Trim());
    }

    /// <summary>Refund a captured payment (late payment, lost race, or customer cancellation).</summary>
    public async Task RefundAsync(Guid bookingId, long? amountMinor, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var payment = await conn.QuerySingleOrDefaultAsync<PaymentRow>(SelectPayment + " WHERE booking_id = @BookingId FOR UPDATE", new { BookingId = bookingId }, tx);
        if (payment is null || payment.Status != PaymentStatus.Succeeded) return;   // nothing captured, or already refunded

        var amount = amountMinor ?? payment.AmountMinor;
        // Gateway refund is idempotent on the key, so a crash after this call and a redelivery is safe.
        await gateway.RefundAsync(payment.GatewayRef, amount, $"refund-{payment.PaymentId:N}", ct);
        await SetStatusAsync(conn, tx, payment.PaymentId, PaymentStatus.Refunded);
        await OutboxWriter.AddAsync(conn, tx, new RefundCompleted(payment.PaymentId, payment.BookingId, amount));
        await tx.CommitAsync(ct);
    }

    /// <summary>Booking ended without payment: stop the customer from paying for it.</summary>
    public async Task CancelAsync(Guid bookingId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var payment = await conn.QuerySingleOrDefaultAsync<PaymentRow>(SelectPayment + " WHERE booking_id = @BookingId FOR UPDATE", new { BookingId = bookingId }, tx);
        if (payment is null || payment.Status != PaymentStatus.RequiresAction) return;

        await gateway.CancelAsync(payment.GatewayRef, ct);
        await SetStatusAsync(conn, tx, payment.PaymentId, PaymentStatus.Cancelled);
        await tx.CommitAsync(ct);
    }

    internal async Task<PaymentRow?> FindAsync(Guid paymentId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<PaymentRow>(SelectPayment + " WHERE payment_id = @Id", new { Id = paymentId });
    }

    private async Task<PaymentRow?> FindByBookingAsync(Guid bookingId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<PaymentRow>(SelectPayment + " WHERE booking_id = @BookingId", new { BookingId = bookingId });
    }

    private static Task SetStatusAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid paymentId, string status) =>
        conn.ExecuteAsync("UPDATE payment SET status = @Status, updated_at = now() WHERE payment_id = @Id", new { Id = paymentId, Status = status }, tx);

    private static CreatePaymentResponse ToResponse(PaymentRow p) => new(p.PaymentId, p.Gateway, p.GatewayRef, p.ClientSecret, p.Status);
}
