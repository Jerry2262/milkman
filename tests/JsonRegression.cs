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
        var gpt = new MilkmanForm.SourceRow("ChatGPT", 12, null, null, null, false);
        var go = new MilkmanForm.SourceRow("Go", 88, null, null, null, true);
        var cc = new MilkmanForm.SourceRow("CC", 164, null, null, null, true);
        Check(gpt.ActionText == "获取额度" && !gpt.TryStart(false), "ChatGPT waits for the first click");
        Check(go.TryStart(false) && cc.TryStart(false), "other sources start automatically");
        Check(gpt.TryStart(true) && gpt.ActionText == null && !gpt.TryStart(true) && !gpt.TryStart(false), "first click starts only one fetch");
        var error = new FetchResult { Error = "synthetic timeout" };
        gpt.Apply(error);
        Check(gpt.ActionText == "重试" && !gpt.TryStart(false) && gpt.TryStart(true), "first failure switches to manual retry");
        var success = new FetchResult { LastSuccess = DateTime.Now };
        success.Windows.Add(new UsageWindow { Label = "5h" });
        gpt.Apply(success);
        Check(gpt.ActionText == null && gpt.TryStart(false), "first success enables automatic refresh");
        gpt.Apply(error);
        Check(!gpt.TryStart(false) && gpt.Result.Windows[0].Label == "5h"
            && gpt.Result.LastSuccess == success.LastSuccess, "failed refresh retains the last successful data");
        go.Apply(new FetchResult(true));
        cc.Apply(new FetchResult(true));
        Check(go.TryStart(false) && cc.TryStart(false), "failure only pauses its own row");
    }
    static void Main() {
        CheckRefreshRows();
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
        Check(defaults.X == -1 && defaults.Y == -1 && defaults.RefreshSeconds == 60 && !defaults.Docked && defaults.OpenCodeAccounts.Count == 0,"configuration defaults and explicit empty accounts");
        Check(Config.Parse("{\"OpenCodeAccounts\":null}").OpenCodeAccounts.Count==0,"explicit null accounts");
        var nullValues=Config.Parse("{\"X\":null,\"Y\":null,\"Docked\":null,\"RefreshSeconds\":null}");
        Check(nullValues.X==-1 && nullValues.Y==-1 && !nullValues.Docked && nullValues.RefreshSeconds==60,"config null values fall back to defaults");
        Check(Config.Parse("{\"RefreshSeconds\":0}").RefreshSeconds==60,"zero refresh interval falls back to default");
        Check(Config.Parse("{\"RefreshSeconds\":-30}").RefreshSeconds==60,"negative refresh interval falls back to default");
        Check(Config.Parse("{\"RefreshSeconds\":2147484,\"OpenCodeAccounts\":[]}").RefreshSeconds==60,"overflowing timer interval falls back");
        Check(Config.Parse("{\"RefreshSeconds\":2147483,\"OpenCodeAccounts\":[]}").RefreshSeconds==2147483,"largest supported whole-second interval");
        var oversizedInterval=Config.Parse("{\"RefreshSeconds\":2147483648,\"X\":42,\"OpenCodeAccounts\":[]}");
        Check(oversizedInterval.RefreshSeconds==60 && oversizedInterval.X==42,"out-of-range interval does not discard other settings");
        var dockScreen=typeof(MilkmanForm).GetMethod("DockMonitorIndex", Private);
        var screens=new[] { new System.Drawing.Rectangle(0,0,1920,1080), new System.Drawing.Rectangle(0,-1080,1920,1080), new System.Drawing.Rectangle(-1920,0,1920,1080) };
        Check(dockScreen!=null && (int)dockScreen.Invoke(null,new object[]{new System.Drawing.Rectangle(100,-1080,520,244),screens})==1,"restore docked upper screen");
        Check((int)dockScreen.Invoke(null,new object[]{new System.Drawing.Rectangle(100,-1319,520,244),screens})==1,"restore legacy collapsed upper screen");
        Check((int)dockScreen.Invoke(null,new object[]{new System.Drawing.Rectangle(-900,-239,520,244),screens})==2,"restore legacy collapsed left screen");
        var nullAccounts=Config.Parse("{\"OpenCodeAccounts\":[null,{\"Name\":\"only\"}]}");
        Check(nullAccounts.OpenCodeAccounts.Count==1 && nullAccounts.OpenCodeAccounts[0].Name=="only","null account entries ignored");
        string stampText=Program.NowRfc3339();
        DateTimeOffset stamp;
        Check(stampText.Length==30 && stampText.EndsWith("Z")
          && DateTimeOffset.TryParse(stampText,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out stamp),"last_refresh RFC3339 format");
        string config="{\"x\":42,\"Y\":-2160,\"DOCKED\":true,\"RefreshSeconds\":90,\"ChatGptAuthPath\":\"C:\\\\fixture\\\\codex\\\\auth.json\",\"OpenCodeAccounts\":[{\"name\":\"测试🥛\",\"Key\":\"fixture-key\",\"AuthPath\":\"C:\\\\fixture\\\\auth.json\"}]}";
        var cfg=Config.Parse(config);
        Check(cfg.X==42 && cfg.Y==-2160 && cfg.Docked && cfg.RefreshSeconds==90,"config numbers and case insensitive fields");
        Check(cfg.ChatGptAuthPath=="C:\\fixture\\codex\\auth.json","chatgpt auth path");
        Check(Config.Parse("{}").ChatGptAuthPath=="","chatgpt auth path defaults to empty");
        Check(cfg.OpenCodeAccounts.Count==1 && cfg.OpenCodeAccounts[0].Name=="测试🥛" && cfg.OpenCodeAccounts[0].Key=="fixture-key","account fields");
        var dictionary=typeof(Config).GetMethod("ToDictionary", BindingFlags.NonPublic|BindingFlags.Instance).Invoke(cfg,null);
        var saved=Config.Parse(Write(dictionary));
        Check(saved.X==cfg.X && saved.Y==cfg.Y && saved.OpenCodeAccounts[0].AuthPath==cfg.OpenCodeAccounts[0].AuthPath,"config save roundtrip");
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
