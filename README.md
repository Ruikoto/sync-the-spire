<p align="center">
  <img src="SyncTheSpire/assets/icon_origin.png" alt="Sync the Spire" width="128" />
</p>

<h1 align="center">Sync the Spire</h1>

<p align="center">
  一键同步《杀戮尖塔2》Mod 配置，和朋友用同一套 Mod 联机。
</p>

<p align="center">
  <a href="https://github.com/Ruikoto/sync-the-spire/releases">下载最新版</a>
</p>

> **⚠️ 首次使用前请手动备份一次存档和 Mod 文件夹。** 本软件经过初步测试，但多一道保险总没错。非营利性开源项目，开发者不对数据损失负责。

## 下载安装

前往 [Releases](https://github.com/Ruikoto/sync-the-spire/releases) 下载，解压即用，无需安装。

## 快速上手

1. 运行 `SyncTheSpire.exe`
2. 填写**远程仓库地址**（房主提供给你的链接），选择认证方式
3. 填写**游戏安装路径**
4. 点击「保存并初始化」，等待完成即可

<details>
<summary>💡 认证方式怎么选？</summary>

| 方式 | 适用场景 |
|------|----------|
| **免密** | 公开仓库，或 Gitee 仓库（推送时系统会弹出登录窗口） |
| **HTTPS** | GitHub 等平台的私有仓库，填用户名和 Token |
| **SSH** | 已配置 SSH 密钥的用户 |

</details>

<details>
<summary>💡 游戏安装路径怎么找？</summary>

Steam 中右键游戏 → 管理 → 浏览本地文件。

</details>

<details>
<summary>💡 存档路径怎么找？</summary>

启动游戏后按 <code>`</code> 键打开控制台，输入 `open saves` 回车。

</details>

## 使用方式

### 👑 做房主 — 分享你的 Mod 配置

1. 输入一个分支名（比如你的昵称），点击「创建」
2. 像平时一样安装、调整 Mods
3. 搞定后点「上传」

其他人就能同步到你的 Mod 配置了。

如果上传时发现云端有更新的内容（比如你在另一台电脑也改过），会弹窗让你选择保留哪边的版本。

### 🎮 跟随别人 — 使用房主的 Mod 配置

1. 点击「浏览分支」
2. 选择房主的分支，确认
3. 同步完成，直接开游戏
4. 之后点击「拉取」可以获取房主的最新 Mod 配置

### 🔌 Mod 开关

关闭 Mod 开关，游戏立刻恢复无 Mod 的原版状态。Mod 不会被删除，重新打开即可恢复。

### 🔀 存档重定向

杀戮尖塔2 的普通模式和 Mod 模式各有一套独立存档。开启存档重定向后，Mod 模式会直接使用普通模式的存档，角色进度互通。随时可以关闭恢复独立存档。

### 💾 存档备份

一键备份整个存档文件夹。需要恢复时选择历史备份点即可，恢复前会自动再备份一次当前状态，不怕选错。

## 常见问题

<details>
<summary>没装过 Git 能用吗？</summary>

能。软件会自动下载所需的 Git 组件，不需要你手动安装任何东西。

</details>

<details>
<summary>会影响游戏本体文件吗？</summary>

不会。软件只操作 Mods 文件夹和存档文件夹，不修改游戏本体。关闭 Mod 开关即可恢复原版状态。

</details>

<details>
<summary>我的账号密码安全吗？</summary>

认证信息使用 Windows 系统级加密存储，不上传到任何服务器，只有你本机当前用户能读取。

</details>

<details>
<summary>支持 Gitee 吗？</summary>

支持。Gitee 仓库推荐用「免密」方式。

</details>

---

<details>
<summary>开发者信息</summary>

**技术栈：** .NET 10 · WinForms · WebView2 · LibGit2Sharp · Tailwind CSS · GitHub Actions

**从源码构建：** 需要 Windows 10/11 + .NET 10 SDK

```bash
dotnet run --project SyncTheSpire

# 发布（自包含 x64）
dotnet publish SyncTheSpire -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true

# 发布（自包含 ARM64）
dotnet publish SyncTheSpire -c Release -r win-arm64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true
```

</details>
