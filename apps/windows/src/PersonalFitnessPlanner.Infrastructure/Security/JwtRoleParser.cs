using System.Text;
using System.Text.Json;
using PersonalFitnessPlanner.Infrastructure.Models;

namespace PersonalFitnessPlanner.Infrastructure.Security;

public sealed record JwtClaimsInfo(
    bool IsValid,
    bool IsExpired,
    string Subject,
    string DisplayName,
    IReadOnlySet<string> Roles,
    DateTimeOffset? ExpiresAt)
{
    public bool IsAdmin => Roles.Contains("admin") || Roles.Contains("administrator") ||
                           Roles.Contains("super_admin") || Roles.Contains("superadmin");
}

public static class JwtRoleParser
{
    private const string RoleClaim = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";

    /// <summary>
    /// Reads authorization UI claims only. Cryptographic validation remains the
    /// server/API handler's responsibility; no local setting can grant admin.
    /// </summary>
    public static JwtClaimsInfo Parse(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
        {
            return Empty();
        }

        try
        {
            var pieces = jwt.Split('.');
            if (pieces.Length != 3)
            {
                return Empty();
            }
            var payload = DecodeBase64Url(pieces[1]);
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddClaim(root, "role", roles);
            AddClaim(root, "roles", roles);
            AddClaim(root, RoleClaim, roles);
            if (root.TryGetProperty("realm_access", out var realm) && realm.ValueKind == JsonValueKind.Object)
            {
                AddClaim(realm, "roles", roles);
            }

            DateTimeOffset? expiresAt = null;
            if (root.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var unix))
            {
                expiresAt = DateTimeOffset.FromUnixTimeSeconds(unix);
            }
            var subject = ReadString(root, "sub");
            var displayName = ReadString(root, "name");
            if (string.IsNullOrWhiteSpace(displayName)) displayName = ReadString(root, "display_name");
            if (string.IsNullOrWhiteSpace(displayName)) displayName = ReadString(root, "email");
            var expired = expiresAt is not null && expiresAt <= DateTimeOffset.UtcNow;
            return new JwtClaimsInfo(true, expired, subject, displayName, roles, expiresAt);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            return Empty();
        }
    }

    public static AuthenticationState ToAuthenticationState(StoredTokens? tokens)
    {
        if (tokens is null) return new AuthenticationState(false, false, string.Empty, "none");
        var claims = Parse(tokens.AccessToken);
        var expired = claims.IsExpired || tokens.ExpiresAt is not null && tokens.ExpiresAt <= DateTimeOffset.UtcNow;
        return new AuthenticationState(claims.IsValid && !expired, claims.IsAdmin && !expired,
            string.IsNullOrWhiteSpace(tokens.DisplayName) ? claims.DisplayName : tokens.DisplayName,
            claims.IsValid ? "jwt" : "invalid-token");
    }

    private static JwtClaimsInfo Empty() => new(false, false, string.Empty, string.Empty,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase), null);

    private static void AddClaim(JsonElement parent, string name, HashSet<string> roles)
    {
        if (!parent.TryGetProperty(name, out var claim)) return;
        if (claim.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in claim.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } role)
                    roles.Add(role.Trim().ToLowerInvariant());
            }
        }
        else if (claim.ValueKind == JsonValueKind.String && claim.GetString() is { Length: > 0 } text)
        {
            foreach (var role in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                roles.Add(role.ToLowerInvariant());
        }
    }

    private static string ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        return Convert.FromBase64String(normalized);
    }
}
