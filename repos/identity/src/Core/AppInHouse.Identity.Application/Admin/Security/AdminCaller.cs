using AppInHouse.Identity.Application.Admin.Common;

namespace AppInHouse.Identity.Application.Admin.Security;

/// <summary>Who is calling the admin API and what they may administer.</summary>
public interface ICurrentCaller
{
    /// <summary>The authenticated subject, or null when anonymous.</summary>
    string? UserId { get; }

    string? IpAddress { get; }
}

/// <param name="IsEligible">The account exists, is active and is not locked out. Ineligible callers have no admin rights.</param>
/// <param name="AdministeredProjectIds">Active projects in which the caller is an unblocked project administrator.</param>
public sealed record AdminAccess(bool IsEligible, bool IsGlobalAdmin, IReadOnlySet<Guid> AdministeredProjectIds)
{
    public static AdminAccess None { get; } = new(false, false, new HashSet<Guid>());
}

/// <summary>Reads the caller's admin rights (implemented by the persistence layer).</summary>
public interface IAdminAccessReader
{
    Task<AdminAccess> ReadAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The rights of the current caller. Every admin use case starts by asserting one of these; nothing is allowed by default.
/// </summary>
public sealed class AdminCaller
{
    public AdminCaller(string userId, AdminAccess access)
    {
        UserId = userId;
        IsGlobalAdmin = access.IsEligible && access.IsGlobalAdmin;
        AdministeredProjectIds = access.IsEligible ? access.AdministeredProjectIds : new HashSet<Guid>();
    }

    public string UserId { get; }
    public bool IsGlobalAdmin { get; }
    public IReadOnlySet<Guid> AdministeredProjectIds { get; }

    public bool IsAnyAdmin => IsGlobalAdmin || AdministeredProjectIds.Count > 0;

    public bool CanAdminister(Guid projectId) => IsGlobalAdmin || AdministeredProjectIds.Contains(projectId);

    public void RequireGlobalAdmin()
    {
        if (!IsGlobalAdmin)
            throw new ForbiddenException("This operation requires a global administrator.");
    }

    public void RequireAnyAdmin()
    {
        if (!IsAnyAdmin)
            throw new ForbiddenException("This operation requires an administrator.");
    }

    /// <summary>Global administrators or administrators of exactly this project.</summary>
    public void RequireProjectAdmin(Guid projectId)
    {
        if (!CanAdminister(projectId))
            throw new ForbiddenException("This operation requires administrator rights on the project.");
    }
}

public interface IAdminAuthorizer
{
    /// <summary>The rights of the current caller; throws <see cref="ForbiddenException"/> for anonymous callers.</summary>
    Task<AdminCaller> GetCallerAsync(CancellationToken cancellationToken = default);
}

public sealed class AdminAuthorizer : IAdminAuthorizer
{
    private readonly ICurrentCaller _currentCaller;
    private readonly IAdminAccessReader _reader;
    private AdminCaller? _cached;

    public AdminAuthorizer(ICurrentCaller currentCaller, IAdminAccessReader reader)
    {
        _currentCaller = currentCaller;
        _reader = reader;
    }

    public async Task<AdminCaller> GetCallerAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null)
            return _cached;

        var userId = _currentCaller.UserId;
        if (string.IsNullOrWhiteSpace(userId))
            throw new ForbiddenException("The caller could not be identified.");

        // Scoped per request: the rights are read once, so one request cannot see two different answers.
        return _cached = new AdminCaller(userId, await _reader.ReadAsync(userId, cancellationToken));
    }
}
