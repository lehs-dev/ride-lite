using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentService.Migrations
{
    /// <inheritdoc />
    public partial class PaymentValidationConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOT VALID rồi mới VALIDATE: bước ADD không chặn ghi và fail ngay nếu có dòng rác,
            // còn bước VALIDATE khóa ràng buộc cho các lần ghi về sau mà không giữ lock dài trên
            // bảng đã có dữ liệu.
            migrationBuilder.Sql(
                """
                ALTER TABLE "Payments"
                    ADD CONSTRAINT "CK_Payments_Method_Defined"
                    CHECK ("Method" IN (1, 2)) NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "Payments"
                    ADD CONSTRAINT "CK_Payments_Status_Defined"
                    CHECK ("Status" BETWEEN 1 AND 8) NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "PaymentRefunds"
                    ADD CONSTRAINT "CK_PaymentRefunds_Amount_Positive"
                    CHECK ("Amount" > 0) NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "PaymentRefunds"
                    ADD CONSTRAINT "CK_PaymentRefunds_Attempt_Count_Bounded"
                    CHECK ("AttemptCount" >= 0 AND "AttemptCount" <= 3) NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "PaymentRefunds"
                    ADD CONSTRAINT "CK_PaymentRefunds_Status_Defined"
                    CHECK ("Status" BETWEEN 1 AND 4) NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "PaymentRefunds"
                    ADD CONSTRAINT "CK_PaymentRefunds_Completed_Not_Before_Created"
                    CHECK ("CompletedAtUtc" IS NULL OR "CompletedAtUtc" >= "CreatedAtUtc") NOT VALID;
                """);

            // Sẽ throw DbException nếu dữ liệu hiện hữu vi phạm — service vẫn boot (Program.cs
            // catch + log) và /health/ready báo đỏ cho tới khi data được sửa.
            migrationBuilder.Sql(
                """
                ALTER TABLE "Payments" VALIDATE CONSTRAINT "CK_Payments_Method_Defined";
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "Payments" VALIDATE CONSTRAINT "CK_Payments_Status_Defined";
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "PaymentRefunds" VALIDATE CONSTRAINT "CK_PaymentRefunds_Amount_Positive";
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "PaymentRefunds" VALIDATE CONSTRAINT "CK_PaymentRefunds_Attempt_Count_Bounded";
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "PaymentRefunds" VALIDATE CONSTRAINT "CK_PaymentRefunds_Status_Defined";
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "PaymentRefunds" VALIDATE CONSTRAINT "CK_PaymentRefunds_Completed_Not_Before_Created";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Payments_Method_Defined",
                table: "Payments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Payments_Status_Defined",
                table: "Payments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PaymentRefunds_Amount_Positive",
                table: "PaymentRefunds");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PaymentRefunds_Attempt_Count_Bounded",
                table: "PaymentRefunds");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PaymentRefunds_Status_Defined",
                table: "PaymentRefunds");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PaymentRefunds_Completed_Not_Before_Created",
                table: "PaymentRefunds");
        }
    }
}
