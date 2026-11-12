using AppInHouse.SharedServices.Application.Conversations;

namespace AppInHouse.SharedServices.Infrastructure.Realtime;

/// <summary>
/// Who is connected, in memory. A user can have several connections at once (two tabs, a phone) and is online while one is open; the first
/// connection and the last one are the moments that count, so only they report a change. The state lives in this process: it is lost on
/// restart (every connection is lost too, and the clients reconnect) and it is not shared between several instances of the API.
/// </summary>
public sealed class UserPresenceTracker : IUserPresenceTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Project, string UserId)> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _users = new(StringComparer.Ordinal);

    public PresenceChange Connected(string projectCode, string userId, string connectionId)
    {
        lock (_gate)
        {
            if (_connections.ContainsKey(connectionId)) return new PresenceChange(projectCode, userId, false, true);

            var key = ChatKeys.Of(projectCode, userId);
            _connections[connectionId] = (projectCode, userId);
            if (!_users.TryGetValue(key, out var set)) _users[key] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(connectionId);
            return new PresenceChange(projectCode, userId, set.Count == 1, true);
        }
    }

    public PresenceChange Disconnected(string connectionId)
    {
        lock (_gate)
        {
            if (!_connections.Remove(connectionId, out var who)) return new PresenceChange(string.Empty, string.Empty, false, false);

            var key = ChatKeys.Of(who.Project, who.UserId);
            if (!_users.TryGetValue(key, out var set)) return new PresenceChange(who.Project, who.UserId, false, false);

            set.Remove(connectionId);
            if (set.Count > 0) return new PresenceChange(who.Project, who.UserId, false, true);

            _users.Remove(key);
            return new PresenceChange(who.Project, who.UserId, true, false);
        }
    }

    public bool IsOnline(string projectCode, string userId)
    {
        lock (_gate) return _users.ContainsKey(ChatKeys.Of(projectCode, userId));
    }

    public IReadOnlyList<string> OnlineUsers(string? projectCode)
    {
        lock (_gate)
        {
            // For one project the plain ids; for all of them each id with its project, since an id is only unique inside its project.
            var users = _connections.Values.Distinct();
            if (projectCode is null) return [.. users.Select(u => ChatKeys.Of(u.Project, u.UserId)).OrderBy(k => k, StringComparer.Ordinal)];
            return [.. users.Where(u => u.Project == projectCode).Select(u => u.UserId).OrderBy(id => id, StringComparer.Ordinal)];
        }
    }

    public int ConnectionCount(string? projectCode)
    {
        lock (_gate) return projectCode is null ? _connections.Count : _connections.Values.Count(c => c.Project == projectCode);
    }
}
