using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SyncTheSpire.Services;

namespace SyncTheSpire;

public class MainForm : Form
{
    private readonly WebView2 _webView;
    private MessageRouter? _router;

    public MainForm()
    {
        Text = "Sync the Spire";
        Size = new System.Drawing.Size(650, 610);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new System.Drawing.Size(640, 480);
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.FromArgb(0x0F, 0x11, 0x17);

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
        // use a dedicated user-data folder so we don't pollute the default profile
        var udFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SyncTheSpire", "WebView2Data");

        var env = await CoreWebView2Environment.CreateAsync(null, udFolder);
        await _webView.EnsureCoreWebView2Async(env);

        // map the wwwroot folder to a virtual hostname so CDN resources work
        // (file:// protocol blocks external scripts due to security policies)
        var wwwroot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "app.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);

        // wire up services & router (pass Form reference for window controls)
        var configService = new ConfigService();
        var junctionService = new JunctionService();
        var gitService = new GitService(configService);
        _router = new MessageRouter(_webView.CoreWebView2, configService, gitService, junctionService, this);

        _webView.CoreWebView2.WebMessageReceived += (_, e) =>
        {
            _router.HandleMessage(e.WebMessageAsJson);
        };

        // navigate via virtual host instead of file://
        _webView.CoreWebView2.Navigate("https://app.local/index.html");
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
