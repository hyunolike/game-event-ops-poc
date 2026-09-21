using CouponOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CouponOps.Web.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// 추첨 이력에 클라이언트 IP 를 남긴다. 이상 탐지의 다계정 축에 쓴다.
    /// 잭팟 당첨 조회 인덱스에 UserId·ClientIp 를 INCLUDE 해 그 질의가 키 조회를 하지 않게 한다.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260921010000_AddDrawLogClientIp")]
    public partial class AddDrawLogClientIp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DrawLogs_DrawEventId_PrizeId_RequestedAt",
                table: "DrawLogs");

            migrationBuilder.AddColumn<string>(
                name: "ClientIp",
                table: "DrawLogs",
                type: "nvarchar(45)",
                maxLength: 45,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DrawLogs_DrawEventId_PrizeId_RequestedAt",
                table: "DrawLogs",
                columns: new[] { "DrawEventId", "PrizeId", "RequestedAt" },
                filter: "[Result] = 1")
                .Annotation("SqlServer:Include", new[] { "UserId", "ClientIp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DrawLogs_DrawEventId_PrizeId_RequestedAt",
                table: "DrawLogs");

            migrationBuilder.DropColumn(
                name: "ClientIp",
                table: "DrawLogs");

            migrationBuilder.CreateIndex(
                name: "IX_DrawLogs_DrawEventId_PrizeId_RequestedAt",
                table: "DrawLogs",
                columns: new[] { "DrawEventId", "PrizeId", "RequestedAt" },
                filter: "[Result] = 1");
        }
    }
}
