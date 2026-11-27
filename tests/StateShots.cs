using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Milkman;

// 离屏渲染浮窗的各个状态，输出 PNG 供人工检查。
// 只构造状态并绘制：不显示窗口、不请求网络、不读写真实配置。
static class StateShots
{
    static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    static readonly BindingFlags PrivStatic = BindingFlags.NonPublic | BindingFlags.Static;

    static void Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : ".";
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 每种状态都要一个干净实例，避免状态互相污染
        Shot(outDir, "01-startup", null);
        Shot(outDir, "02-loading", MarkBusy);
        Shot(outDir, "03-success", FillSuccess);
        Shot(outDir, "04-failure", FillFailure);
        Shot(outDir, "05-partial", FillPartial);
        Console.WriteLine("done");
    }

    static void Shot(string dir, string name, Action<object, List<object>> setup)
    {
        using (var form = new MilkmanForm())
        {
            object session = typeof(MilkmanForm).GetField("_refresh", Priv).GetValue(form);
            List<object> rows = Rows(session);
            if (setup != null) setup(session, rows);

            typeof(MilkmanForm).GetMethod("ApplyScale", Priv).Invoke(form, null);
            int designH = (int)typeof(MilkmanForm).GetField("_designH", Priv).GetValue(form);
            int designW = (int)typeof(MilkmanForm).GetField("W", PrivStatic).GetValue(null);

            // 离屏渲染固定 1:1：设计单位 == 像素。否则 _scale 取自真实显示器 DPI，
            // 而位图按设计尺寸创建，两者不一致会让内容偏移并留白。
            typeof(MilkmanForm).GetField("_scale", Priv).SetValue(form, 1f);
            typeof(MilkmanForm).GetField("_formH", Priv).SetValue(form, designH);
            typeof(MilkmanForm).GetMethod("RebuildFonts", Priv).Invoke(form, null);

            using (var bmp = new Bitmap(designW, designH))
            using (var g = Graphics.FromImage(bmp))
            {
                var e = new PaintEventArgs(g, new Rectangle(0, 0, designW, designH));
                typeof(MilkmanForm).GetMethod("OnPaint", Priv).Invoke(form, new object[] { e });
                bmp.Save(System.IO.Path.Combine(dir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            Console.WriteLine(name + "  " + designW + "x" + designH + "  rows=" + rows.Count);
        }
    }

    static List<object> Rows(object session)
    {
        var list = (IList)session.GetType().GetField("Rows").GetValue(session);
        var rows = new List<object>();
        foreach (var item in list) rows.Add(item);
        return rows;
    }

    static void Start(object session)
    {
        session.GetType().GetProperty("Started").SetValue(session, true, null);
    }

    static void SetBusy(object row, bool busy)
    {
        row.GetType().GetProperty("Busy").SetValue(row, busy, null);
    }

    static void Apply(object row, FetchResult result)
    {
        row.GetType().GetMethod("Apply").Invoke(row, new object[] { result });
    }

    // 刚启动：不作任何修改，就是这个状态
    // 已点击、请求进行中：所有行 Busy，无数据
    static void MarkBusy(object session, List<object> rows)
    {
        Start(session);
        foreach (var row in rows) SetBusy(row, true);
    }

    static void FillSuccess(object session, List<object> rows)
    {
        Start(session);
        for (int i = 0; i < rows.Count; i++) Apply(rows[i], Sample(i));
    }

    static void FillFailure(object session, List<object> rows)
    {
        Start(session);
        foreach (var row in rows) Apply(row, Error("HTTP 请求被中止: 未能创建 SSL/TLS 安全通道。"));
    }

    static void FillPartial(object session, List<object> rows)
    {
        Start(session);
        for (int i = 0; i < rows.Count; i++)
            Apply(rows[i], i == 0 ? Sample(0) : Error(i == 1 ? "Go key 被拒绝" : "无法获取信息"));
    }

    static FetchResult Error(string msg)
    {
        var r = new FetchResult();
        r.Error = msg;
        return r;
    }

    // 覆盖真实会出现的数据形态：未开始、慢窗口、满额
    static FetchResult Sample(int rowIndex)
    {
        var r = new FetchResult();
        r.LastSuccess = DateTime.Now;
        long now = Program.NowUnixMs();
        long h = 3600L * 1000, d = 24 * h;

        if (rowIndex == 0)
        {
            r.Windows.Add(Win("5h", 0.02, now + 2 * h));
            r.Windows.Add(Win("周", 0.22, now + 6 * d));
        }
        else if (rowIndex == 1)
        {
            var w = Win("5h", 0.0, 0);
            w.NotStarted = true;
            r.Windows.Add(w);
            r.Windows.Add(Win("周", 0.79, now + 2 * d));
            r.Windows.Add(Win("月", 0.39, now + 25 * d));
        }
        else
        {
            var w = Win("5h", 0.0, 0);
            w.NotStarted = true;
            r.Windows.Add(w);
            r.Windows.Add(Win("周", 0.0, now + 5 * d));
            r.Windows.Add(Win("月", 0.98, now + 7 * d));
        }
        return r;
    }

    static UsageWindow Win(string label, double pct, long resetMs)
    {
        return new UsageWindow { Label = label, UsedPct = pct, ResetMs = resetMs };
    }
}
