using System.Collections.Immutable;
using System.Security.Claims;
using AppInHouse.Identity.Infrastructure.Identity.Models;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AppInHouse.Identity.STS.Controllers;

/// <summary>OpenID Connect endpoints: authorize, token, userinfo and logout.</summary>
public class AuthorizationController : Controller
{
    private readonly IOpenIddictApplicationManager _applicationManager;
    private readonly IOpenIddictScopeManager _scopeManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;

    public AuthorizationController(
        IOpenIddictApplicationManager applicationManager,
        IOpenIddictScopeManager scopeManager,
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager)
    {
        _applicationManager = applicationManager;
        _scopeManager = scopeManager;
        _signInManager = signInManager;
        _userManager = userManager;
    }

    [HttpGet("~/connect/authorize")]
    [HttpPost("~/connect/authorize")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Authorize()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        // The user is authenticated with the Identity application cookie created by the Account/Login page.
        var result = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!result.Succeeded || request.HasPrompt(Prompts.Login))
        {
            if (request.HasPrompt(Prompts.None))
            {
                return ForbidWith(Errors.LoginRequired, "The user is not logged in.");
            }

            // Drop prompt=login from the return URL, otherwise the user would be asked to log in again forever.
            var prompt = string.Join(" ", request.GetPrompts().Remove(Prompts.Login));
            IEnumerable<KeyValuePair<string, StringValues>> source = Request.HasFormContentType ? Request.Form : Request.Query;
            var parameters = source
                .Where(parameter => parameter.Key != Parameters.Prompt)
                .ToList();
            if (prompt.Length > 0)
                parameters.Add(KeyValuePair.Create(Parameters.Prompt, new StringValues(prompt)));

            return Challenge(
                authenticationSchemes: IdentityConstants.ApplicationScheme,
                properties: new AuthenticationProperties
                {
                    RedirectUri = Request.PathBase + Request.Path + QueryString.Create(parameters)
                });
        }

        var user = await _userManager.GetUserAsync(result.Principal);
        if (user is null || !await CanSignInAsync(user))
        {
            return ForbidWith(Errors.AccessDenied, "The user is no longer allowed to sign in.");
        }

        var principal = await CreateUserPrincipalAsync(user, request.GetScopes());
        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [HttpPost("~/connect/token")]
    [IgnoreAntiforgeryToken]
    [Produces("application/json")]
    public async Task<IActionResult> Exchange()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        if (request.IsClientCredentialsGrantType())
        {
            return await ExchangeClientCredentialsAsync(request);
        }

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            return await ExchangeUserGrantAsync();
        }

        throw new InvalidOperationException("The specified grant type is not supported.");
    }

    [Authorize(AuthenticationSchemes = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)]
    [HttpGet("~/connect/userinfo")]
    [HttpPost("~/connect/userinfo")]
    [Produces("application/json")]
    public async Task<IActionResult> Userinfo()
    {
        var user = await _userManager.FindByIdAsync(User.GetClaim(Claims.Subject) ?? string.Empty);
        if (user is null || !user.IsActive)
        {
            return Challenge(
                authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                properties: new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidToken,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The specified access token is bound to an account that no longer exists."
                }));
        }

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [Claims.Subject] = await _userManager.GetUserIdAsync(user)
        };

        if (User.HasScope(Scopes.Profile))
        {
            claims[Claims.Name] = DisplayName(user);
            claims[Claims.PreferredUsername] = user.UserName ?? string.Empty;
        }

        if (User.HasScope(Scopes.Email) && !string.IsNullOrEmpty(user.Email))
        {
            claims[Claims.Email] = user.Email;
            claims[Claims.EmailVerified] = user.EmailConfirmed;
        }

        return Ok(claims);
    }

    /// <summary>
    /// RP-initiated logout. A request that says where to go next (post_logout_redirect_uri, which OpenIddict has checked
    /// against the registered addresses) and carries an id_token_hint that is genuine AND belongs to the user signed in
    /// here is signed out at once and redirected, without a confirmation page. Anything else gets the confirmation page.
    /// OpenIddict silently ignores a hint it cannot validate (garbage, unsigned, expired keys), and any site could hold an
    /// id_token of its own, so "a hint is present" proves nothing: without the subject check any web page could log any
    /// visitor out of the STS (logout CSRF) and, with it, out of every application that relies on it.
    /// </summary>
    [HttpGet("~/connect/logout")]
    public async Task<IActionResult> Logout()
    {
        var request = HttpContext.GetOpenIddictServerRequest();
        if (!string.IsNullOrEmpty(request?.IdTokenHint)
            && !string.IsNullOrEmpty(request.PostLogoutRedirectUri)
            && await HintBelongsToSignedInUserAsync())
            return await EndSessionAsync();

        return View("~/Views/Account/Logout.cshtml");
    }

    /// <summary>
    /// True when the id_token_hint was validated by OpenIddict (signature, issuer, not tampered with) and its subject is
    /// the user whose Identity cookie this browser holds. With no cookie session there is nothing to end, but nothing to
    /// protect either, so a validated hint alone is enough to redirect.
    /// </summary>
    private async Task<bool> HintBelongsToSignedInUserAsync()
    {
        var hint = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var hintSubject = hint.Succeeded ? hint.Principal?.GetClaim(Claims.Subject) : null;
        if (string.IsNullOrEmpty(hintSubject))
            return false;

        var session = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!session.Succeeded)
            return true;

        return string.Equals(_userManager.GetUserId(session.Principal!), hintSubject, StringComparison.Ordinal);
    }

    [HttpPost("~/connect/logout")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> LogoutPost() => EndSessionAsync();

    private async Task<IActionResult> EndSessionAsync()
    {
        // Ends the STS's own session (the Identity cookie): without this the next /connect/authorize signs the user straight back in.
        await _signInManager.SignOutAsync();

        // Lets OpenIddict redirect to the client's post_logout_redirect_uri (when valid, with its state) or to "/".
        return SignOut(
            authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            properties: new AuthenticationProperties { RedirectUri = "/" });
    }

    private async Task<IActionResult> ExchangeClientCredentialsAsync(OpenIddictRequest request)
    {
        var application = await _applicationManager.FindByClientIdAsync(request.ClientId!)
            ?? throw new InvalidOperationException("The application details cannot be found.");

        var identity = new ClaimsIdentity(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Claims.Name, Claims.Role);

        // No end user: the client itself is the subject.
        identity.SetClaim(Claims.Subject, await _applicationManager.GetClientIdAsync(application));
        identity.SetClaim(Claims.Name, await _applicationManager.GetDisplayNameAsync(application));

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(request.GetScopes());
        principal.SetResources(await ListResourcesAsync(principal.GetScopes()));

        foreach (var claim in principal.Claims)
            claim.SetDestinations(GetDestinations(claim, principal));

        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private async Task<IActionResult> ExchangeUserGrantAsync()
    {
        // The principal stored in the authorization code / refresh token.
        var result = await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var principal = result.Principal
            ?? throw new InvalidOperationException("The grant principal cannot be retrieved.");

        // Re-check the account on every redemption so disabled/locked users cannot renew tokens.
        var user = await _userManager.FindByIdAsync(principal.GetClaim(Claims.Subject) ?? string.Empty);
        if (user is null)
        {
            return ForbidWith(Errors.InvalidGrant, "The token is no longer valid.");
        }

        if (!await CanSignInAsync(user))
        {
            return ForbidWith(Errors.InvalidGrant, "The user is no longer allowed to sign in.");
        }

        foreach (var claim in principal.Claims)
            claim.SetDestinations(GetDestinations(claim, principal));

        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private async Task<ClaimsPrincipal> CreateUserPrincipalAsync(ApplicationUser user, ImmutableArray<string> scopes)
    {
        var principal = await _signInManager.CreateUserPrincipalAsync(user);

        principal.SetClaim(Claims.Name, DisplayName(user));
        principal.SetClaim(Claims.PreferredUsername, user.UserName);
        principal.SetScopes(scopes);
        principal.SetResources(await ListResourcesAsync(principal.GetScopes()));

        foreach (var claim in principal.Claims)
            claim.SetDestinations(GetDestinations(claim, principal));

        return principal;
    }

    private async Task<List<string>> ListResourcesAsync(ImmutableArray<string> scopes)
    {
        var resources = new List<string>();
        await foreach (var resource in _scopeManager.ListResourcesAsync(scopes))
            resources.Add(resource);

        return resources;
    }

    private async Task<bool> CanSignInAsync(ApplicationUser user)
        => user.IsActive && await _signInManager.CanSignInAsync(user) && !await _userManager.IsLockedOutAsync(user);

    private static string DisplayName(ApplicationUser user)
        => string.IsNullOrWhiteSpace(user.FullName) ? user.UserName ?? string.Empty : user.FullName;

    private ForbidResult ForbidWith(string error, string description)
        => Forbid(
            authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            properties: new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description
            }));

    private IEnumerable<string> GetDestinations(Claim claim, ClaimsPrincipal principal)
    {
        switch (claim.Type)
        {
            case Claims.Name or Claims.PreferredUsername:
                yield return Destinations.AccessToken;
                if (principal.HasScope(Scopes.Profile))
                    yield return Destinations.IdentityToken;
                yield break;

            case Claims.Email:
                yield return Destinations.AccessToken;
                if (principal.HasScope(Scopes.Email))
                    yield return Destinations.IdentityToken;
                yield break;

            case Claims.Role:
                yield return Destinations.AccessToken;
                if (principal.HasScope(Scopes.Roles))
                    yield return Destinations.IdentityToken;
                yield break;

            default:
                // Anything else (e.g. the Identity security stamp) must never leak into a token.
                if (claim.Type == _userManager.Options.ClaimsIdentity.SecurityStampClaimType)
                    yield break;

                yield return Destinations.AccessToken;
                yield break;
        }
    }
}
