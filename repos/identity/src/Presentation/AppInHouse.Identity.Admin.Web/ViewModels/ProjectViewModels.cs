using AppInHouse.Identity.Application.Admin.Permissions;
using AppInHouse.Identity.Application.Admin.Projects;
using AppInHouse.Identity.Application.Admin.Roles;

namespace AppInHouse.Identity.Admin.Web.ViewModels;

public sealed record ProjectListViewModel(ProjectListQuery Query, IReadOnlyList<ProjectDto> Items, PagerModel Pager, bool CanCreate);

public sealed record ProjectDetailsViewModel(
    ProjectDto Project,
    IReadOnlyList<ProjectMemberDto> Members,
    IReadOnlyDictionary<string, string> RoleNames,
    bool IsGlobalAdmin);

public sealed record MemberAccessViewModel(
    ProjectDto Project,
    ProjectMemberDto Member,
    IReadOnlyList<RoleDto> Roles,
    IReadOnlyList<PermissionDto> Permissions);

public sealed class ProjectEditViewModel
{
    public Guid Id { get; set; }

    public string ProjectCode { get; set; } = string.Empty;

    public UpdateProjectRequest Form { get; set; } = new();
}
