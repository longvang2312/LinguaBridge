using System.ComponentModel.DataAnnotations;
using AppInHouse.Identity.Application.Admin.Common;
using AppInHouse.Identity.Domain.Entities;

namespace AppInHouse.Identity.Application.Admin.Projects;

public sealed record ProjectDto(
    Guid Id,
    string ProjectCode,
    string Name,
    string? Description,
    bool IsActive,
    DateTime CreatedAt,
    string ConcurrencyToken);

public class ProjectListQuery : PagingQuery
{
    /// <summary>Matches part of the code or the name.</summary>
    [StringLength(100)]
    public string? Search { get; set; }

    public bool? IsActive { get; set; }
}

public sealed class CreateProjectRequest
{
    [Required, StringLength(Project.ProjectCodeMaxLength)]
    public string ProjectCode { get; set; } = string.Empty;

    [Required, StringLength(Project.NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    [StringLength(Project.DescriptionMaxLength)]
    public string? Description { get; set; }
}

public sealed class UpdateProjectRequest
{
    [Required, StringLength(Project.NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    [StringLength(Project.DescriptionMaxLength)]
    public string? Description { get; set; }

    /// <summary>Optional optimistic concurrency token (from a previous read); a mismatch yields 409.</summary>
    [StringLength(Project.ConcurrencyTokenMaxLength)]
    public string? ConcurrencyToken { get; set; }
}

public sealed class SetProjectStatusRequest
{
    /// <summary>Nullable + required so that an omitted value is rejected instead of silently deactivating the project.</summary>
    [Required]
    public bool? IsActive { get; set; }
}

public interface IProjectAdminService
{
    Task<PagedResult<ProjectDto>> ListAsync(ProjectListQuery query, CancellationToken cancellationToken = default);

    Task<ProjectDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ProjectDto> CreateAsync(CreateProjectRequest request, CancellationToken cancellationToken = default);

    Task<ProjectDto> UpdateAsync(Guid id, UpdateProjectRequest request, CancellationToken cancellationToken = default);

    Task<ProjectDto> SetStatusAsync(Guid id, SetProjectStatusRequest request, CancellationToken cancellationToken = default);
}
