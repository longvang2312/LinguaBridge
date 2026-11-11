using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppInHouse.SharedServices.Web.Pages;

/// <summary>The storage section: the configuration list filtered to the Storage channel (the media browser comes with the storage task).</summary>
public class StorageModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Configurations/Index", new { channel = "Storage" });
}
