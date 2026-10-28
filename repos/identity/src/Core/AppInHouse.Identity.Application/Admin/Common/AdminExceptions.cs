namespace AppInHouse.Identity.Application.Admin.Common;

/// <summary>Base of the expected, client-facing failures of the admin use cases (mapped to HTTP problem details).</summary>
public abstract class AdminException : Exception
{
    protected AdminException(string message) : base(message)
    {
    }
}

public sealed class NotFoundException : AdminException
{
    public NotFoundException(string message) : base(message)
    {
    }
}

/// <summary>The caller is authenticated but not allowed to perform the operation.</summary>
public sealed class ForbiddenException : AdminException
{
    public ForbiddenException(string message = "You are not allowed to perform this operation.") : base(message)
    {
    }
}

public sealed class ConflictException : AdminException
{
    public ConflictException(string message) : base(message)
    {
    }
}

public sealed class BadRequestException : AdminException
{
    public BadRequestException(string message, IReadOnlyDictionary<string, string[]>? errors = null) : base(message)
    {
        Errors = errors;
    }

    /// <summary>Optional field-level details.</summary>
    public IReadOnlyDictionary<string, string[]>? Errors { get; }
}
