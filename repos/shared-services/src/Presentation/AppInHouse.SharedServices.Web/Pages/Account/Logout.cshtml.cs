using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppInHouse.SharedServices.Web.Pages.Account;

/// <summary>
/// Signing out is a POST (with the anti-forgery token), so another site cannot sign the administrator out with a link or an
/// image. The GET only shows the confirmation or, after the identity server sent the browser back, the "signed out" message.
/// </summary>
[AllowAnonymous]
public class LogoutModel(PortalAuthenticationOptions authentication) : PageModel
{
    public bool SignedOut { get; private set; }

    public void OnGet(bool signedOut = false) => SignedOut = signedOut;

    public async Task<IActionResult> OnPostAsync()
    {
        if (!authentication.Enabled) return LocalRedirect("/");

        // Clears the session cookie here and sends the browser to the identity server's end-session endpoint, which ends the
        // single sign-on session and brings the browser back to /signout-callback-oidc and then to the "signed out" message.
        var properties = new AuthenticationProperties { RedirectUri = "/Account/Logout?signedOut=true" };
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return SignOut(properties, CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme);
    }
}
