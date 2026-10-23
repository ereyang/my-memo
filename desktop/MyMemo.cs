// 내 메모장 — Windows 전용 프로그램
// 웹 앱(https://ereyang.github.io/my-memo/)을 창에 띄우고, 폭을 마음대로 줄이고, 항상 위에 띄울 수 있게 합니다.
// 빌드: build.cmd (Windows에 들어 있는 C# 컴파일러 + WebView2)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
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
        // 화면 배율(125%·150%)에서 Windows 가 흐리게 늘이지 않도록 고해상도 지원을 알림
        try { Native.SetProcessDpiAwarenessContext((IntPtr)(-4)); } catch { }
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
// 열린 창 목록은 바뀔 때마다 저장해 두었다가 다음에 켤 때 그대로 다시 엶
class MemoApp : ApplicationContext
{
    public static MemoApp Current;
    public readonly List<MemoForm> Forms = new List<MemoForm>();
    public System.Threading.Tasks.Task<CoreWebView2Environment> Env;
    readonly HotkeyWindow keys;
    readonly System.Windows.Forms.Timer saveTimer = new System.Windows.Forms.Timer { Interval = 800 };
    bool ending; // 컴퓨터를 끄는 중이면 창이 닫혀도 목록을 지우지 않음

    public MemoApp()
    {
        Current = this;
        Env = CoreWebView2Environment.CreateAsync(null, Path.Combine(Cfg.Dir, "WebView2"));
        keys = new HotkeyWindow(this);
        saveTimer.Tick += delegate { saveTimer.Stop(); SaveSession(); };
        Microsoft.Win32.SystemEvents.SessionEnding += delegate { ending = true; saveTimer.Stop(); };
        foreach (var s in WinState.LoadAll()) Open(s);
    }

    public MemoForm Main { get { return Forms.Count > 0 ? Forms[0] : null; } }

    // 새 창: 지금 앞에 있는 창 옆에 살짝 비켜서
    public MemoForm OpenNew(string pageId)
    {
        var s = new WinState { Id = pageId ?? "" };
        MemoForm from = Active() ?? Main;
        if (from != null)
        {
            Rectangle b = from.WindowState == FormWindowState.Normal ? from.Bounds : from.RestoreBounds;
            Rectangle wa = Screen.FromRectangle(b).WorkingArea;
            int x = b.X - b.Width - 8; if (x < wa.Left) x = Math.Min(b.X + 36, wa.Right - b.Width);
            s.Bounds = new Rectangle(x, Math.Min(b.Y + 24, wa.Bottom - b.Height), b.Width, b.Height);
            s.Top = from.TopMost;
        }
        return Open(s);
    }

    MemoForm Open(WinState s)
    {
        var f = new MemoForm(s);
        Forms.Add(f);
        // 사용자가 직접 닫은 창만 목록에서 뺌. 마지막 창은 다음에 다시 열리도록 남기고,
        // 컴퓨터를 끌 때 닫히는 창들은 목록을 그대로 둠
        f.FormClosing += (o, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing && Forms.Count == 1) { SaveSession(); ending = true; }
        };
        f.FormClosed += (o, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing) ending = true;
            Forms.Remove(f);
            if (Forms.Count == 0) { saveTimer.Stop(); keys.Dispose(); ExitThread(); }
            else SaveSoon();
        };
        f.Show();
        SaveSoon();
        return f;
    }

    public void SaveSoon() { saveTimer.Stop(); if (!ending) saveTimer.Start(); }

    void SaveSession()
    {
        if (ending) return;
        var parts = new List<string>();
        foreach (var f in Forms) parts.Add(f.State().ToString());
        if (parts.Count > 0) Cfg.Set("wins", string.Join(";", parts.ToArray()));
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
    WebView2 web;
    string pageId;
    double zoom;
    bool wasMax;

    public MemoForm(WinState s)
    {
        pageId = s.Id;
        zoom = s.Zoom;
        Text = "내 메모장";
        MinimumSize = new Size(Dpi.Scale(200), Dpi.Scale(160));
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.White;
        try
        {
            string png = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (File.Exists(png)) Icon = new Icon(png);
        }
        catch { }
        PlaceAt(s.Bounds);
        if (s.Max) WindowState = FormWindowState.Maximized;
        wasMax = s.Max;
        TopMost = s.Top;

        web = new WebView2();
        web.Dock = DockStyle.Fill;
        web.DefaultBackgroundColor = Color.White;
        Controls.Add(web);
        Load += OnLoad;
        // 옮기거나 크기를 바꾸면 창 목록 저장
        Move += delegate { MemoApp.Current.SaveSoon(); };
        Resize += delegate
        {
            if (WindowState != FormWindowState.Minimized) wasMax = WindowState == FormWindowState.Maximized;
            MemoApp.Current.SaveSoon();
        };
    }

    // 다음에 다시 열 때 쓸 이 창의 상태
    public WinState State()
    {
        return new WinState
        {
            Id = pageId ?? "",
            Bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds,
            Max = wasMax,
            Top = TopMost,
            Zoom = zoom
        };
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
        core.DocumentTitleChanged += delegate
        {
            // 제목이 아직 없으면 WebView2 가 주소(ereyang.github.io/…)를 대신 넘겨주므로 그때는 앱 이름으로
            string t = core.DocumentTitle ?? "";
            bool isUrl = t.StartsWith("http", StringComparison.OrdinalIgnoreCase) || t.Contains("github.io/");
            Text = (t.Length == 0 || isUrl) ? "내 메모장" : t;
        };
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
            else if (m.StartsWith("newwin:")) MemoApp.Current.OpenNew(m.Substring(7));
            else if (m.StartsWith("open:")) OpenFile(m.Substring(5));
        };
        // 보고 있는 페이지(주소의 #아이디)와 확대 비율이 바뀌면 창 목록 저장
        core.SourceChanged += delegate
        {
            string src = core.Source ?? "";
            int i = src.IndexOf('#');
            string id2 = i >= 0 ? src.Substring(i + 1) : "";
            if (WinState.IsPageId(id2) && id2 != pageId) { pageId = id2; MemoApp.Current.SaveSoon(); }
        };
        web.ZoomFactor = zoom;
        web.ZoomFactorChanged += delegate { zoom = web.ZoomFactor; MemoApp.Current.SaveSoon(); };
        string id = pageId ?? "";
        core.Navigate(Program.AppUrl + (id.Length > 0 && WinState.IsPageId(id) ? "#" + id : ""));
    }

    void OpenExternal(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return;
        if (!(uri.StartsWith("http://") || uri.StartsWith("https://") || uri.StartsWith("mailto:"))) return;
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    // 메모 속 file:/// 링크 → 기본 프로그램(한글, 엑셀…)으로 열기. 실행 파일은 열지 않는다.
    static readonly string[] Blocked = { ".exe", ".bat", ".cmd", ".com", ".scr", ".msi", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".lnk", ".reg", ".hta", ".cpl", ".jar", ".pif" };
    void OpenFile(string uri)
    {
        string path;
        try { Uri u = new Uri(uri); if (!u.IsFile) return; path = u.LocalPath; } catch { return; }
        if (Array.IndexOf(Blocked, System.IO.Path.GetExtension(path).ToLowerInvariant()) >= 0) return;
        if (!System.IO.File.Exists(path))
        {
            MessageBox.Show(this, "파일을 찾을 수 없어요.\n옮기거나 지웠을 수 있어요.\n\n" + path, "내 메모장", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    public void SetPin(bool on)
    {
        TopMost = on;
        MemoApp.Current.SaveSoon();
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
        if (m.Msg == 0x02E0) // WM_DPICHANGED
        {
            var rc = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
            Bounds = new Rectangle(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top);
            return;
        }
        if (m.Msg == 0x0112) // WM_SYSCOMMAND
        {
            int id = (int)m.WParam & 0xFFF0;
            if ((int)m.WParam == SC_PIN) { SetPin(!TopMost); return; }
            if ((int)m.WParam == SC_RESET) { WindowState = FormWindowState.Normal; Size = new Size(Dpi.Scale(420), Dpi.Scale(640)); return; }
            if ((int)m.WParam == SC_NEWWIN) { MemoApp.Current.OpenNew(null); return; }
        }
        if (m.Msg == Program.ShowMsg && Program.ShowMsg != 0) { MemoApp.Current.ShowMain(); return; }
        base.WndProc(ref m);
    }

    // 저장된 자리가 화면 밖(모니터를 뺐을 때 등)이면 기본 자리(주 화면 오른쪽)로
    void PlaceAt(Rectangle saved)
    {
        Rectangle r = new Rectangle(0, 0, Dpi.Scale(420), Dpi.Scale(640));
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        r.X = wa.Right - r.Width - Dpi.Scale(40); r.Y = wa.Top + Dpi.Scale(60);
        if (!saved.IsEmpty)
        {
            saved.Width = Math.Max(Dpi.Scale(200), saved.Width); saved.Height = Math.Max(Dpi.Scale(160), saved.Height);
            foreach (var s in Screen.AllScreens)
                if (s.WorkingArea.IntersectsWith(new Rectangle(saved.X + 40, saved.Y + 10, 40, 20))) { r = saved; break; }
        }
        Bounds = r;
    }
}

// 창 하나의 상태: 보던 페이지, 위치·크기, 최대화, 항상 위, 확대 비율
// settings.txt 에 wins=창;창;… (창 = 아이디,x,y,w,h,최대화,항상위,확대) 로 저장
class WinState
{
    public string Id = "";
    public Rectangle Bounds = Rectangle.Empty;
    public bool Max, Top;
    public double Zoom = 1;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static bool IsPageId(string id) { return id != null && Regex.IsMatch(id, "^[a-z0-9]+$"); }

    public override string ToString()
    {
        return string.Join(",", new[] {
            IsPageId(Id) ? Id : "",
            Bounds.X.ToString(Inv), Bounds.Y.ToString(Inv), Bounds.Width.ToString(Inv), Bounds.Height.ToString(Inv),
            Max ? "1" : "0", Top ? "1" : "0", Zoom.ToString(Inv) });
    }

    static WinState Parse(string text)
    {
        string[] p = text.Split(',');
        int x, y, w, h; double z;
        if (p.Length != 8 || !int.TryParse(p[1], NumberStyles.Integer, Inv, out x) || !int.TryParse(p[2], NumberStyles.Integer, Inv, out y)
            || !int.TryParse(p[3], NumberStyles.Integer, Inv, out w) || !int.TryParse(p[4], NumberStyles.Integer, Inv, out h)) return null;
        var s = new WinState { Id = IsPageId(p[0]) ? p[0] : "", Bounds = new Rectangle(x, y, w, h), Max = p[5] == "1", Top = p[6] == "1" };
        if (double.TryParse(p[7], NumberStyles.Float, Inv, out z) && z >= 0.25 && z <= 5) s.Zoom = z;
        return s;
    }

    // 지난번에 열려 있던 창들. 예전 버전은 첫 창 하나만 x/y/w/h/max/top/zoom 으로 저장했었음
    public static List<WinState> LoadAll()
    {
        var list = new List<WinState>();
        foreach (var part in Cfg.Get("wins").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var s = Parse(part);
            if (s != null && list.Count < 20) list.Add(s);
        }
        if (list.Count > 0) return list;

        var old = new WinState { Max = Cfg.Get("max") == "1", Top = Cfg.Get("top") == "1" };
        int x, y, w, h; double z;
        if (int.TryParse(Cfg.Get("x"), out x) && int.TryParse(Cfg.Get("y"), out y) && int.TryParse(Cfg.Get("w"), out w) && int.TryParse(Cfg.Get("h"), out h))
        {
            if (Cfg.Get("dpi") != "1") { x = Dpi.Scale(x); y = Dpi.Scale(y); w = Dpi.Scale(w); h = Dpi.Scale(h); }
            old.Bounds = new Rectangle(x, y, w, h);
        }
        if (double.TryParse(Cfg.Get("zoom"), NumberStyles.Float, Inv, out z) && z >= 0.25 && z <= 5) old.Zoom = z;
        list.Add(old);
        return list;
    }
}

// %LOCALAPPDATA%\MyMemo\settings.txt (모든 창이 함께 씀)
static class Cfg
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyMemo");
    static readonly Dictionary<string, string> map = Load();

    public static string Get(string k) { string v; return map.TryGetValue(k, out v) ? v : ""; }
    public static void Set(string k, string v) { map[k] = v; Save(); }

    static Dictionary<string, string> Load()
    {
        var d = new Dictionary<string, string>();
        try
        {
            string f = Path.Combine(Dir, "settings.txt");
            if (File.Exists(f))
                foreach (var line in File.ReadAllLines(f))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) d[line.Substring(0, i)] = line.Substring(i + 1);
                }
        }
        catch { }
        return d;
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var lines = new List<string>();
            foreach (var kv in map) lines.Add(kv.Key + "=" + kv.Value);
            File.WriteAllLines(Path.Combine(Dir, "settings.txt"), lines.ToArray());
        }
        catch { }
    }
}

static class Dpi
{
    public static int Scale(int v) { int d = 96; try { d = Native.GetDpiForSystem(); } catch { } return v * d / 96; }
}

static class Native
{
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern int GetDpiForSystem();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
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
