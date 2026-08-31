using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using VelaShell.Identity.Accounts;
using VelaShell.Identity.Mail;
using VelaShell.Identity.Options;

namespace VelaShell.Identity.Pages.Account;

/// <summary>
/// 「忘记口令」——填邮箱,收一封带一次性链接的信。
///
/// <b>这一页的核心纪律是:不论邮箱存不存在、被不被节流、信发没发出去,
/// 回给用户的都是同一句话。</b> 任何一处措辞上的差别都会把这个页面变成账号枚举器 ——
/// 攻击者拿一份邮箱列表跑一遍,就能筛出"哪些人在这儿有账号",而这正是撞库的第一步。
/// 真实的失败原因只进日志。
/// </summary>
public sealed class ForgotPasswordModel(
    AccountStore accounts,
    PasswordResetStore resets,
    EmailSender mail,
    IOptions<AccountOptions> options,
    IOptions<IdentityServerOptions> server,
    ILogger<ForgotPasswordModel> logger) : PageModel
{
    /// <summary>表单字段。</summary>
    [BindProperty]
    public ForgotInput Input { get; set; } = new();

    /// <summary>已经受理(不代表真的发出去了)。</summary>
    public bool Submitted { get; private set; }

    /// <summary>本部署配了发信出口没有。没配就别让用户白填一遍表单。</summary>
    public bool MailEnabled => mail.Enabled;

    /// <summary>链接有效期,提示语里要用。</summary>
    public TimeSpan Lifetime => options.Value.PasswordResetLifetime;

    /// <summary>渲染页面。</summary>
    public void OnGet()
    {
    }

    /// <summary>受理找回请求。</summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancel)
    {
        if (!MailEnabled)
        {
            return Page();
        }
        if (!ModelState.IsValid)
        {
            return Page();
        }

        Submitted = true;
        IdentityAccount? account = await accounts.FindByLoginAsync(Input.Email, cancel);

        // 下面每一条 return 之前的分支都**不改变页面输出**,只写日志。
        if (account is null)
        {
            logger.LogInformation("找回口令:{Email} 没有对应账号,不发信。", Input.Email);
            return Page();
        }
        if (account.IsDisabled)
        {
            logger.LogWarning("找回口令:账号 {Subject} 已停用,不发信。", account.Id);
            return Page();
        }
        if (string.IsNullOrEmpty(account.Email))
        {
            // 改必填之前留下的老账号:库里没邮箱,信无处可发。
            logger.LogWarning("找回口令:账号 {Subject} 没有邮箱(必填之前建的),只能由管理员处理。", account.Id);
            return Page();
        }

        PasswordResetIssue issue = await resets.IssueAsync(account.Id, cancel);
        if (!issue.Issued)
        {
            logger.LogInformation("找回口令:账号 {Subject} 距上一封信太近,本次不重复发送。", account.Id);
            return Page();
        }

        // 链接里的地址用 Issuer 而不是当前请求的 Host:请求是穿过反代进来的,
        // Host 可以被伪造,而这封信里的链接必须指向真正的认证服务。
        string link = $"{server.Value.Issuer.TrimEnd('/')}/account/reset?token={Uri.EscapeDataString(issue.Token!)}";
        string body = $"""
            你好{(account.DisplayName is null ? "" : $",{account.DisplayName}")}:

            有人为账号 {account.UserName} 申请了重置口令。点下面的链接设置新口令:

            {link}

            链接 {Lifetime.TotalMinutes:0} 分钟内有效,只能使用一次。

            如果这不是你本人操作,忽略这封信即可 —— 在链接被使用之前,你的口令不会有任何改变。

            —— VelaShell 统一认证
            """;
        await mail.SendAsync(account.Email, "重置你的 VelaShell 口令", body, cancel);
        return Page();
    }

    /// <summary>找回表单。</summary>
    public sealed class ForgotInput
    {
        /// <summary>注册时填的邮箱。</summary>
        [Required(ErrorMessage = "请填写邮箱。")]
        [EmailAddress(ErrorMessage = "邮箱格式不对。")]
        [Display(Name = "邮箱")]
        public string Email { get; set; } = "";
    }
}
