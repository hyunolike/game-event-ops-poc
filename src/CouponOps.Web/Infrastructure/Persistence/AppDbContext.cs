using CouponOps.Domain;
using Microsoft.EntityFrameworkCore;

namespace CouponOps.Infrastructure.Persistence;

/// <summary>
/// 인덱스 설계 근거는 docs/01-domain-design.md 6장에 정리돼 있다.
/// 여기서는 "왜 이 인덱스인가"만 짧게 남기고, 트레이드오프는 문서를 참조한다.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<CouponEvent> Events => Set<CouponEvent>();
    public DbSet<Coupon> Coupons => Set<Coupon>();
    public DbSet<IssuanceLog> IssuanceLogs => Set<IssuanceLog>();
    public DbSet<OperationLog> OperationLogs => Set<OperationLog>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<CouponEvent>(e =>
        {
            e.ToTable("Events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.SuspendReason).HasMaxLength(500);
            e.Property(x => x.StartsAt).HasColumnType("datetime2(3)");
            e.Property(x => x.EndsAt).HasColumnType("datetime2(3)");
            e.Property(x => x.SuspendedAt).HasColumnType("datetime2(3)");
            e.Property(x => x.PoolWarmedAt).HasColumnType("datetime2(3)");
            e.Property(x => x.CreatedAt).HasColumnType("datetime2(3)");
            e.Property(x => x.UpdatedAt).HasColumnType("datetime2(3)");
            e.Property(x => x.RowVersion).IsRowVersion();

            // 외부 노출 슬러그 — 중복 생성 방지
            e.HasIndex(x => x.Code).IsUnique();
            // 운영툴 목록: 상태 필터 + 최신순. 파생 상태가 이 세 컬럼의 함수라 커버링이 된다.
            e.HasIndex(x => new { x.SuspendedAt, x.StartsAt });
        });

        b.Entity<Coupon>(e =>
        {
            e.ToTable("Coupons");
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(64).IsRequired();
            e.Property(x => x.IssuedToUserId).HasMaxLength(64);
            e.Property(x => x.RevokeReason).HasMaxLength(500);
            e.Property(x => x.IssuedAt).HasColumnType("datetime2(3)");
            e.Property(x => x.UsedAt).HasColumnType("datetime2(3)");
            e.Property(x => x.RevokedAt).HasColumnType("datetime2(3)");
            e.Property(x => x.RowVersion).IsRowVersion();

            e.HasOne<CouponEvent>().WithMany()
             .HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.NoAction);

            // 코드 유일성의 최종 방어선. 사용(use) API 가 코드만으로 O(1) 조회한다.
            e.HasIndex(x => x.Code).IsUnique();
            // Redis 워밍업이 미발급분만 range scan 하도록. 발급될수록 인덱스가 작아진다.
            e.HasIndex(x => new { x.EventId, x.Status })
             .HasFilter("[Status] = 0");
            // CS 대응: "이 유저가 이 이벤트에서 받은 쿠폰". 미발급 행(대다수)을 제외한다.
            e.HasIndex(x => new { x.EventId, x.IssuedToUserId })
             .HasFilter("[IssuedToUserId] IS NOT NULL");
        });

        b.Entity<IssuanceLog>(e =>
        {
            e.ToTable("IssuanceLogs");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(64).IsRequired();
            e.Property(x => x.CouponCode).HasMaxLength(64);
            e.Property(x => x.IssuePath).HasMaxLength(16).IsRequired();
            e.Property(x => x.FailureDetail).HasMaxLength(500);
            e.Property(x => x.RequestedAt).HasColumnType("datetime2(3)");
            e.Property(x => x.PersistedAt).HasColumnType("datetime2(3)");

            e.HasOne<CouponEvent>().WithMany()
             .HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<Coupon>().WithMany()
             .HasForeignKey(x => x.CouponId).OnDelete(DeleteBehavior.NoAction);

            // 비동기 적재의 핵심. 스트림 소비는 at-least-once 라 재처리가 정상 동작이고,
            // 중복 행은 이 제약으로 막는다.
            e.HasIndex(x => x.RequestId).IsUnique();
            // 운영툴 이력 조회(이벤트 + 기간 + 페이징)의 주 경로. INCLUDE 로 키 조회를 없앤다.
            e.HasIndex(x => new { x.EventId, x.RequestedAt })
             .IncludeProperties(x => new { x.UserId, x.Result, x.CouponCode });
            // "이 유저가 언제 뭘 받았나" — CS 문의 대응
            e.HasIndex(x => new { x.UserId, x.RequestedAt });
            // 실패 사유 분석 전용. 소진 후 실패가 폭증하므로 필터드로 분리해 두는 편이 이득.
            e.HasIndex(x => new { x.EventId, x.Result, x.RequestedAt })
             .HasFilter("[Result] <> 1");
        });

        b.Entity<OperationLog>(e =>
        {
            e.ToTable("OperationLogs");
            e.HasKey(x => x.Id);
            e.Property(x => x.ActorLoginId).HasMaxLength(64).IsRequired();
            e.Property(x => x.TargetType).HasMaxLength(64).IsRequired();
            e.Property(x => x.TargetId).HasMaxLength(64).IsRequired();
            e.Property(x => x.ChangedFields).HasMaxLength(1000);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.ClientIp).HasMaxLength(64);
            e.Property(x => x.OccurredAt).HasColumnType("datetime2(3)");

            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => new { x.ActorId, x.OccurredAt });
            e.HasIndex(x => new { x.TargetType, x.TargetId, x.OccurredAt });
        });

        b.Entity<AdminUser>(e =>
        {
            e.ToTable("AdminUsers");
            e.HasKey(x => x.Id);
            e.Property(x => x.LoginId).HasMaxLength(64).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(256).IsRequired();
            e.Property(x => x.CreatedAt).HasColumnType("datetime2(3)");
            e.HasIndex(x => x.LoginId).IsUnique();
        });
    }
}
