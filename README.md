# Sync the Spire

基于 Git 的极简 Mod 同步器，为《杀戮尖塔2》等缺乏官方创意工坊的游戏设计。

面向完全不懂代码和 Git 的纯小白玩家 —— 底层用 Git 做版本控制和同步，UI 层完全隐藏 Git 概念，包装为"存档"、"同步"、"覆盖"等游戏术语。

## 下载

前往 [Releases](https://github.com/Ruikoto/sync-the-spire/releases) 下载最新版本。

## 快速开始

1. 下载并解压，运行 `SyncTheSpire.exe`
2. 填写远程仓库 URL（支持 HTTPS / SSH / 匿名）
3. 填写游戏安装路径和存档路径（可选）
4. 点击「保存并初始化」

> **游戏安装路径怎么找？** Steam 中右键游戏 → 管理 → 浏览本地文件。
>
> **存档路径怎么找？** 启动游戏后按 `` ` `` 键打开控制台，输入 `open saves` 回车。

## 功能介绍

### Mod 同步

核心功能。每个人通过「分支」管理自己的 Mod 配置，所有人共享同一个 Git 仓库。

**作为房主（分享 Mod 配置的人）：**

1. 在「做房主」区域输入分支名，点击「创建」
2. 按自己的喜好安装、调整 Mods 文件夹里的内容
3. 点击「保存改动并上传」，Mod 配置就会同步到云端

**作为跟随者（使用别人 Mod 配置的人）：**

1. 点击「浏览分支」，选择房主的分支
2. 确认后自动拉取该分支的 Mod 配置，完成后直接启动游戏即可

### 纯净模式

点击「清空 Mod」会断开 Mods 文件夹的链接，游戏恢复原版状态。Mod 数据不会删除，随时可通过「恢复 Mod」重新连接。

### 存档合并

杀戮尖塔2 的普通模式和 Mod 模式使用不同的存档文件夹。开启存档合并后，两者共用同一份存档，角色进度在两种模式间互通。

- **合并** — 两种模式共享存档进度
- **取消合并** — 断开链接，Mod 存档恢复为独立副本

如果合并时检测到两边都有存档数据，会弹出比较界面让你选择保留哪一份。所有操作前均自动备份。

### 存档备份

支持手动备份整个存档文件夹，需要时恢复到任意备份点。恢复前也会自动备份当前状态，防止误操作。

### 更新检测

启动时自动检查新版本，有更新时弹窗提示。也可在 About 页面手动检查和下载。

## 原理简述

```
游戏目录\Mods\  ──(NTFS Junction)──>  %LocalAppData%\SyncTheSpire\Repo\
```

通过 NTFS Junction（目录联接）将游戏 Mods 文件夹指向 AppData 下的 Git 仓库。游戏读取 Mods 时直接穿透到仓库目录，`.git` 等文件不会被游戏扫到。所有 Git 操作在后台自动完成，用户无需了解 Git。

## 从源码构建

需要 Windows 10/11 和 .NET 10 SDK。

```bash
# 开发运行
dotnet run --project SyncTheSpire

# 发布单文件 EXE（自包含）
dotnet publish SyncTheSpire -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true
```

## 技术栈

.NET 10 + WinForms + WebView2 | LibGit2Sharp | HTML/JS/TailwindCSS | GitHub Actions + GitHub Pages
