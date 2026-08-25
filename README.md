# Milkman 🥛

> 榨取/剥削你的 coding plan 额度 —— ChatGPT Plus / OpenCode Go 用量浮窗

Windows 专用的单文件 WinForms 常驻浮窗，不用打开网页即可查看订阅用量。

## 参考项目

- [DeepSeekTimeline](https://github.com/Jerry2262/DeepSeekTimeline) — 版式/交互模板：无边框自绘、顶部吸附 + 缓出动画、双击 Pin、浅色配色均出自它
- [openusage (macOS)](https://github.com/robinebers/openusage) — 数据实现参考：Codex OAuth 凭据刷新、OpenCode Go 官方端点 `zen/go/v1/usage` 的取数与错误分类逻辑

## 功能

- **ChatGPT Plus**：按实际返回的 5h / 周 / 代码审查等窗口分段展示
- **OpenCode Go**：支持多个账户同时展示；每个账户均有 5h / 周 / 月三窗口用量条
- **账户级服务端数据**：OpenCode Go 用量来自官方账户计数，与其他设备/工具消费一致
- 屏幕顶部吸附 + 缓出动画 + 双击 Pin（钉住）——同 DeepSeekTimeline 交互
- 固定开机自启；窗口位置/吸附状态持久化
- 独立刷新：ChatGPT 与每个 OpenCode 账户互不等待；失败只暂停自身并保留上次数据，点击对应区块恢复

## 数据源（均为 HTTP，无本地数据库依赖）

| 订阅 | 端点 | 凭据 |
|---|---|---|
| ChatGPT / Codex | `chatgpt.com/backend-api/wham/usage`（非官方） | `%USERPROFILE%\.codex\auth.json` 的 OAuth token，401 自动刷新 |
| OpenCode Go | `opencode.ai/zen/go/v1/usage`（官方） | `OpenCodeAccounts` 中每个 `auth.json` 的 `opencode-go.key` |

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
{
  "X": 2224,
  "Y": -2160,
  "Docked": true,
  "RefreshSeconds": 60,
  "OpenCodeAccounts": [
    {
      "Name": "个人",
      "AuthPath": "\\\\wsl.localhost\\Ubuntu-24.04\\home\\jerry\\.local\\share\\opencode\\auth.json"
    }
  ]
}
```

- `RefreshSeconds`：刷新间隔（默认 60s）
- `OpenCodeAccounts`：账户列表；增加项目后，浮窗会为每个账户增加一行并同时取数
- `Name`：浮窗中的账户名称
- `AuthPath`：该账户的 opencode `auth.json` 路径

## 临时停用

退出程序并取消下次开机启动：

```powershell
Stop-Process -Name Milkman -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name Milkman -ErrorAction SilentlyContinue
```

需要恢复时手动启动一次，程序会重新登记开机自启：

```powershell
Start-Process "$env:LOCALAPPDATA\Milkman\Milkman.exe"
```

## 免责声明

`wham/usage` 为非官方端点，可能随 OpenAI 调整；本项目仅供个人额度查询使用，请遵守各平台服务条款。
