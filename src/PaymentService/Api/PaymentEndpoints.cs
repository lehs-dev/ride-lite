using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using PaymentService.Application.DTOs;
using PaymentService.Application.Services;
using PaymentService.Domain;
using SwiftRide.Security;

namespace PaymentService.Api;

public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/payments");

        // CHỈ đẩy tiếp nghĩa vụ đã có của một trip (idempotent). Không nhận Amount ở boundary:
        // giá trị do TripService chốt qua RideCompleted. Retry một payment cụ thể là
        // POST /payments/{id}/retry — tách nghĩa để quyền và ngữ nghĩa từng hành vi rõ ràng.
        group.MapPost("/", async (
            [FromBody] CreatePaymentRequest request,
            ClaimsPrincipal principal,
            IPaymentAppService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetActor(principal, out var actorId, out var isPrivileged))
                return Results.Unauthorized();

            try
            {
                var response = await service.CreatePaymentAsync(
                    request.TripId, request.Method, actorId, isPrivileged, cancellationToken);
                return Results.Ok(response);
            }
            catch (Exception ex) when (MapPaymentError(ex, out var result))
            {
                return result;
            }
        }).RequirePermission(PermissionsCatalog.PaymentsCreate);

        // Retry thủ công một payment đã Failed. Tách khỏi POST /payments để "tạo" và "thử lại"
        // không chung một endpoint, và để kiểm soát được quyền trên từng hành vi.
        group.MapPost("/{id:guid}/retry", async (
            Guid id,
            ClaimsPrincipal principal,
            IPaymentAppService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetActor(principal, out var actorId, out var isPrivileged))
                return Results.Unauthorized();

            try
            {
                var response = await service.RetryPaymentAsync(id, actorId, isPrivileged, cancellationToken);
                return Results.Ok(response);
            }
            catch (Exception ex) when (MapPaymentError(ex, out var result))
            {
                return result;
            }
        }).RequirePermission(PermissionsCatalog.PaymentsCreate);

        // Retry theo trip: rider đang PaymentPending có thể chưa biết payment id (trip chưa
        // gán PaymentId khi Paid chưa tới). Cùng nghiệp vụ với /{id}/retry — TripService gọi
        // đường này qua IPaymentReadModelClient.RetryForTripAsync.
        group.MapPost("/trip/{tripId:guid}/retry", async (
            Guid tripId,
            ClaimsPrincipal principal,
            IPaymentAppService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetActor(principal, out var actorId, out var isPrivileged))
                return Results.Unauthorized();

            try
            {
                var response = await service.RetryPaymentByTripAsync(
                    tripId, actorId, isPrivileged, cancellationToken);
                return Results.Ok(response);
            }
            catch (Exception ex) when (MapPaymentError(ex, out var result))
            {
                return result;
            }
        }).RequirePermission(PermissionsCatalog.PaymentsCreate);

        // Đổi phương thức thanh toán khi nghĩa vụ còn mở (P1-1). Charging giữa chừng -> 409.
        group.MapPatch("/{id:guid}/method", async (
            Guid id,
            [FromBody] UpdatePaymentMethodRequest request,
            ClaimsPrincipal principal,
            IPaymentAppService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetActor(principal, out var actorId, out var isPrivileged))
                return Results.Unauthorized();

            try
            {
                var response = await service.UpdateMethodAsync(
                    id, request.Method, actorId, isPrivileged, cancellationToken);
                return Results.Ok(response);
            }
            catch (Exception ex) when (MapPaymentError(ex, out var result))
            {
                return result;
            }
        }).RequirePermission(PermissionsCatalog.PaymentsCreate);

        // Rider đang PaymentPending chưa biết payment id — tra nghĩa vụ theo trip.
        group.MapGet("/trip/{tripId:guid}", async (
            Guid tripId,
            ClaimsPrincipal principal,
            IPaymentAppService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetActor(principal, out var actorId, out var isPrivileged))
                return Results.Unauthorized();

            try
            {
                var response = await service.GetPaymentByTripAsync(tripId, actorId, isPrivileged, cancellationToken);
                return response is null
                    ? Results.NotFound(new { message = $"Chưa có nghĩa vụ thanh toán cho trip {tripId}." })
                    : Results.Ok(response);
            }
            catch (Exception ex) when (MapPaymentError(ex, out var result))
            {
                return result;
            }
        }).RequirePermission(PermissionsCatalog.PaymentsRead);

        group.MapGet("/{id:guid}", async (
            Guid id,
            ClaimsPrincipal principal,
            IPaymentAppService service,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetActor(principal, out var actorId, out var isPrivileged))
                return Results.Unauthorized();

            try
            {
                var response = await service.GetPaymentAsync(id, actorId, isPrivileged, cancellationToken);
                return response is null
                    ? Results.NotFound(new { message = $"Không tìm thấy payment {id}." })
                    : Results.Ok(response);
            }
            catch (Exception ex) when (MapPaymentError(ex, out var result))
            {
                return result;
            }
        }).RequirePermission(PermissionsCatalog.PaymentsRead);

        group.MapGet("/", async (
            ClaimsPrincipal principal,
            IPaymentAppService service,
            int? page,
            int? pageSize,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetActor(principal, out var actorId, out var isPrivileged))
                return Results.Unauthorized();

            var size = Math.Clamp(pageSize ?? 20, 1, 100);
            var current = Math.Max(page ?? 1, 1);

            // Không có payments.admin thì chỉ thấy giao dịch của chính mình.
            var riderScope = isPrivileged ? (Guid?)null : actorId;
            var items = await service.ListPaymentsAsync(riderScope, current, size, cancellationToken);
            return Results.Ok(items);
        }).RequirePermission(PermissionsCatalog.PaymentsRead);

        return app;
    }

    internal static bool TryGetActor(ClaimsPrincipal principal, out Guid actorId, out bool isPrivileged)
    {
        var value = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? principal.FindFirst("sub")?.Value;
        isPrivileged = CanActOnBehalf(principal);
        return Guid.TryParse(value, out actorId);
    }

    /// <summary>
    /// Admin bypass mọi permission check (xem PermissionAuthorizationHandler) → được thao tác
    /// hộ payment của người khác. Rider thì chỉ được thao tác payment của chính mình.
    /// </summary>
    internal static bool CanActOnBehalf(ClaimsPrincipal principal) =>
        principal.IsInRole("Admin") || principal.IsInRole("admin");

    /// <summary>
    /// Một bảng ánh xạ lỗi cho mọi endpoint payment: 404 không có nghĩa vụ, 403 không thuộc về
    /// mình, 400 sai dữ liệu, 409 xung đột trạng thái.
    /// InvalidPaymentTransitionException suy ra từ InvalidOperationException nên phải bắt trước.
    /// internal để AdminPaymentEndpoints dùng chung một bảng ánh xạ.
    /// </summary>
    internal static bool MapPaymentError(Exception exception, out IResult result)
    {
        switch (exception)
        {
            case KeyNotFoundException notFound:
                result = Results.Problem(title: "Không tìm thấy nghĩa vụ thanh toán", detail: notFound.Message,
                    statusCode: StatusCodes.Status404NotFound);
                return true;

            case UnauthorizedAccessException denied:
                result = Results.Problem(title: "Từ chối truy cập", detail: denied.Message,
                    statusCode: StatusCodes.Status403Forbidden);
                return true;

            case InvalidPaymentTransitionException conflict:
                result = Results.Problem(title: "Xung đột trạng thái", detail: conflict.Message,
                    statusCode: StatusCodes.Status409Conflict);
                return true;

            case ArgumentException badRequest:
                result = Results.Problem(title: "Dữ liệu không hợp lệ", detail: badRequest.Message,
                    statusCode: StatusCodes.Status400BadRequest);
                return true;

            case InvalidOperationException invalid:
                result = Results.Problem(title: "Yêu cầu không hợp lệ", detail: invalid.Message,
                    statusCode: StatusCodes.Status400BadRequest);
                return true;

            default:
                result = null!;
                return false;
        }
    }
}