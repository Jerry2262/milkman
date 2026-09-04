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
  class OpenCodeAccountConfig
  {
    public string Name = "";
    public string AuthPath = "";
  }

  class Config
  {
    public int X = -1, Y = -1;
    public bool Docked = false;
    public int RefreshSeconds = 60;
    public List<OpenCodeAccountConfig> OpenCodeAccounts = DefaultOpenCodeAccounts();

    public static Config Load()
    {
      try
      {
        var path = Path.Combine(Program.DataDir(), "config.json");
        if (!File.Exists(path)) return new Config();
        var cfg = new JavaScriptSerializer().Deserialize<Config>(File.ReadAllText(path));
        if (cfg == null) return new Config();
        if (cfg.OpenCodeAccounts == null) cfg.OpenCodeAccounts = new List<OpenCodeAccountConfig>();
        return cfg;
      }
      catch { }
      return new Config();
    }

    public void Save()
    {
      try
      {
        Directory.CreateDirectory(Program.DataDir());
        File.WriteAllText(Path.Combine(Program.DataDir(), "config.json"),
          new JavaScriptSerializer().Serialize(this), new UTF8Encoding(false));
      }
      catch { }
    }

    static List<OpenCodeAccountConfig> DefaultOpenCodeAccounts()
    {
      string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "opencode", "auth.json");
      string wsl = Path.Combine(@"\\wsl.localhost\Ubuntu-24.04", "home", "jerry",
        ".local", "share", "opencode", "auth.json");
      return new List<OpenCodeAccountConfig>
      {
        new OpenCodeAccountConfig
        {
          Name = "个人",
          AuthPath = File.Exists(local) ? local : wsl
        }
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
  }

  class GptFetchResult
  {
    public List<UsageWindow> Windows = new List<UsageWindow>();
    public string Error;
    public DateTime LastSuccess;
  }

  class GoAccountSnapshot
  {
    public string Name;
    public UsageWindow[] Windows = new UsageWindow[3]; // 5h / 周 / 月
    public string Error;
    public DateTime LastSuccess;
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
    // Cloudflare 1010: opencode.ai 按浏览器签名拦截, 需浏览器 UA
    const string BrowserUA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
    static readonly JavaScriptSerializer Js = new JavaScriptSerializer();
    static readonly object JsLock = new object();

    public GptFetchResult FetchGpt()
    {
      var result = new GptFetchResult();
      try
      {
        FetchGptCore(result);
        result.LastSuccess = DateTime.Now;
      }
      catch (Exception e) { result.Error = Short(e.Message); }
      return result;
    }

    static string Short(string s) { return s.Length > 80 ? s.Substring(0, 80) + "…" : s; }

    static object Deserialize(string json)
    {
      lock (JsLock) return Js.DeserializeObject(json);
    }

    static string Serialize(object value) { lock (JsLock) return Js.Serialize(value); }

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
    void FetchGptCore(GptFetchResult result)
    {
      string authPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
      var dict = Deserialize(File.ReadAllText(authPath)) as Dictionary<string, object>;
      var tokens = Map(dict, "tokens");
      string access = Text(tokens, "access_token");
      string refreshToken = Text(tokens, "refresh_token");
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
          File.WriteAllText(authPath, Serialize(dict), new UTF8Encoding(false));
        }
        catch { /* 写回失败仅内存持有 */ }
        access = newAccess;
        resp = HttpGet(WhamUrl, access);
        if (resp == null) throw new Exception("wham/usage 刷新后仍拒绝");
      }
      ParseWham(result, resp);
    }

    string RefreshAccessToken(string refreshToken)
    {
      var body = "client_id=" + CodexClientId + "&grant_type=refresh_token&refresh_token=" + Uri.EscapeDataString(refreshToken);
      var resp = HttpPostForm(TokenUrl, body);
      var d = Deserialize(resp) as Dictionary<string, object>;
      // mac 版参考: 刷新可能返回 refresh_token_expired / reused / invalidated 等错误码
      string error = Text(d, "error");
      if (error.Length > 0) throw new Exception("token 刷新失败: " + error);
      string at = Text(d, "access_token");
      if (string.IsNullOrEmpty(at)) throw new Exception("token 刷新失败");
      return at;
    }

    void ParseWham(GptFetchResult result, string resp)
    {
      var d = Deserialize(resp) as Dictionary<string, object>;
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
      if (crl != null) AddWindowFromDict(result, crl, "代码审查");
      EnsureFiveHourWindow(result.Windows);
    }

    // 服务端临时不返回 5h 时仍保留版位；字段恢复后自动换回真实数据。
    static void EnsureFiveHourWindow(List<UsageWindow> windows)
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

    void AddWindow(GptFetchResult result, Dictionary<string, object> rl, string key, string label)
    {
      AddWindowFromDict(result, Map(rl, key), label);
    }

    void AddWindowFromDict(GptFetchResult result, Dictionary<string, object> w, string label)
    {
      if (w == null) return;
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
      object used = Value(w, "used_percent");
      win.UsedPct = (used != null ? Convert.ToDouble(used) : 0) / 100.0;
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
    public GoAccountSnapshot FetchGo(OpenCodeAccountConfig cfg)
    {
      var account = new GoAccountSnapshot { Name = cfg.Name };
      try
      {
        FetchGoCore(account, cfg);
        account.LastSuccess = DateTime.Now;
      }
      catch (Exception e) { account.Error = Short(e.Message); }
      return account;
    }

    void FetchGoCore(GoAccountSnapshot account, OpenCodeAccountConfig cfg)
    {
      string authPath = cfg.AuthPath;
      if (string.IsNullOrEmpty(authPath) || !File.Exists(authPath))
        throw new Exception("未找到 opencode auth.json");
      var d = Deserialize(File.ReadAllText(authPath)) as Dictionary<string, object>;
      var goe = Map(d, "opencode-go");
      string key = Text(goe, "key").Trim();
      if (key.Length == 0) throw new Exception("未登录 OpenCode Go");

      string body; int status;
      HttpGetWithStatus(GoUsageUrl, key, out body, out status);
      if (status == 401) throw new Exception("Go key 被拒绝");
      if (status == 403) throw new Exception("该 key 无 Go 订阅");
      if (status < 200 || status >= 300) throw new Exception("Go usage HTTP " + status);
      var u = Deserialize(body) as Dictionary<string, object>;
      var usage = Map(u, "usage");
      if (usage == null) throw new Exception("Go usage 响应无法解析");
      account.Windows[0] = ParseGoWindow(usage, "rolling", "5h");
      account.Windows[1] = ParseGoWindow(usage, "weekly", "周");
      account.Windows[2] = ParseGoWindow(usage, "monthly", "月");
    }

    UsageWindow ParseGoWindow(Dictionary<string, object> usage, string key, string label)
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
      return g;
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

    public static void Ensure()
    {
      try
      {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
          k.SetValue(Name, "\"" + Application.ExecutablePath + "\"");
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
      for (int count = 1; count <= 3; count++)
        GptBandPaths[count - 1] = CreateBandPaths(BandTop1, count);
    }

    // 字体层次（参照 DeepSeekTimeline）
    readonly Font fontBig, fontMid, fontTick;
    readonly Rectangle closeRect = new Rectangle(W - 26, 7, 15, 15);
    bool hoverClose;
    const int NoRetry = -2, GptRetry = -1;
    int _hoverRetry = NoRetry;

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
    GptFetchResult _gpt = new GptFetchResult();
    List<GoAccountSnapshot> _goAccounts = new List<GoAccountSnapshot>();
    string _goError;
    Timer _refreshTimer = new Timer();
    Timer _clockTimer = new Timer();
    bool _gptBusy, _gptAuto = true;
    bool[] _goBusy, _goAuto;
    int _formH;
    int _goRowCount;
    GraphicsPath[][] _goBandPaths;

    public MilkmanForm()
    {
      _cfg = Config.Load();
      _fetcher = new DataFetcher();
      foreach (var account in _cfg.OpenCodeAccounts)
        _goAccounts.Add(new GoAccountSnapshot { Name = account.Name });
      if (_cfg.OpenCodeAccounts.Count == 0) _goError = "未配置 OpenCode 账户";
      _goBusy = new bool[_cfg.OpenCodeAccounts.Count];
      _goAuto = new bool[_cfg.OpenCodeAccounts.Count];
      for (int i = 0; i < _goAuto.Length; i++) _goAuto[i] = true;
      _goRowCount = _cfg.OpenCodeAccounts.Count; // 列表留空 = 未订阅, 整块隐藏
      _formH = 16 + (_goRowCount + 1) * GoRowH;
      _goBandPaths = new GraphicsPath[_goRowCount][];
      for (int row = 0; row < _goRowCount; row++)
        _goBandPaths[row] = CreateBandPaths(GoFirstRowY + row * GoRowH + GoBandOffset, 3);

      fontBig = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
      fontMid = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
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
      Size = new Size(W, _formH);
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
          : new Point(Screen.PrimaryScreen.WorkingArea.Right - W - 10, Screen.PrimaryScreen.WorkingArea.Bottom - _formH - 40);


      MouseMove += OnMouseMoveHook;
      MouseLeave += OnMouseLeaveHook;
      MouseDown += OnMouseDownHook;
      Shown += OnShownHook;
      Shown += (s, e) =>
      {
        RefreshAll(true); // 句柄创建后再启动并行取数，确保结果能回到 UI 线程
        _refreshTimer.Start();
        _clockTimer.Start();
      };
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
          Location = new Point(Location.X, monRect.Top - _formH + Peek);
          _dockTimer.Start();
        }
      };

      _refreshTimer.Interval = _cfg.RefreshSeconds * 1000;
      _refreshTimer.Tick += (s, e) => RefreshAll(false);
      _clockTimer.Interval = 5000; // 与 DeepSeek 一致: 5s 周期重绘剩余时间
      _clockTimer.Tick += (s, e) =>
      {
        // 完全收起时可见区只有 5px 静态边条, 跳过无效重绘
        if (docked && !expanded && Top <= monRect.Top - _formH + Peek) return;
        Invalidate();
      };

    }

    // 单个低频调度器触发彼此独立的后台任务；失败只关闭对应来源的 auto 标记
    void RefreshAll(bool manual)
    {
      bool started = StartGptRefresh(manual);
      for (int i = 0; i < _cfg.OpenCodeAccounts.Count; i++)
        started = StartGoRefresh(i, manual) || started;
      if (started) Invalidate();
    }

    bool StartGptRefresh(bool manual)
    {
      if (_gptBusy || (!manual && !_gptAuto)) return false;
      _gptBusy = true;
      Task.Run(() =>
      {
        var result = _fetcher.FetchGpt();
        try { BeginInvoke((Action)(() => ApplyGptResult(result))); } catch { }
      });
      return true;
    }

    void ApplyGptResult(GptFetchResult result)
    {
      _gptBusy = false;
      _gptAuto = result.Error == null;
      if (result.Error == null)
        _gpt = result;
      else
        _gpt.Error = result.Error; // 保留上次成功数据，只更新错误状态
      Invalidate();
    }

    bool StartGoRefresh(int index, bool manual)
    {
      if (index < 0 || index >= _goBusy.Length || _goBusy[index] || (!manual && !_goAuto[index])) return false;
      _goBusy[index] = true;
      OpenCodeAccountConfig cfg = _cfg.OpenCodeAccounts[index];
      Task.Run(() =>
      {
        var result = _fetcher.FetchGo(cfg);
        try { BeginInvoke((Action)(() => ApplyGoResult(index, result))); } catch { }
      });
      return true;
    }

    void ApplyGoResult(int index, GoAccountSnapshot result)
    {
      if (index < 0 || index >= _goAccounts.Count) return;
      _goBusy[index] = false;
      _goAuto[index] = result.Error == null;
      GoAccountSnapshot current = _goAccounts[index];
      if (result.Error == null)
      {
        current.Windows = result.Windows;
        current.LastSuccess = result.LastSuccess;
        current.Error = null;
      }
      else
        current.Error = result.Error; // 仅暂停本账户，其他账户继续刷新
      Invalidate();
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
        m.Result = (IntPtr)(closeRect.Contains(pt) || RetryHit(pt) != NoRetry ? HTCLIENT : HTCAPTION);
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
      if (e.Button != MouseButtons.Left) return;
      if (closeRect.Contains(e.Location)) { Close(); return; } // ✕ = 退出程序
      int retry = RetryHit(e.Location);
      if (retry != NoRetry) RetryAt(retry);
    }

    void OnMouseMoveHook(object sender, MouseEventArgs e)
    {
      bool h = closeRect.Contains(e.Location);
      int retry = RetryHit(e.Location);
      if (h != hoverClose || retry != _hoverRetry)
      {
        hoverClose = h;
        _hoverRetry = retry;
        Cursor = h || retry != NoRetry ? Cursors.Hand : Cursors.Default;
        Invalidate();
      }
    }

    void OnMouseLeaveHook(object sender, EventArgs e)
    {
      if (hoverClose || _hoverRetry != NoRetry)
      {
        hoverClose = false;
        _hoverRetry = NoRetry;
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
      int target = expanded ? monRect.Top : monRect.Top - _formH + Peek;
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
    float Wd(Graphics g, string s, Font f) { return g.MeasureString(s, f).Width; }

    // 版式完全参照 DeepSeekTimeline: 顶部 provider 名, 中部圆角色带(独立圆角条/留间隙),
    // 每条下方 两行元信息: 百分比 + 重置时间
    const int BandH = 12, BandTop1 = 44;
    const int GoFirstRowY = 88, GoRowH = 76, GoBandOffset = 32;
    const float BoxW = W - 40f, Gap = 6f;
    static readonly Color AccentOff = Color.FromArgb(88, 143, 124);
    static readonly SolidBrush BrAccentOff;
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

    Rectangle RetryButtonRect(int rowY)
    {
      return new Rectangle(W - 76, rowY + 1, 48, 22);
    }

    int RetryHit(Point p)
    {
      if (_gpt.Error != null && !_gptBusy && RetryButtonRect(12).Contains(p))
        return GptRetry;
      for (int row = 0; row < _goAccounts.Count && row < _goBusy.Length; row++)
        if (_goAccounts[row].Error != null && !_goBusy[row] && RetryButtonRect(GoFirstRowY + row * GoRowH).Contains(p))
          return row;
      return NoRetry;
    }

    void RetryAt(int retry)
    {
      bool started = retry == GptRetry ? StartGptRefresh(true) : StartGoRefresh(retry, true);
      if (started)
      {
        _hoverRetry = NoRetry;
        Cursor = hoverClose ? Cursors.Hand : Cursors.Default;
        Invalidate();
      }
    }

    string SourceStatus(bool busy, string error, DateTime lastSuccess)
    {
      if (busy) return error != null ? "正在重试…" : lastSuccess == default(DateTime) ? "正在获取…" : "正在刷新…";
      if (error != null) return "无法获取信息";
      return lastSuccess == default(DateTime) ? "" : "更新于 " + lastSuccess.ToString("HH:mm:ss");
    }

    void DrawBlockHead(Graphics g, int rowY, SolidBrush dot, string title, string status, bool error, bool busy, bool retry, bool hoverRetry)
    {
      g.FillEllipse(dot, 22, rowY + 7, 9, 9);
      g.DrawString(title, fontBig, BrText, 40, rowY - 1);
      if (!string.IsNullOrEmpty(status))
      {
        SolidBrush brush = error && !busy ? BrRed : busy ? BrSub : BrFaint;
        int right = retry ? RetryButtonRect(rowY).Left - 8 : W - 20;
        g.DrawString(status, fontMid, brush, right - Wd(g, status, fontMid), rowY + 3);
      }
      if (retry)
      {
        Rectangle r = RetryButtonRect(rowY);
        using (GraphicsPath p = RoundedRect(r, 5f))
        {
          g.FillPath(hoverRetry ? BrAccent : BrTrack, p);
          g.DrawPath(hoverRetry ? PenCloseHot : PenBarBorder, p);
        }
        g.DrawString("重试", fontMid, hoverRetry ? BrBg : BrText,
          r.Left + (r.Width - Wd(g, "重试", fontMid)) / 2f, r.Top + 3);
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
            !window.Unlimited && window.ResetMs > 0 ? "重置 " + FormatRemaining(window.ResetMs) : "");
        else
          g.DrawString(error != null ? "⚠ 无数据" : "—", fontTick, error != null ? BrRed : BrSub,
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

      // ---- 块1: ChatGPT Plus (按实际额度窗口数分段) ----
      DrawBlockHead(g, 12, BrAccent, "ChatGPT Plus",
        SourceStatus(_gptBusy, _gpt.Error, _gpt.LastSuccess), _gpt.Error != null, _gptBusy,
        _gpt.Error != null && !_gptBusy, _hoverRetry == GptRetry);
      int gptCount = Math.Max(1, Math.Min(3, _gpt.Windows.Count));
      DrawWindows(g, _gpt.Windows, GptBandPaths[gptCount - 1], BandTop1, _gpt.Error);

      // ---- 块2+: 每个 OpenCode Go 账户一行，每行 5h/周/月三窗口 ----
      for (int row = 0; row < _goRowCount; row++)
      {
        int rowY = GoFirstRowY + row * GoRowH;
        int bandY = rowY + GoBandOffset;
        GoAccountSnapshot account = row < _goAccounts.Count ? _goAccounts[row] : null;
        string name = account != null ? account.Name
          : row < _cfg.OpenCodeAccounts.Count ? _cfg.OpenCodeAccounts[row].Name : "";
        string title = string.IsNullOrEmpty(name) ? "OpenCode Go" : "OpenCode Go · " + name;
        bool hasError = account != null ? account.Error != null : _goError != null;
        bool busy = row < _goBusy.Length && _goBusy[row];
        DateTime lastSuccess = account != null ? account.LastSuccess : default(DateTime);
        string error = account != null ? account.Error : _goError;
        DrawBlockHead(g, rowY, BrAccentOff, title,
          SourceStatus(busy, error, lastSuccess), hasError, busy,
          account != null && error != null && !busy, _hoverRetry == row);

        DrawWindows(g, account != null ? account.Windows : null, _goBandPaths[row], bandY, error);
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
