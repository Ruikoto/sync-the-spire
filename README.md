<p align="center">
  <img src="SyncTheSpire/assets/icon_origin.png" alt="Sync the Spire" width="100" />
</p>

<h2 align="center">Sync the Spire</h2>

<p align="center">
  一键同步《杀戮尖塔 2》Mod 配置，和朋友用同一套 Mod 联机。
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows%2010%2F11-0078D4?logo=windows&logoColor=white&style=flat" alt="Windows" />
  <img src="https://img.shields.io/badge/license-MIT-22c55e?style=flat" alt="MIT" />
</p>

<p align="center">
  <a href="https://get.microsoft.com/installer/download/9pc112t0c074?referrer=appbadge">
    <img src="https://sts.rkto.cc/ms-store-badge.svg" alt="从 Microsoft Store 获取" width="200" />
  </a>
</p>

<p align="center">
  <a href="https://github.com/Ruikoto/sync-the-spire/releases">GitHub Releases</a>
  &nbsp;·&nbsp;
  <a href="https://sts.rkto.cc">📖 完整文档</a>
</p>

<br />

<p align="center">
  <img src="https://sts.rkto.cc/Screenshot1.png" alt="主界面" width="520" />
</p>

---

> **⚠️ 首次使用前，请手动备份一次存档和 Mod 文件夹。** 非营利性开源项目，开发者不对任何数据损失负责。

## 功能

- 🔄 **一键同步** — 房主上传 Mod 配置，队友一键拉取，无需手动拷贝
- 📋 **同步前预览** — 浏览分支时可查看该分支包含的所有 Mod 及版本
- 🔞 **NSFW 提示** — 分支列表中自动标注包含 NSFW 内容的配置
- 🔌 **Mod 开关** — 一键切回无 Mod 原版状态，Mod 不会被删除
- 🔀 **存档重定向** — Mod 模式与原版模式进度互通，随时可关闭
- 💾 **存档备份** — 一键备份/恢复，恢复前自动再备一次，不怕选错
- 🔒 **安全** — 认证信息使用 Windows 系统级加密，不上传任何服务器
- ⚡ **开箱即用** — 内置 Git 组件，支持 GitHub / Gitee / SSH 多种认证

## 快速上手

**作为队友，跟随房主的 Mod：**

1. 运行程序，填写房主给你的**仓库地址**，选择认证方式
2. 填写**游戏安装路径**，点击「保存并初始化」
3. 初始化完成后，点击「切换分支」→ 选择房主的分支 → 「同步此分支」

**作为房主，分享你的 Mod：**

1. 输入一个分支名（比如你的昵称），点击「创建」
2. 安装、调整好你的 Mods
3. 点击「上传」

<br />

<p align="center">
  <img src="https://sts.rkto.cc/Screenshot2.png" alt="分支列表" width="330" />
  &nbsp;&nbsp;
  <img src="https://sts.rkto.cc/Screenshot3.png" alt="Mod 预览" width="330" />
</p>

<p align="center"><sub>分支列表 · 同步前 Mod 预览</sub></p>

更多说明（认证方式、存档路径查找、常见问题等）请查阅 **[完整文档 →](https://sts.rkto.cc)**

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
