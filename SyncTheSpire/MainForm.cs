using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SyncTheSpire.Services;

namespace SyncTheSpire;

public class MainForm : Form
{
    private readonly WebView2 _webView;
    private MessageRouter? _router;

    // base sizes designed at 96 DPI (100% scaling)
    private const int DesignWidth = 650;
    private const int DesignHeight = 600;
    private const int MinWidth = 640;
    private const int MinHeight = 480;

    public MainForm()
    {
        Text = "Sync the Spire";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.FromArgb(0x0F, 0x11, 0x17);

        // scale window to match current DPI so WebView2 CSS viewport stays consistent
        var scale = DeviceDpi / 96.0;
        Size = new System.Drawing.Size((int)(DesignWidth * scale), (int)(DesignHeight * scale));
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new System.Drawing.Size((int)(MinWidth * scale), (int)(MinHeight * scale));

        // taskbar icon (borderless form hides the title bar, but taskbar still shows it)
        var icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "icon.ico");
        if (File.Exists(icoPath))
            Icon = new Icon(icoPath);

        _webView = new WebView2 { Dock = DockStyle.Fill };
        _webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0F, 0x11, 0x17);
        Controls.Add(_webView);

        Load += async (_, _) => await InitWebView();
    }

    // ── borderless window setup ──────────────────────────────────────────
    // WS_THICKFRAME is needed for native edge-resize and aero-snap,
    // then WM_NCCALCSIZE collapses the non-client frame to zero so
    // nothing visible changes.

    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_MINIMIZEBOX = 0x00020000;
    private const int WS_MAXIMIZEBOX = 0x00010000;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
            return cp;
        }
    }

    // round corners on Windows 11+
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int pref = DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    private async Task InitWebView()
    {
        try
        {
            // use a dedicated user-data folder so we don't pollute the default profile
            var udFolder = Path.Combine(WorkspaceManager.AppDataDir, "WebView2Data");

            var env = await CoreWebView2Environment.CreateAsync(null, udFolder);
            await _webView.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            LogService.Error("WebView2 init failed", ex);
            MessageBox.Show(
                $"无法初始化 WebView2，请确认已安装 WebView2 Runtime。\n\n{ex.Message}",
                "Sync the Spire", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        // map the wwwroot folder to a virtual hostname so CDN resources work
        // (file:// protocol blocks external scripts due to security policies)
        var wwwroot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "app.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);

        // wire up services & router (pass Form reference for window controls)
        var workspaceManager = new WorkspaceManager();
        var junctionService = new JunctionService();
        var gitResolver = new GitResolver();
        var storeUpdateService = new StoreUpdateService();
        storeUpdateService.Initialize(this.Handle);

        // resolve active workspace — create context if one exists
        WorkspaceContext? wsContext = null;
        var activeId = workspaceManager.Config.ActiveWorkspace;
        if (activeId != null && workspaceManager.GetWorkspace(activeId) != null)
        {
            wsContext = new WorkspaceContext(
                workspaceManager.GetWorkspace(activeId)!, workspaceManager, gitResolver, junctionService);
        }

        _router = new MessageRouter(
            _webView.CoreWebView2, workspaceManager, wsContext,
            gitResolver, junctionService, storeUpdateService, this);

        _webView.CoreWebView2.WebMessageReceived += (_, e) =>
        {
            _router.HandleMessage(e.WebMessageAsJson);
        };

        // open external links in default browser instead of inside WebView2
        _webView.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            // only allow http(s) and ms-windows-store:// links
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) &&
                (uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "ms-windows-store"))
            {
                Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
            }
        };

        // navigate via virtual host instead of file://
        _webView.CoreWebView2.Navigate("https://app.local/index.html");
    }

    // keep MinimumSize in sync when dragging across monitors with different DPI
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        var scale = e.DeviceDpiNew / 96.0;
        MinimumSize = new System.Drawing.Size((int)(MinWidth * scale), (int)(MinHeight * scale));
    }

    // ── native drag support ─────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const nint HTCAPTION = 2;

    /// <summary>
    /// called from MessageRouter when JS titlebar wants to initiate a drag
    /// </summary>
    public void BeginDrag()
    {
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
    }

    // ── WndProc: borderless frame + maximize bounds ────────

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_NCCALCSIZE = 0x0083;

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            // collapse the non-client frame to zero so the WS_THICKFRAME border is invisible
            case WM_NCCALCSIZE:
                if (m.WParam != IntPtr.Zero)
                {
                    m.Result = IntPtr.Zero;
                    return;
                }
                break;

            case WM_GETMINMAXINFO:
                // prevent maximized window from covering taskbar
                var info = Marshal.PtrToStructure<MINMAXINFO>(m.LParam);
                var screen = Screen.FromHandle(Handle);
                var wa = screen.WorkingArea;
                info.ptMaxPosition = new Point(wa.X - screen.Bounds.X, wa.Y - screen.Bounds.Y);
                info.ptMaxSize = new Point(wa.Width, wa.Height);
                Marshal.StructureToPtr(info, m.LParam, true);
                m.Result = IntPtr.Zero;
                return;
        }

        base.WndProc(ref m);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public Point ptReserved;
        public Point ptMaxSize;
        public Point ptMaxPosition;
        public Point ptMinTrackSize;
        public Point ptMaxTrackSize;
    }
}
