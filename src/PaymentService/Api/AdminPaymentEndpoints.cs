using Microsoft.AspNetCore.Mvc;
using PaymentService.Application.Services;
using SwiftRide.Security;

namespace PaymentService.Api;

public static class AdminPaymentEndpoints
{
    public static IEndpointRouteBuilder MapAdminPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/payments");

        // Audit thật từ PayDB thay cho dữ liệu mock trước đây. PagedResult kèm TotalCount
        // để frontend hiển thị số trang; lọc thêm theo trip/rider cho đối soát.
        // from/to so sánh trực tiếp nên phải về cùng một hệ quy chiếu: UTC (Kind=Utc) hoặc
        // cả hai Unspecified — query string bind ra Unspecified, không ai tự đổi giờ giúp.
        group.MapGet("/", async (
            string? status,
            DateTime? from,
            DateTime? to,
            Guid? tripId,
            Guid? riderId,
            int? page,
            int? pageSize,
            IPaymentAppService service,
            CancellationToken cancellationToken) =>
        {
            if (from is not null && to is not null && Normalize(from.Value) > Normalize(to.Value))
                return InvalidRange();

            try
            {
                var current = Math.Max(page ?? 1, 1);
                var size = Math.Clamp(pageSize ?? 20, 1, 100);

                var rows = await service.QueryForAdminAsync(
                    status, from, to, tripId, riderId, current, size, cancellationToken);
                return Results.Ok(rows);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(title: "Dữ liệu không hợp lệ", detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }).RequireAuthorization(policy => policy
            .RequireRole("Admin", "admin")
            .AddRequirements(new PermissionRequirement(PermissionsCatalog.PaymentsAdmin)));

        // Chi tiết một payment — admin đối soát từng giao dịch (P1-2).
        group.MapGet("/{id:guid}", async (
            Guid id,
            IPaymentAppService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var payment = await service.GetPaymentForAdminAsync(id, cancellationToken);
                return payment is null
                    ? Results.NotFound(new { message = $"Không tìm thấy payment {id}." })
                    : Results.Ok(payment);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(title: "Dữ liệu không hợp lệ", detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }).RequireAuthorization(policy => policy
            .RequireRole("Admin", "admin")
            .AddRequirements(new PermissionRequirement(PermissionsCatalog.PaymentsAdmin)));

        // Admin retry hộ một payment đã Failed. isPrivileged=true nên bỏ qua kiểm tra chủ sở hữu;
        // dùng chung bảng ánh xạ lỗi với endpoint rider (404/409/400).
        group.MapPost("/{id:guid}/retry", async (
            Guid id,
            IPaymentAppService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var payment = await service.RetryPaymentAsync(
                    id, actorId: Guid.Empty, isPrivileged: true, cancellationToken);
                return Results.Ok(payment);
            }
            catch (Exception ex) when (PaymentEndpoints.MapPaymentError(ex, out var result))
            {
                return result;
            }
        }).RequireAuthorization(policy => policy
            .RequireRole("Admin", "admin")
            .AddRequirements(new PermissionRequirement(PermissionsCatalog.PaymentsAdmin)));

        return app;
    }

    /// <summary>
    /// ToUniversalTime chỉ đổi hệ quy chiếu khi Kind đã là Utc, nên Unspecified được coi thẳng
    /// là UTC: hai giá trị từ query string luôn so sánh được trên cùng một trục thời gian.
    /// </summary>
    private static DateTime Normalize(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static IResult InvalidRange() =>
        Results.Problem(title: "Dữ liệu không hợp lệ",
            detail: "Khoảng thời gian không hợp lệ: 'from' phải <= 'to'.",
            statusCode: StatusCodes.Status400BadRequest);
}
