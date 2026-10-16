using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexProfiles.Core;

namespace CodexProfiles.Windows;

// Runs the real packaged WPF app against synthetic credentials in an isolated directory.
internal static class SmokeTest
{
    public static void Prepare(string output)
    {
        var root = Path.GetFullPath(output);
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("CODEX_PROFILES_HOME", Path.Combine(root, "profiles"));
        Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(root, "codex-home"));
        var store = new ProfileStore();
        (new AppSettings { ShowTray = false, AutoRenew = false }).Save(store.Root);
        Storage.WriteText(store.AuthPath, FakeAuth("personal", "alex@example.test", "plus"));
    }

    private static string FakeAuth(string account, string email, string plan)
    {
        var claims = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> {
            ["email"] = email, ["exp"] = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds(),
            ["https://api.openai.com/auth"] = new { chatgpt_plan_type = plan }
        })).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return JsonSerializer.Serialize(new { tokens = new { account_id = account, access_token = "test." + claims + ".test", id_token = "test." + claims + ".test", refresh_token = "synthetic-" + account } });
    }

    public static async Task RunAsync(MainWindow window, string output)
    {
        try
        {
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert(window.EmptyState.Visibility == Visibility.Visible, "Empty state");
            Capture(window, Path.Combine(output, "empty.png"));
            window.SaveCurrentButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(window.Store.Profiles.Count == 1 && window.ProfileList.Items.Count == 1, "Save current button");
            window.Store.Import(FakeAuth("work", "alex@work.example.test", "team"));
            foreach (var profile in window.Store.Profiles)
                window.Store.Update(profile with { CustomName = profile.Email!.Contains("work") ? "Work" : "Personal",
                    AvatarColorToken = profile.Email.Contains("work") ? "teal" : "green",
                    Usage = new UsageSnapshot(DateTimeOffset.UtcNow, [new("codex:primary", "Codex", 23, 300, DateTimeOffset.UtcNow.AddHours(3)),
                        new("codex:secondary", "Codex", 41, 10080, DateTimeOffset.UtcNow.AddDays(4))], 2) });
            window.RefreshProfiles();
            var target = window.Store.Profiles.Single(x => x.CustomName == "Work");
            await window.SwitchAsync(target.Id);
            Assert(window.Store.CurrentId == target.Id, "Switch account");
            Assert(window.SwitchButton.IsEnabled == false, "Active account switch disabled");
            window.SetSettings(window.Settings with { HideEmails = true, Theme = "Light" });
            Assert(!window.CurrentLabel.Text.Contains('@'), "Email privacy in header");
            Assert(window.ProfileList.Items.Cast<ProfileRow>().All(x => !x.Title.Contains('@') && !x.Subtitle.Contains('@')), "Email privacy in rows");
            AssertButtonText(window);
            Capture(window, Path.Combine(output, "light.png"));
            window.SetSettings(window.Settings with { Theme = "Dark" });
            AssertButtonText(window);
            Capture(window, Path.Combine(output, "dark.png"));
            window.Width = 780; window.Height = 600;
            Capture(window, Path.Combine(output, "compact.png"));
            Storage.WriteText(Path.Combine(output, "result.txt"), "PASS: packaged EXE startup, empty state, save current button, account switching, active button state, email privacy, light/dark/compact rendering.");
            Application.Current.Shutdown(0);
        }
        catch (Exception error)
        {
            Storage.WriteText(Path.Combine(output, "result.txt"), "FAIL: " + error);
            Application.Current.Shutdown(1);
        }
    }

    private static void Assert(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }
    private static void AssertButtonText(MainWindow window)
    {
        window.UpdateLayout();
        Assert(window.FontSize == 14, "Window must use the application typography");
        Assert(((SolidColorBrush)window.Background).Color == ((SolidColorBrush)Application.Current.FindResource("CanvasBrush")).Color,
            "Window background must follow the selected theme");
        var text = Descendants(window.SignInButton).OfType<TextBlock>().First(x => x.Text == "Add account");
        Assert(((SolidColorBrush)text.Foreground).Color == ((SolidColorBrush)window.SignInButton.Foreground).Color,
            "Primary button label must inherit its contrasting foreground in both themes");
        var foreground = ((SolidColorBrush)Application.Current.FindResource("ForegroundBrush")).Color;
        Assert(((SolidColorBrush)window.TitleLabel.Foreground).Color == foreground, "Header theme foreground");
        var title = Descendants(window.ProfileList).OfType<TextBlock>().First(x => x.Text == "Work");
        Assert(((SolidColorBrush)title.Foreground).Color == foreground, "Profile title theme foreground");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
