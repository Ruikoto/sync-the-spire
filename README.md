# Sync the Spire

基于 Git 的极简 Mod 同步器，为《杀戮尖塔2》等缺乏官方创意工坊的游戏设计。

面向完全不懂代码和 Git 的纯小白玩家 —— 底层用 Git 做版本控制和同步，UI 层完全隐藏 Git 概念，包装为"存档"、"同步"、"覆盖"等游戏术语。

## 技术栈

- **框架**: .NET 10, WinForms + WebView2
- **Git 引擎**: LibGit2Sharp（无需用户安装 Git 客户端）
- **前端**: HTML + JavaScript + TailwindCSS (CDN)
- **通信**: WebView2 `PostWebMessageAsString` / `WebMessageReceived`，JSON 协议

## 核心架构

```
%LocalAppData%\SyncTheSpire\
├── config.json          # 用户配置（仓库URL、凭证、游戏路径）
├── Repo\                # 真实 Git 仓库（所有 Git 操作在此执行）
└── WebView2Data\        # WebView2 用户数据

游戏目录\Mods\  ──(Junction)──>  %LocalAppData%\SyncTheSpire\Repo\
```

**为什么要分离？** 游戏目录内不能有 `.git` 文件夹，且玩家可能随时删除 Mods 目录。通过 NTFS Junction（目录联接），游戏读 Mods 文件夹时直接穿透到 AppData 下的 Git 仓库，`.git` 不会被游戏扫到，玩家的修改也会被 Git 捕获。

## 三种模式

| 模式 | 说明 | 底层操作 |
|------|------|----------|
| **纯净模式** | 断开 Mod 连接，回归原版 | 删除 Junction，仓库数据保留在 AppData |
| **联机跟随** | 同步朋友的 Mod 配置（单向） | `fetch` → `reset --hard` → `clean -fd` |
| **本地折腾/做房主** | 创建自己的分支，双向同步 | `checkout -b` → `add .` → `commit` → `push` |

## 快速开始

### 前置要求

- Windows 10/11（WebView2 运行时已内置）
- .NET 10 SDK

### 构建运行

```bash
dotnet build SyncTheSpire/SyncTheSpire.csproj
dotnet run --project SyncTheSpire
```

### 首次配置

1. 启动后进入设置页面
2. 填写远程仓库 URL（HTTPS）
3. 填写用户名和 Token
4. 填写游戏 Mod 文件夹路径
5. 点击"保存并初始化"

程序会自动克隆仓库到 AppData，并创建 Junction 指向游戏 Mod 目录。

## 项目结构

```
SyncTheSpire/
├── Program.cs                # 入口
├── MainForm.cs               # WinForms 宿主窗口 + WebView2 初始化
├── Models/
│   ├── AppConfig.cs          # 配置模型
│   └── IpcMessages.cs        # IPC 请求/响应模型
├── Services/
│   ├── ConfigService.cs      # 配置读写
│   ├── GitService.cs         # LibGit2Sharp 封装
│   ├── JunctionService.cs    # NTFS Junction 管理
│   └── MessageRouter.cs      # IPC 消息分发
└── wwwroot/
    ├── index.html            # 前端页面
    ├── app.js                # 前端逻辑
    └── style.css             # 自定义样式
```

## IPC 协议

前端 → 后端:
```json
{ "action": "SYNC_OTHER_BRANCH", "payload": { "branchName": "coop-main" } }
```

后端 → 前端:
```json
{ "event": "SYNC_OTHER_BRANCH", "data": { "status": "success", "payload": { "message": "已同步到 coop-main" } } }
```

### Action 列表

| Action | 说明 |
|--------|------|
| `GET_STATUS` | 获取当前状态（是否已配置、当前分支、Junction 状态） |
| `INIT_CONFIG` | 保存配置、克隆仓库、创建 Junction |
| `GET_BRANCHES` | 获取远程分支列表 |
| `SWITCH_TO_VANILLA` | 纯净模式（断开 Junction） |
| `SYNC_OTHER_BRANCH` | 强制同步到指定分支 |
| `CREATE_MY_BRANCH` | 创建自己的分支 |
| `SAVE_AND_PUSH_MY_BRANCH` | 提交并上传本地改动 |
| `RESTORE_JUNCTION` | 恢复 Mod 连接 |
