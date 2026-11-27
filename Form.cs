// Milkman — 浮窗：窗口行为、拖拽吸附、刷新调度与位置持久化
// 绘制部分见 Ui.cs（同一个 partial 类）。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Milkman
{
  partial class MilkmanForm : Form
  {
    // 奶白暖色系（Milkman 主题: 牛奶/奶牛, 参照 DeepSeekTimeline 的浅色基调）
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
    }

    // 字体层次（参照 DeepSeekTimeline）：单一家族，按目标屏幕实际像素重建，
    // 绘制时绕开画布缩放，让 GDI+ 去做字形微调。
    Font fontBig, fontMid, fontTick;
    // 字体与几何共用 144 DPI 基准：这些是 144 下的像素，乘 _scale 得到该屏真实字号。
    // 注脚（第三行）在 96 DPI 屏上实际 12px，是可读性与密度的折中。
    const int BigDesignPx = 24, MidDesignPx = 18, TickDesignPx = 18;
    // 中文与拉丁同族：微软雅黑自带完整拉丁字形，无需第二款字体。
    const string UiFace = "Microsoft YaHei UI";
    // 缩放系数：窗口按所在显示器 DPI 相对 144 基准缩放。版面一直是按 150% 显示器调校的，
    // 用 144 作基准就能在 100% 屏上回到原来的观感（520→347），在 150% 屏上保持原尺寸（520）。
    float _scale = 1f;
    int _designH;   // 设计单位高度
    int _formH;     // 实际像素高度
    Rectangle closeRect = new Rectangle(W - CloseRight, CloseTop, CloseSize, CloseSize);
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
    // 吸附收起时顶部保留的提示条高度（见 ScreenLayout.Peek）
    // 收起时的窗口顶端：始终露出 Peek 个实际像素
    int CollapsedTop { get { return monRect.Top - _formH + ScreenLayout.Peek; } }
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
      int gptTop = NextBlockTop();
      var gptPaths = new GraphicsPath[3][];
      for (int count = 1; count <= 3; count++) gptPaths[count - 1] = CreateBandPaths(gptTop, count);
      _refresh.Rows.Add(new SourceRow("ChatGPT Plus", gptTop, BrAccent, gptPaths,
        () => _fetcher.FetchGpt(_cfg.ChatGptAuthPath)));
      foreach (var account in _cfg.OpenCodeAccounts)
        AddSource("OpenCode Go", account.Name, BrAccentOff, () => _fetcher.FetchGo(account));
      var cc = _cfg.CommandCode;
      if (cc != null) AddSource("Command Code", cc.Name, BrAccentCc, () => _fetcher.FetchCommandCode(cc));
      _designH = BlockTop0 + _refresh.Rows.Count * BlockH;

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

    // 下一个 block 的顶部：block 依次向下排，没有特殊首行
    int NextBlockTop() { return BlockTop0 + _refresh.Rows.Count * BlockH; }

    void AddSource(string provider, string name, SolidBrush dot, Func<FetchResult> fetch)
    {
      int y = NextBlockTop();
      string title = string.IsNullOrEmpty(name) ? provider : provider + " · " + name;
      _refresh.Rows.Add(new SourceRow(title, y, dot, new[] { CreateBandPaths(y, 3) }, fetch));
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
      Location = ScreenLayout.AnchorTo(area, new Point(_cfg.OffsetX, _cfg.OffsetY), Size, canDock);
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
      _scale = dpi / (float)ScreenLayout.BaseDpi;
      _formH = Scaled(_designH);
      RebuildFonts();
    }

    // 字体按当前屏幕的实际像素重建
    void RebuildFonts()
    {
      var old = new[] { fontBig, fontMid, fontTick };
      float big = BigDesignPx * _scale, mid = MidDesignPx * _scale, tick = TickDesignPx * _scale;
      fontBig = new Font(UiFace, big, FontStyle.Bold, GraphicsUnit.Pixel);
      fontMid = new Font(UiFace, mid, FontStyle.Regular, GraphicsUnit.Pixel);
      fontTick = new Font(UiFace, tick, FontStyle.Regular, GraphicsUnit.Pixel);
      Font = fontMid;
      foreach (var f in old) if (f != null) f.Dispose();
    }

    int Scaled(int design) { return ScreenLayout.Scaled(design, _scale); }

    // 客户端像素坐标 → 设计单位坐标
    Point ToDesign(Point client) { return ScreenLayout.ToDesign(client, _scale); }

    static Screen FindScreen(string device) { return ScreenLayout.FindScreen(device); }

    static bool DockableOn(Rectangle screen) { return ScreenLayout.DockableOn(screen); }


    // 统一的重新布局入口：换屏、改分辨率、改缩放、拖动结束都走这里。
    // anchor 是相对目标屏左上角的偏移，尺寸随该屏 DPI 重算，吸附态贴屏幕顶边。
    void ApplyLayout(Screen screen, Point anchor)
    {
      monRect = screen.Bounds;
      ApplyScale();
      Location = ScreenLayout.AnchorTo(screen.WorkingArea, anchor, Size, docked);
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

  }
}
