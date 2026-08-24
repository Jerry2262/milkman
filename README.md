# Milkman 🥛

> 榨取/剥削你的 coding plan 额度 —— ChatGPT Plus / OpenCode Go 用量浮窗

单文件 WinForms 常驻浮窗，不用打开网页即可查看订阅用量。

## 参考项目

- [DeepSeekTimeline](https://github.com/Jerry2262/DeepSeekTimeline) — 版式/交互模板：无边框自绘、顶部吸附 + 缓出动画、双击 Pin、浅色配色均出自它
- [openusage (macOS)](https://github.com/robinebers/openusage) — 数据实现参考：Codex OAuth 凭据刷新、OpenCode Go 官方端点 `zen/go/v1/usage` 的取数与错误分类逻辑

## 功能

- **ChatGPT Plus**：周限额用量条 + 重置倒计时（含 5h/周等多窗口自适应）
- **OpenCode Go**：5h / 周 / 月 三窗口独立用量条 + 各自百分比/重置时间
- **账户级服务端数据**：OpenCode Go 用量来自官方账户计数，与其他设备/工具消费一致
- 屏幕顶部吸附 + 缓出动画 + 双击 Pin（钉住）——同 DeepSeekTimeline 交互
- 开机自启（`config.json` 可关）；窗口位置/吸附状态持久化
- 防封号：获取失败即停自动刷新，界面提示"无法获取信息 · 单击重试"，点击恢复

## 数据源（均为 HTTP，无本地数据库依赖）

| 订阅 | 端点 | 凭据 |
|---|---|---|
| ChatGPT / Codex | `chatgpt.com/backend-api/wham/usage`（非官方） | `%USERPROFILE%\.codex\auth.json` 的 OAuth token，401 自动刷新 |
| OpenCode Go | `opencode.ai/zen/go/v1/usage`（官方） | `auth.json` 中 `opencode-go.key`（自动探测 Win / WSL 路径，可配置覆盖） |

## 编译与运行

Windows 自带 `csc.exe`，无需安装任何 SDK：

```
build.bat          # 编译到 %LOCALAPPDATA%\Milkman\Milkman.exe
```

或手动编译（源码在 WSL 侧时）：

```bash
/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe /nologo /target:winexe /platform:anycpu \
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll \
  /out:"C:\Users\jerry\AppData\Local\Milkman\Milkman.exe" '\\wsl.localhost\Ubuntu-24.04\home\jerry\milkman\Main.cs'
```

## 配置

`%LOCALAPPDATA%\Milkman\config.json`：

```json
{ "X": 2224, "Y": -2160, "Docked": true, "AutoStart": true, "RefreshSeconds": 60, "OpenCodeAuthPath": "" }
```

- `AutoStart`：开机自启（默认开）
- `RefreshSeconds`：刷新间隔（默认 60s）
- `OpenCodeAuthPath`：opencode auth.json 路径（留空自动探测）

## 免责声明

`wham/usage` 为非官方端点，可能随 OpenAI 调整；本项目仅供个人额度查询使用，请遵守各平台服务条款。
