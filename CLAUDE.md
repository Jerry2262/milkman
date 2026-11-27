# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目概览

Milkman — Windows 专用的 WinForms 浮窗，显示 ChatGPT Plus（Plus/Pro 订阅额度）、OpenCode Go（5h/周/月三窗口额度）与 Command Code（GOAT 等套餐的 5h/周滚动窗口 + 月度 credits）的用量。目标是保持个人工具的零依赖和低维护成本：编译产物是单个 EXE，不增加跨平台层、项目文件或第三方包；源码按职责分文件组织。

数据源均为**官方/半官方 HTTP 端点**，无本地数据库依赖：
- ChatGPT: `chatgpt.com/backend-api/wham/usage`（Bearer = `auth.json` 的 OAuth token，路径取 `Config.ChatGptAuthPath`，留空用 `%USERPROFILE%\.codex\auth.json`，可指向 WSL 里的文件；401 时经 `auth.openai.com/oauth/token` + client_id `app_EMoamEEZ73f0CkXaXp7hrann` 刷新，把响应里轮换出的 `access_token` / `refresh_token` / `id_token` 一并写回，`last_refresh` 保持 RFC3339 字符串）
- OpenCode Go: `opencode.ai/zen/go/v1/usage`（Bearer = 账户的 `Key`（Console API key，OpenCode 2 用）；为空时回退到 OpenCode 1 `auth.json` 的 `opencode-go.key`；配置模型为 `OpenCodeAccounts` 列表，每项含 Name/Key/AuthPath）
- Command Code: `api.commandcode.ai/alpha/billing/credits`（Bearer = `CommandCode.Key`，Studio → API keys 创建的 key，`user_` 开头；5h/周 = `windowLimits.fiveHour|weekly` 的 `used/cap`，`resetAt` 是 epoch 毫秒且 0 = 窗口尚未开始；月度 = `credits.monthlyCredits` 对比 `monthlyCreditsGranted`，该字段缺失时按 `/alpha/billing/subscriptions` 的 planId 查内置套餐表（GOAT=70），套餐信息取不到则月度条显示 `—`；月度重置时间取 subscriptions 的 `currentPeriodEnd`）

## 常用命令

```bash
# 编译（Windows cmd 中运行 build.bat，双击亦可；源码路径取自脚本所在目录）
build.bat

# 回归检查（Windows cmd 中运行；仅合成数据，不读真实凭据、不请求 API；改任意源文件后必跑）
tests\run.bat

# 场景验收（真实启动浮窗并按常见用法驱动，逐个截图供人工/视觉检查；
# 数据目录靠 MILKMAN_DATA_DIR 隔离，不碰真实配置；会短暂改写自启项，结束时还原）
powershell -NoProfile -ExecutionPolicy Bypass -File tests\scenarios.ps1 -OutDir %TEMP%\milkman-scn

# 从 WSL 手动编译（/out 的目录须已存在；源码路径用 wslpath 转换）
/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe /nologo /target:winexe /platform:anycpu \
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll \
  /codepage:65001 /win32icon:"$(wslpath -w app.ico)" /out:"C:\Users\<user>\AppData\Local\Milkman\Milkman.exe" \
  "$(wslpath -w Layout.cs)" "$(wslpath -w Core.cs)" "$(wslpath -w Ui.cs)" "$(wslpath -w Form.cs)"

# 重启浮窗（编译后验证）
powershell.exe -NoProfile -Command "Stop-Process -Name Milkman -Force -ErrorAction SilentlyContinue"
powershell.exe -NoProfile -Command 'Start-Process "$env:LOCALAPPDATA\Milkman\Milkman.exe"'
```

## 架构（四个源文件，编译成一个 EXE）

- `Layout.cs` — 布局几何的纯函数：显示器识别 `FindScreen`、DPI 换算 `Scaled/ToDesign`、吸附资格 `CanDock`、锚点定位 `AnchorTo`。不持有窗口状态，可独立验证。
- `Core.cs` — 入口 `Program`、`Config`、数据模型、`DataFetcher`、`Json`、`AutoStart`。
- `Ui.cs` — 版式常量、内容文案、排版与自绘（含段宽 `Segment/SegmentIn`、注脚/标题的宽度取舍）。配色与字体主体在 `Form.cs`，窗口与控件的坐标也在 `Form.cs`。
- `Form.cs` — 窗口行为、`WndProc`、拖拽吸附、刷新调度、位置持久化。绘制部分由 `Ui.cs` 以 partial 类补全。

- `Config`（在 `Core.cs`）— 显式字段映射读写 `config.json`（`Monitor/OffsetX/OffsetY` 位置锚点、Docked、RefreshSeconds、ChatGptAuthPath、OpenCodeAccounts、CommandCode）；程序固定开机自启，不设配置开关；解析失败回落默认并备份 `config.json.bad`；新增字段需同步 `Parse()` / `ToDictionary()`，否则会静默读不到/存不住；单个字段类型写错只回落该字段默认值，不让整份配置作废；写盘走临时文件 + `File.Replace` 的原子替换，固定无 BOM 的 UTF-8（`SaveTo`）；读取时 .NET 会剥离 BOM，所以带 BOM 的文件仍能读对，但外部工具（尤其 PowerShell 5 的 `Set-Content -Encoding UTF8`）改写时容易把中文写坏
- `DataFetcher`（在 `Core.cs`）— 无状态网络层；`FetchGpt()`、每个 `FetchGo(account)` 与 `FetchCommandCode(cfg)` 可并行调用；JSON 字段统一安全读取，轻量 JSON 值树逐调用独立解析，不依赖 ASP.NET 或共享序列化器锁；错误消息截断 80 字。HTTP 连接与读写各 15 秒超时（`ReadWriteTimeout` 也要设，默认 5 分钟且每次读重置，滴字节会把该行 Busy 永久卡住），响应体按 2MB 上限边读边截断
- `MilkmanForm` — 自绘 UI + 行为（`Form.cs` 与 `Ui.cs` 两个 partial）：
  - 版式是「一个 block = 一个订阅实例」，每个 block 三行：行1 图标+名称+状态+动作按钮，行2 用量条，行3 注脚（百分比 + 重置时间）。**三行左端统一对齐到 `OutlineLeft`（即圆点左边缘）**，右端收到 `RightEdge`；段数 1/2/3 时末段都必须正好落在 `RightEdge`，由 `SegmentIn` 保证并被 `CheckLayout` 守着。高度 = `BlockTop0 + block数 × BlockH(76)`。`OpenCodeAccounts` 留空、`CommandCode` 为 null 都表示未订阅、对应区块隐藏；ChatGPT 按实际窗口数分 1–3 段。Brush/Pen 与每行的 `GraphicsPath[][]` 缓存复用，低频动态文本直接测量，禁止缓存不断变化的倒计时字符串。
  - 注脚两段文案不写「重置」前缀：每段可用宽度约 103px（96 DPI，`(500−20−2×8)/3` 设计单位），加前缀会让最宽的一组溢出重叠。改为左「5h 14%」右「1h33m」。
  - 注脚左侧是「标签 + 百分比」，右侧是重置倒计时。标签可能远长于「5h」（ChatGPT 的 `code_review_rate_limit` 生成「审查」窗口），所以左侧要按「段宽 − 右段宽 − 间隙」截断；**百分比优先保住**，标签连一个字都放不下时只显示百分比。
  - 第一行状态只表达「正在做什么」（获取中/刷新中）与「何时更新过」（更新于 HH:mm:ss）。**从没成功过的失败**由第三行整行显示原因，第一行只留标题和「重试」；**有旧数据但这次刷新失败**时第三行仍在显示旧数据，错误原因没有落点，第一行改显示「获取失败」，否则用户只看到「重试」不知道发生了什么。成功与获取中都没有按钮，状态右端可以用到按钮右端（`BtnRight`），最宽的「Command Code · A」+「更新于 HH:mm:ss」实测 400.6 设计单位，可用 433，放得下；有按钮时状态为空，不与之争宽。
  - 吸附：`WM_NCHITTEST`→`HTCAPTION` 系统级拖动；`WM_ENTERSIZEMOVE/EXITSIZEMOVE` 判定出入吸附（Top≤monTop+8 吸附，>monTop+30 脱离）；静止时 `DockTick` 每 100ms 采样，滑动用 30ms 缓出（dy/3，`Peek=5`）；双击 Pin 后立即展开并停止 DockTimer，取消 Pin 时恢复
  - 独立刷新：每个 `SourceRow` 维护自己的 busy/auto/error/last-success，共用 `_refreshTimer`、后台派发、绘制和点击命中逻辑。每次启动等待一次「获取用量」；点击后为 HTTPS 启用 TLS 1.2，并发加载所有已配置来源，成功的来源进入定时刷新；失败只暂停自身并保留旧数据，单击该行「重试」恢复。
  - token 刷新并发：一次性 `refresh_token` 被消耗后若发现磁盘被别的进程改过，**不覆盖**对方的写入（可能冲掉它刚轮换出的有效 RT），但用本次拿到的 `access_token` 把请求做完，避免把一次可用的会话变成报错。
  - 收起期间不轮询：`SyncRefreshTimer` 用 `VisibleToUser()`（未吸附 / 已展开 / 已钉住）决定 `_refreshTimer` 是否工作，5px 提示条状态下不联网。展开时 `RefreshWhenOpened` 补一次取数：还没点过「获取用量」就等价于点一次（加载全部），已点过则只补缺失、出错或超过 `max(2×RefreshSeconds, 120s)` 未更新的来源。这样放很久再打开不会是空白，也不用在没人看的时候持续请求。
  - 置顶：`TopMost = true` 只让 WinForms 在**创建之后**用 `SetWindowPos` 补设一层，而后台启动（开机自启、被别的进程拉起）时这个调用会被系统的 z-order 前台锁静默忽略——窗口就永远不置顶，表现为"有时置顶有时不置顶"。所以 `CreateParams` 里直接带 `WS_EX_TOPMOST`，窗口创建即置顶，与是否前台无关；`EnsureTopMost()` 再在 `WM_WINDOWPOSCHANGED` / `WM_STYLECHANGED(GWL_EXSTYLE)` 与 `OnShown` 后补一次丢失的标志（不轮询、不抢焦点）
  - 多屏：位置状态是「显示器设备名 + 相对该屏的偏移」（`Config.Monitor/OffsetX/OffsetY`），不是绝对像素。换屏、改分辨率走 `OnDisplayChanged → ApplyLayout`，改缩放走 `WM_DPICHANGED` 的建议矩形，不做逐屏特判；`monRect` 可能为负坐标屏。吸附资格由 `CanDock` 判定：顶边外侧还有屏幕覆盖时不吸附，否则收起的浮窗会压到相邻屏。
  - DPI：几何常量与字号共用 144 基准，`_scale = dpi / 144`。**两者必须同乘同除**——只改字号或只改几何会让文字与版面比例失衡。文字按物理像素绘制并绕开画布缩放（`DrawText`），否则字形微调被压缩变换破坏。
  - 跨屏重排：`WM_DPICHANGED` 必须按 `lParam` 给出的建议矩形，用**一次** `SetWindowPos` 完成，不要在消息里再自行推导锚点；否则尺寸变化与位置推导互相反馈，拖动跨屏会来回闪动。
- `Json` / `AutoStart`（在 `Core.cs`）— 标准 JSON 读写（保留未知授权字段的数字词法，避免装载 ASP.NET）、自启注册表维护。改动 `Json` / `Config` / 响应解析后必须跑 `tests\run.bat`（仅合成数据）。

## 已知坑（改动前必读）

1. **编译**：`/out` 的目录必须已存在，否则 CS1567；指向 UNC 路径可以正常产出 EXE；源码是无 BOM 的 UTF-8，编译需 `/codepage:65001`；只支持 C#5 语法（无 `?.`、字符串插值等）和 .NET4.x API（无 `ToUnixTimeSeconds`）
2. **Cloudflare 1010**：`opencode.ai` 按浏览器签名拦截，`BrowserUA` 是必需项
3. **wham/usage 响应坑**：`credits` 是对象（`{has_credits, balance}`）非数字；窗口标签按 `limit_window_seconds` 动态生成；成功响应已有其他可用额度窗口但缺少 5h 时插入 `Unlimited` 占位并显示 `5h ∞`，真实 5h 恢复后自动替换
4. **QA 闭环**：代码在 WSL 侧（UNC 供 csc 读取），运行/验收在 Windows 桌面（用户协作看浮窗截图）；改 UI 后让用户截图确认，切勿臆断视觉效果
5. DeepSeekTimeline（`~/stable/DeepSeekTimeline`）曾作为版式参照，该参照已弃用；当前版式以 `Ui.cs` 的 block 模型为准，不必再对齐它的实现
6. **config.json 边界**：解析器只接受标准 JSON（单引号、无引号键会被拒绝）；`null` 数值字段回落默认值，`RefreshSeconds` ≤0 或换算为毫秒会溢出时回落 60；解析失败会把原文件备份为 `config.json.bad` 并回落默认配置；`OpenCodeAccounts` 里的 null 元素会被忽略
7. **Command Code 响应坑**：`windowLimits.*.resetAt` 是 epoch 毫秒、0 表示滚动窗口尚未开始（显示“未开始”）；`monthlyCredits` 是**剩余**不是已用；`monthlyCreditsGranted` 可能不返回，缺它时月度分母靠套餐表兜底（`individual-goat`=70），套餐也取不到就只在月度版位显示 `—`；`/alpha/*` 是 CLI 内部接口，字段随时可能变
8. **refresh_token 一次性**：`oauth/token` 每刷新一次就换发新的 `refresh_token` 并作废旧的，响应里的 `refresh_token` / `id_token` 必须和 `access_token` 一起写回。只写 `access_token` 会让文件里的 RT 变成废的，且症状要等 access_token 到期（约 10 天）才出现——表现为 `刷新失败: refresh_token_reused`，只能重新登录。多台机器也别共用同一份 `auth.json`
9. **场景验收脚本的坑**：`tests/scenarios.ps1` 必须存成**带 BOM** 的 UTF-8（PowerShell 5 会把无 BOM 的脚本按 GBK 解析，中文字符串直接语法错误）；脚本自身要先 `SetThreadDpiAwarenessContext(-4)`，否则 `GetWindowRect` 拿到的是虚拟化坐标，在 150% 屏上量出的尺寸只有实际的 2/3，点击也会点偏；场景验收要直接启动已部署的 `Milkman.exe`，不要在临时目录重新编译主程序再启动（Defender 会拦截临时目录里新出现的未签名可执行文件；`StateShots` 这类测试程序不受影响）。
10. **首次运行的落位**：`RestoreLocation` 在配置没有有效偏移（`OffsetX/OffsetY` 为 -1，首次运行或配置解析失败回退默认值）时会把窗口摆到主屏右下角；`Load` 里再调 `ApplyLayout` 时必须用「窗口当前位置相对目标屏的偏移」当锚点，不能回头用 `_cfg.OffsetX`——`Math.Max(0, -1)` 会把窗口拽到屏幕左上角 (0,0)。
11. **改配置文件只用程序自己的写入路径**：手工改写 `config.json` 时用无 BOM 的 UTF-8。真正的坑是编码不是 BOM——PowerShell 5 读写中文时容易按 ANSI 处理（`Set-Content -Encoding UTF8` 曾把中文写成乱码，实际发生过）。`tests/JsonRegression.cs` 的 `CheckConfigEncoding` 守着这条
