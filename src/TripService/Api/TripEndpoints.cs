using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using SwiftRide.Contracts.Events;
using SwiftRide.Security;
using TripService.Application.DTOs;
using TripService.Application.Services;
using TripService.Domain;

namespace TripService.Api;

public static class TripEndpoints
{
    public static IEndpointRouteBuilder MapTripEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/trips");

        group.MapPost("/", async (
            [FromBody] CreateTripRequest request,
            ClaimsPrincipal principal,
            ITripAppService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetActorId(principal, out var riderId))
                return Results.Unauthorized();

            try
            {
                var trip = await service.CreateTripAsync(riderId, request, cancellationToken);
                return Results.Created($"/trips/{trip.Id}", trip);
            }
            catch (ArgumentException ex)
            {
                return Problem400(ex.Message);
            }
        }).RequirePermission(PermissionsCatalog.TripsCreate);

        group.MapPost("/{id:guid}/driver-accept", async (
            Guid id,
            ClaimsPrincipal principal,
            ITripAppService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetActorId(principal, out var driverId))
                return Results.Unauthorized();

            return await RunAsync(() => service.AssignDriverAsync(id, driverId, cancellationToken), "Tài xế đã nhận chuyến");
        }).RequirePermission(PermissionsCatalog.TripsAccept);

        group.MapPost("/{id:guid}/en-route", async (
            Guid id,
            ClaimsPrincipal principal,
            ITripAppService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetActorId(principal, out var driverId))
                return Results.Unauthorized();

            return await RunAsync(() => service.MarkEnRouteAsync(id, driverId, cancellationToken), "Tài xế đang tới đón");
        }).RequirePermission(PermissionsCatalog.TripsUpdateStatus);

        group.MapPost("/{id:guid}/pickup", async (
            Guid id,
            ClaimsPrincipal principal,
            ITripAppService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetActorId(principal, out var driverId))
                return Results.Unauthorized();

            return await RunAsync(() => service.MarkPickedUpAsync(id, driverId, cancellationToken), "Đã đón khách");
        }).RequirePermission(PermissionsCatalog.TripsUpdateStatus);

        // Trả khách = điểm phát động thanh toán. Không có API nào tự đánh dấu Paid.
        // Rider có thể gửi kèm method (P1-1); bỏ trống → mặc định MoMoApp hoặc method đã đặt trước.
        group.MapPost("/{id:guid}/dropoff", async (
            Guid id,
            [FromBody] SetPaymentMethodRequest? request,
            ClaimsPrincipal principal,
            ITripAppService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetActorId(principal, out var driverId))
                return Results.Unauthorized();

            RidePaymentMethod? method = null;
            try
            {
                method = request is null ? null : ParseMethod(request.Method);
            }
            catch (ArgumentException ex)
            {
                return Problem400(ex.Message);
            }

            return await RunAsync(() => service.MarkDroppedOffAsync(id, driverId, method, cancellationToken),
                "Đã trả khách, chờ thanh toán");
        }).RequirePermission(PermissionsCatalog.TripsUpdateStatus);

        // P1-1: rider tự đặt/đổi phương thức thanh toán cho chuyến của mình.
        group.MapPut("/{id:guid}/payment-method", async (
            Guid id,
            [FromBody] SetPaymentMethodRequest request,
            ClaimsPrincipal principal,
            ITripAppService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetActorId(principal, out var riderId))
                return Results.Unauthorized();

            try
            {
                var method = ParseMethod(request.Method);
                return await RunAsync(
                    () => service.SetPaymentMethodAsync(id, riderId, method, cancellationToken),
                    "Đã cập nhật phương thức thanh toán");
            }
            catch (ArgumentException ex)
            {
                return Problem400(ex.Message);
            }
        }).RequirePermission(PermissionsCatalog.PaymentsCreate);

        // P1-1: rider/driver đọc lại phương thức thanh toán đã chọn cho chuyến. Method sống
        // trong intent RideCompleted của outbox, nên đây là read model — không phải cột TripDB.
        group.MapGet("/{id:guid}/payment-method", async (
            Guid id,
            ClaimsPrincipal principal,
            ITripAppService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetActorId(principal, out var actorId))
                return Results.Unauthorized();

            try
            {
                var method = await service.GetPaymentMethodAsync(
                    id, actorId, CanViewAll(principal), cancellationToken);
                return Results.Ok(new { method = method?.ToString() });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(title: "Từ chối truy cập", detail: ex.Message,
                    statusCode: StatusCodes.Status403Forbidden);
            }
        }).RequirePermission(PermissionsCatalog.TripsRead);

        group.MapPost("/{id:guid}/cancel", async (
            Guid id,
            [FromBody] CancelTripRequest? request,
            ClaimsPrincipal principal,
            ITripAppService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetActorId(principal, out var actorId))
                return Results.Unauthorized();

            return await RunAsync(
                () => service.CancelTripAsync(id, actorId, IsRider(principal), request?.Reason, cancellationToken),
                "Chuyến đã hủy");
        }).RequirePermission(PermissionsCatalog.TripsCancel);

        group.MapGet("/{id:guid}", async (
            Guid id,
            ClaimsPrincipal principal,
            ITripAppService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetActorId(principal, out var actorId))
                return Results.Unauthorized();

            try
            {
                var trip = await service.GetTripAsync(id, actorId, CanViewAll(principal), cancellationToken);
                return trip is null ? Results.NotFound(new { message = $"Không tìm thấy trip {id}." }) : Results.Ok(trip);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(title: "Từ chối truy cập", detail: ex.Message,
                    statusCode: StatusCodes.Status403Forbidden);
            }
        }).RequirePermission(PermissionsCatalog.TripsRead);

        // P1-2: rider theo dõi/retry thanh toán. Retry thật gọi sang PaymentService qua
        // IPaymentReadModelClient — trip không tự đổi trạng thái ở đây.
        group.MapPost("/{id:guid}/payment-retry", async (
            Guid id,
            ClaimsPrincipal principal,
            ITripAppService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetActorId(principal, out var actorId))
                return Results.Unauthorized();

            try
            {
                var result = await service.RetryPaymentAsync(id, actorId, IsRider(principal), cancellationToken);
                var payload = new
                {
                    status = result.Status.ToString(),
                    message = result.Message,
                    paymentStatus = result.Trip.PaymentStatus,
                    trip = result.Trip
                };

                // 200 = PaymentService đang charge lại; 202 = yêu cầu hợp lệ nhưng nghĩa vụ vẫn
                // trong vòng retry tự động; 409 = không retry được (trạng thái/权限/từ chối).
                return result.Status switch
                {
                    PaymentRetryStatus.Retrying => Results.Ok(payload),
                    PaymentRetryStatus.AcceptedNoAction => Results.Accepted($"/trips/{id}", payload),
                    _ => Results.Conflict(payload)
                };
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(title: "Từ chối truy cập", detail: ex.Message,
                    statusCode: StatusCodes.Status403Forbidden);
            }
        }).RequirePermission(PermissionsCatalog.PaymentsCreate);

        return app;
    }

    private static async Task<IResult> RunAsync(Func<Task<TripResponse>> action, string title)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Problem(title: "Từ chối truy cập", detail: ex.Message,
                statusCode: StatusCodes.Status403Forbidden);
        }
        catch (InvalidTripTransitionException ex)
        {
            return Results.Problem(title: "Xung đột trạng thái chuyến đi", detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException ex)
        {
            return Problem400(ex.Message);
        }
    }

    private static IResult Problem400(string detail) => Results.Problem(
        title: "Dữ liệu không hợp lệ", detail: detail, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>Client gửi tên enum (MoMoApp | MoMoWallet) — không chấp nhận số để khỏi khớp nhầm enum nội bộ.</summary>
    private static RidePaymentMethod ParseMethod(string? method)
    {
        if (string.IsNullOrWhiteSpace(method))
            throw new ArgumentException("Method là bắt buộc (MoMoApp | MoMoWallet).");

        if (!Enum.TryParse<RidePaymentMethod>(method.Trim(), ignoreCase: false, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            throw new ArgumentException($"Phương thức thanh toán '{method}' không được hỗ trợ.");
        }

        return parsed;
    }

    internal static bool TryGetActorId(ClaimsPrincipal principal, out Guid actorId)
    {
        var value = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? principal.FindFirst("sub")?.Value;
        return Guid.TryParse(value, out actorId);
    }

    private static bool IsRider(ClaimsPrincipal principal) =>
        principal.IsInRole("Rider") || principal.IsInRole("rider");

    private static bool CanViewAll(ClaimsPrincipal principal) =>
        principal.IsInRole("Admin") || principal.IsInRole("admin");
}
