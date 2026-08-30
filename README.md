# VelaShell 统一认证服务(velashell-identity)

VelaShell 生态的 **OIDC 授权服务器**(OpenIddict + MongoDB)。账号、登录、注册、改口令都在这里,
下游服务只验令牌、不碰口令。

```
浏览器                     统一认证 (7020)              下游服务(市场 / 资讯 / …)
  │  点"登录"                    │                            │
  ├──── /connect/authorize ─────▶│                            │
  │                              │ 没登录 → 登录页/注册页        │
  │◀──── 302 回 /callback?code ──┤                            │
  ├──── /connect/token ─────────▶│                            │
  │◀──── access_token(JWT) ─────┤                            │
  ├──── Authorization: Bearer ───┼───────────────────────────▶│
  │                              │◀── 拉 JWKS 验签(仅第一次)──┤
```

> **2026-08-30 从 [velashell-markets](https://github.com/VelaShellLabs/velashell-markets) 拆出。**
> 拆分的理由是它已经不只服务插件市场了:资讯服务在用,以后还会有别的。
> 一个信任根不该跟着某个业务仓库的发版节奏走。

## 跑起来

```powershell
cp .env.example .env      # 至少改掉 MONGO_ROOT_PASSWORD
docker compose up -d
```

本仓库的 compose **自带一个 MongoDB**,所以它能独立跑起来 —— 开发认证服务不必先起一整套业务系统。
生产上把 `MONGO_CONNECTION` 指向共享副本集即可(库名仍是 `velashell-identity`)。

```bash
dotnet build VelaShell.Identity.slnx
```

| 地址 | 是什么 |
| --- | --- |
| <http://localhost:7020> | 登录后首页会显示你的 `sub` |
| <http://localhost:7020/account/register> | 注册 |
| <http://localhost:7020/.well-known/openid-configuration> | discovery |

第一次进来没有任何账号,两条路选一条:打开注册页自助注册;或在 `.env` 里填
`BOOTSTRAP_USER` / `BOOTSTRAP_PASSWORD`,启动时会建好第一个账号,`sub` 打在启动日志里。

## ⚠️ 三个"改了就出事"的值

拆分时刻意保留了原值,别"顺手统一命名":

| 值 | 在哪 | 改了会怎样 |
| --- | --- | --- |
| `velashell-market-identity` | `Program.cs` 的 `SetApplicationName` | DataProtection 的密钥环隔离键。改了 = 换一套密钥,**所有人当场被登出**,正停在登录页的人还会拿到防伪校验失败 |
| `velashell-identity` | 连接串里的库名 | 账号所在地。改了 = **所有账号消失** |
| `Identity:Issuer` | compose / 配置 | 令牌里的 `iss`。改了 = **所有已发出的令牌立刻失效**,且每个下游服务的 `Auth__Issuer` 都要同步改 |

名字里那个 "market" 是历史包袱,不是笔误。

另外 `identity-keys` 卷里是签名与加密密钥,**丢了等于换签发者** —— 它要进备份清单。

## 接一个新服务进来

客户端与 scope **不需要手工写库**:本服务每次启动都按配置覆盖进 MongoDB,
于是"改配置 → 重启"是唯一的管理方式,不会出现配置与库不一致的第三种状态。

在 `docker-compose.yml` 里加两组环境变量(索引接着已有的往后排):

```yaml
# 新的 API 资源(scope)。它决定下游令牌里的 aud。
- Identity__Scopes__2__Name=velashell-newthing
- Identity__Scopes__2__Resources__0=velashell-newthing
# 新服务的客户端。回跳地址必须锁死在那个服务自己的域名上 —— 这是防钓鱼的第一道闸。
- Identity__Clients__2__ClientId=velashell-newthing-web
- Identity__Clients__2__DisplayName=某个新服务
- Identity__Clients__2__ClientSecret=${NEWTHING_CLIENT_SECRET}   # 有后端的机密客户端才填
- Identity__Clients__2__RedirectUris__0=https://newthing.example.com/signin-oidc
- Identity__Clients__2__PostLogoutRedirectUris__0=https://newthing.example.com/
- Identity__Clients__2__Scopes__0=velashell-newthing
```

**每个服务给自己一个独立的 scope,不要复用别人的。** 令牌不该跨服务通用 ——
市场用户的令牌不该能打到资讯服务上。

### 授权:本服务不管

**认证服务只回答"你是 `sub=xxx`"。"你能干什么"由每个下游服务自己判定。**

这不是偷懒,是有意的分层:市场的审核员、资讯服务的管理员,都是那些服务自己的概念。
把角色塞进认证服务,意味着每加一种权限就要动这个信任根 —— 而它是最不该频繁改动的东西。

下游的做法都一样:配一份 `sub` 白名单,空列表 = 谁都进不去(fail-closed)。
参考 `velashell-markets` 的 `Auth:ModeratorSubjects` 与 `velashell-feeds` 的 `Auth:AdminSubjects`。

### 开放注册与"只让管理员进"

本服务对所有人开放注册(`Accounts__AllowSelfRegistration`)。这**不影响**下游服务限制访问:
任何人都能注册、都能拿到令牌,但拿不到任何下游的准入 —— 那由白名单决定。

需要一个完全不对外的部署时,把 `ALLOW_SELF_REGISTRATION` 关掉,用 `BOOTSTRAP_USER` 建账号。

## 协议这边定了什么

| 项 | 值 | 为什么 |
| --- | --- | --- |
| 流程 | 授权码 + **强制 PKCE** | 浏览器里的公开客户端没地方藏密钥,PKCE 是它唯一能证明"换码的人就是发起授权的人"的手段 |
| 关掉的流程 | 隐式、口令 | 隐式流会把令牌塞进地址栏;口令流让第三方页面直接碰到用户口令 |
| 同意页 | 不弹(`ConsentTypes.Implicit`) | 都是第一方应用。用户点了"登录"就是同意,再问一遍只是噪音 |
| 访问令牌 | **不加密**的 JWT,1 小时 | 下游是独立的资源服务器,要靠 JWKS 自行验签解析。OpenIddict 默认会加密,代码里显式关掉了 |
| 授权码/刷新令牌 | 加密,14 天 | 纯内部凭据,外面没有任何人需要读得懂 |

## issuer:唯一一个容易配错的地方

`Identity:Issuer` 同时是三样东西:令牌里 `iss` 的值、discovery 文档里所有端点 URL 的前缀
(**包括 `jwks_uri`**)、前端点"登录"时跳过去的地址。所以它必须是**浏览器打得开**的地址。

下游服务跑在容器里时,那里的 `localhost` 指的是它自己。于是下游侧分成两个配置:

| 配置 | 含义 |
| --- | --- |
| `Auth:Issuer` | 令牌里 `iss` 应该长什么样。校验用 |
| `Auth:Authority` | 下游实际能访问到的地址。拉 discovery 与 JWKS 用 |

> 令牌里的 `iss` 一定带结尾斜杠(`http://localhost:7020/`),因为 OpenIddict 用 `Uri` 表示 issuer。
> 配置里几乎没人会带。下游两种写法都要认 —— 只认一种的话,这个差别会精确地表现成
> "登录成功,但调任何接口都是 401"。

## 账号

存在 MongoDB 的 `accounts` 集合,口令用框架自带的 `PasswordHasher<T>`(PBKDF2)散列。
没有引入 ASP.NET Core Identity 那一整套 —— 它的价值在角色、双因素、外部登录等一批这里用不到的能力,
代价是一层要专门适配 MongoDB 的抽象。

**`_id` 就是 `sub`,一经签发不可更改**:市场那边的 `Plugin.OwnerSubject`、`Review.Subject`,
资讯服务的 `AdminSubjects`,存的都是它。换了 sub 等于换了个人。
用户名和邮箱都允许改,sub 不允许。
