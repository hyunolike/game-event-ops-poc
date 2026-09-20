using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CouponOps.Application;

/// <summary>
/// 로컬/데모 환경에서 운영툴에 로그인할 계정을 만든다.
/// </summary>
/// <remarks>
/// 계정이 하나라도 있으면 아무것도 하지 않는다. 실제 운영에서는 이 시드를 끄고
/// 계정을 별도 절차로 만든다 — 기본 비밀번호가 배포에 섞여 들어가면 안 된다.
/// </remarks>
public static class AdminUserSeeder
{
    public static async Task SeedAsync(IServiceProvider services, string seedPassword, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (await db.AdminUsers.AnyAsync(ct)) return;

        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AdminUser>>();
        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;

        AdminUser Make(string loginId, AdminRole role)
        {
            var user = AdminUser.Create(loginId, "", role, now);
            return AdminUser.Create(loginId, hasher.HashPassword(user, seedPassword), role, now);
        }

        // 편집자가 둘이다. 2인 승인(maker-checker)은 등록자와 다른 편집자가 있어야 성립하는데,
        // 계정이 하나뿐이면 그 절차를 켜 봤자 아무도 승인할 수 없다.
        db.AdminUsers.AddRange(
            Make("admin", AdminRole.Editor),
            Make("admin2", AdminRole.Editor),
            Make("viewer", AdminRole.Viewer));
        await db.SaveChangesAsync(ct);
    }
}
