using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppInHouse.SharedServices.Web.Pages;

/// <summary>The Telegram section: the configuration list filtered to the Telegram channel.</summary>
public class TelegramModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Configurations/Index", new { channel = "Telegram" });
}
