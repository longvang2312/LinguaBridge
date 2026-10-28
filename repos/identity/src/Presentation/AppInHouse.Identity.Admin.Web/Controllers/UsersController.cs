using AppInHouse.Identity.Admin.Web.Infrastructure;
using AppInHouse.Identity.Admin.Web.ViewModels;
using AppInHouse.Identity.Application.Admin.Projects;
using AppInHouse.Identity.Application.Admin.Security;
using AppInHouse.Identity.Application.Admin.Users;
using Microsoft.AspNetCore.Mvc;

namespace AppInHouse.Identity.Admin.Web.Controllers;

public class UsersController : PortalController
{
    private readonly IUserAdminService _users;
    private readonly IProjectAdminService _projects;
    private readonly IProjectAssignmentService _assignments;
    private readonly ICurrentCaller _caller;

    public UsersController(
        IUserAdminService users,
        IProjectAdminService projects,
        IProjectAssignmentService assignments,
        ICurrentCaller caller)
    {
        _users = users;
        _projects = projects;
        _assignments = assignments;
        _caller = caller;
    }

    public async Task<IActionResult> Index(UserListQuery query, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            query = new UserListQuery();

        var result = await _users.ListAsync(query, cancellationToken);
        return View(new UserListViewModel(query, result.Items, PagerModel.From(result)));
    }

    [HttpGet]
    public IActionResult Create() => View(new CreateUserRequest());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateUserRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            request.Password = string.Empty; // never echo a password back into the page
            return View(request);
        }

        var user = await _users.CreateAsync(request, cancellationToken);
        Success($"User {user.Email} was created.");
        return RedirectToAction(nameof(Details), new { id = user.Id });
    }

    public async Task<IActionResult> Details(string id, CancellationToken cancellationToken)
    {
        var user = await _users.GetAsync(id, cancellationToken);
        var memberships = await _assignments.ListUserProjectsAsync(id, cancellationToken);

        var member = memberships.Select(m => m.ProjectId).ToHashSet();
        var allProjects = await AllAsync(page => _projects.ListAsync(
            new ProjectListQuery { IsActive = true, Page = page, PageSize = 100 }, cancellationToken));

        return View(new UserDetailsViewModel(
            user, memberships, allProjects.Where(p => !member.Contains(p.Id)).ToList(), user.Id == _caller.UserId));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(string id, UpdateUserRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            Failure(ModelErrors());
            return RedirectToAction(nameof(Details), new { id });
        }

        await _users.UpdateAsync(id, request, cancellationToken);
        Success("The user was updated.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Lock(string id, DateTimeOffset? lockedUntil, CancellationToken cancellationToken)
    {
        await _users.LockAsync(id, new LockUserRequest { LockedUntil = lockedUntil }, cancellationToken);
        Success("The user was locked.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Unlock(string id, CancellationToken cancellationToken)
    {
        await _users.UnlockAsync(id, cancellationToken);
        Success("The user was unlocked.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(string id, ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            Failure(ModelErrors());
            return RedirectToAction(nameof(Details), new { id });
        }

        await _users.ResetPasswordAsync(id, request, cancellationToken);
        Success("The password was changed.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignProject(string id, Guid projectId, bool isProjectAdmin, CancellationToken cancellationToken)
    {
        await _assignments.AssignUserAsync(projectId, new AssignProjectUserRequest { UserId = id, IsProjectAdmin = isProjectAdmin }, cancellationToken);
        Success("The user was assigned to the project.");
        return RedirectToAction(nameof(Details), new { id });
    }
}
