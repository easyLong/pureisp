using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

sealed class FakeController : IDisposable
{
    public const string USSlow = "🇺🇸 西美-E";
    public const string USFast = "US-A (E&F)";
    public const string JPSlow = "🇯🇵东京-E";
    public const string JPFast = "Japan B";
    public const string HK = "🇭🇰香港-E";
    public const string SG = "狮城-F";
    public readonly string[] Names = { USSlow, USFast, JPSlow, JPFast, HK, SG, "自动选择", "DIRECT", "住宅节点" };
    public readonly Dictionary<string, object> Delays = new Dictionary<string, object>();
    public readonly List<string> Tested = new List<string>();
    public string Current = USSlow;
    public int Writes;
    public bool RejectWrite, IgnoreWrite, MissingGroup;
    public int ProbePause;
    public readonly ManualResetEvent ProbeStarted = new ManualResetEvent(false);
    public int Port;
    private readonly HttpListener listener = new HttpListener();
    private readonly Thread thread;
    private readonly object gate = new object();

    public FakeController()
    {
        TcpListener reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start(); Port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        listener.Prefixes.Add("http://127.0.0.1:" + Port + "/"); listener.Start();
        thread = new Thread(delegate()
        {
            while (listener.IsListening)
            {
                try { HttpListenerContext context = listener.GetContext(); ThreadPool.QueueUserWorkItem(delegate { Handle(context); }); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
            }
        });
        thread.IsBackground = true; thread.Start();
    }

    private void Handle(HttpListenerContext context)
    {
        try
        {
            string path = context.Request.Url.AbsolutePath;
            if (path == "/proxies")
            {
                Dictionary<string, object> proxies = new Dictionary<string, object>();
                foreach (string name in Names) proxies[name] = new { type = "Shadowsocks", history = new[] { new { delay = 1 } } };
                proxies["自动选择"] = new { type = "URLTest", all = new[] { USSlow, USFast } };
                proxies["DIRECT"] = new { type = "Direct" };
                proxies["住宅节点"] = new { type = "Http", history = new[] { new { delay = 1 } } };
                if (!MissingGroup) proxies[TransitSelector.Group] = new { type = "Selector", now = Current, all = new[] { USSlow, USFast, JPSlow, JPFast, HK, SG, "自动选择", "DIRECT" } };
                Send(context, 200, new { proxies = proxies });
            }
            else if (path.EndsWith("/delay"))
            {
                string name = Uri.UnescapeDataString(path.Substring(9, path.Length - 9 - 6));
                object delay;
                lock (gate) { Tested.Add(name); Delays.TryGetValue(name, out delay); }
                ProbeStarted.Set();
                if (ProbePause > 0) Thread.Sleep(ProbePause);
                if (context.Request.QueryString["timeout"] != "2500" || String.IsNullOrEmpty(context.Request.QueryString["url"])) Send(context, 400, new { error = "missing probe parameters" });
                else if (delay == null) Send(context, 503, new { error = "test timeout" });
                else Send(context, 200, new { delay = delay });
            }
            else if (Uri.UnescapeDataString(path) == "/proxies/" + TransitSelector.Group)
            {
                if (context.Request.HttpMethod == "PUT")
                {
                    if (RejectWrite) { Send(context, 503, new { error = "test rejection" }); return; }
                    using (StreamReader reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                    {
                        var body = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                        if (!IgnoreWrite) Current = (string)body["name"];
                        Writes++;
                    }
                    Send(context, 204, null);
                }
                else Send(context, 200, new { now = Current });
            }
            else Send(context, 404, new { error = "unknown endpoint" });
        }
        catch { try { context.Response.Abort(); } catch { } }
    }
    private static void Send(HttpListenerContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        if (body != null)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body));
            context.Response.ContentType = "application/json"; context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        context.Response.Close();
    }
    public void Reset() { lock (gate) { Tested.Clear(); Delays.Clear(); } Writes = 0; RejectWrite = IgnoreWrite = MissingGroup = false; }
    public void Dispose() { listener.Close(); thread.Join(1000); }
}

// A local HTTP proxy responds to probe requests without contacting the internet.
sealed class ProbeProxy : IDisposable
{
    private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    private readonly Thread thread;
    public readonly int Port;
    private static string ReadHeaders(NetworkStream stream)
    {
        StringBuilder request = new StringBuilder();
        while (request.Length < 8192)
        {
            int next = stream.ReadByte(); if (next < 0) return "";
            request.Append((char)next);
            if (request.Length >= 4 && request.ToString(request.Length - 4, 4) == "\r\n\r\n") return request.ToString();
        }
        return "";
    }
    public ProbeProxy(int latency)
    {
        listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        thread = new Thread(delegate()
        {
            while (true)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); } catch (SocketException) { return; }
                ThreadPool.QueueUserWorkItem(delegate
                {
                    using (client)
                    {
                        try
                        {
                            client.ReceiveTimeout = 3000;
                            using (NetworkStream stream = client.GetStream())
                            {
                                string headers = ReadHeaders(stream);
                                if (headers.StartsWith("CONNECT "))
                                {
                                    byte[] connected = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                                    stream.Write(connected, 0, connected.Length);
                                    headers = ReadHeaders(stream);
                                }
                                if (headers.Length == 0) return;
                                Thread.Sleep(latency);
                                byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n");
                                stream.Write(response, 0, response.Length);
                            }
                        }
                        catch (IOException) { }
                    }
                });
            }
        });
        thread.IsBackground = true; thread.Start();
    }
    public void Dispose() { listener.Stop(); thread.Join(1000); }
}

public static class TransitRegressionTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
    private static int FreePort()
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }

    private static void ActualCore(string directory)
    {
        using (ProbeProxy slow = new ProbeProxy(180))
        using (ProbeProxy fast = new ProbeProxy(20))
        using (ProbeProxy japan = new ProbeProxy(5))
        {
            int proxyPort = FreePort(), controllerPort = FreePort();
            while (controllerPort == proxyPort) controllerPort = FreePort();
            string config = "mixed-port: " + proxyPort + "\nallow-lan: false\nexternal-controller: 127.0.0.1:" + controllerPort + "\nmode: rule\nproxies:\n" +
                "  - {name: '美国慢线', type: http, server: 127.0.0.1, port: " + slow.Port + "}\n" +
                "  - {name: '美国快线', type: http, server: 127.0.0.1, port: " + fast.Port + "}\n" +
                "  - {name: '日本快线', type: http, server: 127.0.0.1, port: " + japan.Port + "}\n" +
                "proxy-groups:\n  - name: '" + TransitSelector.Group + "'\n    type: select\n    proxies: ['美国慢线', '美国快线', '日本快线']\n" +
                "  - name: 'OpenAI-test'\n    type: select\n    proxies: ['美国慢线', '美国快线']\nrules:\n  - MATCH,DIRECT\n";
            string path = Path.Combine(directory, "config.yaml");
            File.WriteAllText(path, config, new UTF8Encoding(false));
            using (CoreService core = new CoreService(proxyPort, controllerPort))
            {
                core.Start(directory);
                TransitSelector selector = new TransitSelector(controllerPort);
                TransitSelection result = selector.Refresh(CancellationToken.None, null);
                Check(result.Name == "美国快线" && result.Region == "美国", "real Mihomo performs fresh probes and picks fastest US ahead of faster Japan");
                selector.Apply(result, CancellationToken.None);
                Check(core.IsRunning && File.ReadAllText(path) == config, "real Mihomo confirms route switch without a core restart or config rewrite");
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + controllerPort + "/proxies/OpenAI-test");
                request.Proxy = null;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    var group = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                    Check((string)group["now"] == "美国慢线", "network switching preserves the independent OpenAI routing group");
                }
            }
            Check(!CoreService.PortOpen(proxyPort) && !CoreService.PortOpen(controllerPort), "real core test cleans up only its isolated ports");
        }
    }

    public static int Main(string[] args)
    {
        try
        {
            ThreadPool.SetMinThreads(8, 8);
            using (FakeController api = new FakeController())
            {
                TransitSelector selector = new TransitSelector(api.Port);
                api.Delays[FakeController.USSlow] = 160; api.Delays[FakeController.USFast] = 45;
                api.Delays[FakeController.JPFast] = 5; api.Delays[FakeController.HK] = 1;
                TransitSelection result = selector.Refresh(CancellationToken.None, null);
                Check(result.Name == FakeController.USFast && result.Delay == 45 && result.Region == "美国", "fresh measurements pick fastest US even when Japan/global are faster: " + result.Name + " / " + result.Delay + " / probes=" + String.Join(",", api.Tested));
                Check(api.Tested.Count == 2 && !api.Tested.Contains(FakeController.JPFast), "US success skips later regions and ignores historical delays");
                selector.Apply(result, CancellationToken.None);
                Check(api.Current == FakeController.USFast && api.Writes == 1, "encoded Unicode/group names and special node names switch via PUT and read back");

                api.Reset(); api.Delays[FakeController.JPSlow] = 120; api.Delays[FakeController.JPFast] = 65; api.Delays[FakeController.HK] = 1;
                result = selector.Refresh(CancellationToken.None, null);
                Check(result.Name == FakeController.JPFast && result.Region == "日本" && api.Tested.Count == 4, "all US failures fall back to fastest Japan before global");

                api.Reset(); api.Delays[FakeController.USFast] = 0; api.Delays[FakeController.JPFast] = -1;
                api.Delays[FakeController.HK] = 25; api.Delays[FakeController.SG] = 50;
                result = selector.Refresh(CancellationToken.None, null);
                Check(result.Name == FakeController.HK && result.Region == "全局" && api.Tested.Count == 6, "US and Japan failures choose best remaining front relay without re-testing failures");
                Check(!api.Tested.Contains("住宅节点") && !api.Tested.Contains("DIRECT") && !api.Tested.Contains("自动选择"), "global fallback excludes residential, DIRECT and nested groups");

                api.Reset(); api.Delays[FakeController.USFast] = "1"; api.Delays[FakeController.JPFast] = 2501;
                string unchanged = api.Current; bool failed = false;
                try { selector.Refresh(CancellationToken.None, null); } catch (InvalidOperationException) { failed = true; }
                Check(failed && api.Writes == 0 && api.Current == unchanged, "all failures or invalid measurements retain current route without writes");

                api.Reset(); api.RejectWrite = true; failed = false;
                try { selector.Apply(result, CancellationToken.None); } catch (WebException) { failed = true; }
                Check(failed && api.Current == unchanged, "failed switch is never reported as success");
                api.Reset(); api.IgnoreWrite = true; failed = false;
                try { selector.Apply(result, CancellationToken.None); } catch (InvalidOperationException) { failed = true; }
                Check(failed, "successful HTTP response still requires selected-node confirmation");

                api.Reset(); api.MissingGroup = true; failed = false;
                try { selector.Refresh(CancellationToken.None, null); } catch (InvalidOperationException) { failed = true; }
                Check(failed && api.Writes == 0, "incomplete group fails safely");

                api.Reset();
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    cancel.Cancel(); failed = false;
                    try { selector.Refresh(cancel.Token, null); } catch (OperationCanceledException) { failed = true; }
                    try { selector.Apply(result, cancel.Token); } catch (OperationCanceledException) { }
                    Check(failed && api.Tested.Count == 0 && api.Writes == 0, "cancelled operations cannot probe or switch");
                }
                api.Reset(); api.ProbePause = 250; api.ProbeStarted.Reset();
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    Exception interrupted = null;
                    Thread worker = new Thread(delegate()
                    {
                        try { selector.Refresh(cancel.Token, null); } catch (Exception error) { interrupted = error; }
                    });
                    worker.IsBackground = true; worker.Start();
                    Check(api.ProbeStarted.WaitOne(3000), "cancellation test reaches an in-flight measurement");
                    cancel.Cancel();
                    Check(worker.Join(3000) && interrupted != null && api.Writes == 0, "cancelling in-flight requests returns promptly without switching");
                }
            }
            ActualCore(args[0]);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + error); return 1; }
    }
}
