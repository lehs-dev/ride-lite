using Microsoft.EntityFrameworkCore;
using PaymentService.Domain.Entities;

namespace PaymentService.Infrastructure.Persistence;

/// <summary>
/// PayDB (PostgreSQL). Toàn bộ cột thời gian là timestamptz — Npgsql map DateTime
/// Kind=Utc sang "timestamp without time zone" và throw khi save nếu không khai báo.
/// </summary>
public class PaymentDbContext : DbContext
{
    public const string Timestamptz = "timestamp with time zone";

    public PaymentDbContext(DbContextOptions<PaymentDbContext> options) : base(options)
    {
    }

    public DbSet<Domain.Entities.Payment> Payments => Set<Domain.Entities.Payment>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Domain.Entities.Payment>(builder =>
        {
            builder.ToTable("Payments");
            builder.HasKey(p => p.Id);

            builder.Property(p => p.PaymentRequestKey).IsRequired().HasMaxLength(128);
            builder.Property(p => p.Currency).IsRequired().HasMaxLength(3);
            builder.Property(p => p.Method).HasConversion<short>();
            builder.Property(p => p.Status).HasConversion<short>();
            builder.Property(p => p.Amount).HasPrecision(18, 2);

            builder.Property(p => p.GatewayPartnerCode).HasMaxLength(64);
            builder.Property(p => p.GatewayOrderRef).HasMaxLength(64);
            builder.Property(p => p.GatewayTransId).HasMaxLength(64);
            builder.Property(p => p.SignatureChecksum).HasMaxLength(128);
            builder.Property(p => p.FailureCode).HasMaxLength(48);
            builder.Property(p => p.FailureMessage).HasMaxLength(256);
            builder.Property(p => p.CorrelationId).HasMaxLength(64);

            builder.Property(p => p.CreatedAtUtc).HasColumnType(Timestamptz);
            builder.Property(p => p.UpdatedAtUtc).HasColumnType(Timestamptz);
            builder.Property(p => p.SettledAtUtc).HasColumnType(Timestamptz);
            builder.Property(p => p.FailedAtUtc).HasColumnType(Timestamptz);

            // Status là concurrency token. Mọi chuyển trạng thái — kể cả claim Created -> Charging —
            // đều đổi cột này, nên UPDATE sinh "WHERE ... Status = @orig": đúng phép thử cần có khi
            // hai phiên claim song song. Không cần cột mới, không thêm DDL.
            // (xmin/.IsRowVersion() đã thử và loại: EF phát hành AddColumn<uint>("xmin") và PostgreSQL
            //  từ chối vì xmin đã là system column.)
            builder.Property(p => p.Status).IsConcurrencyToken();

            // Idempotency nghiệp vụ: một trip chỉ có đúng một nghĩa vụ thanh toán.
            builder.HasIndex(p => p.TripId).IsUnique();
            builder.HasIndex(p => p.PaymentRequestKey).IsUnique();
            builder.HasIndex(p => new { p.Status, p.CreatedAtUtc });
            builder.HasIndex(p => new { p.Status, p.UpdatedAtUtc });
            builder.HasIndex(p => new { p.RiderId, p.CreatedAtUtc });

            builder.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Payments_Amount_Positive", "\"Amount\" > 0");
                // Chặn dữ liệu rác ngay tại DB, không chỉ ở domain: Method/Status phải nằm trong
                // tập enum đã định nghĩa. Currency KHÔNG có check constraint vì varchar(3) đã chặn
                // độ dài và các giá trị cũ viết thường vẫn đang hợp lệ về nghiệp vụ — thêm check
                // in hoa sẽ làm migration fail trên dữ liệu hiện hữu.
                t.HasCheckConstraint("CK_Payments_Method_Defined",
                    "\"Method\" IN (1, 2)");
                // Status enum values: Created=1, PendingRetry=2, Authorized=3, Settled=4, Failed=5, Charging=8
                t.HasCheckConstraint("CK_Payments_Status_Defined",
                    "\"Status\" IN (1, 2, 3, 4, 5, 8)");
            });
        });

        modelBuilder.Entity<OutboxMessage>(builder =>
        {
            builder.ToTable("PaymentOutboxMessages");
            builder.HasKey(m => m.Id);

            builder.Property(m => m.EventType).IsRequired().HasMaxLength(128);
            builder.Property(m => m.Payload).IsRequired();
            builder.Property(m => m.LastError).HasMaxLength(1024);
            builder.Property(m => m.OccurredAtUtc).HasColumnType(Timestamptz);
            builder.Property(m => m.DeliveredAtUtc).HasColumnType(Timestamptz);

            builder.HasIndex(m => m.MessageId).IsUnique();
            // Hàng đợi dispatch: chỉ dòng chưa gửi.
            builder.HasIndex(m => m.OccurredAtUtc)
                .HasFilter("\"DeliveredAtUtc\" IS NULL");
        });

        modelBuilder.Entity<ProcessedEvent>(builder =>
        {
            builder.ToTable("ProcessedEvents");
            builder.HasKey(e => new { e.ConsumerName, e.BusinessKey });

            builder.Property(e => e.ConsumerName).HasMaxLength(64);
            builder.Property(e => e.BusinessKey).HasMaxLength(160);
            builder.Property(e => e.ProcessedAtUtc).HasColumnType(Timestamptz);
        });
    }
}