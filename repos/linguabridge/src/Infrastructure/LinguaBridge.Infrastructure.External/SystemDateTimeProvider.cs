using LinguaBridge.Application.Abstractions;

namespace LinguaBridge.Infrastructure.External;

internal sealed class SystemDateTimeProvider : IDateTimeProvider
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
