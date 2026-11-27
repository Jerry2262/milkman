// Milkman — 入口、Windows 原生调用、配置、数据模型与无状态取数层
// 这一层不依赖任何窗口状态，可独立编译与验证。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using Microsoft.Win32;
using System.Drawing;
using System.Windows.Forms;

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

    // 数据目录。场景验收脚本用 MILKMAN_DATA_DIR 指向临时目录，从而完全不碰真实配置；
    // GetFolderPath 不读环境变量，只认系统已知文件夹，所以这里必须显式支持覆盖。
    public static string DataDir()
    {
      string overridden = Environment.GetEnvironmentVariable("MILKMAN_DATA_DIR");
      if (!string.IsNullOrEmpty(overridden)) return overridden;
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

  class OpenCodeAccountConfig
  {
    public string Name = "";
    public string Key = "";      // OpenCode Go API key（OpenCode 2 从 Console 获取，优先使用）
    public string AuthPath = ""; // OpenCode 1 的 auth.json 路径（Key 为空时回退）
  }

  class CommandCodeConfig
  {
    public string Name = "";
    public string Key = "";      // Studio → API keys 创建的 key（user_ 开头），CLI 与 Provider API 同一把
  }

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
        // 配置文件是给人读和手改的，用缩进格式；凭据文件仍用紧凑格式。
        File.WriteAllText(path, Json.SerializeIndented(ToDictionary()), new UTF8Encoding(false));
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
      // 顺序按「人改的频率」排：账户与密钥在前，程序自动维护的窗口状态在后
      return new Dictionary<string, object> {
        { "OpenCodeAccounts", accounts }, { "CommandCode", commandCode },
        { "ChatGptAuthPath", ChatGptAuthPath }, { "RefreshSeconds", RefreshSeconds },
        { "Docked", Docked }, { "Monitor", Monitor }, { "OffsetX", OffsetX }, { "OffsetY", OffsetY }
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
      // 无显式 label 时按窗口秒数生成（18000→5h, 604800→周, 整日→Nd, 整小时→Nh）
      if (label == null)
      {
        object seconds = Value(w, "limit_window_seconds");
        long sec = seconds != null ? Convert.ToInt64(seconds) : 0;
        if (sec == 18000) label = "5h";
        else if (sec == 604800) label = "周";
        else if (sec >= 86400 && sec % 86400 == 0) label = sec / 86400 + "d";
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
    // 紧凑输出：供凭据文件等程序自用场景，字符数最少
    public static string Serialize(object value)
    {
      var text = new StringBuilder();
      Write(text, value, 0, false);
      if (text.Length > MaxLength) throw new FormatException("JSON size limit");
      return text.ToString();
    }

    // 缩进输出：供人类阅读与手改的配置文??，两空格缩进、每个键独占一行
    public static string SerializeIndented(object value)
    {
      var text = new StringBuilder();
      Write(text, value, 0, true);
      if (text.Length > MaxLength) throw new FormatException("JSON size limit");
      return text.ToString();
    }
    static void Write(StringBuilder text, object value, int depth, bool indent)
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
        if (map.Count == 0) { text.Append("{}"); return; }
        text.Append('{');
        bool first = true;
        foreach (var entry in map)
        {
          if (!first) text.Append(',');
          first = false;
          NewLine(text, depth + 1, indent);
          Quote(text, entry.Key); text.Append(indent ? ": " : ":"); Write(text, entry.Value, depth + 1, indent);
        }
        NewLine(text, depth, indent);
        text.Append('}'); return;
      }
      var list = value as IList;
      if (list != null)
      {
        if (list.Count == 0) { text.Append("[]"); return; }
        text.Append('[');
        for (int i = 0; i < list.Count; i++)
        {
          if (i != 0) text.Append(',');
          NewLine(text, depth + 1, indent);
          Write(text, list[i], depth + 1, indent);
        }
        NewLine(text, depth, indent);
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
    static void NewLine(StringBuilder text, int depth, bool indent)
    {
      if (!indent) return;
      text.Append('\n');
      text.Append(' ', depth * 2);
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
