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
        Size = new System.Drawing.Size(800, 600);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new System.Drawing.Size(640, 480);

        _webView = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_webView);

        Load += async (_, _) => await InitWebView();
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

        // wire up services & router
        var configService = new ConfigService();
        var junctionService = new JunctionService();
        var gitService = new GitService(configService);
        _router = new MessageRouter(_webView.CoreWebView2, configService, gitService, junctionService);

        _webView.CoreWebView2.WebMessageReceived += (_, e) =>
        {
            _router.HandleMessage(e.WebMessageAsJson);
        };

        // navigate via virtual host instead of file://
        _webView.CoreWebView2.Navigate("https://app.local/index.html");
    }
}
