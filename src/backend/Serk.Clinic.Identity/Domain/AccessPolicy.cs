using System.Text.RegularExpressions;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Identity.Domain;

/// <summary>A role definition visible to administrators: built-in (from code, read-only) or organisation-specific custom.</summary>
public sealed record RoleDefinition(string Code, string Name, string Description, IReadOnlyCollection<string> Capabilities, bool IsBuiltIn);

/// <summary>
/// Pure access rules: effective capabilities of an assignment and validation of administrator input. Kept free of I/O so the
/// rules are unit-tested exhaustively.
/// </summary>
public static partial class AccessPolicy
{
    public const int MinPasswordLength = 12;

    /// <summary>effective = (capabilities of active known roles ∪ granted) − revoked, restricted to catalogued capabilities.</summary>
    public static IReadOnlySet<string> Effective(
        IEnumerable<string> roles,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> customRoles,
        IEnumerable<string> granted,
        IEnumerable<string> revoked)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            if (RoleCatalog.IsKnownRole(role))
            {
                set.UnionWith(RoleCatalog.CapabilitiesFor([role]));
            }
            else if (customRoles.TryGetValue(role, out var caps))
            {
                set.UnionWith(caps);
            }
        }

        set.UnionWith(granted);
        set.ExceptWith(revoked);
        set.RemoveWhere(c => !CapabilityCatalog.IsKnown(c));
        return set;
    }

    public static Result<IReadOnlyList<string>> ValidateCapabilities(IEnumerable<string>? capabilities, string field, bool allowEmpty)
    {
        var list = (capabilities ?? []).Select(c => c?.Trim() ?? "").Distinct(StringComparer.Ordinal).ToList();
        if (!allowEmpty && list.Count == 0)
        {
            return Invalid("capabilities_required", "Choose at least one permission.", field);
        }

        var unknown = list.FirstOrDefault(c => !CapabilityCatalog.IsKnown(c));
        return unknown is not null
            ? Invalid("unknown_capability", $"'{unknown}' is not a known permission.", field)
            : list.Order(StringComparer.Ordinal).ToList();
    }

    public static Result<string> ValidateRoleName(string? name)
    {
        var n = (name ?? "").Trim();
        return n.Length is < 2 or > 80 ? Invalid("invalid_name", "Role name must be 2–80 characters.", "name") : n;
    }

    /// <summary>Derives a stable custom role code from its name, e.g. "Senior nurse" → "custom.senior-nurse".</summary>
    public static string CodeFromName(string name)
    {
        var slug = SlugChars().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        if (slug.Length < 2)
        {
            slug = "role-" + Guid.NewGuid().ToString("N")[..6];
        }

        return "custom." + (slug.Length > 40 ? slug[..40].Trim('-') : slug);
    }

    public static Result<string> ValidateUsername(string? username)
    {
        var u = (username ?? "").Trim().ToLowerInvariant();
        return UsernamePattern().IsMatch(u)
            ? u
            : Invalid("invalid_username", "Username must be 3–64 characters: lowercase letters, digits, dot, dash or underscore.", "username");
    }

    public static Result<string> ValidateNewPassword(string? password, string username)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength || password.Length > 256)
        {
            return Invalid("weak_password", $"Passwords must be at least {MinPasswordLength} characters.", "password");
        }

        if (password.Contains(username, StringComparison.OrdinalIgnoreCase))
        {
            return Invalid("weak_password", "The password must not contain the username.", "password");
        }

        return password.Distinct().Count() < 5
            ? Invalid("weak_password", "The password is too repetitive.", "password")
            : password;
    }

    private static Error Invalid(string code, string message, string field) =>
        Error.Validation(code, message, new Dictionary<string, string[]> { [field] = [message] });

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugChars();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{2,63}\z")]
    private static partial Regex UsernamePattern();
}
