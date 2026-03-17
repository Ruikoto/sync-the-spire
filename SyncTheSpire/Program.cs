using LibGit2Sharp;

namespace SyncTheSpire;

class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // repo lives in AppData which may trigger ownership check (CVE-2022-24765)
        GlobalSettings.SetOwnerValidation(false);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
