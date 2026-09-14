using CouponOps.Api;
using CouponOps.Application;
using CouponOps.Infrastructure.Persistence;
using CouponOps.Infrastructure.Redis;
using CouponOps.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<IssuanceOptions>(
    builder.Configuration.GetSection(IssuanceOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("SqlServer")));

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var options = ConfigurationOptions.Parse(
        builder.Configuration.GetConnectionString("Redis")
        ?? throw new InvalidOperationException("ConnectionStrings:Redis 설정이 없습니다."));

    // 기동 시 Redis 가 아직 안 떴다고 앱까지 죽이지는 않는다(컨테이너 기동 순서는 보장되지 않는다).
    // 대신 요청 처리 중 연결이 끊기면 예외가 빠르게 올라와 엔드포인트가 503 으로 거부한다 — fail-fast.
    options.AbortOnConnectFail = false;
    options.ConnectTimeout = 2000;
    options.SyncTimeout = 2000;

    return ConnectionMultiplexer.Connect(options);
});

builder.Services.AddSingleton<IssuanceScript>();
builder.Services.AddSingleton<IIssuanceStore, RedisIssuanceStore>();
builder.Services.AddScoped<IssueCouponService>();
builder.Services.AddScoped<EventProvisioningService>();
builder.Services.AddHostedService<IssuancePersistenceWorker>();

var app = builder.Build();

app.MapIssueEndpoints();

app.Run();

/// <summary>통합 테스트의 WebApplicationFactory 가 진입점을 잡을 수 있도록 공개한다.</summary>
public partial class Program;
