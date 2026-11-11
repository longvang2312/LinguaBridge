using System.Text.Json;
using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Domain;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.SharedServices.Persistence;

internal sealed class ChannelConfigRepository(SharedServicesDbContext db) : IChannelConfigRepository
{
    public async Task<IReadOnlyList<ChannelConfiguration>> GetSeriesAsync(
        string projectCode, ChannelType channelType, string name, CancellationToken cancellationToken = default) =>
        await db.ChannelConfigurations
            .Where(c => c.ProjectCode == projectCode && c.ChannelType == channelType && c.Name == name)
            .ToListAsync(cancellationToken);

    public Task<ChannelConfiguration?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.ChannelConfigurations.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<IReadOnlyList<ChannelConfiguration>> ListAsync(
        string? projectCode, ChannelType? channelType, ConfigurationStatus? status, CancellationToken cancellationToken = default)
    {
        var query = db.ChannelConfigurations.AsNoTracking();
        if (projectCode is not null) query = query.Where(c => c.ProjectCode == projectCode);
        if (channelType is { } channel) query = query.Where(c => c.ChannelType == channel);
        if (status is { } wanted) query = query.Where(c => c.Status == wanted);
        return await query.ToListAsync(cancellationToken);
    }

    public async Task AddAsync(ChannelConfiguration configuration, CancellationToken cancellationToken = default) =>
        await db.ChannelConfigurations.AddAsync(configuration, cancellationToken);
}

internal sealed class ApiKeyRepository(SharedServicesDbContext db) : IApiKeyRepository
{
    public async Task<IReadOnlyList<ApiKey>> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default) =>
        await db.ApiKeys.Where(k => k.KeyPrefix == keyPrefix).ToListAsync(cancellationToken);

    public async Task AddAsync(ApiKey key, CancellationToken cancellationToken = default) =>
        await db.ApiKeys.AddAsync(key, cancellationToken);

    public Task<bool> AnyAsync(CancellationToken cancellationToken = default) => db.ApiKeys.AnyAsync(cancellationToken);
}

internal sealed class EfUnitOfWork(SharedServicesDbContext db) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => db.SaveChangesAsync(cancellationToken);
}

internal sealed class AuditLogWriter(SharedServicesDbContext db, ICurrentActor actor, TimeProvider clock) : IAuditLogWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Record(string action, string resourceType, string resourceId, object? details = null) =>
        db.AuditLogs.Add(AuditLog.Create(
            actor.Name, action, resourceType, resourceId,
            details is null ? null : JsonSerializer.Serialize(details, Json),
            clock.GetUtcNow()));
}
