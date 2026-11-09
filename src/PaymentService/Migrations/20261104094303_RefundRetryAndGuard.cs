using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentService.Migrations
{
    /// <inheritdoc />
    public partial class RefundRetryAndGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaymentRefunds_PaymentId",
                table: "PaymentRefunds");

            migrationBuilder.DropIndex(
                name: "IX_PaymentRefunds_Status",
                table: "PaymentRefunds");

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "PaymentRefunds",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptAtUtc",
                table: "PaymentRefunds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Status_UpdatedAtUtc",
                table: "Payments",
                columns: new[] { "Status", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRefunds_PaymentId",
                table: "PaymentRefunds",
                column: "PaymentId",
                unique: true,
                filter: "\"Status\" <> 4");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRefunds_Status_CreatedAtUtc",
                table: "PaymentRefunds",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRefunds_Status_NextAttemptAtUtc",
                table: "PaymentRefunds",
                columns: new[] { "Status", "NextAttemptAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_Status_UpdatedAtUtc",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_PaymentRefunds_PaymentId",
                table: "PaymentRefunds");

            migrationBuilder.DropIndex(
                name: "IX_PaymentRefunds_Status_CreatedAtUtc",
                table: "PaymentRefunds");

            migrationBuilder.DropIndex(
                name: "IX_PaymentRefunds_Status_NextAttemptAtUtc",
                table: "PaymentRefunds");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "PaymentRefunds");

            migrationBuilder.DropColumn(
                name: "NextAttemptAtUtc",
                table: "PaymentRefunds");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRefunds_PaymentId",
                table: "PaymentRefunds",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRefunds_Status",
                table: "PaymentRefunds",
                column: "Status");
        }
    }
}
