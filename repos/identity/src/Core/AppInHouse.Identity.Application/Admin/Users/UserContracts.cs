using System.ComponentModel.DataAnnotations;
using AppInHouse.Identity.Application.Admin.Common;

namespace AppInHouse.Identity.Application.Admin.Users;

public sealed record UserDto(
    string Id,
    string Email,
    string FullName,
    bool IsActive,
    bool EmailConfirmed,
    bool IsLockedOut,
    DateTimeOffset? LockoutEnd,
    DateTime CreatedAt,
    DateTime? LastLoginAt);

public class UserListQuery : PagingQuery
{
    [StringLength(100)]
    public string? Search { get; set; }

    public bool? IsActive { get; set; }
}

public sealed class CreateUserRequest
{
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required, StringLength(150)]
    public string FullName { get; set; } = string.Empty;

    /// <summary>Validated against the Identity password policy. Never logged or audited.</summary>
    [Required, StringLength(128)]
    public string Password { get; set; } = string.Empty;

    public bool EmailConfirmed { get; set; }
}

public sealed class UpdateUserRequest
{
    [Required, StringLength(150)]
    public string FullName { get; set; } = string.Empty;

    [Required]
    public bool? IsActive { get; set; }
}

public sealed class LockUserRequest
{
    /// <summary>When the lock ends; omitted means locked until an administrator unlocks the account.</summary>
    public DateTimeOffset? LockedUntil { get; set; }
}

public sealed class ResetPasswordRequest
{
    /// <summary>Validated against the Identity password policy. Never logged or audited.</summary>
    [Required, StringLength(128)]
    public string NewPassword { get; set; } = string.Empty;
}

public interface IUserAdminService
{
    Task<PagedResult<UserDto>> ListAsync(UserListQuery query, CancellationToken cancellationToken = default);

    Task<UserDto> GetAsync(string id, CancellationToken cancellationToken = default);

    Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken = default);

    Task<UserDto> UpdateAsync(string id, UpdateUserRequest request, CancellationToken cancellationToken = default);

    Task<UserDto> LockAsync(string id, LockUserRequest request, CancellationToken cancellationToken = default);

    Task<UserDto> UnlockAsync(string id, CancellationToken cancellationToken = default);

    Task ResetPasswordAsync(string id, ResetPasswordRequest request, CancellationToken cancellationToken = default);
}
