using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;

namespace AppInHouse.Identity.IntegrationTests;

/// <summary>Drives the whole Authorization Code + PKCE flow through the real Login page.</summary>
public class AuthorizationCodeFlowTests : IClassFixture<StsWebApplicationFactory>
{
    private readonly StsWebApplicationFactory _factory;

    public AuthorizationCodeFlowTests(StsWebApplicationFactory factory) => _factory = factory;

    private HttpClient NewBrowser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    private static (string Verifier, string Challenge) NewPkcePair()
    {
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string AuthorizeUrl(string challenge, string scope = "openid profile email offline_access identity_admin_api")
        => QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = "admin-portal",
            ["response_type"] = "code",
            ["redirect_uri"] = StsWebApplicationFactory.AdminPortalRedirectUri,
            ["scope"] = scope,
            ["state"] = "xyz",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        });

    private static async Task<string> LoginAsync(HttpClient browser, string returnUrl, string password)
    {
        var page = await browser.GetStringAsync($"/Account/Login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);

        var response = await browser.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = StsWebApplicationFactory.AdminEmail,
            ["Password"] = password,
            ["ReturnUrl"] = returnUrl,
            ["__RequestVerificationToken"] = token,
        }));

        return response.StatusCode == HttpStatusCode.Redirect
            ? response.Headers.Location!.OriginalString
            : await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task LoginPage_IsServed()
    {
        var response = await NewBrowser().GetAsync("/Account/Login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Sign in", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Authorize_WhenAnonymous_RedirectsToLogin()
    {
        var (_, challenge) = NewPkcePair();

        var response = await NewBrowser().GetAsync(AuthorizeUrl(challenge));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        // The cookie handler may emit an absolute URL.
        var path = location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
        Assert.StartsWith("/Account/Login", path);
        Assert.Contains("ReturnUrl=", path);
    }

    [Fact]
    public async Task Login_WithWrongPassword_ShowsGenericError()
    {
        var body = await LoginAsync(NewBrowser(), "/", "definitely-wrong");

        Assert.Contains("Invalid email or password.", body);
    }

    [Fact]
    public async Task Login_DoesNotFollowExternalReturnUrl()
    {
        var location = await LoginAsync(NewBrowser(), "https://evil.example/", StsWebApplicationFactory.AdminPassword);

        Assert.Equal("/", location);
    }

    [Fact]
    public async Task FullFlow_CodePkce_Userinfo_Refresh()
    {
        var browser = NewBrowser();
        var (verifier, challenge) = NewPkcePair();
        var authorizeUrl = AuthorizeUrl(challenge);

        // 1. Sign in through the login page, which sends the browser back to /connect/authorize.
        var returnedTo = await LoginAsync(browser, authorizeUrl, StsWebApplicationFactory.AdminPassword);
        Assert.StartsWith("/connect/authorize", returnedTo);

        // 2. Authorize: redirects to the client with a code.
        var authorize = await browser.GetAsync(returnedTo);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var callback = authorize.Headers.Location!;
        Assert.StartsWith(StsWebApplicationFactory.AdminPortalRedirectUri, callback.AbsoluteUri);
        var query = QueryHelpers.ParseQuery(callback.Query);
        Assert.Equal("xyz", query["state"]);
        var code = query["code"].ToString();
        Assert.NotEmpty(code);

        // 3. Redeem the code with the PKCE verifier.
        var tokenResponse = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = "admin-portal",
            ["code"] = code,
            ["redirect_uri"] = StsWebApplicationFactory.AdminPortalRedirectUri,
            ["code_verifier"] = verifier,
        }));
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var tokens = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = tokens.GetProperty("access_token").GetString()!;
        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(tokens.GetProperty("id_token").GetString()));

        // 4. The access token works against /connect/userinfo.
        using var userinfoRequest = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        userinfoRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var userinfoResponse = await _factory.CreateClient().SendAsync(userinfoRequest);
        Assert.Equal(HttpStatusCode.OK, userinfoResponse.StatusCode);
        var userinfo = await userinfoResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(StsWebApplicationFactory.AdminEmail, userinfo.GetProperty("email").GetString());
        Assert.Equal("System Administrator", userinfo.GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(userinfo.GetProperty("sub").GetString()));

        // 5. The authorization code is single use.
        var replay = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = "admin-portal",
            ["code"] = code,
            ["redirect_uri"] = StsWebApplicationFactory.AdminPortalRedirectUri,
            ["code_verifier"] = verifier,
        }));
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);

        // 6. Refresh token rotation: a new refresh token is issued.
        var refreshResponse = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = "admin-portal",
            ["refresh_token"] = refreshToken,
        }));
        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
        var refreshed = await refreshResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(refreshed.GetProperty("access_token").GetString()));
        Assert.NotEqual(refreshToken, refreshed.GetProperty("refresh_token").GetString());
    }

    [Fact]
    public async Task Token_WithWrongCodeVerifier_IsRejected()
    {
        var browser = NewBrowser();
        var (_, challenge) = NewPkcePair();

        var returnedTo = await LoginAsync(browser, AuthorizeUrl(challenge), StsWebApplicationFactory.AdminPassword);
        var authorize = await browser.GetAsync(returnedTo);
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();

        var response = await browser.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = "admin-portal",
            ["code"] = code,
            ["redirect_uri"] = StsWebApplicationFactory.AdminPortalRedirectUri,
            ["code_verifier"] = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_grant", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Authorize_WithoutPkce_IsRejected()
    {
        var browser = NewBrowser();
        var url = QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = "admin-portal",
            ["response_type"] = "code",
            ["redirect_uri"] = StsWebApplicationFactory.AdminPortalRedirectUri,
            ["scope"] = "openid",
        });

        var response = await browser.GetAsync(url);

        // Rejected up front (before any login is requested) because the client requires PKCE.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }
}
