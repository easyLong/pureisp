using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

public sealed class TransitSelection
{
    public string Name;
    public string Region;
    public int Delay;
}

// Measures fresh delays through the local core; no account server or YAML changes.
public sealed class TransitSelector
{
    public const string Group = "🛡️ 住宅前置中继";
    private readonly string address;
    private const string TestUrl = "http://cp.cloudflare.com/generate_204";
    private const int ProbeTimeout = 2500;

    public TransitSelector(int port) { address = "http://127.0.0.1:" + port; }

    private Dictionary<string, object> Request(string path, string method, object body, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(address + path);
        request.Proxy = null;
        request.Method = method;
        request.Timeout = request.ReadWriteTimeout = ProbeTimeout + 1500;
        request.KeepAlive = false;
        request.ServicePoint.ConnectionLimit = Math.Max(request.ServicePoint.ConnectionLimit, 4);
        using (token.Register(delegate { request.Abort(); }))
        {
            if (body != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body));
                request.ContentType = "application/json";
                request.ContentLength = bytes.Length;
                using (Stream stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            }
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                token.ThrowIfCancellationRequested();
                string text = reader.ReadToEnd();
                if (String.IsNullOrWhiteSpace(text)) return new Dictionary<string, object>();
                Dictionary<string, object> result = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
                if (result == null) throw new InvalidDataException("内核返回的数据格式无效。");
                return result;
            }
        }
    }

    private static string RegionOf(string name)
    {
        if (Regex.IsMatch(name, "🇺🇸|美国|西美|美西|美东|洛杉矶|纽约|United[ _-]*States|(?:^|[^A-Z])US(?:[^A-Z]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "美国";
        if (Regex.IsMatch(name, "🇯🇵|日本|东京|大阪|名古屋|Japan|(?:^|[^A-Z])JP(?:[^A-Z]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "日本";
        return "全局";
    }

    private List<string> Candidates(CancellationToken token)
    {
        Dictionary<string, object> response = Request("/proxies", "GET", null, token);
        object value;
        Dictionary<string, object> proxies = response.TryGetValue("proxies", out value) ? value as Dictionary<string, object> : null;
        Dictionary<string, object> group = proxies != null && proxies.TryGetValue(Group, out value) ? value as Dictionary<string, object> : null;
        object[] names = group != null && group.TryGetValue("all", out value) ? value as object[] : null;
        if (names == null) throw new InvalidOperationException("网络线路配置不完整，请联系管理员更新客户端。");
        List<string> result = new List<string>();
        foreach (object item in names)
        {
            string name = item as string;
            Dictionary<string, object> proxy = name != null && proxies.TryGetValue(name, out value) ? value as Dictionary<string, object> : null;
            if (proxy == null || proxy.ContainsKey("all") || result.Contains(name)) continue;
            string type = proxy.TryGetValue("type", out value) ? Convert.ToString(value) : "";
            if (String.Equals(type, "Direct", StringComparison.OrdinalIgnoreCase) || String.Equals(type, "Reject", StringComparison.OrdinalIgnoreCase) || String.Equals(type, "Pass", StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(name);
        }
        // Only permitted front relays participate, preventing residential dialer loops.
        return result;
    }

    private TransitSelection Best(List<string> names, string region, CancellationToken token)
    {
        List<TransitSelection> measured = new List<TransitSelection>();
        object gate = new object();
        Parallel.ForEach(names, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, delegate(string name)
        {
            try
            {
                Dictionary<string, object> response = Request("/proxies/" + Uri.EscapeDataString(name) + "/delay?timeout=" + ProbeTimeout + "&url=" + Uri.EscapeDataString(TestUrl), "GET", null, token);
                object value;
                int delay;
                if (!response.TryGetValue("delay", out value) || !(value is int) || !Int32.TryParse(Convert.ToString(value), out delay) || delay <= 0 || delay > ProbeTimeout) return;
                lock (gate) measured.Add(new TransitSelection { Name = name, Region = region, Delay = delay });
            }
            catch (WebException error) { if (error.Response != null) error.Response.Close(); token.ThrowIfCancellationRequested(); }
            catch (InvalidDataException) { token.ThrowIfCancellationRequested(); }
            catch (ArgumentException) { token.ThrowIfCancellationRequested(); }
        });
        measured.Sort(delegate(TransitSelection left, TransitSelection right)
        {
            int difference = left.Delay.CompareTo(right.Delay);
            return difference != 0 ? difference : StringComparer.Ordinal.Compare(left.Name, right.Name);
        });
        return measured.Count == 0 ? null : measured[0];
    }

    public TransitSelection Refresh(CancellationToken token, Action<string> progress)
    {
        List<string> names = Candidates(token);
        foreach (string region in new[] { "美国", "日本", "全局" })
        {
            List<string> candidates = names.FindAll(delegate(string name) { return RegionOf(name) == region; });
            if (candidates.Count == 0) continue;
            if (progress != null) progress("正在测速：" + region + "前置中继（" + candidates.Count + " 个）…");
            TransitSelection result = Best(candidates, region, token);
            if (result != null) return result;
        }
        throw new InvalidOperationException("暂时没有可用线路，已保留当前网络，请稍后重试。");
    }

    public void Apply(TransitSelection selection, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string path = "/proxies/" + Uri.EscapeDataString(Group);
        Request(path, "PUT", new { name = selection.Name }, token);
        object value;
        Dictionary<string, object> current = Request(path, "GET", null, token);
        if (!current.TryGetValue("now", out value) || Convert.ToString(value) != selection.Name)
            throw new InvalidOperationException("网络切换未确认，请稍后重试。");
    }
}
