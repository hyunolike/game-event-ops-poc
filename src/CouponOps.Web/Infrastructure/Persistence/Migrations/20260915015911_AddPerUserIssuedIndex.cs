using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CouponOps.Web.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPerUserIssuedIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_IssuanceLogs_EventId_UserId_Issued",
                table: "IssuanceLogs",
                columns: new[] { "EventId", "UserId" },
                filter: "[Result] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IssuanceLogs_EventId_UserId_Issued",
                table: "IssuanceLogs");
        }
    }
}
