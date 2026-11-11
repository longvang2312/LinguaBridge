using System.Text.Json;
using AppInHouse.SharedServices.Application.Abstractions;
using AppInHouse.SharedServices.Application.Audit;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Application.Storage;
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

    public async Task<IReadOnlyList<ApiKey>> ListAsync(string? projectCode, CancellationToken cancellationToken = default)
    {
        var query = db.ApiKeys.AsNoTracking();
        if (projectCode is not null) query = query.Where(k => k.ProjectCode == projectCode);
        return await query.OrderByDescending(k => k.CreatedAt).ThenBy(k => k.Id).ToListAsync(cancellationToken);
    }

    public Task<ApiKey?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, cancellationToken);
}

internal sealed class AuditLogRepository(SharedServicesDbContext db) : IAuditLogRepository
{
    public async Task<IReadOnlyList<AuditLog>> ListAsync(
        string? actor, string? action, DateTimeOffset? from, DateTimeOffset? to, int limit, int offset, CancellationToken cancellationToken = default)
    {
        var query = db.AuditLogs.AsNoTracking();
        if (actor is not null) query = query.Where(l => l.Actor.Contains(actor));
        if (action is not null) query = query.Where(l => l.Action == action);
        if (from is { } start) query = query.Where(l => l.CreatedAt >= start);
        if (to is { } end) query = query.Where(l => l.CreatedAt < end);
        return await query.OrderByDescending(l => l.CreatedAt).ThenBy(l => l.Id).Skip(offset).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ActionsAsync(CancellationToken cancellationToken = default) =>
        await db.AuditLogs.AsNoTracking().Select(l => l.Action).Distinct().OrderBy(a => a).ToListAsync(cancellationToken);
}

internal sealed class DeliveryLogRepository(SharedServicesDbContext db) : IDeliveryLogRepository
{
    public void Add(DeliveryLog log) => db.DeliveryLogs.Add(log);

    public async Task<IReadOnlyList<DeliveryLog>> ListRecentAsync(
        string? projectCode, ChannelType channelType, int limit, CancellationToken cancellationToken = default)
    {
        var query = db.DeliveryLogs.AsNoTracking().Where(l => l.ChannelType == channelType);
        if (projectCode is not null) query = query.Where(l => l.ProjectCode == projectCode);
        return await query.OrderByDescending(l => l.CreatedAt).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DeliveryLog>> ListRecentAsync(
        string? projectCode, IReadOnlyCollection<ChannelType> channelTypes, int limit, CancellationToken cancellationToken = default)
    {
        var channels = channelTypes.ToList();   // a plain list, which EF turns into IN (...)
        var query = db.DeliveryLogs.AsNoTracking().Where(l => channels.Contains(l.ChannelType));
        if (projectCode is not null) query = query.Where(l => l.ProjectCode == projectCode);
        return await query.OrderByDescending(l => l.CreatedAt).Take(limit).ToListAsync(cancellationToken);
    }
}

internal sealed class StoredFileRepository(SharedServicesDbContext db) : IStoredFileRepository
{
    public void Add(StoredFile file) => db.StoredFiles.Add(file);

    public Task<StoredFile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.StoredFiles.FirstOrDefaultAsync(f => f.Id == id, cancellationToken);

    public void Remove(StoredFile file) => db.StoredFiles.Remove(file);

    public async Task<IReadOnlyList<StoredFile>> ListAsync(string? projectCode, int limit, int offset, CancellationToken cancellationToken = default)
    {
        var query = db.StoredFiles.AsNoTracking();
        if (projectCode is not null) query = query.Where(f => f.ProjectCode == projectCode);
        return await query.OrderByDescending(f => f.CreatedAt).ThenBy(f => f.Id).Skip(offset).Take(limit).ToListAsync(cancellationToken);
    }
}

internal sealed class MediaLogRepository(SharedServicesDbContext db) : IMediaLogRepository
{
    public void Add(MediaLog log) => db.MediaLogs.Add(log);

    public async Task<IReadOnlyList<MediaLog>> ListRecentAsync(string? projectCode, int limit, CancellationToken cancellationToken = default)
    {
        var query = db.MediaLogs.AsNoTracking();
        if (projectCode is not null) query = query.Where(l => l.ProjectCode == projectCode);
        return await query.OrderByDescending(l => l.CreatedAt).Take(limit).ToListAsync(cancellationToken);
    }
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
