using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.AccessControl;
using System.Security.Principal;
using CodexProfiles.Core;

static string Jwt(string account, string email = "alex@example.test", int days = 7, int issued = 0) => "test." +
    Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> {
        ["sub"] = account, ["email"] = email, ["exp"] = DateTimeOffset.UtcNow.AddDays(days).ToUnixTimeSeconds(),
        ["iat"] = DateTimeOffset.UtcNow.AddMinutes(issued).ToUnixTimeSeconds(),
        ["https://api.openai.com/auth"] = new { chatgpt_plan_type = "plus" }
    })).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".test";
static string Auth(string account, string? refresh = null, int issued = 0) => JsonSerializer.Serialize(new {
    tokens = new { account_id = account, access_token = Jwt(account, issued: issued), id_token = Jwt(account), refresh_token = refresh ?? "refresh-" + account }
});

// A real child process exercises the stdio protocol, cancellation, and token persistence without network calls.
if (args.FirstOrDefault() is "app-server" or "login")
{
    var home = Environment.GetEnvironmentVariable("CODEX_HOME")!;
    if (args[0] == "login")
    {
        Console.WriteLine("https://auth.openai.com/synthetic-test");
        Storage.WriteText(Path.Combine(home, "auth.json"), Auth("login"));
        return;
    }
    var mode = Environment.GetEnvironmentVariable("CODEX_PROFILES_FAKE_MODE");
    var observedRefresh = false;
    while (Console.ReadLine() is { } line)
    {
        var request = JsonNode.Parse(line)!;
        if (request["id"] is null) continue;
        var id = request["id"]!.GetValue<int>();
        var method = request["method"]!.GetValue<string>();
        object result = new { };
        if (method == "initialize")
        {
            if (Environment.GetEnvironmentVariable("OPENAI_API_KEY") is not null) throw new Exception("Inherited API key");
        }
        if (method == "account/read")
        {
            observedRefresh = true;
            if (mode == "permanent")
            { Console.WriteLine(JsonSerializer.Serialize(new { id, error = new { message = "refresh_token_reused" } })); continue; }
            if (mode != "unchanged" && mode != "swallowed") Storage.WriteText(Path.Combine(home, "auth.json"), Auth("renew", "rotated-refresh", 5));
            if (mode == "timeout") { await Task.Delay(10000); continue; }
            result = new { account = new { type = "chatgpt" } };
        }
        if (method == "getAuthStatus") result = new { authMethod = "chatgpt", authToken = mode == "swallowed" && observedRefresh ? null : "synthetic" };
        if (method == "account/rateLimits/read") result = new { rateLimits = new { primary = new { usedPercent = 25, windowDurationMins = 300 } } };
        if (method == "account/login/start" && request["params"]?["type"]?.GetValue<string>() != "chatgptAuthTokens") throw new Exception("Usage login must use external access tokens");
        Console.WriteLine(JsonSerializer.Serialize(new { id, result }));
    }
    return;
}

var tests = new List<(string Name, Func<Task> Run)>();
void Add(string name, Action<ProfileStore, string> action) => tests.Add((name, () => WithStore((s, r) => { action(s, r); return Task.CompletedTask; })));
void AddAsync(string name, Func<ProfileStore, string, Task> action) => tests.Add((name, () => WithStore(action)));
static void Assert(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
static void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected failure"); }
static async Task Fails(Func<Task> action) { try { await action(); } catch { return; } throw new Exception("Expected async failure"); }
static async Task WithStore(Func<ProfileStore, string, Task> action)
{
    var root = Path.Combine(Path.GetTempPath(), "codex-profiles-test-" + Guid.NewGuid());
    try { await action(new ProfileStore(Path.Combine(root, "store"), Path.Combine(root, "home")), root); }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); Environment.SetEnvironmentVariable("CODEX_PROFILES_FAKE_MODE", null); }
}

Add("Archive round-trip and deduplication", (store, root) => {
    var original = Auth("one"); store.Import(original); var profile = store.Profiles.Single();
    store.Update(profile with { CustomName = "Personal", AvatarSymbol = "terminal", AvatarColorToken = "green" });
    var path = Path.Combine(root, "backup.json"); store.Export(path);
    var target = new ProfileStore(Path.Combine(root, "other"), Path.Combine(root, "other-home"));
    Assert(target.Import(File.ReadAllText(path)) == 1); Assert(target.Import(File.ReadAllText(path)) == 1);
    Assert(target.Profiles.Count == 1 && target.Profiles.Single().CustomName == "Personal");
    Assert(target.Profiles.Single().AvatarSymbol == "terminal");
    Assert(target.Auth(target.Profiles.Single().Id).Json == original);
});
Add("macOS fractional ISO dates and strict archive versions", (store, _) => {
    var archive = new JsonObject { ["format"] = "codex-profiles-archive", ["version"] = 1, ["exportedAt"] = "2026-09-02T20:41:58Z",
        ["profiles"] = new JsonArray(new JsonObject { ["createdAt"] = "2026-09-02T20:41:51.123Z", ["authJSONString"] = Auth("one") }) };
    Assert(store.Import(archive.ToJsonString()) == 1);
    archive["version"] = 99; Throws(() => store.Import(archive.ToJsonString())); Assert(store.Profiles.Count == 1);
    archive["version"] = 1; archive["profiles"]![0]!["createdAt"] = "invalid"; Throws(() => store.Import(archive.ToJsonString()));
});
Add("Invalid archive is validated before any profile is changed", (store, _) => {
    store.Import(Auth("old"));
    var backup = new ProfilesArchive("codex-profiles-archive", 1, DateTimeOffset.UtcNow,
        [new(null, null, null, null, DateTimeOffset.UtcNow, null, Auth("valid")), new(null, null, null, null, DateTimeOffset.UtcNow, null, "{}")]);
    Throws(() => store.Import(JsonSerializer.Serialize(backup, Storage.Json)));
    Assert(store.Profiles.Count == 1 && store.Auth(store.Profiles[0].Id).AccountId == "old");
});
Add("Switch preserves outgoing rotated credentials", (store, _) => {
    store.Import(Auth("one")); store.Import(Auth("two"));
    Storage.WriteText(store.AuthPath, Auth("one", "rotated", 10));
    store.Restore(store.Profiles[1].Id); store.Restore(store.Profiles[0].Id);
    Assert(store.Current!.RefreshToken == "rotated");
});
Add("Unsaved outgoing session and local sign-out remain recoverable", (store, _) => {
    store.Import(Auth("target")); Storage.WriteText(store.AuthPath, Auth("unsaved"));
    store.Restore(store.Profiles[0].Id); Assert(store.Profiles.Count == 2);
    store.SignOut(); Assert(store.Current is null && store.Profiles.Count == 2);
    store.Restore(store.Profiles.Single(x => store.Auth(x.Id).AccountId == "unsaved").Id);
    Assert(store.Current!.AccountId == "unsaved");
});
Add("Non-file credential stores block switching without modifying auth", (store, _) => {
    store.Import(Auth("target")); Storage.WriteText(store.AuthPath, Auth("active"));
    foreach (var mode in new[] { "keyring", "auto", "ephemeral" }) {
        Storage.WriteText(Path.Combine(store.CodexHome, "config.toml"), $"cli_auth_credentials_store = '{mode}' # test");
        Throws(() => store.Restore(store.Profiles[0].Id)); Assert(store.Current!.AccountId == "active");
    }
    Storage.WriteText(Path.Combine(store.CodexHome, "config.toml"), "cli_auth_credentials_store = 'file'\n[other]\ncli_auth_credentials_store = 'keyring'");
    store.Restore(store.Profiles[0].Id); Assert(store.Current!.AccountId == "target");
});
Add("Old imports cannot overwrite newer credentials", (store, _) => {
    store.Import(Auth("one", "latest", 10)); store.Import(Auth("one", "stale", -10));
    Assert(store.Auth(store.Profiles.Single().Id).RefreshToken == "latest");
});
Add("Exports synchronize the active session", (store, root) => {
    store.Import(Auth("one")); Storage.WriteText(store.AuthPath, Auth("one", "rotated", 10));
    var path = Path.Combine(root, "backup.json"); store.Export(path);
    Assert(File.ReadAllText(path).Contains("rotated"));
});
Add("API keys are distinct and malformed imports are rejected", (store, _) => {
    store.Import("{\"OPENAI_API_KEY\":\"fake-one\"}"); store.Import("{\"OPENAI_API_KEY\":\"fake-two\"}");
    Assert(store.Profiles.Count == 2); Throws(() => store.Import("{}")); Throws(() => store.Import("[]"));
    Assert(!store.Auth(store.Profiles[0].Id).IsManaged);
});
Add("Same email with different account IDs remains distinct", (store, _) => {
    store.Import(Auth("personal")); store.Import(Auth("business")); Assert(store.Profiles.Count == 2);
});
Add("Vault survives restart and refuses corrupt data", (store, _) => {
    store.Import(Auth("one")); Assert(new ProfileStore(store.Root, store.CodexHome).Profiles.Count == 1);
    var path = Path.Combine(store.Root, "profiles.dat");
    if (OperatingSystem.IsWindows()) Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("refresh-one"), "DPAPI encryption missing");
    Storage.WriteText(path, "corrupt"); Throws(() => new ProfileStore(store.Root, store.CodexHome));
    Assert(File.ReadAllText(path) == "corrupt");
});
Add("Secret files restrict permissions at creation", (store, root) => {
    store.Import(Auth("one")); var path = Path.Combine(root, "export.json"); store.Export(path);
    if (OperatingSystem.IsWindows()) {
        var security = new FileInfo(path).GetAccessControl();
        Assert(security.AreAccessRulesProtected);
        var allowed = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
        var user = WindowsIdentity.GetCurrent().User!;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        foreach (var rule in allowed) Assert(rule.IdentityReference == user || rule.IdentityReference == system);
    } else Assert(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite));
});
Add("Shared and active refresh tokens cannot be renewed", (store, _) => {
    store.Import(Auth("one", "shared")); store.Import(Auth("two", "shared"));
    Assert(store.Profiles.All(x => !store.CanRenew(x.Id)));
    store.Import(Auth("three")); var id = store.Profiles.Last().Id; Assert(store.CanRenew(id));
    Storage.WriteText(store.AuthPath, Auth("three")); Assert(!store.CanRenew(id));
});
Add("Renewal verifies identity and persists partial rotation", (store, _) => {
    store.Import(Auth("one")); var id = store.Profiles.Single().Id; var previous = store.Auth(id);
    Throws(() => store.CompleteRenewal(id, new AuthDocument(Auth("other")), previous));
    Assert(store.Auth(id).AccountId == "one");
    Throws(() => store.CompleteRenewal(id, previous, previous));
});
Add("Usage parsing handles multiple buckets and legacy data", (_, _) => {
    var snapshot = UsageSnapshot.Parse(JsonNode.Parse("""{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":125,"windowDurationMins":300,"resetsAt":1800000000},"secondary":{"usedPercent":-2}}},"rateLimitResetCredits":{"availableCount":3}}""")!.AsObject());
    Assert(snapshot.Windows.Count == 2 && snapshot.Windows[0].RemainingPercent == 0 && snapshot.Windows[1].RemainingPercent == 100 && snapshot.AvailableResets == 3);
    Assert(UsageSnapshot.Parse(JsonNode.Parse("""{"rateLimits":{"primary":{"usedPercent":20}}}""")!.AsObject()).Windows.Single().RemainingPercent == 80);
    Assert(UsageSnapshot.Parse(new JsonObject()).Windows.Count == 0);
});
Add("Renewal schedule enforces lead window and daily backoff", (store, _) => {
    store.Import(Auth("one")); var profile = store.Profiles.Single(); var auth = store.Auth(profile.Id);
    var attempted = DateTimeOffset.UtcNow;
    Assert(AppSettings.NextRenewal(auth, profile, 1) == auth.ExpiresAt!.Value.AddDays(-1));
    Assert(AppSettings.NextRenewal(auth, profile with { LastRenewalAttemptAt = attempted }, 7) == attempted.AddDays(1));
});
AddAsync("Real stdio login isolates CODEX_HOME and imports credentials", async (store, _) => {
    var cli = new CodexCli(Environment.ProcessPath!, store); string? url = null;
    await cli.LoginAsync(value => url = value, CancellationToken.None);
    Assert(url == "https://auth.openai.com/synthetic-test" && store.Profiles.Count == 1 && store.Current is null);
    Assert(!Directory.EnumerateDirectories(Path.Combine(store.Root, "work")).Any());
});
AddAsync("Real stdio usage request uses external tokens and clears API keys", async (store, _) => {
    store.Import(Auth("usage")); Environment.SetEnvironmentVariable("OPENAI_API_KEY", "synthetic-inherited-key");
    try {
        var usage = await new CodexCli(Environment.ProcessPath!, store).LimitsAsync(store.Profiles[0].Id, CancellationToken.None);
        Assert(usage.Windows.Single().RemainingPercent == 75);
    } finally { Environment.SetEnvironmentVariable("OPENAI_API_KEY", null); }
});
AddAsync("Real stdio renewal saves rotated tokens", async (store, _) => {
    store.Import(Auth("renew")); var id = store.Profiles.Single().Id;
    await new CodexCli(Environment.ProcessPath!, store).RenewAsync(id, CancellationToken.None);
    Assert(store.Auth(id).RefreshToken == "rotated-refresh" && store.Profiles.Single().LastRenewalAttemptAt is not null);
});
foreach (var mode in new[] { "permanent", "swallowed", "unchanged", "timeout" })
AddAsync("Renewal failure classification and recovery: " + mode, async (store, _) => {
    Environment.SetEnvironmentVariable("CODEX_PROFILES_FAKE_MODE", mode);
    store.Import(Auth("renew")); var id = store.Profiles.Single().Id;
    using var cancellation = new CancellationTokenSource(mode == "timeout" ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(15));
    await Fails(() => new CodexCli(Environment.ProcessPath!, store).RenewAsync(id, cancellation.Token));
    Assert(store.Profiles.Single().RenewalRequiresSignIn == (mode is "permanent" or "swallowed"));
    if (mode == "timeout") Assert(store.Auth(id).RefreshToken == "rotated-refresh");
    Assert(!Directory.EnumerateDirectories(Path.Combine(store.Root, "work")).Any());
});

var failed = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + "\n" + error); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed");
Environment.ExitCode = failed == 0 ? 0 : 1;
