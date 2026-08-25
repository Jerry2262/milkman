# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目概览

Milkman — Windows 专用的单文件 WinForms 浮窗，显示 ChatGPT Plus（Plus/Pro 订阅额度）与 OpenCode Go（5h/周/月三窗口额度）的用量。目标是保持个人工具的单文件、零依赖和低维护成本；不要增加跨平台层、项目文件或第三方包。UI 以 DeepSeekTimeline（`~/DeepSeekTimeline`）为视觉模板。

数据源均为**官方/半官方 HTTP 端点**，无本地数据库依赖：
- ChatGPT: `chatgpt.com/backend-api/wham/usage`（Bearer = `%USERPROFILE%\.codex\auth.json` 的 OAuth token；401 时经 `auth.openai.com/oauth/token` + client_id `app_EMoamEEZ73f0CkXaXp7hrann` 刷新并写回）
- OpenCode Go: `opencode.ai/zen/go/v1/usage`（Bearer = `opencode-go.key`；唯一配置模型为 `OpenCodeAccounts` 列表，每项包含显示名称和 auth.json 路径）

## 常用命令

```bash
# 编译（在 Windows cmd 中运行 build.bat，双击亦可）
build.bat

# 从 WSL 手动编译（等效于 build.bat，注意单引号保留 \\wsl.localhost 双反斜杠）
/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe /nologo /target:winexe /platform:anycpu \
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll \
  /out:"C:\Users\jerry\AppData\Local\Milkman\Milkman.exe" '\\wsl.localhost\Ubuntu-24.04\home\jerry\milkman\Main.cs'

# 重启浮窗（编译后验证）
powershell.exe -NoProfile -Command "Stop-Process -Name Milkman -Force -ErrorAction SilentlyContinue"
powershell.exe -NoProfile -Command "Start-Process 'C:\Users\jerry\AppData\Local\Milkman\Milkman.exe'"
```

## 架构（单文件 Main.cs，按 region 组织）

- `Program` — 入口；TLS1.2 显式设置（.NET 4.x 默认不协商，必踩坑）；写自启注册表；`DataDir()` = `%LOCALAPPDATA%\Milkman`
- `Config` — 强类型读写 `config.json`（窗口 X/Y、Docked、RefreshSeconds、OpenCodeAccounts）；程序固定开机自启，不设配置开关
- `DataFetcher` — 无状态网络层；`FetchGpt()` 与每个 `FetchGo(account)` 可并行调用；JSON 字段统一安全读取，序列化器只在短临界区加锁；错误消息截断 80 字
- `MilkmanForm` — 自绘 UI + 行为：
  - 版式：宽 520，单 OpenCode 账户时高 168，每增加一个账户高度增加 76；ChatGPT 按实际窗口数分 1–3 段；Brush/Pen/BandPath 缓存复用，低频动态文本直接测量，禁止缓存不断变化的倒计时字符串
  - 吸附：`WM_NCHITTEST`→`HTCAPTION` 系统级拖动；`WM_ENTERSIZEMOVE/EXITSIZEMOVE` 判定出入吸附（Top≤monTop+8 吸附，>monTop+30 脱离）；`DockTick` 30ms 缓出（dy/3，`Peek=4`）；双击 Pin 后立即展开并停止 DockTimer，取消 Pin 时恢复
  - 独立刷新：一个低频 `_refreshTimer` 仅负责调度；ChatGPT 与各 Go 账户分别维护 busy/auto/error/last-success，失败只暂停自身并保留旧数据，单击对应区块单独重试
  - 多屏：`monRect` 可能为负坐标屏（副屏在顶部），坐标计算要考虑
- `AutoStart` — HKCU Run 的 `Milkman` 键

## 已知坑（改动前必读）

1. **编译**：`/out` 不支持 UNC 路径（必须输出到 Windows 本地）；输出目录需先存在；从 bash 调 csc 时 UNC 源路径必须单引号包裹；只支持 C#5 语法（无 `?.`、字符串插值等）和 .NET4.x API（无 `ToUnixTimeSeconds`）
2. **Cloudflare 1010**：`opencode.ai` 按浏览器签名拦截，`BrowserUA` 是必需项
3. **wham/usage 响应坑**：`credits` 是对象（`{has_credits, balance}`）非数字；窗口标签按 `limit_window_seconds` 动态生成（plus 当前 primary=604800 秒即"周"）
4. **QA 闭环**：代码在 WSL 侧（UNC 供 csc 读取），运行/验收在 Windows 桌面（用户协作看浮窗截图）；改 UI 后让用户截图确认，切勿臆断视觉效果
5. DeepSeekTimeline 的 `Main.cs`（`~/DeepSeekTimeline`）是版式/交互的做法参照，不是本仓库文件
