using BookYourShow.BuildingBlocks.Messaging;
using BookYourShow.Contracts;
using Payment.Api.Application;

namespace Payment.Api.Handlers;

/// <summary>Booking failed: refund if money was captured, otherwise stop the open intent.</summary>
public sealed class BookingFailedHandler(PaymentProcessor processor) : IIntegrationEventHandler<BookingFailed>
{
    public Task HandleAsync(BookingFailed evt, MessageContext context, CancellationToken ct) =>
        evt.RefundRequired ? processor.RefundAsync(evt.BookingId, null, ct) : processor.CancelAsync(evt.BookingId, ct);
}

public sealed class BookingExpiredHandler(PaymentProcessor processor) : IIntegrationEventHandler<BookingExpired>
{
    public Task HandleAsync(BookingExpired evt, MessageContext context, CancellationToken ct) =>
        processor.CancelAsync(evt.BookingId, ct);
}

public sealed class BookingCancelledHandler(PaymentProcessor processor) : IIntegrationEventHandler<BookingCancelled>
{
    public Task HandleAsync(BookingCancelled evt, MessageContext context, CancellationToken ct) =>
        processor.RefundAsync(evt.BookingId, evt.AmountMinor, ct);
}
