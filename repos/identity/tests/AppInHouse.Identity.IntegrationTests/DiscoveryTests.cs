using System.Net;
using System.Text.Json;

namespace AppInHouse.Identity.IntegrationTests;

public class DiscoveryTests : IClassFixture<StsWebApplicationFactory>
{
    private readonly HttpClient _client;

    public DiscoveryTests(StsWebApplicationFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Discovery_ReturnsOk_WithTokenEndpoint()
    {
        var response = await _client.GetAsync("/.well-known/openid-configuration");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.EndsWith("/connect/token", root.GetProperty("token_endpoint").GetString());
    }

    [Fact]
    public async Task Discovery_AdvertisesAllConfiguredEndpointsAndFlows()
    {
        using var document = JsonDocument.Parse(await _client.GetStringAsync("/.well-known/openid-configuration"));
        var root = document.RootElement;

        Assert.EndsWith("/connect/authorize", root.GetProperty("authorization_endpoint").GetString());
        Assert.EndsWith("/connect/userinfo", root.GetProperty("userinfo_endpoint").GetString());
        Assert.EndsWith("/connect/logout", root.GetProperty("end_session_endpoint").GetString());
        Assert.EndsWith("/connect/introspect", root.GetProperty("introspection_endpoint").GetString());

        var grantTypes = Strings(root, "grant_types_supported");
        Assert.Contains("authorization_code", grantTypes);
        Assert.Contains("client_credentials", grantTypes);
        Assert.Contains("refresh_token", grantTypes);

        Assert.Contains("S256", Strings(root, "code_challenge_methods_supported"));
        Assert.Contains("identity_admin_api", Strings(root, "scopes_supported"));
    }

    [Fact]
    public async Task JwksEndpoint_PublishesSigningKey()
    {
        using var document = JsonDocument.Parse(await _client.GetStringAsync("/.well-known/jwks"));

        Assert.True(document.RootElement.GetProperty("keys").GetArrayLength() > 0);
    }

    private static string[] Strings(JsonElement root, string property)
        => root.GetProperty(property).EnumerateArray().Select(e => e.GetString()!).ToArray();
}
