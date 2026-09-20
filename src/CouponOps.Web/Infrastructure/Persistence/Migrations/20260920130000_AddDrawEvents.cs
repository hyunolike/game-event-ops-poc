using System;
using CouponOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CouponOps.Web.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// 룰렛(확률 지급) 이벤트 스키마. 설계 근거는 docs/04-roulette-design.md.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260920130000_AddDrawEvents")]
    public partial class AddDrawEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DrawEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StartsAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    EndsAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    DailyDrawLimit = table.Column<int>(type: "int", nullable: false),
                    DailyResetAt = table.Column<TimeSpan>(type: "time", nullable: false),
                    TicketItemId = table.Column<long>(type: "bigint", nullable: true),
                    TicketCost = table.Column<int>(type: "int", nullable: false),
                    SoldOutPolicy = table.Column<byte>(type: "tinyint", nullable: false),
                    PityThreshold = table.Column<int>(type: "int", nullable: false),
                    PityPrizeId = table.Column<long>(type: "bigint", nullable: true),
                    ActiveWeightVersionId = table.Column<long>(type: "bigint", nullable: true),
                    SuspendedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    SuspendReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PoolWarmedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DrawEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DrawPrizes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DrawEventId = table.Column<long>(type: "bigint", nullable: false),
                    SlotIndex = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    ItemQty = table.Column<int>(type: "int", nullable: false),
                    Weight = table.Column<int>(type: "int", nullable: false),
                    InitialStock = table.Column<int>(type: "int", nullable: false),
                    // 자기 참조지만 FK 를 걸지 않는다. 같은 테이블 안의 순환 삭제 경로를
                    // SQL Server 가 거부하고, 대체 대상의 유효성은 도메인 검증이 이미 보증한다.
                    FallbackPrizeId = table.Column<long>(type: "bigint", nullable: true),
                    IsJackpot = table.Column<bool>(type: "bit", nullable: false),
                    IsBlank = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DrawPrizes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DrawPrizes_DrawEvents_DrawEventId",
                        column: x => x.DrawEventId,
                        principalTable: "DrawEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DrawWeightVersions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DrawEventId = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SnapshotHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TotalWeight = table.Column<int>(type: "int", nullable: false),
                    ActivatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    DeactivatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CreatedByAdminId = table.Column<long>(type: "bigint", nullable: false),
                    ChangeReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DrawWeightVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DrawWeightVersions_DrawEvents_DrawEventId",
                        column: x => x.DrawEventId,
                        principalTable: "DrawEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DrawLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DrawEventId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Result = table.Column<byte>(type: "tinyint", nullable: false),
                    WeightVersionId = table.Column<long>(type: "bigint", nullable: true),
                    RandomValue = table.Column<long>(type: "bigint", nullable: false),
                    TotalWeight = table.Column<int>(type: "int", nullable: false),
                    Roll = table.Column<int>(type: "int", nullable: false),
                    PrizeId = table.Column<long>(type: "bigint", nullable: true),
                    OriginalPrizeId = table.Column<long>(type: "bigint", nullable: true),
                    FallbackApplied = table.Column<bool>(type: "bit", nullable: false),
                    PityApplied = table.Column<bool>(type: "bit", nullable: false),
                    PrizeName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ItemId = table.Column<long>(type: "bigint", nullable: true),
                    ItemQty = table.Column<int>(type: "int", nullable: false),
                    TicketsSpent = table.Column<int>(type: "int", nullable: false),
                    PityCountAfter = table.Column<int>(type: "int", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    PersistedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    PersistenceLagMs = table.Column<int>(type: "int", nullable: false),
                    FailureDetail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DrawLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DrawLogs_DrawEvents_DrawEventId",
                        column: x => x.DrawEventId,
                        principalTable: "DrawEvents",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "DrawRewardMails",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DrawEventId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PrizeId = table.Column<long>(type: "bigint", nullable: false),
                    PrizeName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ItemId = table.Column<long>(type: "bigint", nullable: false),
                    ItemQty = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RevokeReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DrawRewardMails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DrawRewardMails_DrawEvents_DrawEventId",
                        column: x => x.DrawEventId,
                        principalTable: "DrawEvents",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_DrawEvents_Code",
                table: "DrawEvents",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DrawEvents_SuspendedAt_StartsAt",
                table: "DrawEvents",
                columns: new[] { "SuspendedAt", "StartsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DrawPrizes_DrawEventId_SlotIndex",
                table: "DrawPrizes",
                columns: new[] { "DrawEventId", "SlotIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DrawWeightVersions_DrawEventId_Version",
                table: "DrawWeightVersions",
                columns: new[] { "DrawEventId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DrawWeightVersions_DrawEventId_DeactivatedAt",
                table: "DrawWeightVersions",
                columns: new[] { "DrawEventId", "DeactivatedAt" },
                filter: "[DeactivatedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DrawLogs_RequestId",
                table: "DrawLogs",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DrawLogs_DrawEventId_RequestedAt",
                table: "DrawLogs",
                columns: new[] { "DrawEventId", "RequestedAt" })
                .Annotation("SqlServer:Include", new[] { "UserId", "Result", "PrizeName", "PrizeId" });

            migrationBuilder.CreateIndex(
                name: "IX_DrawLogs_UserId_RequestedAt",
                table: "DrawLogs",
                columns: new[] { "UserId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DrawLogs_DrawEventId_PrizeId_RequestedAt",
                table: "DrawLogs",
                columns: new[] { "DrawEventId", "PrizeId", "RequestedAt" },
                filter: "[Result] = 1");

            // FK 전용 인덱스. (UserId, DrawEventId) 는 선두 컬럼이 달라 FK 조회를 커버하지 못한다.
            migrationBuilder.CreateIndex(
                name: "IX_DrawRewardMails_DrawEventId",
                table: "DrawRewardMails",
                column: "DrawEventId");

            migrationBuilder.CreateIndex(
                name: "IX_DrawRewardMails_RequestId",
                table: "DrawRewardMails",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DrawRewardMails_UserId_DrawEventId",
                table: "DrawRewardMails",
                columns: new[] { "UserId", "DrawEventId" },
                filter: "[ClaimedAt] IS NULL AND [RevokedAt] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "DrawRewardMails");
            migrationBuilder.DropTable(name: "DrawLogs");
            migrationBuilder.DropTable(name: "DrawWeightVersions");
            migrationBuilder.DropTable(name: "DrawPrizes");
            migrationBuilder.DropTable(name: "DrawEvents");
        }
    }
}
