using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using VelaShell.Identity.Options;

namespace VelaShell.Identity.Accounts;

/// <summary>签发重置令牌的结果。</summary>
/// <param name="Token">明文令牌,只在这一刻存在 —— 拿去拼链接,之后无从取回。</param>
/// <param name="Throttled">被节流挡下(距上一封信太近),没有签发新令牌。</param>
public readonly record struct PasswordResetIssue(string? Token, bool Throttled)
{
    /// <summary>是否真的签出了一张令牌。</summary>
    public bool Issued => Token is not null;
}

/// <summary>
/// 找回口令的令牌存取。
///
/// 三条不变量,改这个文件之前先读一遍:
/// <list type="number">
///   <item><description>库里只有散列,明文令牌绝不落盘、绝不进日志。</description></item>
///   <item><description>一张令牌只能用一次;用掉的同时,该账号其余未用的令牌一并作废。</description></item>
///   <item><description>校验一律走"散列查库",不接受任何按 subject 反查的捷径 —— 那等于允许猜账号。</description></item>
/// </list>
/// </summary>
public sealed class PasswordResetStore(IMongoDatabase database, IOptions<AccountOptions> options)
{
    private readonly IMongoCollection<PasswordResetToken> _tokens =
        database.GetCollection<PasswordResetToken>("password_resets");

    /// <summary>建索引:散列唯一 + 按账号查 + 到期自动清。</summary>
    public async Task EnsureIndexesAsync(CancellationToken cancel = default) =>
        await _tokens.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<PasswordResetToken>(
                Builders<PasswordResetToken>.IndexKeys.Ascending(t => t.TokenHash),
                new CreateIndexOptions { Name = "ux_reset_token", Unique = true }),
            new CreateIndexModel<PasswordResetToken>(
                Builders<PasswordResetToken>.IndexKeys.Ascending(t => t.Subject).Descending(t => t.CreatedAt),
                new CreateIndexOptions { Name = "ix_reset_subject" }),
            // TTL:过期即删,不必写清理任务。ExpireAfter 设为零表示"到 ExpiresAt 那一刻就算过期"。
            // 注意 MongoDB 的 TTL 线程每 60 秒才扫一遍,所以**过期判断不能依赖它** ——
            // 真正的过期由 ValidateAsync 按时间比较来判,TTL 只负责把垃圾扫走。
            new CreateIndexModel<PasswordResetToken>(
                Builders<PasswordResetToken>.IndexKeys.Ascending(t => t.ExpiresAt),
                new CreateIndexOptions { Name = "ttl_reset_expiry", ExpireAfter = TimeSpan.Zero })
        ], cancel);

    /// <summary>
    /// 给某个账号签一张新令牌。距上一封信不足 <c>PasswordResetResendInterval</c> 时不签,
    /// 返回 <see cref="PasswordResetIssue.Throttled" /> —— 调用方**照样要给用户同一句回话**,
    /// 否则"有没有被节流"就成了一个能被观测的信号。
    /// </summary>
    public async Task<PasswordResetIssue> IssueAsync(string subject, CancellationToken cancel = default)
    {
        DateTime now = DateTime.UtcNow;
        DateTime since = now - options.Value.PasswordResetResendInterval;
        long recent = await _tokens.CountDocumentsAsync(
            t => t.Subject == subject && t.UsedAt == null && t.CreatedAt > since,
            new CountOptions { Limit = 1 }, cancel);
        if (recent > 0)
        {
            return new(null, true);
        }

        // 256 位随机。令牌进的是 URL,所以用 URL 安全的 Base64(无填充)。
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                              .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        await _tokens.InsertOneAsync(new()
        {
            Subject = subject,
            TokenHash = Hash(token),
            CreatedAt = now,
            ExpiresAt = now + options.Value.PasswordResetLifetime
        }, cancellationToken: cancel);
        return new(token, false);
    }

    /// <summary>
    /// 校验一张明文令牌,通过则返回它对应的账号 id。
    /// 不存在、已用过、已过期一律返回 <c>null</c> —— 对外只有"行"与"不行"两种结果。
    /// </summary>
    public async Task<string?> ValidateAsync(string token, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }
        string hash = Hash(token);
        PasswordResetToken? found = await _tokens.Find(t => t.TokenHash == hash).FirstOrDefaultAsync(cancel);
        return found is null || found.UsedAt is not null || found.ExpiresAt <= DateTime.UtcNow ? null : found.Subject;
    }

    /// <summary>
    /// 用掉一张令牌,并把该账号其余未用的令牌一并作废。
    ///
    /// 返回是否**真的**由本次调用把它从"未用"翻成"已用"。这一步是原子的
    /// (条件更新 + 判断匹配数),于是两个并发的重置请求里只有一个能成功 ——
    /// 靠"先查再写"是挡不住的。
    /// </summary>
    public async Task<bool> RedeemAsync(string token, CancellationToken cancel = default)
    {
        string hash = Hash(token);
        DateTime now = DateTime.UtcNow;
        UpdateResult result = await _tokens.UpdateOneAsync(
            t => t.TokenHash == hash && t.UsedAt == null && t.ExpiresAt > now,
            Builders<PasswordResetToken>.Update.Set(t => t.UsedAt, now),
            cancellationToken: cancel);
        if (result.MatchedCount == 0)
        {
            return false;
        }

        PasswordResetToken? used = await _tokens.Find(t => t.TokenHash == hash).FirstOrDefaultAsync(cancel);
        if (used is not null)
        {
            // 同一个账号手上可能还攥着几张之前申请的链接。口令既然已经换了,
            // 那几张就该立刻失效 —— 否则一封更早的旧信仍然能再改一次口令。
            await _tokens.UpdateManyAsync(
                t => t.Subject == used.Subject && t.UsedAt == null,
                Builders<PasswordResetToken>.Update.Set(t => t.UsedAt, now),
                cancellationToken: cancel);
        }
        return true;
    }

    private static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
}
