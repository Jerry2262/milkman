# Milkman 🥛

> ChatGPT Plus / OpenCode Go / Command Code 用量浮窗 —— 不打开网页也能看到还剩多少额度

![界面预览](docs/screenshot.png)

Windows 专用的单文件 WinForms 常驻浮窗：无边框、半透明、屏幕顶部吸附，鼠标靠近展开，双击钉住。整个程序只有一个 [Main.cs](Main.cs)，用系统自带的 `csc.exe` 编译，不依赖 SDK 或第三方包。

## 功能

- **ChatGPT Plus**：按服务端返回的窗口（5h / 周 / 代码审查）分段展示；服务端暂时不返回 5h 时显示 `5h ∞`
- **OpenCode Go**：支持多个账户；每个账户 5h / 周 / 月三条用量，账户级计数与其他设备一致
- **Command Code（GOAT 等套餐）**：5h / 周两个滚动窗口加月度 credits；配置里没有这一段时整块隐藏
- 屏幕顶部吸附 + 缓出动画 + 双击 Pin
- 固定开机自启；窗口位置与吸附状态持久化
- 独立刷新：ChatGPT、每个 Go 账户与 Command Code 互不等待；失败只暂停自身并保留旧数据，点对应“重试”恢复
- 原生置顶丢失后自动恢复，不抢前台焦点

## 数据源

| 订阅 | 端点 | 凭据 |
|---|---|---|
| ChatGPT / Codex | `chatgpt.com/backend-api/wham/usage`（非官方） | `%USERPROFILE%\.codex\auth.json` 的 OAuth token，401 时自动刷新并写回 |
| OpenCode Go | `opencode.ai/zen/go/v1/usage` | `OpenCodeAccounts[].Key`（Console API key）；回退 OpenCode 1 `auth.json` 里的 `opencode-go.key` |
| Command Code（GOAT 等套餐） | `api.commandcode.ai/alpha/billing/credits`（CLI `/usage` 用的内部接口） | `CommandCode.Key`（Studio → API keys，`user_` 开头）；套餐与月度周期另取 `/alpha/billing/subscriptions` |

## 系统要求

- Windows 10 / 11
- .NET Framework 4.x（系统自带，无需安装 SDK）

## 编译与运行

```bat
build.bat
```

产物为 `%LOCALAPPDATA%\Milkman\Milkman.exe`，双击即可运行。程序每次启动都会登记开机自启。

程序与窗口使用内嵌的多尺寸图标，无需额外携带图标文件。图标源文件为 [app.svg](<app.svg>)，编译使用 [app.ico](<app.ico>)。

在 WSL 中手动编译（`/out` 必须是 Windows 本地路径，不能是 `\\wsl.localhost\...`）：

```bash
/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe /nologo /target:winexe /platform:anycpu \
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll \
  /codepage:65001 /win32icon:"$(wslpath -w app.ico)" /out:"C:\path\to\Milkman.exe" "$(wslpath -w Main.cs)"
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
    { "Name": "个人", "Key": "oc_sk_...", "AuthPath": "" }
  ],
  "CommandCode": { "Name": "主账号", "Key": "user_..." }
}
```

- `RefreshSeconds`：刷新间隔，默认 60 秒
- `OpenCodeAccounts`：账户列表，每项在浮窗中占一行；留空数组 `[]` 会隐藏 OpenCode Go 区块
- `Name`：显示名称
- `Key`：OpenCode Go 的 API key（Console → Account › Personal API keys，`oc_sk_` 开头）。OpenCode 2 把凭据存进本地数据库、不再更新 `auth.json`，所以推荐直接填这里
- `AuthPath`：OpenCode 1 的 `auth.json` 路径；`Key` 为空时从中读取 `opencode-go.key`
- `CommandCode`：Command Code 订阅（GOAT / Pro 等）；整段删掉或写成 `null` 会隐藏该区块
- `CommandCode.Key`：Studio → API keys 创建的 key（`user_` 开头），CLI 与 Provider API 同一把；`Name` 只是显示名
- `X` / `Y` / `Docked`：窗口位置与吸附状态，由程序自动维护

`config.json` 必须是标准 JSON：程序只接受标准语法，单引号、无引号键之类的宽松写法会被拒绝。解析失败时程序回落到默认配置，并把原文件备份为 `config.json.bad`，避免原配置被静默覆盖。

首次运行且没有配置账户时，程序会探测本机 OpenCode 1 的 `%LOCALAPPDATA%\opencode\auth.json` 和 `~/.local/share/opencode/auth.json`；找不到就隐藏 Go 区块，填好配置后自动显示。

## 使用

- 鼠标移到屏幕顶部提示条上展开，移开自动收起
- 拖到屏幕顶吸附；往下拖离取消吸附
- 吸附状态下双击切换「钉住」，钉住后不自动收起
- 右上角 `✕` 退出程序
- 前台是全屏视频/游戏时不自动弹出

## 维护

- **换 Go key**：在 Console 重新创建后更新 `config.json` 里的 `Key`，下一轮刷新生效
- **换 Command Code key**：Studio 重新生成后更新 `CommandCode.Key`，重启浮窗生效
- **OpenCode 1 用户**：把 `AuthPath` 指向 `auth.json` 即可，不需要 `Key`
- **ChatGPT token**：过期后自动用 `refresh_token` 刷新并写回 `auth.json`

## 启动开销与回归检查

JSON 读写使用 [Main.cs](<Main.cs>) 中的轻量值树实现，不加载 ASP.NET 序列化组件。配置已明确账户列表时不再探测默认认证路径；自启注册表值未变化时不重复写入。首次额度刷新仍在窗口显示时立即执行，刷新间隔不变。

在 Windows 中运行：

```bat
tests\run.bat
```

[JsonRegression.cs](<tests/JsonRegression.cs>) 使用合成数据检查配置兼容性（含空值回落、账户 null 过滤）、授权 JSON 未知字段与数字精度、非法输入、并发读写、`last_refresh` 的 RFC3339 时间戳格式，以及与原序列化器的差分结果；不读取真实认证文件、不请求 API。测试程序才会引用旧序列化组件作为对照，正式程序不依赖它。

## 已知限制

- `wham/usage` 是非官方端点，可能随 OpenAI 调整
- OpenCode Go 用量接口没有公开文档，字段变化时可能需要适配
- Command Code 的 `/alpha/*` 是 CLI 内部接口，同样没有公开文档；月度总量在服务端没返回 `monthlyCreditsGranted` 时按套餐表估算（GOAT = 70 credits），未知套餐的月度条显示 `—`
- `config.json` 中的 key 为明文（与 OpenCode 1 的 `auth.json` 相同），不要提交到仓库或分享
- 仅支持 Windows

## 参考项目

- [DeepSeekTimeline](https://github.com/Jerry2262/DeepSeekTimeline) — 版式与交互模板
- [openusage](https://github.com/robinebers/openusage) — Codex 凭据刷新与 Go 用量取数参考

## License

[MIT](LICENSE)
