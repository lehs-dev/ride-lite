using Microsoft.EntityFrameworkCore;
using TripService.Domain.Entities;

namespace TripService.Infrastructure.Persistence;

public class TripDbContext : DbContext
{
    public TripDbContext(DbContextOptions<TripDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<Trip> Trips => Set<Trip>();
    public DbSet<TripStatusHistory> TripStatusHistories => Set<TripStatusHistory>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // Trip
        modelBuilder.Entity<Trip>(builder =>
        {
            builder.ToTable("Trips");

            builder.HasKey(t => t.Id);
            builder.Property(t => t.Id).HasColumnName("id");
            builder.Property(t => t.RiderId).HasColumnName("riderId");
            builder.Property(t => t.DriverId).HasColumnName("driverId");

            builder.Property(t => t.FromLat).HasColumnName("fromLat");
            builder.Property(t => t.FromLon).HasColumnName("fromLon");
            builder.Property(t => t.FromAddress).HasColumnName("fromAddress");

            builder.Property(t => t.ToLat).HasColumnName("toLat");
            builder.Property(t => t.ToLon).HasColumnName("toLon");
            builder.Property(t => t.ToAddress).HasColumnName("toAddress");

            builder.Property(t => t.Status).HasColumnName("status");
            builder.Property(t => t.Distance).HasColumnName("distance");
            builder.Property(t => t.EstimatedTime).HasColumnName("estimatedTime");
            builder.Property(t => t.Price).HasColumnName("price");

            builder.Property(t => t.PaymentMethod).HasColumnName("paymentMethod");
            builder.Property(t => t.PaymentStatus).HasColumnName("paymentStatus");

            builder.Property(t => t.CreatedAtUtc)
                .HasColumnName("createdAt")
                .HasColumnType("datetime2");

            builder.Property(t => t.UpdatedAtUtc)
                .HasColumnName("updatedAt")
                .HasColumnType("datetime2");

            builder.Property(t => t.PickUpTimeUtc)
                .HasColumnName("pickUpTime")
                .HasColumnType("datetime2");

            builder.Property(t => t.DropOffTimeUtc)
                .HasColumnName("dropOffTime")
                .HasColumnType("datetime2");

            builder.Property(t => t.FromAddress)
                .IsRequired()
                .HasMaxLength(255);

            builder.Property(t => t.ToAddress)
                .IsRequired()
                .HasMaxLength(255);

            builder.Property(t => t.Price)
                .HasPrecision(10, 2);

            builder.Property(t => t.Status)
            .HasConversion<string>()
            .HasMaxLength(255)
            .IsRequired();

            builder.Property(t => t.PaymentStatus)
                .HasConversion<string>()
                .HasMaxLength(255)
                .IsRequired();

            builder.Property(t => t.PaymentMethod)
                .HasMaxLength(255)
                .IsRequired();
            builder.HasOne<User>()
                .WithMany()
                .HasForeignKey(t => t.RiderId)
                .OnDelete(DeleteBehavior.NoAction);

            builder.HasOne<User>()
                .WithMany()
                .HasForeignKey(t => t.DriverId)
                .OnDelete(DeleteBehavior.NoAction);
                });

        // User
        modelBuilder.Entity<User>(builder =>
        {
            builder.ToTable("Users");
            builder.HasKey(u => u.Id);
            builder.Property(u => u.Username).IsRequired().HasMaxLength(100);
            builder.Property(u => u.Email).IsRequired().HasMaxLength(256);
            builder.Property(u => u.FullName).IsRequired().HasMaxLength(200);
            builder.Property(u => u.PasswordHash).IsRequired().HasMaxLength(256);
            builder.Property(u => u.PasswordSalt).IsRequired().HasMaxLength(128);

            builder.HasIndex(u => u.Username).IsUnique();
            builder.HasIndex(u => u.Email).IsUnique();
        });

        // Role
        modelBuilder.Entity<Role>(builder =>
        {
            builder.ToTable("Roles");
            builder.HasKey(r => r.Id);
            builder.Property(r => r.Name).IsRequired().HasMaxLength(50);
            builder.Property(r => r.Description).HasMaxLength(250);

            builder.HasIndex(r => r.Name).IsUnique();
        });

        // Permission
        modelBuilder.Entity<Permission>(builder =>
        {
            builder.ToTable("Permissions");
            builder.HasKey(p => p.Id);
            builder.Property(p => p.Code).IsRequired().HasMaxLength(100);
            builder.Property(p => p.Description).IsRequired().HasMaxLength(250);
            builder.Property(p => p.Module).IsRequired().HasMaxLength(50);

            builder.HasIndex(p => p.Code).IsUnique();
        });

        // UserRole (Many-to-Many join table)
        modelBuilder.Entity<UserRole>(builder =>
        {
            builder.ToTable("UserRoles");
            builder.HasKey(ur => new { ur.UserId, ur.RoleId });

            builder.HasOne(ur => ur.User)
                .WithMany(u => u.UserRoles)
                .HasForeignKey(ur => ur.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(ur => ur.Role)
                .WithMany(r => r.UserRoles)
                .HasForeignKey(ur => ur.RoleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // RolePermission (Many-to-Many join table)
        modelBuilder.Entity<RolePermission>(builder =>
        {
            builder.ToTable("RolePermissions");
            builder.HasKey(rp => new { rp.RoleId, rp.PermissionId });

            builder.HasOne(rp => rp.Role)
                .WithMany(r => r.RolePermissions)
                .HasForeignKey(rp => rp.RoleId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(rp => rp.Permission)
                .WithMany(p => p.RolePermissions)
                .HasForeignKey(rp => rp.PermissionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Trip
        modelBuilder.Entity<Trip>(builder =>
        {
            builder.ToTable("Trips");
            builder.HasKey(t => t.Id);

            builder.Property(t => t.PickupAddress).IsRequired().HasMaxLength(256);
            builder.Property(t => t.DestinationAddress).IsRequired().HasMaxLength(256);
            builder.Property(t => t.Currency).IsRequired().HasMaxLength(3).HasDefaultValue("VND");
            builder.Property(t => t.Status).HasConversion<short>();
            builder.Property(t => t.PaymentRequestKey).HasMaxLength(128);
            builder.Property(t => t.TransactionId).HasMaxLength(64);

            builder.Property(t => t.PickupLatitude).HasPrecision(9, 6);
            builder.Property(t => t.PickupLongitude).HasPrecision(9, 6);
            builder.Property(t => t.DestinationLatitude).HasPrecision(9, 6);
            builder.Property(t => t.DestinationLongitude).HasPrecision(9, 6);
            builder.Property(t => t.EstimatedPrice).HasPrecision(18, 2);

            // Optimistic lock: driver-accept/pickup đồng thời chỉ một request thắng.
            builder.Property(t => t.RowVersion).IsRowVersion();

            builder.HasIndex(t => new { t.RiderId, t.CreatedAtUtc });
            builder.HasIndex(t => new { t.DriverId, t.Status });

            // Một nghĩa vụ thanh toán duy nhất cho mỗi trip.
            builder.HasIndex(t => t.PaymentRequestKey).IsUnique()
                .HasFilter("[PaymentRequestKey] IS NOT NULL");
        });

        // TripStatusHistory (append-only)
        modelBuilder.Entity<TripStatusHistory>(builder =>
        {
            builder.ToTable("TripStatusHistories");
            builder.HasKey(h => h.Id);

            builder.Property(h => h.FromStatus).HasConversion<short>();
            builder.Property(h => h.ToStatus).HasConversion<short>();
            builder.Property(h => h.ActorType).IsRequired().HasMaxLength(16);
            builder.Property(h => h.Reason).HasMaxLength(256);

            builder.HasOne(h => h.Trip)
                .WithMany()
                .HasForeignKey(h => h.TripId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasIndex(h => h.TripId);
        });

        // Outbox intent
        modelBuilder.Entity<OutboxMessage>(builder =>
        {
            builder.ToTable("TripOutboxMessages");
            builder.HasKey(m => m.Id);

            builder.Property(m => m.EventType).IsRequired().HasMaxLength(128);
            builder.Property(m => m.Payload).IsRequired();
            builder.Property(m => m.LastError).HasMaxLength(1024);

            builder.HasIndex(m => m.MessageId).IsUnique();
            builder.HasIndex(m => m.OccurredAtUtc)
                .HasFilter("[DeliveredAtUtc] IS NULL");
        });

        // Inbox dedup theo khóa nghiệp vụ
        modelBuilder.Entity<ProcessedEvent>(builder =>
        {
            builder.ToTable("ProcessedEvents");
            builder.HasKey(e => new { e.ConsumerName, e.BusinessKey });

            builder.Property(e => e.ConsumerName).HasMaxLength(64);
            builder.Property(e => e.BusinessKey).HasMaxLength(160);
        });
    }
}
