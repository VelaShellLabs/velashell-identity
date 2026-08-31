using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace VelaShell.Identity.Accounts;

/// <summary>本服务自用的声明名(标准 OIDC 声明一律用 <c>OpenIddictConstants.Claims</c>)。</summary>
public static class IdentityClaims
{
    /// <summary>
    /// 安全戳在会话 cookie 与刷新令牌里的声明名。
    ///
    /// 刻意**不给它任何 Destination**:它既不该出现在访问令牌里(市场 API 用不着,
    /// 而访问令牌是能被解开看的),也不该进 id_token。没有 destination 的声明仍然会被
    /// OpenIddict 存进授权码与刷新令牌 —— 那正是续期时要比对的地方。
    /// </summary>
    public const string SecurityStamp = "velashell:stamp";
}

/// <summary>
/// 一个可登录的账号。
///
/// 文档 <c>_id</c> 就是令牌里的 <c>sub</c>,也是市场那边 <c>Plugin.OwnerSubject</c> /
/// <c>Review.Subject</c> 存的值。因此**它一旦签发就不能改** —— 换了 sub 等于换了个人,
/// 历史插件与评价会集体失去归属。用户名和邮箱都允许改,sub 不允许。
/// </summary>
public sealed class IdentityAccount
{
    /// <summary>账号标识,同时是 OIDC 的 <c>sub</c>。</summary>
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

    /// <summary>登录用的用户名。</summary>
    public required string UserName { get; set; }

    /// <summary>用户名的规范化形式(小写)。唯一索引建在它上面,于是"Alice"与"alice"不能同时存在。</summary>
    public required string NormalizedUserName { get; set; }

    /// <summary>
    /// 邮箱。**必填**,可以用它代替用户名登录。
    ///
    /// 2026-09-01 由可空改为必填。可空那阵子留下过一个真实的坑:唯一索引是稀疏的,
    /// 而稀疏只跳过"字段不存在"的文档、不跳过"字段存在但为 null"的 —— C# 的 <c>string?</c>
    /// 恰恰会把 null 老老实实写进库,于是第二个不填邮箱的人注册时撞上重复键,
    /// 被翻译成一句莫名其妙的"这个邮箱已经注册过了"。见 <see cref="AccountStore.EnsureIndexesAsync" />。
    ///
    /// ⚠️ 改为必填之前建的账号,库里这两个字段仍可能是 null(反射反序列化不认 <c>required</c>)。
    /// **不要假设从库里读出来的一定非空** —— <c>ConnectEndpoints</c> 发 email 声明前的判空、
    /// 首页那个"未填写"提示,都是为这批账号留的。
    ///
    /// 这批账号照常能用用户名登录,但目前**没有自助补填的入口**(<c>Pages/</c> 下只有
    /// Login / Register / Index,没有资料编辑页),只能直接改库。要么补一个编辑页,
    /// 要么运维手工 <c>updateOne</c>。
    /// </summary>
    public required string Email { get; set; }

    /// <summary>邮箱的规范化形式(小写)。唯一索引建在它上面。</summary>
    public required string NormalizedEmail { get; set; }

    /// <summary>显示名,进入令牌的 <c>name</c> 声明。留空时退回用户名。</summary>
    public string? DisplayName { get; set; }

    /// <summary>口令散列(PBKDF2,由 <see cref="Microsoft.AspNetCore.Identity.PasswordHasher{TUser}" /> 产生)。</summary>
    public required string PasswordHash { get; set; }

    /// <summary>
    /// 安全戳。改口令或禁用账号时换一个新值 —— 已签发的刷新令牌会在下次续期时对不上而失效。
    /// </summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>创建时间(UTC)。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>最近一次登录成功的时间(UTC)。</summary>
    public DateTime? LastLoginAt { get; set; }

    /// <summary>是否已停用。停用后既登不进来,已有的刷新令牌也换不出新的访问令牌。</summary>
    public bool IsDisabled { get; set; }

    /// <summary>连续登录失败次数,成功一次即清零。</summary>
    public int AccessFailedCount { get; set; }

    /// <summary>锁定到期时间(UTC)。为空表示未锁定。</summary>
    public DateTime? LockoutEndsAt { get; set; }
}
