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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

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
    }
}
