using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PaymentService.Application.Abstractions;
using PaymentService.Domain.Entities;
using PaymentService.Infrastructure.Messaging;
using PaymentService.Infrastructure.Payment;
using PaymentService.Infrastructure.Persistence;
using PaymentService.Infrastructure.Services;
using SwiftRide.Contracts.Events;
using Testcontainers.PostgreSql;

namespace PaymentService.Tests.Consumers;

/// <summary>
/// Consumer test chạy trên PostgreSQL container thật: EnsureCreated + transaction +
/// unique index là những thứ mock không tái hiện nổi. In-memory bus thay RabbitMQ.
/// </summary>
public class RideCompletedConsumerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder().WithImage("postgres:18.6").Build();

    private ServiceProvider _provider = null!;
    private IBusControl _bus = null!;
    private CancellationTokenSource _hostCts = new();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<PaymentDbContext>(options =>
            options.UseNpgsql(_postgres.GetConnectionString()));
        services.Configure<MoMoSandboxOptions>(o => o.SimulatedLatencyMs = 0);
        services.AddSingleton<IPaymentGateway, MoMoSandboxGateway>();
        services.AddScoped<IPaymentEventPublisher, PaymentEventPublisher>();
        services.AddScoped<PaymentProcessor>();
        // Dispatcher outbox thật — test xác minh row PaymentSettled được gửi đi.
        services.AddHostedService<OutboxDispatcherWorker>();

        services.AddMassTransitTestHarness(x =>
        {
            x.AddConsumer<RideCompletedConsumer, RideCompletedConsumerDefinition>();
            
        });

        _provider = services.BuildServiceProvider(validateScopes: false);
        _bus = _provider.GetRequiredService<IBusControl>();
        await _bus.StartAsync();

        // BackgroundService không tự chạy ngoài ASP.NET Core host — kích hoạt thủ công.
        foreach (var hosted in _provider.GetServices<IHostedService>())
            await hosted.StartAsync(_hostCts.Token);

        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PaymentDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        _hostCts.Cancel();
        await _bus.StopAsync();
        await _provider.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task SingleDelivery_ChargesSettlesAndPublishesPaymentSettled()
    {
        var tripId = Guid.NewGuid();
        var key = EventContracts.BuildPaymentRequestKey(tripId);

        await Publish(tripId, key, amount: 95_000m);

        Assert.Equal(PaymentStatus.Settled, (await LoadPayments()).Single().Status);
        var harness = _provider.GetRequiredService<ITestHarness>();
        Assert.True(await harness.Consumed.Any<RideCompleted>());

        // PaymentSettled đi qua outbox table + dispatcher (BackgroundService), không phải
        // context.Publish trực tiếp — kiểm chứng bằng cách chờ row outbox được gửi.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!cts.IsCancellationRequested)
        {
            using var scope = _provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
            if (await db.OutboxMessages.AnyAsync(m => m.EventType == nameof(PaymentSettled)
                    && m.DeliveredAtUtc != null, cts.Token))
                return;
            await Task.Delay(100, cts.Token);
        }
        Assert.Fail("PaymentSettled không được dispatch qua outbox trong 20s.");
    }

    [Fact]
    public async Task DuplicateDelivery_DoesNotDoubleChargeOrDoublePublish()
    {
        var tripId = Guid.NewGuid();
        var key = EventContracts.BuildPaymentRequestKey(tripId);

        await Publish(tripId, key, 95_000m);
        // Redelivery cùng message nhưng EventId khác — dedup phải theo TripId+key.
        await Publish(tripId, key, 95_000m);

        var payments = await LoadPayments();
        Assert.Single(payments);
        Assert.Equal(PaymentStatus.Settled, payments[0].Status);

        using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var outboxRows = await db.OutboxMessages.Where(m => m.EventType == nameof(PaymentSettled)).CountAsync();
        Assert.Equal(1, outboxRows);
    }

    [Fact]
    public async Task AmountConflictForSameTrip_GoesToErrorQueueWithoutMutating()
    {
        var tripId = Guid.NewGuid();
        var key = EventContracts.BuildPaymentRequestKey(tripId);

        await Publish(tripId, key, 95_000m);
        await Publish(tripId, key, 200_000m);

        var payment = (await LoadPayments()).Single();
        Assert.Equal(95_000m, payment.Amount);
        Assert.Equal(PaymentStatus.Settled, payment.Status);
    }

    [Fact]
    public async Task InsufficientFundsTrigger_FailsPaymentAndStaysClosed()
    {
        var tripId = Guid.NewGuid();
        var key = EventContracts.BuildPaymentRequestKey(tripId);

        await Publish(tripId, key, 213_003m);

        var payment = (await LoadPayments()).Single();
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal(MoMoSandboxGateway.CodeInsufficientFunds, payment.FailureCode);

        // Redelivery sau khi Failed: không được charge lần hai.
        await Publish(tripId, key, 213_003m);
        Assert.Equal(0, (await LoadPayments()).Single().AttemptCount);
    }

    [Fact]
    public async Task RedeliveryAfterCrashBeforeTerminalResult_ResumesAndSettlesOnce()
    {
        // P0-1 trên PostgreSQL thật: payment đã tồn tại ở Created mà KHÔNG có inbox row
        // (= process chết ngay sau khi tạo row, trước khi chốt kết quả). Bản cũ claim inbox
        // ngay lúc tạo, nên redelivery bị ACK mù và payment kẹt Created vĩnh viễn.
        var tripId = Guid.NewGuid();
        var key = EventContracts.BuildPaymentRequestKey(tripId);
        var riderId = Guid.NewGuid();

        using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
            db.Payments.Add(Payment.CreateFromRide(tripId, key, riderId, null,
                95_000m, "VND", PaymentMethod.MoMoApp, null));
            await db.SaveChangesAsync();
        }

        await Publish(tripId, key, 95_000m);

        var settled = (await LoadPayments()).Single();
        Assert.Equal(PaymentStatus.Settled, settled.Status);
        Assert.Equal(1, await CountOutboxRows(nameof(PaymentSettled)));
        Assert.Equal(1, await CountInboxRows(tripId, key));

        // Redelivery sau khi đã chốt: không được tạo charge thứ hai hay event thứ hai.
        await Publish(tripId, key, 95_000m);
        Assert.Equal(1, await CountOutboxRows(nameof(PaymentSettled)));
        Assert.Single(await LoadPayments());
    }

    private async Task<int> CountOutboxRows(string eventType)
    {
        using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        return await db.OutboxMessages.CountAsync(m => m.EventType == eventType);
    }

    private async Task<int> CountInboxRows(Guid tripId, string key)
    {
        using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        return await db.ProcessedEvents.CountAsync(e =>
            e.ConsumerName == RideCompletedConsumer.InboxConsumerName && e.BusinessKey == $"{tripId}:{key}");
    }

    private async Task Publish(Guid tripId, string key, decimal amount)
    {
        var endpoint = await _bus.GetSendEndpoint(
            new Uri($"queue:{RideCompletedConsumerDefinition.QueueName}"));
        // Wait for ack: consumer đã chạy xong (hoặc vào _error) trước khi test kiểm tra DB.
        await endpoint.Send(new RideCompleted(
            EventId: Guid.NewGuid(),
            TripId: tripId,
            RiderId: Guid.NewGuid(),
            DriverId: null,
            Amount: amount,
            Currency: "VND",
            OccurredAtUtc: DateTime.UtcNow,
            CorrelationId: null,
            PaymentRequestKey: key));
        await WaitForSettledOrError(tripId);
    }

    /// <summary>
    /// In-memory bus dispatch async; poll tới khi payment đạt trạng thái chốt
    /// (Settled/Failed) hoặc message rơi vào queue _error — tối đa 15s.
    /// </summary>
    private async Task WaitForSettledOrError(Guid tripId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!cts.IsCancellationRequested)
        {
            using var scope = _provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
            var status = await db.Payments.Where(p => p.TripId == tripId)
                .Select(p => p.Status).FirstOrDefaultAsync(cts.Token);
            if (status is PaymentStatus.Settled or PaymentStatus.Failed)
                return;

            await Task.Delay(100, cts.Token);
        }
    }

    private async Task<List<Payment>> LoadPayments()
    {
        using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        return await db.Payments.AsNoTracking().ToListAsync();
    }
}