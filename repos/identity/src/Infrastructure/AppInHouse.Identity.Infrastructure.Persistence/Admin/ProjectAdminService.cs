using AppInHouse.Identity.Application.Abstractions;
using AppInHouse.Identity.Application.Admin.Audit;
using AppInHouse.Identity.Application.Admin.Common;
using AppInHouse.Identity.Application.Admin.Projects;
using AppInHouse.Identity.Application.Admin.Security;
using AppInHouse.Identity.Domain.Entities;
using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.Identity.Infrastructure.Persistence.Admin;

public sealed class ProjectAdminService : IProjectAdminService
{
    private readonly AppAuthDbContext _auth;
    private readonly IAdminAuthorizer _authorizer;
    private readonly IAuditService _audit;
    private readonly IPermissionInvalidationService _invalidation;

    public ProjectAdminService(
        AppAuthDbContext auth,
        IAdminAuthorizer authorizer,
        IAuditService audit,
        IPermissionInvalidationService invalidation)
    {
        _auth = auth;
        _authorizer = authorizer;
        _audit = audit;
        _invalidation = invalidation;
    }

    public async Task<PagedResult<ProjectDto>> ListAsync(ProjectListQuery query, CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireAnyAdmin();

        var projects = _auth.Projects.AsNoTracking().Where(p => !p.IsDeleted);

        // Project administrators only ever see the projects they administer.
        if (!caller.IsGlobalAdmin)
        {
            var visible = caller.AdministeredProjectIds.ToList();
            projects = projects.Where(p => visible.Contains(p.Id));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            projects = projects.Where(p => p.ProjectCode.Contains(search) || p.Name.Contains(search));
        }

        if (query.IsActive is { } isActive)
            projects = projects.Where(p => p.IsActive == isActive);

        return await projects
            .OrderBy(p => p.ProjectCode)
            .Select(p => new ProjectDto(p.Id, p.ProjectCode, p.Name, p.Description, p.IsActive, p.CreatedAt, p.ConcurrencyToken))
            .ToPagedResultAsync(query, cancellationToken);
    }

    public async Task<ProjectDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Authorize before touching the data so that probing ids reveals nothing.
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireProjectAdmin(id);

        var project = await FindAsync(id, tracking: false, cancellationToken);
        return ToDto(project);
    }

    public async Task<ProjectDto> CreateAsync(CreateProjectRequest request, CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireGlobalAdmin();

        var project = new Project(request.ProjectCode, request.Name, request.Description);

        var code = project.ProjectCode.ToUpper();
        if (await _auth.Projects.AnyAsync(p => !p.IsDeleted && p.ProjectCode.ToUpper() == code, cancellationToken))
            throw new ConflictException($"A project with the code '{project.ProjectCode}' already exists.");

        _auth.Projects.Add(project);
        try
        {
            await _auth.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Lost a race against another creation of the same code (filtered unique index).
            throw new ConflictException($"A project with the code '{project.ProjectCode}' already exists.");
        }

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.Create, AuditEntities.Project, project.Id.ToString(), project.Id, NewValues: Snapshot(project)),
            cancellationToken);

        return ToDto(project);
    }

    public async Task<ProjectDto> UpdateAsync(Guid id, UpdateProjectRequest request, CancellationToken cancellationToken = default)
    {
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireProjectAdmin(id);

        var project = await FindAsync(id, tracking: true, cancellationToken);
        if (request.ConcurrencyToken is not null && request.ConcurrencyToken != project.ConcurrencyToken)
            throw new ConflictException("The project was modified by someone else. Reload it and try again.");

        var before = Snapshot(project);
        project.UpdateDetails(request.Name, request.Description);
        await SaveAsync(cancellationToken);

        await _audit.WriteAsync(
            new AuditEntry(AuditActions.Update, AuditEntities.Project, project.Id.ToString(), project.Id, before, Snapshot(project)),
            cancellationToken);

        return ToDto(project);
    }

    public async Task<ProjectDto> SetStatusAsync(Guid id, SetProjectStatusRequest request, CancellationToken cancellationToken = default)
    {
        // Locking or re-activating a whole project is a platform decision, not a project one.
        var caller = await _authorizer.GetCallerAsync(cancellationToken);
        caller.RequireGlobalAdmin();

        if (request.IsActive is null)
            throw new BadRequestException("IsActive is required.");

        var project = await FindAsync(id, tracking: true, cancellationToken);
        var before = Snapshot(project);

        if (request.IsActive == true)
            project.Activate();
        else
            project.Deactivate();

        await SaveAsync(cancellationToken);

        // Everybody's permissions in the project change at once.
        await _invalidation.InvalidateProjectAsync(project.ProjectCode, cancellationToken);
        await _audit.WriteAsync(
            new AuditEntry(AuditActions.StatusChange, AuditEntities.Project, project.Id.ToString(), project.Id, before, Snapshot(project)),
            cancellationToken);

        return ToDto(project);
    }

    private async Task<Project> FindAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? _auth.Projects : _auth.Projects.AsNoTracking();
        return await query.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("The project was not found.");
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _auth.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The project was modified by someone else. Reload it and try again.");
        }
    }

    private static ProjectDto ToDto(Project p)
        => new(p.Id, p.ProjectCode, p.Name, p.Description, p.IsActive, p.CreatedAt, p.ConcurrencyToken);

    private static object Snapshot(Project p) => new { p.Id, p.ProjectCode, p.Name, p.Description, p.IsActive };
}
