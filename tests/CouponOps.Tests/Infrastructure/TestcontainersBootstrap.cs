using System.Runtime.CompilerServices;

namespace CouponOps.Tests.Infrastructure;

internal static class TestcontainersBootstrap
{
    /// <summary>
    /// Testcontainers 설정을 런타임이 읽기 전에 맞춰 둔다.
    /// </summary>
    /// <remarks>
    /// 이 저장소의 개발 컨테이너는 Docker Hub 의 이미지 blob 다운로드가 막혀 있다.
    /// 그래서 (1) Docker Hub 에 있는 Ryuk(리소스 정리 컨테이너)을 끄고,
    /// (2) MSSQL·Redis 이미지를 환경변수로 갈아끼운다.
    /// 환경변수가 없으면 표준 이미지를 그대로 쓰므로, 이 우회가 코드에 고착되지 않는다.
    /// </remarks>
    [ModuleInitializer]
    internal static void Init()
    {
        if (Environment.GetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED") is null)
            Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");
    }

    public static string MsSqlImage =>
        Environment.GetEnvironmentVariable("COUPONOPS_TEST_MSSQL_IMAGE")
        ?? "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";

    public static string RedisImage =>
        Environment.GetEnvironmentVariable("COUPONOPS_TEST_REDIS_IMAGE")
        ?? "redis:7-alpine";
}
