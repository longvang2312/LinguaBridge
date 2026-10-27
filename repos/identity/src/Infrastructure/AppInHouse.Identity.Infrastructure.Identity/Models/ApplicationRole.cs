using Microsoft.AspNetCore.Identity;

namespace AppInHouse.Identity.Infrastructure.Identity.Models;

public class ApplicationRole : IdentityRole
{
    public const int DescriptionMaxLength = 500;

    public ApplicationRole()
    {
    }

    public ApplicationRole(string roleName) : base(roleName)
    {
    }

    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
