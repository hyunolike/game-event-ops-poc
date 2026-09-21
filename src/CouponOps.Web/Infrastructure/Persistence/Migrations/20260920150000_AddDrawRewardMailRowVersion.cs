using CouponOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CouponOps.Web.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// 보상 우편에 낙관적 동시성 토큰을 붙인다.
    /// 같은 우편을 동시에 여러 번 수령해도 정확히 한 번만 성공해야 하고,
    /// 그 규칙을 <c>DrawRewardMail.TryClaim</c> 한 곳에만 두기 위한 장치다.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260920150000_AddDrawRewardMailRowVersion")]
    public partial class AddDrawRewardMailRowVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "DrawRewardMails",
                type: "rowversion",
                // rowversion 은 SQL Server 가 자동으로 채우므로 기존 행이 있어도 NOT NULL 로 추가할 수 있다.
                // 모델이 IsRequired 이므로 여기서도 맞춰 둔다 — 어긋나면 다음 마이그레이션이 컬럼을 고치려 든다.
                rowVersion: true,
                nullable: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "DrawRewardMails");
        }
    }
}
