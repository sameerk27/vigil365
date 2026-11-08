namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// The home domain of a guest account: their mail, else the UPN part before <c>#EXT#</c>
/// (<c>ana_contoso.com#EXT#@tenant.onmicrosoft.com</c> → <c>contoso.com</c>). The part after #EXT# is
/// always the host tenant, which is not where the guest comes from.
/// </summary>
public static class GuestDomains
{
    public static string? Of(string? mail, string? upn)
    {
        if (!string.IsNullOrEmpty(mail) && mail.Contains('@')) return mail[(mail.LastIndexOf('@') + 1)..].ToLowerInvariant();
        if (string.IsNullOrEmpty(upn)) return null;
        var ext = upn.IndexOf("#EXT#@", StringComparison.OrdinalIgnoreCase);
        if (ext >= 0)
        {
            var local = upn[..ext];
            var underscore = local.LastIndexOf('_');
            return underscore >= 0 && underscore < local.Length - 1 ? local[(underscore + 1)..].ToLowerInvariant() : null;
        }
        return upn.Contains('@') ? upn[(upn.LastIndexOf('@') + 1)..].ToLowerInvariant() : null;
    }
}
