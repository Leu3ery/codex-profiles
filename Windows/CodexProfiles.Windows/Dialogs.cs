using System.Windows;
using System.Windows.Controls;
using CodexProfiles.Core;
using Microsoft.Win32;

namespace CodexProfiles.Windows;

internal static class DialogLayout
{
    public static StackPanel Body(Window window, string title, double width)
    {
        window.Title = title; window.Width = width; window.SizeToContent = SizeToContent.Height;
        window.ResizeMode = ResizeMode.NoResize; window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new StackPanel { Margin = new Thickness(24) };
        window.Content = body;
        var heading = new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 20) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "ForegroundBrush");
        body.Children.Add(heading);
        return body;
    }
    public static void Label(StackPanel body, string text)
    {
        var label = new TextBlock { Text = text, Margin = new Thickness(0, 12, 0, 6), TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, "ForegroundBrush");
        body.Children.Add(label);
    }
    public static ComboBox Picker(StackPanel body, string[] choices, string selected)
    {
        var picker = new ComboBox { ItemsSource = choices, SelectedItem = selected };
        body.Children.Add(picker); return picker;
    }
    public static void Finish(StackPanel body, Window window, Action save)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var done = new Button { Content = "Save", IsDefault = true, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        done.Click += (_, _) => { save(); window.DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(done); body.Children.Add(buttons);
    }
}

internal sealed class ProfileDialog : Window
{
    private readonly TextBox name;
    private readonly ComboBox symbol;
    private readonly ComboBox tint;
    public string ProfileName => name.Text.Trim();
    public string Symbol => symbol.SelectedItem?.ToString() ?? "person.crop.circle";
    public string Tint => tint.SelectedItem?.ToString() ?? "blue";
    public ProfileDialog(Profile profile)
    {
        var body = DialogLayout.Body(this, "Edit profile", 440);
        DialogLayout.Label(body, "Name"); name = new TextBox { Text = profile.CustomName ?? "", MaxLength = 120 }; body.Children.Add(name);
        DialogLayout.Label(body, "Avatar"); symbol = DialogLayout.Picker(body,
            ["person.crop.circle", "terminal", "laptopcomputer", "briefcase", "moon.stars", "sparkles", "flame", "bolt.circle"], profile.AvatarSymbol ?? "person.crop.circle");
        DialogLayout.Label(body, "Color"); tint = DialogLayout.Picker(body, ["blue", "graphite", "green", "orange", "pink", "red", "teal"], profile.AvatarColorToken ?? "blue");
        DialogLayout.Finish(body, this, () => { });
    }
}

internal sealed class SettingsDialog : Window
{
    public AppSettings Value { get; private set; }
    public SettingsDialog(AppSettings value)
    {
        Value = value;
        var body = DialogLayout.Body(this, "Settings", 560);
        var privacy = new CheckBox { Content = "Hide email addresses", IsChecked = value.HideEmails }; body.Children.Add(privacy);
        var tray = new CheckBox { Content = "Keep running in the system tray when the window closes", IsChecked = value.ShowTray }; body.Children.Add(tray);
        var renewal = new CheckBox { Content = "Automatically renew inactive sessions while this app runs", IsChecked = value.AutoRenew }; body.Children.Add(renewal);
        DialogLayout.Label(body, "Renew before access token expiry (days; retries at least 24 hours apart)");
        var days = DialogLayout.Picker(body, ["1", "2", "3", "4", "5", "6", "7"], Math.Clamp(value.LeadDays, 1, 7).ToString());
        DialogLayout.Label(body, "Appearance"); var theme = DialogLayout.Picker(body, ["System", "Light", "Dark"], value.Theme);
        DialogLayout.Label(body, "Codex CLI executable (optional; native codex.exe)");
        var cli = FilePicker(body, value.CliPath);
        DialogLayout.Label(body, "Codex desktop executable (optional; used when restarting Codex)");
        var desktop = FilePicker(body, value.DesktopPath);
        DialogLayout.Label(body, "Profiles are encrypted for your Windows account. Exported JSON backups contain credentials.\nCtrl+1–9 switches profiles in the current sort order; Ctrl+0 opens this window.");
        DialogLayout.Finish(body, this, () => Value = value with {
            HideEmails = privacy.IsChecked == true, ShowTray = tray.IsChecked == true, AutoRenew = renewal.IsChecked == true,
            LeadDays = int.Parse(days.SelectedItem.ToString()!), Theme = theme.SelectedItem.ToString()!,
            CliPath = string.IsNullOrWhiteSpace(cli.Text) ? null : cli.Text.Trim(),
            DesktopPath = string.IsNullOrWhiteSpace(desktop.Text) ? null : desktop.Text.Trim() });
    }
    private TextBox FilePicker(StackPanel body, string? value)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBox { Text = value ?? "", Margin = new Thickness(0, 0, 8, 0) };
        var browse = new Button { Content = "Browse…", Margin = new Thickness(0) };
        browse.Click += (_, _) => {
            var dialog = new OpenFileDialog { Title = "Select Codex executable", Filter = "Executable|*.exe" };
            if (dialog.ShowDialog(this) == true) text.Text = dialog.FileName;
        };
        Grid.SetColumn(browse, 1); grid.Children.Add(text); grid.Children.Add(browse); body.Children.Add(grid);
        return text;
    }
}
