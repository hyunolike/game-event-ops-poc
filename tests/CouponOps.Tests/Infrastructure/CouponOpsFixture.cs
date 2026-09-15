using CouponOps.Infrastructure.Persistence;
using DotNet.Testcontainers.Builders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Testcontainers.MsSql;
using Testcontainers.Redis;

namespace CouponOps.Tests.Infrastructure;

/// <summary>
/// MSSQL + Redis 컨테이너를 띄우고 실제 앱(WebApplicationFactory)을 그 위에 연결한다.
/// 컨테이너 기동이 느리므로 테스트 클래스 전체가 하나를 공유한다.
/// </summary>
public sealed class CouponOpsFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder()
        .WithImage(TestcontainersBootstrap.MsSqlImage)
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage(TestcontainersBootstrap.RedisImage)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(6379))
        .Build();

    private WebApplicationFactory<Program>? _app;

    public WebApplicationFactory<Program> App => _app!;
    public string SqlConnectionString { get; private set; } = "";
    public string RedisConnectionString { get; private set; } = "";
    public IConnectionMultiplexer Redis { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_sql.StartAsync(), _redis.StartAsync());

        // MsSql 모듈은 master 를 가리킨다. 애플리케이션 DB 로 바꾼다.
        // Max Pool Size 를 넉넉히 둔다. DB 경로는 트랜잭션 내내 커넥션을 점유하므로
        // 기본값 100 이면 잠금 동작을 재기도 전에 풀이 먼저 바닥난다.
        SqlConnectionString = _sql.GetConnectionString()
            .Replace("Database=master", "Database=CouponOps") + ";Max Pool Size=200";
        RedisConnectionString = _redis.GetConnectionString();

        // 앱을 띄우기 전에 스키마를 만든다. 적재 워커가 기동 직후 없는 테이블을 조회하지 않도록.
        await MigrateAsync();

        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:SqlServer", SqlConnectionString)
             .UseSetting("ConnectionStrings:Redis", RedisConnectionString)
             // 테스트는 적재 지연을 기다린다. 기본 200ms 대신 짧게 잡아 대기 시간을 줄인다.
             .UseSetting("Issuance:PersistencePollInterval", "00:00:00.050")
             .UseSetting("Issuance:CodeSecret", "test-secret")
             .UseSetting("Admin:SeedPassword", AdminClient.SeedPassword)
             // EF 의 SQL 로그가 테스트 출력을 덮지 않도록 낮춘다.
             .UseSetting("Logging:LogLevel:Default", "Warning")
             .UseSetting("Logging:LogLevel:Microsoft.EntityFrameworkCore", "Warning"));

        // WebApplicationFactory 는 첫 요청 전까지 호스트를 만들지 않는다.
        // 적재 워커를 미리 돌리기 위해 여기서 강제로 기동한다.
        _ = _app.Services;

        Redis = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString);
    }

    private async Task MigrateAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(SqlConnectionString)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync();
    }

    /// <summary>앱의 DI 에서 스코프를 하나 빌려 준다(직접 DB 를 확인할 때 사용).</summary>
    public IServiceScope CreateScope() => App.Services.CreateScope();

    /// <summary>
    /// 이벤트의 이력이 기대 건수만큼 적재될 때까지 기다린 뒤 IssuePath 집합을 돌려준다.
    /// Redis 경로는 비동기 적재라 즉시 보이지 않는다.
    /// </summary>
    public async Task<List<string>> WaitForIssuePathsAsync(long eventId, int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var scope = CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var paths = await db.IssuanceLogs.AsNoTracking()
                .Where(l => l.EventId == eventId)
                .Select(l => l.IssuePath)
                .Distinct()
                .ToListAsync();

            if (paths.Count >= expected) return paths;
            await Task.Delay(100);
        }

        throw new TimeoutException($"이벤트 {eventId} 의 이력 경로 {expected}종이 적재되지 않았습니다.");
    }

    public async Task DisposeAsync()
    {
        if (Redis is not null) await Redis.CloseAsync();
        _app?.Dispose();
        await Task.WhenAll(_sql.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }
}

[CollectionDefinition(Name)]
public sealed class CouponOpsCollection : ICollectionFixture<CouponOpsFixture>
{
    public const string Name = "couponops";
}
