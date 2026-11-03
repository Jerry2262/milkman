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
    static object Read(string json) { return typeof(DataFetcher).GetMethod("Deserialize", Private).Invoke(null, new object[] { json }); }
    static string Write(object value) { return (string)typeof(DataFetcher).GetMethod("Serialize", Private).Invoke(null, new object[] { value }); }
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
    static void Main() {
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
        var result=new GptFetchResult();
        parse.Invoke(new DataFetcher(), new object[]{result,"{\"rate_limit\":{\"primary_window\":{\"limit_window_seconds\":604800,\"used_percent\":42.5,\"reset_at\":1900000000}},\"credits\":{\"has_credits\":false,\"balance\":null}}"});
        Check(result.Windows.Count==2 && result.Windows[0].Label=="5h" && result.Windows[0].Unlimited, "preserve 5h placeholder");
        Check(result.Windows[1].Label=="周" && Math.Abs(result.Windows[1].UsedPct-.425)<.000001, "parse GPT usage");
        foreach (string invalid in new[] { "", " ", "undefined", "NaN", "Infinity", "+1", "01", "-01", "1.", ".1", "1e", "1e+", "[1,]", "{\"a\":1,}", "[true false]", "{a:1}", "{}x", "\"\\x20\"", "\"\\u12xz\"", "\"a\nb\"" }) {
            bool rejected=false; try { Read(invalid); } catch { rejected=true; }
            Check(rejected,"reject malformed JSON");
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
        var nullAccounts=Config.Parse("{\"OpenCodeAccounts\":[null,{\"Name\":\"only\"}]}");
        Check(nullAccounts.OpenCodeAccounts.Count==1 && nullAccounts.OpenCodeAccounts[0].Name=="only","null account entries ignored");
        string config="{\"x\":42,\"Y\":-2160,\"DOCKED\":true,\"RefreshSeconds\":90,\"OpenCodeAccounts\":[{\"name\":\"测试🥛\",\"Key\":\"fixture-key\",\"AuthPath\":\"C:\\\\fixture\\\\auth.json\"}]}";
        var cfg=Config.Parse(config);
        Check(cfg.X==42 && cfg.Y==-2160 && cfg.Docked && cfg.RefreshSeconds==90,"config numbers and case insensitive fields");
        Check(cfg.OpenCodeAccounts.Count==1 && cfg.OpenCodeAccounts[0].Name=="测试🥛" && cfg.OpenCodeAccounts[0].Key=="fixture-key","account fields");
        var dictionary=typeof(Config).GetMethod("ToDictionary", BindingFlags.NonPublic|BindingFlags.Instance).Invoke(cfg,null);
        var saved=Config.Parse(Write(dictionary));
        Check(saved.X==cfg.X && saved.Y==cfg.Y && saved.OpenCodeAccounts[0].AuthPath==cfg.OpenCodeAccounts[0].AuthPath,"config save roundtrip");
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
            Check(Equal(Read(encoded),legacy.DeserializeObject(encoded)),"legacy differential JSON");
        }
        System.Threading.Tasks.Parallel.For(0,1000,delegate(int i) { var value=Read(auth); string encoded=Write(value); if(!encoded.Contains("9007199254740993")) throw new Exception("concurrent JSON state"); });
        Check(true,"parallel parser and writer isolation");
        Console.WriteLine("PASS " + checks + " JSON/config regression checks (synthetic data only)");
    }
}
