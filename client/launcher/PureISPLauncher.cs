using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

#if PAID_EDITION
[assembly: System.Reflection.AssemblyVersion("1.1.4.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.4.0")]
#else
[assembly: System.Reflection.AssemblyVersion("1.0.4.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.4.0")]
#endif

// Proxy settings belong to the explicitly launched application, never HKCU/HKLM.
public static class ApplicationProxy
{
    public const string Address = "http://127.0.0.1:7890";

    public static ProcessStartInfo CreateIdeStartInfo(string executable)
    {
        ProcessStartInfo info = new ProcessStartInfo(executable);
        info.WorkingDirectory = Path.GetDirectoryName(executable);
        info.UseShellExecute = false;
        // A developer terminal may be running under Electron's Node mode.
        info.EnvironmentVariables.Remove("ELECTRON_RUN_AS_NODE");
        info.Arguments = "--proxy-server=\"" + Address + "\" --proxy-bypass-list=\"localhost;127.0.0.1;[::1]\"";
        info.EnvironmentVariables["HTTP_PROXY"] = Address;
        info.EnvironmentVariables["HTTPS_PROXY"] = Address;
        info.EnvironmentVariables["ALL_PROXY"] = Address;
        // Login callbacks must stay local. These changes only affect this child.
        string bypass = info.EnvironmentVariables["NO_PROXY"];
        info.EnvironmentVariables["NO_PROXY"] = String.IsNullOrEmpty(bypass)
            ? "localhost,127.0.0.1,::1"
            : bypass + ",localhost,127.0.0.1,::1";
        return info;
    }

    public static ProcessStartInfo CreateCoreStartInfo(string directory)
    {
        return CreateCoreStartInfo(directory, Path.Combine(directory, "config.yaml"));
    }

    public static ProcessStartInfo CreateCoreStartInfo(string directory, string configPath)
    {
        ProcessStartInfo info = new ProcessStartInfo(Path.Combine(directory, "PureISP-Core.exe"));
        info.WorkingDirectory = directory;
        info.Arguments = "-d \"" + directory + "\" -f \"" + configPath + "\"";
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.WindowStyle = ProcessWindowStyle.Hidden;
        // Avoid inheriting stale upstream proxies or recursively proxying through itself.
        foreach (string name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" })
            info.EnvironmentVariables.Remove(name);
        return info;
    }
}

public sealed class CoreService : IDisposable
{
    private readonly int proxyPort;
    private readonly int controllerPort;
    private Process ownedProcess;
    private string runtimeConfigPath;
    private bool disposed;
    private readonly object gate = new object();

    public CoreService() : this(7890, 9090) { }

    public CoreService(int proxyPort, int controllerPort)
    {
        this.proxyPort = proxyPort;
        this.controllerPort = controllerPort;
    }

    public bool IsRunning
    {
        get { lock (gate) { return ownedProcess != null && !ownedProcess.HasExited; } }
    }

    public int ControllerPort { get { return controllerPort; } }

    public static bool PortOpen(int port)
    {
        using (TcpClient client = new TcpClient())
        {
            try
            {
                IAsyncResult pending = client.BeginConnect("127.0.0.1", port, null, null);
                using (WaitHandle wait = pending.AsyncWaitHandle)
                {
                    if (!wait.WaitOne(150)) return false;
                    client.EndConnect(pending);
                    return true;
                }
            }
            catch (SocketException) { return false; }
        }
    }

    public void Start(string directory)
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException("CoreService");
            if (ownedProcess != null) throw new InvalidOperationException("内核已经启动。");
            if (!File.Exists(Path.Combine(directory, "PureISP-Core.exe")))
                throw new FileNotFoundException("缺少 PureISP-Core.exe，请完整解压客户端。");
#if PROTECTED_CONFIG
            string configPath = ProtectedConfig.Prepare(directory);
#else
            string configPath = Path.Combine(directory, "config.yaml");
            if (!File.Exists(configPath)) throw new FileNotFoundException("缺少 config.yaml，请完整解压客户端。");
#endif
            if (PortOpen(proxyPort) || PortOpen(controllerPort))
            {
#if PROTECTED_CONFIG
                ProtectedConfig.Delete(configPath);
#endif
                throw new InvalidOperationException(proxyPort + " 或 " + controllerPort + " 端口已被使用。请先退出原来的 PureISP；其他代理程序不会被自动关闭。");
            }
            runtimeConfigPath = configPath;
            ownedProcess = Process.Start(ApplicationProxy.CreateCoreStartInfo(directory, configPath));
        }
        try
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                if (!IsRunning) throw new InvalidOperationException("内核启动失败。请检查配置文件和端口占用。");
                if (PortOpen(proxyPort) && ControllerReady()) return;
                Thread.Sleep(100);
            }
            throw new TimeoutException("等待代理就绪超时，请检查配置文件。");
        }
        catch { Dispose(); throw; }
    }

    private bool ControllerReady()
    {
        try
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + controllerPort + "/version");
            request.Proxy = null;
            request.Timeout = 300;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                return response.StatusCode == HttpStatusCode.OK;
        }
        catch (WebException) { return false; }
    }

    public void Dispose()
    {
        Stop(true);
    }

    // Shutdown restores the proxy first and does not wait for the core to exit.
    public void Stop(bool waitForExit)
    {
        lock (gate)
        {
            disposed = true;
            if (ownedProcess == null)
            {
#if PROTECTED_CONFIG
                ProtectedConfig.Delete(runtimeConfigPath);
                runtimeConfigPath = null;
#endif
                return;
            }
            try
            {
                if (!ownedProcess.HasExited)
                {
                    ownedProcess.Kill();
                    if (waitForExit) ownedProcess.WaitForExit(3000);
                }
            }
            finally
            {
                ownedProcess.Dispose(); ownedProcess = null;
#if PROTECTED_CONFIG
                ProtectedConfig.Delete(runtimeConfigPath);
#endif
                runtimeConfigPath = null;
            }
        }
    }
}

public class MainForm : Form
{
    protected CoreService service;
    protected readonly SystemProxyLease systemProxy;
    protected readonly string directory;
    protected bool openWebWhenReady = true;
    protected readonly Label status = new Label();
    protected readonly Button openWeb = new Button();
    protected readonly Button launchIde = new Button();
    protected readonly Button refreshTransit = new Button();
    protected readonly Label transitInfo = new Label();
    protected bool selectingTransit;
    private CancellationTokenSource transitCancellation;
    private readonly IdeLocator ideLocator = IdeLocator.ForCurrentUser();
    private readonly Label idePath = new Label();
    private readonly ToolTip pathTip = new ToolTip();
    private readonly NotifyIcon tray = new NotifyIcon();
    private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
    protected readonly ToolStripMenuItem trayWeb = new ToolStripMenuItem("打开 Web 控制台");
    protected readonly ToolStripMenuItem trayIde = new ToolStripMenuItem("启动 Antigravity IDE");
    private readonly bool trayEnabled;
    private readonly string diagnosticDirectory;
    private readonly Action<string> reportError;
    private bool trayNoticeShown;
    private bool exitRequested;
    protected volatile bool closing;

    public MainForm() : this(new CoreService(), SystemProxyLease.ForCurrentUser(), true, true) { }

    // Isolated tests use a simulated proxy store and never change the user's network.
    public MainForm(CoreService service, SystemProxyLease systemProxy, bool startServices, bool trayEnabled,
        string diagnosticDirectory = null, Action<string> reportError = null, string runtimeDirectory = null)
    {
        this.service = service;
        this.directory = runtimeDirectory ?? AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        this.systemProxy = systemProxy;
        this.trayEnabled = trayEnabled;
        this.diagnosticDirectory = diagnosticDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PureISP");
        this.reportError = reportError ?? delegate(string text) { MessageBox.Show(this, text, "PureISP"); };
        using (Stream iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("PureISP.Icon"))
        using (Icon embedded = new Icon(iconStream)) Icon = (Icon)embedded.Clone();
        Text = "PureISP 1.0.4 — 双轨分流";
        ClientSize = new Size(480, 494);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(24, 29, 39);
        ForeColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 10);

        Label title = new Label { Text = "PureISP 纯净静态住宅专线", Bounds = new Rectangle(24, 22, 430, 32), Font = new Font(Font.FontFamily, 15, FontStyle.Bold) };
        status.Bounds = new Rectangle(24, 68, 430, 48);
        status.Text = "正在启动代理内核…";
        Label description = new Label { Text = "浏览器自动使用双轨分流，IDE 从面板启动。\n✕ 收进托盘保持代理；停止并退出恢复原设置。", Bounds = new Rectangle(24, 118, 430, 48) };
        ConfigureButton(refreshTransit, "切换网络", 176);
        transitInfo.Bounds = new Rectangle(24, 216, 432, 32);
        transitInfo.Text = "连接后可点击切换网络，自动选择可用线路。";
        transitInfo.AutoEllipsis = true;
        ConfigureButton(openWeb, "打开 Web 控制台", 252);
        ConfigureButton(launchIde, "启动 Antigravity IDE（独立代理）", 298);
        Button chooseIde = new Button();
        ConfigureButton(chooseIde, "选择 / 更换 IDE 路径", 344);
        idePath.Text = "IDE 安装位置：自动查找，也可手动选择。";
        idePath.Bounds = new Rectangle(24, 386, 432, 36);
        idePath.AutoEllipsis = true;
        Button stop = new Button();
        ConfigureButton(stop, "停止并退出", 430);
        openWeb.Enabled = launchIde.Enabled = refreshTransit.Enabled = false;
        refreshTransit.Click += delegate { RefreshTransit(); };
        openWeb.Click += delegate { OpenWeb(); };
        launchIde.Click += delegate { LaunchIde(); };
        chooseIde.Click += delegate { ChooseIde(); };
        stop.Click += delegate { RequestExit(); };
        Controls.AddRange(new Control[] { title, status, description, refreshTransit, transitInfo, openWeb, launchIde, chooseIde, idePath, stop });
        if (startServices) Shown += delegate { StartServices(); };
        tray.Icon = Icon;
        tray.Text = "PureISP · 双轨分流";
        trayMenu.Items.Add("打开 PureISP 面板", null, delegate { ShowPanel(); });
        trayMenu.Items.Add(trayWeb);
        trayMenu.Items.Add(trayIde);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("停止并退出", null, delegate { RequestExit(); });
        trayWeb.Enabled = trayIde.Enabled = false;
        trayWeb.Click += delegate { OpenWeb(); };
        trayIde.Click += delegate { LaunchIde(); };
        tray.ContextMenuStrip = trayMenu;
        tray.DoubleClick += delegate { ShowPanel(); };
        tray.Visible = trayEnabled;
        Resize += delegate { if (WindowState == FormWindowState.Minimized && !closing) HideToTray(); };
        Application.ApplicationExit += OnApplicationExit;
    }

    public void RequestExit()
    {
        exitRequested = true;
        Close();
    }

    private void ShowPanel()
    {
        if (closing || IsDisposed) return;
        WindowState = FormWindowState.Normal;
        ShowInTaskbar = true;
        Show();
        Activate();
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        if (trayEnabled && !trayNoticeShown)
        {
            trayNoticeShown = true;
            tray.ShowBalloonTip(2500, "PureISP 仍在运行", "代理继续工作。双击托盘图标打开面板；右键可停止并退出。", ToolTipIcon.Info);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs args)
    {
        if (args.CloseReason == CloseReason.UserClosing && !exitRequested)
        {
            args.Cancel = true;
            HideToTray();
            base.OnFormClosing(args);
            return;
        }
        closing = true;
        bool shutdown = args.CloseReason == CloseReason.WindowsShutDown;
        if (!StopServices(shutdown) && args.CloseReason == CloseReason.UserClosing)
        {
            args.Cancel = true;
            closing = exitRequested = false;
            ShowPanel();
        }
        base.OnFormClosing(args);
    }

    protected override void WndProc(ref Message message)
    {
        // QUERY is only a question. Another app or the user can still cancel shutdown.
        if (message.Msg == 0x0011) { message.Result = new IntPtr(1); return; }
        if (message.Msg == 0x0016)
        {
            if (message.WParam != IntPtr.Zero)
            {
                closing = exitRequested = true;
                StopServices(true);
            }
            message.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref message);
    }

    protected bool StopServices(bool shutdown)
    {
        CancelTransitRefresh();
        try
        {
            systemProxy.Dispose();
            service.Stop(!shutdown);
            if (!closing && !IsDisposed)
            {
                transitInfo.Text = "连接后可点击切换网络，自动选择可用线路。";
                UpdateTransitButton();
            }
            return true;
        }
        catch (Exception ex)
        {
            // Keep the backup on restoration failure; never show a shutdown modal.
            try
            {
                Directory.CreateDirectory(diagnosticDirectory);
                File.AppendAllText(Path.Combine(diagnosticDirectory, "shutdown-error.log"), DateTime.Now.ToString("s") + " " + ex.Message + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (!shutdown) reportError("停止失败，原设置备份已保留，请重试：" + ex.Message);
            return false;
        }
    }

    private void OnApplicationExit(object sender, EventArgs args)
    {
        closing = true;
        StopServices(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelTransitRefresh();
            Application.ApplicationExit -= OnApplicationExit;
            tray.Visible = false;
            tray.Dispose();
            trayMenu.Dispose();
            pathTip.Dispose();
            if (Icon != null) { Icon.Dispose(); Icon = null; }
        }
        base.Dispose(disposing);
    }

    private void ConfigureButton(Button button, string text, int y)
    {
        button.Text = text;
        button.Bounds = new Rectangle(24, y, 432, 36);
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Color.FromArgb(37, 47, 63);
        button.ForeColor = Color.White;
    }

    protected virtual void StartServices()
    {
        Task.Factory.StartNew(delegate
        {
            if (closing) return;
            string error = null;
            try
            {
                systemProxy.RecoverStale();
                service.Start(directory);
            }
            catch (Exception ex) { error = ex.Message; }
            if (closing || IsDisposed) { service.Stop(false); return; }
            try
            {
                BeginInvoke((Action)delegate
                {
                    if (closing) return;
                    if (error != null) { status.Text = error; status.ForeColor = Color.LightCoral; OnStartFinished(false); return; }
                    try { systemProxy.Enable(); }
                    catch (Exception ex) { service.Dispose(); status.Text = "系统代理启用失败：" + ex.Message; status.ForeColor = Color.LightCoral; OnStartFinished(false); return; }
                    status.Text = "双轨分流已就绪：127.0.0.1:7890\n浏览器系统代理已启用，退出恢复原设置。";
                    status.ForeColor = Color.LightGreen;
                    openWeb.Enabled = launchIde.Enabled = true;
                    trayWeb.Enabled = trayIde.Enabled = true;
                    UpdateTransitButton();
                    if (openWebWhenReady) OpenWeb();
                    OnStartFinished(true);
                });
            }
            catch (InvalidOperationException) { }
        });
    }

    protected virtual void OnStartFinished(bool success) { }

    protected virtual bool CanRefreshTransit { get { return !closing && service.IsRunning; } }

    protected void UpdateTransitButton()
    {
        refreshTransit.Enabled = CanRefreshTransit && !selectingTransit;
        refreshTransit.Text = selectingTransit ? "正在切换…" : "切换网络";
    }

    private void CancelTransitRefresh()
    {
        if (transitCancellation != null)
            try { transitCancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void ShowTransitProgress(string text, CancellationToken token)
    {
        if (closing || IsDisposed || token.IsCancellationRequested) return;
        try { BeginInvoke((Action)delegate
        {
            if (closing || token.IsCancellationRequested) return;
            transitInfo.ForeColor = Color.FromArgb(166, 185, 207);
            transitInfo.Text = "正在检测可用线路，请稍候…";
        }); } catch (InvalidOperationException) { }
    }

    private void RefreshTransit()
    {
        if (!CanRefreshTransit || selectingTransit) return;
        CoreService currentCore = service;
        CancellationTokenSource cancellation = new CancellationTokenSource();
        transitCancellation = cancellation;
        selectingTransit = true;
        UpdateTransitButton();
        transitInfo.ForeColor = Color.FromArgb(166, 185, 207);
        transitInfo.Text = "正在切换网络，请稍候…";
        Task.Factory.StartNew(delegate
        {
            TransitSelection result = null;
            string error = null;
            try
            {
                TransitSelector selector = new TransitSelector(currentCore.ControllerPort);
                result = selector.Refresh(cancellation.Token, delegate(string text) { ShowTransitProgress(text, cancellation.Token); });
                cancellation.Token.ThrowIfCancellationRequested();
                if (!Object.ReferenceEquals(currentCore, service) || !currentCore.IsRunning) throw new OperationCanceledException();
                selector.Apply(result, cancellation.Token);
            }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException ex) { error = ex.Message; }
            catch (Exception) { error = "网络切换失败，请确认已连接后重试。"; }
            if (closing || IsDisposed) { cancellation.Dispose(); return; }
            try { BeginInvoke((Action)delegate
            {
                bool cancelled = cancellation.IsCancellationRequested || !Object.ReferenceEquals(currentCore, service) || !currentCore.IsRunning;
                selectingTransit = false;
                transitCancellation = null;
                if (!cancelled)
                {
                    transitInfo.ForeColor = error == null && result != null ? Color.LightGreen : Color.LightCoral;
                    transitInfo.Text = error ?? (result == null ? "网络切换已取消。" : "网络已切换 · 延迟 " + result.Delay + " ms");
                    pathTip.SetToolTip(transitInfo, transitInfo.Text);
                }
                else transitInfo.Text = "网络切换已取消，连接后可重新尝试。";
                cancellation.Dispose();
                UpdateTransitButton();
            }); } catch (InvalidOperationException) { cancellation.Dispose(); }
        });
    }

    private void OpenWeb()
    {
        try { Process.Start("http://127.0.0.1:9090/ui/"); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "打开控制台失败"); }
    }

    private void LaunchIde()
    {
        if (!service.IsRunning)
        {
            MessageBox.Show(this, "代理内核已停止，请重新启动 PureISP。", "PureISP");
            return;
        }
        string executable;
        try { executable = ideLocator.Resolve(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "查找 IDE 失败，请手动选择路径"); return; }
        if (executable == null) executable = ChooseIde();
        if (executable == null) return;
        ShowIdePath(executable);
        // Electron reuses an existing process and ignores a new child's environment.
        Process[] existing = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable));
        bool alreadyRunning = existing.Length > 0;
        foreach (Process process in existing) process.Dispose();
        if (alreadyRunning)
        {
            MessageBox.Show(this, "请先保存工作并完整退出 Antigravity IDE，再从此按钮启动。已运行的 IDE 无法更换继承的代理环境。", "PureISP");
            return;
        }
        try { Process.Start(ApplicationProxy.CreateIdeStartInfo(executable)); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "启动 IDE 失败"); }
    }

    private string ChooseIde()
    {
        using (OpenFileDialog dialog = new OpenFileDialog {
            Title = "选择 Antigravity IDE 的可执行文件",
            Filter = "Antigravity IDE|Antigravity IDE.exe;Antigravity.exe",
            CheckFileExists = true })
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return null;
            try
            {
                ideLocator.Remember(dialog.FileName);
                ShowIdePath(dialog.FileName);
                return dialog.FileName;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "保存 IDE 路径失败"); return null; }
        }
    }

    private void ShowIdePath(string executable)
    {
        idePath.Text = "IDE：" + executable;
        pathTip.SetToolTip(idePath, executable);
    }

    [STAThread]
    public static void Main()
    {
        string directory = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).ToUpperInvariant();
        string id;
        using (SHA256 hash = SHA256.Create()) id = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(directory))).Replace("-", "");
        bool created;
        using (Mutex instance = new Mutex(true, @"Local\PureISP-" + id, out created))
        {
            if (!created) { MessageBox.Show("此目录的 PureISP 已经运行。", "PureISP"); return; }
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
            finally { instance.ReleaseMutex(); }
        }
    }
}
