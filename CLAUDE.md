# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目概览

Milkman — 单文件 WinForms 浮窗，显示 ChatGPT Plus（Plus/Pro 订阅额度）与 OpenCode Go（5h/周/月三窗口额度）的用量，目标"榨取 coding plan 额度"（名字来自目录名 milkman）。UI 以 DeepSeekTimeline（`~/DeepSeekTimeline`，可随时参考其 Main.cs）为唯一视觉模板。

数据源均为**官方/半官方 HTTP 端点**，无本地数据库依赖：
- ChatGPT: `chatgpt.com/backend-api/wham/usage`（Bearer = `%USERPROFILE%\.codex\auth.json` 的 OAuth token；401 时经 `auth.openai.com/oauth/token` + client_id `app_EMoamEEZ73f0CkXaXp7hrann` 刷新并写回）
- OpenCode Go: `opencode.ai/zen/go/v1/usage`（Bearer = `opencode-go.key`；auth.json 路径探测：Windows 的 `~/.local/share/opencode/auth.json` → WSL 的 `\\wsl.localhost\Ubuntu-24.04\home\jerry\.local\share\opencode\auth.json`）

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
- `Config` — `config.json`（窗口 X/Y、Docked、AutoStart、RefreshSeconds、OpenCodeAuthPath）
- `DataFetcher` — 唯一的网络层；`Refresh()` 双源各自 try/catch（失败互不影响）；错误消息截断 80 字；`HttpGetWithStatus` 保留状态码+body 供错误分类（401=key 拒绝 / 403=无订阅）
- `MilkmanForm` — 自绘 UI + 行为：
  - 版式：520×160，浅色系（Bg #F7F7F8 等），静态缓存 Brush/Pen/BandPath/文本测量 `Wd()`；GDI 对象零帧间创建
  - 吸附：`WM_NCHITTEST`→`HTCAPTION` 系统级拖动（无边框窗 MaximizeBox 拦不住双击最大化→拦截 `WM_SYSCOMMAND/SYSCOMMAND` 和 `WM_NCLBUTTONDBLCLK`）；`WM_ENTERSIZEMOVE/EXITSIZEMOVE` 判定出入吸附（Top≤monTop+8 吸附，>monTop+30 脱离）；`DockTick` 30ms 缓出（dy/3 步进，`Peek=4` 收起条）；`pinned` 双击钉住时 DockTick 冻结
  - 防封号：获取失败（GptWindows 空且 GptError 非空）→ `_refreshTimer.Stop()`，单击浮窗重试恢复
  - 多屏：`monRect` 可能为负坐标屏（副屏在顶部），坐标计算要考虑
- `AutoStart` — HKCU Run 的 `Milkman` 键

## 已知坑（改动前必读）

1. **编译**：`/out` 不支持 UNC 路径（必须输出到 Windows 本地）；输出目录需先存在；从 bash 调 csc 时 UNC 源路径必须单引号包裹；只支持 C#5 语法（无 `?.`、字符串插值等）和 .NET4.x API（无 `ToUnixTimeSeconds`）
2. **Cloudflare 1010**：`opencode.ai` 按浏览器签名拦截，`BrowserUA` 是必需项
3. **wham/usage 响应坑**：`credits` 是对象（`{has_credits, balance}`）非数字；窗口标签按 `limit_window_seconds` 动态生成（plus 当前 primary=604800 秒即"周"）
4. **QA 闭环**：代码在 WSL 侧（UNC 供 csc 读取），运行/验收在 Windows 桌面（用户协作看浮窗截图）；改 UI 后让用户截图确认，切勿臆断视觉效果
5. DeepSeekTimeline 的 `Main.cs`（`~/DeepSeekTimeline`）是版式/交互的做法参照，不是本仓库文件
