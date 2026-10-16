using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CodexProfiles.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace CodexProfiles.Windows;

public sealed record ProfileRow(Guid Id, string Title, string Subtitle, string State, string Shortcut,
    string Avatar, Brush AvatarBrush, string Warning, List<UsageWindow> Windows, string CacheLabel);

public partial class MainWindow : Window
{
    internal ProfileStore Store { get; } = new();
    internal AppSettings Settings { get; private set; }
    private readonly DispatcherTimer maintenance = new() { Interval = TimeSpan.FromMinutes(1) };
    private Forms.NotifyIcon? tray;
    private CancellationTokenSource? work;
    private string? loginUrl;
    private bool exiting;
    private bool initialized;
    private bool IsBusy => work is not null;
    private List<ProfileRow> rows = [];
    private ProfileRow? Selected => ProfileList.SelectedItem as ProfileRow;

    public MainWindow()
    {
        Settings = AppSettings.Load(Store.Root);
        InitializeComponent();
        ApplyTheme();
        SortPicker.SelectedIndex = Settings.Sort switch { "Created" => 1, "Name" => 2, _ => 0 };
        initialized = true;
        RefreshProfiles();
        ConfigureTray();
        maintenance.Tick += async (_, _) => await MaintainAsync();
        maintenance.Start();
        Closing += OnClosing;
        Closed += (_, _) => { maintenance.Stop(); tray?.Dispose(); work?.Cancel(); };
    }

    internal void ApplyTheme()
    {
        var systemDark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0;
        var dark = Settings.Theme == "Dark" || (Settings.Theme == "System" && systemDark);
        var colors = dark
            ? new[] { "#191D1B", "#222825", "#EDF1EE", "#A8B5AC", "#38433C", "#6ED6A7" }
            : new[] { "#F5F5F4", "#FFFFFF", "#202321", "#606762", "#DDE2DE", "#177B55" };
        var keys = new[] { "CanvasBrush", "SurfaceBrush", "ForegroundBrush", "SecondaryBrush", "BorderBrush", "AccentBrush" };
        for (var i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        Application.Current.Resources["AccentTextBrush"] = new SolidColorBrush(dark ? Color.FromRgb(20, 45, 32) : Colors.White);
    }

    private string PrivateText(string? value)
    {
        if (value is null) return "";
        return Settings.HideEmails ? System.Text.RegularExpressions.Regex.Replace(value, @"[^\s@]+@[^\s@]+", "Hidden email") : value;
    }

    internal void RefreshProfiles()
    {
        var selected = Selected?.Id;
        var current = Store.Current;
        var currentId = Store.CurrentId;
        CurrentLabel.Text = current is null ? "No active Codex session" : "Active session · " + PrivateText(current.Email ?? (current.ApiKey is null ? "ChatGPT account" : "API key"));
        var sorted = Settings.Sort switch
        {
            "Name" => Store.Profiles.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase),
            "Created" => Store.Profiles.OrderByDescending(x => x.CreatedAt),
            _ => Store.Profiles.OrderByDescending(x => x.LastLoadedAt).ThenByDescending(x => x.CreatedAt)
        };
        rows = sorted.Select((profile, index) =>
        {
            var auth = Store.Auth(profile.Id);
            var usage = profile.Usage;
            var color = profile.AvatarColorToken switch { "green" => "#287D59", "orange" => "#A96724", "pink" => "#A24E7F", "red" => "#A64343", "teal" => "#287D83", "graphite" => "#59616B", _ => "#386CAC" };
            var symbol = profile.AvatarSymbol switch { "terminal" => "›_", "laptopcomputer" => "▣", "briefcase" => "▤", "moon.stars" => "☾", "sparkles" => "✧", "flame" => "♨", "bolt.circle" => "ϟ", _ => "●" };
            var title = PrivateText(profile.DisplayName);
            var plan = auth.ApiKey is null ? auth.Plan ?? "ChatGPT" : "API key";
            var subtitle = PrivateText(profile.Email);
            if (subtitle == title) subtitle = "Saved account";
            var cache = usage is null ? "Limits have not been checked" : $"Cached {usage.FetchedAt.LocalDateTime:g}" +
                (usage.FetchedAt < DateTimeOffset.UtcNow.AddHours(-1) || usage.Windows.Any(x => x.ResetsAt <= DateTimeOffset.UtcNow) ? " · Refresh needed" : "") +
                (usage.AvailableResets is { } resets ? $" · {resets} resets available" : "");
            return new ProfileRow(profile.Id, title, string.IsNullOrEmpty(subtitle) ? plan : subtitle + " · " + plan,
                profile.Id == currentId ? "● Active" : "Saved", index < 9 ? $"Ctrl+{index + 1}" : "", symbol,
                new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), profile.RenewalStatus ?? "",
                usage?.Windows ?? [], cache);
        }).ToList();
        ProfileList.ItemsSource = rows;
        ProfileList.SelectedItem = rows.FirstOrDefault(x => x.Id == selected) ?? rows.FirstOrDefault(x => x.Id == currentId) ?? rows.FirstOrDefault();
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateEnabled();
        UpdateTray();
    }

    private void UpdateEnabled()
    {
        Toolbar.IsEnabled = !IsBusy;
        ProfileActions.IsEnabled = !IsBusy && Selected is not null;
        ProfileList.IsEnabled = !IsBusy;
        BusyIndicator.Visibility = IsBusy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = IsBusy ? Visibility.Visible : Visibility.Collapsed;
        if (Selected is { } row) SwitchButton.IsEnabled = row.Id != Store.CurrentId;
    }

    internal async Task RunAsync(string status, Func<CancellationToken, Task> action, bool quiet = false)
    {
        if (IsBusy) return;
        work = new CancellationTokenSource();
        StatusLabel.Text = status;
        UpdateEnabled();
        try { await action(work.Token); StatusLabel.Text = "Done · Profiles stay on this PC"; }
        catch (OperationCanceledException) { StatusLabel.Text = "Cancelled"; }
        catch (Exception error)
        {
            StatusLabel.Text = PrivateText(error.Message);
            if (!quiet) MessageBox.Show(this, PrivateText(error.Message), "Codex Profiles", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            work.Dispose(); work = null;
            loginUrl = null; LoginLink.Visibility = Visibility.Collapsed;
            try { RefreshProfiles(); } catch (Exception error) { StatusLabel.Text = PrivateText(error.Message); }
            UpdateEnabled();
        }
    }

    private CodexCli Cli() => new(CodexCli.Find(Settings.CliPath), Store);
    private async void SignIn(object sender, RoutedEventArgs e) => await RunAsync("Waiting for browser sign-in…", async token =>
    {
        await Cli().LoginAsync(url => Dispatcher.Invoke(() =>
        {
            loginUrl = url;
            LoginLink.Visibility = Visibility.Visible;
            StatusLabel.Text = "Finish signing in in your browser. The current Codex account stays active.";
        }), token);
    });
    private void OpenLoginLink(object sender, RoutedEventArgs e) { if (loginUrl is not null) Process.Start(new ProcessStartInfo(loginUrl) { UseShellExecute = true }); }
    private void CancelWork(object sender, RoutedEventArgs e) => work?.Cancel();

    internal async Task SwitchAsync(Guid id)
    {
        await RunAsync("Switching account…", async token =>
        {
            Store.ValidateFileStorage();
            _ = Store.Auth(id);
            var desktop = await DesktopRuntime.QuitAsync(token);
            try { Store.Restore(id); }
            finally { if (desktop is not null) DesktopRuntime.Launch(Settings.DesktopPath ?? desktop); }
        });
        ProfileList.SelectedItem = rows.FirstOrDefault(x => x.Id == id);
    }
    private async void SwitchSelected(object sender, RoutedEventArgs e) { if (Selected is { } row) await SwitchAsync(row.Id); }
    private async void SaveCurrent(object sender, RoutedEventArgs e) => await RunAsync("Saving current session…", _ => { Store.CaptureCurrent(); return Task.CompletedTask; });
    private async void SignOut(object sender, RoutedEventArgs e) => await RunAsync("Signing out locally…", async token =>
    {
        Store.ValidateFileStorage();
        var desktop = await DesktopRuntime.QuitAsync(token);
        try { Store.SignOut(); }
        finally { if (desktop is not null) DesktopRuntime.Launch(Settings.DesktopPath ?? desktop); }
    });
    private async void ImportProfiles(object sender, RoutedEventArgs e)
    {
        var panel = new OpenFileDialog { Title = "Import profiles or auth.json", Filter = "JSON files|*.json", Multiselect = true };
        if (panel.ShowDialog(this) == true) await ImportPaths(panel.FileNames);
    }
    private Task ImportPaths(IEnumerable<string> paths) => RunAsync("Importing profiles…", _ =>
    {
        foreach (var path in paths) Store.Import(File.ReadAllText(path));
        return Task.CompletedTask;
    });
    private async void FilesDropped(object sender, DragEventArgs e)
    {
        if (!IsBusy && e.Data.GetData(DataFormats.FileDrop) is string[] files) await ImportPaths(files);
    }
    private async void ExportAll(object sender, RoutedEventArgs e) => await ExportAsync(null);
    private async void ExportSelected(object sender, RoutedEventArgs e) { if (Selected is { } row) await ExportAsync(row.Id); }
    private async Task ExportAsync(Guid? id)
    {
        var panel = new SaveFileDialog { Title = "Export profile backup", FileName = id is null ? "Codex-Profiles-Backup.codexprofiles.json" : "Profile.codexprofile.json", Filter = "JSON files|*.json" };
        if (panel.ShowDialog(this) != true) return;
        await RunAsync("Exporting backup…", _ => { Store.Export(panel.FileName, id); return Task.CompletedTask; });
        StatusLabel.Text = "Backup saved. Exported files contain credentials; keep them private.";
    }
    private async void DeleteSelected(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row || MessageBox.Show(this, $"Remove saved profile “{row.Title}”? The active Codex session will stay signed in.",
            "Remove profile", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAsync("Removing profile…", _ => { Store.Delete(row.Id); return Task.CompletedTask; });
    }
    private void EditSelected(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row || IsBusy) return;
        var profile = Store.Profiles.Single(x => x.Id == row.Id);
        var dialog = new ProfileDialog(profile) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            Store.Update(profile with { CustomName = dialog.ProfileName, AvatarSymbol = dialog.Symbol, AvatarColorToken = dialog.Tint });
            RefreshProfiles();
        }
    }
    private async void CheckLimits(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        await RunAsync("Checking account limits…", async token =>
        {
            var snapshot = await Cli().LimitsAsync(row.Id, token);
            Store.Update(Store.Profiles.Single(x => x.Id == row.Id) with { Usage = snapshot });
        });
    }
    private async void RenewSelected(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row) await RunAsync("Renewing saved session…", token => Cli().RenewAsync(row.Id, token));
    }
    private async Task MaintainAsync()
    {
        if (IsBusy || !initialized) return;
        await RunAsync("Synchronizing sessions…", async token =>
        {
            Store.Synchronize();
            if (Settings.AutoRenew)
            {
                var due = Store.Profiles.FirstOrDefault(x => AppSettings.NextRenewal(Store.Auth(x.Id), x, Settings.LeadDays) <= DateTimeOffset.UtcNow
                    && Store.CanRenew(x.Id));
                if (due is not null) await Cli().RenewAsync(due.Id, token);
            }
            var current = Store.Profiles.FirstOrDefault(x => x.Id == Store.CurrentId &&
                (x.Usage is null || x.Usage.FetchedAt < DateTimeOffset.UtcNow.AddMinutes(5 * -1)));
            if (current is not null && Store.Auth(current.Id) is { IsManaged: true, ExpiresAt: { } expiry } && expiry > DateTimeOffset.UtcNow)
            {
                var usage = await Cli().LimitsAsync(current.Id, token);
                Store.Update(Store.Profiles.Single(x => x.Id == current.Id) with { Usage = usage });
            }
        }, quiet: true);
    }
    private void ProfileSelected(object sender, SelectionChangedEventArgs e) { if (initialized) UpdateEnabled(); }
    private void SortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        Settings = Settings with { Sort = (SortPicker.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Recent" };
        Settings.Save(Store.Root); RefreshProfiles();
    }
    private async void ShortcutPressed(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || IsBusy) return;
        var number = e.Key is >= Key.D0 and <= Key.D9 ? e.Key - Key.D0
            : e.Key is >= Key.NumPad0 and <= Key.NumPad9 ? e.Key - Key.NumPad0 : -1;
        if (number == 0) { ShowFromTray(); e.Handled = true; }
        else if (number > 0 && number <= rows.Count) { e.Handled = true; await SwitchAsync(rows[number - 1].Id); }
    }
    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        var dialog = new SettingsDialog(Settings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            Settings = dialog.Value; Settings.Save(Store.Root); ApplyTheme(); ConfigureTray(); RefreshProfiles();
        }
    }
    internal void SetSettings(AppSettings settings)
    {
        Settings = settings; ApplyTheme(); RefreshProfiles();
    }
    private void ConfigureTray()
    {
        tray?.Dispose(); tray = null;
        if (!Settings.ShowTray) return;
        var resource = Application.GetResourceStream(new Uri("AppIcon.ico", UriKind.Relative));
        using var icon = new System.Drawing.Icon(resource.Stream);
        tray = new Forms.NotifyIcon { Icon = (System.Drawing.Icon)icon.Clone(), Text = "Codex Profiles", Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        UpdateTray();
    }
    private void UpdateTray()
    {
        if (tray is null) return;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Codex Profiles", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add(new Forms.ToolStripSeparator());
        foreach (var row in rows)
        {
            var item = new Forms.ToolStripMenuItem((row.State.Contains("Active") ? "✓ " : "") + row.Title) { Enabled = !IsBusy };
            item.Click += async (_, _) => await Dispatcher.InvokeAsync(async () => await SwitchAsync(row.Id)).Task.Unwrap();
            menu.Items.Add(item);
        }
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Dispatcher.Invoke(() => { exiting = true; Close(); }));
        var previous = tray.ContextMenuStrip; tray.ContextMenuStrip = menu; previous?.Dispose();
    }
    private void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!exiting && Settings.ShowTray) { e.Cancel = true; Hide(); }
        else if (IsBusy) { e.Cancel = true; work?.Cancel(); StatusLabel.Text = "Cancelling the current operation. Close again when it finishes."; }
    }
}
