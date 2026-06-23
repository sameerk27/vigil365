using System.ComponentModel.DataAnnotations;

namespace M365SecurityDashboard.Api.Models;

/// <summary>
/// A user who has signed in to Vigil365, with their assigned role. Identity comes
/// from the validated Microsoft token (email); the role is managed in-app by an
/// Admin, not in Entra ID. This keeps role management self-contained — no Azure
/// App Roles setup and no high-privilege Graph write permission required.
/// </summary>
public sealed class AppUser
{
    /// <summary>The user's email / UPN from the token. Primary key, stored lower-cased.</summary>
    [MaxLength(320)]
    public required string Email { get; set; }

    /// <summary>Display name from the token, for the user-management UI.</summary>
    [MaxLength(200)]
    public string? DisplayName { get; set; }

    /// <summary>One of: Admin, Analyst, Viewer.</summary>
    [MaxLength(20)]
    public string Role { get; set; } = AppRoles.Viewer;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the user most recently signed in.</summary>
    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>The three role values. Kept as constants to avoid magic strings.</summary>
public static class AppRoles
{
    public const string Admin = "Admin";
    public const string Analyst = "Analyst";
    public const string Viewer = "Viewer";

    public static readonly string[] All = [Admin, Analyst, Viewer];
    public static bool IsValid(string? role) => role is Admin or Analyst or Viewer;
}
