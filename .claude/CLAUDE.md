# Project Memory

Instructions here apply to this project and are shared with team members.

## Context

## Release Metadata (`release-info.json`)

每次发版前编辑，提交后再打 tag。CI 自动读取并合并到 gh-pages 上的 `version.json`。

| 字段 | 类型 | 说明 |
|---|---|---|
| `changelog` | string | 更新日志，留空则前端不显示 changelog 区域 |
| `force_update` | boolean | 强制更新。`true` 时用户无法关闭更新弹窗 |

## Announcements (`announcements.json`)

编辑后推送到 main 即自动部署到 gh-pages（由 `deploy-announcements.yml` 处理）。

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | string | 唯一标识符，用于 localStorage 记录用户已关闭的公告 |
| `enabled` | boolean | 总开关，`false` = 草稿不展示 |
| `title` | string | 标题（加粗显示），可留空 |
| `content` | string | 正文内容 |
| `type` | `"info"` \| `"warning"` \| `"error"` | 横幅颜色：蓝 / 黄 / 红 |
| `expires_at` | string \| null | ISO 8601 过期时间，`null` = 永不过期 |
| `dismissible` | boolean | 是否允许用户关闭，`false` = 每次启动都显示 |