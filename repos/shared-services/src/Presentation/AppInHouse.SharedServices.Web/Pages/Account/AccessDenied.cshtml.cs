using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppInHouse.SharedServices.Web.Pages.Account;

/// <summary>Where the portal sends a signed-in user who lacks the administrator role. Anonymous, or the user could not even read it.</summary>
[AllowAnonymous]
public class AccessDeniedModel(PortalAuthenticationOptions authentication) : PageModel
{
    public string RequiredRole => string.IsNullOrWhiteSpace(authentication.RequiredRole) ? "quản trị" : authentication.RequiredRole;

    public void OnGet()
    {
    }
}
