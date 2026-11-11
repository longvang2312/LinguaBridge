using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace AppInHouse.SharedServices.Web.Pages.Account;

/// <summary>Starts the sign-in at the identity server. Reached by a link; the identity server brings the user back to /signin-oidc.</summary>
[AllowAnonymous]
public class LoginModel(PortalAuthenticationOptions authentication) : PageModel
{
    public string ReturnUrl { get; private set; } = "/";

    /// <param name="returnUrl">Where to go after signing in; only an address on this site is accepted.</param>
    /// <param name="error">Set when a sign-in just failed: the page explains instead of starting another one.</param>
    /// <param name="switchAccount">Asks the identity server for a new login even if it still has a session.</param>
    public IActionResult OnGet(string? returnUrl = null, string? error = null, bool switchAccount = false)
    {
        ReturnUrl = Url.IsLocalUrl(returnUrl) && !returnUrl.StartsWith("/Account/", StringComparison.OrdinalIgnoreCase) ? returnUrl : "/";

        // Without the identity server (development) there is nothing to sign in to.
        if (!authentication.Enabled) return LocalRedirect(ReturnUrl);
        if (!string.IsNullOrEmpty(error)) return Page();

        var properties = new AuthenticationProperties { RedirectUri = ReturnUrl };
        if (switchAccount) properties.SetParameter(OpenIdConnectParameterNames.Prompt, "login");
        return Challenge(properties, OpenIdConnectDefaults.AuthenticationScheme);
    }
}
