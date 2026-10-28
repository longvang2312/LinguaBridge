using AppInHouse.Identity.Admin.Web.Infrastructure;
using AppInHouse.Identity.Admin.Web.ViewModels;
using AppInHouse.Identity.Application.Admin.Clients;
using AppInHouse.Identity.Application.Admin.Common;
using Microsoft.AspNetCore.Mvc;

namespace AppInHouse.Identity.Admin.Web.Controllers;

public class ClientsController : PortalController
{
    private readonly IClientAdminService _clients;

    public ClientsController(IClientAdminService clients) => _clients = clients;

    public async Task<IActionResult> Index(PagingQuery query, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            query = new PagingQuery();

        var result = await _clients.ListAsync(query, cancellationToken);
        return View(new ClientListViewModel(result.Items, PagerModel.From(result)));
    }

    [HttpGet]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
        => View(new ClientFormViewModel
        {
            Form = new ClientForm { Type = ClientApplicationType.Web },
            AvailableScopes = await _clients.ListScopesAsync(cancellationToken),
        });

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind(Prefix = "Form")] ClientForm form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return View(new ClientFormViewModel { Form = form, AvailableScopes = await _clients.ListScopesAsync(cancellationToken) });

        var created = await _clients.CreateAsync(
            new CreateClientRequest
            {
                ClientId = form.ClientId,
                DisplayName = form.DisplayName,
                Type = form.Type,
                RedirectUris = Lines(form.RedirectUris),
                PostLogoutRedirectUris = Lines(form.PostLogoutRedirectUris),
                Scopes = form.Scopes,
                AllowRefreshTokens = form.AllowRefreshTokens,
            },
            cancellationToken);

        if (created.ClientSecret is null)
        {
            Success($"Client {created.Client.ClientId} was created.");
            return RedirectToAction(nameof(Edit), new { id = created.Client.Id });
        }

        return SecretPage(new ClientSecretViewModel(created.Client.ClientId, created.ClientSecret, Rotated: false));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(string id, CancellationToken cancellationToken)
    {
        var client = await _clients.GetAsync(id, cancellationToken);
        return View(new ClientFormViewModel
        {
            Id = client.Id,
            Form = new ClientForm
            {
                ClientId = client.ClientId,
                DisplayName = client.DisplayName,
                Type = client.Type,
                RedirectUris = string.Join(Environment.NewLine, client.RedirectUris),
                PostLogoutRedirectUris = string.Join(Environment.NewLine, client.PostLogoutRedirectUris),
                Scopes = client.Scopes.ToList(),
                AllowRefreshTokens = client.AllowRefreshTokens,
            },
            AvailableScopes = await _clients.ListScopesAsync(cancellationToken),
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(string id, [Bind(Prefix = "Form")] ClientForm form, CancellationToken cancellationToken)
    {
        // The client id and the type cannot change: they are not part of what is validated or sent.
        ModelState.Remove("Form.ClientId");
        ModelState.Remove("Form.Type");
        if (!ModelState.IsValid)
        {
            var client = await _clients.GetAsync(id, cancellationToken);
            form.ClientId = client.ClientId;
            form.Type = client.Type;
            return View(new ClientFormViewModel { Id = id, Form = form, AvailableScopes = await _clients.ListScopesAsync(cancellationToken) });
        }

        await _clients.UpdateAsync(
            id,
            new UpdateClientRequest
            {
                DisplayName = form.DisplayName,
                RedirectUris = Lines(form.RedirectUris),
                PostLogoutRedirectUris = Lines(form.PostLogoutRedirectUris),
                Scopes = form.Scopes,
                AllowRefreshTokens = form.AllowRefreshTokens,
            },
            cancellationToken);

        Success("The client was updated.");
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RotateSecret(string id, CancellationToken cancellationToken)
    {
        var rotated = await _clients.RotateSecretAsync(id, cancellationToken);
        return SecretPage(new ClientSecretViewModel(rotated.ClientId, rotated.ClientSecret, Rotated: true));
    }

    /// <summary>The plain secret is rendered in the response itself (never redirected, stored or cached) and cannot be retrieved again.</summary>
    private ViewResult SecretPage(ClientSecretViewModel model)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        return View("Secret", model);
    }
}
