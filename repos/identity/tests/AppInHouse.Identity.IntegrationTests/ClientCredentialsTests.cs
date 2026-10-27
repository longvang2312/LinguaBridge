using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AppInHouse.Identity.IntegrationTests;

public class ClientCredentialsTests : IClassFixture<StsWebApplicationFactory>
{
    private readonly HttpClient _client;

    public ClientCredentialsTests(StsWebApplicationFactory factory) => _client = factory.CreateClient();

    private Task<HttpResponseMessage> PostTokenAsync(params (string Key, string Value)[] fields)
        => _client.PostAsync("/connect/token", new FormUrlEncodedContent(
            fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))));

    [Fact]
    public async Task Token_ClientCredentials_ReturnsAccessToken()
    {
        var response = await PostTokenAsync(
            ("grant_type", "client_credentials"),
            ("client_id", "internal-service"),
            ("client_secret", StsWebApplicationFactory.InternalServiceSecret),
            ("scope", "identity_admin_api"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("access_token").GetString()));
        Assert.Equal("Bearer", json.GetProperty("token_type").GetString());
        Assert.True(json.GetProperty("expires_in").GetInt32() > 0);
        Assert.False(json.TryGetProperty("refresh_token", out _), "client_credentials must not issue refresh tokens.");
    }

    [Fact]
    public async Task Token_WrongSecret_ReturnsInvalidClient()
    {
        var response = await PostTokenAsync(
            ("grant_type", "client_credentials"),
            ("client_id", "internal-service"),
            ("client_secret", "wrong-secret"),
            ("scope", "identity_admin_api"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_client", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Token_UnknownScope_ReturnsInvalidScope()
    {
        var response = await PostTokenAsync(
            ("grant_type", "client_credentials"),
            ("client_id", "internal-service"),
            ("client_secret", StsWebApplicationFactory.InternalServiceSecret),
            ("scope", "does_not_exist"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_scope", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Token_ScopeNotPermittedForClient_IsRejected()
    {
        var response = await PostTokenAsync(
            ("grant_type", "client_credentials"),
            ("client_id", "internal-service"),
            ("client_secret", StsWebApplicationFactory.InternalServiceSecret),
            ("scope", "profile"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Token_PublicClientCannotUseClientCredentials()
    {
        var response = await PostTokenAsync(
            ("grant_type", "client_credentials"),
            ("client_id", "admin-portal"));

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.Contains(error, new[] { "unauthorized_client", "invalid_client", "invalid_request" });
    }

    [Fact]
    public async Task Userinfo_WithoutToken_IsUnauthorized()
    {
        var response = await _client.GetAsync("/connect/userinfo");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
