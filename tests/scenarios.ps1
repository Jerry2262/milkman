# Milkman 场景验收：在隔离环境里真实启动浮窗，按常见使用方式驱动，逐场景截图。
# 不碰用户的真实配置与自启项：LOCALAPPDATA 指向临时目录，自启键先备份后还原。
# 用法（Windows PowerShell）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\scenarios.ps1 -OutDir <目录>
param(
  [string]$OutDir = "$env:TEMP\milkman-scenarios",
  [string]$Repo = ""
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win {
  [DllImport("user32.dll")] public static extern bool SetThreadDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  public struct RECT { public int L, T, R, B; }
  public struct POINT { public int X, Y; }
}
"@

# 脚本自身必须是每屏 DPI 感知的：否则 GetWindowRect 返回的是被系统虚拟化过的坐标，
# 量出来的窗口尺寸和点击位置都会错（在 150% 屏上会缩成 2/3）。
[void][Win]::SetThreadDpiAwarenessContext([IntPtr](-4))

$repo = if ($Repo) { $Repo } else { Split-Path -Parent $PSScriptRoot }

# 备份自启项，脚本结束时还原
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$savedRun = (Get-ItemProperty $runKey -Name Milkman -ErrorAction SilentlyContinue).Milkman

$savedLocal = $env:LOCALAPPDATA
$work = Join-Path $env:TEMP ("milkman-scn-" + [guid]::NewGuid().ToString("N"))
$dataDir = Join-Path $work "data"
$exeCopy = Join-Path $env:LOCALAPPDATA "Milkman\Milkman.exe"
New-Item -ItemType Directory -Force -Path $OutDir, $dataDir | Out-Null
$cfgPath = Join-Path $dataDir "config.json"

# 直接启动已部署的正式 exe：临时目录里新编译的未签名 exe 会被 Defender 拦截。
# 数据目录靠 MILKMAN_DATA_DIR 隔离，因此多个实例互不干扰。
if (-not (Test-Path $exeCopy)) { throw "找不到已部署的 Milkman.exe，请先运行 build.bat" }

function Get-AppWindow([int]$procId) {
  $script:found = [IntPtr]::Zero
  [Win]::EnumWindows({
    param($h, $l)
    $p = 0
    [void][Win]::GetWindowThreadProcessId($h, [ref]$p)
    if ($p -eq $procId -and [Win]::IsWindowVisible($h)) {
      $r = New-Object Win+RECT
      [void][Win]::GetWindowRect($h, [ref]$r)
      if (($r.R - $r.L) -gt 120) { $script:found = $h; return $false }
    }
    return $true
  }, [IntPtr]::Zero) | Out-Null
  return $script:found
}

function Save-Shot([IntPtr]$hwnd, [string]$name) {
  $r = New-Object Win+RECT
  [void][Win]::GetWindowRect($hwnd, [ref]$r)
  $w = $r.R - $r.L; $h = $r.B - $r.T
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $hdc = $g.GetHdc()
  [void][Win]::PrintWindow($hwnd, $hdc, 2)
  $g.ReleaseHdc($hdc)
  $path = Join-Path $OutDir ($name + ".png")
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  $dpi = [Win]::GetDpiForWindow($hwnd)
  Write-Output ("  {0,-22} {1}x{2} dpi={3} pos=({4},{5}) -> {6}" -f $name, $w, $h, $dpi, $r.L, $r.T, (Split-Path $path -Leaf))
}

# 点第一行右端的动作按钮：位置按窗口实际像素换算，跨 DPI 都适用
function Click-Action([IntPtr]$hwnd) {
  $r = New-Object Win+RECT
  [void][Win]::GetWindowRect($hwnd, [ref]$r)
  $w = $r.R - $r.L; $h = $r.B - $r.T
  Click-At $hwnd ([int]($w * 0.83)) ([int]($h * 0.095))
}

function Click-At([IntPtr]$hwnd, [int]$x, [int]$y) {
  $lp = [IntPtr](($y -shl 16) -bor $x)
  [void][Win]::PostMessage($hwnd, 0x0201, [IntPtr]1, $lp)
  Start-Sleep -Milliseconds 60
  [void][Win]::PostMessage($hwnd, 0x0202, [IntPtr]0, $lp)
}

function Start-App {
  # 让程序把数据目录指向临时目录，真实配置不受影响
  $env:MILKMAN_DATA_DIR = $dataDir
  $p = Start-Process -FilePath $exeCopy -PassThru
  Remove-Item Env:\MILKMAN_DATA_DIR -ErrorAction SilentlyContinue
  if (-not $p.WaitForInputIdle(10000)) { throw "窗口未就绪" }
  Start-Sleep -Milliseconds 700
  $hwnd = Get-AppWindow $p.Id
  if ($hwnd -eq [IntPtr]::Zero) { throw "找不到窗口" }
  return @{ Proc = $p; Hwnd = $hwnd }
}

function Stop-App($app) {
  if ($app -and -not $app.Proc.HasExited) {
    $app.Proc.Kill(); $app.Proc.WaitForExit()
  }
  Start-Sleep -Milliseconds 300
}

# 当前真实配置，作为「用户已配置好」的场景输入
$realConfig = Join-Path $savedLocal "Milkman\config.json"
$cfgConfigured = if (Test-Path $realConfig) { Get-Content -Raw $realConfig } else { "{}" }

# 失败态：把密钥换成无效值，让服务端真的拒绝，走真实错误路径。
# 配置可能是紧凑格式也可能是缩进格式，冒号两侧允许空白，否则替换会静默失效。
$cfgBroken = $cfgConfigured -replace '"Key"\s*:\s*"[^"]*"', '"Key": "oc_sk_invalid_probe"'
$cfgBroken = $cfgBroken -replace '"user_[^"]*"', '"user_invalid_probe"'
if ($cfgBroken -eq $cfgConfigured) { throw "无效密钥构造失败：配置里没有可替换的 Key 字段" }

try {
  # ---------- 场景 1：首次启动，无任何订阅配置 ----------
  Write-Output "[1/6] 首次启动（无订阅）"
  Set-Content -LiteralPath $cfgPath -Value '{"OpenCodeAccounts":[],"CommandCode":null}' -Encoding UTF8 -NoNewline
  $app = Start-App
  Save-Shot $app.Hwnd "01-first-launch"
  Stop-App $app

  # ---------- 场景 2：已配置，启动后等待点击 ----------
  Write-Output "[2/6] 已配置启动（等待点击获取用量）"
  [System.IO.File]::WriteAllText($cfgPath, $cfgConfigured, (New-Object System.Text.UTF8Encoding($false)))
  $app = Start-App
  Save-Shot $app.Hwnd "02-configured-idle"

  # ---------- 场景 3：点击后立刻抓取「获取中」 ----------
  Write-Output "[3/6] 点击获取用量（获取中）"
  # 按钮在第一行右侧：client 坐标约 (300,15)，随 DPI 缩放
  Click-Action $app.Hwnd
  Start-Sleep -Milliseconds 250
  Save-Shot $app.Hwnd "03-loading"

  # ---------- 场景 4：等真实取数完成 ----------
  Write-Output "[4/6] 取数完成（成功态）"
  Start-Sleep -Seconds 4
  Save-Shot $app.Hwnd "04-loaded"
  Stop-App $app

  # ---------- 场景 5：错误密钥，走真实 401 失败路径 ----------
  Write-Output "[5/6] 密钥无效（失败态）"
  [System.IO.File]::WriteAllText($cfgPath, $cfgBroken, (New-Object System.Text.UTF8Encoding($false)))
  $app = Start-App
  Click-Action $app.Hwnd
  Start-Sleep -Seconds 4
  Save-Shot $app.Hwnd "05-failure"
  Stop-App $app

  # ---------- 场景 6：吸附收起态 ----------
  Write-Output "[6/6] 吸附收起（顶部只留提示条）"
  $dockedCfg = $cfgConfigured -replace '"Docked"\s*:\s*false', '"Docked": true'
  if ($dockedCfg -eq $cfgConfigured) { throw "吸附场景构造失败：配置里没有 Docked:false 可替换" }
  [System.IO.File]::WriteAllText($cfgPath, $dockedCfg, (New-Object System.Text.UTF8Encoding($false)))
  $app = Start-App
  # 把鼠标移开浮窗，让它自然收起
  [void][Win]::SetCursorPos(200, 400)
  Start-Sleep -Seconds 2
  $r = New-Object Win+RECT; [void][Win]::GetWindowRect($app.Hwnd, [ref]$r)
  $mon = [System.Windows.Forms.Screen]::FromHandle($app.Hwnd).Bounds
  Write-Output ("  收起检查: 窗口顶={0} 屏幕顶={1} 露出高度={2}" -f $r.T, $mon.Top, ($r.B - $mon.Top))
  if ($r.T -ge $mon.Top) { throw "吸附场景失败：窗口没有收起到屏幕外" }
  Save-Shot $app.Hwnd "06-docked-collapsed"

  # 鼠标移到提示条上展开
  [void][Win]::SetCursorPos([int](($r.L + $r.R) / 2), ($mon.Top + 2))
  Start-Sleep -Seconds 2
  $r2 = New-Object Win+RECT; [void][Win]::GetWindowRect($app.Hwnd, [ref]$r2)
  Write-Output ("  展开检查: 窗口顶={0} 屏幕顶={1}" -f $r2.T, $mon.Top)
  if ($r2.T -ne $mon.Top) { throw "吸附场景失败：鼠标移上去窗口没有展开" }
  Save-Shot $app.Hwnd "07-docked-expanded"
  Stop-App $app
}
finally {
  # 还原自启项与真实配置不受影响
  if ($savedRun) {
    Set-ItemProperty -Path $runKey -Name Milkman -Value $savedRun
  } else {
    Remove-ItemProperty -Path $runKey -Name Milkman -ErrorAction SilentlyContinue
  }
  if (Test-Path $work) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
}

Write-Output ""
Write-Output "截图目录: $OutDir"
Write-Output "自启项已还原: $((Get-ItemProperty $runKey -Name Milkman -ErrorAction SilentlyContinue).Milkman)"
