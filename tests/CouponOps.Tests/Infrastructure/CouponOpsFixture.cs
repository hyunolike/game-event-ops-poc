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
        SqlConnectionString = _sql.GetConnectionString().Replace("Database=master", "Database=CouponOps");
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
