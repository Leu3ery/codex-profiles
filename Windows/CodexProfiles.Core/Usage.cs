using System.Text.Json.Nodes;

namespace CodexProfiles.Core;

public sealed record UsageWindow(string Id, string Bucket, double UsedPercent, int? DurationMinutes, DateTimeOffset? ResetsAt)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
    public string DurationLabel => DurationMinutes is not { } m ? "Usage" : m % 1440 == 0 ? $"{m / 1440}d" : m % 60 == 0 ? $"{m / 60}h" : $"{m}m";
    public string ResetLabel => ResetsAt is not { } date ? "Reset time unavailable"
        : date <= DateTimeOffset.UtcNow ? "Reset due" : $"Resets {date.LocalDateTime:g}";
}
public sealed record UsageSnapshot(DateTimeOffset FetchedAt, List<UsageWindow> Windows, int? AvailableResets)
{
    public static UsageSnapshot Parse(JsonObject result)
    {
        var buckets = result["rateLimitsByLimitId"] as JsonObject;
        if (buckets is null || buckets.Count == 0)
            buckets = result["rateLimits"] is JsonObject legacy ? new JsonObject { [AuthDocument.Text(legacy["limitId"]) ?? "codex"] = legacy.DeepClone() } : [];
        var windows = new List<UsageWindow>();
        foreach (var (key, bucket) in buckets)
        {
            foreach (var kind in new[] { "primary", "secondary" })
            {
                if (bucket?[kind] is not JsonObject value || value["usedPercent"] is not JsonValue used
                    || !used.TryGetValue<double>(out var percent) || !double.IsFinite(percent)) continue;
                var duration = value["windowDurationMins"] is JsonValue minutes && minutes.TryGetValue<int>(out var m) && m > 0 ? (int?)m : null;
                windows.Add(new(key + ":" + kind, AuthDocument.Text(bucket?["limitName"]) ?? key,
                    Math.Clamp(percent, 0, 100), duration, AuthDocument.Unix(value["resetsAt"])));
            }
        }
        var resets = result["rateLimitResetCredits"]?["availableCount"] is JsonValue count && count.TryGetValue<int>(out var c) && c >= 0 ? (int?)c : null;
        return new(DateTimeOffset.UtcNow, windows, resets);
    }
}

public sealed record AppSettings
{
    public bool HideEmails { get; init; }
    public bool AutoRenew { get; init; } = true;
    public bool ShowTray { get; init; } = true;
    public int LeadDays { get; init; } = 1;
    public string Sort { get; init; } = "Recent";
    public string Theme { get; init; } = "System";
    public string? CliPath { get; init; }
    public string? DesktopPath { get; init; }

    public static AppSettings Load(string root)
    {
        var path = Path.Combine(root, "settings.json");
        return File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Storage.Json)
            ?? new() : new();
    }
    public void Save(string root) => Storage.WriteText(Path.Combine(root, "settings.json"), System.Text.Json.JsonSerializer.Serialize(this, Storage.Json));
    public static DateTimeOffset? NextRenewal(AuthDocument auth, Profile profile, int leadDays) => !auth.IsManaged || auth.ExpiresAt is not { } expiry ? null
        : new[] { expiry.AddDays(-Math.Clamp(leadDays, 1, 7)), profile.LastRenewalAttemptAt?.AddDays(1) ?? DateTimeOffset.MinValue }.Max();
}
