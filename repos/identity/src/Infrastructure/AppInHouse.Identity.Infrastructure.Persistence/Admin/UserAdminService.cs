using AppInHouse.Identity.Application.Abstractions;
using AppInHouse.Identity.Application.Admin.Audit;
using AppInHouse.Identity.Application.Admin.Common;
using AppInHouse.Identity.Application.Admin.Security;
using AppInHouse.Identity.Application.Admin.Users;
using AppInHouse.Identity.Infrastructure.Identity.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.Identity.Infrastructure.Persistence.Admin;

/// <summary>User administration. Passwords are only ever handed to ASP.NET Core Identity; they are never stored in audit data.</summary>
public sealed class UserAdminService : IUserAdminService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAdminAuthorizer _authorizer;
    private readonly IAuditService _audit;
    private readonly IPermissionInvalidationService _invalidation;

    public UserAdminService(
        UserManager<ApplicationUser> userManager,
        IAdminAuthorizer authorizer,
        IAuditService audit,
        IPermissionInvalidationService invalidation)
    {
        _userManager = userManager;
        _authorizer = authorizer;
        _audit = audit;
        _invalidation = invalidation;
    }

    public async Task<PagedResult<UserDto>> ListAsync(UserListQuery query, CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireGlobalAdmin();

        var users = _userManager.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            users = users.Where(u => u.Email!.Contains(search) || u.FullName.Contains(search));
        }

        if (query.IsActive is { } isActive)
            users = users.Where(u => u.IsActive == isActive);

        var page = await users.OrderBy(u => u.Email).ToPagedResultAsync(query, cancellationToken);
        return new PagedResult<UserDto>(page.Items.Select(ToDto).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<UserDto> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireGlobalAdmin();

        return ToDto(await FindAsync(id));
    }

    public async Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireGlobalAdmin();

        var email = request.Email.Trim();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FullName = request.FullName.Trim(),
            EmailConfirmed = request.EmailConfirmed,
            IsActive = true,
        };

        IdentityResultMapper.ThrowIfFailed(await _userManager.CreateAsync(user, request.Password));

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.Create, AuditEntities.User, user.Id, NewValues: Snapshot(user)),
            cancellationToken);

        return ToDto(user);
    }

    public async Task<UserDto> UpdateAsync(string id, UpdateUserRequest request, CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireGlobalAdmin();

        if (request.IsActive is null)
            throw new BadRequestException("IsActive is required.");

        var user = await FindAsync(id);
        if (request.IsActive == false && IsSelf(caller, user))
            throw new BadRequestException("You cannot deactivate your own account.");

        var before = Snapshot(user);
        var activeChanged = user.IsActive != request.IsActive;
        user.FullName = request.FullName.Trim();
        user.IsActive = request.IsActive.Value;

        IdentityResultMapper.ThrowIfFailed(await _userManager.UpdateAsync(user));

        if (activeChanged)
        {
            // Sign the user out everywhere when deactivated and make the new state effective at once.
            if (!user.IsActive)
                IdentityResultMapper.ThrowIfFailed(await _userManager.UpdateSecurityStampAsync(user));

            await _invalidation.InvalidateUserAsync(user.Id, cancellationToken);
        }

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.Update, AuditEntities.User, user.Id, OldValues: before, NewValues: Snapshot(user)),
            cancellationToken);

        return ToDto(user);
    }

    public async Task<UserDto> LockAsync(string id, LockUserRequest request, CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireGlobalAdmin();

        var until = request.LockedUntil ?? DateTimeOffset.UtcNow.AddYears(100);
        if (until <= DateTimeOffset.UtcNow)
            throw new BadRequestException("LockedUntil must lie in the future.");

        var user = await FindAsync(id);
        if (IsSelf(caller, user))
            throw new BadRequestException("You cannot lock your own account.");

        var before = Snapshot(user);
        IdentityResultMapper.ThrowIfFailed(await _userManager.SetLockoutEnabledAsync(user, true));
        IdentityResultMapper.ThrowIfFailed(await _userManager.SetLockoutEndDateAsync(user, until));
        IdentityResultMapper.ThrowIfFailed(await _userManager.UpdateSecurityStampAsync(user));

        await _invalidation.InvalidateUserAsync(user.Id, cancellationToken);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.Lock, AuditEntities.User, user.Id, OldValues: before, NewValues: Snapshot(user)),
            cancellationToken);

        return ToDto(user);
    }

    public async Task<UserDto> UnlockAsync(string id, CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireGlobalAdmin();

        var user = await FindAsync(id);
        var before = Snapshot(user);
        IdentityResultMapper.ThrowIfFailed(await _userManager.SetLockoutEndDateAsync(user, null));
        IdentityResultMapper.ThrowIfFailed(await _userManager.ResetAccessFailedCountAsync(user));

        await _invalidation.InvalidateUserAsync(user.Id, cancellationToken);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.Unlock, AuditEntities.User, user.Id, OldValues: before, NewValues: Snapshot(user)),
            cancellationToken);

        return ToDto(user);
    }

    public async Task ResetPasswordAsync(string id, ResetPasswordRequest request, CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireGlobalAdmin();

        var user = await FindAsync(id);
        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        IdentityResultMapper.ThrowIfFailed(await _userManager.ResetPasswordAsync(user, token, request.NewPassword));

        // Deliberately no password (nor hash) in the audit record.
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.ResetPassword, AuditEntities.User, user.Id, NewValues: new { user.Id, user.Email }),
            cancellationToken);
    }

    private async Task<ApplicationUser> FindAsync(string id)
        => await _userManager.FindByIdAsync(id) ?? throw new NotFoundException("The user was not found.");

    private static bool IsSelf(AdminCaller caller, ApplicationUser user) => caller.UserId == user.Id;

    private static UserDto ToDto(ApplicationUser u)
        => new(
            u.Id,
            u.Email ?? string.Empty,
            u.FullName,
            u.IsActive,
            u.EmailConfirmed,
            u.LockoutEnd > DateTimeOffset.UtcNow,
            u.LockoutEnd,
            u.CreatedAt,
            u.LastLoginAt);

    private static object Snapshot(ApplicationUser u)
        => new { u.Id, u.Email, u.FullName, u.IsActive, u.EmailConfirmed, u.LockoutEnd };
}
