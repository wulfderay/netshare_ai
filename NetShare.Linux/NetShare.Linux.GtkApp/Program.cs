using Gtk;
using NetShare.Linux.Core;
using NetShare.Linux.Core.Settings;

namespace NetShare.Linux.GtkApp;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // NOTE: Gtk# 3 requires GTK to be installed on the system (libgtk-3).
        Application.Init();

        Console.Error.WriteLine("[GtkApp] Starting NetShare.Linux.GtkApp");

        var store = new SettingsStore();
        using var host = new AppHost(store);

        // IMPORTANT: Start() captures SynchronizationContext.Current.
        // On Gtk#, SynchronizationContext may not marshal to GTK main loop reliably,
        // so the UI also refreshes via periodic timer and explicit marshaling.
        host.Start();

        var win = new MainWindow(host, store);
        win.DeleteEvent += (_, _) =>
        {
            try { Console.Error.WriteLine("[GtkApp] Shutting down"); } catch { }
            host.SaveSettings();
            Application.Quit();
        };

        win.ShowAll();
        Application.Run();
    }
}
