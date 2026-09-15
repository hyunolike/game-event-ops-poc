using CouponOps.Application;

namespace CouponOps.Infrastructure.Workers;

/// <summary>
/// 운영자 계정 시드를 백그라운드에서 재시도한다.
/// </summary>
/// <remarks>
/// 기동 경로에서 동기로 시드하면 <b>DB 가 아직 안 떴을 때 앱이 아예 못 뜬다.</b>
/// 컨테이너 배포에서는 앱이 DB 보다 먼저 기동되는 것이 예외가 아니라 일상이고,
/// 그때마다 컨테이너가 죽으면 재시작 루프에 빠진다.
///
/// 올바른 모양은 "앱은 뜨되 준비되지 않았다고 보고하는 것" 이다.
/// /health/ready 가 503 을 주는 동안 배포 스크립트와 로드밸런서가 트래픽을 막아 주므로,
/// 시드는 여기서 느긋하게 재시도하면 된다.
/// </remarks>
public sealed class AdminSeedWorker(
    IServiceProvider services, IConfiguration config, ILogger<AdminSeedWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var password = config["Admin:SeedPassword"] ?? "admin1234";
        var delay = TimeSpan.FromSeconds(1);

        for (var attempt = 1; !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                await AdminUserSeeder.SeedAsync(services, password, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "운영자 계정 시드 실패 ({Attempt}회차). {Delay}초 뒤 재시도합니다.",
                    attempt, delay.TotalSeconds);

                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }
}
