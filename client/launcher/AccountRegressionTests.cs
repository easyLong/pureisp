using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

public static class AccountRegressionTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
    private static object Field(object target, string name, Type owner = null)
    {
        return (owner ?? target.GetType()).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    }
    private static void Set(object target, string name, object value)
    {
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    }
    private static void Pump(Func<bool> until)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(15);
        while (!until())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("Account operation timed out");
            Application.DoEvents(); Thread.Sleep(15);
        }
        Application.DoEvents();
    }
    private static int FreePort()
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    private static CoreService Core(AccountForm form) { return (CoreService)Field(form, "service", typeof(MainForm)); }
    private static void Click(AccountForm form, string name) { ((Button)Field(form, name)).PerformClick(); }
    private static void Login(AccountForm form, Dictionary<string, object> info)
    {
        ((TextBox)Field(form, "licenseKey")).Text = (string)info["licenseKey"];
        Click(form, "start"); Pump(delegate { return !(bool)Field(form, "busy"); });
        Check(Core(form).IsRunning, "valid login starts actual Mihomo" + (Core(form).IsRunning ? "" : ": " + ((Label)Field(form, "status", typeof(MainForm))).Text));
    }
    private static void License(Dictionary<string, object> info, bool enabled, DateTime expires)
    {
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create((string)info["serviceUrl"] + "/v1/admin/cards/" + (string)info["cardId"] + "/license");
        request.Proxy = null; request.Method = "PUT"; request.ContentType = "application/json";
        request.Headers["X-Admin-Key"] = (string)info["adminKey"];
        byte[] data = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new { enabled = enabled, expiresAt = expires.ToUniversalTime().ToString("o") }));
        using (Stream output = request.GetRequestStream()) output.Write(data, 0, data.Length);
        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse()) Check(response.StatusCode == HttpStatusCode.OK, "account management update succeeds");
    }
    private static string Persistent()
    {
        ProxySettings p = WindowsSystemProxy.Read();
        string value = p.Flags + "|" + p.Server + "|" + p.Bypass + "|" + p.AutoConfig;
        foreach (string name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" }) value += "|" + Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        return value;
    }
    [STAThread]
    public static int Main(string[] args)
    {
        string before = Persistent();
        AccountForm form = null;
        try
        {
            Check(AccountTime.BeijingExpiry("2027-10-11T02:08:52.444Z") == "2027-10-11 10:08:52", "customer expiry shows Beijing time with seconds");
            Check(AccountTime.BeijingExpiry("2027-10-11T02:08:52-07:00") == "2027-10-11 17:08:52", "expiry display converts explicit offsets independently of the computer timezone");
            string directory = args[0], stateDirectory = Path.Combine(directory, "client-state");
            var info = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(directory, "fixture.json")));
            int proxyPort = FreePort(), controllerPort = FreePort();
            while (controllerPort == proxyPort) controllerPort = FreePort();
            string yaml = "mixed-port: " + proxyPort + "\nallow-lan: false\nexternal-controller: 127.0.0.1:" + controllerPort + "\nmode: rule\nproxies: []\nrules:\n  - MATCH,DIRECT\n";
            File.WriteAllText(Path.Combine(directory, "config.yaml"), yaml);
            ProxySettings saved = new ProxySettings { Flags = 9, Server = "previous:8080", Bypass = "*.example", AutoConfig = "https://example.com/proxy.pac" };
            ProxySettings current = saved; bool failRestore = false; int restoreCount = 0, errors = 0;
            SystemProxyLease lease = new SystemProxyLease(delegate { return current; }, delegate(ProxySettings value)
            {
                if (value.SameAs(saved))
                {
                    Check(form != null && Core(form).IsRunning, "account logout restores proxy before stopping the core");
                    if (failRestore) throw new IOException("test restore failure");
                    restoreCount++;
                }
                current = value;
            }, Path.Combine(directory, "proxy.backup"));
            Application.EnableVisualStyles();
            form = new AccountForm(lease, false, stateDirectory, directory, delegate { return new CoreService(proxyPort, controllerPort); }, delegate { errors++; });
            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-2000, -2000); form.Show(); Application.DoEvents();
            Check(!Core(form).IsRunning && current.SameAs(saved), "unlogged client neither starts proxy nor changes network");
            Check(!form.Controls.Contains((Control)Field(form, "openWeb", typeof(MainForm))) && !(bool)Field(form, "openWebWhenReady", typeof(MainForm)), "customer UI has no technical web console or automatic browser launch");
            Button switchNetwork = (Button)Field(form, "refreshTransit", typeof(MainForm));
            Check(form.Controls.Contains(switchNetwork) && switchNetwork.Text == "切换网络" && !switchNetwork.Enabled, "customer has a single network switch button, disabled before authorization");
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(directory, "account-login.png")); }
            ((TextBox)Field(form, "licenseKey")).Text = "PUREISP-00000000-00000000-00000000-00000000";
            Click(form, "start"); Pump(delegate { return !(bool)Field(form, "busy"); });
            Check(!Core(form).IsRunning && current.SameAs(saved), "invalid card does not start proxy or change network");
            Login(form, info);
            Check(current.SameAs(SystemProxyLease.Desired()), "proxy is enabled after authorization and core readiness");
            Check(((TextBox)Field(form, "licenseKey")).UseSystemPasswordChar, "remembered card is masked in customer UI");
            byte[] cacheBytes = File.ReadAllBytes(Path.Combine(stateDirectory, "license-session.bin"));
            Check(!Encoding.UTF8.GetString(cacheBytes).Contains((string)info["licenseKey"]), "saved session is protected by Windows DPAPI");
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(directory, "account-connected.png")); }
            Check(switchNetwork.Enabled, "network switch is enabled only after authorization and core readiness");
            foreach (Control control in form.Controls)
                if (control.Visible) Check(form.ClientRectangle.Contains(control.Bounds), "visible customer controls fit within the panel: " + control.GetType().Name);
            switchNetwork.PerformClick();
            Pump(delegate { return !(bool)Field(form, "selectingTransit", typeof(MainForm)); });
            Check(((Label)Field(form, "transitInfo", typeof(MainForm))).Text.Contains("配置不完整") && Core(form).IsRunning && current.SameAs(SystemProxyLease.Desired()), "missing transit group shows a user-facing error without stopping proxy or altering network settings");
            DateTime changedExpiry = DateTime.UtcNow.AddDays(40);
            License(info, true, changedExpiry); Click(form, "start"); Pump(delegate { return !(bool)Field(form, "busy"); });
            Check(DateTime.Parse(((AccountSession)Field(form, "session")).expiresAt).ToUniversalTime() > DateTime.UtcNow.AddDays(39), "manual refresh sees renewal immediately without relogin");
            form.RequestExit(); form.Dispose();
            form = new AccountForm(lease, false, stateDirectory, directory, delegate { return new CoreService(proxyPort, controllerPort); }, delegate { errors++; });
            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-2000, -2000); form.Show(); Application.DoEvents();
            Check(!Core(form).IsRunning && current.SameAs(saved) && ((TextBox)Field(form, "licenseKey")).Text == (string)info["licenseKey"], "reopening remembers the card and waits for an explicit Start");
            Set(form, "api", new AccountApi("http://127.0.0.1:" + FreePort()));
            ((TextBox)Field(form, "licenseKey")).Text = "PUREISP-00000000-00000000-00000000-00000000";
            Click(form, "start"); Pump(delegate { return !(bool)Field(form, "busy"); });
            Check(!Core(form).IsRunning && current.SameAs(saved), "another card cannot borrow cached authorization during an outage");
            Login(form, info);
            Check(Core(form).IsRunning, "remembered valid card supports offline Start until the original deadline");
            Set(form, "api", new AccountApi((string)info["serviceUrl"]));
            failRestore = true; form.GetType().GetMethod("EndSession", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { "Test stop" });
            Check(errors == 1 && Core(form).IsRunning && File.Exists(Path.Combine(directory, "proxy.backup")), "restore failure keeps core and backup for safe retry");
            failRestore = false; form.GetType().GetMethod("EndSession", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { "Test stop" });
            Check(!Core(form).IsRunning && current.SameAs(saved) && !File.Exists(Path.Combine(stateDirectory, "license-session.bin")), "logout stops owned core and clears login state");
            Login(form, info);
            License(info, false, changedExpiry); Click(form, "start"); Pump(delegate { return !(bool)Field(form, "busy"); });
            Check(Field(form, "session") == null && !Core(form).IsRunning, "server disable signs out an existing session on refresh");
            License(info, true, changedExpiry); Login(form, info);
            Set(form, "api", new AccountApi("http://127.0.0.1:" + FreePort()));
            Click(form, "start"); Pump(delegate { return !(bool)Field(form, "busy"); });
            Check(Core(form).IsRunning && Field(form, "session") != null, "account-service outage retains an existing unexpired authorization");
            AccountSession session = (AccountSession)Field(form, "session");
            session.expiresAt = DateTime.UtcNow.AddSeconds(-5).ToString("o");
            session.validUntil = DateTime.UtcNow.AddDays(1).ToString("o"); // Cached by the previous grace-period client.
            form.CheckAuthorization(); Pump(delegate { return Field(form, "session") == null && !Core(form).IsRunning; });
            Check(current.SameAs(saved), "paid expiry is enforced offline even when an old cache contains an extra day");
            Check(File.ReadAllText(Path.Combine(directory, "config.yaml")) == yaml, "license operations never rewrite or remove proxy configuration");
            AccountSession clockSession = new AccountSession { hint = "test", token = "test", status = "active", serverTime = DateTime.UtcNow.ToString("o"), expiresAt = DateTime.UtcNow.AddDays(1).ToString("o"), validUntil = DateTime.UtcNow.AddDays(2).ToString("o"), savedAtUtcTicks = DateTime.UtcNow.Ticks, lastSeenUtcTicks = DateTime.UtcNow.Ticks };
            bool clockRejected = false; try { clockSession.EffectiveTime(DateTime.UtcNow.AddMinutes(-10)); } catch (InvalidDataException) { clockRejected = true; }
            Check(clockRejected, "cached authorization requires online verification after clock rollback");
            Check(restoreCount == 4, "exit, logout, disable and offline expiry each restore network settings");
            form.RequestExit();
            Check(before == Persistent(), "account tests leave actual system proxy and user environment unchanged");
            Console.WriteLine("ARTIFACTS " + directory); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex.Message); return 1; }
        finally { if (form != null && !form.IsDisposed) { form.RequestExit(); form.Dispose(); } }
    }
}
