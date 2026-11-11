using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppInHouse.SharedServices.Web.Pages;

/// <summary>The email section: the configuration list filtered to the Email channel (editing and send-test come with the email task).</summary>
public class EmailModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Configurations/Index", new { channel = "Email" });
}
