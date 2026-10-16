using System.Windows;
using CodexProfiles.Core;

namespace CodexProfiles.Windows;

public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, error) =>
        {
            MessageBox.Show("The operation failed. Your saved profiles remain on disk.\n\n" + error.Exception.Message,
                "Codex Profiles", MessageBoxButton.OK, MessageBoxImage.Error);
            error.Handled = true;
        };
        try
        {
            var smoke = e.Args.Length == 2 && e.Args[0] == "--smoke-test";
            if (smoke) SmokeTest.Prepare(e.Args[1]);
            var scope = Environment.GetEnvironmentVariable("CODEX_PROFILES_HOME") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codex Profiles");
            instance = new Mutex(true, @"Local\CodexProfiles-" + AuthDocument.Hash(Path.GetFullPath(scope).ToUpperInvariant()), out var created);
            if (!created) { MessageBox.Show("Codex Profiles is already running. Open it from the system tray.", "Codex Profiles"); Shutdown(); return; }
            var window = new MainWindow();
            MainWindow = window;
            if (smoke) window.Loaded += async (_, _) => await SmokeTest.RunAsync(window, e.Args[1]);
            window.Show();
        }
        catch (Exception error)
        {
            if (e.Args.Contains("--smoke-test")) { Console.Error.WriteLine(error.Message); Shutdown(1); }
            else { MessageBox.Show("Could not open Codex Profiles.\n\n" + error.Message, "Codex Profiles", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
        }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
