using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CouponOps.Infrastructure.Persistence;

/// <summary>
/// 마이그레이션 생성 전용. 이것이 없으면 EF 도구가 Program.cs 의 호스트를 띄우려 하고,
/// Minimal API 의 app.Run() 에서 멈춘다. 실제 실행 경로와는 무관하다.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=localhost,1433;Database=CouponOps;User Id=sa;Password=placeholder;TrustServerCertificate=True")
            .Options;

        return new AppDbContext(options);
    }
}
