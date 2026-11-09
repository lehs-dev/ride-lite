using System.Data.Common;

namespace PaymentService.Infrastructure.Persistence;

/// <summary>Lý do baseline __EFMigrationsHistory (audit log + test).</summary>
public enum BaselineReason
{
    /// <summary>Không cần baseline: DB đã có migration hoặc đang trắng hoàn toàn.</summary>
    None = 0,

    /// <summary>Có history table nhưng không có migration nào — DB dev tạo bằng EnsureCreated.</summary>
    EnsureCreatedDetected = 1,

    /// <summary>Opt-in qua <c>PaymentDb:EnsureCreatedBaseline=true</c>.</summary>
    ExplicitMarker = 2,
}

/// <summary>
/// Pure logic quyết định có được chèn baseline InitialPayDb hay không — tách khỏi EF để test
/// được cả ba case: DB trắng, DB EnsureCreated cũ, và DB chưa tới.
/// </summary>
/// <remarks>
/// Lỗ hổng của phiên bản trước: "history table rỗng" bị coi là "chưa tồn tại" → baseline mù quáng.
/// Với DB trắng thật (cold start), điều đó khiến MigrateAsync bỏ qua InitialPayDb và vỡ ở
/// CreateTable. Chỉ baseline khi chắc chắn có schema EnsureCreated; nếu không thì chạy migrations
/// từ đầu (đúng cho DB trắng) và để lỗi bộc lộ thay vì âm thầm ghi đè.
/// </remarks>
public static class BaselineDecision
{
    /// <param name="ShouldBaseline">Chèn baseline InitialPayDb trước khi MigrateAsync.</param>
    /// <param name="Reason"><see cref="Persistence.BaselineReason"/> mô tả lý do.</param>
    public sealed record Decision(bool BaselineRequired, BaselineReason Reason)
    {
        public static readonly Decision None = new(false, BaselineReason.None);
    }

    /// <param name="explicitEnsureCreatedMarker">
    /// Người vận hành khẳng định đây là DB EnsureCreated (opt-in). Ưu tiên cao nhất.
    /// </param>
    /// <param name="appliedMigrations">
    /// Danh sách migration đọc được từ history table; <c>null</c> nghĩa là không đọc được gì
    /// (history table chưa tồn tại, hoặc DB chưa tới và lỗi đã được caller gom lại) — khi đó
    /// KHÔNG baseline.
    /// </param>
    public static Decision ShouldBaseline(
        bool explicitEnsureCreatedMarker,
        IReadOnlyList<string>? appliedMigrations)
    {
        if (explicitEnsureCreatedMarker)
            return new Decision(true, BaselineReason.ExplicitMarker);

        // Không đọc được history (DB chưa tới / provider lỗi) → mặc định an toàn: không baseline.
        if (appliedMigrations is null)
            return Decision.None;

        // History table tồn tại nhưng rỗng = schema do EnsureCreated tạo (nó không viết history).
        return appliedMigrations.Count == 0
            ? new Decision(true, BaselineReason.EnsureCreatedDetected)
            : Decision.None;
    }

    /// <summary>
    /// Phân loại failure lúc khởi động: chỉ exception kết nối cấp provider (<see cref="DbException"/>,
    /// Npgsql dịch socket/timeout/DNS thành <c>NpgsqlException</c>) đáng để retry hữu hạn.
    /// OperationCanceled (host đang dừng) và mọi lỗi khác (schema/migration/provider sai) là fail-fast.
    /// </summary>
    public static bool IsTransientConnectivityFailure(Exception ex) =>
        ex is DbException;
}
