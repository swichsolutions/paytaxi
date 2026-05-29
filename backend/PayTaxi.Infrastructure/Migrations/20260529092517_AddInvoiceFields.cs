using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PayTaxi.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "InvoiceNumberSeq",
                startValue: 1000000L);

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                table: "Parks",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "InvoiceNumber",
                table: "Cashouts",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Cashouts_InvoiceNumber",
                table: "Cashouts",
                column: "InvoiceNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Cashouts_InvoiceNumber",
                table: "Cashouts");

            migrationBuilder.DropColumn(
                name: "Phone",
                table: "Parks");

            migrationBuilder.DropColumn(
                name: "InvoiceNumber",
                table: "Cashouts");

            migrationBuilder.DropSequence(
                name: "InvoiceNumberSeq");
        }
    }
}
