// 내 메모장 — Windows 전용 프로그램
// 웹 앱(https://ereyang.github.io/my-memo/)을 창에 띄우고, 폭을 마음대로 줄이고, 항상 위에 띄울 수 있게 합니다.
// 빌드: build.cmd (Windows에 들어 있는 C# 컴파일러 + WebView2)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

static class Program
{
    public const string AppUrl = "https://ereyang.github.io/my-memo/";
    public static readonly int ShowMsg = Native.RegisterWindowMessage("MYMEMO_DESKTOP_SHOW");

    [STAThread]
    static void Main()
    {
        bool first;
        using (var mutex = new Mutex(true, "MyMemoDesktop_SingleInstance", out first))
        {
            if (!first)
            {
                // 이미 켜져 있으면 그 창을 앞으로
                Native.PostMessage((IntPtr)0xffff, ShowMsg, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            Application.ThreadException += (s, e) => LogError(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => LogError(e.ExceptionObject as Exception);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MemoApp());
        }
    }

    // 문제가 생기면 %LOCALAPPDATA%\MyMemo\error.log 에 기록
    public static void LogError(Exception ex)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyMemo");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), DateTime.Now.ToString("s") + " " + ex + Environment.NewLine);
        }
        catch { }
    }
}

// 창 여러 개 관리: 단축키는 숨은 창이 받고, 마지막 창이 닫히면 프로그램 종료
class MemoApp : ApplicationContext
{
    public static MemoApp Current;
    public readonly List<MemoForm> Forms = new List<MemoForm>();
    public System.Threading.Tasks.Task<CoreWebView2Environment> Env;
    readonly HotkeyWindow keys;

    public MemoApp()
    {
        Current = this;
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyMemo");
        Env = CoreWebView2Environment.CreateAsync(null, Path.Combine(dir, "WebView2"));
        keys = new HotkeyWindow(this);
        Open(null, true);
    }

    public MemoForm Main { get { return Forms.Count > 0 ? Forms[0] : null; } }

    public MemoForm Open(string pageId, bool main)
    {
        var f = new MemoForm(main, pageId);
        Forms.Add(f);
        f.FormClosed += delegate { Forms.Remove(f); if (Forms.Count == 0) { keys.Dispose(); ExitThread(); } };
        if (!main && Forms.Count > 1)
        {
            // 지금 앞에 있는 창 옆에 살짝 비켜서
            MemoForm from = Active() ?? Forms[0];
            Rectangle b = from.WindowState == FormWindowState.Normal ? from.Bounds : from.RestoreBounds;
            Rectangle wa = Screen.FromRectangle(b).WorkingArea;
            int x = b.X - b.Width - 8; if (x < wa.Left) x = Math.Min(b.X + 36, wa.Right - b.Width);
            f.Bounds = new Rectangle(x, Math.Min(b.Y + 24, wa.Bottom - b.Height), b.Width, b.Height);
            f.TopMost = from.TopMost;
        }
        f.Show();
        return f;
    }

    public MemoForm Active()
    {
        IntPtr fg = Native.GetForegroundWindow();
        foreach (var f in Forms) if (f.Handle == fg) return f;
        return null;
    }

    // Ctrl+Alt+M : 모든 창 보이기 / 숨기기
    public void ToggleAll()
    {
        bool anyFront = Active() != null;
        foreach (var f in Forms)
        {
            if (anyFront) f.WindowState = FormWindowState.Minimized;
            else f.BringUp();
        }
    }

    public void PinActive()
    {
        MemoForm f = Active() ?? Main;
        if (f != null) f.SetPin(!f.TopMost);
    }

    public void ShowMain() { foreach (var f in Forms) f.BringUp(); }
}

class HotkeyWindow : NativeWindow, IDisposable
{
    const int HK_SHOW = 1, HK_PIN = 2;
    readonly MemoApp app;
    public HotkeyWindow(MemoApp app)
    {
        this.app = app;
        CreateHandle(new CreateParams { Caption = "MyMemoHotkeys" });
        Native.RegisterHotKey(Handle, HK_SHOW, 0x0002 | 0x0001 | 0x4000, (int)Keys.M);   // Ctrl+Alt+M
        Native.RegisterHotKey(Handle, HK_PIN, 0x0002 | 0x0001 | 0x4000, (int)Keys.T);    // Ctrl+Alt+T
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312)
        {
            if ((int)m.WParam == HK_SHOW) app.ToggleAll();
            else if ((int)m.WParam == HK_PIN) app.PinActive();
            return;
        }
        if (m.Msg == Program.ShowMsg && Program.ShowMsg != 0) { app.ShowMain(); return; }
        base.WndProc(ref m);
    }
    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        Native.UnregisterHotKey(Handle, HK_SHOW); Native.UnregisterHotKey(Handle, HK_PIN);
        DestroyHandle();
    }
}

class MemoForm : Form
{
    const int SC_PIN = 0x1F0;   // 시스템 메뉴: 항상 위에
    const int SC_RESET = 0x1F1; // 시스템 메뉴: 크기 기본값
    const int SC_NEWWIN = 0x1F2; // 시스템 메뉴: 새 창
    readonly string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyMemo");
    readonly Dictionary<string, string> cfg = new Dictionary<string, string>();
    WebView2 web;
    readonly bool isMain;
    readonly string pageId;

    public MemoForm(bool main, string pageId)
    {
        isMain = main;
        this.pageId = pageId;
        Text = "내 메모장";
        MinimumSize = new Size(200, 160);
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.White;
        try
        {
            string png = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (File.Exists(png)) Icon = new Icon(png);
        }
        catch { }
        LoadCfg();
        if (isMain) { RestoreBoundsFromCfg(); TopMost = Get("top") == "1"; }
        else Size = new Size(420, 640);

        web = new WebView2();
        web.Dock = DockStyle.Fill;
        web.DefaultBackgroundColor = Color.White;
        Controls.Add(web);
        Load += OnLoad;
    }

    async void OnLoad(object sender, EventArgs e)
    {
        var sm = Native.GetSystemMenu(Handle, false);
        Native.AppendMenu(sm, 0x800, IntPtr.Zero, null);
        Native.AppendMenu(sm, 0, (IntPtr)SC_PIN, "항상 위에 표시\tCtrl+Alt+T");
        Native.AppendMenu(sm, 0, (IntPtr)SC_RESET, "창 크기 기본값으로");
        Native.AppendMenu(sm, 0, (IntPtr)SC_NEWWIN, "새 창 열기	Ctrl+Shift+N");
        UpdatePinMenu();


        try
        {
            var env = await MemoApp.Current.Env;
            await web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            MessageBox.Show("화면을 띄우는 구성 요소(WebView2)를 찾지 못했어요.\n\n" + ex.Message, "내 메모장", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }
        var core = web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsZoomControlEnabled = true;
        // 브라우저 단축키(Ctrl + - / = 확대·축소 등)는 끄고 메모장이 직접 처리:
        // 글자를 골라 두었으면 고른 글자 크기만, 아니면 메모장이 "zoom:" 메시지를 보내 창 전체 확대·축소
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.DocumentTitleChanged += delegate { string t = core.DocumentTitle; Text = string.IsNullOrEmpty(t) ? "내 메모장" : t; };
        core.NewWindowRequested += (s, a) => { a.Handled = true; OpenExternal(a.Uri); };
        core.NavigationStarting += (s, a) =>
        {
            if (!a.Uri.StartsWith(Program.AppUrl, StringComparison.OrdinalIgnoreCase) && !a.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            { a.Cancel = true; OpenExternal(a.Uri); }
        };
        core.WebMessageReceived += (s, a) =>
        {
            string m = a.TryGetWebMessageAsString();
            if (m == "pin") SetPin(!TopMost);
            else if (m == "ready") SendPin();
            else if (m == "zoom:+") web.ZoomFactor = Math.Min(5, Math.Round(web.ZoomFactor * 1.1, 2));
            else if (m == "zoom:-") web.ZoomFactor = Math.Max(0.25, Math.Round(web.ZoomFactor / 1.1, 2));
            else if (m == "zoom:0") web.ZoomFactor = 1;
            else if (m.StartsWith("newwin:")) MemoApp.Current.Open(m.Substring(7), false);
        };
        double z;
        if (double.TryParse(Get("zoom"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out z) && z >= 0.25 && z <= 5) web.ZoomFactor = z;
        string id = pageId ?? "";
        core.Navigate(Program.AppUrl + (id.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9]+$") ? "#" + id : ""));
    }

    void OpenExternal(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return;
        if (!(uri.StartsWith("http://") || uri.StartsWith("https://") || uri.StartsWith("mailto:"))) return;
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    public void SetPin(bool on)
    {
        TopMost = on;
        if (isMain) Set("top", on ? "1" : "0");
        UpdatePinMenu();
        SendPin();
    }

    void SendPin()
    {
        try { if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsString(TopMost ? "pin:1" : "pin:0"); } catch { }
    }

    void UpdatePinMenu()
    {
        Native.CheckMenuItem(Native.GetSystemMenu(Handle, false), SC_PIN, TopMost ? 0x8 : 0x0);
    }

    public void BringUp()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        Native.SetForegroundWindow(Handle);
        if (web != null) web.Focus();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0112) // WM_SYSCOMMAND
        {
            int id = (int)m.WParam & 0xFFF0;
            if ((int)m.WParam == SC_PIN) { SetPin(!TopMost); return; }
            if ((int)m.WParam == SC_RESET) { WindowState = FormWindowState.Normal; Size = new Size(420, 640); return; }
            if ((int)m.WParam == SC_NEWWIN) { MemoApp.Current.Open(null, false); return; }
        }
        if (m.Msg == Program.ShowMsg && Program.ShowMsg != 0) { MemoApp.Current.ShowMain(); return; }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (isMain)
        {
            Rectangle b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            Set("x", b.X.ToString()); Set("y", b.Y.ToString()); Set("w", b.Width.ToString()); Set("h", b.Height.ToString());
            Set("max", WindowState == FormWindowState.Maximized ? "1" : "0");
            try { if (web != null && web.CoreWebView2 != null) Set("zoom", web.ZoomFactor.ToString(System.Globalization.CultureInfo.InvariantCulture)); } catch { }
            SaveCfg();
        }
        base.OnFormClosing(e);
    }

    void RestoreBoundsFromCfg()
    {
        int x, y, w, h;
        Rectangle r = new Rectangle(0, 0, 420, 640);
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        r.X = wa.Right - r.Width - 40; r.Y = wa.Top + 60;
        if (int.TryParse(Get("x"), out x) && int.TryParse(Get("y"), out y) && int.TryParse(Get("w"), out w) && int.TryParse(Get("h"), out h))
        {
            var saved = new Rectangle(x, y, Math.Max(200, w), Math.Max(160, h));
            foreach (var s in Screen.AllScreens)
                if (s.WorkingArea.IntersectsWith(new Rectangle(saved.X + 40, saved.Y + 10, 40, 20))) { r = saved; break; }
        }
        Bounds = r;
        if (Get("max") == "1") WindowState = FormWindowState.Maximized;
    }

    string Get(string k) { string v; return cfg.TryGetValue(k, out v) ? v : ""; }
    void Set(string k, string v) { cfg[k] = v; SaveCfg(); }
    void LoadCfg()
    {
        try
        {
            string f = Path.Combine(dataDir, "settings.txt");
            if (!File.Exists(f)) return;
            foreach (var line in File.ReadAllLines(f))
            {
                int i = line.IndexOf('=');
                if (i > 0) cfg[line.Substring(0, i)] = line.Substring(i + 1);
            }
        }
        catch { }
    }
    void SaveCfg()
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            var lines = new List<string>();
            foreach (var kv in cfg) lines.Add(kv.Key + "=" + kv.Value);
            File.WriteAllLines(Path.Combine(dataDir, "settings.txt"), lines.ToArray());
        }
        catch { }
    }
}

static class Native
{
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, int mods, int vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr GetSystemMenu(IntPtr hWnd, bool revert);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenu(IntPtr menu, int flags, IntPtr id, string text);
    [DllImport("user32.dll")] public static extern int CheckMenuItem(IntPtr menu, int id, int flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
}
