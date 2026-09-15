namespace CouponOps.Domain;

/// <summary>운영툴 계정. 3단계에서 쿠키 인증과 함께 사용한다.</summary>
public class AdminUser
{
    private AdminUser() { }

    public long Id { get; private set; }
    public string LoginId { get; private set; } = default!;
    public string PasswordHash { get; private set; } = default!;
    public AdminRole Role { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public static AdminUser Create(string loginId, string passwordHash, AdminRole role, DateTime nowUtc) =>
        new() { LoginId = loginId, PasswordHash = passwordHash, Role = role, IsActive = true, CreatedAt = nowUtc };
}
