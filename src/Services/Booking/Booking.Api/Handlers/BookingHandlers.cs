using Booking.Api.Application;
using BookYourShow.BuildingBlocks.Messaging;
using BookYourShow.Contracts;

namespace Booking.Api.Handlers;

public sealed class PaymentSucceededHandler(BookingSaga saga) : IIntegrationEventHandler<PaymentSucceeded>
{
    public Task HandleAsync(PaymentSucceeded evt, MessageContext context, CancellationToken ct) =>
        saga.ConfirmAsync(evt.BookingId, evt.PaymentId, evt.AmountMinor, ct);
}

public sealed class PaymentFailedHandler(BookingSaga saga) : IIntegrationEventHandler<PaymentFailed>
{
    public Task HandleAsync(PaymentFailed evt, MessageContext context, CancellationToken ct) =>
        saga.FailAsync(evt.BookingId, $"Payment failed: {evt.Reason}", ct);
}

public sealed class HoldExpiredHandler(BookingSaga saga) : IIntegrationEventHandler<HoldExpired>
{
    public Task HandleAsync(HoldExpired evt, MessageContext context, CancellationToken ct) =>
        saga.ExpireByHoldAsync(evt.HoldId, ct);
}
