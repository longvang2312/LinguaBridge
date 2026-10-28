using AppInHouse.Identity.Admin.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;

namespace AppInHouse.Identity.Admin.Web.Controllers;

public class AccountController : PortalController
{
    /// <summary>Ends the portal session and the STS session. POST only (with an antiforgery token) so it cannot be forced by a link.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Logout()
        => SignOut(
            new AuthenticationProperties { RedirectUri = Url.Action("SignedOut", "Account") },
            CookieAuthenticationDefaults.AuthenticationScheme,
            OpenIdConnectDefaults.AuthenticationScheme);

    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
    public IActionResult SignedOut() => View();
}
