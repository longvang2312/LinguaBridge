using AppInHouse.Identity.Application.Admin.Common;
using Microsoft.AspNetCore.Identity;

namespace AppInHouse.Identity.Infrastructure.Persistence.Admin;

internal static class IdentityResultMapper
{
    /// <summary>Duplicate names/emails become 409; every other failure (e.g. password policy) becomes 400 with details.</summary>
    public static void ThrowIfFailed(IdentityResult result)
    {
        if (result.Succeeded)
            return;

        var duplicate = result.Errors.FirstOrDefault(e => e.Code.StartsWith("Duplicate", StringComparison.Ordinal));
        if (duplicate is not null)
            throw new ConflictException(duplicate.Description);

        var errors = result.Errors
            .GroupBy(e => e.Code)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());
        throw new BadRequestException("The request could not be processed.", errors);
    }
}
