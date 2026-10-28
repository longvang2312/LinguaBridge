using System.ComponentModel.DataAnnotations;
using AppInHouse.Identity.Application.Admin.Clients;

namespace AppInHouse.Identity.Admin.Web.ViewModels;

/// <summary>The client form: URIs are edited as one per line.</summary>
public sealed class ClientForm
{
    [Required, StringLength(100, MinimumLength = 3), RegularExpression(@"^[A-Za-z0-9][A-Za-z0-9._\-]*$", ErrorMessage = "Use letters, digits, '.', '_' and '-'.")]
    public string ClientId { get; set; } = string.Empty;

    [Required, StringLength(200)]
    public string DisplayName { get; set; } = string.Empty;

    [Required]
    public ClientApplicationType? Type { get; set; }

    public string? RedirectUris { get; set; }

    public string? PostLogoutRedirectUris { get; set; }

    public List<string> Scopes { get; set; } = new();

    public bool AllowRefreshTokens { get; set; }
}

public sealed record ClientListViewModel(IReadOnlyList<ClientDto> Items, PagerModel Pager);

public sealed class ClientFormViewModel
{
    /// <summary>Null when creating.</summary>
    public string? Id { get; set; }

    public ClientForm Form { get; set; } = new();

    public IReadOnlyList<string> AvailableScopes { get; set; } = Array.Empty<string>();

    public bool IsNew => Id is null;
}

/// <summary>Shown exactly once, straight from the response of the POST that produced it.</summary>
public sealed record ClientSecretViewModel(string ClientId, string Secret, bool Rotated);
