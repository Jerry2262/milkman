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
        System.IO.Directory.CreateDirectory(outDir); // 目录不存在时 Save 会抛 GDI+ 异常
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 每种状态都要一个干净实例，避免状态互相污染
        Shot(outDir, "01-startup", null);
        Shot(outDir, "02-loading", MarkBusy);
        Shot(outDir, "03-success", FillSuccess);
        Shot(outDir, "04-failure", FillFailure);
        Shot(outDir, "05-partial", FillPartial);
        // 边界：段数 1 与 3、超长账户名、超长错误、满额与无穷
        Shot(outDir, "06-one-segment", FillOneSegment);
        Shot(outDir, "07-three-segments", FillThreeSegments);
        Shot(outDir, "08-long-names", FillLongNames);
        Shot(outDir, "09-long-error", FillLongError);
        Shot(outDir, "10-extremes", FillExtremes);
        Shot(outDir, "11-stale-error", FillStaleError);
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

    // ChatGPT 只返回 1 个窗口：整行只有一段
    static void FillOneSegment(object session, List<object> rows)
    {
        Start(session);
        for (int i = 0; i < rows.Count; i++) Apply(rows[i], Sample(i));
        var one = new FetchResult();
        one.LastSuccess = DateTime.Now;
        one.Windows.Add(Win("月", 0.995, Program.NowUnixMs() + 29L * 24 * 3600 * 1000));
        Apply(rows[0], one);
    }

    // ChatGPT 返回 3 个窗口：每段最窄
    static void FillThreeSegments(object session, List<object> rows)
    {
        Start(session);
        for (int i = 0; i < rows.Count; i++) Apply(rows[i], Sample(i));
        long now = Program.NowUnixMs();
        var three = new FetchResult();
        three.LastSuccess = DateTime.Now;
        three.Windows.Add(Win("5h", 0.99, now + 5 * 3600 * 1000));
        three.Windows.Add(Win("周", 1.0, now + 7L * 24 * 3600 * 1000));
        three.Windows.Add(Win("月", 0.07, now + 30L * 24 * 3600 * 1000));
        Apply(rows[0], three);
    }

    // 账户名很长：标题必须被截断，且不能压到状态文字
    static void FillLongNames(object session, List<object> rows)
    {
        Start(session);
        string[] names = {
            "ChatGPT Plus 年度订阅（团队版）",
            "OpenCode Go · 这是一个非常长的账户名称用来测试标题截断行为",
            "Command Code · GOAT 专业版订阅"
        };
        for (int i = 0; i < rows.Count && i < names.Length; i++)
        {
            SetTitle(rows[i], names[i]);
            Apply(rows[i], Sample(i));
        }
    }

    // 错误原因很长：第三行必须截断到一行，不换行也不溢出
    static void FillLongError(object session, List<object> rows)
    {
        Start(session);
        foreach (var row in rows)
            Apply(row, Error("HTTP 请求被中止: 未能创建 SSL/TLS 安全通道；如果使用代理请确认代理正在运行，"
                + "并检查系统时间是否正确，证书链可能因时间偏差而无法验证。"));
    }

    // 满额与无穷占位同时出现
    static void FillExtremes(object session, List<object> rows)
    {
        Start(session);
        long now = Program.NowUnixMs();
        var a = new FetchResult();
        a.LastSuccess = DateTime.Now;
        a.Windows.Add(Win("5h", 1.0, now + 3 * 3600 * 1000));
        a.Windows.Add(Win("周", 0.0, 0));
        a.Windows[1].NotStarted = true;
        var b = new FetchResult();
        b.LastSuccess = DateTime.Now;
        b.Windows.Add(Win("周", 0.5, now + 6L * 24 * 3600 * 1000));
        b.Windows[0].Unlimited = true;
        var c = new FetchResult();
        c.LastSuccess = DateTime.Now;
        c.Windows.Add(Win("审查", 0.34, now + 17L * 3600 * 1000));   // 真实标签
        c.Windows.Add(Win("代码审查窗口", 0.61, now + 9L * 3600 * 1000)); // 极端长标签：只保百分比
        Apply(rows[0], a);
        Apply(rows[1], b);
        Apply(rows[2], c);
    }

    // 先成功、随后刷新失败：旧数据保留，错误原因没有第三行可放，第一行给短状态
    static void FillStaleError(object session, List<object> rows)
    {
        Start(session);
        for (int i = 0; i < rows.Count; i++) Apply(rows[i], Sample(i));
        for (int i = 0; i < rows.Count; i++)
            Apply(rows[i], Error(i == 0 ? "HTTP 请求被中止: 未能创建 SSL/TLS 安全通道。" : "连接超时"));
    }

    static void SetTitle(object row, string title)
    {
        row.GetType().GetField("Title").SetValue(row, title);
    }

    static UsageWindow Win(string label, double pct, long resetMs)
    {
        return new UsageWindow { Label = label, UsedPct = pct, ResetMs = resetMs };
    }
}
