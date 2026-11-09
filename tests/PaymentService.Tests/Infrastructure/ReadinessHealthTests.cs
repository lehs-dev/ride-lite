using System.Data.Common;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PaymentService.Infrastructure.Persistence;
using SwiftRide.Security;
using Xunit;

namespace PaymentService.Tests.Infrastructure;

/// <summary>
/// P1-3: readiness phải phản ánh dependency thật — mã HTTP đúng và body liệt kê check nào đỏ,
/// không chỉ trả "Degraded" 200. Pure mapping + writer, không cần DB/container/TestHost.
/// </summary>
public class ReadinessHealthTests
{
    [Theory]
    [InlineData(HealthStatus.Healthy, HttpStatusCode.OK)]
    [InlineData(HealthStatus.Degraded, HttpStatusCode.OK)]
    [InlineData(HealthStatus.Unhealthy, HttpStatusCode.ServiceUnavailable)]
    public void StatusCodeMapping_DependencyFaultIs503(HealthStatus status, HttpStatusCode expected) =>
        Assert.Equal(expected, (HttpStatusCode)SwiftRideHealthReportWriter.GetStatusCode(status));

    private static HealthReportEntry Entry(
        HealthStatus status, string? description, TimeSpan duration) =>
        new(status, description, duration, exception: null, data: null);

    [Fact]
    public async Task Writer_ExposesEveryCheckAndItsStatus()
    {
        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["paydb"] = Entry(HealthStatus.Unhealthy,
                    "Không kết nối được PostgreSQL PayDB.", TimeSpan.FromMilliseconds(7)),
                ["rabbitmq"] = Entry(HealthStatus.Healthy, null, TimeSpan.FromMilliseconds(2)),
            },
            TimeSpan.FromMilliseconds(9));

        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };
        await using var stream = new MemoryStream();
        context.Response.Body = stream;

        await SwiftRideHealthReportWriter.WriteAsync(context, report);
        stream.Position = 0;

        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;

        // Report tổng hợp từ một check Unhealthy phải là Unhealthy → 503 trong payload.
        Assert.Equal("Unhealthy", root.GetProperty("status").GetString());
        Assert.Equal(503, root.GetProperty("statusCode").GetInt32());

        var checks = root.GetProperty("checks").EnumerateArray().ToDictionary(
            c => c.GetProperty("name").GetString()!,
            c => c);

        Assert.Contains("paydb", checks.Keys);
        Assert.Equal("Unhealthy", checks["paydb"].GetProperty("status").GetString());
        Assert.Contains("PostgreSQL", checks["paydb"].GetProperty("description").GetString());
        Assert.Equal("Healthy", checks["rabbitmq"].GetProperty("status").GetString());
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
    }

    [Fact]
    public void AggregateReportWithUnhealthyDependencyIsNotReady()
    {
        // Chứng minh truthiness thật: một dependency đỏ kéo cả report xuống Unhealthy (→ 503),
        // kể khi dependency kia chỉ Degraded.
        var entries = new Dictionary<string, HealthReportEntry>
        {
            ["rabbitmq"] = Entry(HealthStatus.Degraded, "đang reconnect", TimeSpan.Zero),
        };
        var withoutFault = new HealthReport(entries, TimeSpan.Zero);
        Assert.Equal(HealthStatus.Degraded, withoutFault.Status);
        Assert.Equal((int)HttpStatusCode.OK, SwiftRideHealthReportWriter.GetStatusCode(withoutFault.Status));

        entries["paydb"] = Entry(HealthStatus.Unhealthy, "db down", TimeSpan.Zero);
        var withFault = new HealthReport(entries, TimeSpan.Zero);
        Assert.Equal(HealthStatus.Unhealthy, withFault.Status);
        Assert.Equal(
            (int)HttpStatusCode.ServiceUnavailable,
            SwiftRideHealthReportWriter.GetStatusCode(withFault.Status));
    }
}

/// <summary>
/// Pure logic của nhánh lỗi khởi động — không cần PostgreSQL thật.
/// </summary>
public class PaymentDbInitializerDecisionTests
{
    private sealed class FakeDbException : DbException
    {
        public FakeDbException() : base("connection refused") { }
    }

    [Fact]
    public void Baseline_DetectionIsConservativeWhenHistoryUnreadable()
    {
        // Cold start: provider không đọc được history (DB chưa tới / query hỏng) và caller đã gom
        // lại thành null → quyết định an toàn là KHÔNG baseline. Nếu baseline mù quáng ở đây,
        // MigrateAsync sẽ bỏ qua InitialPayDb và vỡ ở CreateTable.
        var unknown = BaselineDecision.ShouldBaseline(false, null);
        Assert.False(unknown.BaselineRequired);
        Assert.Equal(BaselineReason.None, unknown.Reason);

        // History table TỒN TẠI nhưng rỗng mới là dấu hiệu EnsureCreated.
        var ensureCreated = BaselineDecision.ShouldBaseline(false, Array.Empty<string>());
        Assert.True(ensureCreated.BaselineRequired);
        Assert.Equal(BaselineReason.EnsureCreatedDetected, ensureCreated.Reason);
    }

    [Fact]
    public void Baseline_SkippedWhenMigrationsAlreadyApplied()
    {
        var decision = BaselineDecision.ShouldBaseline(
            false, ["20261102132014_InitialPayDb", "20261106041500_PaymentValidationConstraints", "20261109000000_RemoveRefundLogic"]);
        Assert.False(decision.BaselineRequired);
    }

    [Fact]
    public void Baseline_ForcedByExplicitOperatorMarker()
    {
        var decision = BaselineDecision.ShouldBaseline(true, null);
        Assert.True(decision.BaselineRequired);
        Assert.Equal(BaselineReason.ExplicitMarker, decision.Reason);
    }

    [Theory]
    [InlineData(true, true, false)]   // applied -> boot tiếp
    [InlineData(false, true, true)]   // DB chưa dậy sau trần retry -> abort
    [InlineData(false, false, true)]  // schema/provider sai -> abort ngay
    public void StartupOutcome_AbortsUnlessSchemaConverged(
        bool applied, bool neverConnected, bool expectAbort)
    {
        var outcome = new PaymentDbInitializer.StartupMigrationOutcome(applied, neverConnected);
        Assert.Equal(expectAbort, outcome.ShouldAbortStartup);
    }

    [Fact]
    public void ConnectivityClassification_OnlyProviderErrorsAreRetryable()
    {
        Assert.True(BaselineDecision.IsTransientConnectivityFailure(new FakeDbException()));
        Assert.False(BaselineDecision.IsTransientConnectivityFailure(new InvalidOperationException()));
        // OperationCanceled không phải DbException → không được coi là lỗi kết nối có thể retry.
        Assert.False(BaselineDecision.IsTransientConnectivityFailure(new OperationCanceledException()));
    }

    [Fact]
    public void RetryCeiling_IsFiniteSoStartupCannotHangForever()
    {
        // 12 lần * 5s backoff ≈ 60s: đủ cho Postgres cold start, vẫn fail-fast khi DB sai thật.
        Assert.InRange(PaymentDbInitializer.MaxUnreachableAttempts, 1, 60);
        Assert.True(PaymentDbInitializer.StartupMigrationBackoff > TimeSpan.Zero);
    }
}