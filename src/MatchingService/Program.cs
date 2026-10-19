using SwiftRide.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSwiftRideTelemetry("SwiftRide.MatchingService");
builder.Services.AddSwiftRideAuthentication(builder.Configuration);

var app = builder.Build();

app.UseHttpsRedirection();
app.UseSwiftRideCorrelationId(); // Trước auth — OnStarting bắt cả 401/403/429
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { service = "MatchingService", status = "Healthy" }))
    .AllowAnonymous();

// Áp dụng kiểm tra quyền động (Dynamic RBAC)
app.MapPost("/match", () => Results.Problem(
    title: "Not implemented",
    detail: "Matching handler has not been implemented.",
    statusCode: StatusCodes.Status501NotImplemented))
    .RequirePermission(PermissionsCatalog.MatchingMatch);

app.MapPost("/quote", () => Results.Problem(
    title: "Not implemented",
    detail: "Pricing handler has not been implemented.",
    statusCode: StatusCodes.Status501NotImplemented))
    .RequirePermission(PermissionsCatalog.MatchingQuote);

app.Run();
