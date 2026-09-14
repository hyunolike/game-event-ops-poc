using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CouponOps.Web.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminUsers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LoginId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Role = table.Column<byte>(type: "tinyint", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StartsAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    EndsAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    TotalQuantity = table.Column<int>(type: "int", nullable: false),
                    PerUserLimit = table.Column<int>(type: "int", nullable: false),
                    IssuanceMode = table.Column<byte>(type: "tinyint", nullable: false),
                    SuspendedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    SuspendReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PoolWarmedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OperationLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ActorId = table.Column<long>(type: "bigint", nullable: false),
                    ActorLoginId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Action = table.Column<byte>(type: "tinyint", nullable: false),
                    TargetType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TargetId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BeforeJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AfterJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ChangedFields = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ClientIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Coupons",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<long>(type: "bigint", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    IssuedToUserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IssuedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UsedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RevokeReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Coupons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Coupons_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "IssuanceLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Result = table.Column<byte>(type: "tinyint", nullable: false),
                    CouponId = table.Column<long>(type: "bigint", nullable: true),
                    CouponCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    PersistedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    PersistenceLagMs = table.Column<int>(type: "int", nullable: false),
                    IssuePath = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    FailureDetail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssuanceLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IssuanceLogs_Coupons_CouponId",
                        column: x => x.CouponId,
                        principalTable: "Coupons",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_IssuanceLogs_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminUsers_LoginId",
                table: "AdminUsers",
                column: "LoginId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Coupons_Code",
                table: "Coupons",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Coupons_EventId_IssuedToUserId",
                table: "Coupons",
                columns: new[] { "EventId", "IssuedToUserId" },
                filter: "[IssuedToUserId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Coupons_EventId_Status",
                table: "Coupons",
                columns: new[] { "EventId", "Status" },
                filter: "[Status] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Events_Code",
                table: "Events",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_SuspendedAt_StartsAt",
                table: "Events",
                columns: new[] { "SuspendedAt", "StartsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IssuanceLogs_CouponId",
                table: "IssuanceLogs",
                column: "CouponId");

            migrationBuilder.CreateIndex(
                name: "IX_IssuanceLogs_EventId_RequestedAt",
                table: "IssuanceLogs",
                columns: new[] { "EventId", "RequestedAt" })
                .Annotation("SqlServer:Include", new[] { "UserId", "Result", "CouponCode" });

            migrationBuilder.CreateIndex(
                name: "IX_IssuanceLogs_EventId_Result_RequestedAt",
                table: "IssuanceLogs",
                columns: new[] { "EventId", "Result", "RequestedAt" },
                filter: "[Result] <> 1");

            migrationBuilder.CreateIndex(
                name: "IX_IssuanceLogs_RequestId",
                table: "IssuanceLogs",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IssuanceLogs_UserId_RequestedAt",
                table: "IssuanceLogs",
                columns: new[] { "UserId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OperationLogs_ActorId_OccurredAt",
                table: "OperationLogs",
                columns: new[] { "ActorId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OperationLogs_OccurredAt",
                table: "OperationLogs",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_OperationLogs_TargetType_TargetId_OccurredAt",
                table: "OperationLogs",
                columns: new[] { "TargetType", "TargetId", "OccurredAt" });

            // IssuanceLogs 는 단조 증가 키를 가진 append-only 테이블이라 페이지 분할은 없지만,
            // 모든 삽입이 마지막 페이지로 몰려 래치 경합(PAGELATCH_EX)이 생긴다.
            // SQL Server 2019+ 의 이 옵션이 대기 중인 삽입의 순서를 조정해 경합을 완화한다.
            migrationBuilder.Sql(
                "ALTER INDEX [PK_IssuanceLogs] ON [IssuanceLogs] SET (OPTIMIZE_FOR_SEQUENTIAL_KEY = ON);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminUsers");

            migrationBuilder.DropTable(
                name: "IssuanceLogs");

            migrationBuilder.DropTable(
                name: "OperationLogs");

            migrationBuilder.DropTable(
                name: "Coupons");

            migrationBuilder.DropTable(
                name: "Events");
        }
    }
}
