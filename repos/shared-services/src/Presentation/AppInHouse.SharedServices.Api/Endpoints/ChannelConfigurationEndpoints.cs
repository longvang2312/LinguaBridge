using System.Security.Claims;
using System.Text.Json;
using AppInHouse.SharedServices.Api.Auth;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Domain;
using Microsoft.AspNetCore.Mvc;

namespace AppInHouse.SharedServices.Api.Endpoints;

/// <summary>
/// Body of "save a configuration". <see cref="Secret"/> is write-only (no response ever contains it): omit it, or send the mask
/// back, to keep the stored secret. <see cref="Settings"/> is a JSON object without secrets.
/// </summary>
public sealed record SaveChannelConfigurationRequest(
    string ProjectCode, ChannelType ChannelType, ProviderType ProviderType, string Name, JsonElement? Settings = null, string? Secret = null);

public sealed record RollbackConfigurationRequest(string ProjectCode, ChannelType ChannelType, string Name, int? TargetVersion = null);

public static class ChannelConfigurationEndpoints
{
    public static IEndpointRouteBuilder MapChannelConfigurationEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/channel-configurations").WithTags("Channel configurations");

        // A key of a project works on that project only; the platform's keys (project "default") and bearer tokens work on all.
        group.MapGet("", async (
                [FromQuery] string? projectCode, [FromQuery] ChannelType? channelType, [FromQuery] ConfigurationStatus? status,
                ClaimsPrincipal user, IChannelConfigManager manager, CancellationToken ct) =>
            {
                if (!ProjectAccess.TryResolve(user, projectCode, out var project, allowAll: true))
                    return ProjectAccess.Forbidden("An API key can only list the configurations of its own project.");
                return Results.Ok(await manager.ListAsync(new ChannelConfigurationQuery(project, channelType, status), ct));
            })
            .RequireAuthorization(Policies.ConfigRead)
            .WithName("ListChannelConfigurations")
            .WithSummary("Lists configurations (every version), secrets masked.");

        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, IChannelConfigManager manager, CancellationToken ct) =>
            {
                var configuration = await manager.GetAsync(id, ct);
                return ProjectAccess.CanAct(user, configuration.ProjectCode) ? Results.Ok(configuration) : ProjectAccess.Forbidden("This configuration belongs to another project.");
            })
            .RequireAuthorization(Policies.ConfigRead)
            .WithName("GetChannelConfiguration");

        group.MapPost("", async (SaveChannelConfigurationRequest request, ClaimsPrincipal user, IChannelConfigManager manager, CancellationToken ct) =>
            {
                if (!string.IsNullOrWhiteSpace(request.ProjectCode) && !ProjectAccess.CanAct(user, request.ProjectCode))
                    return ProjectAccess.Forbidden("An API key can only save configurations for its own project.");

                var saved = await manager.SaveAsync(new SaveChannelConfigurationCommand(
                    request.ProjectCode, request.ChannelType, request.ProviderType, request.Name,
                    request.Settings is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } settings ? settings.GetRawText() : "{}",
                    request.Secret), ct);
                return Results.Created($"/api/v1/channel-configurations/{saved.Id}", saved);
            })
            .RequireAuthorization(Policies.ConfigWrite)
            .WithName("SaveChannelConfiguration")
            .WithSummary("Creates version 1, or the next version, as a Draft. The secret is stored protected and never returned.");

        group.MapPost("/{id:guid}/activate", async (Guid id, ClaimsPrincipal user, IChannelConfigManager manager, CancellationToken ct) =>
            {
                var configuration = await manager.GetAsync(id, ct);
                return ProjectAccess.CanAct(user, configuration.ProjectCode)
                    ? Results.Ok(await manager.ActivateAsync(id, ct))
                    : ProjectAccess.Forbidden("This configuration belongs to another project.");
            })
            .RequireAuthorization(Policies.ConfigWrite)
            .WithName("ActivateChannelConfiguration")
            .WithSummary("Makes this version the active one; the previous active version is archived.");

        group.MapPost("/rollback", async (RollbackConfigurationRequest request, ClaimsPrincipal user, IChannelConfigManager manager, CancellationToken ct) =>
            {
                if (!string.IsNullOrWhiteSpace(request.ProjectCode) && !ProjectAccess.CanAct(user, request.ProjectCode))
                    return ProjectAccess.Forbidden("An API key can only roll back configurations of its own project.");

                return Results.Ok(await manager.RollbackAsync(
                    new RollbackConfigurationCommand(request.ProjectCode, request.ChannelType, request.Name, request.TargetVersion), ct));
            })
            .RequireAuthorization(Policies.ConfigWrite)
            .WithName("RollbackChannelConfiguration")
            .WithSummary("Goes back to an earlier version (the one active before, or the version given) at once.");

        return routes;
    }
}
