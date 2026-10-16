using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexProfiles.Core;

public sealed record Profile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string? CustomName { get; init; }
    public string? Email { get; init; }
    public string? AvatarSymbol { get; init; } = "person.crop.circle";
    public string? AvatarColorToken { get; init; } = "blue";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoadedAt { get; init; }
    public DateTimeOffset? LastRenewalAttemptAt { get; init; }
    public string? RenewalStatus { get; init; }
    public bool RenewalRequiresSignIn { get; init; }
    public UsageSnapshot? Usage { get; init; }
    public string DisplayName => string.IsNullOrWhiteSpace(CustomName) ? Email ?? "Profile" : CustomName;
}

public sealed record StoredProfile(Profile Profile, string AuthJSONString);
public sealed record ArchivedProfile(string? CustomName, string? Email, string? AvatarSymbol,
    string? AvatarColorToken, DateTimeOffset CreatedAt, DateTimeOffset? LastLoadedAt, string AuthJSONString);
public sealed record ProfilesArchive(string Format, int Version, DateTimeOffset ExportedAt, List<ArchivedProfile> Profiles);

public sealed class ProfileStore
{
    public string Root { get; }
    public string CodexHome { get; }
    public string AuthPath => Path.Combine(CodexHome, "auth.json");
    private string VaultPath => Path.Combine(Root, "profiles.dat");
    private List<StoredProfile> entries;
    public IReadOnlyList<Profile> Profiles => entries.Select(x => x.Profile).ToList();
    public AuthDocument? Current => File.Exists(AuthPath) ? new AuthDocument(File.ReadAllText(AuthPath)) : null;
    public Guid? CurrentId => Current is { } auth ? entries.FirstOrDefault(x => Auth(x.Profile.Id).Identity == auth.Identity)?.Profile.Id : null;

    public ProfileStore(string? root = null, string? codexHome = null)
    {
        Root = Path.GetFullPath(root ?? Environment.GetEnvironmentVariable("CODEX_PROFILES_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codex Profiles"));
        CodexHome = Path.GetFullPath(codexHome ?? Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
        Storage.PrivateDirectory(Root);
        entries = File.Exists(VaultPath)
            ? JsonSerializer.Deserialize<List<StoredProfile>>(Storage.Unprotect(File.ReadAllBytes(VaultPath)), Storage.Json)
                ?? throw new IOException("The profile vault is damaged. Restore a backup; the vault has not been changed.")
            : [];
        foreach (var entry in entries) _ = new AuthDocument(entry.AuthJSONString);
        if (entries.Select(x => x.Profile.Id).Distinct().Count() != entries.Count)
            throw new IOException("The profile vault contains duplicate identifiers.");
    }

    public AuthDocument Auth(Guid id) => new(entries.Single(x => x.Profile.Id == id).AuthJSONString);
    private void Commit(List<StoredProfile> next)
    {
        Storage.AtomicWrite(VaultPath, Storage.Protect(JsonSerializer.SerializeToUtf8Bytes(next, Storage.Json)));
        entries = next;
    }
    public void Update(Profile profile) => Commit(entries.Select(x => x.Profile.Id == profile.Id ? x with { Profile = profile } : x).ToList());
    public void Delete(Guid id) => Commit(entries.Where(x => x.Profile.Id != id).ToList());

    private static StoredProfile Upsert(List<StoredProfile> target, AuthDocument auth, ArchivedProfile? metadata = null)
    {
        var index = target.FindIndex(x => new AuthDocument(x.AuthJSONString).Identity == auth.Identity);
        var previous = index >= 0 ? target[index] : null;
        var oldAuth = previous is null ? null : new AuthDocument(previous.AuthJSONString);
        if (oldAuth?.UpdatedAt is { } oldDate && auth.UpdatedAt is { } newDate && oldDate > newDate) auth = oldAuth;
        var changed = previous?.AuthJSONString != auth.Json;
        var profile = (previous?.Profile ?? new Profile()) with
        {
            Email = auth.Email ?? metadata?.Email ?? previous?.Profile.Email,
            CustomName = metadata?.CustomName ?? previous?.Profile.CustomName,
            AvatarSymbol = metadata?.AvatarSymbol ?? previous?.Profile.AvatarSymbol ?? "person.crop.circle",
            AvatarColorToken = metadata?.AvatarColorToken ?? previous?.Profile.AvatarColorToken ?? "blue",
            CreatedAt = metadata?.CreatedAt ?? previous?.Profile.CreatedAt ?? DateTimeOffset.UtcNow,
            LastLoadedAt = metadata?.LastLoadedAt ?? previous?.Profile.LastLoadedAt,
            RenewalRequiresSignIn = !changed && previous?.Profile.RenewalRequiresSignIn == true,
            RenewalStatus = changed ? null : previous?.Profile.RenewalStatus,
            LastRenewalAttemptAt = changed ? null : previous?.Profile.LastRenewalAttemptAt
        };
        var result = new StoredProfile(profile, auth.Json);
        if (index >= 0) target[index] = result; else target.Add(result);
        return result;
    }

    public Profile CaptureCurrent(string? name = null)
    {
        var current = Current ?? throw new IOException("No file-based Codex session found. Sign in or import auth.json.");
        var next = entries.ToList();
        var saved = Upsert(next, current);
        if (!string.IsNullOrWhiteSpace(name))
        {
            saved = saved with { Profile = saved.Profile with { CustomName = name.Trim() } };
            next[next.FindIndex(x => x.Profile.Id == saved.Profile.Id)] = saved;
        }
        Commit(next);
        return saved.Profile;
    }

    public void Synchronize(bool captureUnsaved = false)
    {
        if (Current is not { } current) return;
        var exists = entries.FirstOrDefault(x => new AuthDocument(x.AuthJSONString).Identity == current.Identity);
        if (exists is null && !captureUnsaved) return;
        var next = entries.ToList();
        var saved = Upsert(next, current);
        if (exists != saved) Commit(next);
    }

    public void ValidateFileStorage()
    {
        var path = Path.Combine(CodexHome, "config.toml");
        if (!File.Exists(path)) return;
        // Only the root TOML setting controls credential storage; ignore nested tables.
        foreach (var line in File.ReadLines(path))
        {
            var text = line.Trim();
            if (text.StartsWith('[')) break;
            if (!Regex.IsMatch(text, @"^cli_auth_credentials_store\s*=")) continue;
            if (!Regex.IsMatch(text, "^cli_auth_credentials_store\\s*=\\s*[\"']file[\"']\\s*(#.*)?$"))
                throw new InvalidOperationException("Profile switching requires cli_auth_credentials_store = \"file\" in Codex config.toml.");
        }
    }

    public void Restore(Guid id)
    {
        ValidateFileStorage();
        _ = Auth(id);
        Synchronize(captureUnsaved: true);
        var replacement = Auth(id);
        var previous = File.Exists(AuthPath) ? File.ReadAllBytes(AuthPath) : null;
        Storage.WriteText(AuthPath, replacement.Json);
        try { Update(Profiles.Single(x => x.Id == id) with { LastLoadedAt = DateTimeOffset.UtcNow }); }
        catch
        {
            if (previous is not null) Storage.AtomicWrite(AuthPath, previous); else File.Delete(AuthPath);
            throw;
        }
    }

    public void SignOut()
    {
        ValidateFileStorage();
        Synchronize(captureUnsaved: true);
        if (File.Exists(AuthPath)) File.Delete(AuthPath);
    }

    public int Import(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject
            ?? throw new FormatException("Expected an auth file or profile backup.");
        var imports = new List<(AuthDocument Auth, ArchivedProfile? Metadata)>();
        if (node.ContainsKey("format") || node.ContainsKey("profiles"))
        {
            ProfilesArchive archive;
            try { archive = JsonSerializer.Deserialize<ProfilesArchive>(json, Storage.Json) ?? throw new FormatException(); }
            catch { throw new FormatException("The backup is damaged or has invalid dates."); }
            if (archive.Format != "codex-profiles-archive" || archive.Version != 1 || archive.ExportedAt == default
                || archive.Profiles is not { Count: > 0 }) throw new FormatException("Expected codex-profiles-archive version 1.");
            foreach (var entry in archive.Profiles)
            {
                if (entry is null || entry.CreatedAt == default || entry.AuthJSONString is null) throw new FormatException("Invalid backup entry.");
                imports.Add((new AuthDocument(entry.AuthJSONString), entry));
            }
        }
        else imports.Add((new AuthDocument(json), null));
        Synchronize();
        var next = entries.ToList();
        foreach (var (auth, metadata) in imports) Upsert(next, auth, metadata);
        Commit(next);
        return imports.Count;
    }

    public void Export(string path, Guid? id = null)
    {
        Synchronize();
        var exported = entries.Where(x => id is null || x.Profile.Id == id).Select(x => new ArchivedProfile(
            x.Profile.CustomName, x.Profile.Email, x.Profile.AvatarSymbol, x.Profile.AvatarColorToken,
            x.Profile.CreatedAt, x.Profile.LastLoadedAt, x.AuthJSONString)).ToList();
        if (exported.Count == 0) throw new InvalidOperationException("No profiles to export.");
        Storage.WriteText(path, JsonSerializer.Serialize(new ProfilesArchive("codex-profiles-archive", 1, DateTimeOffset.UtcNow, exported), Storage.Json));
    }

    public bool CanRenew(Guid id)
    {
        ValidateFileStorage();
        var auth = Auth(id);
        var current = Current;
        return auth.IsManaged && !Profiles.Single(x => x.Id == id).RenewalRequiresSignIn
            && (current is null || (current.Identity != auth.Identity && current.RefreshToken != auth.RefreshToken))
            && entries.Count(x => new AuthDocument(x.AuthJSONString).RefreshToken == auth.RefreshToken) == 1;
    }

    public void CompleteRenewal(Guid id, AuthDocument renewed, AuthDocument previous)
    {
        if (renewed.Identity != previous.Identity) throw new InvalidOperationException("Codex returned a different account; credentials were not saved.");
        // Persist rotated credentials even if expiry verification fails later.
        if (renewed.AccessToken != previous.AccessToken || renewed.RefreshToken != previous.RefreshToken)
        {
            var next = entries.ToList();
            var index = next.FindIndex(x => x.Profile.Id == id);
            next[index] = next[index] with { AuthJSONString = renewed.Json };
            Commit(next);
        }
        if (renewed.ExpiresAt is null || renewed.ExpiresAt <= DateTimeOffset.UtcNow ||
            (renewed.AccessToken == previous.AccessToken && renewed.RefreshToken == previous.RefreshToken))
            throw new InvalidOperationException("Codex did not save a renewed access token. Update Codex or sign in again.");
    }
}
