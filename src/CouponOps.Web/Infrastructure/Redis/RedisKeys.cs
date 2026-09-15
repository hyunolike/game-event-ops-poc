namespace CouponOps.Infrastructure.Redis;

/// <summary>
/// Redis 키 규약.
/// </summary>
/// <remarks>
/// 한 이벤트에 속한 키는 모두 <c>{evt:&lt;id&gt;}</c> 해시 태그를 공유한다.
/// 발급 스크립트가 이 키들을 한 번에 만지므로, Redis Cluster 로 확장할 때 같은 슬롯에
/// 있지 않으면 CROSSSLOT 오류가 난다. 단일 노드인 지금 정해 두지 않으면 나중에 바꿀 수 없다.
/// </remarks>
public static class RedisKeys
{
    public static string Tag(long eventId) => $"{{evt:{eventId}}}";

    public static string Meta(long eventId) => $"{Tag(eventId)}:meta";
    public static string Pool(long eventId) => $"{Tag(eventId)}:pool";
    public static string Stock(long eventId) => $"{Tag(eventId)}:stock";
    public static string Seq(long eventId) => $"{Tag(eventId)}:seq";
    public static string Users(long eventId) => $"{Tag(eventId)}:users";
    public static string Request(long eventId, Guid requestId) => $"{Tag(eventId)}:req:{requestId:N}";
    public static string Stream(long eventId) => $"{Tag(eventId)}:issued";

    /// <summary>
    /// 적재 워커가 폴링할 이벤트 목록. 해시 태그가 없는 것은 의도적이다 —
    /// 이 키는 앱만 쓰고 Lua 스크립트는 건드리지 않으므로 다른 슬롯에 있어도 무방하다.
    /// </summary>
    public const string StreamRegistry = "couponops:streams";

    public const string ConsumerGroup = "db-writer";
}
