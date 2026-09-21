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

    /// <summary>
    /// 룰렛 이벤트 키. 쿠폰과 접두사를 나눠 같은 Redis 에서 두 종류가 공존해도 섞이지 않게 한다.
    /// 한 이벤트의 키는 모두 <c>{drw:&lt;id&gt;}</c> 해시 태그를 공유한다 —
    /// 추첨 스크립트가 아홉 개 키를 한 번에 만지므로 Cluster 에서 같은 슬롯에 있어야 한다.
    /// </summary>
    public static class Draw
    {
        public static string Tag(long drawEventId) => $"{{drw:{drawEventId}}}";

        public static string Meta(long id) => $"{Tag(id)}:meta";
        /// <summary>누적 가중치 배열. 워밍업 때 만들고 이벤트 기간 내내 불변이다.</summary>
        public static string Cum(long id) => $"{Tag(id)}:cum";
        /// <summary>유한 재고 슬롯만 담는다. 항목이 없으면 무제한이라는 뜻이다.</summary>
        public static string Stock(long id) => $"{Tag(id)}:stock";
        /// <summary>일일 카운터. 리셋 시각 계산은 앱이 하고 그 결과가 키 이름에 들어간다.</summary>
        public static string Daily(long id, string day) => $"{Tag(id)}:daily:{day}";
        public static string Pity(long id) => $"{Tag(id)}:pity";
        public static string Tickets(long id) => $"{Tag(id)}:tickets";
        public static string Request(long id, Guid requestId) => $"{Tag(id)}:req:{requestId:N}";
        public static string Stream(long id) => $"{Tag(id)}:drawn";
        public static string Won(long id) => $"{Tag(id)}:won";

        /// <summary>적재 워커가 폴링할 룰렛 이벤트 목록. 스크립트가 만지지 않으므로 해시 태그가 없다.</summary>
        public const string StreamRegistry = "couponops:draw-streams";
    }
}
