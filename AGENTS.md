# AGENTS.md

> 给 AI 代理与新加入者的操作约定。**动手之前先读完本文件,以及它指向的文档。**

## 一、开工前必读:velashell-docs

VelaShell 生态的**全部文档**集中在一个仓库:
**[VelaShellLabs/velashell-docs](https://github.com/VelaShellLabs/velashell-docs)**。
本仓库**不放** `docs/`、`docs-en/` —— 设计手册、开发规范与开发文档都在那边。

**在动任何代码之前**,先把下表中与你要改的部分相关的几篇读掉。跳过这一步直接改,
结果通常是两种:与既有设计冲突,或者重复实现一个已经存在的能力。

| 位置 | 内容 |
| --- | --- |
| [`zh/host/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/host) | 宿主分层架构与依赖方向、工程化重构蓝图、交互与界面规格、快捷键参考、设置项审计,以及 SFTP / FTP / Telnet / 串口 / Redis / S3 / 系统密钥链等可行性调研 |
| [`zh/plugins/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/plugins) | 插件系统设计蓝图 01–15(进程模型、IPC 协议、权限系统、UI 扩展、威胁模型、路线图)与[进度总览 STATUS](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/plugins/STATUS.md) |
| [`zh/sdk/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/sdk) | 插件契约 SDK 参考、SDK 仓库的发版流程 |
| [`zh/cli/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/cli) | `vela-plugin` 命令行手册、CLI 仓库的发版流程 |
| [`zh/templates/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/zh/templates) | 插件开发指南、打包与发布、模板仓库的发版流程 |

英文镜像在 [`en/`](https://github.com/VelaShellLabs/velashell-docs/tree/main/en),与 `zh/` 同构。
[仓库首页](https://github.com/VelaShellLabs/velashell-docs)有按「我想做什么」组织的快速入口表。

## 二、涉及文档的改动一律同步到 velashell-docs

**这是本文件最重要的一条。**

- 本仓库里**不新建** `docs/`、`docs-en/` 或任何成体系的文档目录。要写文档,去 velashell-docs 开 PR。
- 改了代码,而**行为、接口、配置项、命令行、构建流程或版本纪律**与现有文档对不上时,
  必须**同时**在 velashell-docs 提一个 PR 把文档改过来。两个 PR 在正文里互相引用,一起合。
  只改代码不改文档,等于让文档开始骗人 —— 而文档是别人照抄的。
- velashell-docs 的 `zh/` 与 `en/` 是**互为镜像**的两棵树,文件一一对应。改了中文就要改英文,
  反之亦然。漏一边,两棵树就开始漂。
- velashell-docs 内部的互相引用**一律走相对路径**(如 `../templates/dev-guide.md`),
  不要写回 GitHub 绝对 URL —— 文档集中到一个仓库,消掉的正是那种一改路径就断的跨仓库链接。
- **例外**:留在代码仓库里的少数几份文件不适用上述规则,因为它们服务的是「在这个仓库里写代码」
  这件事,搬走只会离使用场景更远。各仓库的例外清单见下面第三节。


## 三、本仓库:velashell-identity(统一认证服务)

VelaShell 生态的 OIDC 授权服务器(OpenIddict + MongoDB)。账号、登录、注册、改口令都在这里,
下游服务(插件市场、资讯服务…)只验令牌、不碰口令。

2026-08-30 从 velashell-markets 拆出 —— 它已经不只服务插件市场了,
而一个信任根不该跟着某个业务仓库的发版节奏走。

### 跑起来

```bash
docker compose up -d          # compose 自带 MongoDB,能独立跑
dotnet build VelaShell.Identity.slnx
```

### ⚠️ 三个"改了就出事"的值

拆分时刻意保留了原值。看到它们别"顺手统一命名":

| 值 | 在哪 | 改了会怎样 |
| --- | --- | --- |
| `velashell-market-identity` | `Program.cs` 的 `SetApplicationName` | DataProtection 密钥环隔离键。改 = 换一套密钥,**所有人当场被登出** |
| `velashell-identity` | 连接串库名 | 账号所在地。改 = **所有账号消失** |
| `Identity:Issuer` | compose / 配置 | 令牌里的 `iss`。改 = **所有已发出的令牌失效**,下游全要跟着改 |

名字里那个 "market" 是历史包袱,不是笔误 —— `Program.cs` 里有一段注释专门说明这件事。

`identity-keys` 卷里是签名与加密密钥,**丢了等于换签发者**,要进备份清单。

### 几条硬约束

- **本服务不管授权。** 它只回答"你是 `sub=xxx`";"你能干什么"由每个下游自己判定
  (白名单,fail-closed)。把角色塞进认证服务,意味着每加一种权限就要动这个信任根。
  这条在 README 里展开写了,加功能前先读。
- **每个下游一个独立 scope**,不要复用。令牌不该跨服务通用。
- **客户端与 scope 靠配置覆盖进库**,每次启动都写一遍。所以"改配置 → 重启"是唯一的
  管理方式 —— 别去手工改 MongoDB,那会造出配置与库不一致的第三种状态。
- **回跳白名单是防钓鱼的第一道闸**,新客户端的 `RedirectUris` 必须锁死在它自己的域名上。
- **`_id` 就是 `sub`,一经签发不可更改**:下游存的归属关系全指向它。

### 留在本仓库的文档

`README.md`、`AGENTS.md`。下游怎么接入见各自仓库的 README;
拆分前的历史说明在 velashell-markets 的 `docs/identity-integration.md`。
