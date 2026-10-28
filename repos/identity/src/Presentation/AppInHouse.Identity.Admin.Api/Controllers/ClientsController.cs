using AppInHouse.Identity.Application.Admin.Clients;
using AppInHouse.Identity.Application.Admin.Common;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace AppInHouse.Identity.Admin.Api.Controllers;

[Route($"{Root}/clients")]
[SwaggerTag("OpenID Connect clients (Web, SPA, Mobile, Machine). Global administrators only.")]
public class ClientsController : AdminControllerBase
{
    private readonly IClientAdminService _clients;

    public ClientsController(IClientAdminService clients) => _clients = clients;

    [HttpGet]
    [SwaggerOperation(Summary = "Lists clients")]
    [ProducesResponseType(typeof(PagedResult<ClientDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] PagingQuery query, CancellationToken cancellationToken)
        => Ok(await _clients.ListAsync(query, cancellationToken));

    [HttpPost]
    [SwaggerOperation(
        Summary = "Creates a client",
        Description = "Confidential clients (Web, Machine) get a generated secret, which is returned only in this response.")]
    [ProducesResponseType(typeof(CreatedClientDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateClientRequest request, CancellationToken cancellationToken)
    {
        var created = await _clients.CreateAsync(request, cancellationToken);
        NoStore();
        return Created($"{Root}/clients/{created.Client.Id}", created);
    }

    [HttpPut("{id}")]
    [SwaggerOperation(Summary = "Updates a client", Description = "The client id and the type cannot be changed.")]
    [ProducesResponseType(typeof(ClientDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateClientRequest request, CancellationToken cancellationToken)
        => Ok(await _clients.UpdateAsync(id, request, cancellationToken));

    [HttpPost("{id}/rotate-secret")]
    [SwaggerOperation(
        Summary = "Rotates the secret of a confidential client",
        Description = "Generates a new secret (returned only in this response) and invalidates the previous one.")]
    [ProducesResponseType(typeof(RotatedSecretDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RotateSecret(string id, CancellationToken cancellationToken)
    {
        var rotated = await _clients.RotateSecretAsync(id, cancellationToken);
        NoStore();
        return Ok(rotated);
    }
}
