using System.Security.Claims;
using CouponOps.Application;
using CouponOps.Domain;
using CouponOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CouponOps.Pages.Account;

public sealed class LoginModel(
    AppDbContext db, IPasswordHasher<AdminUser> hasher, IAuditLogger audit) : PageModel
{
    [BindProperty] public string LoginId { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";

    public string? Error { get; private set; }

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? RedirectToPage("/Events/Index") : Page();

    public async Task<IActionResult> OnPostAsync(string? returnUrl, CancellationToken ct)
    {
        var user = await db.AdminUsers.SingleOrDefaultAsync(u => u.LoginId == LoginId, ct);

        // 아이디가 없을 때와 비밀번호가 틀렸을 때의 응답을 구분하지 않는다.
        // 구분하면 어떤 계정이 존재하는지 알려주는 꼴이 된다.
        var ok = user is { IsActive: true }
                 && hasher.VerifyHashedPassword(user, user.PasswordHash, Password)
                    != PasswordVerificationResult.Failed;

        if (!ok)
        {
            Error = "아이디 또는 비밀번호가 올바르지 않습니다.";
            return Page();
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user!.Id.ToString()),
            new Claim(ClaimTypes.Name, user.LoginId),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
        ], CookieAuthenticationDefaults.AuthenticationScheme);

        var principal = new ClaimsPrincipal(identity);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

        // SignInAsync 는 응답 쿠키만 세팅한다. 이번 요청의 HttpContext.User 는 여전히 익명이라
        // 아래 감사 로그가 'system' 으로 남는다. principal 을 직접 꽂아 준다.
        HttpContext.User = principal;

        // 로그인도 감사 대상이다. "누가 언제 들어왔나" 는 사고 조사의 출발점이다.
        audit.Record(OperationAction.AdminSignedIn, nameof(AdminUser), user.Id.ToString(),
            before: null, after: new { user.LoginId, Role = user.Role.ToString() });
        await db.SaveChangesAsync(ct);

        return LocalRedirect(returnUrl ?? "/Events");
    }
}
