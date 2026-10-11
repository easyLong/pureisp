using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

public sealed class LauncherTestForm : MainForm
{
    public LauncherTestForm(CoreService core, SystemProxyLease proxy, string diagnostics, Action<string> error)
        : base(core, proxy, false, false, diagnostics, error) { }

    public IntPtr SessionMessage(int id, bool confirmed)
    {
        Message message = Message.Create(Handle, id, confirmed ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero);
        base.WndProc(ref message);
        return message.Result;
    }

    public void OpenFromTray()
    {
        ContextMenuStrip menu = (ContextMenuStrip)typeof(MainForm).GetField("trayMenu", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(this);
        ((ToolStripMenuItem)menu.Items[0]).PerformClick();
    }
}

public static class RegressionTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    private static string PersistentSettings()
    {
        string result = "";
        using (RegistryKey internet = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
            foreach (string name in new[] { "ProxyEnable", "ProxyServer", "ProxyOverride", "AutoConfigURL" })
                result += name + "=" + Convert.ToString(internet.GetValue(name, "<absent>")) + "\n";
        foreach (string name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" })
            result += name + "=" + (Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) ?? "<absent>") + "\n";
        return result;
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("PUREISP_PROXY_TEST_CHILD") == "1")
        {
            foreach (string name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" })
                Console.WriteLine(Environment.GetEnvironmentVariable(name));
            return 0;
        }
        try
        {
            string before = PersistentSettings();
            string installA = Path.Combine(args[0], "custom install A", "Antigravity IDE.exe");
            string installB = Path.Combine(args[0], "other location", "Antigravity.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(installA));
            Directory.CreateDirectory(Path.GetDirectoryName(installB));
            File.WriteAllText(installA, "fixture, never execute");
            File.WriteAllText(installB, "fixture, never execute");
            string preference = Path.Combine(args[0], "settings", "ide-path.txt");
            IdeLocator locator = new IdeLocator(preference, delegate { return new[] { installA, installB }; });
            Check(locator.Resolve() == installA, "IDE discovery accepts custom installation paths with spaces");
            locator.Remember(installB);
            IdeLocator restarted = new IdeLocator(preference, delegate { return new[] { installA }; });
            Check(restarted.Resolve() == installB, "manual selection survives restart and takes precedence over discovery");
            locator.Remember(installA);
            Check(restarted.Resolve() == installA, "user can replace the saved IDE path");
            bool wrongFileRejected = false;
            try { locator.Remember(Process.GetCurrentProcess().MainModule.FileName); }
            catch (ArgumentException) { wrongFileRejected = true; }
            Check(wrongFileRejected && restarted.Resolve() == installA, "wrong executable cannot replace a valid preference");
            File.Delete(installA);
            Check(locator.Resolve() == installB, "removed installation falls back to discovery");
            File.Delete(installB);
            Check(locator.Resolve() == null, "missing IDE requests file selection instead of using a fixed drive");
            File.WriteAllText(installB, "fixture, never execute");
            object shortcutShell = null;
            object shortcut = null;
            string shortcutPath = Path.Combine(args[0], "Antigravity IDE.lnk");
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                shortcutShell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shortcutShell, new object[] { shortcutPath });
                shortcut.GetType().InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { installB });
                shortcut.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally
            {
                if (shortcut != null) Marshal.FinalReleaseComObject(shortcut);
                if (shortcutShell != null) Marshal.FinalReleaseComObject(shortcutShell);
            }
            Check(IdeLocator.ReadShortcutTarget(shortcutPath) == installB, "Windows shortcut resolves to the actual installation path");
            int installed = 0;
            foreach (string candidate in IdeLocator.DiscoverInstalled()) if (IdeLocator.IsIdeExecutable(candidate)) installed++;
            Console.WriteLine("INFO valid local discovery candidates: " + installed);
            ProxySettings actualSettings = WindowsSystemProxy.Read();
            Check(actualSettings != null, "WinINet queries real proxy settings without changing them");
            ProxySettings savedProxy = new ProxySettings { Flags = 13, Server = "old.proxy:8080", Bypass = "company.internal", AutoConfig = "http://company.internal/proxy.pac" };
            ProxySettings memoryProxy = savedProxy;
            string backup = Path.Combine(args[0], "proxy-test.backup");
            using (SystemProxyLease lease = new SystemProxyLease(delegate { return memoryProxy; }, delegate(ProxySettings value) { memoryProxy = value; }, backup))
            {
                lease.Enable();
                Check(memoryProxy.SameAs(SystemProxyLease.Desired()) && File.Exists(backup), "system proxy is enabled only after durable original-settings backup");
                lease.Enable();
                lease.Dispose();
                Check(memoryProxy.SameAs(savedProxy) && !File.Exists(backup), "stop restores flags, server, bypass and PAC");
            }
            using (SystemProxyLease lease = new SystemProxyLease(delegate { return memoryProxy; }, delegate(ProxySettings value) { memoryProxy = value; }, backup))
            {
                lease.Enable();
                ProxySettings external = new ProxySettings { Flags = 3, Server = "another.proxy:9999" };
                memoryProxy = external;
                lease.Dispose();
                Check(memoryProxy.SameAs(external), "stop preserves another application's later proxy change");
            }
            memoryProxy = savedProxy;
            SystemProxyLease crashed = new SystemProxyLease(delegate { return memoryProxy; }, delegate(ProxySettings value) { memoryProxy = value; }, backup);
            crashed.Enable();
            using (SystemProxyLease recovered = new SystemProxyLease(delegate { return memoryProxy; }, delegate(ProxySettings value) { memoryProxy = value; }, backup)) recovered.RecoverStale();
            Check(memoryProxy.SameAs(savedProxy) && !File.Exists(backup), "stale backup restores original settings on recovery");
            crashed.Dispose();
            using (SystemProxyLease failing = new SystemProxyLease(delegate { return memoryProxy; }, delegate(ProxySettings value) { memoryProxy = value; if (value.SameAs(SystemProxyLease.Desired())) throw new IOException("simulated set failure"); }, backup))
            {
                bool rejected = false;
                try { failing.Enable(); } catch (IOException) { rejected = true; }
                Check(rejected && memoryProxy.SameAs(savedProxy), "enable failure rolls back original proxy settings");
            }
            string[] names = { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" };
            string[] previous = Array.ConvertAll(names, Environment.GetEnvironmentVariable);
            string electronMode = Environment.GetEnvironmentVariable("ELECTRON_RUN_AS_NODE");
            try
            {
                Environment.SetEnvironmentVariable("HTTP_PROXY", "http://127.0.0.1:7897");
                Environment.SetEnvironmentVariable("HTTPS_PROXY", "http://127.0.0.1:7897");
                Environment.SetEnvironmentVariable("ALL_PROXY", "socks5://127.0.0.1:7897");
                Environment.SetEnvironmentVariable("NO_PROXY", "company.internal");
                Environment.SetEnvironmentVariable("ELECTRON_RUN_AS_NODE", "1");
                ProcessStartInfo ide = ApplicationProxy.CreateIdeStartInfo(Process.GetCurrentProcess().MainModule.FileName);
                Check(!ide.UseShellExecute, "IDE uses an explicit child environment");
                Check(ide.Arguments.Contains("127.0.0.1:7890"), "Chromium uses port 7890");
                Check(!ide.EnvironmentVariables.ContainsKey("ELECTRON_RUN_AS_NODE"), "IDE starts as an Electron app rather than Node");
                ide.EnvironmentVariables["PUREISP_PROXY_TEST_CHILD"] = "1";
                ide.RedirectStandardOutput = true;
                ide.CreateNoWindow = true;
                using (Process child = Process.Start(ide))
                {
                    string output = child.StandardOutput.ReadToEnd();
                    child.WaitForExit();
                    string[] lines = output.Trim().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                    Check(child.ExitCode == 0 && lines.Length == 4, "proxy probe child exits successfully");
                    for (int i = 0; i < 3; i++) Check(lines[i] == ApplicationProxy.Address, names[i] + " overrides stale 7897 only in child");
                    Check(lines[3].Contains("company.internal") && lines[3].Contains("localhost,127.0.0.1,::1"), "login loopback bypass preserves existing exclusions");
                }
                Check(Environment.GetEnvironmentVariable("HTTPS_PROXY") == "http://127.0.0.1:7897", "parent environment remains unchanged");
                ProcessStartInfo core = ApplicationProxy.CreateCoreStartInfo(args[0]);
                foreach (string name in names) Check(!core.EnvironmentVariables.ContainsKey(name), "core does not inherit " + name);
                TcpListener proxyReservation = new TcpListener(IPAddress.Loopback, 0);
                TcpListener controllerReservation = new TcpListener(IPAddress.Loopback, 0);
                proxyReservation.Start();
                controllerReservation.Start();
                int proxyPort = ((IPEndPoint)proxyReservation.LocalEndpoint).Port;
                int controllerPort = ((IPEndPoint)controllerReservation.LocalEndpoint).Port;
                string configPath = Path.Combine(args[0], "config.yaml");
                string config = File.ReadAllText(configPath)
                    .Replace("mixed-port: 7890", "mixed-port: " + proxyPort)
                    .Replace("external-controller: 127.0.0.1:9090", "external-controller: 127.0.0.1:" + controllerPort);
                File.WriteAllText(configPath, config);
                proxyReservation.Stop();
                controllerReservation.Stop();
                using (CoreService service = new CoreService(proxyPort, controllerPort))
                {
                    service.Start(args[0]);
                    Check(service.IsRunning && CoreService.PortOpen(proxyPort) && CoreService.PortOpen(controllerPort), "actual core starts and becomes ready");
                    using (CoreService second = new CoreService(proxyPort, controllerPort))
                    {
                        bool rejected = false;
                        try { second.Start(args[0]); } catch (InvalidOperationException) { rejected = true; }
                        Check(rejected && service.IsRunning, "port conflict leaves existing core running");
                    }
                    int reported = 0;
                    bool failRestore = true;
                    string formBackup = Path.Combine(args[0], "form-proxy.backup");
                    memoryProxy = savedProxy;
                    using (SystemProxyLease formLease = new SystemProxyLease(delegate { return memoryProxy; }, delegate(ProxySettings value) {
                        if (value.SameAs(savedProxy))
                        {
                            Check(service.IsRunning, "proxy restoration happens before stopping the owned core");
                            if (failRestore) throw new IOException("simulated restoration failure");
                        }
                        memoryProxy = value;
                    }, formBackup))
                    using (LauncherTestForm form = new LauncherTestForm(service, formLease, args[0], delegate { reported++; }))
                    {
                        formLease.Enable();
                        form.Show();
                        Check(form.Icon != null, "custom app icon loads from the embedded executable resource");
                        form.Close();
                        Check(!form.IsDisposed && !form.Visible && memoryProxy.SameAs(SystemProxyLease.Desired()) && service.IsRunning,
                            "window X hides the panel while keeping the proxy and actual core running");
                        form.OpenFromTray();
                        Check(form.Visible && form.ShowInTaskbar, "tray menu restores the panel");
                        form.WindowState = FormWindowState.Minimized;
                        Check(!form.Visible && service.IsRunning, "minimize keeps the actual proxy core running in the background");
                        form.RequestExit();
                        Check(!form.IsDisposed && form.Visible && reported == 1 && File.Exists(formBackup) && service.IsRunning,
                            "failed explicit exit preserves backup and core, restores the panel and reports the error");
                        failRestore = false;
                        form.RequestExit();
                        Check(form.IsDisposed && memoryProxy.SameAs(savedProxy) && !File.Exists(formBackup),
                            "explicit stop restores the original proxy and exits successfully on retry");
                    }
                    Check(!service.IsRunning && !CoreService.PortOpen(proxyPort) && !CoreService.PortOpen(controllerPort), "stop closes only the owned core");
                    service.Dispose();
                    Check(!service.IsRunning, "repeated cleanup is safe");
                }
                using (CoreService shutdownCore = new CoreService(proxyPort, controllerPort))
                {
                    shutdownCore.Start(args[0]);
                    int restores = 0;
                    bool failRestore = true;
                    int messages = 0;
                    string shutdownBackup = Path.Combine(args[0], "shutdown-proxy.backup");
                    memoryProxy = savedProxy;
                    using (SystemProxyLease shutdownLease = new SystemProxyLease(delegate { return memoryProxy; }, delegate(ProxySettings value) {
                        if (value.SameAs(savedProxy))
                        {
                            Check(shutdownCore.IsRunning, "shutdown restores proxy before stopping its core");
                            if (failRestore) throw new IOException("simulated shutdown restore failure");
                            restores++;
                        }
                        memoryProxy = value;
                    }, shutdownBackup))
                    using (LauncherTestForm form = new LauncherTestForm(shutdownCore, shutdownLease, args[0], delegate { messages++; }))
                    {
                        shutdownLease.Enable();
                        Check(form.SessionMessage(0x0011, true) == new IntPtr(1) && shutdownCore.IsRunning && restores == 0,
                            "shutdown query returns approval without stopping the proxy");
                        form.SessionMessage(0x0016, false);
                        Check(shutdownCore.IsRunning && memoryProxy.SameAs(SystemProxyLease.Desired()),
                            "cancelled shutdown keeps the original active session working");
                        form.SessionMessage(0x0016, true);
                        Check(messages == 0 && File.Exists(shutdownBackup) && File.Exists(Path.Combine(args[0], "shutdown-error.log")),
                            "shutdown restore failure retains backup and logs without a modal dialog");
                        failRestore = false;
                        form.SessionMessage(0x0016, true);
                        form.SessionMessage(0x0016, true);
                        Check(restores == 1 && memoryProxy.SameAs(savedProxy) && !File.Exists(shutdownBackup) && !shutdownCore.IsRunning,
                            "confirmed shutdown restores once and stops only the owned core");
                    }
                    for (int attempt = 0; attempt < 25 && (CoreService.PortOpen(proxyPort) || CoreService.PortOpen(controllerPort)); attempt++)
                        System.Threading.Thread.Sleep(20);
                    Check(!CoreService.PortOpen(proxyPort) && !CoreService.PortOpen(controllerPort), "shutdown core releases its actual ports");
                }
            }
            finally
            {
                for (int i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], previous[i]);
                Environment.SetEnvironmentVariable("ELECTRON_RUN_AS_NODE", electronMode);
            }
            Check(before == PersistentSettings(), "regression tests leave actual proxy and user environment unchanged");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex.Message); return 1; }
    }
}
