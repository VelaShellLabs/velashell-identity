using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using VelaShell.Identity.Accounts;
using VelaShell.Identity.Options;

namespace VelaShell.Identity.Pages.Account;

/// <summary>
/// 用邮件里那条一次性链接设置新口令。
///
/// 这一页**不建立会话**:重置成功后把人送回登录页,让他用新口令登一次。
/// 一是确认新口令确实记住了,二是重置本身会换掉安全戳、把所有设备踢下线,
/// 顺手给自己签一张新 cookie 会让"我到底被踢没被踢"变得难以解释。
/// </summary>
public sealed class ResetPasswordModel(
    AccountStore accounts,
    PasswordResetStore resets,
    IOptions<AccountOptions> options,
    ILogger<ResetPasswordModel> logger) : PageModel
{
    /// <summary>链接里带的一次性令牌。</summary>
    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    /// <summary>表单字段。</summary>
    [BindProperty]
    public ResetInput Input { get; set; } = new();

    /// <summary>令牌当前是否可用。不可用就只显示一句话,连表单都不渲染。</summary>
    public bool TokenValid { get; private set; }

    /// <summary>失败原因。</summary>
    public string? Error { get; private set; }

    /// <summary>口令下限,提示语与强度条要用。</summary>
    public int MinimumPasswordLength => options.Value.MinimumPasswordLength;

    /// <summary>打开链接。先验令牌,过期或用过的直接给结论,别让人白填一遍表单。</summary>
    public async Task OnGetAsync(CancellationToken cancel) =>
        TokenValid = Token is not null && await resets.ValidateAsync(Token, cancel) is not null;

    /// <summary>设置新口令。</summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancel)
    {
        // 重新验一次。GET 到 POST 之间令牌可能已经过期,或者已经被另一个标签页用掉了。
        string? subject = Token is null ? null : await resets.ValidateAsync(Token, cancel);
        if (subject is null)
        {
            TokenValid = false;
            return Page();
        }
        TokenValid = true;

        if (!ModelState.IsValid)
        {
            return Page();
        }
        if (Input.Password.Length < MinimumPasswordLength)
        {
            Error = $"新口令至少 {MinimumPasswordLength} 位。";
            return Page();
        }

        IdentityAccount? account = await accounts.FindByIdAsync(subject, cancel);
        if (account is null)
        {
            TokenValid = false;
            return Page();
        }

        // 先把令牌兑掉再改口令。这一步是原子的:两个并发请求里只有一个能拿到 true,
        // 另一个会看到"链接已失效"而不是**两次**改写口令。
        if (!await resets.RedeemAsync(Token!, cancel))
        {
            TokenValid = false;
            return Page();
        }

        await accounts.ChangePasswordAsync(account, Input.Password, cancel);
        logger.LogInformation("账号 {Subject} 通过邮件链接重置了口令。", account.Id);
        return RedirectToPage("/Account/Login", new { reset = true });
    }

    /// <summary>重置表单。</summary>
    public sealed class ResetInput
    {
        /// <summary>新口令。</summary>
        [Required(ErrorMessage = "请填写新口令。")]
        [Display(Name = "新口令")]
        public string Password { get; set; } = "";

        /// <summary>确认新口令。</summary>
        [Required(ErrorMessage = "请再输一次新口令。")]
        [Compare(nameof(Password), ErrorMessage = "两次输入的口令不一致。")]
        [Display(Name = "确认新口令")]
        public string ConfirmPassword { get; set; } = "";
    }
}
