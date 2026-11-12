using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.Application.Platform;

/// <summary>
/// Looks up the project (tenant) and refuses what its limits do not allow. A project that has no row is not limited: the rows are the
/// platform's way of knowing its applications, and the services keep working for a project code nobody registered yet.
/// The daily quotas count what was actually delivered (the successful rows of the delivery log) since 00:00 UTC. The check and the send are
/// not one step, so requests that arrive together may overshoot the quota by a few messages.
/// </summary>
public sealed class QuotaGuard(IProjectRepository projects, IUsageRepository usage, TimeProvider clock) : IQuotaGuard
{
    public async Task EnsureCanSendAsync(string projectCode, ChannelType channel, CancellationToken cancellationToken = default)
    {
        var tenant = await projects.GetByCodeAsync(projectCode, cancellationToken);
        if (tenant is null) return;
        if (!tenant.IsActive) throw new ProjectInactiveException(tenant.ProjectCode);

        var chat = channel != ChannelType.Email;
        var limit = chat ? tenant.DailyChatQuota : tenant.DailyEmailQuota;
        if (limit is not { } allowed) return;

        var (from, to) = Today(clock.GetUtcNow());
        var channels = chat ? ProviderCatalog.BotChannels : [ChannelType.Email];
        var used = await usage.CountDeliveriesAsync(tenant.ProjectCode, channels, DeliveryStatus.Sent, from, to, cancellationToken);
        if (used >= allowed)
        {
            throw new QuotaExceededException(
                chat ? QuotaKind.DailyChat : QuotaKind.DailyEmail, allowed, used, to,
                $"Project '{tenant.ProjectCode}' has used its daily quota of {allowed} {(chat ? "chat messages" : "emails")}. It starts again at 00:00 UTC.");
        }
    }

    public async Task EnsureCanStoreAsync(string projectCode, long incomingBytes, CancellationToken cancellationToken = default)
    {
        var tenant = await projects.GetByCodeAsync(projectCode, cancellationToken);
        if (tenant is null) return;
        if (!tenant.IsActive) throw new ProjectInactiveException(tenant.ProjectCode);
        if (tenant.StorageQuotaBytes is not { } allowed) return;

        var used = await usage.SumStorageBytesAsync(tenant.ProjectCode, cancellationToken);
        if (used + Math.Max(incomingBytes, 1) > allowed)
        {
            throw new QuotaExceededException(
                QuotaKind.Storage, allowed, used, null,
                $"Project '{tenant.ProjectCode}' has used {used} of its {allowed} bytes of storage. Delete files or ask for a larger quota.");
        }
    }

    /// <summary>The current UTC day as [from, to).</summary>
    public static (DateTimeOffset From, DateTimeOffset To) Today(DateTimeOffset now)
    {
        var from = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        return (from, from.AddDays(1));
    }
}

public sealed class ProjectManager(
    IProjectRepository projects,
    IUsageRepository usage,
    IUnitOfWork unitOfWork,
    IAuditLogWriter audit,
    TimeProvider clock) : IProjectManager
{
    public async Task<IReadOnlyList<ProjectDto>> ListAsync(string? onlyProject, CancellationToken cancellationToken = default)
    {
        var all = await projects.ListAsync(cancellationToken);
        return [.. all
            .Where(p => onlyProject is null || p.ProjectCode == onlyProject)
            .OrderBy(p => p.ProjectCode == ProjectTenant.PlatformCode ? 0 : 1).ThenBy(p => p.ProjectCode, StringComparer.Ordinal)
            .Select(ToDto)];
    }

    public async Task<ProjectDto> CreateAsync(CreateProjectCommand command, CancellationToken cancellationToken = default)
    {
        var code = ChannelConfigurationValidator.NormalizeProjectCode(command.ProjectCode);
        if (!ChannelConfigurationValidator.IsValidProjectCode(code))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(command.ProjectCode)] = ["Use 1 to 50 lower-case letters, digits, '-' or '_', starting with a letter or digit."],
            });
        }

        if (await projects.GetByCodeAsync(code, cancellationToken) is not null) throw new DomainException($"Project '{code}' already exists.");

        ProjectTenant tenant;
        try
        {
            tenant = ProjectTenant.Create(code, command.Name, command.Description, command.Environment, command.DailyEmailQuota, command.DailyChatQuota, command.StorageQuotaBytes, clock.GetUtcNow());
        }
        catch (DomainException ex)
        {
            throw Invalid(ex);
        }

        projects.Add(tenant);
        audit.Record("CreateProject", "Project", code, new { tenant.Name, Environment = tenant.Environment.ToString(), tenant.DailyEmailQuota, tenant.DailyChatQuota, tenant.StorageQuotaBytes });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(tenant);
    }

    public async Task<ProjectDto> UpdateAsync(string projectCode, UpdateProjectCommand command, CancellationToken cancellationToken = default)
    {
        var code = ChannelConfigurationValidator.NormalizeProjectCode(projectCode);
        var tenant = await projects.GetByCodeAsync(code, cancellationToken) ?? throw new NotFoundException($"Project '{code}' does not exist.");

        try
        {
            tenant.Update(command.Name, command.Description, command.Environment, command.DailyEmailQuota, command.DailyChatQuota, command.StorageQuotaBytes, command.IsActive);
        }
        catch (DomainException ex)
        {
            throw Invalid(ex);
        }

        audit.Record("UpdateProject", "Project", code, new { tenant.Name, Environment = tenant.Environment.ToString(), tenant.DailyEmailQuota, tenant.DailyChatQuota, tenant.StorageQuotaBytes, tenant.IsActive });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(tenant);
    }

    public async Task<ProjectUsageDto> GetUsageAsync(string projectCode, CancellationToken cancellationToken = default)
    {
        var code = ChannelConfigurationValidator.NormalizeProjectCode(projectCode);
        var tenant = await projects.GetByCodeAsync(code, cancellationToken) ?? throw new NotFoundException($"Project '{code}' does not exist.");

        var now = clock.GetUtcNow();
        var (from, to) = QuotaGuard.Today(now);
        var emails = await usage.CountDeliveriesAsync(code, [ChannelType.Email], DeliveryStatus.Sent, from, to, cancellationToken);
        var chats = await usage.CountDeliveriesAsync(code, ProviderCatalog.BotChannels, DeliveryStatus.Sent, from, to, cancellationToken);
        return new ProjectUsageDto(
            code, DateOnly.FromDateTime(from.UtcDateTime), emails, tenant.DailyEmailQuota, chats, tenant.DailyChatQuota,
            await usage.SumStorageBytesAsync(code, cancellationToken), tenant.StorageQuotaBytes,
            await usage.CountFilesAsync(code, cancellationToken), await usage.CountActiveApiKeysAsync(code, now, cancellationToken));
    }

    private static ValidationException Invalid(DomainException ex) =>
        new(new Dictionary<string, string[]> { ["Project"] = [ex.Message] });

    private static ProjectDto ToDto(ProjectTenant p) =>
        new(p.ProjectCode, p.Name, p.Description, p.Environment.ToString(), p.DailyEmailQuota, p.DailyChatQuota, p.StorageQuotaBytes, p.IsActive, p.CreatedAt);
}
