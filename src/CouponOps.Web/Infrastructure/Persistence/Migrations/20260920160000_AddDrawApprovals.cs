using System;
using CouponOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CouponOps.Web.Infrastructure.Persistence.Migrations
{
    /// <summary>확률 변경의 2인 승인(maker-checker) 기록.</summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260920160000_AddDrawApprovals")]
    public partial class AddDrawApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DrawApprovals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DrawEventId = table.Column<long>(type: "bigint", nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BaseWeightVersionId = table.Column<long>(type: "bigint", nullable: true),
                    RequestedByAdminId = table.Column<long>(type: "bigint", nullable: false),
                    RequestedByLoginId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RequestReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    DecidedByAdminId = table.Column<long>(type: "bigint", nullable: true),
                    DecidedByLoginId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DrawApprovals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DrawApprovals_DrawEvents_DrawEventId",
                        column: x => x.DrawEventId,
                        principalTable: "DrawEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DrawApprovals_DrawEventId_Status",
                table: "DrawApprovals",
                columns: new[] { "DrawEventId", "Status" },
                filter: "[Status] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_DrawApprovals_RequestedAt",
                table: "DrawApprovals",
                column: "RequestedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "DrawApprovals");
        }
    }
}
