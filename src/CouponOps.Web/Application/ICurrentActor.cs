using System.Security.Claims;
using CouponOps.Domain;

namespace CouponOps.Application;

/// <summary>현재 요청을 수행 중인 운영자. 감사 로그의 "누가" 에 해당한다.</summary>
public interface ICurrentActor
{
    long Id { get; }
    string LoginId { get; }
    AdminRole Role { get; }
    bool CanEdit { get; }
}

public sealed class HttpCurrentActor(IHttpContextAccessor accessor) : ICurrentActor
{
    public const string SystemLoginId = "system";

    private ClaimsPrincipal? User => accessor.HttpContext?.User;

    /// <summary>
    /// 로그인한 운영자가 없으면 시스템 액터(Id 0)로 본다.
    /// 시드·백그라운드 작업·테스트처럼 HTTP 컨텍스트가 없는 경로에서도 감사 로그는 남아야 한다.
    /// </summary>
    public long Id =>
        long.TryParse(User?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;

    public string LoginId => User?.Identity?.IsAuthenticated == true
        ? User.FindFirstValue(ClaimTypes.Name) ?? SystemLoginId
        : SystemLoginId;

    public AdminRole Role =>
        Enum.TryParse<AdminRole>(User?.FindFirstValue(ClaimTypes.Role), out var r) ? r : AdminRole.Viewer;

    public bool CanEdit => Role == AdminRole.Editor;
}
