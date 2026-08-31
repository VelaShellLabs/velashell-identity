using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using VelaShell.Identity.Accounts;
using VelaShell.Identity.Options;
using VelaShell.Identity.Pages.Account;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace VelaShell.Identity.Pages;

/// <summary>
/// 认证服务的门面页:没登录时给两个入口,登录后给"我是谁"和改口令。
/// 这里刻意把 <c>sub</c> 显示出来 —— 市场的审核员名单要用它,不然只能去数据库里翻。
/// </summary>
public sealed class IndexModel(AccountStore accounts, IOptions<AccountOptions> options,
                               IOptions<IdentityServerOptions> server) : PageModel
{
    /// <summary>当前登录的账号,未登录为 <c>null</c>。</summary>
    public IdentityAccount? Account { get; private set; }

    /// <summary>
    /// 访问令牌寿命。改口令的提示语里要用它 —— 换戳能立刻切断会话与刷新令牌,
    /// 但**已经签出去的访问令牌切不掉**,只能等它自己过期。写死一个数字迟早会与配置对不上。
    /// </summary>
    public TimeSpan AccessTokenLifetime => server.Value.AccessTokenLifetime;

    /// <summary>改口令的失败原因。</summary>
    public string? Error { get; private set; }

    /// <summary>改口令是否刚刚成功。</summary>
    public bool PasswordChanged { get; private set; }

    /// <summary>改资料的失败原因。与改口令分开:两个表单在同一页上,错误不该串台。</summary>
    public string? ProfileError { get; private set; }

    /// <summary>资料是否刚刚保存成功。</summary>
    public bool ProfileSaved { get; private set; }

    /// <summary>是否开放自助注册。</summary>
    public bool AllowRegistration => options.Value.AllowSelfRegistration;

    /// <summary>当前口令(仅用于改口令表单)。</summary>
    [BindProperty]
    public string CurrentPassword { get; set; } = "";

    /// <summary>新口令。</summary>
    [BindProperty]
    public string NewPassword { get; set; } = "";

    /// <summary>资料表单:邮箱。</summary>
    [BindProperty]
    public string ProfileEmail { get; set; } = "";

    /// <summary>资料表单:显示名。</summary>
    [BindProperty]
    public string? ProfileDisplayName { get; set; }

    /// <summary>渲染页面。</summary>
    public async Task OnGetAsync(CancellationToken cancel)
    {
        await LoadAsync(cancel);
        FillProfileForm();
    }

    /// <summary>
    /// 改资料(显示名 + 邮箱)。不要旧口令 —— 这不是凭据变更,
    /// 而且能走到这里的人已经握着一张有效会话了。
    /// </summary>
    public async Task<IActionResult> OnPostProfileAsync(CancellationToken cancel)
    {
        await LoadAsync(cancel);
        if (Account is null)
        {
            return RedirectToPage("/Account/Login");
        }

        RegistrationResult result = await accounts.UpdateProfileAsync(Account, ProfileEmail, ProfileDisplayName, cancel);
        if (!result.Succeeded)
        {
            ProfileError = result.Error;
            return Page();
        }

        // 会话 cookie 里带着 name 声明,显示名改了就得重签,否则页面顶上还挂着旧名字。
        // 这里不换安全戳,所以别的设备**不会**被踢下线 —— 改昵称不该有那种后果。
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            SessionPrincipal.Create(Account),
            new AuthenticationProperties { IsPersistent = true, IssuedUtc = DateTimeOffset.UtcNow });
        ProfileSaved = true;
        FillProfileForm();
        return Page();
    }

    /// <summary>改口令。旧口令必须对得上,否则一张被顺走的 cookie 就能顶掉账号。</summary>
    public async Task<IActionResult> OnPostPasswordAsync(CancellationToken cancel)
    {
        await LoadAsync(cancel);
        FillProfileForm();
        if (Account is null)
        {
            return RedirectToPage("/Account/Login");
        }

        if (await accounts.CheckPasswordAsync(Account, CurrentPassword, cancel) != SignInStatus.Success)
        {
            Error = "当前口令不对。";
            return Page();
        }
        if (NewPassword.Length < options.Value.MinimumPasswordLength)
        {
            Error = $"新口令至少 {options.Value.MinimumPasswordLength} 位。";
            return Page();
        }

        await accounts.ChangePasswordAsync(Account, NewPassword, cancel);
        // 换戳会让所有带旧戳的 cookie 立刻失效 —— 包括当前这一张。用新戳重签一次,
        // 否则"改完口令,下一次点击就被弹回登录页"的是本人,而不是那个该被踢掉的人。
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            SessionPrincipal.Create(Account),
            new AuthenticationProperties { IsPersistent = true, IssuedUtc = DateTimeOffset.UtcNow });
        PasswordChanged = true;
        return Page();
    }

    /// <summary>把库里的值填进资料表单。POST 失败重渲染时不覆盖用户刚敲的内容。</summary>
    private void FillProfileForm()
    {
        if (Account is null || ProfileError is not null)
        {
            return;
        }
        ProfileEmail = Account.Email ?? "";
        ProfileDisplayName = Account.DisplayName;
    }

    private async Task LoadAsync(CancellationToken cancel)
    {
        string? subject = User.FindFirst(Claims.Subject)?.Value;
        Account = subject is null ? null : await accounts.FindByIdAsync(subject, cancel);
    }
}
