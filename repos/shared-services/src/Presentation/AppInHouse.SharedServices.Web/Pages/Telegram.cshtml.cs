using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppInHouse.SharedServices.Web.Pages;

/// <summary>The older address of the Telegram settings (the bots are now channels of the notification service): it goes to <c>/Notifications/Channels</c>, where the bots are now, keeping the chosen project.</summary>
public class TelegramModel : PageModel
{
    public IActionResult OnGet(string? project) => RedirectToPage("/Notifications/Channels", string.IsNullOrWhiteSpace(project) ? null : new { project });
}
