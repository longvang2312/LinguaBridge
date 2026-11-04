using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AppInHouse.Identity.Infrastructure.Persistence.Contexts;
using AppInHouse.Identity.Infrastructure.Persistence.Seeders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenIddict.Abstractions;

namespace AppInHouse.Identity.IntegrationTests;

/// <summary>RP-initiated logout (OpenID Connect RP-Initiated Logout 1.0) at <c>/connect/logout</c>.</summary>
public class LogoutTests : IClassFixture<StsWebApplicationFactory>
{
    private const string PostLogoutUri = "https://localhost:44303/signout-callback-oidc";

    private readonly StsWebApplicationFactory _factory;

    public LogoutTests(StsWebApplicationFactory factory) => _factory = factory;

    private HttpClient NewBrowser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    /// <summary>Signs the admin in at the STS (cookie on <paramref name="browser"/>) and returns the id_token the portal received.</summary>
    private static async Task<string> SignInAsync(HttpClient browser, string email = StsWebApplicationFactory.AdminEmail, string password = StsWebApplicationFactory.AdminPassword)
    {
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorizeUrl = QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = "admin-portal",
            ["response_type"] = "code",
            ["redirect_uri"] = StsWebApplicationFactory.AdminPortalRedirectUri,
            ["scope"] = "openid profile email identity_admin_api",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        });

        var page = await browser.GetStringAsync($"/Account/Login?returnUrl={Uri.EscapeDataString(authorizeUrl)}");
        var antiforgery = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var login = await browser.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["ReturnUrl"] = authorizeUrl,
            ["__RequestVerificationToken"] = antiforgery,
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var authorize = await browser.GetAsync(login.Headers.Location!.OriginalString);
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();
        var token = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = "admin-portal",
            ["code"] = code,
            ["redirect_uri"] = StsWebApplicationFactory.AdminPortalRedirectUri,
            ["code_verifier"] = verifier,
        }));
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        return (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id_token").GetString()!;
    }

    private static string LogoutUrl(string? idToken, string? redirectUri, string? state = null) =>
        QueryHelpers.AddQueryString("/connect/logout", new Dictionary<string, string?>
        {
            ["id_token_hint"] = idToken,
            ["post_logout_redirect_uri"] = redirectUri,
            ["state"] = state,
        });

    /// <summary>True while the browser still has an STS session: authorize answers with a code instead of the login page.</summary>
    private static async Task<bool> StillSignedInAsync(HttpClient browser)
    {
        var response = await browser.GetAsync(QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = "admin-portal",
            ["response_type"] = "code",
            ["redirect_uri"] = StsWebApplicationFactory.AdminPortalRedirectUri,
            ["scope"] = "openid",
            ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes("v"))),
            ["code_challenge_method"] = "S256",
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        return location.OriginalString.Contains("code=", StringComparison.Ordinal) && !location.OriginalString.Contains("/Account/Login", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logout_WithIdTokenHintAndRegisteredRedirect_EndsTheSessionAndRedirectsWithoutAConfirmationPage()
    {
        var browser = NewBrowser();
        var idToken = await SignInAsync(browser);
        Assert.True(await StillSignedInAsync(browser));

        var response = await browser.GetAsync(LogoutUrl(idToken, PostLogoutUri, state: "back-here"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith(PostLogoutUri, location);
        Assert.Equal("back-here", QueryHelpers.ParseQuery(new Uri(location).Query)["state"]);

        // The Identity cookie is gone: signing in again needs the password.
        Assert.False(await StillSignedInAsync(browser));
    }

    [Fact]
    public async Task Logout_WithoutParameters_AsksForConfirmationAndKeepsTheSession()
    {
        var browser = NewBrowser();
        await SignInAsync(browser);

        var response = await browser.GetAsync("/connect/logout");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Sign out", await response.Content.ReadAsStringAsync());
        Assert.True(await StillSignedInAsync(browser));
    }

    [Fact]
    public async Task Logout_WithRedirectButNoIdTokenHint_DoesNotSignOutOnAGet()
    {
        var browser = NewBrowser();
        await SignInAsync(browser);

        var response = await browser.GetAsync(LogoutUrl(null, PostLogoutUri));

        // Nothing proves who is asking, so a link must not be able to log the user out.
        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(await StillSignedInAsync(browser));
    }

    [Fact]
    public async Task Logout_WithUnregisteredRedirect_NeverRedirectsThereAndKeepsTheSession()
    {
        var browser = NewBrowser();
        var idToken = await SignInAsync(browser);

        var response = await browser.GetAsync(LogoutUrl(idToken, "https://evil.example/landing"));

        Assert.True(response.Headers.Location is null || !response.Headers.Location.OriginalString.Contains("evil.example"));
        Assert.True(await StillSignedInAsync(browser));
    }

    public static TheoryData<string> UnusableHints() => new()
    {
        "garbage",
        "a.b.c",
        // A well-formed JWT with alg "none" and the admin's claims, i.e. anyone can make one.
        UnsignedJwt(),
    };

    private static string UnsignedJwt()
    {
        static string B64(string json) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(json));
        return $"{B64("{\"alg\":\"none\",\"typ\":\"JWT\"}")}.{B64("{\"iss\":\"http://localhost/\",\"sub\":\"anyone\",\"aud\":\"admin-portal\"}")}.";
    }

    [Theory]
    [MemberData(nameof(UnusableHints))]
    public async Task Logout_WithAHintThatIsNotAGenuineIdToken_DoesNotSignTheUserOut(string hint)
    {
        var browser = NewBrowser();
        await SignInAsync(browser);

        var response = await browser.GetAsync(LogoutUrl(hint, PostLogoutUri));

        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(await StillSignedInAsync(browser));
    }

    [Fact]
    public async Task Logout_WithAnotherUsersIdToken_DoesNotSignTheVictimOut()
    {
        // The attacker is a real user of the STS: their own id_token is genuine, but it is not the victim's.
        const string attackerEmail = "attacker@appinhouse.local";
        const string attackerPassword = "Attacker@Pass123!";
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AppInHouse.Identity.Infrastructure.Identity.Models.ApplicationUser>>();
            if (await users.FindByEmailAsync(attackerEmail) is null)
            {
                var created = await users.CreateAsync(new AppInHouse.Identity.Infrastructure.Identity.Models.ApplicationUser
                {
                    UserName = attackerEmail, Email = attackerEmail, EmailConfirmed = true, IsActive = true,
                }, attackerPassword);
                Assert.True(created.Succeeded, string.Join(";", created.Errors.Select(e => e.Description)));
            }
        }

        var attackersIdToken = await SignInAsync(NewBrowser(), attackerEmail, attackerPassword);
        var victim = NewBrowser();
        var victimsIdToken = await SignInAsync(victim);
        Assert.NotEqual(victimsIdToken, attackersIdToken);

        var response = await victim.GetAsync(LogoutUrl(attackersIdToken, PostLogoutUri));

        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(await StillSignedInAsync(victim));
    }

    [Fact]
    public async Task Logout_WithAnExpiredButGenuineIdToken_StillWorks()
    {
        // A learner who has been reading for longer than the id_token lives must still be able to sign out in one click.
        await using var factory = new ShortLivedIdTokenFactory();
        var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var idToken = await SignInAsync(browser);
        await Task.Delay(TimeSpan.FromSeconds(3));
        var expiry = DateTimeOffset.FromUnixTimeSeconds(long.Parse(System.Text.Json.Nodes.JsonNode.Parse(
            Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(idToken.Split('.')[1])))!["exp"]!.ToString()));
        Assert.True(expiry < DateTimeOffset.UtcNow, "the test needs an id_token that has already expired");

        var response = await browser.GetAsync(LogoutUrl(idToken, PostLogoutUri));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(PostLogoutUri, response.Headers.Location!.OriginalString);
        Assert.False(await StillSignedInAsync(browser));
    }

    private sealed class ShortLivedIdTokenFactory : StsWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
                services.Configure<OpenIddict.Server.OpenIddictServerOptions>(options => options.IdentityTokenLifetime = TimeSpan.FromSeconds(1)));
        }
    }

    [Fact]
    public async Task Logout_ConfirmationFormPost_StillEndsTheSession()
    {
        var browser = NewBrowser();
        await SignInAsync(browser);
        var page = await browser.GetStringAsync("/connect/logout");
        var antiforgery = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        var response = await browser.PostAsync("/connect/logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiforgery,
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False(await StillSignedInAsync(browser));
    }
}

/// <summary>The learners' client gets its logout addresses, including on a database seeded before they existed.</summary>
public class LinguaBridgeAppPostLogoutSeederTests
{
    private static readonly string[] Defaults =
    [
        "http://localhost:3000/login.html",
        "http://localhost:5173/login.html",
        "http://localhost:3000/callback",
    ];

    [Fact]
    public async Task Seeder_RegistersPostLogoutRedirectsOnANewClient()
    {
        await using var factory = new AppSeedingFactory();
        using var scope = factory.Services.CreateScope();

        var uris = await PostLogoutUrisAsync(scope.ServiceProvider);

        Assert.Equivalent(Defaults, uris, strict: true);
    }

    [Fact]
    public async Task Seeder_AddsMissingPostLogoutRedirectsToAnExistingClient_WithoutTouchingTheRest()
    {
        await using var factory = new AppSeedingFactory();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var applications = services.GetRequiredService<IOpenIddictApplicationManager>();

        // Make the client look like one seeded before this change: no logout addresses, one added by an administrator.
        var client = (await applications.FindByClientIdAsync("linguabridge-app"))!;
        var descriptor = new OpenIddictApplicationDescriptor();
        await applications.PopulateAsync(descriptor, client);
        descriptor.PostLogoutRedirectUris.Clear();
        descriptor.PostLogoutRedirectUris.Add(new Uri("https://admin-added.example/bye"));
        await applications.UpdateAsync(client, descriptor);
        var redirectsBefore = await applications.GetRedirectUrisAsync((await applications.FindByClientIdAsync("linguabridge-app"))!);

        await services.GetRequiredService<DbSeeder>().SeedAsync();

        var uris = await PostLogoutUrisAsync(services);
        Assert.Equivalent(Defaults.Append("https://admin-added.example/bye"), uris, strict: true);
        var after = (await applications.FindByClientIdAsync("linguabridge-app"))!;
        Assert.Equal(redirectsBefore, await applications.GetRedirectUrisAsync(after));

        // Idempotent: a second run changes nothing.
        await services.GetRequiredService<DbSeeder>().SeedAsync();
        Assert.Equivalent(Defaults.Append("https://admin-added.example/bye"), await PostLogoutUrisAsync(services), strict: true);
    }

    [Fact]
    public async Task Seeder_UsesTheConfiguredPostLogoutRedirects()
    {
        await using var factory = new AppSeedingFactory(("Seeding:LinguaBridgeAppPostLogoutRedirectUris:0", "https://app.example/signed-out"));
        using var scope = factory.Services.CreateScope();

        Assert.Equal(["https://app.example/signed-out"], await PostLogoutUrisAsync(scope.ServiceProvider));
    }

    private static async Task<string[]> PostLogoutUrisAsync(IServiceProvider services)
    {
        var applications = services.GetRequiredService<IOpenIddictApplicationManager>();
        var client = await applications.FindByClientIdAsync("linguabridge-app");
        Assert.NotNull(client);
        return [.. await applications.GetPostLogoutRedirectUrisAsync(client)];
    }

    private sealed class AppSeedingFactory(params (string Key, string Value)[] extra) : StsWebApplicationFactory
    {
        private readonly string _databaseName = Guid.NewGuid().ToString();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["Seeding:LinguaBridgeApiClientSecret"] = "integration-test-linguabridge-api-secret",
                    ["Seeding:LinguaBridgeAppEnabled"] = "true",
                };
                foreach (var (key, value) in extra)
                    values[key] = value;
                configuration.AddInMemoryCollection(values);
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppIdentityDbContext>>();
                services.RemoveAll<DbContextOptions>();
                services.AddDbContext<AppIdentityDbContext>(options => options.UseInMemoryDatabase(_databaseName));
            });
        }
    }
}
