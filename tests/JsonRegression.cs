using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Milkman;

static class JsonRegression
{
    static int checks;
    static readonly BindingFlags Private = BindingFlags.Static | BindingFlags.NonPublic;
    static object Read(string json) { return Json.Parse(json); }
    static string Write(object value) { return Json.Serialize(value); }
    static Dictionary<string,object> Map(object value) { return (Dictionary<string,object>)value; }
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; }
    static void RoundTrip(string json) { var a=Read(json); var b=Read(Write(a)); Check(Equal(a,b), "roundtrip: " + json); }
    static bool Equal(object a, object b) {
        if (a == null || b == null) return a == b;
        var ma = a as Dictionary<string,object>; var mb = b as Dictionary<string,object>;
        if (ma != null || mb != null) {
            if (ma == null || mb == null || ma.Count != mb.Count) return false;
            foreach (var kv in ma) if (!mb.ContainsKey(kv.Key) || !Equal(kv.Value,mb[kv.Key])) return false;
            return true;
        }
        var la = a as IList; var lb = b as IList;
        if (la != null || lb != null) { if(la == null || lb == null || la.Count != lb.Count) return false; for(int i=0;i<la.Count;i++) if(!Equal(la[i],lb[i])) return false; return true; }
        if (a is string || b is string || a is bool || b is bool) return a.Equals(b);
        return Convert.ToDecimal(a,CultureInfo.InvariantCulture) == Convert.ToDecimal(b,CultureInfo.InvariantCulture);
    }
    static void CheckAuthRefresh() {
        var core=typeof(DataFetcher).GetMethod("FetchGptCore", BindingFlags.NonPublic|BindingFlags.Instance);
        string root=Environment.GetEnvironmentVariable("MILKMAN_AUTH_TEST_ROOT");
        if (string.IsNullOrEmpty(root)) root=System.IO.Path.GetTempPath();
        string dir=System.IO.Path.Combine(root,"Milkman 凭据-"+Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        string path=System.IO.Path.Combine(dir,"auth.json");
        const string oldAuth="{\"tokens\":{\"access_token\":\"at1\",\"refresh_token\":\"rt1\"},\"label\":\"个人\"}";
        const string cliAuth="{\"tokens\":{\"access_token\":\"cli-at\",\"refresh_token\":\"cli-rt\"}}";
        const string usage="{\"rate_limit\":{\"primary_window\":{\"limit_window_seconds\":604800,\"used_percent\":10}}}";
        const string rotated="{\"access_token\":\"at2\",\"refresh_token\":\"rt2\"}";
        try {
            System.IO.File.WriteAllText(path,oldAuth);
            Func<string,string> cliWon=at => {
                if (at=="at1") { System.IO.File.WriteAllText(path,cliAuth); return null; }
                Check(at=="cli-at","reuse newer CLI access token");
                return usage;
            };
            Func<string,string> noRefresh=rt => { throw new Exception("must not refresh an obsolete token"); };
            core.Invoke(new DataFetcher(),new object[]{new FetchResult(),path,cliWon,noRefresh});
            Check(System.IO.File.ReadAllText(path)==cliAuth,"clean read of newer credentials does not rewrite the file");

            System.IO.File.WriteAllText(path,oldAuth);
            Func<string,string> afterRefresh=at => at=="at2" ? usage : null;
            Func<string,string> refresh=rt => { Check(rt=="rt1","refresh uses current token"); return rotated; };
            core.Invoke(new DataFetcher(),new object[]{new FetchResult(),path,afterRefresh,refresh});
            byte[] savedAuth=System.IO.File.ReadAllBytes(path);
            string savedText=System.Text.Encoding.UTF8.GetString(savedAuth);
            Check(savedText.Contains("\"refresh_token\":\"rt2\""),"rotated token reaches disk");
            Check(savedAuth[0]=='{' && savedText.Contains("\"label\":\"个人\""),"auth rewrite preserves UTF-8 without BOM");

            System.IO.File.WriteAllText(path,oldAuth);
            Func<string,string> concurrentWrite=rt => { System.IO.File.WriteAllText(path,cliAuth); return rotated; };
            bool rejected=false;
            try { core.Invoke(new DataFetcher(),new object[]{new FetchResult(),path,afterRefresh,concurrentWrite}); }
            catch (TargetInvocationException e) { rejected=e.InnerException.Message.Contains("凭据已变更"); }
            Check(rejected && System.IO.File.ReadAllText(path)==cliAuth,"refresh does not overwrite an observed concurrent credential update");
            Check(System.IO.Directory.GetFileSystemEntries(dir).Length==1,"refresh leaves no temporary credential file");
        } finally { System.IO.Directory.Delete(dir,true); }
    }
    static void CheckRefreshRows() {
        int network = 0;
        var dispatched = new List<MilkmanForm.SourceRow>();
        var session = new MilkmanForm.RefreshSession(() => network++, dispatched.Add);
        foreach (var title in new[] { "ChatGPT", "Go A", "Go B", "CC" })
            session.Rows.Add(new MilkmanForm.SourceRow(title, 0, null, null, null));
        var first = session.Rows[0];
        Check(!session.RefreshDue() && network == 0 && dispatched.Count == 0
            && session.Rows.FindAll(r => session.ActionText(r) != null).Count == 1
            && session.ActionText(first) == "获取用量", "startup waits for one shared action");
        Check(session.Click(first) && !session.Click(first) && network == 1
            && dispatched.Count == session.Rows.Count && session.Rows.TrueForAll(r => r.Busy && dispatched.Contains(r)),
            "first click dispatches every configured source once");
        var cached = new FetchResult { LastSuccess = DateTime.Now };
        cached.Windows.Add(new UsageWindow { ResetMs = 1000 });
        first.Apply(cached);
        var failed = session.Rows[1];
        failed.Apply(new FetchResult { Error = "timeout" });
        dispatched.Clear();
        Check(session.RefreshDue() && dispatched.Count == 1 && dispatched[0] == first,
            "scheduled refresh skips busy and failed sources");
        dispatched.Clear();
        Check(session.ActionText(failed) == "重试" && session.Click(failed) && network == 1
            && dispatched.Count == 1 && dispatched[0] == failed, "retry dispatches only the failed row");
        first.Apply(new FetchResult { Error = "timeout" });
        failed.Apply(new FetchResult(true));
        dispatched.Clear();
        Check(session.RefreshDue() && dispatched.Count == 1 && dispatched[0] == failed && first.Result == cached
            && session.HasCountdown(999) && !session.HasCountdown(1000), "recovered row resumes and stale data counts down until expiry");
    }
    // 位置模型：显示器身份 + 相对偏移。覆盖单屏、双屏、副屏在顶部/左侧、屏消失、工作区夹取。
    static void CheckPlacement() {
        var size = new System.Drawing.Size(520,244);
        var primary   = new System.Drawing.Rectangle(0,0,1920,1128);
        var upper     = new System.Drawing.Rectangle(0,-1440,2560,1392);
        var left      = new System.Drawing.Rectangle(-1920,0,1920,1080);
        var tiny      = new System.Drawing.Rectangle(0,0,800,600);

        // 单屏：偏移就是相对主屏的位置
        Check(ScreenLayout.AnchorTo(primary,new System.Drawing.Point(100,200),size,false)==new System.Drawing.Point(100,200),
            "single screen keeps the anchor offset");

        // 副屏在顶部(负坐标)：同一锚点语义不变
        Check(ScreenLayout.AnchorTo(upper,new System.Drawing.Point(300,140),size,false)==new System.Drawing.Point(300,-1300),
            "upper screen resolves negative coordinates from its own origin");

        // 副屏在左侧：坐标系原点不同，偏移语义一致
        Check(ScreenLayout.AnchorTo(left,new System.Drawing.Point(120,150),size,false)==new System.Drawing.Point(-1800,150),
            "left screen keeps the same relative offset");

        // 分辨率变小：偏移超出可见范围时被夹回工作区
        Check(ScreenLayout.AnchorTo(tiny,new System.Drawing.Point(1700,1000),size,false)==new System.Drawing.Point(280,356),
            "offset beyond a shrunken screen is clamped into view");

        // 吸附态：纵向贴屏幕顶边，横向超出时仍被夹回可见范围
        Check(ScreenLayout.AnchorTo(upper,new System.Drawing.Point(2400,900),size,true)==new System.Drawing.Point(2040,-1440),
            "docked placement pins to the screen top edge");

        // 窗口比工作区还宽时不产生负坐标
        Check(ScreenLayout.AnchorTo(new System.Drawing.Rectangle(0,0,400,400),new System.Drawing.Point(0,0),size,false).X==0,
            "screen narrower than the window stays at the left edge");

        // 屏消失：按名字找不到返回 null，调用方回落主屏
        var findScreen = typeof(MilkmanForm).GetMethod("FindScreen", Private);
        Check((object)findScreen.Invoke(null,new object[]{"\\\\.\\DISPLAY9"})==null,"missing monitor resolves to null");
        Check((object)findScreen.Invoke(null,new object[]{""})==null,"empty monitor name resolves to null");

        // 吸附资格：只有顶边外侧没有其它屏幕覆盖时才允许吸附
        var top    = new System.Drawing.Rectangle(0,-1440,2560,1440); // 上方副屏
        var bottom = new System.Drawing.Rectangle(0,0,1920,1200);      // 主屏，顶边正好是副屏底边
        var side   = new System.Drawing.Rectangle(1920,0,1280,800);    // 主屏右侧，同样被副屏横跨
        var alone  = new System.Drawing.Rectangle(0,0,1920,1200);

        // 用户当前的布局：副屏在主屏正上方，因此主屏不能吸附，副屏可以
        Check(!ScreenLayout.CanDock(bottom,new[]{top,bottom}),"main screen cannot dock when another screen sits directly above");
        Check(ScreenLayout.CanDock(top,new[]{top,bottom}),"upper screen can dock because nothing is above it");

        // 只有一块屏时总是可以吸附
        Check(ScreenLayout.CanDock(alone,new[]{alone}),"single screen can always dock");

        // 上方有屏但横向不重叠，互不影响
        var farAbove = new System.Drawing.Rectangle(3000,-1440,1280,800);
        Check(ScreenLayout.CanDock(alone,new[]{alone,farAbove}),"a screen above but horizontally apart does not block docking");

        // 右侧屏也在副屏覆盖范围内
        Check(!ScreenLayout.CanDock(side,new[]{top,bottom,side}),"screen spanned by an upper display cannot dock");

        // 位于下方的那块屏不会阻止上方屏吸附
        Check(ScreenLayout.CanDock(top,new[]{top,bottom,side}),"lower screens never block docking above them");
    }

    // 配置文件编码：必须是无 BOM 的 UTF-8，且非 ASCII（中文账户名）往返后不变。
    // 这直接对应一次真实事故：外部工具以带 BOM 的 UTF-8 改写配置后账户名损坏。
    static void CheckConfigEncoding() {
        string dir=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"Milkman-enc-"+Guid.NewGuid().ToString("N"));
        string path=System.IO.Path.Combine(dir,"config.json");
        try {
            var cfg=new Config();
            cfg.Monitor=@"\\.\DISPLAY2";
            cfg.OffsetX=1182; cfg.OffsetY=855; cfg.Docked=false;
            cfg.OpenCodeAccounts.Add(new OpenCodeAccountConfig { Name="个人", Key="oc_sk_fixture", AuthPath="" });
            Check(cfg.SaveTo(path),"config saves to a temporary path");

            byte[] raw=System.IO.File.ReadAllBytes(path);
            Check(!(raw.Length>=3 && raw[0]==0xEF && raw[1]==0xBB && raw[2]==0xBF),"config is written without a BOM");
            Check(raw[0]=='{',"config starts with '{', not a byte-order mark");

            // 中文与原样字节必须完整落盘
            string text=System.Text.Encoding.UTF8.GetString(raw);
            Check(text.Contains("个人"),"non-ASCII account names survive the write");
            byte[] nameBytes=System.Text.Encoding.UTF8.GetBytes("个人");
            Check(IndexOf(raw,nameBytes)>=0,"account name is stored as canonical UTF-8 bytes");

            // 读回后内容一致（往返不丢中文字段）
            var loaded=Config.Parse(text);
            Check(loaded.OpenCodeAccounts.Count==1 && loaded.OpenCodeAccounts[0].Name=="个人","config round-trips a Chinese account name");
            Check(loaded.Monitor==@"\\.\DISPLAY2" && loaded.OffsetX==1182 && loaded.OffsetY==855,"config round-trips the placement anchor");

            // 配置文件要给人读：必须是多行缩进格式，且账户字段排在程序维护的窗口状态之前
            Check(text.Contains("\n"),"config is written across multiple lines for human editing");
            Check(text.Contains("\n  \"OpenCodeAccounts\""),"config indents top-level keys");
            Check(text.IndexOf("\"OpenCodeAccounts\"") < text.IndexOf("\"Monitor\""),"human-edited fields come before machine-maintained layout state");

            // 凭据文件仍须紧凑：同一份 JSON 的紧凑序列化不含换行
            string compact=Write(new Dictionary<string,object>{{"a",1},{"b","x"}});
            Check(!compact.Contains("\n"),"credential files stay compact");
            Check(compact=="{\"a\":1,\"b\":\"x\"}","compact form keeps the single-line shape");
        } finally { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir,true); }
    }

    static int IndexOf(byte[] haystack, byte[] needle) {
        for (int i=0; i+needle.Length<=haystack.Length; i++) {
            int j=0;
            while (j<needle.Length && haystack[i+j]==needle[j]) j++;
            if (j==needle.Length) return i;
        }
        return -1;
    }

    // 版式对齐：三行左端必须落在同一条线上；各段数下末段必须收到右边界。
    // 段数 1/2/3 对应注脚 2/4/6 块（每段左右各一块文字）。
    static void CheckLayout() {
        const int left = 20, right = 500, gap = 8;

        // 段数 1、2、3 都要满足：首段左端 == 左对齐线，末段右端 == 右边界
        foreach (int count in new[] { 1, 2, 3 }) {
            float firstX, firstW;
            MilkmanForm.SegmentIn(left,right,gap,0,count,out firstX,out firstW);
            Check(Math.Abs(firstX-left)<0.001,"first segment starts on the left alignment line (count="+count+")");

            float lastX, lastW;
            MilkmanForm.SegmentIn(left,right,gap,count-1,count,out lastX,out lastW);
            Check(Math.Abs((lastX+lastW)-right)<0.001,"last segment ends on the right edge (count="+count+")");

            // 相邻段间距恒定，段宽一致
            for (int i=0;i<count;i++) {
                float x,w;
                MilkmanForm.SegmentIn(left,right,gap,i,count,out x,out w);
                Check(Math.Abs(w-firstW)<0.001,"all segments share one width (count="+count+")");
                if (i>0) {
                    float px,pw;
                    MilkmanForm.SegmentIn(left,right,gap,i-1,count,out px,out pw);
                    Check(Math.Abs((x-(px+pw))-gap)<0.001,"segment gap is constant (count="+count+")");
                }
            }
        }
    }

    static void Main() {
        CheckRefreshRows();
        CheckPlacement();
        CheckLayout();
        CheckConfigEncoding();
        System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("fr-FR");
        foreach (var json in new[] {
            "null", "true", "false", "123", "-2147483649", "9223372036854775807", "1.25", "1e-3",
            "\"中文🥛\\r\\n\\t\\b\\f\\\"\\\\\\/\"", "[]", "{}", "[null,true,1.25,\"中\",{}]",
            "{\"tokens\":{\"access_token\":\"fixture\",\"refresh_token\":\"unchanged\",\"id_token\":\"unknown-preserved\"},\"last_refresh\":1793609878711,\"future\":[null,{\"bool\":true,\"n\":12.5}]}"
        }) RoundTrip(json);
        var d = Map(Read("{\"tokens\":{\"access_token\":\"old\",\"refresh_token\":\"keep\"},\"future\":{\"items\":[1,2,3]},\"flag\":false}"));
        Map(d["tokens"])["access_token"]="new"; d["last_refresh"]=1900000000000L;
        var updated=Map(Read(Write(d)));
        Check((string)Map(updated["tokens"])["refresh_token"]=="keep", "preserve refresh token");
        Check((string)Map(updated["tokens"])["access_token"]=="new", "replace access token");
        Check(((IList)Map(updated["future"])["items"]).Count==3, "preserve unknown nested JSON");
        var parse=typeof(DataFetcher).GetMethod("ParseWham", BindingFlags.NonPublic|BindingFlags.Instance);
        var result=new FetchResult();
        parse.Invoke(new DataFetcher(), new object[]{result,"{\"rate_limit\":{\"primary_window\":{\"limit_window_seconds\":604800,\"used_percent\":42.5,\"reset_at\":1900000000}},\"credits\":{\"has_credits\":false,\"balance\":null}}"});
        Check(result.Windows.Count==2 && result.Windows[0].Label=="5h" && result.Windows[0].Unlimited, "preserve 5h placeholder");
        Check(result.Windows[1].Label=="周" && Math.Abs(result.Windows[1].UsedPct-.425)<.000001, "parse GPT usage");
        bool emptyUsageRejected = false;
        try { parse.Invoke(new DataFetcher(), new object[]{new FetchResult(),"{\"rate_limit\":{\"primary_window\":{}}}"}); }
        catch (TargetInvocationException e) { emptyUsageRejected = e.InnerException.Message.Contains("无用量窗口"); }
        Check(emptyUsageRejected, "GPT must not report success without usable windows");

        var whamResponse=typeof(DataFetcher).GetMethod("WhamResponse", Private);
        Check(whamResponse.Invoke(null,new object[]{"",401})==null,"unauthorized enters token refresh");
        bool rateLimitRejected = false;
        try { whamResponse.Invoke(null,new object[]{"{\"error\":\"rate limited\"}",429}); }
        catch (TargetInvocationException e) { rateLimitRejected = e.InnerException.Message.EndsWith("429"); }
        Check(rateLimitRejected,"rate limiting preserves status without refreshing the token");
        CheckAuthRefresh();
        // 刷新响应里的 token 成对轮换：refresh_token/id_token 必须一起写回，否则下次刷新被 reused 拒绝
        var parseRefresh=typeof(DataFetcher).GetMethod("ParseRefresh", Private);
        var applyRefresh=typeof(DataFetcher).GetMethod("ApplyRefresh", Private);
        var stored=Map(Map(Read("{\"tokens\":{\"access_token\":\"at1\",\"refresh_token\":\"rt1\",\"id_token\":\"idt1\",\"account_id\":\"acc\"},\"OPENAI_API_KEY\":null}"))["tokens"]);
        applyRefresh.Invoke(null,new object[]{stored,parseRefresh.Invoke(null,new object[]{"{\"access_token\":\"at2\",\"refresh_token\":\"rt2\",\"id_token\":\"idt2\",\"expires_in\":3600}"})});
        Check((string)stored["access_token"]=="at2" && (string)stored["refresh_token"]=="rt2" && (string)stored["id_token"]=="idt2","persist rotated refresh token");
        Check((string)stored["account_id"]=="acc","keep unrelated token fields");
        applyRefresh.Invoke(null,new object[]{stored,parseRefresh.Invoke(null,new object[]{"{\"access_token\":\"at3\"}"})});
        Check((string)stored["refresh_token"]=="rt2" && (string)stored["id_token"]=="idt2","keep rotated tokens when response omits them");
        string reused="";
        try { parseRefresh.Invoke(null,new object[]{"{\"error\":{\"message\":\"used\",\"type\":\"invalid_request_error\",\"code\":\"refresh_token_reused\"}}"}); }
        catch (TargetInvocationException e) { reused=e.InnerException.Message; }
        Check(reused=="刷新失败: refresh_token_reused","refresh error code surfaces");
        bool missingAccessRejected = false;
        try { parseRefresh.Invoke(null, new object[]{"{\"refresh_token\":\"rt2\"}"}); }
        catch (TargetInvocationException e) { missingAccessRejected = e.InnerException.Message.EndsWith("无 access_token"); }
        Check(missingAccessRejected, "token rotation cannot succeed without an access token");

        // 代表不同语法边界：截断、非标准数字、分隔符、键、尾部内容与字符串转义。
        foreach (string invalid in new[] { "", "NaN", "+1", "01", "1.", "1e+", "[1,]", "{\"a\":1,}", "[true false]", "{a:1}", "{}x", "\"\\x20\"", "\"\\u12xz\"", "\"a\nb\"" }) {
            bool rejected = false;
            try { Read(invalid); } catch (FormatException) { rejected = true; }
            Check(rejected, "reject malformed JSON: " + invalid);
        }
        string auth = "{\"tokens\":{\"access_token\":\"old\",\"unknown\":1e400},\"unknown\":{\"i\":9007199254740993,\"n\":-0,\"d\":1.2300e-40,\"arr\":[null,true,\"\\u0000\"]}}";
        var authTree=Map(Read(auth)); Map(authTree["tokens"])["access_token"]="new";
        string rewritten=Write(authTree);
        foreach(string token in new[]{"1e400","9007199254740993","-0","1.2300e-40"}) Check(rewritten.Contains(token),"preserve unknown number token");
        Check((string)Map(Map(Read(rewritten))["tokens"])["access_token"]=="new","auth update with extreme unknown values");
        double precise=1.2345678901234567d;
        Check(Convert.ToDouble(Read(Write(precise)),CultureInfo.InvariantCulture)==precise,"double roundtrip precision");
        float single=1.23456789f;
        Check(Convert.ToSingle(Read(Write(single)),CultureInfo.InvariantCulture)==single,"float roundtrip precision");
        var defaults=Config.Parse("{\"OpenCodeAccounts\":[]}");
        Check(defaults.Monitor=="" && defaults.OffsetX == -1 && defaults.OffsetY == -1 && defaults.RefreshSeconds == 60 && !defaults.Docked && defaults.OpenCodeAccounts.Count == 0,"configuration defaults and explicit empty accounts");
        Check(Config.Parse("{\"OpenCodeAccounts\":null}").OpenCodeAccounts.Count==0,"explicit null accounts");
        var nullValues=Config.Parse("{\"Monitor\":null,\"OffsetX\":null,\"OffsetY\":null,\"Docked\":null,\"RefreshSeconds\":null}");
        Check(nullValues.Monitor=="" && nullValues.OffsetX==-1 && nullValues.OffsetY==-1 && !nullValues.Docked && nullValues.RefreshSeconds==60,"config null values fall back to defaults");
        Check(Config.Parse("{\"RefreshSeconds\":0}").RefreshSeconds==60,"zero refresh interval falls back to default");
        Check(Config.Parse("{\"RefreshSeconds\":-30}").RefreshSeconds==60,"negative refresh interval falls back to default");
        Check(Config.Parse("{\"RefreshSeconds\":2147484,\"OpenCodeAccounts\":[]}").RefreshSeconds==60,"overflowing timer interval falls back");
        Check(Config.Parse("{\"RefreshSeconds\":2147483,\"OpenCodeAccounts\":[]}").RefreshSeconds==2147483,"largest supported whole-second interval");
        var oversizedInterval=Config.Parse("{\"RefreshSeconds\":2147483648,\"OffsetX\":42,\"OpenCodeAccounts\":[]}");
        Check(oversizedInterval.RefreshSeconds==60 && oversizedInterval.OffsetX==42,"out-of-range interval does not discard other settings");
        // 旧版绝对像素 X/Y 已废弃：读到也不影响新模型，位置回落到主屏
        var oldPixels=Config.Parse("{\"X\":2438,\"Y\":-2160,\"Docked\":true,\"OpenCodeAccounts\":[]}");
        Check(oldPixels.Monitor=="" && oldPixels.OffsetX==-1 && oldPixels.OffsetY==-1 && oldPixels.Docked,"legacy pixel coordinates are dropped in favour of the anchor model");
        var nullAccounts=Config.Parse("{\"OpenCodeAccounts\":[null,{\"Name\":\"only\"}]}");
        Check(nullAccounts.OpenCodeAccounts.Count==1 && nullAccounts.OpenCodeAccounts[0].Name=="only","null account entries ignored");
        string stampText=Program.NowRfc3339();
        DateTimeOffset stamp;
        Check(stampText.Length==30 && stampText.EndsWith("Z")
          && DateTimeOffset.TryParse(stampText,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out stamp),"last_refresh RFC3339 format");
        string config="{\"Monitor\":\"\\\\\\\\.\\\\DISPLAY2\",\"OffsetX\":42,\"OffsetY\":-1080,\"DOCKED\":true,\"RefreshSeconds\":90,\"ChatGptAuthPath\":\"C:\\\\fixture\\\\codex\\\\auth.json\",\"OpenCodeAccounts\":[{\"name\":\"测试🥛\",\"Key\":\"fixture-key\",\"AuthPath\":\"C:\\\\fixture\\\\auth.json\"}]}";
        var cfg=Config.Parse(config);
        Check(cfg.Monitor=="\\\\.\\DISPLAY2" && cfg.OffsetX==42 && cfg.OffsetY==-1080 && cfg.Docked && cfg.RefreshSeconds==90,"config numbers and case insensitive fields");
        Check(cfg.ChatGptAuthPath=="C:\\fixture\\codex\\auth.json","chatgpt auth path");
        Check(Config.Parse("{}").ChatGptAuthPath=="","chatgpt auth path defaults to empty");
        Check(cfg.OpenCodeAccounts.Count==1 && cfg.OpenCodeAccounts[0].Name=="测试🥛" && cfg.OpenCodeAccounts[0].Key=="fixture-key","account fields");
        var dictionary=typeof(Config).GetMethod("ToDictionary", BindingFlags.NonPublic|BindingFlags.Instance).Invoke(cfg,null);
        var saved=Config.Parse(Write(dictionary));
        Check(saved.Monitor==cfg.Monitor && saved.OffsetX==cfg.OffsetX && saved.OffsetY==cfg.OffsetY && saved.OpenCodeAccounts[0].AuthPath==cfg.OpenCodeAccounts[0].AuthPath,"config save roundtrip");
        Check(saved.ChatGptAuthPath==cfg.ChatGptAuthPath,"chatgpt auth path roundtrip");
        bool depthRejected=false;
        try { Read(new string('[',102)+"0"+new string(']',102)); } catch { depthRejected=true; }
        Check(depthRejected,"JSON depth limit");
        bool sizeRejected=false;
        try { Read(new string(' ',2097153)); } catch { sizeRejected=true; }
        Check(sizeRejected,"JSON size limit");
        var legacy=new System.Web.Script.Serialization.JavaScriptSerializer();
        var random=new Random(20261103);
        for(int i=0;i<500;i++) {
            var text=new System.Text.StringBuilder();
            for(int j=0;j<32;j++) text.Append((char)random.Next(0,256));
            var fixture=new Dictionary<string,object>{{"text",text.ToString()},{"n",(decimal)random.Next(-10000,10000)/100},{"a",new object[]{null,true,i}}};
            string encoded=Write(fixture);
            if (!Equal(Read(encoded),legacy.DeserializeObject(encoded)))
                throw new Exception("legacy differential JSON, fixture " + i);
        }
        checks++;
        System.Threading.Tasks.Parallel.For(0,1000,delegate(int i) { var value=Read(auth); string encoded=Write(value); if(!encoded.Contains("9007199254740993")) throw new Exception("concurrent JSON state"); });
        Check(true,"parallel parser and writer isolation");

        // Command Code：配置节三处同步（字段 / Parse / ToDictionary）与真实 credits 响应形状
        Check(Config.Parse("{}").CommandCode==null,"command code absent");
        Check(Config.Parse("{\"CommandCode\":null}").CommandCode==null,"command code null");
        var ccConfig=Config.Parse("{\"CommandCode\":{\"name\":\"主账号\",\"key\":\"user_fixture\"}}");
        Check(ccConfig.CommandCode!=null && ccConfig.CommandCode.Name=="主账号" && ccConfig.CommandCode.Key=="user_fixture","command code config");
        var ccSaved=Config.Parse(Write(typeof(Config).GetMethod("ToDictionary", BindingFlags.NonPublic|BindingFlags.Instance).Invoke(ccConfig,null)));
        Check(ccSaved.CommandCode!=null && ccSaved.CommandCode.Name=="主账号" && ccSaved.CommandCode.Key=="user_fixture","command code save roundtrip");
        var parseCc=typeof(DataFetcher).GetMethod("ParseCommandCode", BindingFlags.NonPublic|BindingFlags.Instance);
        var fetcher=new DataFetcher();
        var ccLive=new FetchResult(true);
        parseCc.Invoke(fetcher,new object[]{ccLive,
          "{\"credits\":{\"belowThreshold\":false,\"monthlyCredits\":67.808776588,\"purchasedCredits\":0,\"freeCredits\":0},\"windowLimits\":{\"limited\":true,\"fiveHour\":{\"used\":2.191223412,\"cap\":14,\"resetAt\":1793881990707},\"weekly\":{\"used\":7.5,\"cap\":35,\"resetAt\":0}}}",
          "{\"success\":true,\"data\":{\"planId\":\"individual-goat\",\"currentPeriodEnd\":\"2026-12-05T07:25:45.000Z\"}}"});
        Check(ccLive.Windows[0]!=null && Math.Abs(ccLive.Windows[0].UsedPct-2.191223412/14)<1e-12 && Math.Abs(ccLive.Windows[0].ResetMs-1793881990707)<1,"command code 5h window");
        Check(ccLive.Windows[1]!=null && ccLive.Windows[1].NotStarted && ccLive.Windows[1].ResetMs==0,"command code unopened weekly window");
        Check(ccLive.Windows[2]!=null && Math.Abs(ccLive.Windows[2].UsedPct-(1-67.808776588/70))<1e-9 && ccLive.Windows[2].ResetMs>0,"command code monthly sized from plan grant");
        var ccGrant=new FetchResult(true);
        parseCc.Invoke(fetcher,new object[]{ccGrant,
          "{\"credits\":{\"monthlyCredits\":25,\"monthlyCreditsGranted\":50},\"windowLimits\":{\"fiveHour\":{\"used\":0,\"cap\":14,\"resetAt\":0}}}",null});
        Check(ccGrant.Windows[2]!=null && Math.Abs(ccGrant.Windows[2].UsedPct-0.5)<1e-12 && ccGrant.Windows[1]==null,"command code reported grant wins");
        var ccBadSubscription=new FetchResult(true);
        parseCc.Invoke(fetcher,new object[]{ccBadSubscription,
          "{\"credits\":{\"monthlyCredits\":25,\"monthlyCreditsGranted\":50},\"windowLimits\":{\"fiveHour\":{\"used\":1,\"cap\":4},\"weekly\":{\"used\":2,\"cap\":8}}}","<html>"});
        Check(ccBadSubscription.Windows[0].UsedPct==0.25 && ccBadSubscription.Windows[1].UsedPct==0.25 && ccBadSubscription.Windows[2].UsedPct==0.5 && ccBadSubscription.Windows[2].ResetMs==0,"malformed optional subscription keeps credits data");
        var ccSeconds=new FetchResult(true);
        parseCc.Invoke(fetcher,new object[]{ccSeconds,
          "{\"credits\":{\"monthlyCredits\":0},\"windowLimits\":{\"fiveHour\":{\"used\":1,\"cap\":4,\"resetAt\":1793881990}}}",null});
        Check(ccSeconds.Windows[0]!=null && Math.Abs(ccSeconds.Windows[0].ResetMs-1793881990000)<1,"command code second-granularity resetAt");
        var ccBare=new FetchResult(true);
        parseCc.Invoke(fetcher,new object[]{ccBare,"{\"credits\":{\"monthlyCredits\":5}}",null});
        Check(ccBare.Windows[0]==null && ccBare.Windows[1]==null && ccBare.Windows[2]==null,"command code degrades without limits or grant");
        var ccPayg=new FetchResult(true);
        parseCc.Invoke(fetcher,new object[]{ccPayg,"{\"credits\":{\"monthlyCredits\":10,\"monthlyCreditsGranted\":20},\"windowLimits\":{\"limited\":false}}",null});
        Check(ccPayg.Windows[0]==null && ccPayg.Windows[1]==null && ccPayg.Windows[2]!=null,"command code pay-as-you-go hides rolling windows");
        bool ccRejected=false;
        try { parseCc.Invoke(fetcher,new object[]{new FetchResult(true),"{\"error\":{\"code\":\"UNAUTHORIZED\"}}",null}); }
        catch (TargetInvocationException) { ccRejected=true; }
        Check(ccRejected,"command code rejects response without credits");

        Console.WriteLine("PASS " + checks + " JSON/config regression checks (synthetic data only)");
    }
}
