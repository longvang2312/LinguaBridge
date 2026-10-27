namespace AppInHouse.Identity.Application.Authorization;

/// <param name="LockoutEnd">Identity lockout end; the user is locked out while it lies in the future.</param>
public sealed record UserAccessState(bool IsActive, DateTimeOffset? LockoutEnd);

public sealed record ProjectAccessInfo(Guid Id, bool IsActive, bool IsDeleted);

public sealed record ProjectMembership(bool IsProjectAdmin, bool IsBlocked);

/// <summary>A permission assigned (or explicitly revoked) directly to a user in a project.</summary>
public sealed record DirectPermissionAssignment(string PermissionCode, bool IsRevoked);
