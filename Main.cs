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
      NativeMethods.SetProcessDPIAware();
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
    public static extern bool SetProcessDPIAware();

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
    public int X = -1, Y = -1;
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
      try
      {
        Directory.CreateDirectory(Program.DataDir());
        File.WriteAllText(Path.Combine(Program.DataDir(), "config.json"),
          Json.Serialize(ToDictionary()), new UTF8Encoding(false));
        return true;
      }
      catch { return false; }
    }

    public static Config Parse(string json)
    {
      var d = ConfigObject(Json.Parse(json));
      var cfg = new Config();
      object value;
      if (d.TryGetValue("X", out value) && value != null) cfg.X = Convert.ToInt32(value, CultureInfo.InvariantCulture);
      if (d.TryGetValue("Y", out value) && value != null) cfg.Y = Convert.ToInt32(value, CultureInfo.InvariantCulture);
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
        { "X", X }, { "Y", Y }, { "Docked", Docked }, { "RefreshSeconds", RefreshSeconds },
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

    // 字体层次（参照 DeepSeekTimeline）
    readonly Font fontBig, fontMid, fontTick;
    readonly Rectangle closeRect = new Rectangle(W - 26, 7, 15, 15);
    bool hoverClose;
    SourceRow _hoverAction;

    // 吸附与动画（参照 DeepSeekTimeline：WM_NCHITTEST 拖动 + 顶部吸附 + 缓出）
    const int WM_NCHITTEST = 0x84, HTCLIENT = 1, HTCAPTION = 2;
    const int WM_ENTERSIZEMOVE = 0x231, WM_EXITSIZEMOVE = 0x232;
    const int WM_WINDOWPOSCHANGED = 0x47, WM_STYLECHANGED = 0x7D;
    const int GWL_EXSTYLE = -20, WS_EX_TOPMOST = 0x00000008;
    const int WM_SYSCOMMAND = 0x112, SC_MAXIMIZE = 0xF030;
    const int WM_NCLBUTTONDBLCLK = 0xA3;
    const int Peek = 5; // 吸附收起时顶部保留的提示条高度(px), 与 DeepSeekTimeline 一致
    const int DockIdleMs = 100, DockAnimationMs = 30;
    bool docked, expanded, dragging, pinned; // pinned = 双击钉住, DockTick 不自动收放
    Rectangle monRect;
    Timer _dockTimer;

    Config _cfg;
    DataFetcher _fetcher;
    readonly RefreshSession _refresh;
    Timer _refreshTimer = new Timer();
    Timer _clockTimer = new Timer();
    int _formH;

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
        if (!Started) return row == Rows[0] ? "获取全部额度" : null;
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
      _formH = 16 + _refresh.Rows.Count * GoRowH;

      fontBig = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
      fontMid = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
      fontTick = new Font("Microsoft YaHei UI", 7.5f, FontStyle.Regular);
      Font = fontMid;

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
      Size = new Size(W, _formH);
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
        if (!_cfg.Docked) monRect = Screen.FromControl(this).Bounds;
        if (_cfg.Docked)
        {
          docked = true;
          expanded = false;
          pinned = false;
          Location = new Point(Location.X, monRect.Top - _formH + Peek);
          _dockTimer.Start();
        }
      };

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
      if (!docked || expanded || Top > monRect.Top - _formH + Peek) Invalidate();
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
        monRect = Screen.FromControl(this).Bounds;
        if (docked)
        {
          if (Top > monRect.Top + 30) { docked = false; pinned = false; _dockTimer.Stop(); } // 下拉脱离
        }
        else if (Top <= monRect.Top + 8)
        {
          docked = true;                              // 拖到顶 = 吸附
          expanded = true;
          Invalidate();
          _dockTimer.Start();
        }
        SaveState();
        base.WndProc(ref m);
        return;
      }
      if (m.Msg == WM_NCHITTEST)
      {
        int lp = (int)m.LParam;
        int sx = lp & 0xFFFF; if (sx >= 0x8000) sx -= 0x10000;
        int sy = (lp >> 16) & 0xFFFF; if (sy >= 0x8000) sy -= 0x10000;
        Point pt = PointToClient(new Point(sx, sy));
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
      if (closeRect.Contains(e.Location)) { Close(); return; } // ✕ = 退出程序
      var row = ActionHit(e.Location);
      if (row != null && _refresh.Click(row))
      {
        _hoverAction = null;
        Cursor = Cursors.Default;
        Invalidate();
      }
    }

    void OnMouseMoveHook(object sender, MouseEventArgs e)
    {
      bool h = closeRect.Contains(e.Location);
      var row = ActionHit(e.Location);
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

    void RestoreLocation()
    {
      Point saved = new Point(_cfg.X, _cfg.Y);
      Screen screen = Screen.PrimaryScreen;
      if (saved == new Point(-1, -1))
      {
        Rectangle area = screen.WorkingArea;
        saved = new Point(area.Right - W - 10, area.Bottom - _formH - 40);
      }
      else
      {
        var window = new Rectangle(saved, Size);
        screen = Screen.FromRectangle(window);
        if (_cfg.Docked)
        {
          Screen[] screens = Screen.AllScreens;
          int index = DockMonitorIndex(window, Array.ConvertAll(screens, s => s.Bounds));
          if (index >= 0) screen = screens[index];
        }
      }
      monRect = screen.Bounds;
      Rectangle target = _cfg.Docked ? monRect : screen.WorkingArea;
      Location = new Point(Math.Max(target.Left, Math.Min(saved.X, target.Right - Width)),
        _cfg.Docked ? target.Top : Math.Max(target.Top, Math.Min(saved.Y, target.Bottom - Height)));
    }

    static int DockMonitorIndex(Rectangle window, Rectangle[] screens)
    {
      // 新配置保存屏幕顶边；旧配置可能保存的是收起时的位置。
      int index = -1, bestOverlap = 0;
      bool exact = false;
      for (int i = 0; i < screens.Length; i++)
      {
        Rectangle screen = screens[i];
        long aboveTop = (long)screen.Top - window.Top;
        if (aboveTop < 0 || aboveTop > window.Height - Peek) continue;
        int overlap = Math.Min(screen.Right, window.Right) - Math.Max(screen.Left, window.Left);
        bool onTop = aboveTop == 0;
        if (overlap <= 0 || (exact && !onTop) || (exact == onTop && overlap <= bestOverlap)) continue;
        index = i;
        bestOverlap = overlap;
        exact = onTop;
      }
      return index;
    }

    void SaveState()
    {
      int x = Left, y = docked ? monRect.Top : Top;
      if (_cfg.X == x && _cfg.Y == y && _cfg.Docked == docked) return;
      var cfg = Config.Load(); // 只在位置变化时重读，保留运行期间手改的账户配置
      cfg.X = x; cfg.Y = y; cfg.Docked = docked;
      if (cfg.Save()) { _cfg.X = x; _cfg.Y = y; _cfg.Docked = docked; }
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
      int target = expanded ? monRect.Top : monRect.Top - _formH + Peek;
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
    float Wd(Graphics g, string s, Font f) { return g.MeasureString(s, f).Width; }

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

    // 按像素宽度截断，超出时以省略号收尾
    static string Fit(Graphics g, string text, Font font, float max)
    {
      if (g.MeasureString(text, font).Width <= max) return text;
      int low = 0, high = text.Length;
      while (low < high)
      {
        int mid = (low + high + 1) / 2;
        if (g.MeasureString(text.Substring(0, mid) + "…", font).Width <= max) low = mid;
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
      g.DrawString(title, fontBig, BrText, 40, rowY - 1);
      if (!string.IsNullOrEmpty(status))
      {
        SolidBrush brush = error && !busy ? BrRed : busy ? BrSub : BrFaint;
        int right = action != null ? ActionRect(row).Left - 8 : W - 20;
        float room = right - 40 - Wd(g, title, fontBig) - 8;
        // 放不下的错误长文退回落款，完整原因由空数据行承担，避免标题行出现半截文字
        if (error && !busy && Wd(g, status, fontMid) > room) status = "无法获取信息";
        g.DrawString(status, fontMid, brush, right - Wd(g, status, fontMid), rowY + 3);
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
        g.DrawString(action, fontMid, hot ? BrBg : BrText,
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
      g.DrawString(left, fontTick, BrText, x, y + 2);
      if (right.Length > 0)
        g.DrawString(right, fontTick, BrSub, x + w - Wd(g, right, fontTick), y + 2);
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
          g.DrawString("—", fontTick, BrSub, x, bandY + BandH + 8);
        else
          // 首段显示具体原因，其余段保持占位文案
          g.DrawString(i == 0 ? "⚠ " + Fit(g, error, fontTick, width - 10) : "⚠ 无数据", fontTick, BrRed,
            x, bandY + BandH + 8);
      }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
      base.OnPaint(e);
      var g = e.Graphics;
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
      g.Clear(Bg);

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
      g.DrawRectangle(PenBorder, 0.5f, 0.5f, W - 1f, ClientSize.Height - 1f);
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
