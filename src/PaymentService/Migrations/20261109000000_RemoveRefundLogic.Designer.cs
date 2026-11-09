using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PaymentService.Infrastructure.Persistence;

#nullable disable

namespace PaymentService.Migrations
{
    [DbContext(typeof(PaymentDbContext))]
    [Migration("20261109000000_RemoveRefundLogic")]
    partial class RemoveRefundLogic
    {
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "10.0.0")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            modelBuilder.Entity("PaymentService.Domain.Entities.OutboxMessage", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid");

                    b.Property<string>("EventType")
                        .IsRequired()
                        .HasMaxLength(128);

                    b.Property<string>("Payload")
                        .IsRequired();

                    b.Property<string>("LastError")
                        .HasMaxLength(1024);

                    b.Property<DateTime>("OccurredAtUtc")
                        .HasColumnType("timestamp with time zone");

                    b.Property<DateTime?>("DeliveredAtUtc")
                        .HasColumnType("timestamp with time zone");

                    b.HasKey("Id");

                    b.HasIndex("MessageId")
                        .IsUnique();

                    b.HasIndex("OccurredAtUtc")
                        .HasFilter("\"DeliveredAtUtc\" IS NULL");

                    b.ToTable("PaymentOutboxMessages");
                });

            modelBuilder.Entity("PaymentService.Domain.Entities.Payment", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid");

                    b.Property<Guid>("TripId")
                        .HasColumnType("uuid");

                    b.Property<string>("PaymentRequestKey")
                        .IsRequired()
                        .HasMaxLength(128);

                    b.Property<Guid>("RiderId")
                        .HasColumnType("uuid");

                    b.Property<Guid?>("DriverId")
                        .HasColumnType("uuid");

                    b.Property<decimal>("Amount")
                        .HasPrecision(18, 2)
                        .HasColumnType("numeric(18,2)");

                    b.Property<string>("Currency")
                        .IsRequired()
                        .HasMaxLength(3);

                    b.Property<short>("Method")
                        .HasColumnType("smallint");

                    b.Property<short>("Status")
                        .HasColumnType("smallint");

                    b.Property<string>("GatewayPartnerCode")
                        .HasMaxLength(64);

                    b.Property<string>("GatewayOrderRef")
                        .HasMaxLength(64);

                    b.Property<string>("GatewayTransId")
                        .HasMaxLength(64);

                    b.Property<string>("SignatureChecksum")
                        .HasMaxLength(128);

                    b.Property<string>("FailureCode")
                        .HasMaxLength(48);

                    b.Property<string>("FailureMessage")
                        .HasMaxLength(256);

                    b.Property<string>("CorrelationId")
                        .HasMaxLength(64);

                    b.Property<int>("AttemptCount")
                        .HasColumnType("integer");

                    b.Property<Guid?>("SettledEventId")
                        .HasColumnType("uuid");

                    b.Property<DateTime>("CreatedAtUtc")
                        .HasColumnType("timestamp with time zone");

                    b.Property<DateTime>("UpdatedAtUtc")
                        .HasColumnType("timestamp with time zone");

                    b.Property<DateTime?>("SettledAtUtc")
                        .HasColumnType("timestamp with time zone");

                    b.Property<DateTime?>("FailedAtUtc")
                        .HasColumnType("timestamp with time zone");

                    b.HasKey("Id");

                    b.HasIndex("PaymentRequestKey")
                        .IsUnique();

                    b.HasIndex("RiderId", "CreatedAtUtc");

                    b.HasIndex("Status", "CreatedAtUtc");

                    b.HasIndex("Status", "UpdatedAtUtc");

                    b.HasIndex("TripId")
                        .IsUnique();

                    b.ToTable("Payments", null, tb =>
                        {
                            tb.HasCheckConstraint("CK_Payments_Amount_Positive", "\"Amount\" > 0");
                            tb.HasCheckConstraint("CK_Payments_Method_Defined", "\"Method\" IN (1, 2)");
                            tb.HasCheckConstraint("CK_Payments_Status_Defined", "\"Status\" IN (1, 2, 3, 4, 5, 8)");
                        });
                });

            modelBuilder.Entity("PaymentService.Domain.Entities.ProcessedEvent", b =>
                {
                    b.Property<string>("ConsumerName")
                        .HasMaxLength(64);

                    b.Property<string>("BusinessKey")
                        .HasMaxLength(160);

                    b.Property<Guid>("MessageId")
                        .HasColumnType("uuid");

                    b.Property<DateTime>("ProcessedAtUtc")
                        .HasColumnType("timestamp with time zone");

                    b.HasKey("ConsumerName", "BusinessKey");

                    b.ToTable("ProcessedEvents");
                });
#pragma warning restore 612, 618
        }
    }
}