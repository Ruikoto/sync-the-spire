<p align="center">
  <img src="SyncTheSpire/assets/icon_origin.png" alt="Sync the Spire" width="128" />
</p>

<h1 align="center">Sync the Spire</h1>

基于 Git 的极简 Mod 同步器，为《杀戮尖塔2》等缺乏官方创意工坊的游戏设计。

面向完全不懂代码和 Git 的纯小白玩家 —— 底层用 Git 做版本控制和同步，UI 层完全隐藏 Git 概念，包装为"存档"、"同步"、"覆盖"等游戏术语。

## ⚠️ 数据安全警告

使用前请手动备份一次，虽然初步测试没任何问题，但此软件未经过广泛与全面的测试，**多一道保险总没错**。本仓库为非营利性开源项目，开发者不对任何数据损失负责。

## 下载

前往 [Releases](https://github.com/Ruikoto/sync-the-spire/releases) 下载最新版本。

提供两种构建：
- **自包含版（推荐）** — 无需安装 .NET 运行时，开箱即用
- **框架依赖版** — 体积更小，需要系统已安装 .NET 10 运行时

同时提供 x64 和 ARM64 架构，程序会自动识别并推荐对应版本。

## 快速开始

1. 下载并解压，运行 `SyncTheSpire.exe`
2. 填写远程仓库 URL，选择认证方式：
   - **免密** — 公开仓库无需认证（推荐 Gitee 用户使用）
   - **HTTPS** — 用户名 + 密码/Token
   - **SSH** — 私钥路径 + 可选口令
3. 填写游戏安装路径和存档路径（可选）
4. 点击「保存并初始化」

> **游戏安装路径怎么找？** Steam 中右键游戏 → 管理 → 浏览本地文件。
>
> **存档路径怎么找？** 启动游戏后按 `` ` `` 键打开控制台，输入 `open saves` 回车。

如果系统上未安装 Git，程序会自动下载 MinGit 便携版，无需手动安装。

## 功能介绍

### Mod 同步

核心功能。每个人通过「分支」管理自己的 Mod 配置，所有人共享同一个 Git 仓库。

**作为房主（分享 Mod 配置的人）：**

1. 在「做房主」区域输入分支名，点击「创建」
2. 按自己的喜好安装、调整 Mods 文件夹里的内容
3. 点击「保存改动并上传」，Mod 配置就会同步到云端

上传时会自动检测冲突（例如其他设备也推送了更新）。冲突时弹出对话框，可以选择「使用本地版本（覆盖云端）」或「使用云端版本（覆盖本地）」。

**作为跟随者（使用别人 Mod 配置的人）：**

1. 点击「浏览分支」，选择房主的分支
2. 确认后自动拉取该分支的 Mod 配置，完成后直接启动游戏即可

### 纯净模式

点击「清空 Mod」会断开 Mods 文件夹的链接，游戏恢复原版状态。Mod 数据不会删除，随时可通过「恢复 Mod」重新连接。

### 存档重定向

杀戮尖塔2 的普通模式和 Mod 模式使用不同的存档文件夹。开启存档重定向后，Mod 模式会直接读取普通模式的存档，角色进度在两种模式间互通。

- **开启** — Mod 模式使用普通模式的存档
- **关闭** — 恢复为独立存档

> 存档重定向通过内置辅助 Mod 实现（感谢 @皮一下就很凡），开启时会自动安装。

### 存档备份

支持手动备份整个存档文件夹，需要时恢复到任意备份点。恢复前也会自动备份当前状态，防止误操作。创建 Mod 链接时也会自动备份原有 Mod 文件夹。

### 自动更新

启动时自动检查新版本。根据版本差异采取不同策略：

- **强制更新** — 版本过旧时弹出不可关闭的更新窗口
- **弹窗提示** — 有新版本时弹窗提醒，可选择稍后更新
- **静默提示** — 小版本更新仅在 About 页面显示小红点

也可在 About 页面手动检查和下载。

### 应用内公告

支持推送公告到所有用户，显示为页面顶部的彩色横幅。支持不同级别（信息/警告/错误）、过期时间和用户关闭记忆。

## 原理简述

```
游戏目录\Mods\  ──(NTFS Junction)──>  %LocalAppData%\SyncTheSpire\Repo\
```

通过 NTFS Junction（目录联接）将游戏 Mods 文件夹指向 AppData 下的 Git 仓库。游戏读取 Mods 时直接穿透到仓库目录，`.git` 等文件不会被游戏扫到（`.git` 目录被分离存放）。所有 Git 操作在后台自动完成，用户无需了解 Git。

认证凭据使用 Windows DPAPI 加密存储，仅当前用户可解密。

## 从源码构建

需要 Windows 10/11 和 .NET 10 SDK。

```bash
# 开发运行
dotnet run --project SyncTheSpire

# 发布单文件 EXE（自包含，x64）
dotnet publish SyncTheSpire -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true

# 发布单文件 EXE（自包含，ARM64）
dotnet publish SyncTheSpire -c Release -r win-arm64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true
```

## 技术栈

.NET 10 + WinForms + WebView2 | LibGit2Sharp | HTML/JS/TailwindCSS | GitHub Actions + GitHub Pages
