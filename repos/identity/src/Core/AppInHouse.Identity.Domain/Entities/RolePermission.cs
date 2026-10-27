using AppInHouse.Identity.Domain.Common;

namespace AppInHouse.Identity.Domain.Entities;

/// <summary>Grants a <see cref="Permission"/> to a role (AspNetRoles).</summary>
public class RolePermission
{
    public const int RoleIdMaxLength = 450;

    // Required by EF Core.
    private RolePermission() { }

    public RolePermission(string roleId, Guid permissionId)
    {
        Id = Guid.NewGuid();
        RoleId = Guard.Required(roleId, RoleIdMaxLength, nameof(roleId));
        PermissionId = Guard.RequiredId(permissionId, nameof(permissionId));
        GrantedAt = DateTime.UtcNow;
    }

    public Guid Id { get; private set; }
    public string RoleId { get; private set; } = string.Empty;
    public Guid PermissionId { get; private set; }
    public DateTime GrantedAt { get; private set; }

    public Permission Permission { get; private set; } = null!;
}
