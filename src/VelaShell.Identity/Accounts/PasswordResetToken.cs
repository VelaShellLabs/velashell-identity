using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace VelaShell.Identity.Accounts;

/// <summary>
/// 一张找回口令的凭据。
///
/// **库里存的是散列,不是令牌本身。** 明文令牌只出现在两个地方:发出去的那封信里,
/// 和用户点回来时的地址栏里。这样即便整个 accounts 库被拖走,拿到的也只是一堆散列 ——
/// 而一张有效的重置令牌等价于口令本身,能直接接管账号。
///
/// 用独立集合而不是内嵌在账号上:重置令牌是**短命且会堆积**的东西,
/// 挂在账号文档里意味着每次登录都要顺带读一堆过期垃圾,而且没法用 TTL 索引自动清。
/// </summary>
public sealed class PasswordResetToken
{
    /// <summary>主键。</summary>
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

    /// <summary>这张令牌属于哪个账号(<c>IdentityAccount.Id</c>,也就是 sub)。</summary>
    public required string Subject { get; set; }

    /// <summary>明文令牌的 SHA-256(小写十六进制)。唯一索引建在它上面。</summary>
    public required string TokenHash { get; set; }

    /// <summary>签发时间(UTC)。发信节流按它算。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>过期时间(UTC)。TTL 索引建在它上面,过期后由 MongoDB 自己清掉。</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// 被使用的时间(UTC)。为空表示还没用过。
    ///
    /// 用过的令牌**标记而不是删除**:删掉的话,"这张链接已经用过了"与"这张链接根本不存在"
    /// 会退化成同一种结果,出问题时没法回答"到底是谁在什么时候重置的"。
    /// 真正的清理交给 TTL 索引。
    /// </summary>
    public DateTime? UsedAt { get; set; }
}
