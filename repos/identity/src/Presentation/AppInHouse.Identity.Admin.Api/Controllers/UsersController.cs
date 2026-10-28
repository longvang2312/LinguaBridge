using AppInHouse.Identity.Application.Admin.Common;
using AppInHouse.Identity.Application.Admin.Users;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace AppInHouse.Identity.Admin.Api.Controllers;

[Route($"{Root}/users")]
[SwaggerTag("User accounts. Global administrators only.")]
public class UsersController : AdminControllerBase
{
    private readonly IUserAdminService _users;

    public UsersController(IUserAdminService users) => _users = users;

    [HttpGet]
    [SwaggerOperation(Summary = "Lists users")]
    [ProducesResponseType(typeof(PagedResult<UserDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] UserListQuery query, CancellationToken cancellationToken)
        => Ok(await _users.ListAsync(query, cancellationToken));

    [HttpGet("{id}")]
    [SwaggerOperation(Summary = "Gets a user")]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken)
        => Ok(await _users.GetAsync(id, cancellationToken));

    [HttpPost]
    [SwaggerOperation(Summary = "Creates a user", Description = "The password must satisfy the Identity password policy and is never logged.")]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request, CancellationToken cancellationToken)
    {
        var user = await _users.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = user.Id }, user);
    }

    [HttpPut("{id}")]
    [SwaggerOperation(Summary = "Updates the name and the active flag of a user")]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateUserRequest request, CancellationToken cancellationToken)
        => Ok(await _users.UpdateAsync(id, request, cancellationToken));

    [HttpPost("{id}/lock")]
    [SwaggerOperation(Summary = "Locks a user", Description = "Optionally until a given time. You cannot lock yourself.")]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Lock(string id, [FromBody] LockUserRequest? request, CancellationToken cancellationToken)
        => Ok(await _users.LockAsync(id, request ?? new LockUserRequest(), cancellationToken));

    [HttpPost("{id}/unlock")]
    [SwaggerOperation(Summary = "Unlocks a user")]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unlock(string id, CancellationToken cancellationToken)
        => Ok(await _users.UnlockAsync(id, cancellationToken));

    [HttpPost("{id}/reset-password")]
    [SwaggerOperation(Summary = "Sets a new password for a user", Description = "The password is never logged or audited.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetPassword(string id, [FromBody] ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        await _users.ResetPasswordAsync(id, request, cancellationToken);
        return NoContent();
    }
}
