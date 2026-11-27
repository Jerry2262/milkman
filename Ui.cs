// Milkman — 浮窗皮肤与自绘：版式、配色、字体、绘制
// 与 Form.cs 同属 MilkmanForm。版式是「一个 block = 一个订阅实例」，每个 block 三行：
//   row1 头部：图标 + 名称 + 状态 + 动作按钮，全部垂直居中于同一条轴
//   row2 用量条：三段均分，与 row1 共用同一列网格
//   row3 注脚：每段左端百分比、右端重置时间
// 所有坐标只在本文件计算，内容层不参与。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Milkman
{
  partial class MilkmanForm : Form
  {
    // ---------------- 列网格 ----------------
    const int W = 520;                 // 窗口宽（设计单位）

    // 三行共用一条左对齐线，就是圆点的左边缘：
    //   行1 圆点从 OutlineLeft 开始，标题紧随其后
    //   行2 用量条从 OutlineLeft 开始
    //   行3 注脚从 OutlineLeft 开始
    const int OutlineLeft = 20;        // 三行左端对齐基准（= 圆点左边缘）
    const int RightEdge = 500;         // 右对齐线（左右对称）
    const int ContentW = RightEdge - OutlineLeft;

    // 圆点：直径 10，紧贴左对齐线
    const int IconD = 10;
    // 标题从圆点右侧开始，与圆点留 8px
    const int TitleLeft = OutlineLeft + IconD + 8;

    // ---------------- block 内三行的纵向节奏 ----------------
    // 纵向节奏取自设计图（block 顶 12、条顶 32、注脚顶 48）；
    // 行高比设计图的 70 多 6，因为注脚字号提升后 70 只剩 2.7px 留白，block 之间会挤。
    const int BlockH = 76;             // 单个 block 高度（含 block 之间留白）
    const int BlockTop0 = 12;          // 首个 block 的顶部
    const int Row1H = 24;              // 头部行高
    const int Row1Top = 0;             // 头部行相对 block 顶
    const int Row2Top = 32;            // 用量条行相对 block 顶
    const int BarH = 10;               // 条高
    const int SegGap = 8;              // 段间距
    const int Row3Top = 48;            // 注脚行相对 block 顶

    // 头部行中心轴：row1 内所有元素都以此为垂直基准
    const int AxisY = Row1Top + Row1H / 2;

    // 关闭按钮
    const int CloseRight = 26, CloseTop = 7, CloseSize = 15;

    // 动作按钮：右端让开关闭按钮所在区域，宽度固定不随状态变化
    const int BtnW = 76, BtnH = 22;
    const int BtnGap = 8;              // 按钮与状态文字之间的间隙
    const int BtnRight = W - CloseRight - CloseSize - 8;   // = 471，与关闭按钮留 8px


    // 注脚文字基线相对 row3 顶部的偏移
    const int MetaBaseline = 0;
    // 注脚左段与右段之间的最小间隙
    const int MetaGap = 6;

    // ---------------- 绘制基础 ----------------
    // 文字按物理像素绘制并绕开画布缩放：字形由 GDI+ 按最终尺寸微调，不会被压缩变换破坏。
    // 坐标与测量仍以设计单位表达，进出这里时换算一次。
    float Wd(Graphics g, string s, Font f) { return g.MeasureString(s, f).Width / _scale; }

    float Ht(Graphics g, string s, Font f) { return g.MeasureString(s, f).Height / _scale; }

    void DrawText(Graphics g, string s, Font f, Brush b, float x, float y)
    {
      if (string.IsNullOrEmpty(s)) return;
      Matrix m = g.Transform; // 这是副本，必须自己释放
      g.ResetTransform();
      g.DrawString(s, f, b, x * _scale, y * _scale);
      g.Transform = m;
      m.Dispose();
    }

    // 垂直居中绘制：cy 是期望的文字视觉中心
    void DrawTextCentered(Graphics g, string s, Font f, Brush b, float x, float cy)
    {
      if (string.IsNullOrEmpty(s)) return;
      DrawText(g, s, f, b, x, cy - Ht(g, s, f) / 2f);
    }

    // 右端对齐到 right，垂直居中于 cy
    void DrawRightCentered(Graphics g, string s, Font f, Brush b, float right, float cy)
    {
      if (string.IsNullOrEmpty(s)) return;
      DrawTextCentered(g, s, f, b, right - Wd(g, s, f), cy);
    }

    // 在 [left, left+width] 内水平居中，垂直居中于 cy
    void DrawCenterCentered(Graphics g, string s, Font f, Brush b, float left, float width, float cy)
    {
      if (string.IsNullOrEmpty(s)) return;
      DrawTextCentered(g, s, f, b, left + (width - Wd(g, s, f)) / 2f, cy);
    }

    static readonly Color AccentOff = Color.FromArgb(88, 143, 124);
    static readonly SolidBrush BrAccentOff;
    static readonly Color AccentCc = Color.FromArgb(176, 136, 84);   // 暖棕: Command Code
    static readonly SolidBrush BrAccentCc;
    // ChatGPT 按实际窗口数选择 1/2/3 段；OpenCode 固定三段。

    // 第 i 段的左端与宽度（设计单位）
    static void Segment(int index, int count, out float x, out float w)
    {
      SegmentIn(OutlineLeft, RightEdge, SegGap, index, count, out x, out w);
    }

    // 纯函数版：给定内容区间与段间距，算出第 index 段的左端与宽度。
    // 抽出来是为了让「三行左端对齐」「末段收到右边界」这两条能被直接验证。
    internal static void SegmentIn(int left, int right, int gap, int index, int count,
      out float x, out float w)
    {
      w = ((right - left) - (count - 1) * gap) / (float)count;
      x = left + index * (w + gap);
    }

    static GraphicsPath[] CreateBandPaths(float rowTop, int count)
    {
      var paths = new GraphicsPath[count];
      for (int i = 0; i < count; i++)
      {
        float x, w;
        Segment(i, count, out x, out w);
        paths[i] = RoundedRect(new RectangleF(x, rowTop + Row2Top, w, BarH), 4f);
      }
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

    // ---------------- 命中的几何 ----------------
    Rectangle ActionRect(SourceRow row)
    {
      int axis = row.Y + AxisY;
      return new Rectangle(BtnRight - BtnW, axis - BtnH / 2, BtnW, BtnH);
    }

    SourceRow ActionHit(Point p)
    {
      foreach (var row in _refresh.Rows)
        if (_refresh.ActionText(row) != null && ActionRect(row).Contains(p)) return row;
      return null;
    }

    // ---------------- 内容层 ----------------
    // 只负责取值与文案，不参与任何坐标计算。
    // 第一行只表达「正在做什么」和「何时更新过」。
    // 失败原因由第三行整行承担，这里返回空，避免同一信息在标题行再占宽度。
    // 成功/获取中都没有按钮，状态右端可以用到按钮右端，因此「更新于 HH:mm:ss」放得下。
    string SourceStatus(bool busy, string error, DateTime lastSuccess)
    {
      if (busy) return lastSuccess == default(DateTime) ? "获取中" : "刷新中";
      // 从没成功过：原因由第三行整行显示，第一行留白
      if (error != null && lastSuccess == default(DateTime)) return "";
      // 有旧数据但这次刷新失败：第三行还在显示旧数据，错误原因没有别的落点，
      // 这里给一个短状态，避免用户只看到「重试」却不知道发生了什么
      if (error != null) return "获取失败";
      return lastSuccess == default(DateTime) ? "" : "更新于 " + lastSuccess.ToString("HH:mm:ss");
    }

    // 一条用量条的左右两段文案
    // 注脚两段文案。每段可用宽度约 103px（96 DPI），"重置 " 前缀占 27px 会把最宽的一组挤到溢出，
    // 因此省掉前缀：左段「5h 14%」，右段「1h33m」。右段为空表示无需显示。
    static void BandTexts(UsageWindow window, out string label, out string pct, out string right)
    {
      label = window.Label ?? "";
      pct = window.Unlimited ? "∞" : (int)(window.UsedPct * 100) + "%";
      right = window.Unlimited ? ""
        : window.NotStarted ? "未开始"
        : window.ResetMs > 0 ? FormatRemaining(window.ResetMs) : "";
    }

    // 按宽度截断，超出时以省略号收尾
    string Fit(Graphics g, string text, Font font, float max)
    {
      if (g.MeasureString(text, font).Width <= max * _scale) return text;
      int low = 0, high = text.Length;
      while (low < high)
      {
        int mid = (low + high + 1) / 2;
        if (g.MeasureString(text.Substring(0, mid) + "…", font).Width <= max * _scale) low = mid;
        else high = mid - 1;
      }
      return text.Substring(0, low) + "…";
    }

    // ---------------- 排版层 ----------------
    // row1：图标与标题垂直居中于同一条轴；状态右对齐到按钮左侧；按钮贴右侧留白
    void DrawBlockHead(Graphics g, SourceRow row)
    {
      int axis = row.Y + AxisY;
      bool error = row.Result.Error != null, busy = row.Busy;
      string title = row.Title, action = _refresh.ActionText(row);
      string status = SourceStatus(busy, row.Result.Error, row.Result.LastSuccess);

      g.FillEllipse(row.Dot, OutlineLeft, axis - IconD / 2, IconD, IconD);

      Rectangle act = ActionRect(row);
      // 无按钮时状态右端与按钮右端对齐，保持这条右线稳定
      float statusRight = action != null ? act.Left - BtnGap : BtnRight;

      // 状态宽度确定后，标题只用剩下的空间并截断，两者因此不会重叠。
      float statusW = 0;
      if (!string.IsNullOrEmpty(status))
      {
        statusW = Wd(g, status, fontMid);
        DrawRightCentered(g, status, fontMid, busy ? BrSub : BrFaint, statusRight, axis);
      }

      // 标题从内容左边界起，可用空间扣掉状态占位；放不下就截断
      // 有按钮时标题最远只能到按钮左侧；无按钮时可以用到状态右端或内容右边界
      float titleRight = action != null ? act.Left - BtnGap : (statusW > 0 ? statusRight - BtnGap - statusW : RightEdge);
      float titleRoom = titleRight - TitleLeft;
      DrawTextCentered(g, Fit(g, title, fontBig, titleRoom), fontBig, BrText, TitleLeft, axis);

      if (action != null)
      {
        bool hot = row == _hoverAction;
        using (GraphicsPath p = RoundedRect(act, 5f))
        {
          g.FillPath(hot ? BrAccent : BrTrack, p);
          g.DrawPath(hot ? PenCloseHot : PenBarBorder, p);
        }
        DrawCenterCentered(g, action, fontMid, hot ? BrBg : BrText, act.Left, act.Width, axis);
      }
    }

    // row2 + row3：每段一条轨道，注脚左端贴段左、右端贴段右
    void DrawBand(Graphics g, GraphicsPath p, float x, float y, float w, double frac)
    {
      g.FillPath(BrTrack, p);
      float fw = w * (float)(Math.Max(0, Math.Min(1, frac)));
      if (fw > 0)
      {
        g.SetClip(p);
        g.FillRectangle(StatusBrush(frac), x, y, fw, BarH);
        g.ResetClip();
      }
      g.DrawPath(PenBarBorder, p);
    }

    void DrawWindows(Graphics g, IList<UsageWindow> windows, GraphicsPath[] paths, int rowY, string error)
    {
      // paths 是单个 block 的段数组，长度即段数（Go/CC 固定 3，ChatGPT 1–3）
      int count = paths.Length;
      float barY = rowY + Row2Top;
      float metaY = rowY + Row3Top + MetaBaseline;
      // 整行无数据：错误原因只写一次，横跨整行宽度，避免逐段重复又被截断
      if (windows == null || windows.Count == 0)
      {
        for (int i = 0; i < count; i++)
        {
          float x, w;
          Segment(i, count, out x, out w);
          DrawBand(g, paths[i], x, barY, w, 0);
        }
        if (error != null)
          DrawText(g, Fit(g, "⚠ " + error, fontTick, ContentW), fontTick, BrRed, OutlineLeft, metaY);
        return;
      }

      for (int i = 0; i < count; i++)
      {
        float x, w;
        Segment(i, count, out x, out w);
        var window = i < windows.Count ? windows[i] : null;
        DrawBand(g, paths[i], x, barY, w, window != null ? window.UsedPct : 0);

        if (window == null)
        {
          DrawText(g, "—", fontTick, BrSub, x, metaY);
          continue;
        }
        string label, pct, right;
        BandTexts(window, out label, out pct, out right);
        // 标签可能比「5h」长得多（例如代码审查窗口），右侧还有剩余时间。
        // 百分比优先保住：先按剩余宽度缩短标签，实在放不下就只显示百分比。
        float rightW = string.IsNullOrEmpty(right) ? 0 : Wd(g, right, fontTick);
        float pctW = Wd(g, pct, fontTick);
        float gapW = Wd(g, " ", fontTick);
        float labelMax = w - rightW - (rightW > 0 ? MetaGap : 0) - pctW - gapW;
        // 只要标签没被截成纯省略号就带上；连一个字都留不下才只显示百分比
        string shortLabel = labelMax > 0 ? Fit(g, label, fontTick, labelMax) : "…";
        string text = shortLabel.Length > 0 && shortLabel != "…" ? shortLabel + " " + pct : pct;
        DrawText(g, Fit(g, text, fontTick, w - rightW - (rightW > 0 ? MetaGap : 0)), fontTick, BrText, x, metaY);
        if (rightW > 0) DrawRight(g, right, fontTick, BrSub, x + w, metaY);
      }
    }

    // 注脚左对齐到 left、右对齐到 right，同一基线
    void DrawRight(Graphics g, string s, Font f, Brush b, float right, float y)
    {
      if (string.IsNullOrEmpty(s)) return;
      DrawText(g, s, f, b, right - Wd(g, s, f), y);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
      base.OnPaint(e);
      var g = e.Graphics;
      g.SmoothingMode = SmoothingMode.AntiAlias;
      // 浮窗透明度与浅色底会让灰度抗锯齿显得发虚；ClearType 在像素网格上渲染，小字号更扎实。
      g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
      g.Clear(Bg);
      g.ScaleTransform(_scale, _scale); // 版面按设计单位绘制，由这里统一放大

      foreach (var row in _refresh.Rows)
      {
        DrawBlockHead(g, row);
        var windows = row.Result.Windows;
        int path = Math.Max(0, Math.Min(row.Paths.Length, windows.Count) - 1);
        DrawWindows(g, windows, row.Paths[path], row.Y, row.Result.Error);
      }

      // ---- 关闭按钮 ----
      Pen cp = hoverClose ? PenCloseHot : PenCloseDim;
      g.DrawLine(cp, closeRect.Left + 3, closeRect.Top + 4, closeRect.Right - 3, closeRect.Bottom - 4);
      g.DrawLine(cp, closeRect.Right - 3, closeRect.Top + 4, closeRect.Left + 3, closeRect.Bottom - 4);

      // ---- 细边框 ----
      g.DrawRectangle(PenBorder, 0.5f, 0.5f, W - 1f, _designH - 1f);
    }

    static SolidBrush StatusBrush(double frac)
    {
      if (frac < 0.5) return BrGreen;
      if (frac < 0.8) return BrYellow;
      return BrRed;
    }

    // 剩余时间统一用 d/h/m，不混中文「天」：注脚宽度紧张，拉丁字符更窄，
    // 且同一条文案里中英混排会显得不齐。
    static string FormatRemaining(double resetMs)
    {
      long ms = (long)(resetMs - Program.NowUnixMs());
      if (ms <= 0) return "已重置";
      if (ms > 365L * 24 * 3600 * 1000) return "—"; // 异常大的重置时间：不能让绘制线程抛 OverflowException
      var t = TimeSpan.FromMilliseconds(ms);
      if (t.TotalDays >= 1) return (int)t.TotalDays + "d" + t.Hours + "h";
      return (int)t.TotalHours + "h" + t.Minutes.ToString("00") + "m";
    }
  }
}
