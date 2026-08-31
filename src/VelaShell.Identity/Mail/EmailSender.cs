using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using VelaShell.Identity.Options;

namespace VelaShell.Identity.Mail;

/// <summary>
/// 发信。
///
/// 用 MailKit 而不是 <c>System.Net.Mail.SmtpClient</c>:后者对 STARTTLS 的协商与证书处理
/// 在真实服务商(QQ / 163 / Gmail 这些)面前经常谈不拢,而且微软自己在文档里就不推荐新代码用它。
/// </summary>
public sealed class EmailSender(IOptions<MailOptions> options, ILogger<EmailSender> logger)
{
    /// <summary>本部署是否配了发信出口。</summary>
    public bool Enabled => options.Value.Enabled;

    /// <summary>
    /// 发一封纯文本信。
    ///
    /// **失败只记日志、不抛**,而且调用方(找回口令那条路)也不该把结果告诉用户 ——
    /// "这个地址发得出去 / 发不出去"本身就是一条能被用来枚举账号的信息。
    /// 运维要排查发信问题,看日志。
    /// </summary>
    /// <returns>是否发送成功。仅供日志与测试使用,别把它渲染到页面上。</returns>
    public async Task<bool> SendAsync(string to, string subject, string body, CancellationToken cancel = default)
    {
        MailOptions mail = options.Value;
        if (!mail.Enabled)
        {
            logger.LogWarning("没有配置 Mail:Host,发往 {To} 的信没有发出。", to);
            return false;
        }

        MimeMessage message = new();
        message.From.Add(new MailboxAddress(mail.FromName, mail.EffectiveFrom));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        try
        {
            using SmtpClient client = new();
            await client.ConnectAsync(mail.Host, mail.Port,
                mail.UseImplicitTls ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable,
                cancel);
            if (!string.IsNullOrEmpty(mail.UserName))
            {
                await client.AuthenticateAsync(mail.UserName, mail.Password, cancel);
            }
            await client.SendAsync(message, cancel);
            await client.DisconnectAsync(true, cancel);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 收件地址进日志,信的内容不进 —— 里面装着一次性重置链接。
            logger.LogError(ex, "发往 {To} 的信没有发出。", to);
            return false;
        }
    }
}
