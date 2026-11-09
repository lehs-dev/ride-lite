using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace PaymentService.Infrastructure.Persistence;

/// <summary>
/// Hội tụ schema PayDB qua migrations. DB dev tạo bằng EnsureCreatedAsync (không có
/// __EFMigrationsHistory) được baseline về InitialPayDb — khi đó các migration hiện có
/// chỉ áp phần còn thiếu, không đụng dữ liệu. DB mới chạy MigrateAsync từ đầu.
/// </summary>
public static class PaymentDbInitializer
{
    // Migration ID của 20261102132014_InitialPayDb — dùng cho baseline thủ công.
    public const string InitialMigrationId = "20261102132014_InitialPayDb";

    private const string HistoryTableDdl = """
        CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
            "MigrationId" character varying(150) NOT NULL,
            "ProductVersion" character varying(32) NOT NULL,
            CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
        );
        """;

    /// <summary>Khoảng chờ giữa các lần thử migrations lúc khởi động.</summary>
    public static readonly TimeSpan StartupMigrationBackoff = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Số lần thử tối đa khi mọi thất bại đều là "không kết nối được" (Postgres còn đang khởi
    /// động). Tổng thời gian chờ ~60s: đủ cho cold start trong compose, vẫn fail-fast khi DB
    /// sai thật (chuỗi kết nối nhầm, user không quyền) thay vì boot với schema chưa hội tụ.
    /// </summary>
    public const int MaxUnreachableAttempts = 12;

    /// <summary>Kết quả một lượt hội tụ schema lúc khởi động — ánh xạ thẳng sang quyết định boot.</summary>
    /// <param name="Applied">true nếu migrations chạy xong (schema đã hội tụ).</param>
    /// <param name="NeverConnected">
    /// true nếu mọi lần thất bại đều là lỗi kết nối cấp provider, tức nhiều khả năng DB chưa dậy
    /// hơn là schema/provider hỏng.
    /// </param>
    public sealed record StartupMigrationOutcome(bool Applied, bool NeverConnected)
    {
        /// <summary>
        /// Chính sách khởi động: schema chưa hội tụ thì service CHƯA phục vụ được gì (mọi query
        /// Payments/ProcessedEvents sẽ 500), nên host phải chết thay vì mở cổng với schema lệch.
        /// /health/live và restart policy của orchestrator mới là đường đúng để chờ DB dậy.
        /// </summary>
        public bool ShouldAbortStartup => !Applied;
    }

    /// <summary>
    /// Startup fail-fast (P1-3): thử <see cref="ApplyAsync"/> với backoff hữu hạn rồi trả kết quả
    /// để caller (Program.cs) tự quyết định abort. Chạy trước <c>app.Run()</c> nên retry ở đây an toàn.
    /// </summary>
    public static async Task<StartupMigrationOutcome> InitializeWithRetryAsync(
        PaymentDbContext db,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var unreachableAttempts = 0;

        while (true)
        {
            try
            {
                await ApplyAsync(db, logger, cancellationToken);
                return new StartupMigrationOutcome(Applied: true, NeverConnected: false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (DbException ex) when (BaselineDecision.IsTransientConnectivityFailure(ex))
            {
                unreachableAttempts++;
                logger.LogError(ex,
                    "Chưa kết nối được PayDB (lần {Attempt}/{Max}) — thử lại sau {Backoff}.",
                    unreachableAttempts, MaxUnreachableAttempts, StartupMigrationBackoff);

                if (unreachableAttempts >= MaxUnreachableAttempts)
                    return new StartupMigrationOutcome(Applied: false, NeverConnected: true);
            }
            catch (Exception ex)
            {
                // Không phải lỗi kết nối: schema/provider/migration sai — retry cũng không khỏi.
                logger.LogError(ex, "Migrations PayDB thất bại vì lỗi không phải kết nối — dừng thử lại.");
                return new StartupMigrationOutcome(Applied: false, NeverConnected: false);
            }

            await Task.Delay(StartupMigrationBackoff, cancellationToken);
        }
    }

    /// <summary>Hội tụ schema một lần. Ném exception để caller quyết định chính sách retry.</summary>
    public static async Task ApplyAsync(
        PaymentDbContext db,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            var decision = BaselineDecision.ShouldBaseline(
                HasExplicitEnsureCreatedMarker(),
                await GetAppliedMigrationsSafeAsync(db, logger, cancellationToken));

            if (decision.BaselineRequired)
            {
                // Schema đang do EnsureCreated tạo: baseline để MigrateAsync chỉ chạy phần còn thiếu.
                await db.Database.ExecuteSqlRawAsync(HistoryTableDdl, cancellationToken);
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ({0}, {1})
                    ON CONFLICT ("MigrationId") DO NOTHING
                    """,
                    [InitialMigrationId, ProductVersion],
                    cancellationToken);
                logger.LogInformation(
                    "Baseline __EFMigrationsHistory ({Reason}) cho PayDB — chỉ áp phần migration còn thiếu.",
                    decision.Reason);
            }

            await db.Database.MigrateAsync(cancellationToken);
            logger.LogInformation("PayDB schema đã hội tụ qua migrations.");
        });
    }

    /// <summary>Tương thích gọi cũ (test integration): một lượt, không retry.</summary>
    public static Task InitializeAsync(PaymentDbContext db, ILogger logger) =>
        ApplyAsync(db, logger, CancellationToken.None);

    /// <summary>Nguồn sự thật cho readiness: provider tự trả lời kết nối được chưa.</summary>
    public static Task<bool> CanConnectAsync(PaymentDbContext db, CancellationToken cancellationToken = default) =>
        db.Database.CanConnectAsync(cancellationToken);

    /// <summary>
    /// Lịch sử migration đọc được hay không là tín hiệu phân biệt "DB trắng/chưa tới" với
    /// "history table rỗng vì EnsureCreated". Trả null khi không đọc được.
    /// </summary>
    /// <remarks>
    /// Lỗ hổng cũ: bắt <c>DbException</c> rồi coi "không có migration nào" = "EnsureCreated", nên
    /// DB chưa tới bị baseline mù quáng (INSERT sau đó lại throw — vòng lặp vô hạn ở caller). Ở đây
    /// lỗi kết nối NÉM NGAY để <see cref="InitializeWithRetryAsync"/> retry; chỉ những exception
    /// không phải kết nối (thường là history table chưa tồn tại) mới được diễn giải là "trắng/rỗng".
    /// </remarks>
    internal static async Task<IReadOnlyList<string>?> GetAppliedMigrationsSafeAsync(
        PaymentDbContext db,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // GetAppliedMigrations đọc history table qua migration assembly — throw nếu table chưa có.
            return (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
        }
        catch (DbException)
        {
            throw; // DB chưa tới → thuộc trách nhiệm retry của caller.
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Không đọc được __EFMigrationsHistory của PayDB — coi như chưa có baseline.");
            return null;
        }
    }

    /// <summary>
    /// Marker opt-in cho DB dev từng tạo bằng EnsureCreated: đặt
    /// <c>PaymentDb__EnsureCreatedBaseline=true</c> nếu self-check heuristic bỏ sót mà bạn chắc
    /// chắn schema hiện tại tương đương InitialPayDb. Mặc định tắt — không tự đoán từ chuỗi kết nối.
    /// </summary>
    internal const string EnsureCreatedMarkerKey = "PaymentDb:EnsureCreatedBaseline";
    internal const string EnsureCreatedMarkerEnvKey = "PaymentDb__EnsureCreatedBaseline";

    private static bool HasExplicitEnsureCreatedMarker()
    {
        // Initializer là static (không DI được IConfiguration ở đây) — host environment là nguồn duy nhất.
        var raw = Environment.GetEnvironmentVariable(EnsureCreatedMarkerEnvKey);
        return bool.TryParse(raw, out var flag) && flag;
    }

    private static string ProductVersion => "10.0.12";
}
