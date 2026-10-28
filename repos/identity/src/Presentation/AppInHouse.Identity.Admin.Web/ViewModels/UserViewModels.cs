using AppInHouse.Identity.Application.Admin.Projects;
using AppInHouse.Identity.Application.Admin.Users;

namespace AppInHouse.Identity.Admin.Web.ViewModels;

public sealed record UserListViewModel(UserListQuery Query, IReadOnlyList<UserDto> Items, PagerModel Pager);

public sealed record UserDetailsViewModel(
    UserDto User,
    IReadOnlyList<UserProjectDto> Projects,
    IReadOnlyList<ProjectDto> AssignableProjects,
    bool IsCurrentUser);
