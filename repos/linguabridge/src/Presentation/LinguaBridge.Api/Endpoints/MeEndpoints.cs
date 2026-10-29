using LinguaBridge.Application.Abstractions;

namespace LinguaBridge.Api.Endpoints;

public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        // No RequireAuthorization: the fallback policy already protects it (a test proves that).
        app.MapGet("/api/v1/me", (ICurrentUser user) => Results.Ok(new MeResponse(user.UserId, user.ClientId, user.IsAuthenticated)))
            .WithName("GetMe")
            .WithTags("Me");

        return app;
    }
}

public sealed record MeResponse(Guid? UserId, string? ClientId, bool IsAuthenticated);
