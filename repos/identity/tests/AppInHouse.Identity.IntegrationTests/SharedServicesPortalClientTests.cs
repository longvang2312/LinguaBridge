using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;

namespace AppInHouse.Identity.IntegrationTests;

/// <summary>The Shared Services portal signs administrators in here: its client must exist and its ID token must carry the roles.</summary>
public class SharedServicesPortalClientTests : IClassFixture<StsWebApplicationFactory>
{
    private const string RedirectUri = "http://localhost:5031/signin-oidc";
    private readonly StsWebApplicationFactory _factory;

    public SharedServicesPortalClientTests(StsWebApplicationFactory factory) => _factory = factory;

    private HttpClient NewBrowser() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static string AuthorizeUrl(string challenge, string scope) => QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
    {
        ["client_id"] = "shared-services-portal",
        ["response_type"] = "code",
        ["redirect_uri"] = RedirectUri,
        ["scope"] = scope,
        ["state"] = "xyz",
        ["code_challenge"] = challenge,
        ["code_challenge_method"] = "S256",
    });

    private async Task<(HttpClient Browser, string Verifier, HttpResponseMessage Authorize)> SignInAndAuthorizeAsync(string scope)
    {
        var browser = NewBrowser();
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorizeUrl = AuthorizeUrl(challenge, scope);

        var page = await browser.GetStringAsync($"/Account/Login?returnUrl={Uri.EscapeDataString(authorizeUrl)}");
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var login = await browser.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = StsWebApplicationFactory.AdminEmail,
            ["Password"] = StsWebApplicationFactory.AdminPassword,
            ["ReturnUrl"] = authorizeUrl,
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        return (browser, verifier, await browser.GetAsync(login.Headers.Location!.OriginalString));
    }

    private static JsonElement PayloadOf(string jwt)
    {
        var payload = jwt.Split('.')[1];
        return JsonDocument.Parse(WebEncoders.Base64UrlDecode(payload)).RootElement.Clone();
    }

    [Fact]
    public async Task TheAdministrator_GetsACode_ForTheScopesThePortalAsksFor_AndTheIdTokenCarriesTheRoleAndTheEmail()
    {
        var (browser, verifier, authorize) = await SignInAndAuthorizeAsync("openid profile email roles");

        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var query = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query);
        Assert.StartsWith(RedirectUri, authorize.Headers.Location.AbsoluteUri);
        Assert.False(query.ContainsKey("error"), query.TryGetValue("error_description", out var why) ? why.ToString() : "");

        var response = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = "shared-services-portal",
            ["code"] = query["code"].ToString(),
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = verifier,
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var idToken = PayloadOf((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id_token").GetString()!);

        Assert.Equal("shared-services-portal", idToken.GetProperty("aud").ValueKind == JsonValueKind.Array
            ? idToken.GetProperty("aud")[0].GetString()
            : idToken.GetProperty("aud").GetString());
        Assert.Equal(StsWebApplicationFactory.AdminEmail, idToken.GetProperty("email").GetString());
        var role = idToken.GetProperty("role");
        var roles = role.ValueKind == JsonValueKind.Array ? role.EnumerateArray().Select(r => r.GetString()).ToList() : [role.GetString()];
        Assert.Contains("GlobalAdmin", roles);
    }

    [Fact]
    public async Task WithoutTheRolesScope_TheIdTokenHasNoRole_SoThePortalMustAskForIt()
    {
        var (browser, verifier, authorize) = await SignInAndAuthorizeAsync("openid profile email");
        var query = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query);

        var response = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = "shared-services-portal",
            ["code"] = query["code"].ToString(),
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = verifier,
        }));
        var idToken = PayloadOf((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id_token").GetString()!);

        Assert.False(idToken.TryGetProperty("role", out _));
    }

    [Fact]
    public async Task AnAddressThatIsNotRegistered_IsRefused_SoACodeCannotBeSentToAnotherSite()
    {
        var browser = NewBrowser();
        var url = QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = "shared-services-portal",
            ["response_type"] = "code",
            ["redirect_uri"] = "http://evil.example/signin-oidc",
            ["scope"] = "openid",
            ["code_challenge"] = "x",
            ["code_challenge_method"] = "S256",
        });

        var response = await browser.GetAsync(url);

        Assert.DoesNotContain("evil.example", response.Headers.Location?.ToString() ?? string.Empty);
    }
}
