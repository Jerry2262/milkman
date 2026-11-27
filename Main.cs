// Milkman — 榨取/剥削 ChatGPT Plus·OpenCode Go·Command Code 等 coding plan 额度的用量浮窗
// 参照 DeepSeekTimeline 的单文件 WinForms 模式：csc 编译，无 VS 工程。
// 数据源(均为 HTTP, 无本地依赖):
//   ChatGPT/Codex:  %USERPROFILE%\.codex\auth.json 的 OAuth token -> chatgpt.com/backend-api/wham/usage（非官方）
//   OpenCode Go:    opencode-go.key -> https://opencode.ai/zen/go/v1/usage（官方账户级消费百分比）
//   Command Code:   Studio API key -> https://api.commandcode.ai/alpha/billing/credits（CLI /usage 用的内部接口）
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Milkman
{
  static class Program
  {
    [STAThread]
    static void Main()
    {
      NativeMethods.EnablePerMonitorDpi(); // 每屏 DPI 原生渲染，避免被系统位图拉伸
      Application.EnableVisualStyles();
      Application.SetCompatibleTextRenderingDefault(false);
      AutoStart.Ensure(); // 固定开机自启；每次启动同时刷新注册表中的 exe 路径
      Application.Run(new MilkmanForm());
    }

    public static string DataDir()
    {
      return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Milkman");
    }

    public static long NowUnixMs()
    {
      return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
    }

    // Codex auth.json 的 last_refresh 是 RFC3339 字符串（例: 2026-10-29T03:42:15.411646300Z）
    public static string NowRfc3339()
    {
      return DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "00Z";
    }

  }

  static class NativeMethods
  {
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(POINT pt, int flags);

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    // PER_MONITOR_AWARE_V2 = -4：窗口按所在显示器自己的缩放渲染（Win10 1703+），
    // 系统不再把它当位图缩放，多屏混合 DPI 时既不模糊也不会尺寸错位。
    public static void EnablePerMonitorDpi()
    {
      try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { }
      try { SetProcessDPIAware(); } catch { } // 旧系统退化为系统级感知
    }

    // 目标显示器区域的实际 DPI；取不到按 96(100%) 处理
    public static int DpiOf(Rectangle bounds)
    {
      try
      {
        var pt = new POINT { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + bounds.Height / 2 };
        uint dx, dy;
        if (GetDpiForMonitor(MonitorFromPoint(pt, 2), 0, out dx, out dy) == 0 && dx > 0) return (int)dx;
      }
      catch { }
      return 96;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS m);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct MARGINS { public int L, R, T, B; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int index);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    public static extern bool MoveFileEx(string source, string target, int flags);
  }

  // =======================================================================
  // 配置缓存: %LOCALAPPDATA%\Milkman\config.json
  // =======================================================================
  class OpenCodeAccountConfig
  {
    public string Name = "";
    public string Key = "";      // OpenCode Go API key（OpenCode 2 从 Console 获取，优先使用）
    public string AuthPath = ""; // OpenCode 1 的 auth.json 路径（Key 为空时回退）
  }

  // Command Code（GOAT / Pro 等套餐）：一把 Studio API key，没有订阅就不写这一段
  class CommandCodeConfig
  {
    public string Name = "";
    public string Key = "";      // Studio → API keys 创建的 key（user_ 开头），CLI 与 Provider API 同一把
  }

  // 注意：新增配置字段时必须同时更新 Parse() 与 ToDictionary()，否则该字段会静默读不到/存不住。
  class Config
  {
    // 位置状态以「显示器 + 相对该屏左上角的偏移」保存：显示器分辨率、DPI 或整体布局变化后
    // 这些值依然有效，不需要为单屏/双屏/来回切换分别适配。Monitor 为空表示旧配置或首次运行，
    // 位置重置到主屏。旧版按绝对像素存的 X/Y 已废弃，读到也丢弃。
    public string Monitor = "";
    public int OffsetX = -1, OffsetY = -1;
    public bool Docked = false;
    public int RefreshSeconds = 60;
    // 留空 = %USERPROFILE%\.codex\auth.json；填了只认填的那一份（可以指向 WSL 里的路径）
    public string ChatGptAuthPath = "";
    public List<OpenCodeAccountConfig> OpenCodeAccounts = new List<OpenCodeAccountConfig>();
    public CommandCodeConfig CommandCode; // null = 未订阅, 整块隐藏

    public static Config Load()
    {
      try
      {
        var path = Path.Combine(Program.DataDir(), "config.json");
        if (!File.Exists(path)) return Defaults();
        try { return Parse(File.ReadAllText(path)); }
        catch
        {
          // 解析失败时先保留原文件副本，避免关窗时 SaveState 用默认值静默覆盖
          try { File.Copy(path, path + ".bad", true); } catch { }
          return Defaults();
        }
      }
      catch { }
      return Defaults();
    }

    static Config Defaults()
    {
      return new Config { OpenCodeAccounts = DefaultOpenCodeAccounts() };
    }

    public bool Save()
    {
      return SaveTo(Path.Combine(Program.DataDir(), "config.json"));
    }

    // 写入指定路径，供测试用临时文件验证编码。始终写无 BOM 的 UTF-8：
    // 带 BOM 会让其他工具读到的首字符异常，中文账户名随即损坏。
    internal bool SaveTo(string path)
    {
      try
      {
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, Json.Serialize(ToDictionary()), new UTF8Encoding(false));
        return true;
      }
      catch { return false; }
    }

    public static Config Parse(string json)
    {
      var d = ConfigObject(Json.Parse(json));
      var cfg = new Config();
      object value;
      if (d.TryGetValue("Monitor", out value) && value != null)
        cfg.Monitor = Convert.ToString(value, CultureInfo.InvariantCulture);
      if (d.TryGetValue("OffsetX", out value) && value != null) cfg.OffsetX = Convert.ToInt32(value, CultureInfo.InvariantCulture);
      if (d.TryGetValue("OffsetY", out value) && value != null) cfg.OffsetY = Convert.ToInt32(value, CultureInfo.InvariantCulture);
      if (d.TryGetValue("Docked", out value) && value != null) cfg.Docked = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
      if (d.TryGetValue("RefreshSeconds", out value) && value != null)
      {
        try { cfg.RefreshSeconds = Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch (OverflowException) { } // 保留本字段默认值与其他配置。
      }
      // WinForms Timer 使用 int 毫秒；非正值或换算后溢出的秒数回落默认间隔。
      if (cfg.RefreshSeconds <= 0 || cfg.RefreshSeconds > int.MaxValue / 1000) cfg.RefreshSeconds = 60;
      if (d.TryGetValue("ChatGptAuthPath", out value) && value != null)
        cfg.ChatGptAuthPath = Convert.ToString(value, CultureInfo.InvariantCulture);
      if (d.TryGetValue("OpenCodeAccounts", out value))
      {
        if (value != null)
        {
          var list = value as IList;
          if (list == null) throw new FormatException("accounts must be an array");
          foreach (var item in list)
          {
            if (item == null) continue; // 手写配置里的 null 元素直接忽略，避免后续解引用崩溃
            var entry = ConfigObject(item);
            var account = new OpenCodeAccountConfig();
            if (entry.TryGetValue("Name", out value)) account.Name = value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            if (entry.TryGetValue("Key", out value)) account.Key = value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            if (entry.TryGetValue("AuthPath", out value)) account.AuthPath = value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            cfg.OpenCodeAccounts.Add(account);
          }
        }
      }
      else cfg.OpenCodeAccounts = DefaultOpenCodeAccounts();
      if (d.TryGetValue("CommandCode", out value) && value != null)
      {
        var entry = ConfigObject(value);
        var cc = new CommandCodeConfig();
        if (entry.TryGetValue("Name", out value)) cc.Name = value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        if (entry.TryGetValue("Key", out value)) cc.Key = value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        cfg.CommandCode = cc;
      }
      return cfg;
    }

    static Dictionary<string, object> ConfigObject(object value)
    {
      var source = value as Dictionary<string, object>;
      if (source == null) throw new FormatException("config must be an object");
      var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
      foreach (var entry in source) result[entry.Key] = entry.Value;
      return result;
    }

    Dictionary<string, object> ToDictionary()
    {
      var accounts = new List<object>();
      foreach (var account in OpenCodeAccounts)
        accounts.Add(account == null ? null : new Dictionary<string, object> {
          { "Name", account.Name }, { "Key", account.Key }, { "AuthPath", account.AuthPath }
        });
      object commandCode = CommandCode == null ? null : (object)new Dictionary<string, object> {
        { "Name", CommandCode.Name }, { "Key", CommandCode.Key }
      };
      return new Dictionary<string, object> {
        { "Monitor", Monitor }, { "OffsetX", OffsetX }, { "OffsetY", OffsetY },
        { "Docked", Docked }, { "RefreshSeconds", RefreshSeconds },
        { "ChatGptAuthPath", ChatGptAuthPath },
        { "OpenCodeAccounts", accounts }, { "CommandCode", commandCode }
      };
    }

    static List<OpenCodeAccountConfig> DefaultOpenCodeAccounts()
    {
      // OpenCode 1 的常见凭据位置：Windows 本地数据目录，或 XDG 风格的 home
      string localData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "opencode", "auth.json");
      string home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "opencode", "auth.json");
      string path = File.Exists(localData) ? localData : File.Exists(home) ? home : null;
      if (path == null) return new List<OpenCodeAccountConfig>(); // 未配置账户时整块隐藏
      return new List<OpenCodeAccountConfig>
      {
        new OpenCodeAccountConfig { Name = "个人", AuthPath = path }
      };
    }
  }

  // =======================================================================
  // 数据模型
  // =======================================================================
  class UsageWindow
  {
    public string Label;
    public double UsedPct, ResetMs;
    public bool Unlimited;
    public bool NotStarted; // rolling 窗口无用量: 服务端返回的是占位 resetsAt
  }

  class FetchResult
  {
    public readonly IList<UsageWindow> Windows;
    public string Error;
    public DateTime LastSuccess;

    public FetchResult(bool fixedWindows = false)
    {
      Windows = fixedWindows ? (IList<UsageWindow>)new UsageWindow[3] : new List<UsageWindow>();
    }
  }

  // =======================================================================
  // DataFetcher: 无状态取数；ChatGPT 与每个 OpenCode 账户可并行调用
  // =======================================================================
  class DataFetcher
  {
    const string WhamUrl = "https://chatgpt.com/backend-api/wham/usage";
    const string TokenUrl = "https://auth.openai.com/oauth/token";
    const string CodexClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    const string GoUsageUrl = "https://opencode.ai/zen/go/v1/usage";
    const string CcCreditsUrl = "https://api.commandcode.ai/alpha/billing/credits";
    const string CcSubscriptionsUrl = "https://api.commandcode.ai/alpha/billing/subscriptions";
    // Cloudflare 1010: opencode.ai 按浏览器签名拦截, 需浏览器 UA
    const string BrowserUA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    public FetchResult FetchGpt(string authPath)
    {
      var result = new FetchResult();
      try
      {
        FetchGptCore(result, authPath, GetWhamUsage, RequestTokenRefresh);
        result.LastSuccess = DateTime.Now;
      }
      catch (Exception e) { result.Error = Short(e.Message); }
      return result;
    }

    static string Short(string s) { return s.Length > 80 ? s.Substring(0, 80) + "…" : s; }

    static object Value(Dictionary<string, object> d, string key)
    {
      object value;
      return d != null && d.TryGetValue(key, out value) ? value : null;
    }

    static Dictionary<string, object> Map(Dictionary<string, object> d, string key)
    {
      return Value(d, key) as Dictionary<string, object>;
    }

    static string Text(Dictionary<string, object> d, string key)
    {
      return Convert.ToString(Value(d, key) ?? "");
    }

    // ---------------- ChatGPT ----------------
    void FetchGptCore(FetchResult result, string authPath,
      Func<string, string> getUsage, Func<string, string> requestRefresh)
    {
      // 留空走 Windows 侧默认位置；显式路径不回退到其他凭据。
      if (string.IsNullOrEmpty(authPath))
        authPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
      if (!File.Exists(authPath)) throw new Exception("auth 文件不存在: " + authPath);
      authPath = Path.GetFullPath(authPath);
      string access = ReadAccessToken(File.ReadAllText(authPath, Encoding.UTF8));
      string resp = getUsage(access);
      if (resp == null)
      {
        // CLI 可能已在首次请求期间刷新；先复用磁盘里的新 access token。
        string current = File.ReadAllText(authPath, Encoding.UTF8);
        string latest = ReadAccessToken(current);
        if (latest != access) resp = getUsage(latest);
        if (resp == null)
        {
          string refreshed = RefreshGptFile(authPath, current, requestRefresh);
          resp = getUsage(refreshed);
          if (resp == null) throw new Exception("wham/usage 刷新后仍拒绝");
        }
      }
      ParseWham(result, resp);
    }

    static string ReadAccessToken(string json)
    {
      var dict = Json.Parse(json) as Dictionary<string, object>;
      string access = Text(Map(dict, "tokens"), "access_token");
      if (access.Length == 0) throw new Exception("codex auth.json 无 access_token");
      return access;
    }

    static string RefreshGptFile(string authPath, string current, Func<string, string> requestRefresh)
    {
      var dict = Json.Parse(current) as Dictionary<string, object>;
      var tokens = Map(dict, "tokens");
      string refresh = Text(tokens, "refresh_token");
      if (refresh.Length == 0) throw new Exception("codex auth.json 无 refresh_token");
      string temp = Path.Combine(Path.GetDirectoryName(authPath), ".milkman-auth-" + Guid.NewGuid().ToString("N") + ".tmp");
      bool wsl = temp.StartsWith(@"\\wsl.localhost\", StringComparison.OrdinalIgnoreCase)
        || temp.StartsWith(@"\\wsl$\", StringComparison.OrdinalIgnoreCase);
      try
      {
        // 先确认能创建私有暂存文件，再消耗一次性 refresh_token。
        PrepareAuthFile(temp, wsl);
        CheckAuthUnchanged(authPath, current);
        TokenRefresh refreshed = ParseRefresh(requestRefresh(refresh));
        ApplyRefresh(tokens, refreshed);
        dict["last_refresh"] = Program.NowRfc3339();
        using (var file = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None))
        {
          byte[] bytes = Encoding.UTF8.GetBytes(Json.Serialize(dict));
          file.Write(bytes, 0, bytes.Length);
          file.Flush(true);
        }
        CheckAuthUnchanged(authPath, current);
        // WSL 不支持 ReplaceFileW；同目录 Rename Overwrite 不允许退化成复制。
        if (wsl)
        {
          if (!NativeMethods.MoveFileEx(temp, authPath, 1)) // MOVEFILE_REPLACE_EXISTING
            throw new System.ComponentModel.Win32Exception();
        }
        else File.Replace(temp, authPath, null);
        return refreshed.Access;
      }
      finally
      {
        try { File.Delete(temp); } catch { }
      }
    }

    static void CheckAuthUnchanged(string path, string expected)
    {
      if (!string.Equals(File.ReadAllText(path, Encoding.UTF8), expected, StringComparison.Ordinal))
        throw new IOException("Codex 凭据已变更，请重试");
    }

    static void PrepareAuthFile(string path, bool wsl)
    {
      if (!wsl)
      {
        var acl = new System.Security.AccessControl.FileSecurity();
        acl.SetAccessRuleProtection(true, false);
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
          acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(identity.User,
            System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        using (new FileStream(path, FileMode.CreateNew, System.Security.AccessControl.FileSystemRights.FullControl,
          FileShare.None, 4096, FileOptions.None, acl)) { }
        return;
      }
      // UNC 新文件继承发行版 umask，先创建空文件，再用同一发行版收紧权限。
      string[] parts = path.Substring(2).Split(new[] { '\\' }, 3);
      if (parts.Length != 3 || Array.Exists(parts[1].ToCharArray(), c => char.IsWhiteSpace(c) || c == '"'))
        throw new IOException("无法保护 WSL 凭据临时文件");
      using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
      var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "wsl.exe"),
        "--distribution " + parts[1] + " --exec /usr/bin/xargs -0 /bin/chmod 600 --");
      start.UseShellExecute = false;
      start.WorkingDirectory = Environment.SystemDirectory;
      start.CreateNoWindow = true;
      start.RedirectStandardInput = true;
      using (var process = System.Diagnostics.Process.Start(start))
      {
        // NUL 分隔传路径，保留空格和非 ASCII 字符、不经过 shell。
        byte[] bytes = Encoding.UTF8.GetBytes("/" + parts[2].Replace('\\', '/') + "\0");
        var input = process.StandardInput.BaseStream;
        input.Write(bytes, 0, bytes.Length);
        input.Dispose();
        if (!process.WaitForExit(15000))
        {
          try { process.Kill(); } catch (InvalidOperationException) { }
          throw new IOException("设置 WSL 凭据临时文件权限超时");
        }
        if (process.ExitCode != 0) throw new IOException("无法保护 WSL 凭据临时文件");
      }
    }

    // 刷新响应里的 token 是成对轮换的：refresh_token 一次性，服务端换发新的一枚。
    // 只写回 access_token 会把作废的 refresh_token 留在 auth.json 里，下次刷新被 reused 拒绝。
    sealed class TokenRefresh
    {
      public string Access = "";
      public string Refresh = "";
      public string IdToken = "";
    }

    static string RequestTokenRefresh(string refreshToken)
    {
      var body = "client_id=" + CodexClientId + "&grant_type=refresh_token&refresh_token=" + Uri.EscapeDataString(refreshToken);
      return HttpPostForm(TokenUrl, body);
    }

    static TokenRefresh ParseRefresh(string text)
    {
      // 非 JSON（例如 Cloudflare 拦截页）也归到解析失败，不把语法制式异常抛给 UI
      Dictionary<string, object> d = null;
      try { d = Json.Parse(text) as Dictionary<string, object>; }
      catch { }
      if (d == null) throw new Exception("刷新失败: 响应无法解析");
      // 刷新可能返回 refresh_token_expired / reused / invalidated 等错误码
      string error = RefreshError(d);
      if (error.Length > 0) throw new Exception("刷新失败: " + error);
      var refreshed = new TokenRefresh();
      refreshed.Access = Text(d, "access_token");
      refreshed.Refresh = Text(d, "refresh_token");
      refreshed.IdToken = Text(d, "id_token");
      if (string.IsNullOrEmpty(refreshed.Access)) throw new Exception("刷新失败: 响应无 access_token");
      return refreshed;
    }

    // error 可能是字符串，也可能是 {code, message} 对象（reused/expired 都在 code 里）
    static string RefreshError(Dictionary<string, object> d)
    {
      var detail = Map(d, "error");
      if (detail == null) return Text(d, "error");
      string code = Text(detail, "code");
      return code.Length > 0 ? code : Text(detail, "message");
    }

    // 只覆盖响应里确实带回来的字段，缺省的保持原值
    static void ApplyRefresh(Dictionary<string, object> tokens, TokenRefresh refreshed)
    {
      tokens["access_token"] = refreshed.Access;
      if (!string.IsNullOrEmpty(refreshed.Refresh)) tokens["refresh_token"] = refreshed.Refresh;
      if (!string.IsNullOrEmpty(refreshed.IdToken)) tokens["id_token"] = refreshed.IdToken;
    }

    void ParseWham(FetchResult result, string resp)
    {
      var d = Json.Parse(resp) as Dictionary<string, object>;
      if (d == null) throw new Exception("wham 响应无法解析");

      var rl = Map(d, "rate_limit");
      if (rl != null)
      {
        // 实际响应里 primary/secondary 的秒数因账号而异（plus 当前=604800 周窗口），标签按秒数动态生成
        AddWindow(result, rl, "primary_window", null);
        AddWindow(result, rl, "secondary_window", null);
      }
      // code_review_rate_limit 是独立周限，可选展示
      var crl = Map(d, "code_review_rate_limit");
      if (crl != null) AddWindowFromDict(result, Map(crl, "primary_window") ?? crl, "代码审查");
      if (result.Windows.Count == 0) throw new Exception("wham/usage 响应无用量窗口");
      EnsureFiveHourWindow(result.Windows);
    }

    // 服务端临时不返回 5h 时仍保留版位；字段恢复后自动换回真实数据。
    static void EnsureFiveHourWindow(IList<UsageWindow> windows)
    {
      int index = -1;
      for (int i = 0; i < windows.Count; i++)
        if (windows[i].Label == "5h") { index = i; break; }
      if (index < 0)
        windows.Insert(0, new UsageWindow { Label = "5h", Unlimited = true });
      else if (index > 0)
      {
        UsageWindow window = windows[index];
        windows.RemoveAt(index);
        windows.Insert(0, window);
      }
    }

    void AddWindow(FetchResult result, Dictionary<string, object> rl, string key, string label)
    {
      AddWindowFromDict(result, Map(rl, key), label);
    }

    void AddWindowFromDict(FetchResult result, Dictionary<string, object> w, string label)
    {
      object used = Value(w, "used_percent");
      if (used == null) return;
      var win = new UsageWindow();
      // 无显式 label 时按窗口秒数生成（18000→5h, 604800 或其他周四→周, 其余按天/小时）
      if (label == null)
      {
        object seconds = Value(w, "limit_window_seconds");
        long sec = seconds != null ? Convert.ToInt64(seconds) : 0;
        if (sec == 18000) label = "5h";
        else if (sec == 604800) label = "周";
        else if (sec >= 86400 && sec % 86400 == 0) label = sec / 86400 + "天";
        else if (sec >= 3600 && sec % 3600 == 0) label = sec / 3600 + "h";
        else label = (sec / 3600.0).ToString("F1") + "h";
      }
      win.Label = label;
      win.UsedPct = Convert.ToDouble(used, CultureInfo.InvariantCulture) / 100.0;
      double resetMs = 0;
      object reset = Value(w, "reset_at") ?? Value(w, "resets_at_time");
      object after = Value(w, "reset_after_seconds");
      if (reset != null) resetMs = Convert.ToDouble(reset) * 1000;
      else if (after != null) resetMs = Program.NowUnixMs() + Convert.ToDouble(after) * 1000;
      win.ResetMs = resetMs;
      result.Windows.Add(win);
    }

    // ---------------- OpenCode Go (参照 macOS openusage 实现) ----------------
    // 官方端点: GET https://opencode.ai/zen/go/v1/usage, Bearer = auth.json 中 opencode-go.key
    // 返回 { usage: { rolling, weekly, monthly } }, 各含 percent(0-100) + resetsAt(ISO8601),
    // 账户级服务端计费(与官方 dashboard 一致, 其他机器/工具消费同样计入)
    public FetchResult FetchGo(OpenCodeAccountConfig cfg)
    {
      var account = new FetchResult(true);
      try
      {
        FetchGoCore(account, cfg);
        account.LastSuccess = DateTime.Now;
      }
      catch (Exception e) { account.Error = Short(e.Message); }
      return account;
    }

    void FetchGoCore(FetchResult account, OpenCodeAccountConfig cfg)
    {
      // OpenCode 2 把凭据迁进 opencode.db，auth.json 不再更新；配置里的 Key 优先。
      string key = (cfg.Key ?? "").Trim();
      if (key.Length == 0)
      {
        string authPath = cfg.AuthPath;
        if (string.IsNullOrEmpty(authPath) || !File.Exists(authPath))
          throw new Exception("未配置 Go key");
        var d = Json.Parse(File.ReadAllText(authPath)) as Dictionary<string, object>;
        var goe = Map(d, "opencode-go");
        key = Text(goe, "key").Trim();
        if (key.Length == 0) throw new Exception("未登录 OpenCode Go");
      }

      string body; int status;
      HttpGetWithStatus(GoUsageUrl, key, out body, out status);
      if (status == 401) throw new Exception("Go key 被拒绝");
      if (status == 403) throw new Exception("该 key 无 Go 订阅");
      if (status < 200 || status >= 300) throw new Exception("Go usage HTTP " + status);
      var u = Json.Parse(body) as Dictionary<string, object>;
      var usage = Map(u, "usage");
      if (usage == null) throw new Exception("Go usage 响应无法解析");
      account.Windows[0] = ParseGoWindow(usage, "rolling", "5h", true);
      account.Windows[1] = ParseGoWindow(usage, "weekly", "周", false);
      account.Windows[2] = ParseGoWindow(usage, "monthly", "月", false);
    }

    UsageWindow ParseGoWindow(Dictionary<string, object> usage, string key, string label, bool rolling)
    {
      var w = Map(usage, key);
      if (w == null) return null;
      object percent = Value(w, "percent");
      if (percent == null) throw new Exception("Go " + key + " 无 percent");
      var g = new UsageWindow { Label = label, UsedPct = Convert.ToDouble(percent) / 100.0, ResetMs = 0 };
      string resetsAt = Text(w, "resetsAt");
      DateTimeOffset t;
      if (DateTimeOffset.TryParse(resetsAt, out t))
        g.ResetMs = (long)((t.UtcDateTime - new DateTime(1970, 1, 1)).TotalMilliseconds);
      // 无用量时 rolling 的 resetsAt 是「现在 + 5h」占位值（openusage 同款判定），显示“未开始”而非倒计时
      if (rolling && g.UsedPct == 0 && g.ResetMs > 0
        && Math.Abs(g.ResetMs - Program.NowUnixMs() - 5 * 3600 * 1000L) <= 2000)
        g.NotStarted = true;
      return g;
    }

    // ---------------- Command Code (GOAT / Pro 等套餐) ----------------
    // 官方 CLI 里 /usage 用的内部接口(未公开, 可能变动):
    //   GET /alpha/billing/credits, Bearer = Studio API key
    //   -> { credits: { monthlyCredits, purchasedCredits, freeCredits, monthlyCreditsGranted? },
    //        windowLimits: { limited, fiveHour: { used, cap, resetAt }, weekly: { ... } } }
    //   GET /alpha/billing/subscriptions -> 套餐 id 与账单周期(月度重置时间); 仅兜底, 失败不影响滚动窗口
    // resetAt 是 epoch 毫秒(0 = 窗口未开始); 5h/周是滚动窗口, 从首次请求起算
    public FetchResult FetchCommandCode(CommandCodeConfig cfg)
    {
      var snapshot = new FetchResult(true);
      try
      {
        FetchCommandCodeCore(snapshot, cfg);
        snapshot.LastSuccess = DateTime.Now;
      }
      catch (Exception e) { snapshot.Error = Short(e.Message); }
      return snapshot;
    }

    void FetchCommandCodeCore(FetchResult snapshot, CommandCodeConfig cfg)
    {
      string key = (cfg.Key ?? "").Trim();
      if (key.Length == 0) throw new Exception("未配置 Command Code key");

      string body; int status;
      HttpGetWithStatus(CcCreditsUrl, key, out body, out status);
      if (status == 401) throw new Exception("Command Code key 被拒绝");
      if (status == 403) throw new Exception("该 key 无 Command Code 权限");
      if (status < 200 || status >= 300) throw new Exception("Command Code usage HTTP " + status);

      // 订阅信息只提供月度分母(套餐表)与月度重置时间; 失败时这两项留空, 滚动窗口照常显示
      string subscriptions = null;
      try
      {
        int subsStatus;
        HttpGetWithStatus(CcSubscriptionsUrl, key, out subscriptions, out subsStatus);
        if (subsStatus < 200 || subsStatus >= 300) subscriptions = null;
      }
      catch { subscriptions = null; }

      ParseCommandCode(snapshot, body, subscriptions);
    }

    void ParseCommandCode(FetchResult snapshot, string creditsBody, string subscriptionsBody)
    {
      var root = Json.Parse(creditsBody) as Dictionary<string, object>;
      var credits = Map(root, "credits");
      if (credits == null) throw new Exception("Command Code 响应无法解析");

      string planId = "";
      double periodEndMs = 0;
      if (!string.IsNullOrEmpty(subscriptionsBody))
      {
        try
        {
          var subscription = Map(Json.Parse(subscriptionsBody) as Dictionary<string, object>, "data");
          planId = Text(subscription, "planId");
          periodEndMs = TimestampMs(Value(subscription, "currentPeriodEnd"));
        }
        catch (FormatException) { }
        catch (InvalidCastException) { }
        catch (OverflowException) { }
      }

      var limits = Map(root, "windowLimits") ?? Map(credits, "windowLimits");
      bool limited = true;
      if (limits != null)
      {
        object limitedFlag = Value(limits, "limited");
        if (limitedFlag is bool && !(bool)limitedFlag) limited = false;
      }
      snapshot.Windows[0] = limited ? ParseCcWindow(limits, "fiveHour", "5h") : null;
      snapshot.Windows[1] = limited ? ParseCcWindow(limits, "weekly", "周") : null;
      snapshot.Windows[2] = ParseCcMonthlyWindow(credits, planId, periodEndMs);
    }

    UsageWindow ParseCcWindow(Dictionary<string, object> limits, string key, string label)
    {
      var window = Map(limits, key);
      if (window == null) return null;
      object cap = Value(window, "cap");
      if (cap == null) return null;
      double capValue = ToDouble(cap);
      if (capValue <= 0) return null;
      var parsed = new UsageWindow { Label = label, UsedPct = ToDouble(Value(window, "used") ?? 0) / capValue };
      parsed.ResetMs = TimestampMs(Value(window, "resetAt"));
      if (parsed.ResetMs <= 0) parsed.NotStarted = true; // resetAt=0: 滚动窗口尚未开始
      return parsed;
    }

    UsageWindow ParseCcMonthlyWindow(Dictionary<string, object> credits, string planId, double periodEndMs)
    {
      object remainingValue = Value(credits, "monthlyCredits");
      if (remainingValue == null) return null;
      double remaining = ToDouble(remainingValue);
      double granted = ToDouble(Value(credits, "monthlyCreditsGranted") ?? 0);
      if (granted <= 0) granted = PlanMonthlyCredits(planId);
      if (granted <= 0) return null; // 分母未知时不编造上限, 该版位显示 “—”
      double used = 1.0 - remaining / granted;
      return new UsageWindow {
        Label = "月",
        UsedPct = used < 0 ? 0 : used,
        ResetMs = periodEndMs
      };
    }

    // CLI 内置套餐表的月度 credits（服务端没返回 monthlyCreditsGranted 时兜底；未知套餐返回 0）
    static double PlanMonthlyCredits(string planId)
    {
      switch (planId)
      {
        case "individual-goat": return 70;
        case "individual-pro": return 30;
        case "individual-go": return 10;
        case "individual-go-v1": return 10;
        default: return 0;
      }
    }

    // resetAt 是 epoch 毫秒（0 = 窗口未开始）; currentPeriodEnd 是 ISO8601; 秒级时间戳也兼容
    static double TimestampMs(object value)
    {
      if (value == null) return 0;
      var text = value as string;
      if (text != null)
      {
        DateTimeOffset parsed;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed)) return 0;
        return (parsed.UtcDateTime - new DateTime(1970, 1, 1)).TotalMilliseconds;
      }
      double number = ToDouble(value);
      if (number <= 0) return 0;
      return number < 100000000000.0 ? number * 1000.0 : number;
    }

    static double ToDouble(object value)
    {
      var text = value as string;
      if (text == null) return Convert.ToDouble(value, CultureInfo.InvariantCulture);
      double parsed;
      return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
    }

    // ---------------- HTTP ----------------
    // 只有 401 进入 OAuth 刷新；限流和其他失败交给当前来源的错误状态。
    static string GetWhamUsage(string bearer)
    {
      string body; int status;
      HttpGetWithStatus(WhamUrl, bearer, out body, out status);
      return WhamResponse(body, status);
    }

    static string WhamResponse(string body, int status)
    {
      if (status == 401) return null;
      if (status < 200 || status >= 300) throw new Exception("wham/usage HTTP " + status);
      return body;
    }

    static HttpWebRequest CreateRequest(string url, string method, string bearer)
    {
      var req = (HttpWebRequest)WebRequest.Create(url);
      req.Method = method;
      req.Accept = "application/json";
      req.UserAgent = BrowserUA;
      req.Timeout = 15000;
      if (bearer != null) req.Headers["Authorization"] = "Bearer " + bearer;
      return req;
    }

    static void HttpGetWithStatus(string url, string bearer, out string body, out int status)
    {
      body = ReadResponse(CreateRequest(url, "GET", bearer), out status);
    }

    static string HttpPostForm(string url, string body)
    {
      var req = CreateRequest(url, "POST", null);
      req.ContentType = "application/x-www-form-urlencoded";
      var bytes = Encoding.UTF8.GetBytes(body);
      req.ContentLength = bytes.Length;
      using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
      int status;
      return ReadResponse(req, out status);
    }

    // 4xx/5xx 也保留响应体（如 OAuth 错误码），无 HTTP 响应的网络错误才抛出。
    static string ReadResponse(HttpWebRequest req, out int status)
    {
      HttpWebResponse response;
      try { response = (HttpWebResponse)req.GetResponse(); }
      catch (WebException e)
      {
        response = e.Response as HttpWebResponse;
        if (response == null) throw new Exception("HTTP " + e.Message);
      }
      using (response)
      using (var stream = response.GetResponseStream())
      using (var reader = new StreamReader(stream))
      {
        status = (int)response.StatusCode;
        return reader.ReadToEnd();
      }
    }
  }

  // =======================================================================
  // 开机自启: HKCU Run
  // =======================================================================
  static class AutoStart
  {
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Milkman";

    public static void Ensure()
    {
      try
      {
        string value = "\"" + Application.ExecutablePath + "\"";
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
          if (k != null && string.Equals(k.GetValue(Name) as string, value, StringComparison.Ordinal)) return;
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
          k.SetValue(Name, value, RegistryValueKind.String);
      }
      catch { }
    }
  }

  // =======================================================================
  // 浮窗 UI：无边框、置顶、可拖动、双击切换详情、托盘
  // =======================================================================
  class MilkmanForm : Form
  {
    // 奶白暖色系（Milkman 主题: 牛奶/奶牛, 参照 DeepSeekTimeline 的浅色基调）
    const int W = 520; // DeepSeek 同宽
    static readonly Color Bg = Color.FromArgb(251, 248, 242);        // 奶白背景
    static readonly Color TextMain = Color.FromArgb(40, 38, 34);     // 暖墨
    static readonly Color TextSub = Color.FromArgb(116, 110, 100);   // 暖灰
    static readonly Color TextFaint = Color.FromArgb(158, 151, 139); // 淡暖灰
    static readonly Color Accent = Color.FromArgb(198, 111, 100);    // 暖红(保留)
    static readonly Color TrackCol = Color.FromArgb(235, 228, 216);  // 暖浅轨道
    static readonly Color BarGreen = Color.FromArgb(46, 204, 113);
    static readonly Color BarYellow = Color.FromArgb(241, 196, 15);
    static readonly Color BarRed = Color.FromArgb(231, 76, 60);
    static readonly SolidBrush BrBg, BrText, BrSub, BrFaint, BrAccent, BrTrack, BrGreen, BrYellow, BrRed;
    static readonly Pen PenCloseDim = new Pen(Color.FromArgb(165, 167, 173), 1.6f);
    static readonly Pen PenCloseHot = new Pen(Color.FromArgb(210, 96, 86), 1.6f);
    static readonly Pen PenBorder = new Pen(Color.FromArgb(232, 224, 210));
    static readonly Pen PenBarBorder = new Pen(Color.FromArgb(212, 202, 186));

    static MilkmanForm()
    {
      BrBg = new SolidBrush(Bg);
      BrText = new SolidBrush(TextMain);
      BrSub = new SolidBrush(TextSub);
      BrFaint = new SolidBrush(TextFaint);
      BrAccent = new SolidBrush(Accent);
      BrTrack = new SolidBrush(TrackCol);
      BrGreen = new SolidBrush(BarGreen);
      BrYellow = new SolidBrush(BarYellow);
      BrRed = new SolidBrush(BarRed);
      BrAccentOff = new SolidBrush(AccentOff);
      BrAccentCc = new SolidBrush(AccentCc);
      for (int count = 1; count <= 3; count++)
        GptBandPaths[count - 1] = CreateBandPaths(BandTop1, count);
    }

    // 字体层次（参照 DeepSeekTimeline）。
    // 中文/英文分用两款字体：拉丁字符用 Segoe UI（Windows 原生 UI 字体，字形紧凑），
    // 中文用微软雅黑（雅黑 UI 变体字形偏窄）。两者各自按目标屏幕实际像素重建，
    // 并在绘制时绕开画布缩放，让 GDI+ 去做字形微调。
    Font fontBig, fontMid, fontTick;      // 中文
    Font latinBig, latinMid, latinTick;   // 拉丁字符
    // 字体与几何共用 144 DPI 基准：这些是 144 下的像素，乘 _scale 得到该屏真实字号。
    const int BigDesignPx = 24, MidDesignPx = 18, TickDesignPx = 15;
    // 中英文分用两款字体：拉丁字符用 Segoe UI，中文用微软雅黑。
    // 两款都是 Windows 自带，无需携带字体文件。
    const string LatinFace = "Segoe UI";
    const string CjkFace = "Microsoft YaHei UI";
    // 缩放系数：窗口按所在显示器 DPI 相对 144 基准缩放。版面一直是按 150% 显示器调校的，
    // 用 144 作基准就能在 100% 屏上回到原来的观感（520→347），在 150% 屏上保持原尺寸（520）。
    const int BaseDpi = 144;
    float _scale = 1f;
    int _designH;   // 设计单位高度
    int _formH;     // 实际像素高度
    Rectangle closeRect = new Rectangle(W - 26, 7, 15, 15);
    bool hoverClose;
    SourceRow _hoverAction;

    // 吸附与动画（参照 DeepSeekTimeline：WM_NCHITTEST 拖动 + 顶部吸附 + 缓出）
    const int WM_NCHITTEST = 0x84, HTCLIENT = 1, HTCAPTION = 2;
    const int WM_ENTERSIZEMOVE = 0x231, WM_EXITSIZEMOVE = 0x232;
    const int WM_WINDOWPOSCHANGED = 0x47, WM_STYLECHANGED = 0x7D;
    const int GWL_EXSTYLE = -20, WS_EX_TOPMOST = 0x00000008;
    const int WM_SYSCOMMAND = 0x112, SC_MAXIMIZE = 0xF030;
    const int WM_NCLBUTTONDBLCLK = 0xA3;
    const int WM_DPICHANGED = 0x02E0;
    // 吸附收起时顶部保留的提示条高度。这是鼠标唯一能点到的目标，必须固定为实际像素，
    // 不随 DPI 缩放：切成设计单位会在 100% 屏上缩成 3px，直接变得点不中。
    const int Peek = 5;
    // 收起时的窗口顶端：始终露出 Peek 个实际像素
    int CollapsedTop { get { return monRect.Top - _formH + Peek; } }
    const int DockIdleMs = 100, DockAnimationMs = 30;
    bool docked, expanded, dragging, pinned; // pinned = 双击钉住, DockTick 不自动收放
    Rectangle monRect;
    Timer _dockTimer;

    Config _cfg;
    DataFetcher _fetcher;
    readonly RefreshSession _refresh;
    Timer _refreshTimer = new Timer();
    Timer _clockTimer = new Timer();

    internal sealed class SourceRow
    {
      public readonly Func<FetchResult> Fetch;
      public readonly string Title;
      public readonly int Y;
      public readonly SolidBrush Dot;
      public readonly GraphicsPath[][] Paths;
      public FetchResult Result { get; private set; }
      public bool Busy { get; private set; }
      public bool Auto { get; private set; }

      public SourceRow(string title, int y, SolidBrush dot, GraphicsPath[][] paths, Func<FetchResult> fetch)
      {
        Title = title; Y = y; Dot = dot; Paths = paths; Fetch = fetch;
        Result = new FetchResult();
      }

      public bool TryStart(bool manual)
      {
        if (Busy || (!manual && !Auto)) return false;
        Busy = true;
        return true;
      }

      public void Apply(FetchResult result)
      {
        Busy = false;
        Auto = result.Error == null;
        if (Auto) Result = result;
        else Result.Error = result.Error; // 保留上次成功数据和时间
      }
    }

    // 仅 UI 线程驱动状态；派发后无需等待其他行完成。
    internal sealed class RefreshSession
    {
      public readonly List<SourceRow> Rows = new List<SourceRow>();
      readonly Action<SourceRow> dispatch;
      readonly Action enableNetwork;
      public bool Started { get; private set; }
      public RefreshSession(Action enableNetwork, Action<SourceRow> dispatch)
      {
        this.enableNetwork = enableNetwork;
        this.dispatch = dispatch;
      }
      public string ActionText(SourceRow row)
      {
        if (!Started) return row == Rows[0] ? "获取用量" : null;
        return !row.Busy && row.Result.Error != null ? "重试" : null;
      }
      public bool Click(SourceRow row)
      {
        if (ActionText(row) == null) return false;
        if (!Started)
        {
          Started = true;
          enableNetwork();
          foreach (var source in Rows) if (source.TryStart(true)) dispatch(source);
        }
        else if (row.TryStart(true)) dispatch(row);
        return true;
      }
      public bool RefreshDue()
      {
        bool any = false;
        foreach (var row in Rows)
          if (row.TryStart(false)) { dispatch(row); any = true; }
        return any;
      }
      public bool HasCountdown(long now)
      {
        foreach (var row in Rows)
          foreach (var w in row.Result.Windows)
            if (w != null && !w.NotStarted && !w.Unlimited && w.ResetMs > now) return true;
        return false;
      }
    }

    public MilkmanForm()
    {
      _refresh = new RefreshSession(EnableNetwork, DispatchRefresh);
      _cfg = Config.Load();
      _fetcher = new DataFetcher();
      _refresh.Rows.Add(new SourceRow("ChatGPT Plus", 12, BrAccent, GptBandPaths,
        () => _fetcher.FetchGpt(_cfg.ChatGptAuthPath)));
      foreach (var account in _cfg.OpenCodeAccounts)
        AddSource("OpenCode Go", account.Name, BrAccentOff, () => _fetcher.FetchGo(account));
      var cc = _cfg.CommandCode;
      if (cc != null) AddSource("Command Code", cc.Name, BrAccentCc, () => _fetcher.FetchCommandCode(cc));
      _designH = 16 + _refresh.Rows.Count * GoRowH;

      // 字体按屏幕实际像素创建（SetScale 里重建），这里先建一份初值
      RebuildFonts();

      // ExtractAssociatedIcon 不接受 UNC 路径(网络共享/WSL 互操作路径启动), 抛 ArgumentException 会中断启动
      try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
      catch (ArgumentException) { } // 取不到就保留系统默认图标
      FormBorderStyle = FormBorderStyle.None;
      TopMost = true;
      ShowInTaskbar = false;
      StartPosition = FormStartPosition.Manual;
      MaximizeBox = false;
      MinimizeBox = false;
      Opacity = 0.80; // 与 DeepSeek 一致
      SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
        | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
      RestoreLocation();


      MouseMove += OnMouseMoveHook;
      MouseLeave += OnMouseLeaveHook;
      MouseDown += OnMouseDownHook;
      Shown += OnShownHook;
      FormClosing += (s, e) => SaveState();

      _dockTimer = new Timer();
      _dockTimer.Interval = DockIdleMs;
      _dockTimer.Tick += (s, e) => DockTick();

      Load += (s, e) =>
      {
        // 吸附状态已在 RestoreLocation 里按目标屏是否可吸附判定过，这里沿用
        if (docked) { expanded = false; pinned = false; }
        // 句柄就绪后按最终所在屏重算尺寸与位置（吸附态贴屏幕顶边）
        ApplyLayout(Screen.FromControl(this),
          new Point(Math.Max(0, _cfg.OffsetX), Math.Max(0, _cfg.OffsetY)));
        if (docked) _dockTimer.Start();
      };
      // 插拔屏幕、改分辨率或缩放的统一入口：重新解析所在屏并按锚点归位
      Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
      FormClosing += (s, e) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;

      _refreshTimer.Interval = _cfg.RefreshSeconds * 1000;
      _refreshTimer.Tick += (s, e) => { if (_refresh.RefreshDue()) RepaintUsage(); };
      _clockTimer.Interval = 5000; // 与 DeepSeek 一致: 5s 周期重绘剩余时间
      _clockTimer.Tick += (s, e) =>
      {
        RepaintUsage(); // 包括倒计时刚结束的最后一次重绘
        _clockTimer.Enabled = _refresh.HasCountdown(Program.NowUnixMs());
      };

    }

    void AddSource(string provider, string name, SolidBrush dot, Func<FetchResult> fetch)
    {
      int y = GoFirstRowY + (_refresh.Rows.Count - 1) * GoRowH;
      string title = string.IsNullOrEmpty(name) ? provider : provider + " · " + name;
      _refresh.Rows.Add(new SourceRow(title, y, dot, new[] { CreateBandPaths(y + GoBandOffset, 3) }, fetch));
    }

    void RepaintUsage()
    {
      if (!docked || expanded || Top > CollapsedTop) Invalidate();
    }

    static void EnableNetwork()
    {
      try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; } catch { }
    }

    void DispatchRefresh(SourceRow row)
    {
      Task.Run(() =>
      {
        var result = row.Fetch();
        try { BeginInvoke((Action)(() => {
          row.Apply(result);
          _refreshTimer.Enabled = _refresh.Rows.Exists(r => r.Auto);
          _clockTimer.Enabled = _refresh.HasCountdown(Program.NowUnixMs());
          RepaintUsage();
        })); }
        catch (InvalidOperationException) { } // 窗口已关闭
      });
    }

    // ---------------- 系统级拖动 + 顶部吸附（参照 DeepSeekTimeline） ----------------
    // 不抢焦点 + 不占 Alt-Tab；拖动由 WM_NCHITTEST 交给系统（HTCAPTION 原生拖动）
    protected override CreateParams CreateParams
    {
      get
      {
        CreateParams cp = base.CreateParams;
        cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
        // WinForms 的 TopMost 是创建后调 SetWindowPos 补设的，后台启动（如开机自启）时会被前台锁忽略；
        // 创建参数里直接带上 WS_EX_TOPMOST 才是创建即置顶，与是否前台无关。
        if (TopMost) cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
        return cp;
      }
    }

    // TopMost 是 WinForms 的期望状态，原生窗口被降级后该属性仍可能为 true；
    // 只在收到层级/样式变更通知后补回缺失标志，不轮询、不主动抢前台。
    void EnsureTopMost()
    {
      if (!IsHandleCreated || IsDisposed || !TopMost) return;
      if ((NativeMethods.GetWindowLong(Handle, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0) return;
      NativeMethods.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0,
        0x0001 | 0x0002 | 0x0010); // HWND_TOPMOST; NOSIZE | NOMOVE | NOACTIVATE
    }

    protected override void WndProc(ref Message m)
    {
      if (m.Msg == WM_WINDOWPOSCHANGED
        || (m.Msg == WM_STYLECHANGED && m.WParam.ToInt64() == GWL_EXSTYLE))
      {
        base.WndProc(ref m);
        EnsureTopMost();
        return;
      }
      if (m.Msg == WM_DPICHANGED)
      {
        // 系统在 lParam 给出新 DPI 下的建议矩形。按微软要求用它一次性完成重排：
        // 拖动跨屏时只重排一次，不回头按当前位置反推锚点，否则会和尺寸变化互相反馈、来回抖动。
        int dpi = (int)(m.WParam.ToInt64() & 0xFFFF);
        var suggested = (NativeMethods.RECT)System.Runtime.InteropServices.Marshal.PtrToStructure(
          m.LParam, typeof(NativeMethods.RECT));
        var box = new Rectangle(suggested.L, suggested.T, suggested.R - suggested.L, suggested.B - suggested.T);
        monRect = Screen.FromRectangle(box).Bounds;
        SetScale(dpi);
        // 吸附态仍贴合屏幕顶边，拖动中的窗口跟手走建议位置
        int y = docked ? (expanded ? monRect.Top : CollapsedTop) : suggested.T;
        NativeMethods.SetWindowPos(Handle, IntPtr.Zero, suggested.L, y, Scaled(W), _formH,
          0x0004 | 0x0010); // NOZORDER | NOACTIVATE
        Invalidate();
        m.Result = IntPtr.Zero;
        return;
      }
      if (m.Msg == WM_ENTERSIZEMOVE)
      {
        dragging = true;
        base.WndProc(ref m);
        return;
      }
      if (m.Msg == WM_EXITSIZEMOVE)
      {
        // 拖拽结束: 判定 进入吸附 / 保持吸附 / 脱离吸附
        dragging = false;
        Screen target = Screen.FromControl(this);
        if (docked)
        {
          bool escaped = Top > target.Bounds.Top + Scaled(30);
          // 拖到上方还有屏幕的显示器时不再吸附，浮窗回到可见的常规位置
          if (escaped || !DockableOn(target.Bounds)) { docked = false; pinned = false; _dockTimer.Stop(); }
        }
        else if (Top <= target.Bounds.Top + Scaled(8) && DockableOn(target.Bounds))
        {
          docked = true;                              // 拖到顶 = 吸附
          expanded = true;
          _dockTimer.Start();
        }
        // 尺寸随拖动已由 WM_DPICHANGED 按目标屏设好；这里只在吸附态归位到屏幕顶边
        monRect = target.Bounds;
        if (docked) Location = new Point(Left, CollapsedTop);
        SaveState();
        base.WndProc(ref m);
        return;
      }
      if (m.Msg == WM_NCHITTEST)
      {
        int lp = (int)m.LParam;
        int sx = lp & 0xFFFF; if (sx >= 0x8000) sx -= 0x10000;
        int sy = (lp >> 16) & 0xFFFF; if (sy >= 0x8000) sy -= 0x10000;
        Point pt = ToDesign(PointToClient(new Point(sx, sy)));
        m.Result = (IntPtr)(closeRect.Contains(pt) || ActionHit(pt) != null ? HTCLIENT : HTCAPTION);
        return;
      }
      // 拦截"标题栏双击": 吸附(docked)时切换 Pin(钉住), 非吸附时双击无效果
      // pin = 保持当前展开/收起状态不动, 鼠标离开不自动收起; 再双击 unpin 恢复自动收放
      if (m.Msg == WM_NCLBUTTONDBLCLK)
      {
        if (docked)
        {
          pinned = !pinned;
          if (pinned)
          {
            expanded = true;
            NativeMethods.SetWindowPos(Handle, IntPtr.Zero, Left, monRect.Top, 0, 0,
              0x0001 | 0x0004 | 0x0010); // 立即展开后停表，钉住期间不再空轮询
            _dockTimer.Stop();
          }
          else
            _dockTimer.Start();
          Invalidate();
        }
        m.Result = IntPtr.Zero;
        return;
      }
      // 拦截"标题栏双击最大化", 保持固定浮窗(无边框窗 MaximizeBox 拦不住系统行为)
      if (m.Msg == WM_SYSCOMMAND && (m.WParam.ToInt32() & 0xFFF0) == SC_MAXIMIZE)
      {
        m.Result = IntPtr.Zero;
        return;
      }
      base.WndProc(ref m);
    }

    void OnMouseDownHook(object sender, MouseEventArgs e)
    {
      if (e.Button != MouseButtons.Left) return;
      Point p = ToDesign(e.Location);
      if (closeRect.Contains(p)) { Close(); return; } // ✕ = 退出程序
      var row = ActionHit(p);
      if (row != null && _refresh.Click(row))
      {
        _hoverAction = null;
        Cursor = Cursors.Default;
        Invalidate();
      }
    }

    void OnMouseMoveHook(object sender, MouseEventArgs e)
    {
      Point p = ToDesign(e.Location);
      bool h = closeRect.Contains(p);
      var row = ActionHit(p);
      if (h != hoverClose || row != _hoverAction)
      {
        hoverClose = h;
        _hoverAction = row;
        Cursor = h || row != null ? Cursors.Hand : Cursors.Default;
        Invalidate();
      }
    }

    void OnMouseLeaveHook(object sender, EventArgs e)
    {
      if (hoverClose || _hoverAction != null)
      {
        hoverClose = false;
        _hoverAction = null;
        Cursor = Cursors.Default;
        Invalidate();
      }
    }

    void OnShownHook(object sender, EventArgs e)
    {
      try
      {
        int round = 2;
        NativeMethods.DwmSetWindowAttribute(Handle, 33, ref round, 4);
        NativeMethods.MARGINS mg = new NativeMethods.MARGINS();
        mg.L = 1; mg.R = 1; mg.T = 1; mg.B = 1;
        NativeMethods.DwmExtendFrameIntoClientArea(Handle, ref mg);
      }
      catch { }
      EnsureTopMost(); // 显示完成后再确认一次原生置顶标志
    }

    // 位置恢复：按保存的显示器名找屏，用相对该屏的偏移算坐标；该屏不在就退回主屏。
    // 分辨率、DPI、整体布局怎么变都走同一条路径，不需要为单屏/多屏分别判断。
    void RestoreLocation()
    {
      Screen screen = FindScreen(_cfg.Monitor) ?? Screen.PrimaryScreen;
      monRect = screen.Bounds;
      Rectangle area = screen.WorkingArea;
      ApplyScale();
      // 配置里的吸附状态只在目标屏仍允许吸附时沿用，否则回到常规位置
      bool canDock = _cfg.Docked && DockableOn(monRect);
      if (_cfg.Docked && !canDock) docked = false;
      if (_cfg.OffsetX < 0 || _cfg.OffsetY < 0)
      {
        // 首次运行或旧配置：靠右下角摆放
        Location = new Point(area.Right - Width - 10, area.Bottom - _formH - 40);
        return;
      }
      Location = AnchorTo(area, new Point(_cfg.OffsetX, _cfg.OffsetY), Size, canDock);
    }

    // 按目标显示器的 DPI 设置缩放与窗口尺寸；绘制与命中测试都换算回 96 DPI 设计单位
    void ApplyScale()
    {
      SetScale(NativeMethods.DpiOf(monRect));
      ClientSize = new Size(Scaled(W), _formH);
    }

    // 只更新缩放与尺寸数值，不碰窗口位置：位置由调用方一次性设定
    void SetScale(int dpi)
    {
      _scale = dpi / (float)BaseDpi;
      _formH = Scaled(_designH);
      RebuildFonts();
    }

    // 字体按当前屏幕的实际像素重建；中英各一款，尺寸一致以保证基线对齐
    void RebuildFonts()
    {
      var old = new[] { fontBig, fontMid, fontTick, latinBig, latinMid, latinTick };
      float big = BigDesignPx * _scale, mid = MidDesignPx * _scale, tick = TickDesignPx * _scale;
      fontBig = new Font(CjkFace, big, FontStyle.Bold, GraphicsUnit.Pixel);
      fontMid = new Font(CjkFace, mid, FontStyle.Regular, GraphicsUnit.Pixel);
      fontTick = new Font(CjkFace, tick, FontStyle.Regular, GraphicsUnit.Pixel);
      latinBig = new Font(LatinFace, big, FontStyle.Bold, GraphicsUnit.Pixel);
      latinMid = new Font(LatinFace, mid, FontStyle.Regular, GraphicsUnit.Pixel);
      latinTick = new Font(LatinFace, tick, FontStyle.Regular, GraphicsUnit.Pixel);
      Font = fontMid;
      foreach (var f in old) if (f != null) f.Dispose();
    }

    int Scaled(int design) { return (int)Math.Round(design * _scale); }

    // 客户端像素坐标 → 设计单位坐标
    Point ToDesign(Point client)
    {
      return new Point((int)Math.Round(client.X / _scale), (int)Math.Round(client.Y / _scale));
    }

    static Screen FindScreen(string device)
    {
      if (string.IsNullOrEmpty(device)) return null;
      foreach (var screen in Screen.AllScreens)
        if (string.Equals(screen.DeviceName, device, StringComparison.OrdinalIgnoreCase)) return screen;
      return null;
    }

    // 只有顶边外侧没有其它屏幕覆盖时才允许吸附：吸附会把窗口移到屏幕外，
    // 若上方还有屏幕，收起的浮窗就会压到那块屏上。纯函数，便于直接验证。
    internal static bool CanDock(Rectangle screen, Rectangle[] others)
    {
      foreach (var other in others)
      {
        if (other.Top + other.Height > screen.Top) continue;         // 该屏不在上方
        if (other.Left >= screen.Right || other.Right <= screen.Left) continue; // 横向不相交
        return false;
      }
      return true;
    }

    // 把相对偏移约束在该屏工作区内，保证窗口始终可见可点。
    // 纯函数：输入显示器区域、偏移、窗口尺寸与吸附状态，输出实际坐标，便于直接验证。
    internal static Point AnchorTo(Rectangle area, Point offset, Size size, bool topEdge)
    {
      int x = Math.Max(area.Left, Math.Min(area.Left + offset.X, area.Right - size.Width));
      int y = topEdge ? area.Top : Math.Max(area.Top, Math.Min(area.Top + offset.Y, area.Bottom - size.Height));
      return new Point(x, y);
    }

    // 某块屏是否允许吸附：顶边外侧不能有其它屏幕
    static bool DockableOn(Rectangle screen)
    {
      var all = Screen.AllScreens;
      var others = new Rectangle[all.Length];
      for (int i = 0; i < all.Length; i++) others[i] = all[i].Bounds;
      return CanDock(screen, others);
    }

    // 统一的重新布局入口：换屏、改分辨率、改缩放、拖动结束都走这里。
    // anchor 是相对目标屏左上角的偏移，尺寸随该屏 DPI 重算，吸附态贴屏幕顶边。
    void ApplyLayout(Screen screen, Point anchor)
    {
      monRect = screen.Bounds;
      ApplyScale();
      Location = AnchorTo(screen.WorkingArea, anchor, Size, docked);
      if (docked) Location = new Point(Location.X, CollapsedTop);
    }

    // 显示器布局变化：重新解析所在屏并按保存的锚点归位
    void OnDisplayChanged(object sender, EventArgs e)
    {
      if (IsDisposed || !IsHandleCreated) return;
      ApplyLayout(FindScreen(_cfg.Monitor) ?? Screen.FromControl(this),
        new Point(Math.Max(0, _cfg.OffsetX), Math.Max(0, _cfg.OffsetY)));
      Invalidate();
    }

    void SaveState()
    {
      Screen screen = Screen.FromControl(this);
      Point offset = new Point(Left - screen.Bounds.Left, docked ? 0 : Top - screen.Bounds.Top);
      if (_cfg.Monitor == screen.DeviceName && _cfg.OffsetX == offset.X
        && _cfg.OffsetY == offset.Y && _cfg.Docked == docked) return;
      var cfg = Config.Load(); // 只在位置变化时重读，保留运行期间手改的账户配置
      cfg.Monitor = screen.DeviceName; cfg.OffsetX = offset.X; cfg.OffsetY = offset.Y; cfg.Docked = docked;
      if (cfg.Save())
      {
        _cfg.Monitor = cfg.Monitor; _cfg.OffsetX = offset.X; _cfg.OffsetY = offset.Y; _cfg.Docked = docked;
      }
    }

    // 吸附模式核心: 鼠标感应 + 缓出滑动动画
    void DockTick()
    {
      if (!docked || dragging || pinned) return; // pinned 时冻结自动收放
      Point mp = Cursor.Position;
      Rectangle wr = new Rectangle(Location, Size);
      // 只认窗口自身的可见范围(收起时就是顶部 5px 提示条), 不外扩任何感应区
      bool inside = Rectangle.Intersect(wr, monRect).Contains(mp);
      if (inside)
      {
        if (!expanded && !ForegroundFullscreen())
        {
          expanded = true;
          Invalidate(); // 展开瞬间立即按最新数据重绘
        }
      }
      else if (expanded)
      {
        expanded = false; // 检测到离开即开始收起，无额外等待
      }
      int target = expanded ? monRect.Top : CollapsedTop;
      if (wr.Top != target)
      {
        int dy = target - wr.Top;
        int step = dy / 3;
        if (step == 0) step = dy > 0 ? 1 : -1;
        NativeMethods.SetWindowPos(Handle, IntPtr.Zero, wr.Left, wr.Top + step, 0, 0,
          0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE
      }
      int interval = Top == target ? DockIdleMs : DockAnimationMs;
      if (_dockTimer.Interval != interval) _dockTimer.Interval = interval;
    }

    // 前台程序全屏(视频/游戏)时不弹出, 防干扰
    bool ForegroundFullscreen()
    {
      IntPtr fg = NativeMethods.GetForegroundWindow();
      if (fg == IntPtr.Zero || fg == Handle) return false;
      NativeMethods.RECT r;
      if (!NativeMethods.GetWindowRect(fg, out r)) return false;
      return r.L <= monRect.Left && r.T <= monRect.Top
          && r.R >= monRect.Right && r.B >= monRect.Bottom;
    }

    // ---------------- 自绘 ----------------
    // 文字按物理像素绘制并绕开画布缩放：字形由 GDI+ 按最终尺寸微调，不会被压缩变换破坏。
    // 坐标与测量仍以设计单位表达，进出这里时换算一次。
    // 中英文分用两款字体：拉丁字符走 LatinFace，其余（中文、符号）走 CjkFace。
    // 判定该字符交给拉丁字体渲染：ASCII 可见字符与常见排版符号。
    // 中文、全角标点、⚠/∞ 之类由微软雅黑承担，避免拉丁字体缺字时回退到别的字形。
    static bool IsLatin(char c)
    {
      if (c >= 'A' && c <= 'Z') return true;
      if (c >= 'a' && c <= 'z') return true;
      if (c >= '0' && c <= '9') return true;
      return c == ' ' || c == '%' || c == '.' || c == ',' || c == ':'
        || c == '-' || c == '+' || c == '/' || c == '(' || c == ')';
    }

    // pick: 中文那一款（同为 Bold/Regular/字号），拉丁段自动换用对应拉丁字体
    Font LatinOf(Font cjk)
    {
      if (cjk == fontBig) return latinBig;
      if (cjk == fontMid) return latinMid;
      return latinTick;
    }

    // 逐段混排：返回整串宽度
    float MixedWidth(Graphics g, string s, Font cjk)
    {
      if (string.IsNullOrEmpty(s)) return 0;
      Font latin = LatinOf(cjk);
      float w = 0;
      int i = 0;
      while (i < s.Length)
      {
        bool lat = IsLatin(s[i]);
        int j = i;
        while (j < s.Length && IsLatin(s[j]) == lat) j++;
        w += g.MeasureString(s.Substring(i, j - i), lat ? latin : cjk).Width;
        i = j;
      }
      return w;
    }

    float Wd(Graphics g, string s, Font f) { return MixedWidth(g, s, f) / _scale; }

    void DrawText(Graphics g, string s, Font cjk, Brush b, float x, float y)
    {
      if (string.IsNullOrEmpty(s)) return;
      Matrix m = g.Transform;
      g.ResetTransform();
      Font latin = LatinOf(cjk);
      float px = x * _scale, py = y * _scale;
      int i = 0;
      while (i < s.Length)
      {
        bool lat = IsLatin(s[i]);
        int j = i;
        while (j < s.Length && IsLatin(s[j]) == lat) j++;
        string part = s.Substring(i, j - i);
        g.DrawString(part, lat ? latin : cjk, b, px, py);
        px += g.MeasureString(part, lat ? latin : cjk).Width;
        i = j;
      }
      g.Transform = m;
    }

    // 版式完全参照 DeepSeekTimeline: 顶部 provider 名, 中部圆角色带(独立圆角条/留间隙),
    // 每条下方 两行元信息: 百分比 + 重置时间
    const int BandH = 12, BandTop1 = 44;
    const int GoFirstRowY = 88, GoRowH = 76, GoBandOffset = 32;
    const float BoxW = W - 40f, Gap = 6f;
    static readonly Color AccentOff = Color.FromArgb(88, 143, 124);
    static readonly SolidBrush BrAccentOff;
    static readonly Color AccentCc = Color.FromArgb(176, 136, 84);   // 暖棕: Command Code
    static readonly SolidBrush BrAccentCc;
    // ChatGPT 按实际窗口数选择 1/2/3 段；OpenCode 固定三段。路径只创建一次。
    static readonly GraphicsPath[][] GptBandPaths = new GraphicsPath[3][];

    static GraphicsPath[] CreateBandPaths(float y, int count)
    {
      float width = (BoxW - (count - 1) * Gap) / count;
      var paths = new GraphicsPath[count];
      for (int i = 0; i < count; i++)
        paths[i] = RoundedRect(new RectangleF(20 + i * (width + Gap), y, width, BandH), 5f);
      return paths;
    }

    static GraphicsPath RoundedRect(RectangleF r, float rad)
    {
      GraphicsPath p = new GraphicsPath();
      p.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
      p.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
      p.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
      p.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
      p.CloseFigure();
      return p;
    }

    Rectangle ActionRect(SourceRow row)
    {
      int width = _refresh.Started ? 48 : 100;
      return new Rectangle(W - 28 - width, row.Y + 1, width, 22);
    }

    SourceRow ActionHit(Point p)
    {
      foreach (var row in _refresh.Rows)
        if (_refresh.ActionText(row) != null && ActionRect(row).Contains(p)) return row;
      return null;
    }

    string SourceStatus(bool busy, string error, DateTime lastSuccess)
    {
      if (busy) return error != null ? "正在重试…" : lastSuccess == default(DateTime) ? "正在获取…" : "正在刷新…";
      if (error != null) return error;
      return lastSuccess == default(DateTime) ? "" : "更新于 " + lastSuccess.ToString("HH:mm:ss");
    }

    // 按宽度截断：测量走混排口径，与传入的 max 同一设计单位
    string Fit(Graphics g, string text, Font font, float max)
    {
      if (MixedWidth(g, text, font) <= max * _scale) return text;
      int low = 0, high = text.Length;
      while (low < high)
      {
        int mid = (low + high + 1) / 2;
        if (MixedWidth(g, text.Substring(0, mid) + "…", font) <= max * _scale) low = mid;
        else high = mid - 1;
      }
      return text.Substring(0, low) + "…";
    }

    void DrawBlockHead(Graphics g, SourceRow row)
    {
      int rowY = row.Y;
      bool error = row.Result.Error != null, busy = row.Busy;
      string title = row.Title, action = _refresh.ActionText(row);
      string status = SourceStatus(busy, row.Result.Error, row.Result.LastSuccess);
      g.FillEllipse(row.Dot, 22, rowY + 7, 9, 9);
      DrawText(g, title, fontBig, BrText, 40, rowY - 1);
      if (!string.IsNullOrEmpty(status))
      {
        SolidBrush brush = error && !busy ? BrRed : busy ? BrSub : BrFaint;
        int right = action != null ? ActionRect(row).Left - 8 : W - 20;
        float room = right - 40 - Wd(g, title, fontBig) - 8;
        // 放不下的错误长文退回落款，完整原因由空数据行承担，避免标题行出现半截文字
        if (error && !busy && Wd(g, status, fontMid) > room) status = "无法获取信息";
        DrawText(g, status, fontMid, brush, right - Wd(g, status, fontMid), rowY + 3);
      }
      if (action != null)
      {
        bool hot = row == _hoverAction;
        Rectangle r = ActionRect(row);
        using (GraphicsPath p = RoundedRect(r, 5f))
        {
          g.FillPath(hot ? BrAccent : BrTrack, p);
          g.DrawPath(hot ? PenCloseHot : PenBarBorder, p);
        }
        DrawText(g, action, fontMid, hot ? BrBg : BrText,
          r.Left + (r.Width - Wd(g, action, fontMid)) / 2f, r.Top + 3);
      }
    }

    // 单条独立圆角用量条: 轨道浅灰 + 已用填充(状态色) + 淡边框
    void DrawBand(Graphics g, GraphicsPath p, float x, float y, float w, double frac)
    {
      g.FillPath(BrTrack, p);
      float fw = w * (float)(Math.Max(0, Math.Min(1, frac)));
      if (fw > 0)
      {
        g.SetClip(p);
        g.FillRectangle(StatusBrush(frac), x, y, fw, BandH);
        g.ResetClip();
      }
      g.DrawPath(PenBarBorder, p);
    }

    // 条下元信息: 左端窗口百分比, 右端重置时间(两端对齐, 同小字号)
    void DrawBandMeta(Graphics g, float x, float w, float y, string left, string right)
    {
      DrawText(g, left, fontTick, BrText, x, y + 2);
      if (right.Length > 0)
        DrawText(g, right, fontTick, BrSub, x + w - Wd(g, right, fontTick), y + 2);
    }

    void DrawWindows(Graphics g, IList<UsageWindow> windows, GraphicsPath[] paths, float bandY, string error)
    {
      int count = paths.Length;
      float width = (BoxW - (count - 1) * Gap) / count;
      for (int i = 0; i < count; i++)
      {
        float x = 20 + i * (width + Gap);
        var window = windows != null && i < windows.Count ? windows[i] : null;
        DrawBand(g, paths[i], x, bandY, width, window != null ? window.UsedPct : 0);
        if (window != null)
          DrawBandMeta(g, x, width, bandY + BandH + 6,
            window.Label + " " + (window.Unlimited ? "∞" : (int)(window.UsedPct * 100) + "%"),
            window.Unlimited ? ""
              : window.NotStarted ? "未开始"
              : window.ResetMs > 0 ? "重置 " + FormatRemaining(window.ResetMs) : "");
        else if (error == null)
          DrawText(g, "—", fontTick, BrSub, x, bandY + BandH + 8);
        else
          // 首段显示具体原因，其余段保持占位文案
          DrawText(g, i == 0 ? "⚠ " + Fit(g, error, fontTick, width - 10) : "⚠ 无数据", fontTick, BrRed,
            x, bandY + BandH + 8);
      }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
      base.OnPaint(e);
      var g = e.Graphics;
      g.SmoothingMode = SmoothingMode.AntiAlias;
      // 浮窗透明度与浅色底会让灰度抗锯齿显得发虚；ClearType 在像素网格上渲染，小字号更扎实。
      g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
      g.Clear(Bg);
      g.ScaleTransform(_scale, _scale); // 版面按 96 DPI 设计单位绘制，由这里统一放大

      foreach (var row in _refresh.Rows)
      {
        DrawBlockHead(g, row);
        var windows = row.Result.Windows;
        int path = Math.Max(0, Math.Min(row.Paths.Length, windows.Count) - 1);
        DrawWindows(g, windows, row.Paths[path], row.Y + GoBandOffset, row.Result.Error);
      }

      // ---- 关闭按钮 ----
      Pen cp = hoverClose ? PenCloseHot : PenCloseDim;
      g.DrawLine(cp, closeRect.Left + 3, closeRect.Top + 4, closeRect.Right - 3, closeRect.Bottom - 3);
      g.DrawLine(cp, closeRect.Right - 3, closeRect.Top + 4, closeRect.Left + 3, closeRect.Bottom - 3);

      // ---- 细边框 ----
      g.DrawRectangle(PenBorder, 0.5f, 0.5f, W - 1f, _designH - 1f);
    }

    static SolidBrush StatusBrush(double frac)
    {
      if (frac < 0.5) return BrGreen;
      if (frac < 0.8) return BrYellow;
      return BrRed;
    }

    string FormatRemaining(double resetMs)
    {
      long ms = (long)(resetMs - Program.NowUnixMs());
      if (ms <= 0) return "已重置";
      var t = TimeSpan.FromMilliseconds(ms);
      if (t.TotalDays >= 1) return (int)t.TotalDays + "天" + t.Hours + "h";
      return (int)t.TotalHours + "h" + t.Minutes.ToString("00") + "m";
    }
  }

  // Small JSON value tree: no reflection serializer or ASP.NET assembly initialization.
  // Keep number tokens intact when rewriting OAuth files, including unknown fields.
  static class Json
  {
    const int MaxDepth = 100, MaxLength = 2097152;
    public static object Parse(string text)
    {
      if (text == null || text.Length > MaxLength) throw new FormatException("JSON size limit");
      var reader = new Reader(text);
      object value = reader.Value(0);
      reader.Space();
      if (reader.Position != text.Length) throw new FormatException("Trailing JSON content");
      return value;
    }
    public static string Serialize(object value)
    {
      var text = new StringBuilder();
      Write(text, value, 0);
      if (text.Length > MaxLength) throw new FormatException("JSON size limit");
      return text.ToString();
    }
    static void Write(StringBuilder text, object value, int depth)
    {
      if (depth > MaxDepth) throw new FormatException("JSON depth limit");
      if (value == null) { text.Append("null"); return; }
      var number = value as Number;
      if (number != null) { text.Append(number.Token); return; }
      var str = value as string;
      if (str != null) { Quote(text, str); return; }
      if (value is bool) { text.Append((bool)value ? "true" : "false"); return; }
      var map = value as IDictionary<string, object>;
      if (map != null)
      {
        text.Append('{'); bool first = true;
        foreach (var entry in map)
        {
          if (!first) text.Append(','); first = false;
          Quote(text, entry.Key); text.Append(':'); Write(text, entry.Value, depth + 1);
        }
        text.Append('}'); return;
      }
      var list = value as IList;
      if (list != null)
      {
        text.Append('[');
        for (int i = 0; i < list.Count; i++) { if (i != 0) text.Append(','); Write(text, list[i], depth + 1); }
        text.Append(']'); return;
      }
      switch (Type.GetTypeCode(value.GetType()))
      {
        case TypeCode.Byte: case TypeCode.SByte: case TypeCode.Int16: case TypeCode.UInt16:
        case TypeCode.Int32: case TypeCode.UInt32: case TypeCode.Int64: case TypeCode.UInt64:
        case TypeCode.Decimal: case TypeCode.Double: case TypeCode.Single:
          string token = value is double ? ((double)value).ToString("R", CultureInfo.InvariantCulture)
            : value is float ? ((float)value).ToString("R", CultureInfo.InvariantCulture)
            : Convert.ToString(value, CultureInfo.InvariantCulture);
          if (token == "NaN" || token == "Infinity" || token == "-Infinity") throw new FormatException("Invalid JSON number");
          text.Append(token); return;
        default: throw new ArgumentException("Unsupported JSON value");
      }
    }
    static void Quote(StringBuilder text, string value)
    {
      if (value == null) throw new ArgumentException("Null JSON property name");
      text.Append('"');
      foreach (char c in value)
      {
        if (c == '"' || c == '\\') { text.Append('\\'); text.Append(c); }
        else if (c < 32 || char.IsSurrogate(c)) { text.Append("\\u"); text.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); }
        else text.Append(c);
      }
      text.Append('"');
    }
    sealed class Reader
    {
      readonly string text;
      public int Position;
      public Reader(string text) { this.text = text; }
      public void Space() { while (Position < text.Length && (text[Position] == ' ' || text[Position] == '\t' || text[Position] == '\r' || text[Position] == '\n')) Position++; }
      bool Take(char c) { if (Position < text.Length && text[Position] == c) { Position++; return true; } return false; }
      void Need(char c) { if (!Take(c)) throw new FormatException("Invalid JSON syntax"); }
      public object Value(int depth)
      {
        if (depth > MaxDepth) throw new FormatException("JSON depth limit");
        Space(); if (Position >= text.Length) throw new FormatException("Incomplete JSON");
        char c = text[Position];
        if (c == '"') return String();
        if (Take('{'))
        {
          var map = new Dictionary<string, object>(); Space(); if (Take('}')) return map;
          do { Space(); string key = String(); Space(); Need(':'); map[key] = Value(depth + 1); Space(); if (Take('}')) return map; Need(','); } while (true);
        }
        if (Take('['))
        {
          var list = new List<object>(); Space(); if (Take(']')) return list.ToArray();
          do { list.Add(Value(depth + 1)); Space(); if (Take(']')) return list.ToArray(); Need(','); } while (true);
        }
        if (c == 't') { Literal("true"); return true; }
        if (c == 'f') { Literal("false"); return false; }
        if (c == 'n') { Literal("null"); return null; }
        int start = Position; Take('-');
        if (!Take('0')) { if (Position >= text.Length || text[Position] < '1' || text[Position] > '9') throw new FormatException("Invalid JSON number"); Digits(); }
        if (Take('.')) { int digits = Position; Digits(); if (digits == Position) throw new FormatException("Invalid JSON fraction"); }
        if (Take('e') || Take('E')) { if (!Take('+')) Take('-'); int digits = Position; Digits(); if (digits == Position) throw new FormatException("Invalid JSON exponent"); }
        return new Number(text.Substring(start, Position - start));
      }
      void Digits() { while (Position < text.Length && text[Position] >= '0' && text[Position] <= '9') Position++; }
      void Literal(string value) { for (int i = 0; i < value.Length; i++) Need(value[i]); }
      string String()
      {
        Need('"'); var value = new StringBuilder();
        while (Position < text.Length)
        {
          char c = text[Position++];
          if (c == '"') return value.ToString();
          if (c < 32) throw new FormatException("Invalid JSON string");
          if (c != '\\') { value.Append(c); continue; }
          if (Position >= text.Length) throw new FormatException("Incomplete JSON escape");
          c = text[Position++];
          switch (c)
          {
            case '"': case '\\': case '/': value.Append(c); break;
            case 'b': value.Append('\b'); break; case 'f': value.Append('\f'); break;
            case 'n': value.Append('\n'); break; case 'r': value.Append('\r'); break; case 't': value.Append('\t'); break;
            case 'u':
              int code = 0;
              for (int i = 0; i < 4; i++)
              {
                if (Position >= text.Length) throw new FormatException("Incomplete JSON unicode escape");
                char hex = text[Position++];
                int n = hex >= '0' && hex <= '9' ? hex - '0' : hex >= 'a' && hex <= 'f' ? hex - 'a' + 10 : hex >= 'A' && hex <= 'F' ? hex - 'A' + 10 : -1;
                if (n < 0) throw new FormatException("Invalid JSON unicode escape");
                code = code * 16 + n;
              }
              value.Append((char)code); break;
            default: throw new FormatException("Invalid JSON escape");
          }
        }
        throw new FormatException("Unterminated JSON string");
      }
    }
    sealed class Number : IConvertible
    {
      public readonly string Token;
      public Number(string token) { Token = token; }
      decimal Decimal() { return decimal.Parse(Token, NumberStyles.Float, CultureInfo.InvariantCulture); }
      public override string ToString() { return Token; }
      public TypeCode GetTypeCode() { return TypeCode.Decimal; }
      public bool ToBoolean(IFormatProvider p) { return Convert.ToBoolean(Decimal()); }
      public byte ToByte(IFormatProvider p) { return Convert.ToByte(Decimal()); }
      public char ToChar(IFormatProvider p) { throw new InvalidCastException(); }
      public DateTime ToDateTime(IFormatProvider p) { throw new InvalidCastException(); }
      public decimal ToDecimal(IFormatProvider p) { return Decimal(); }
      public double ToDouble(IFormatProvider p) { return double.Parse(Token, NumberStyles.Float, CultureInfo.InvariantCulture); }
      public short ToInt16(IFormatProvider p) { return Convert.ToInt16(Decimal()); }
      public int ToInt32(IFormatProvider p) { return Convert.ToInt32(Decimal()); }
      public long ToInt64(IFormatProvider p) { return Convert.ToInt64(Decimal()); }
      public sbyte ToSByte(IFormatProvider p) { return Convert.ToSByte(Decimal()); }
      public float ToSingle(IFormatProvider p) { return float.Parse(Token, NumberStyles.Float, CultureInfo.InvariantCulture); }
      public string ToString(IFormatProvider p) { return Token; }
      public object ToType(Type type, IFormatProvider p) { if (type == typeof(string)) return Token; if (type == typeof(double)) return ToDouble(p); if (type == typeof(float)) return ToSingle(p); return Convert.ChangeType(Decimal(), type, p); }
      public ushort ToUInt16(IFormatProvider p) { return Convert.ToUInt16(Decimal()); }
      public uint ToUInt32(IFormatProvider p) { return Convert.ToUInt32(Decimal()); }
      public ulong ToUInt64(IFormatProvider p) { return Convert.ToUInt64(Decimal()); }
    }
  }
}
