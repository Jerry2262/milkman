// Milkman — 榨取/剥削 ChatGPT Plus·OpenCode Go 等 coding plan 额度的用量浮窗
// 参照 DeepSeekTimeline 的单文件 WinForms 模式：csc 编译，无 VS 工程。
// 数据源(均为 HTTP, 无本地依赖):
//   ChatGPT/Codex:  %USERPROFILE%\.codex\auth.json 的 OAuth token -> chatgpt.com/backend-api/wham/usage（非官方）
//   OpenCode Go:    opencode-go.key -> https://opencode.ai/zen/go/v1/usage（官方账户级消费百分比）
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Milkman
{
  static class Program
  {
    [STAThread]
    static void Main()
    {
      // .NET Framework 4.x 默认不协商 TLS1.2+，而 chatgpt.com 已不支持旧协议
      try { System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12; } catch { }
      NativeMethods.SetProcessDPIAware();
      Application.EnableVisualStyles();
      Application.SetCompatibleTextRenderingDefault(false);
      AutoStart.Set(Config.Load().AutoStart); // 开机自启（config.json 的 AutoStart=false 可关）
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
  }

  // =======================================================================
  // 配置缓存: %LOCALAPPDATA%\Milkman\config.json
  // =======================================================================
  class Config
  {
    public int X = -1, Y = -1;
    public bool Docked = false;
    public bool AutoStart = true;
    public int RefreshSeconds = 60;
    public string OpenCodeAuthPath = ""; // 空 = 自动探测(Win 本地 -> WSL)

    public static Config Load()
    {
      var cfg = new Config();
      try
      {
        var path = Path.Combine(Program.DataDir(), "config.json");
        if (File.Exists(path))
        {
          var d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
          if (d == null) return cfg;
          cfg.X = GetInt(d, "X", -1);
          cfg.Y = GetInt(d, "Y", -1);
          cfg.Docked = GetBool(d, "Docked", false);
          cfg.AutoStart = GetBool(d, "AutoStart", true);
          cfg.RefreshSeconds = GetInt(d, "RefreshSeconds", 60);
          cfg.OpenCodeAuthPath = GetStr(d, "OpenCodeAuthPath", "");
        }
      }
      catch { }
      return cfg;
    }

    public void Save()
    {
      try
      {
        Directory.CreateDirectory(Program.DataDir());
        var d = new Dictionary<string, object>
        {
          { "X", X }, { "Y", Y }, { "Docked", Docked },
          { "RefreshSeconds", RefreshSeconds }
        };
        File.WriteAllText(Path.Combine(Program.DataDir(), "config.json"),
          new JavaScriptSerializer().Serialize(d), new UTF8Encoding(false));
      }
      catch { }
    }

    static int GetInt(Dictionary<string, object> d, string k, int def)
    {
      object v;
      return d.TryGetValue(k, out v) && v != null ? Convert.ToInt32(v) : def;
    }
    static bool GetBool(Dictionary<string, object> d, string k, bool def)
    {
      object v;
      if (!d.TryGetValue(k, out v) || v == null) return def;
      return Convert.ToBoolean(v);
    }
    static string GetStr(Dictionary<string, object> d, string k, string def)
    {
      object v;
      return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : def;
    }
  }

  // =======================================================================
  // 数据模型
  // =======================================================================
  class UsageWindow { public string Label; public double UsedPct; public double ResetMs; }

  class Snapshot
  {
    public List<UsageWindow> GptWindows = new List<UsageWindow>();
    public string GptPlanType;
    public string GptError;
    public UsageWindow GoSession, GoWeek, GoMonth;
    public string GoError;
    public DateTime LastSuccess;
  }

  // =======================================================================
  // DataFetcher: 拉取两个数据源（每次全量刷新，失败各自灰显不相互影响）
  // =======================================================================
  class DataFetcher
  {
    Config _cfg;
    public Snapshot Snap = new Snapshot();
    public event EventHandler Updated;

    const string WhamUrl = "https://chatgpt.com/backend-api/wham/usage";
    const string TokenUrl = "https://auth.openai.com/oauth/token";
    const string CodexClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    const string GoUsageUrl = "https://opencode.ai/zen/go/v1/usage";
    // Cloudflare 1010: opencode.ai 按浏览器签名拦截, 需浏览器 UA
    const string BrowserUA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
    static readonly JavaScriptSerializer Js = new JavaScriptSerializer();

    public DataFetcher(Config cfg) { _cfg = cfg; }

    public void Refresh()
    {
      var snap = new Snapshot();
      try { FetchGpt(snap); } catch (Exception e) { snap.GptError = Short(e.Message); }
      try { FetchGo(snap); } catch (Exception e) { snap.GoError = Short(e.Message); }
      if (snap.GptWindows.Count > 0 || snap.GptPlanType != null || snap.GoMonth != null)
        snap.LastSuccess = DateTime.Now;
      else
        snap.LastSuccess = Snap.LastSuccess; // 保留上次成功时间
      Snap = snap;
      var h = Updated; if (h != null) h(this, EventArgs.Empty);
    }

    static string Short(string s) { return s.Length > 80 ? s.Substring(0, 80) + "…" : s; }

    // ---------------- ChatGPT ----------------
    void FetchGpt(Snapshot snap)
    {
      string authPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
      string access, refreshToken;
      var dict = Js.DeserializeObject(File.ReadAllText(authPath)) as Dictionary<string, object>;
      var tokens = dict != null ? dict["tokens"] as Dictionary<string, object> : null;
      access = tokens != null ? Convert.ToString(tokens["access_token"]) : "";
      refreshToken = tokens != null ? Convert.ToString(tokens["refresh_token"]) : "";
      if (string.IsNullOrEmpty(access))
        throw new Exception("codex auth.json 无 access_token");

      var resp = HttpGet(WhamUrl, access);
      if (resp == null)
      {
        string newAccess = RefreshAccessToken(refreshToken);
        try
        {
          tokens["access_token"] = newAccess;
          dict["last_refresh"] = Program.NowUnixMs() / 1000;
          File.WriteAllText(authPath, Js.Serialize(dict), new UTF8Encoding(false));
        }
        catch { /* 写回失败仅内存持有 */ }
        access = newAccess;
        resp = HttpGet(WhamUrl, access);
        if (resp == null) throw new Exception("wham/usage 刷新后仍拒绝");
      }
      ParseWham(snap, resp);
    }

    string RefreshAccessToken(string refreshToken)
    {
      var body = "client_id=" + CodexClientId + "&grant_type=refresh_token&refresh_token=" + Uri.EscapeDataString(refreshToken);
      var resp = HttpPostForm(TokenUrl, body);
      var d = Js.DeserializeObject(resp) as Dictionary<string, object>;
      // mac 版参考: 刷新可能返回 refresh_token_expired / reused / invalidated 等错误码
      if (d != null && d["error"] != null)
        throw new Exception("token 刷新失败: " + Convert.ToString(d["error"]));
      var at = d != null ? Convert.ToString(d["access_token"]) : "";
      if (string.IsNullOrEmpty(at)) throw new Exception("token 刷新失败");
      return at;
    }

    void ParseWham(Snapshot snap, string resp)
    {
      var d = Js.DeserializeObject(resp) as Dictionary<string, object>;
      if (d == null) throw new Exception("wham 响应无法解析");
      snap.GptPlanType = Convert.ToString(d["plan_type"] ?? "");

      var rl = d["rate_limit"] as Dictionary<string, object>;
      if (rl != null)
      {
        // 实际响应里 primary/secondary 的秒数因账号而异（plus 当前=604800 周窗口），标签按秒数动态生成
        AddWindow(snap, rl, "primary_window", null);
        AddWindow(snap, rl, "secondary_window", null);
      }
      // code_review_rate_limit 是独立周限，可选展示
      var crl = d["code_review_rate_limit"] as Dictionary<string, object>;
      if (crl != null) AddWindowFromDict(snap, crl, "代码审查");
    }

    void AddWindow(Snapshot snap, Dictionary<string, object> rl, string key, string label)
    {
      AddWindowFromDict(snap, rl[key] as Dictionary<string, object>, label);
    }

    void AddWindowFromDict(Snapshot snap, Dictionary<string, object> w, string label)
    {
      if (w == null) return;
      var win = new UsageWindow();
      // 无显式 label 时按窗口秒数生成（18000→5h, 604800 或其他周四→周, 其余按天/小时）
      if (label == null)
      {
        long sec = w["limit_window_seconds"] != null ? Convert.ToInt64(w["limit_window_seconds"]) : 0;
        if (sec == 18000) label = "5h";
        else if (sec == 604800) label = "周";
        else if (sec >= 86400 && sec % 86400 == 0) label = "N 天";
        else if (sec >= 3600 && sec % 3600 == 0) label = sec / 3600 + "h";
        else label = (sec / 3600.0).ToString("F1") + "h";
      }
      win.Label = label;
      win.UsedPct = Convert.ToDouble(w["used_percent"] ?? 0) / 100.0;
      double resetMs = 0;
      if (w["reset_at"] != null) resetMs = Convert.ToDouble(w["reset_at"]) * 1000;
      else if (w["resets_at_time"] != null) resetMs = Convert.ToDouble(w["resets_at_time"]) * 1000;
      else if (w["reset_after_seconds"] != null)
        resetMs = Program.NowUnixMs() + Convert.ToDouble(w["reset_after_seconds"]) * 1000;
      win.ResetMs = resetMs;
      snap.GptWindows.Add(win);
    }

    // ---------------- OpenCode Go (参照 macOS openusage 实现) ----------------
    // 官方端点: GET https://opencode.ai/zen/go/v1/usage, Bearer = auth.json 中 opencode-go.key
    // 返回 { usage: { rolling, weekly, monthly } }, 各含 percent(0-100) + resetsAt(ISO8601),
    // 账户级服务端计费(与官方 dashboard 一致, 其他机器/工具消费同样计入)
    void FetchGo(Snapshot snap)
    {
      string authPath = FindOpenCodeAuthPath();
      if (authPath == null) { snap.GoError = "未找到 opencode auth.json"; return; }
      var d = Js.DeserializeObject(File.ReadAllText(authPath)) as Dictionary<string, object>;
      var goe = d != null ? d["opencode-go"] as Dictionary<string, object> : null;
      string key = goe != null ? Convert.ToString(goe["key"] ?? "") : "";
      key = key.Trim();
      if (key.Length == 0) { snap.GoError = "未登录 OpenCode Go"; return; }

      string body; int status;
      HttpGetWithStatus(GoUsageUrl, key, out body, out status);
      if (status == 401) throw new Exception("Go key 被拒绝");
      if (status == 403) throw new Exception("该 key 无 Go 订阅");
      if (status < 200 || status >= 300) throw new Exception("Go usage HTTP " + status);
      var u = Js.DeserializeObject(body) as Dictionary<string, object>;
      var usage = u != null ? u["usage"] as Dictionary<string, object> : null;
      if (usage == null) throw new Exception("Go usage 响应无法解析");
      snap.GoSession = ParseGoWindow(usage, "rolling", "5h");
      snap.GoWeek = ParseGoWindow(usage, "weekly", "周");
      snap.GoMonth = ParseGoWindow(usage, "monthly", "月");
    }

    UsageWindow ParseGoWindow(Dictionary<string, object> usage, string key, string label)
    {
      var w = usage[key] as Dictionary<string, object>;
      if (w == null) return null;
      if (w["percent"] == null) throw new Exception("Go " + key + " 无 percent");
      var g = new UsageWindow { Label = label, UsedPct = Convert.ToDouble(w["percent"]) / 100.0, ResetMs = 0 };
      string resetsAt = Convert.ToString(w["resetsAt"] ?? "");
      DateTimeOffset t;
      if (DateTimeOffset.TryParse(resetsAt, out t))
        g.ResetMs = (long)((t.UtcDateTime - new DateTime(1970, 1, 1)).TotalMilliseconds);
      return g;
    }

    // auth.json 路径: 先 Windows 本地, 再尝试 WSL 侧固定路径; 均由 config.OpenCodeAuthPath 可覆盖
    string FindOpenCodeAuthPath()
    {
      if (!string.IsNullOrEmpty(_cfg.OpenCodeAuthPath) && File.Exists(_cfg.OpenCodeAuthPath))
        return _cfg.OpenCodeAuthPath;
      string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "opencode", "auth.json");
      if (File.Exists(local)) return local;
      string wsl = Path.Combine(@"\\wsl.localhost\Ubuntu-24.04", "home", "jerry",
        ".local", "share", "opencode", "auth.json");
      if (File.Exists(wsl)) return wsl;
      return null;
    }

    // ---------------- HTTP ----------------
    // 返回响应体；401/403 -> null；网络错误抛异常
    static string HttpGet(string url, string bearer)
    {
      string body; int status;
      HttpGetWithStatus(url, bearer, out body, out status);
      if (status == 401 || status == 403) return null;
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

    // 与 HttpGet 相同但保留状态码与响应体(错误分类用); 网络错误抛异常
    static void HttpGetWithStatus(string url, string bearer, out string body, out int status)
    {
      var req = CreateRequest(url, "GET", bearer);
      try
      {
        using (var resp = (HttpWebResponse)req.GetResponse())
        using (var s = resp.GetResponseStream())
        using (var r = new StreamReader(s))
        {
          body = r.ReadToEnd();
          status = (int)resp.StatusCode;
        }
      }
      catch (WebException e)
      {
        var resp = e.Response as HttpWebResponse;
        if (resp != null)
        {
          status = (int)resp.StatusCode;
          using (var s = resp.GetResponseStream())
          using (var r = new StreamReader(s))
            body = r.ReadToEnd();
          return;
        }
        throw new Exception("HTTP " + e.Message);
      }
    }

    static string HttpPostForm(string url, string body)
    {
      var req = CreateRequest(url, "POST", null);
      req.ContentType = "application/x-www-form-urlencoded";
      var bytes = Encoding.UTF8.GetBytes(body);
      req.ContentLength = bytes.Length;
      using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
      using (var resp = (HttpWebResponse)req.GetResponse())
      using (var s = resp.GetResponseStream())
      using (var r = new StreamReader(s))
        return r.ReadToEnd();
    }
  }

  // =======================================================================
  // 开机自启: HKCU Run
  // =======================================================================
  static class AutoStart
  {
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Milkman";

    public static void Set(bool on)
    {
      try
      {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
          if (on) k.SetValue(Name, "\"" + Application.ExecutablePath + "\"");
          else k.DeleteValue(Name, false);
        }
      }
      catch { }
    }
  }

  // =======================================================================
  // 浮窗 UI：无边框、置顶、可拖动、双击切换详情、托盘
  // =======================================================================
  class MilkmanForm : Form
  {
    // 浅色系（参照 DeepSeekTimeline）
    const int W = 520; // DeepSeek 同宽
    static readonly Color Bg = Color.FromArgb(247, 247, 248);
    static readonly Color TextMain = Color.FromArgb(40, 40, 46);
    static readonly Color TextSub = Color.FromArgb(110, 112, 119);
    static readonly Color TextFaint = Color.FromArgb(154, 156, 163);
    static readonly Color Accent = Color.FromArgb(198, 111, 100);
    static readonly Color TrackCol = Color.FromArgb(226, 226, 231);
    static readonly Color BarGreen = Color.FromArgb(46, 204, 113);
    static readonly Color BarYellow = Color.FromArgb(241, 196, 15);
    static readonly Color BarRed = Color.FromArgb(231, 76, 60);
    static readonly SolidBrush BrText, BrSub, BrFaint, BrAccent, BrTrack, BrGreen, BrYellow, BrRed;
    static readonly Pen PenCloseDim = new Pen(Color.FromArgb(165, 167, 173), 1.6f);
    static readonly Pen PenCloseHot = new Pen(Color.FromArgb(210, 96, 86), 1.6f);
    static readonly Pen PenBorder = new Pen(Color.FromArgb(224, 224, 229));
    static readonly Pen PenBarBorder = new Pen(Color.FromArgb(208, 208, 216));
    static readonly Dictionary<string, float> MeasureCache = new Dictionary<string, float>();

    static MilkmanForm()
    {
      BrText = new SolidBrush(TextMain);
      BrSub = new SolidBrush(TextSub);
      BrFaint = new SolidBrush(TextFaint);
      BrAccent = new SolidBrush(Accent);
      BrTrack = new SolidBrush(TrackCol);
      BrGreen = new SolidBrush(BarGreen);
      BrYellow = new SolidBrush(BarYellow);
      BrRed = new SolidBrush(BarRed);
      BrAccentOff = new SolidBrush(AccentOff);
      BandPathGpt = RoundedRect(new RectangleF(20, BandTop1, BoxW, BandH), 5f);
      for (int i = 0; i < 3; i++)
        BandPathGo[i] = RoundedRect(new RectangleF(GoSegX[i], BandTop2, SegW, BandH), 5f);
    }

    // 字体层次（参照 DeepSeekTimeline）
    readonly Font fontBig, fontMid, fontSub, fontTick;
    readonly Rectangle closeRect = new Rectangle(W - 26, 7, 15, 15);
    bool hoverClose;

    // 吸附与动画（参照 DeepSeekTimeline：WM_NCHITTEST 拖动 + 顶部吸附 + 缓出）
    const int WM_NCHITTEST = 0x84, HTCLIENT = 1, HTCAPTION = 2;
    const int WM_ENTERSIZEMOVE = 0x231, WM_EXITSIZEMOVE = 0x232;
    const int WM_SYSCOMMAND = 0x112, SC_MAXIMIZE = 0xF030;
    const int WM_NCLBUTTONDBLCLK = 0xA3;
    const int Peek = 4; // 吸附收起时顶部保留的提示条高度(px, 用户微调: 5px 偏粗/3px 偏细)
    bool docked, expanded, dragging, pinned; // pinned = 双击钉住, DockTick 不自动收放
    Rectangle monRect;
    Timer _dockTimer;

    Config _cfg;
    DataFetcher _fetcher;
    Snapshot _snap = new Snapshot();
    Timer _refreshTimer = new Timer();
    Timer _clockTimer = new Timer();
    bool _busy;
    const int H = 160; // 双块 + 每条一体元信息(左百分比/右重置)

    public MilkmanForm()
    {
      _cfg = Config.Load();
      _fetcher = new DataFetcher(_cfg);

      fontBig = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
      fontMid = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
      fontSub = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
      fontTick = new Font("Microsoft YaHei UI", 7.5f, FontStyle.Regular);
      Font = fontMid;

      FormBorderStyle = FormBorderStyle.None;
      TopMost = true;
      ShowInTaskbar = false;
      StartPosition = FormStartPosition.Manual;
      MaximizeBox = false;
      MinimizeBox = false;
      Opacity = 0.80; // 与 DeepSeek 一致
      SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
        | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
      Size = new Size(W, H);
      // 参照 DeepSeek: 恢复吸附时先按 X clamp 到虚拟屏并设 y=0(屏内), 使 OnLoad 的 Screen.FromControl 能判对屏
      if (_cfg.Docked && _cfg.X >= 0)
      {
        Rectangle vs = SystemInformation.VirtualScreen;
        int x = Math.Max(vs.Left, Math.Min(_cfg.X, vs.Right - W));
        Location = new Point(x, 0);
      }
      else
        Location = (_cfg.X >= 0 && _cfg.Y >= 0)
          ? new Point(_cfg.X, _cfg.Y)
          : new Point(Screen.PrimaryScreen.WorkingArea.Right - W - 10, Screen.PrimaryScreen.WorkingArea.Bottom - H - 40);


      MouseMove += OnMouseMoveHook;
      MouseLeave += OnMouseLeaveHook;
      MouseDown += OnMouseDownHook;
      Shown += OnShownHook;
      FormClosing += (s, e) => SaveState();

      _dockTimer = new Timer();
      _dockTimer.Interval = 30;
      _dockTimer.Tick += (s, e) => DockTick();

      Load += (s, e) =>
      {
        monRect = Screen.FromControl(this).Bounds;
        if (_cfg.Docked)
        {
          docked = true;
          expanded = false;
          pinned = false;
          Location = new Point(Location.X, monRect.Top - H + Peek);
          _dockTimer.Start();
        }
      };

      _refreshTimer.Interval = _cfg.RefreshSeconds * 1000;
      _refreshTimer.Tick += (s, e) => RefreshAsync();
      _clockTimer.Interval = 5000; // 与 DeepSeek 一致: 5s 周期重绘剩余时间
      _clockTimer.Tick += (s, e) =>
      {
        // 完全收起时可见区只有 5px 静态边条, 跳过无效重绘
        if (docked && !expanded && Top <= monRect.Top - H + Peek) return;
        Invalidate();
      };

      _fetcher.Updated += (s, e) =>
      {
        try
        {
          BeginInvoke((Action)(() =>
          {
            _snap = _fetcher.Snap;
            _busy = false;
            // 防封号策略: 获取失败即停止自动刷新（用户需开代理后单击重试），成功则恢复
            if (_snap.GptWindows.Count == 0 && _snap.GptError != null)
              _refreshTimer.Stop();
            else
              _refreshTimer.Start();
            Invalidate();
          }));
        }
        catch { }
      };

      RefreshAsync();
      _refreshTimer.Start();
      _clockTimer.Start();
    }

    void RefreshAsync()
    {
      if (_busy) return;
      _busy = true;
      Task.Run(() => { try { _fetcher.Refresh(); } catch { _busy = false; } });
    }

    // ---------------- 系统级拖动 + 顶部吸附（参照 DeepSeekTimeline） ----------------
    // 不抢焦点 + 不占 Alt-Tab；拖动由 WM_NCHITTEST 交给系统（HTCAPTION 原生拖动）
    protected override CreateParams CreateParams
    {
      get
      {
        CreateParams cp = base.CreateParams;
        cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
        return cp;
      }
    }

    protected override void WndProc(ref Message m)
    {
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
        m.Result = (IntPtr)(closeRect.Contains(pt) ? HTCLIENT : HTCAPTION);
        return;
      }
      // 拦截"标题栏双击": 吸附(docked)时切换 Pin(钉住), 非吸附时双击无效果
      // pin = 保持当前展开/收起状态不动, 鼠标离开不自动收起; 再双击 unpin 恢复自动收放
      if (m.Msg == WM_NCLBUTTONDBLCLK)
      {
        if (docked)
        {
          pinned = !pinned;
          if (pinned) { expanded = true; } // 钉住时保持展开以明确"正在使用"
          SaveState();
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
      if (closeRect.Contains(e.Location)) { Close(); return; } // ✕ = 退出程序
      RefreshAsync(); // 单击 = 手动刷新（失败暂停后靠它恢复）
    }

    void OnMouseMoveHook(object sender, MouseEventArgs e)
    {
      bool h = closeRect.Contains(e.Location);
      if (h != hoverClose)
      {
        hoverClose = h;
        Cursor = h ? Cursors.Hand : Cursors.Default;
        Invalidate();
      }
    }

    void OnMouseLeaveHook(object sender, EventArgs e)
    {
      if (hoverClose) { hoverClose = false; Cursor = Cursors.Default; Invalidate(); }
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
    }

    void SaveState()
    {
      var cfg = Config.Load();
      cfg.X = Location.X;
      cfg.Y = Location.Y;
            cfg.Docked = docked;
      cfg.Save();
    }

    // 吸附模式核心: 鼠标感应 + 缓出滑动动画
    void DockTick()
    {
      if (!docked || dragging || pinned) return; // pinned 时冻结自动收放
      Point mp = Cursor.Position;
      Rectangle wr = new Rectangle(Location, Size);
      bool nearTop = mp.X >= wr.Left - 80 && mp.X <= wr.Right + 80
                  && mp.Y >= monRect.Top - 4 && mp.Y <= monRect.Top + 6;
      bool inside = mp.X >= wr.Left - 20 && mp.X <= wr.Right + 20
                 && mp.Y >= wr.Top - 20 && mp.Y <= wr.Bottom + 20;
      if (nearTop || inside)
      {
        if (!expanded && !ForegroundFullscreen())
        {
          expanded = true;
          Invalidate(); // 展开瞬间立即按最新数据重绘
        }
      }
      else if (expanded)
      {
        expanded = false; // 离开立即收起（与 DeepSeek 一致, 零延迟）
      }
      int target = expanded ? monRect.Top : monRect.Top - H + Peek;
      if (Top != target)
      {
        int dy = target - Top;
        int step = dy / 3;
        if (step == 0) step = dy > 0 ? 1 : -1;
        NativeMethods.SetWindowPos(Handle, IntPtr.Zero, Left, Top + step, 0, 0,
          0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE
      }
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
    // 文本宽度缓存：帧间相同字符串零测量开销（参照 DeepSeekTimeline）
    float Wd(Graphics g, string s, Font f)
    {
      float w;
      if (!MeasureCache.TryGetValue(s + "|" + f.Size + (f.Bold ? "b" : ""), out w))
      {
        w = g.MeasureString(s, f).Width;
        MeasureCache[s + "|" + f.Size + (f.Bold ? "b" : "")] = w;
      }
      return w;
    }

    // 版式完全参照 DeepSeekTimeline: 顶部 provider 名, 中部圆角色带(独立圆角条/留间隙),
    // 每条下方 两行元信息: 百分比 + 重置时间
    const int BandH = 12, BandTop1 = 40, BandTop2 = 112;
    const float BoxW = W - 40f, Gap = 6f, SegW = (BoxW - 2 * Gap) / 3f;
    static readonly Color AccentOff = Color.FromArgb(88, 143, 124);
    static readonly SolidBrush BrAccentOff;
    // 用量条圆角轮廓(固定几何, 静态缓存): ChatGPT 整条一段; Go 三段各独立圆角
    static readonly GraphicsPath BandPathGpt;
    static readonly GraphicsPath[] BandPathGo = new GraphicsPath[3];
    static readonly float[] GoSegX = new float[] { 20, 20 + SegW + Gap, 20 + 2 * (SegW + Gap) };

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

    // 区块头: 仅圆点 + provider 名; 出错时右侧红字
    void DrawBlockHead(Graphics g, int rowY, SolidBrush dot, string s1, string s2Error, bool error)
    {
      g.FillEllipse(dot, 22, rowY + 7, 9, 9);
      g.DrawString(s1, fontBig, BrText, 40, rowY - 1);
      if (error && s2Error != null)
        g.DrawString(s2Error, fontSub, BrRed, W - 20 - Wd(g, s2Error, fontSub), rowY + 3);
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

    protected override void OnPaint(PaintEventArgs e)
    {
      base.OnPaint(e);
      var g = e.Graphics;
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
      g.Clear(Bg);

      // ---- 更新时间: 右上角闲置区 (✕ 左侧右对齐, 顶部紧凑) ----
      string pr = _snap.LastSuccess == default(DateTime) ? "" : "更新于 " + _snap.LastSuccess.ToString("HH:mm:ss");
      if (pr.Length > 0)
        g.DrawString(pr, fontTick, BrFaint, W - 28 - Wd(g, pr, fontTick), 15);

      // ---- 块1: ChatGPT Plus (单窗口 = 整条) ----
      var w = _snap.GptWindows.Count > 0 ? _snap.GptWindows[0] : null;
      DrawBlockHead(g, 12, BrAccent, "ChatGPT Plus", "无法获取信息 · 单击重试", w == null && _snap.GptError != null);
      DrawBand(g, BandPathGpt, 20, BandTop1, BoxW, w != null ? w.UsedPct : 0);
      // 条下一行: 左端百分比, 右端重置时间
      if (w != null)
      {
        DrawBandMeta(g, 20, BoxW, BandTop1 + BandH + 6, w.Label + " " + (int)(w.UsedPct * 100) + "%",
          w.ResetMs > 0 ? "重置 " + FormatRemaining(w.ResetMs) : "");
      }
      else
        g.DrawString(_snap.GptError != null ? "⚠ " + _snap.GptError : "暂无数据", fontTick, BrRed, 20, BandTop1 + BandH + 6);

      // ---- 块2: OpenCode Go (三窗口 = 三条独立圆角条, 留间隙) ----
      DrawBlockHead(g, 84, BrAccentOff, "OpenCode Go", "无法获取信息 · 单击重试", _snap.GoMonth == null && _snap.GoError != null);
      var goWindows = new UsageWindow[] { _snap.GoSession, _snap.GoWeek, _snap.GoMonth };
      for (int i = 0; i < 3; i++)
      {
        var seg = goWindows[i];
        DrawBand(g, BandPathGo[i], GoSegX[i], BandTop2, SegW, seg != null ? seg.UsedPct : 0);
        if (seg != null)
          DrawBandMeta(g, GoSegX[i], SegW, BandTop2 + BandH + 6, seg.Label + " " + (int)(seg.UsedPct * 100) + "%",
            seg.ResetMs > 0 ? "重置 " + FormatRemaining(seg.ResetMs) : "");
        else
          g.DrawString(_snap.GoError != null ? "⚠ 无数据" : "—", fontTick, BrRed, GoSegX[i], BandTop2 + BandH + 6);
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
}
