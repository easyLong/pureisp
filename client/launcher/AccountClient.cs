using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Drawing;

public sealed class AccountSettings
{
    public string serviceUrl;
    public string renewalUrl;
    public static void ValidateUrl(string value)
    {
        Uri uri;
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || !String.IsNullOrEmpty(uri.UserInfo) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
            throw new InvalidDataException("授权服务必须使用 HTTPS；本机测试允许 HTTP。");
    }
}

public sealed class AccountSession
{
    public string id, hint, label, licenseKey, token, status, expiresAt, serverTime, validUntil, renewalUrl;
    public long savedAtUtcTicks, lastSeenUtcTicks;
    public bool cached;
    public DateTime EffectiveTime(DateTime localNow)
    {
        if (savedAtUtcTicks <= 0 || lastSeenUtcTicks <= 0 || localNow.Ticks < lastSeenUtcTicks - TimeSpan.FromMinutes(5).Ticks)
            throw new InvalidDataException("电脑时间发生变化，请联网重新验证授权。");
        DateTime server = DateTime.Parse(serverTime, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
        long elapsed = Math.Max(0, localNow.Ticks - savedAtUtcTicks);
        return server.AddTicks(elapsed);
    }
    public bool IsUsable(DateTime effectiveNow)
    {
        DateTime expiry, deadline;
        return status == "active" && !String.IsNullOrEmpty(token) &&
            DateTime.TryParse(expiresAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out expiry) &&
            DateTime.TryParse(validUntil, null, System.Globalization.DateTimeStyles.RoundtripKind, out deadline) &&
            effectiveNow < expiry.ToUniversalTime() &&
            effectiveNow < deadline.ToUniversalTime();
    }
}

public static class AccountTime
{
    public static string BeijingExpiry(string value)
    {
        return DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
            .ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
    }
}

public sealed class AccountApiException : Exception
{
    public int StatusCode { get; private set; }
    public AccountApiException(int status, string message) : base(message) { StatusCode = status; }
}

public sealed class AccountApi
{
    private readonly string address;
    private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
    public AccountApi(string address) { AccountSettings.ValidateUrl(address); this.address = address.TrimEnd('/'); }
    public AccountSession Request(string path, string token = null, object body = null)
    {
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(address + path);
        request.Proxy = null; // Login must work before the paid proxy exists.
        request.AllowAutoRedirect = false;
        request.Timeout = request.ReadWriteTimeout = 10000;
        request.Method = body == null ? "GET" : "POST";
        request.Accept = "application/json";
        if (!String.IsNullOrEmpty(token)) request.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
        HttpWebResponse response;
        try
        {
            if (body != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json.Serialize(body));
                request.ContentType = "application/json";
                request.ContentLength = bytes.Length;
                using (Stream output = request.GetRequestStream()) output.Write(bytes, 0, bytes.Length);
            }
            response = (HttpWebResponse)request.GetResponse();
        }
        catch (WebException ex)
        {
            response = ex.Response as HttpWebResponse;
            if (response == null) throw new AccountApiException(503, "授权服务暂时无法连接。");
        }
        using (response)
        {
            string text;
            using (Stream input = response.GetResponseStream())
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] block = new byte[8192]; int count;
                while ((count = input.Read(block, 0, block.Length)) != 0)
                {
                    if (buffer.Length + count > 1024 * 1024) throw new InvalidDataException("授权服务响应过大。");
                    buffer.Write(block, 0, count);
                }
                text = Encoding.UTF8.GetString(buffer.ToArray());
            }
            if ((int)response.StatusCode != 200 && (int)response.StatusCode != 201)
            {
                string message = "授权验证失败，请稍后重试。";
                try { var error = json.Deserialize<Dictionary<string, object>>(text); if (error.ContainsKey("message")) message = Convert.ToString(error["message"]); } catch (ArgumentException) { }
                throw new AccountApiException((int)response.StatusCode, message);
            }
            AccountSession result = json.Deserialize<AccountSession>(text);
            if (result == null) throw new InvalidDataException("授权服务响应无效。");
            return result;
        }
    }
}

public sealed class AccountSessionStore
{
    private readonly string path;
    private readonly byte[] entropy;
    private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
    public AccountSessionStore(string path, string serviceUrl)
    {
        this.path = path;
        using (SHA256 hash = SHA256.Create()) entropy = hash.ComputeHash(Encoding.UTF8.GetBytes(serviceUrl));
    }
    public AccountSession Load()
    {
        if (!File.Exists(path)) return null;
        byte[] data = ProtectedData.Unprotect(File.ReadAllBytes(path), entropy, DataProtectionScope.CurrentUser);
        return json.Deserialize<AccountSession>(Encoding.UTF8.GetString(data));
    }
    public void Save(AccountSession value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(json.Serialize(value)), entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path + ".tmp", data);
        if (File.Exists(path)) File.Replace(path + ".tmp", path, null); else File.Move(path + ".tmp", path);
    }
    public void Clear() { if (File.Exists(path)) File.Delete(path); }
}

public sealed class AccountForm : MainForm
{
    private readonly TextBox licenseKey = new TextBox();
    private readonly Button start = new Button();
    private readonly Label accountInfo = new Label(), licenseLabel = new Label();
    private Button chooseButton, stopButton;
    private Label idePathLabel;
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    private readonly Stopwatch clock = new Stopwatch();
    private AccountApi api;
    private AccountSettings settings;
    private AccountSessionStore cache;
    private AccountSession session;
    private DateTime anchor;
    private DateTime nextRefresh = DateTime.MinValue;
    private bool busy;
    private readonly string stateDirectory;
    private readonly Func<CoreService> coreFactory;

    public AccountForm() : this(SystemProxyLease.ForCurrentUser(), true,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PureISP", "license")) { }
    public AccountForm(SystemProxyLease lease, bool trayEnabled, string stateDirectory, string runtimeDirectory = null,
        Func<CoreService> coreFactory = null, Action<string> reportError = null)
        : base(coreFactory == null ? new CoreService() : coreFactory(), lease, false, trayEnabled, stateDirectory, reportError, runtimeDirectory)
    {
        this.stateDirectory = stateDirectory;
        this.coreFactory = coreFactory ?? delegate { return new CoreService(); };
        Text = "PureISP 1.1.4 — 授权版";
        ClientSize = new Size(480, 546);
        openWebWhenReady = false;
        Controls.Remove(openWeb); trayWeb.Visible = false;
        foreach (Control control in Controls)
        {
            if (control.Text.StartsWith("浏览器自动")) control.Visible = false;
            if (control is Button && control.Text.StartsWith("选择 /")) { control.Top = 388; chooseButton = (Button)control; }
            if (control.Text.StartsWith("IDE 安装位置")) { control.Top = 430; idePathLabel = (Label)control; }
            if (control.Text == "停止并退出") { control.Top = 482; stopButton = (Button)control; }
        }
        status.Bounds = new Rectangle(24, 196, 432, 56);
        status.Text = "填入授权卡密，点击启动即可使用。";
        refreshTransit.Top = 258;
        transitInfo.Bounds = new Rectangle(24, 300, 432, 36);
        launchIde.Top = 344;
        licenseLabel.Text = "授权卡密"; licenseLabel.Bounds = new Rectangle(24, 68, 110, 24);
        licenseKey.Bounds = new Rectangle(24, 96, 332, 30); licenseKey.MaxLength = 100;
        licenseKey.UseSystemPasswordChar = true;
        Place(start, "启动", 368, 94, 88);
        accountInfo.Bounds = new Rectangle(24, 146, 432, 44);
        accountInfo.Text = "未启动 · 填入卡密即可";
        Controls.AddRange(new Control[] { licenseLabel, licenseKey, start, accountInfo });
        start.Click += delegate { ActivateAndConnect(); };
        AcceptButton = start;
        timer.Interval = 30000;
        timer.Tick += delegate { CheckAuthorization(); };
        Shown += delegate { InitializeAccount(); };
        ToggleButtons();
    }
    private void Place(Button button, string text, int x, int y, int width)
    {
        button.Text = text; button.Bounds = new Rectangle(x, y, width, 36);
        button.FlatStyle = FlatStyle.Flat; button.BackColor = Color.FromArgb(37, 47, 63);
    }
    private void InitializeAccount()
    {
        try
        {
            systemProxy.RecoverStale();
            settings = new JavaScriptSerializer().Deserialize<AccountSettings>(File.ReadAllText(Path.Combine(directory, "account-service.json")));
            AccountSettings.ValidateUrl(settings.serviceUrl); AccountSettings.ValidateUrl(settings.renewalUrl);
            api = new AccountApi(settings.serviceUrl);
            Directory.CreateDirectory(stateDirectory);
            cache = new AccountSessionStore(Path.Combine(stateDirectory, "license-session.bin"), settings.serviceUrl);
            try { session = cache.Load(); } catch (CryptographicException) { cache.Clear(); } catch (ArgumentException) { cache.Clear(); }
            ToggleButtons(); timer.Start();
            if (session != null) { licenseKey.Text = session.licenseKey; accountInfo.Text = "已记住卡密 · 点击启动验证授权"; }
        }
        catch (Exception) { status.Text = "授权服务尚未配置或网络恢复失败。\n请联系管理员检查部署设置。"; api = null; ToggleButtons(); }
    }
    private void ToggleButtons()
    {
        start.Enabled = api != null && !busy;
        start.Text = busy ? "验证中…" : "启动";
        licenseKey.Enabled = !busy && !service.IsRunning;
        launchIde.Enabled = trayIde.Enabled = !busy && service.IsRunning;
        chooseButton.Enabled = !busy;
        UpdateTransitButton();
    }
    protected override bool CanRefreshTransit
    {
        get { return base.CanRefreshTransit && !busy && session != null && clock.IsRunning && session.IsUsable(anchor.Add(clock.Elapsed)); }
    }
    private static string NormalizeKey(string value) { return (value ?? "").Trim().ToUpperInvariant(); }
    private void ActivateAndConnect()
    {
        string key = NormalizeKey(licenseKey.Text);
        if (key.Length == 0) { status.Text = "请输入授权卡密。"; return; }
        if (service.IsRunning) { VerifyAndConnect(); return; }
        bool sameCard = session != null && NormalizeKey(session.licenseKey) == key;
        Run(delegate { return api.Request("/v1/activate", null, new { licenseKey = key }); }, delegate(AccountSession result)
        {
            result.licenseKey = key;
            Accept(result); Connect();
        }, sameCard);
    }
    private void VerifyAndConnect()
    {
        if (busy || session == null) return;
        AccountSession previous = session;
        Run(delegate
        {
            AccountSession result = api.Request("/v1/me", previous.token);
            return result;
        }, delegate(AccountSession result)
        {
            result.licenseKey = previous.licenseKey;
            Accept(result);
            if (!service.IsRunning) Connect();
            else { busy = false; ShowAccount(); status.Text = result.cached ? "已连接；授权服务使用已验证的授权快照。" : "已连接，有效期已更新。"; ToggleButtons(); }
        }, true);
    }
    private void Run(Func<AccountSession> action, Action<AccountSession> done, bool allowOffline)
    {
        if (busy || closing) return;
        busy = true; ToggleButtons(); status.ForeColor = Color.FromArgb(166, 185, 207); status.Text = "正在验证授权…";
        Task.Factory.StartNew(delegate
        {
            AccountSession result = null; Exception error = null;
            try { result = action(); } catch (Exception ex) { error = ex; }
            if (closing || IsDisposed) return;
            try { BeginInvoke((Action)delegate
            {
                if (closing) return;
                if (error == null)
                {
                    try { done(result); } catch (Exception) { busy = false; EndSession("授权信息无效，请重新验证卡密。"); }
                    return;
                }
                busy = false;
                AccountApiException apiError = error as AccountApiException;
                if (allowOffline && apiError != null && apiError.StatusCode >= 500 && TryOffline()) return;
                if (allowOffline) EndSession(apiError == null ? "授权验证失败，请重新验证卡密。" : apiError.Message);
                else { status.Text = apiError == null ? "连接失败，请稍后重试。" : apiError.Message; ToggleButtons(); }
            }); } catch (InvalidOperationException) { }
        });
    }
    private void Accept(AccountSession result)
    {
        if (result == null || String.IsNullOrEmpty(result.token)) throw new InvalidDataException();
        DateTime serverNow = DateTime.Parse(result.serverTime, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
        if (!result.IsUsable(serverNow)) throw new InvalidDataException();
        result.savedAtUtcTicks = result.lastSeenUtcTicks = DateTime.UtcNow.Ticks;
        session = result; anchor = serverNow; clock.Restart();
        cache.Save(session); nextRefresh = DateTime.UtcNow.AddHours(24); ShowAccount();
    }
    private bool TryOffline()
    {
        try
        {
            DateTime effective = session.EffectiveTime(DateTime.UtcNow);
            if (clock.IsRunning && anchor.Add(clock.Elapsed) > effective) effective = anchor.Add(clock.Elapsed);
            if (!session.IsUsable(effective)) return false;
            anchor = effective; clock.Restart();
            nextRefresh = DateTime.UtcNow.AddMinutes(5);
            ShowAccount();
            if (!service.IsRunning) { busy = true; Connect(); }
            else { status.Text = "已连接；暂时无法同步授权，沿用已有有效授权。"; ToggleButtons(); }
            return true;
        }
        catch (Exception) { return false; }
    }
    private void Connect()
    {
        if (closing || session == null || !session.IsUsable(anchor.Add(clock.Elapsed))) throw new InvalidDataException();
        service.Dispose(); service = coreFactory();
        StartServices();
    }
    protected override void OnStartFinished(bool success)
    {
        busy = false;
        if (success) status.Text = "已连接。浏览器自动接入，IDE 请从下面启动。";
        else status.Text = "连接失败，请点击“启动”重试。";
        ShowAccount(); ToggleButtons();
    }
    private void ShowAccount()
    {
        if (session == null) return;
        accountInfo.Text = "已授权 " + session.hint + "\n有效期至 " + AccountTime.BeijingExpiry(session.expiresAt) + "（北京时间）";
    }
    public void CheckAuthorization()
    {
        if (closing || session == null || !clock.IsRunning) return;
        try
        {
            DateTime effective = anchor.Add(clock.Elapsed);
            if (!session.IsUsable(effective)) { if (!busy) VerifyAndConnect(); return; }
            session.lastSeenUtcTicks = Math.Max(session.lastSeenUtcTicks, DateTime.UtcNow.Ticks);
            cache.Save(session);
            if (!busy && DateTime.UtcNow >= nextRefresh) VerifyAndConnect();
        }
        catch (Exception) { if (!busy) EndSession("授权已到期或无法验证，请联系管理员续费后点击启动。"); }
    }
    private bool EndSession(string message)
    {
        // Never kill the local core before restoring the browser's original network settings.
        if (!StopServices(false)) { status.Text = "网络设置恢复失败，请点击“停止并退出”重试。"; ToggleButtons(); return false; }
        session = null; clock.Reset();
        try { if (cache != null) cache.Clear(); }
        catch (IOException) { message += "\n本地授权缓存清除失败，请联系管理员处理。"; }
        catch (UnauthorizedAccessException) { message += "\n本地授权缓存无法写入，请联系管理员处理。"; }
        accountInfo.Text = "未授权 · 请联系管理员续费";
        status.Text = message; status.ForeColor = Color.LightCoral;
        Show(); WindowState = FormWindowState.Normal; ShowInTaskbar = true;
        ToggleButtons(); return true;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Stop(); timer.Dispose(); }
        base.Dispose(disposing);
    }
}

public static class AccountEntryPoint
{
    [STAThread]
    public static void Main()
    {
        bool created;
        using (Mutex instance = new Mutex(true, @"Local\PureISP-Account", out created))
        {
            if (!created) { MessageBox.Show("PureISP 授权版已在运行。", "PureISP"); return; }
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new AccountForm());
        }
    }
}
