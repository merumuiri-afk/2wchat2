using TwoWChat.UI;

namespace TwoWChat;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length == 2 && args[0] == "--apply-update")
        {
            try { Updates.UpdateInstaller.ApplyAsync(args[1]).GetAwaiter().GetResult(); }
            catch (Exception e)
            {
                AppStore.Log("Update: " + e);
                MessageBox.Show("Обновление не завершено: " + e.Message + "\n\nЗакройте другие копии программы и запустите 2WChat.exe из прежней папки. При успешной замене резервная копия находится в 2WChat.exe.previous.", "2WChat — обновление", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.ExitCode = 1;
            }
            return;
        }
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            AppStore.Log(e.Exception.ToString());
            MessageBox.Show("Ошибка: " + e.Exception.Message + "\n\nПодробности: " + Path.Combine(AppStore.Folder, "diagnostics.log"), "2WChat", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppStore.Log(e.ExceptionObject.ToString() ?? "Unknown error");
        string? smoke = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data-dir" && i + 1 < args.Length) AppStore.SetDataDirectory(args[++i]);
            else if (args[i] == "--ui-smoke" && i + 1 < args.Length)
            {
                smoke = Path.GetFullPath(args[++i]);
                AppStore.SetDataDirectory(Path.Combine(smoke, "profile"));
            }
        }
        Application.Run(new MainForm(AppStore.Load(), smoke));
    }
}
