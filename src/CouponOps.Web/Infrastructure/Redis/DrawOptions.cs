namespace CouponOps.Infrastructure.Redis;

public sealed class DrawOptions
{
    public const string SectionName = "Draw";

    /// <summary>
    /// 멱등 키 보관 기간. 이 기간 안의 재시도는 최초 결과(같은 경품)를 그대로 돌려받는다.
    /// 이벤트 기간보다 짧으면 늦은 재시도가 재평가되므로 실제 운영에서는 이벤트 길이 이상으로 잡는다.
    /// </summary>
    public TimeSpan IdempotencyTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// 일일 카운터 키의 TTL. 리셋 경계를 넘긴 유저가 이전 날짜 키를 다시 읽는 일이 없도록
    /// 하루보다 넉넉히 잡되, 무한히 쌓이지 않게 한정한다.
    /// </summary>
    public TimeSpan DailyCounterTtl { get; set; } = TimeSpan.FromHours(48);

    /// <summary>실패 이력을 스트림에 남길 비율(0.0 ~ 1.0).</summary>
    public double FailureLogSampleRate { get; set; } = 1.0;

    public int PersistenceBatchSize { get; set; } = 500;
    public TimeSpan PersistencePollInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// 같은 유저의 잭팟 당첨이 이 횟수에 이르면 이상 징후로 본다.
    /// 확률적으로 불가능하지는 않으므로 "부정" 이 아니라 "봐야 할 것" 으로 다룬다.
    /// </summary>
    public int JackpotRepeatThreshold { get; set; } = 3;

    /// <summary>추첨 폭주를 판정하는 시간 창.</summary>
    public TimeSpan BurstWindow { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>한 유저가 <see cref="BurstWindow"/> 안에 이 횟수를 넘겨 돌리면 이상 징후로 본다.</summary>
    public int BurstThreshold { get; set; } = 100;
}
