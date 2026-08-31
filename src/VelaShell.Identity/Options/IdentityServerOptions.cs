namespace VelaShell.Identity.Options;

/// <summary>
/// 统一认证服务自身的配置:对外身份(issuer)、令牌寿命与签名密钥的落盘位置。
/// </summary>
public sealed class IdentityServerOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "Identity";

    /// <summary>
    /// 签发者标识。**必须是浏览器与资源服务器都能访问到的地址**,令牌里的 <c>iss</c>、
    /// discovery 文档里的所有端点 URL 都由它推导。换地址就等于换签发者,老令牌会全部失效。
    /// </summary>
    public string Issuer { get; set; } = "http://localhost:7020";

    /// <summary>
    /// 是否要求 OAuth 端点走 HTTPS。**生产环境必须为 true**;
    /// 本机用 http://localhost:7020 起服务时才关掉它。
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>
    /// 签名与加密密钥的存放目录。首次启动生成,之后重复使用 ——
    /// 目录不持久化的话每次重启都会换一套密钥,表现为"昨天登录的人今天全被登出"。
    /// </summary>
    public string KeyDirectory { get; set; } = "keys";

    /// <summary>访问令牌寿命。短一点更安全,前端会用刷新令牌自动续期。</summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>身份令牌(id_token)寿命。</summary>
    public TimeSpan IdentityTokenLifetime { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>刷新令牌寿命,也就是"多久不来就要重新登录"。</summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// 要注册的 API 资源(scope)。市场用 <c>velashell-market</c> 作为受众。
    /// 默认值写在 appsettings.json 里而不是这里 —— 配置绑定给数组是"在已有元素后面追加",
    /// 代码里再放一份默认值会让配好的那份变成第二个元素。
    /// </summary>
    public ApiScopeOptions[] Scopes { get; set; } = [];

    /// <summary>要注册的客户端。同样见 appsettings.json。</summary>
    public ClientOptions[] Clients { get; set; } = [];
}

/// <summary>一个 API 资源(scope)的声明。</summary>
public sealed class ApiScopeOptions
{
    /// <summary>scope 名,客户端在授权请求里写的就是它。</summary>
    public string Name { get; set; } = "";

    /// <summary>展示名,授权页与管理界面用。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// 该 scope 对应的资源标识。它们会成为访问令牌里的 <c>aud</c> ——
    /// 市场 API 的 <c>Auth:Audience</c> 必须命中其中之一,否则一律 401。
    /// </summary>
    public string[] Resources { get; set; } = [];
}

/// <summary>一个客户端(应用)的声明。</summary>
public sealed class ClientOptions
{
    /// <summary>客户端标识。</summary>
    public string ClientId { get; set; } = "";

    /// <summary>展示名。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// 客户端密钥。**浏览器里的单页应用必须留空** —— 前端没有可保密的地方,
    /// 留空即注册为公开客户端,强制走 PKCE。只有服务端到服务端的客户端才该填。
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>登录成功后允许回跳的地址。不在这个白名单里的 redirect_uri 会被直接拒绝。</summary>
    public string[] RedirectUris { get; set; } = [];

    /// <summary>退出登录后允许回跳的地址。</summary>
    public string[] PostLogoutRedirectUris { get; set; } = [];

    /// <summary>该客户端可以申请的 API scope(<c>openid</c> / <c>profile</c> / <c>email</c> 默认就给)。</summary>
    public string[] Scopes { get; set; } = [];
}

/// <summary>账号策略:自助注册开不开、口令下限、锁定规则,以及首个管理账号。</summary>
public sealed class AccountOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "Accounts";

    /// <summary>是否允许自助注册。关掉之后只能由播种或运维直接建账号。</summary>
    public bool AllowSelfRegistration { get; set; } = true;

    /// <summary>口令最小长度。</summary>
    public int MinimumPasswordLength { get; set; } = 8;

    /// <summary>连续失败多少次后锁定。设为 0 表示不锁 —— 那等于把口令暴力破解的门开着。</summary>
    public int MaxFailedAttempts { get; set; } = 8;

    /// <summary>锁定时长。</summary>
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// 首个账号:集合为空时按它建一个,方便第一次部署有人能登录。
    /// 留空则不建,启动日志里会提示去注册页自助注册。
    /// </summary>
    public BootstrapAccountOptions? Bootstrap { get; set; }

    /// <summary>
    /// 重置链接的有效期。短一点更安全 —— 这封信可能一直躺在收件箱里,
    /// 而链接的效力等同于口令本身。
    /// </summary>
    public TimeSpan PasswordResetLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 同一个账号两次发信的最小间隔。挡的是"拿注册页当免费短信炮台"那种滥用 ——
    /// 攻击者不需要登录就能触发发信,没有节流等于把别人的收件箱交出去。
    /// </summary>
    public TimeSpan PasswordResetResendInterval { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>首个账号的凭据。</summary>
public sealed class BootstrapAccountOptions
{
    /// <summary>用户名。</summary>
    public string UserName { get; set; } = "";

    /// <summary>口令。</summary>
    public string Password { get; set; } = "";

    /// <summary>邮箱。**必填** —— 与自助注册同一条规则,留空建不出账号(见 <c>AccountStore.CreateAsync</c>)。</summary>
    public string Email { get; set; } = "";

    /// <summary>显示名,可留空。</summary>
    public string? DisplayName { get; set; }
}

/// <summary>
/// 发信设置(找回口令要用)。
///
/// **没配 <see cref="Host" /> 就等于关掉找回口令功能** —— 这是刻意的:与其在没有出口的情况下
/// 假装发出了一封信、让用户干等,不如在页面上直说"本部署没开通发信,请联系管理员"。
/// </summary>
public sealed class MailOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "Mail";

    /// <summary>SMTP 主机名。留空表示本部署不发信。</summary>
    public string Host { get; set; } = "";

    /// <summary>SMTP 端口。587 = STARTTLS,465 = 隐式 TLS,25 = 明文。</summary>
    public int Port { get; set; } = 587;

    /// <summary>
    /// 是否使用隐式 TLS(端口 465 那种"一连上就是 TLS")。
    /// 为 false 时按 STARTTLS 协商,若服务器不支持则降级为明文 —— 587 端口填 false。
    /// </summary>
    public bool UseImplicitTls { get; set; }

    /// <summary>SMTP 用户名。留空表示不认证(内网中继才可能这样)。</summary>
    public string UserName { get; set; } = "";

    /// <summary>SMTP 口令。多数邮箱服务商这里要填的是「授权码」而不是登录口令。</summary>
    public string Password { get; set; } = "";

    /// <summary>发件地址。留空则回落到 <see cref="UserName" /> —— 多数服务商要求两者一致。</summary>
    public string FromAddress { get; set; } = "";

    /// <summary>发件人显示名。</summary>
    public string FromName { get; set; } = "VelaShell 统一认证";

    /// <summary>本部署是否具备发信能力。</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(Host);

    /// <summary>实际用于发信的地址。</summary>
    public string EffectiveFrom => string.IsNullOrWhiteSpace(FromAddress) ? UserName : FromAddress;
}
