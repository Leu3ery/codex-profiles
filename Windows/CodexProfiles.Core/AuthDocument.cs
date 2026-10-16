using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CodexProfiles.Core;

public sealed class AuthDocument
{
    public string Json { get; }
    public JsonObject Root { get; }
    public string? AccessToken => Text(Root["tokens"]?["access_token"]);
    public string? RefreshToken => Text(Root["tokens"]?["refresh_token"]);
    public string? AccountId => Text(Root["tokens"]?["account_id"]);
    public string? ApiKey => Text(Root["OPENAI_API_KEY"]);
    public string? Email { get; }
    public string Identity { get; }
    public string? Plan { get; }
    public DateTimeOffset? ExpiresAt { get; }
    public DateTimeOffset? UpdatedAt { get; }
    public bool IsManaged => ApiKey is null && RefreshToken is not null &&
        (Text(Root["auth_mode"]) is null or "chatgpt");

    public AuthDocument(string json)
    {
        Json = json;
        try
        {
            Root = JsonNode.Parse(json) as JsonObject ?? throw new FormatException();
            if (ApiKey is null && AccessToken is null && RefreshToken is null) throw new FormatException();
            var access = Claims(AccessToken);
            var identity = Claims(Text(Root["tokens"]?["id_token"]));
            Email = (Text(identity?["email"]) ?? Text(access?["https://api.openai.com/profile"]?["email"]))?.ToLowerInvariant();
            var account = AccountId ?? Text(identity?["sub"]) ?? Text(access?["sub"]);
            Identity = ApiKey is not null ? "key:" + Hash(ApiKey)
                : account is not null ? "account:" + account + ":" + Email
                : Email is not null ? "email:" + Email
                : "token:" + Hash(RefreshToken ?? AccessToken!);
            Plan = Text(identity?["https://api.openai.com/auth"]?["chatgpt_plan_type"])
                ?? Text(access?["https://api.openai.com/auth"]?["chatgpt_plan_type"]);
            ExpiresAt = Unix(access?["exp"]);
            UpdatedAt = DateTimeOffset.TryParse(Text(Root["last_refresh"]), out var refreshed)
                ? refreshed : Unix(access?["iat"]);
        }
        catch (Exception e) when (e is not FormatException || e.Message != "This is not a valid Codex auth JSON file.")
        { throw new FormatException("This is not a valid Codex auth JSON file."); }
    }

    public static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text)
        && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static DateTimeOffset? Unix(JsonNode? node)
    {
        if (node is not JsonValue value || !value.TryGetValue<double>(out var number) || !double.IsFinite(number)) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(checked((long)number)); }
        catch (Exception e) when (e is ArgumentOutOfRangeException or OverflowException) { return null; }
    }
    private static JsonObject? Claims(string? token)
    {
        try
        {
            var value = token!.Split('.')[1].Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + (4 - value.Length % 4) % 4, '=');
            return JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value))) as JsonObject;
        }
        catch { return null; }
    }
}
