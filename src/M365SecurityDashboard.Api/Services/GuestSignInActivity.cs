namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Reading guests' sign-in activity. Selecting signInActivity needs Entra ID P1 or P2; without it
/// Microsoft refuses the whole request, so the guests are read again without that field.
/// </summary>
public static class GuestSignInActivity
{
    public const string WithActivityPath = "/v1.0/users?$filter=userType eq 'Guest'&$select=id,displayName,userPrincipalName,mail,signInActivity";
    public const string WithoutActivityPath = "/v1.0/users?$filter=userType eq 'Guest'&$select=id,displayName,userPrincipalName,mail";

    /// <summary>True for Graph's Authentication_RequestFromNonPremiumTenantOrB2CTenant refusal.</summary>
    public static bool NeedsPremium(string? graphError) =>
        graphError?.Contains("RequestFromNonPremiumTenant", StringComparison.OrdinalIgnoreCase) == true;
}
