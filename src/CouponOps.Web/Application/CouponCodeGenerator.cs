using System.Security.Cryptography;

namespace CouponOps.Application;

/// <summary>
/// 사전 생성(PreGenerated) 모드의 쿠폰 코드 생성기.
/// </summary>
/// <remarks>
/// 코드는 추측 불가능해야 한다. 순차 번호를 노출하면 열거 공격으로 남의 코드를 사용할 수 있다.
/// 알파벳에서 I·O·0·1 을 뺀 것은 유저가 코드를 눈으로 옮겨 적을 때의 오독을 막기 위함이다.
/// </remarks>
public static class CouponCodeGenerator
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // 32자
    public const int DefaultLength = 12;

    /// <summary>
    /// 서로 다른 코드 <paramref name="count"/>개를 만든다.
    /// 12자 × 32자모 = 60비트. 100만 건에서 충돌 확률은 ~4e-7 수준이고,
    /// 그마저도 여기서 중복을 제거한 뒤 DB UNIQUE 인덱스가 최종 방어선이 된다.
    /// </summary>
    public static string[] Generate(int count, int length = DefaultLength)
    {
        var set = new HashSet<string>(count, StringComparer.Ordinal);
        var buffer = new byte[length];

        while (set.Count < count)
        {
            RandomNumberGenerator.Fill(buffer);

            // 32 가 256 을 나누어떨어지므로 (b & 31) 은 편향 없이 균등하다.
            var chars = new char[length];
            for (var i = 0; i < length; i++) chars[i] = Alphabet[buffer[i] & 31];

            set.Add(new string(chars));
        }

        return [.. set];
    }
}
