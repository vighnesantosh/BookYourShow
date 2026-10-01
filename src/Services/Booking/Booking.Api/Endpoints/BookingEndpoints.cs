using Booking.Api.Application;
using BookYourShow.BuildingBlocks.Idempotency;
using BookYourShow.BuildingBlocks.Security;

namespace Booking.Api.Endpoints;

public static class BookingEndpoints
{
    public static IEndpointRouteBuilder MapBookingEndpoints(this IEndpointRouteBuilder app)
    {
        var bookings = app.MapGroup("/bookings").WithTags("Bookings");

        bookings.MapPost("/", async (CreateBookingRequest body, BookingService service, ICurrentUser user, CancellationToken ct) =>
            await service.CreateAsync(user.UserId, body, ct) switch
            {
                ServiceResult<CreateBookingResult>.Ok ok => Results.Created($"/bookings/{ok.Value.Booking.BookingId}", ok.Value),
                ServiceResult<CreateBookingResult>.Error e => Results.Problem(e.Detail, statusCode: e.Status),
                _ => throw new InvalidOperationException(),
            }).RequireIdempotencyKey();

        bookings.MapGet("/{bookingId:guid}", async (Guid bookingId, BookingService service, ICurrentUser user, CancellationToken ct) =>
            await service.GetAsync(bookingId, ct, user.UserId) is { } view
                ? Results.Ok(view)
                : Results.Problem("Booking not found", statusCode: StatusCodes.Status404NotFound));

        bookings.MapPost("/{bookingId:guid}/cancel", async (Guid bookingId, BookingService service, ICurrentUser user, CancellationToken ct) =>
            await service.CancelAsync(user.UserId, bookingId, ct) switch
            {
                ServiceResult<BookingView>.Ok ok => Results.Accepted($"/bookings/{bookingId}", ok.Value),
                ServiceResult<BookingView>.Error e => Results.Problem(e.Detail, statusCode: e.Status),
                _ => throw new InvalidOperationException(),
            }).RequireIdempotencyKey();

        app.MapGet("/me/bookings", async (int? limit, BookingService service, ICurrentUser user, CancellationToken ct) =>
            Results.Ok(await service.ListForUserAsync(user.UserId, limit ?? 20, ct))).WithTags("Bookings");

        return app;
    }
}
