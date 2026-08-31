using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Identity.Options;

namespace VelaShell.Identity.Accounts;

/// <summary>登录的结论。把"密码错"与"账号不存在"合并成同一种失败,避免枚举用户名。</summary>
public enum SignInStatus
{
    /// <summary>通过。</summary>
    Success,

    /// <summary>用户名或口令不对。</summary>
    InvalidCredentials,

    /// <summary>连续失败过多,已被临时锁定。</summary>
    LockedOut,

    /// <summary>账号被停用。</summary>
    Disabled
}

/// <summary>注册的结论。</summary>
/// <param name="Account">成功时的账号。</param>
/// <param name="Error">失败时给用户看的原因。</param>
public readonly record struct RegistrationResult(IdentityAccount? Account, string? Error)
{
    /// <summary>是否成功。</summary>
    public bool Succeeded => Account is not null;
}

/// <summary>
/// 账号的读写与口令校验。
///
/// 这里不引入 ASP.NET Core Identity 的整套 UserManager/Store 体系:那套东西的价值在于
/// 角色、双因素、外部登录、令牌提供程序等一大批我们用不到的能力,代价是一层需要专门适配
/// MongoDB 的抽象。市场需要的只有"建账号、验口令、防爆破"三件事,直接落在集合上更清楚。
/// 唯独口令散列复用框架的 <see cref="PasswordHasher{TUser}" /> —— 自己写散列是最不该做的事。
/// </summary>
public sealed partial class AccountStore(IMongoDatabase database, IOptions<AccountOptions> options)
{
    private readonly IMongoCollection<IdentityAccount> _accounts = database.GetCollection<IdentityAccount>("accounts");
    private readonly PasswordHasher<IdentityAccount> _hasher = new();

    /// <summary>用户名允许的字符:字母、数字、下划线、点、连字符,3~32 位。</summary>
    [GeneratedRegex(@"^[a-zA-Z0-9_.\-]{3,32}$")]
    private static partial Regex UserNamePattern { get; }

    /// <summary>邮箱唯一索引的名字。建立与迁移两处都要用,拎出来避免抄错。</summary>
    private const string EmailIndexName = "ux_account_email";

    /// <summary>建立唯一索引。用户名与邮箱的唯一性由**数据库**保证,不靠应用层的"先查再插"。</summary>
    public async Task EnsureIndexesAsync(CancellationToken cancel = default)
    {
        await DropLegacySparseEmailIndexAsync(cancel);
        await _accounts.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<IdentityAccount>(
                Builders<IdentityAccount>.IndexKeys.Ascending(a => a.NormalizedUserName),
                new CreateIndexOptions { Name = "ux_account_username", Unique = true }),
            // 用 partial 而不是 sparse。**这不是风格问题**:sparse 只跳过"字段不存在"的文档,
            // 字段存在但值为 null 的照样进索引 —— 而 C# 的 string? 恰恰会把 null 写进库。
            // 早先那条 sparse 索引就是这么让第二个不填邮箱的账号撞上重复键的。
            // 邮箱现在已是必填,新账号不会再有 null;这条 partial 仍然留着,
            // 一来让改必填之前遗留的 null 账号不互相打架,二来这个约束的语义本来就是
            // "只约束真正填了邮箱的账号",写清楚比依赖"反正不会有 null"更牢靠。
            new CreateIndexModel<IdentityAccount>(
                Builders<IdentityAccount>.IndexKeys.Ascending(a => a.NormalizedEmail),
                new CreateIndexOptions<IdentityAccount>
                {
                    Name = EmailIndexName,
                    Unique = true,
                    PartialFilterExpression = Builders<IdentityAccount>.Filter.Type(a => a.NormalizedEmail, BsonType.String)
                })
        ], cancel);
    }

    /// <summary>
    /// 把老部署上那条 sparse 邮箱索引换掉。
    ///
    /// 同名索引只要选项对不上,<c>CreateMany</c> 就直接抛 <c>IndexKeySpecsConflict</c>,
    /// 而这个方法是启动播种时 await 的 —— 不先删,已经跑过的部署会**卡在启动阶段起不来**。
    /// 判据用"有没有 partialFilterExpression",而不是去比 sparse 标志:
    /// 只要不是我们要的那条就重建,将来再改选项也不用回来改这里。
    /// </summary>
    private async Task DropLegacySparseEmailIndexAsync(CancellationToken cancel)
    {
        List<BsonDocument> existing;
        try
        {
            using IAsyncCursor<BsonDocument> cursor = await _accounts.Indexes.ListAsync(cancel);
            existing = await cursor.ToListAsync(cancel);
        }
        catch (MongoCommandException)
        {
            // 集合还不存在(全新部署),没有索引可迁移。
            return;
        }

        BsonDocument? email = existing.Find(i => i.GetValue("name", "").AsString == EmailIndexName);
        if (email is null || email.Contains("partialFilterExpression"))
        {
            return;
        }
        await _accounts.Indexes.DropOneAsync(EmailIndexName, cancel);
    }

    /// <summary>按 <c>sub</c> 取账号。</summary>
    public async Task<IdentityAccount?> FindByIdAsync(string id, CancellationToken cancel = default) =>
        await _accounts.Find(a => a.Id == id).FirstOrDefaultAsync(cancel);

    /// <summary>按用户名或邮箱取账号 —— 登录框里两种都收。</summary>
    public async Task<IdentityAccount?> FindByLoginAsync(string login, CancellationToken cancel = default)
    {
        string normalized = Normalize(login);
        return await _accounts.Find(a => a.NormalizedUserName == normalized || a.NormalizedEmail == normalized)
                              .FirstOrDefaultAsync(cancel);
    }

    /// <summary>集合里一个账号都没有?播种首个管理账号时用它判断。</summary>
    public async Task<bool> IsEmptyAsync(CancellationToken cancel = default) =>
        await _accounts.CountDocumentsAsync(FilterDefinition<IdentityAccount>.Empty,
            new CountOptions { Limit = 1 }, cancel) == 0;

    /// <summary>注册一个账号。用户名/邮箱重复由唯一索引挡下,这里把写冲突翻译成可读的提示。</summary>
    public async Task<RegistrationResult> CreateAsync(string userName, string password, string email,
                                                      string? displayName, CancellationToken cancel = default)
    {
        userName = userName.Trim();
        email = email?.Trim() ?? "";
        displayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();

        if (!UserNamePattern.IsMatch(userName))
        {
            return new(null, "用户名只能用字母、数字、下划线、点或连字符,长度 3~32 位。");
        }
        if (string.IsNullOrEmpty(password) || password.Length < options.Value.MinimumPasswordLength)
        {
            return new(null, $"口令至少 {options.Value.MinimumPasswordLength} 位。");
        }
        // 邮箱是必填项。页面上有 [Required] + [EmailAddress],这里是后端的兜底 ——
        // 播种首个账号和将来任何非页面的建号路径都走这个方法,校验不能只长在表单上。
        if (email.Length == 0)
        {
            return new(null, "请填写邮箱。它是你自助找回口令的唯一凭据。");
        }
        if (!email.Contains('@') || email.Length < 5)
        {
            return new(null, "邮箱格式不对。");
        }

        IdentityAccount account = new()
        {
            UserName = userName,
            NormalizedUserName = Normalize(userName),
            Email = email,
            NormalizedEmail = Normalize(email),
            DisplayName = displayName,
            PasswordHash = ""
        };
        account.PasswordHash = _hasher.HashPassword(account, password);

        try
        {
            await _accounts.InsertOneAsync(account, cancellationToken: cancel);
            return new(account, null);
        }
        catch (MongoWriteException e) when (e.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // 唯一索引名直接告诉我们撞的是哪一条,不需要再回查一次。
            return new(null, e.WriteError.Message.Contains(EmailIndexName, StringComparison.Ordinal)
                                 ? $"邮箱 {email} 已经注册过了。可以直接用它登录,或换一个邮箱。"
                                 : $"用户名 {userName} 已经被占用了,换一个吧。");
        }
    }

    /// <summary>
    /// 改资料(显示名与邮箱)。用户名不在其列 —— 它是登录标识,注册后不给改。
    ///
    /// **不换安全戳**:改个昵称或邮箱不是凭据变更,没有理由把人从所有设备上踢下线。
    /// 但改完之后会话 cookie 里的 <c>name</c> 声明就旧了,调用方要用新账号重签一次,
    /// 否则页面顶上还挂着旧名字直到下次登录。
    ///
    /// 改动直接写回传进来的实例,省得调用方再查一次库。
    /// </summary>
    public async Task<RegistrationResult> UpdateProfileAsync(IdentityAccount account, string email,
                                                             string? displayName, CancellationToken cancel = default)
    {
        email = email?.Trim() ?? "";
        displayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();

        if (email.Length == 0)
        {
            return new(null, "请填写邮箱。");
        }
        if (!email.Contains('@') || email.Length < 5)
        {
            return new(null, "邮箱格式不对。");
        }

        string normalized = Normalize(email);
        try
        {
            await _accounts.UpdateOneAsync(a => a.Id == account.Id,
                Builders<IdentityAccount>.Update
                                       .Set(a => a.Email, email)
                                       .Set(a => a.NormalizedEmail, normalized)
                                       .Set(a => a.DisplayName, displayName),
                cancellationToken: cancel);
        }
        catch (MongoWriteException e) when (e.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return new(null, $"邮箱 {email} 已经被另一个账号占用了。");
        }

        account.Email = email;
        account.NormalizedEmail = normalized;
        account.DisplayName = displayName;
        return new(account, null);
    }

    /// <summary>
    /// 校验口令。失败会累加计数并在超过阈值时锁定;成功则清零计数、记录登录时间,
    /// 并在散列参数过期时顺手升级散列。
    /// </summary>
    public async Task<SignInStatus> CheckPasswordAsync(IdentityAccount account, string password,
                                                       CancellationToken cancel = default)
    {
        if (account.IsDisabled)
        {
            return SignInStatus.Disabled;
        }
        if (account.LockoutEndsAt is { } until && until > DateTime.UtcNow)
        {
            return SignInStatus.LockedOut;
        }

        PasswordVerificationResult verification = _hasher.VerifyHashedPassword(account, account.PasswordHash, password);
        if (verification == PasswordVerificationResult.Failed)
        {
            await RegisterFailureAsync(account, cancel);
            return account.LockoutEndsAt is { } end && end > DateTime.UtcNow
                       ? SignInStatus.LockedOut
                       : SignInStatus.InvalidCredentials;
        }

        UpdateDefinition<IdentityAccount> update = Builders<IdentityAccount>.Update
            .Set(a => a.AccessFailedCount, 0)
            .Set(a => a.LockoutEndsAt, null)
            .Set(a => a.LastLoginAt, DateTime.UtcNow);

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            update = update.Set(a => a.PasswordHash, _hasher.HashPassword(account, password));
        }

        await _accounts.UpdateOneAsync(a => a.Id == account.Id, update, cancellationToken: cancel);
        return SignInStatus.Success;
    }

    /// <summary>
    /// 改口令。同时换掉安全戳 —— 别处的会话 cookie 与刷新令牌带的还是旧戳,
    /// 下一次请求/续期时会比对不上而被拒(见 Program.cs 的 OnValidatePrincipal
    /// 与 ConnectEndpoints.ExchangeAsync)。
    ///
    /// 新戳同时写回传进来的实例:调用方通常要立刻用它重签当前这台设备的 cookie,
    /// 否则改完口令的人自己会被下一次请求踢出去。
    /// </summary>
    public async Task ChangePasswordAsync(IdentityAccount account, string password, CancellationToken cancel = default)
    {
        string stamp = Guid.NewGuid().ToString("N");
        await _accounts.UpdateOneAsync(a => a.Id == account.Id,
            Builders<IdentityAccount>.Update
                                   .Set(a => a.PasswordHash, _hasher.HashPassword(account, password))
                                   .Set(a => a.SecurityStamp, stamp)
                                   .Set(a => a.AccessFailedCount, 0)
                                   .Set(a => a.LockoutEndsAt, null),
            cancellationToken: cancel);
        account.SecurityStamp = stamp;
    }

    private async Task RegisterFailureAsync(IdentityAccount account, CancellationToken cancel)
    {
        int failures = account.AccessFailedCount + 1;
        bool lockout = options.Value.MaxFailedAttempts > 0 && failures >= options.Value.MaxFailedAttempts;

        await _accounts.UpdateOneAsync(a => a.Id == account.Id,
            Builders<IdentityAccount>.Update
                                   .Set(a => a.AccessFailedCount, lockout ? 0 : failures)
                                   .Set(a => a.LockoutEndsAt, lockout ? DateTime.UtcNow + options.Value.LockoutDuration : null),
            cancellationToken: cancel);

        if (lockout)
        {
            account.LockoutEndsAt = DateTime.UtcNow + options.Value.LockoutDuration;
        }
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
}
