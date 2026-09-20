using CouponOps.Api;
using CouponOps.Application;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using CouponOps.Infrastructure.Redis;
using CouponOps.Infrastructure.Workers;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.WebEncoders;
using StackExchange.Redis;

// 컨테이너 HEALTHCHECK 진입점. 이미지에 curl 을 넣지 않기 위해 앱 자신이 프로브가 된다.
if (args is ["--healthcheck", ..])
    return await HealthProbe.RunAsync(
        args.ElementAtOrDefault(1) ?? "http://127.0.0.1:8080/health/live");

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<IssuanceOptions>(
    builder.Configuration.GetSection(IssuanceOptions.SectionName));

builder.Services.Configure<DrawOptions>(
    builder.Configuration.GetSection(DrawOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);

// 기본 HtmlEncoder 는 비 ASCII 문자를 전부 숫자 참조(&#xC774;)로 바꾼다.
// 한글 페이지에서는 응답 크기가 몇 배로 늘고 HTML 을 사람이 읽을 수 없게 된다.
// 범위를 넓혀도 <, >, &, ", ' 이스케이프는 그대로라 XSS 방어에는 영향이 없다.
builder.Services.Configure<WebEncoderOptions>(o =>
    o.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));
builder.Services.AddHttpContextAccessor();

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
builder.Services.AddSingleton<DrawScript>();
builder.Services.AddSingleton<IDrawStore, RedisDrawStore>();
// 추첨 hot path 가 경품명·리셋 시각 때문에 매번 DB 를 읽지 않도록 하는 캐시.
// 판정에 쓰이는 값은 전부 Redis 에 있으므로, 이 캐시가 잠시 낡아도 정확성에는 영향이 없다.
builder.Services.AddSingleton<DrawMetaCache>();
builder.Services.AddSingleton<IPasswordHasher<AdminUser>, PasswordHasher<AdminUser>>();

builder.Services.AddScoped<ICurrentActor, HttpCurrentActor>();
builder.Services.AddScoped<IAuditLogger, AuditLogger>();
builder.Services.AddScoped<IssueCouponService>();
builder.Services.AddScoped<DbIssueCouponService>();   // 4단계 비교 측정용 대조군
builder.Services.AddScoped<EventAdminService>();
builder.Services.AddScoped<SpinDrawService>();
builder.Services.AddScoped<DrawAdminService>();
builder.Services.AddScoped<DrawMailService>();
builder.Services.AddScoped<DrawApprovalService>();

builder.Services.AddHostedService<IssuancePersistenceWorker>();
builder.Services.AddHostedService<DrawPersistenceWorker>();
builder.Services.AddHostedService<AdminSeedWorker>();

// ── 인증: 운영툴 전용 쿠키 ────────────────────────────────────────────────────
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Account/Login";
        o.AccessDeniedPath = "/Account/Denied";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);   // 운영자 한 근무 교대
        o.SlidingExpiration = true;
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;    // 운영툴에 외부 사이트발 요청이 올 이유가 없다
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });

builder.Services.AddAuthorizationBuilder()
    // 읽기전용(Viewer)은 조회만, 편집(Editor)만 변경 액션에 접근할 수 있다.
    .AddPolicy(Policies.Editor, p => p.RequireRole(nameof(AdminRole.Editor)));

builder.Services.AddRazorPages(o =>
{
    // 기본값이 "로그인 필요" 다. 새 페이지를 추가하면서 인증을 깜빡해도 열리지 않는다.
    o.Conventions.AuthorizeFolder("/");
    o.Conventions.AllowAnonymousToPage("/Account/Login");
    o.Conventions.AllowAnonymousToPage("/Account/Denied");
    // 확률 공시는 유저에게 보이는 페이지다. 로그인 뒤에 두면 공시의 의미가 없다.
    o.Conventions.AllowAnonymousToPage("/Odds");
});

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

// 유저 대상 발급 API 는 운영툴 인증과 무관하다(게임 클라이언트가 호출한다).
app.MapIssueEndpoints();
app.MapIssueDbEndpoints();
app.MapEventStatusEndpoints();
app.MapDrawEndpoints();
app.MapDrawStatusEndpoints();
app.MapDrawMailEndpoints();
app.MapHealthEndpoints();

app.Run();
return 0;

/// <summary>통합 테스트의 WebApplicationFactory 가 진입점을 잡을 수 있도록 공개한다.</summary>
public partial class Program;
