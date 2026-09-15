using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CouponOps.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CouponOps.Tests;

/// <summary>
/// 헬스체크는 배포 전환의 게이트다. 여기가 거짓말하면 무중단 배포가 중단 배포가 된다.
/// 그래서 "정상일 때 200" 뿐 아니라 <b>의존성이 죽었을 때 503 을 주는지</b>도 확인한다.
/// </summary>
[Collection(CouponOpsCollection.Name)]
public sealed class HealthEndpointTests(CouponOpsFixture fx)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact(DisplayName = "liveness 는 의존성과 무관하게 200 이다")]
    public async Task Liveness_does_not_depend_on_dependencies()
    {
        // 죽은 Redis·DB 를 가리키는 앱이라도 프로세스는 살아 있다.
        // 여기서 503 을 주면 오케스트레이터가 멀쩡한 앱을 계속 재시작시킨다.
        using var broken = BrokenDependencies();
        var res = await broken.CreateClient().GetAsync("/health/live");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact(DisplayName = "readiness 는 Redis·MSSQL 을 각각 확인하고 200 을 준다")]
    public async Task Readiness_reports_each_dependency()
    {
        var res = await fx.App.CreateClient().GetAsync("/health/ready");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Json);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("status").GetString().Should().Be("healthy");

        var checks = body.GetProperty("checks");
        checks.GetProperty("mssql").GetProperty("status").GetString().Should().Be("healthy");
        checks.GetProperty("redis").GetProperty("status").GetString().Should().Be("healthy");

        // 각 검사의 소요 시간이 보여야 한다 — 느려지는 것을 알아채려면 필요하다.
        checks.GetProperty("mssql").GetProperty("durationMs").GetInt64().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact(DisplayName = "의존성이 끊기면 503 과 함께 어느 쪽이 문제인지 알려준다")]
    public async Task Readiness_fails_and_names_the_broken_dependency()
    {
        using var broken = BrokenDependencies();

        var res = await broken.CreateClient().GetAsync("/health/ready");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Json);

        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "배포 스크립트와 로드밸런서가 이 코드로 판단한다");
        body.GetProperty("status").GetString().Should().Be("unhealthy");

        var checks = body.GetProperty("checks");
        checks.GetProperty("redis").GetProperty("status").GetString().Should().Be("unhealthy");
        checks.GetProperty("mssql").GetProperty("status").GetString().Should().Be("unhealthy");

        // 원인이 비어 있으면 새벽에 호출받은 사람이 아무것도 못 한다.
        checks.GetProperty("redis").GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact(DisplayName = "/health 는 readiness 의 별칭이다")]
    public async Task Health_alias_matches_readiness()
    {
        var res = await fx.App.CreateClient().GetAsync("/health");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>닿을 수 없는 Redis·MSSQL 을 가리키는 앱. 공유 컨테이너는 건드리지 않는다.</summary>
    private static WebApplicationFactory<Program> BrokenDependencies() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            // 포트 1 은 연결이 즉시 거부된다 — 타임아웃을 기다릴 필요가 없다.
            .UseSetting("ConnectionStrings:Redis", "127.0.0.1:1,abortConnect=false,connectTimeout=300")
            .UseSetting("ConnectionStrings:SqlServer",
                "Server=127.0.0.1,1;Database=Nope;User Id=sa;Password=nope;TrustServerCertificate=True;Connect Timeout=1")
            .UseSetting("Logging:LogLevel:Default", "None"));
}
