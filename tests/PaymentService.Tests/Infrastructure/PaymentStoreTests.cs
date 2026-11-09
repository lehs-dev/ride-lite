using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PaymentEntity = PaymentService.Domain.Entities.Payment;
using PaymentService.Application.Abstractions;
using PaymentService.Domain.Entities;
using PaymentService.Infrastructure.Messaging;
using PaymentService.Infrastructure.Persistence;
using PaymentService.Infrastructure.Services;
using SwiftRide.Contracts.Events;
using Testcontainers.PostgreSql;

namespace PaymentService.Tests.Infrastructure;

/// <summary>
/// Những guarantee chỉ có thể chứng minh trên PostgreSQL thật: concurrency token chặn
/// double-charge giữa hai phiên song song, và worker
/// cứu được payment mồ côi. EF InMemory không bump version thật nên không thay được.
/// </summary>
public class PaymentStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder().WithImage("postgres:18.6").Build();

    private ServiceProvider _provider = null!;
    private CountingGateway _gateway = new();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<PaymentDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()));
        services.AddSingleton<IPaymentGateway>(_gateway);
        services.AddScoped<IPaymentEventPublisher, PaymentEventPublisher>();
        services.AddScoped<PaymentProcessor>();
        _provider = services.BuildServiceProvider(validateScopes: false);

        // Đi đúng đường khởi động production để patch SQL idempotent cũng được thực thi.
        await using var scope = _provider.CreateAsyncScope();
        await PaymentDbInitializer.InitializeAsync(
            scope.ServiceProvider.GetRequiredService<PaymentDbContext>(),
            NullLogger.Instance);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task TwoParallelClaimants_ChargeExactlyOnce()
    {
        // P0-2: hai phiên cùng cầm một payment Created (REST + worker, hoặc hai instance
        // consumer). Phiên thắng claim đổi Status -> Charging; phiên kia phát sinh
        // UPDATE ... WHERE "Status" = Created khớp 0 row -> DbUpdateConcurrencyException ->
        // đứng ngoài, không gọi gateway. Latency nhân tạo giữ phiên thắng ở bước gateway đủ
        // lâu để phiên kia chắc chắn rơi vào đúng cửa sổ claim, nên test không ăn may lịch
        // trình thread (không delay thì phiên thua có thể thấy Settled và đi nhánh dedup).
        var paymentId = await SeedAsync(PaymentStatus.Created);
        _gateway.Reset();
        _gateway.Latency = TimeSpan.FromMilliseconds(750);
        try
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var loadedBarrier = new SemaphoreSlim(0, 2);
            var first = ClaimOnceAsync(paymentId, gate, loadedBarrier);
            var second = ClaimOnceAsync(paymentId, gate, loadedBarrier);

            // Cả hai phiên phải đọc xong snapshot Created trước khi bên nào được claim.
            using var barrierTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.True(await loadedBarrier.WaitAsync(2, barrierTimeout.Token));
            gate.SetResult();

            var outcomes = await Task.WhenAll(first, second);
            var report = $"outcomes=[{string.Join(", ", outcomes)}] charges={_gateway.Charges}";

            Assert.True(_gateway.Charges == 1, $"gateway phải được gọi đúng một lần — {report}");
            Assert.Equal(1, outcomes.Count(o => o == ChargeOutcome.Settled));
            Assert.True(outcomes.Count(o => o != ChargeOutcome.Settled) == 1,
                $"đúng một phiên bị chặn khỏi gateway — {report}");

            await using var scope = _provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
            var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == paymentId);
            Assert.Equal(PaymentStatus.Settled, payment.Status);
            Assert.Equal(1, await db.OutboxMessages.CountAsync(m =>
                m.EventType == nameof(PaymentSettled) && m.AggregateId == paymentId));
        }
        finally
        {
            _gateway.Latency = TimeSpan.Zero;
        }
    }

    [Fact]
    public async Task StaleChargingLease_IsReclaimedByWorkerAndChargedOnce()
    {
        // P0-1: process chết sau khi claim, trước khi ghi kết quả. Trước bản sửa, không có
        // đường nào cứu: worker chỉ quét PendingRetry.
        var paymentId = await SeedAsync(PaymentStatus.Created);
        await MarkStaleChargingAsync(paymentId, TimeSpan.FromMinutes(10));
        _gateway.Reset();

        await RunWorkerOneCycleAsync(TimeSpan.FromSeconds(40));

        Assert.Equal(1, _gateway.Charges);
        var payment = await LoadAsync(paymentId);
        Assert.Equal(PaymentStatus.Settled, payment.Status);

        await RunWorkerOneCycleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, _gateway.Charges); // không charge lại sau khi đã chốt
    }

    [Fact]
    public async Task OrphanedCreatedPayment_IsPickedUpByWorker()
    {
        // Nghĩa vụ Created mà chưa phiên nào claim (consumer chết ngay sau khi tạo row).
        var paymentId = await SeedAsync(PaymentStatus.Created, age: TimeSpan.FromMinutes(5));
        _gateway.Reset();

        await RunWorkerOneCycleAsync(TimeSpan.FromSeconds(40));

        Assert.Equal(1, _gateway.Charges);
        Assert.Equal(PaymentStatus.Settled, (await LoadAsync(paymentId)).Status);
    }

    private async Task<ChargeOutcome> ClaimOnceAsync(
        Guid paymentId, TaskCompletionSource gate, SemaphoreSlim loadedBarrier)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var processor = scope.ServiceProvider.GetRequiredService<PaymentProcessor>();

        // Load TRƯỚC barrier: cả hai phiên phải cầm cùng một trạng thái gốc.
        var payment = await db.Payments.SingleAsync(p => p.Id == paymentId);
        loadedBarrier.Release();
        await gate.Task;

        return await processor.ChargeAsync(payment, Guid.NewGuid(), null, CancellationToken.None);
    }

    private async Task<Guid> SeedAsync(PaymentStatus status, TimeSpan? age = null)
    {
        var tripId = Guid.NewGuid();
        var payment = PaymentEntity.CreateFromRide(tripId, EventContracts.BuildPaymentRequestKey(tripId),
            Guid.NewGuid(), null, 95_000m, "VND", PaymentMethod.MoMoApp, null);

        switch (status)
        {
            case PaymentStatus.Settled:
                payment.MarkAuthorized();
                payment.MarkSettled(Guid.NewGuid());
                break;
            case PaymentStatus.Created when age is not null:
                payment.CreatedAtUtc = DateTime.UtcNow - age.Value;
                break;
            case PaymentStatus.Created:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Seed ngoài phạm vi test.");
        }

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment.Id;
    }

    private async Task MarkStaleChargingAsync(Guid paymentId, TimeSpan age)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var payment = await db.Payments.SingleAsync(p => p.Id == paymentId);

        payment.MarkCharging();
        payment.UpdatedAtUtc = DateTime.UtcNow - age; // quá ChargingLease -> worker được phép reclaim
        await db.SaveChangesAsync();
    }

    private async Task<PaymentEntity> LoadAsync(Guid paymentId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        return await db.Payments.AsNoTracking().SingleAsync(p => p.Id == paymentId);
    }

    /// <summary>Chạy PaymentBackgroundWorker thật (BackgroundService) trong một khoảng thời gian.</summary>
    private async Task RunWorkerOneCycleAsync(TimeSpan within)
    {
        using var cts = new CancellationTokenSource();
        var worker = ActivatorUtilities.CreateInstance<PaymentBackgroundWorker>(_provider);
        await worker.StartAsync(cts.Token);
        try
        {
            var deadline = DateTime.UtcNow + within;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
                if (_gateway.Charges > 0)
                {
                    // Cho chu kỳ ghi kết quả chốt hoàn tất.
                    await Task.Delay(1500);
                    return;
                }
            }
        }
        finally
        {
            cts.Cancel();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private sealed class CountingGateway : IPaymentGateway
    {
        private int _charges;

        public int Charges => Volatile.Read(ref _charges);

        /// <summary>Kéo dài bước gateway để mở cửa sổ claim cho test cạnh tranh.</summary>
        public TimeSpan Latency { get; set; }

        /// <summary>Fixture dùng chung cho cả class (xunit) — phải reset đầu mỗi test.</summary>
        public void Reset() => Interlocked.Exchange(ref _charges, 0);

        public async Task<GatewayChargeResult> ChargeAsync(PaymentIntent intent, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _charges);
            if (Latency > TimeSpan.Zero)
                await Task.Delay(Latency, cancellationToken);
            return TestFixtures.Success(intent.Amount);
        }
    }
}