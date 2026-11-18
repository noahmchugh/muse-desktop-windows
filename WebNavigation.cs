using System;
using System.Diagnostics;

namespace MuseDesktop;

/// <summary>
/// Which hosts a WebView window may navigate to in-app. A window that
/// hosts a signed-in session should not double as a general browser:
/// pages it wanders to run in that session's profile. Anything outside
/// the allowlist opens in the user's default browser instead.
/// </summary>
internal static class WebNavigation
{
    // muse.ai itself, its subdomains, and the identity-provider hosts
    // its sign-in flow redirects through. Muse is a Meta service, and
    // its sign-in leaves muse.ai for Meta's own hosts before coming
    // back — in practice the first hop is instagram.com's auth
    // redirect (seen in nav-divert.log on a real first run). If those
    // hosts are not allowed in-app, the navigation is cancelled, the
    // sign-in opens in the default browser instead, and the window is
    // left sitting on a blank page.
    public static bool IsMuseHost(string host) =>
        IsHostOrSubdomain(host, "muse.ai") ||
        IsHostOrSubdomain(host, "instagram.com") ||
        IsHostOrSubdomain(host, "meta.com") ||
        IsHostOrSubdomain(host, "facebook.com") ||
        host.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("login.live.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("appleid.apple.com", StringComparison.OrdinalIgnoreCase);

    // Adobe Express and Adobe's sign-in hosts.
    public static bool IsAdobeHost(string host) =>
        IsHostOrSubdomain(host, "adobe.com") ||
        IsHostOrSubdomain(host, "adobelogin.com");

    private static bool IsHostOrSubdomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    // True when the URI may load inside a WebView whose session hosts
    // satisfy isAllowedHost. Non-web schemes (about:, data:, blob:)
    // are WebView internals rather than sites, so they stay in-app;
    // unparseable URIs are left for the WebView to reject on its own.
    public static bool MayNavigateInApp(string? uri, Func<string, bool> isAllowedHost)
    {
        if (string.IsNullOrEmpty(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return true;
        }
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            return true;
        }
        return isAllowedHost(parsed.Host);
    }

    public static void OpenInDefaultBrowser(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch { /* nowhere sensible to report this from a nav handler */ }
    }
}
