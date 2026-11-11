using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppInHouse.SharedServices.Web.Pages;

/// <summary>The older address of the chat section (it was only Telegram): it goes to <c>/Chat</c>, keeping the chosen project.</summary>
public class TelegramModel : PageModel
{
    public IActionResult OnGet(string? project) => RedirectToPage("/Chat", string.IsNullOrWhiteSpace(project) ? null : new { project });
}
