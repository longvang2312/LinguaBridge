using Microsoft.AspNetCore.Identity;

namespace AppInHouse.Identity.Infrastructure.Identity.Models;

public class ApplicationUser : IdentityUser
{
    public const int FullNameMaxLength = 150;

    public string FullName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
}
