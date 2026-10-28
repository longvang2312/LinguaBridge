using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using static AppInHouse.Identity.IntegrationTests.AdminApi.AdminApiHost;
using static AppInHouse.Identity.IntegrationTests.AdminApi.AdminWorld;

namespace AppInHouse.Identity.IntegrationTests.AdminApi;

public class ClientsApiTests
{
    private static async Task<AdminApiHost> StartWithScopesAsync()
    {
        var api = await AdminApiHost.StartAsync();
        await api.ScopeAsync(async sp =>
        {
            var scopes = sp.GetRequiredService<IOpenIddictScopeManager>();
            foreach (var name in new[] { "openid", "profile", "offline_access", "orders_api" })
                await scopes.CreateAsync(new OpenIddictScopeDescriptor { Name = name });
        });
        return api;
    }

    private static object Web(string clientId = "erp-web", object? redirectUris = null, object? scopes = null, bool refresh = true) => new
    {
        clientId,
        displayName = "ERP Web",
        type = "Web",
        redirectUris = redirectUris ?? new[] { "https://erp.example.com/signin-oidc" },
        postLogoutRedirectUris = new[] { "https://erp.example.com/signout-callback-oidc" },
        scopes = scopes ?? new[] { "profile", "orders_api" },
        allowRefreshTokens = refresh,
    };

    private static object Machine(string clientId = "svc-erp") => new
    {
        clientId,
        displayName = "ERP service",
        type = "Machine",
        scopes = new[] { "orders_api" },
    };

    private static Task<bool> SecretIsValidAsync(AdminApiHost api, string clientId, string secret)
        => api.ScopeAsync(async sp =>
        {
            var apps = sp.GetRequiredService<IOpenIddictApplicationManager>();
            return await apps.ValidateClientSecretAsync((await apps.FindByClientIdAsync(clientId))!, secret);
        });

    private static async Task<string> AllAuditJsonAsync(AdminApiHost api)
        => string.Join("\n", (await api.AuditLogsAsync()).Select(l => $"{l.OldValuesJson} {l.NewValuesJson}"));

    // ---- create ----

    [Fact]
    public async Task WebClient_IsCreatedConfidential_WithAOneTimeSecret_ThatIsStoredHashedAndNeverAudited()
    {
        await using var api = await StartWithScopesAsync();

        var response = await api.PostAsync("/api/v1/clients", Root, Web());

        var created = await ExpectAsync(HttpStatusCode.Created, response);
        var secret = created.GetProperty("clientSecret").GetString()!;
        var client = created.GetProperty("client");
        Assert.True(secret.Length >= 32);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("Web", client.GetProperty("type").GetString());
        Assert.True(client.GetProperty("isConfidential").GetBoolean());
        Assert.Equal(new[] { "offline_access", "openid", "orders_api", "profile" }, client.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()!).ToArray());
        Assert.True(client.GetProperty("allowRefreshTokens").GetBoolean());

        Assert.True(await SecretIsValidAsync(api, "erp-web", secret));
        var stored = await api.ScopeAsync(async sp =>
        {
            var apps = sp.GetRequiredService<IOpenIddictApplicationManager>();
            var descriptor = new OpenIddictApplicationDescriptor();
            await apps.PopulateAsync(descriptor, (await apps.FindByClientIdAsync("erp-web"))!);
            return descriptor.ClientSecret;
        });
        Assert.NotEqual(secret, stored); // only a hash is persisted

        var audit = Assert.Single(await api.AuditLogsAsync(), l => l.EntityName == "Client");
        Assert.Equal("CREATE", audit.Action);
        Assert.Equal(Root, audit.UserId);
        Assert.DoesNotContain(secret, await AllAuditJsonAsync(api));
        Assert.Contains("erp-web", audit.NewValuesJson);
    }

    [Fact]
    public async Task SecretIsNeverReturnedAgain_ByListing()
    {
        await using var api = await StartWithScopesAsync();
        var created = await ExpectAsync(HttpStatusCode.Created, await api.PostAsync("/api/v1/clients", Root, Machine()));
        var secret = created.GetProperty("clientSecret").GetString()!;

        var list = await api.GetAsync("/api/v1/clients", Root);

        var text = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, text);
        Assert.DoesNotContain("clientSecret", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SpaAndMobileClients_ArePublic_WithoutASecret()
    {
        await using var api = await StartWithScopesAsync();

        var spa = await ExpectAsync(HttpStatusCode.Created, await api.PostAsync("/api/v1/clients", Root, new
        {
            clientId = "portal-spa", displayName = "Portal", type = "Spa",
            redirectUris = new[] { "https://portal.example.com/callback", "http://localhost:4200/callback" },
            scopes = new[] { "profile" },
        }));
        var mobile = await ExpectAsync(HttpStatusCode.Created, await api.PostAsync("/api/v1/clients", Root, new
        {
            clientId = "field-app", displayName = "Field app", type = "Mobile",
            redirectUris = new[] { "com.example.fieldapp:/oauth2redirect" },
            scopes = new[] { "profile" },
        }));

        Assert.Equal(JsonValueKind.Null, spa.GetProperty("clientSecret").ValueKind);
        Assert.False(spa.GetProperty("client").GetProperty("isConfidential").GetBoolean());
        Assert.Equal("Spa", spa.GetProperty("client").GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, mobile.GetProperty("clientSecret").ValueKind);
        Assert.Equal("Mobile", mobile.GetProperty("client").GetProperty("type").GetString());
    }

    [Fact]
    public async Task MachineClient_GetsASecret_AndOnlyClientCredentials()
    {
        await using var api = await StartWithScopesAsync();

        var created = await ExpectAsync(HttpStatusCode.Created, await api.PostAsync("/api/v1/clients", Root, Machine()));

        Assert.False(string.IsNullOrEmpty(created.GetProperty("clientSecret").GetString()));
        var permissions = await api.ScopeAsync(async sp =>
        {
            var apps = sp.GetRequiredService<IOpenIddictApplicationManager>();
            return (await apps.GetPermissionsAsync((await apps.FindByClientIdAsync("svc-erp"))!)).ToArray();
        });
        Assert.Contains(OpenIddictConstants.Permissions.GrantTypes.ClientCredentials, permissions);
        Assert.DoesNotContain(OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode, permissions);
        Assert.DoesNotContain(OpenIddictConstants.Permissions.Endpoints.Authorization, permissions);
    }

    [Fact]
    public async Task InteractiveClients_RequirePkce()
    {
        await using var api = await StartWithScopesAsync();
        await ExpectAsync(HttpStatusCode.Created, await api.PostAsync("/api/v1/clients", Root, Web()));

        var requirements = await api.ScopeAsync(async sp =>
        {
            var apps = sp.GetRequiredService<IOpenIddictApplicationManager>();
            return (await apps.GetRequirementsAsync((await apps.FindByClientIdAsync("erp-web"))!)).ToArray();
        });

        Assert.Contains(OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange, requirements);
    }

    // ---- create: validation ----

    [Fact]
    public async Task DuplicateClientIds_AreAConflict()
    {
        await using var api = await StartWithScopesAsync();
        await ExpectAsync(HttpStatusCode.Created, await api.PostAsync("/api/v1/clients", Root, Web()));

        await ExpectAsync(HttpStatusCode.Conflict, await api.PostAsync("/api/v1/clients", Root, Web()));
    }

    [Theory]
    [InlineData("http://erp.example.com/cb")]          // plain http outside localhost
    [InlineData("https://erp.example.com/*")]          // wildcard
    [InlineData("https://erp.example.com/cb#frag")]    // fragment
    [InlineData("javascript:alert(1)")]
    [InlineData("com.example.app:/cb")]                // private-use scheme is for Mobile only
    [InlineData("/relative/path")]
    [InlineData("")]
    public async Task UnsafeRedirectUris_AreRejected_ForWebClients(string uri)
    {
        await using var api = await StartWithScopesAsync();

        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync("/api/v1/clients", Root, Web(redirectUris: new[] { uri })));

        Assert.Empty(await api.AuditLogsAsync());
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData("file:///etc/passwd")]
    public async Task DangerousSchemes_AreRejected_EvenForMobileClients(string uri)
    {
        await using var api = await StartWithScopesAsync();

        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync("/api/v1/clients", Root, new
        {
            clientId = "bad-app", displayName = "Bad", type = "Mobile", redirectUris = new[] { uri },
        }));
    }

    [Fact]
    public async Task InvalidClientDefinitions_AreRejected()
    {
        await using var api = await StartWithScopesAsync();
        var url = "/api/v1/clients";

        // interactive clients need a redirect URI
        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync(url, Root, Web(redirectUris: Array.Empty<string>())));
        // unknown scope
        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync(url, Root, Web(scopes: new[] { "no_such_scope" })));
        // machine clients: no redirects, no refresh tokens, no interactive scopes
        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync(url, Root, new { clientId = "m1", displayName = "M", type = "Machine", redirectUris = new[] { "https://x.example.com/cb" } }));
        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync(url, Root, new { clientId = "m2", displayName = "M", type = "Machine", allowRefreshTokens = true }));
        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync(url, Root, new { clientId = "m3", displayName = "M", type = "Machine", scopes = new[] { "openid" } }));
        // missing / unknown type, bad client id
        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync(url, Root, new { clientId = "t1", displayName = "T" }));
        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync(url, Root, new { clientId = "t2", displayName = "T", type = "Quantum" }));
        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync(url, Root, Machine("has space")));
        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync(url, Root, Machine("ab")));

        Assert.Equal(0, await api.ScopeAsync(sp => sp.GetRequiredService<IOpenIddictApplicationManager>().CountAsync().AsTask()));
    }

    // ---- update ----

    [Fact]
    public async Task UpdatingAClient_ChangesTheEditableFields_ButKeepsTheSecretWorking()
    {
        await using var api = await StartWithScopesAsync();
        var created = await ExpectAsync(HttpStatusCode.Created, await api.PostAsync("/api/v1/clients", Root, Web()));
        var id = created.GetProperty("client").GetProperty("id").GetString();
        var secret = created.GetProperty("clientSecret").GetString()!;

        var updated = await ExpectAsync(HttpStatusCode.OK, await api.PutAsync($"/api/v1/clients/{id}", Root, new
        {
            displayName = "ERP Web (renamed)",
            redirectUris = new[] { "https://erp.example.com/new-callback" },
            postLogoutRedirectUris = Array.Empty<string>(),
            scopes = new[] { "orders_api" },
            allowRefreshTokens = false,
        }));

        Assert.Equal("ERP Web (renamed)", updated.GetProperty("displayName").GetString());
        Assert.Equal("erp-web", updated.GetProperty("clientId").GetString()); // immutable
        Assert.Equal("Web", updated.GetProperty("type").GetString());          // immutable
        Assert.Equal("https://erp.example.com/new-callback", Assert.Single(updated.GetProperty("redirectUris").EnumerateArray()).GetString());
        Assert.False(updated.GetProperty("allowRefreshTokens").GetBoolean());
        Assert.DoesNotContain("profile", updated.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));

        // The stored secret hash must survive the update untouched.
        Assert.True(await SecretIsValidAsync(api, "erp-web", secret));
        var audit = Assert.Single(await api.AuditLogsAsync(), l => l.Action == "UPDATE");
        Assert.Contains("ERP Web\"", audit.OldValuesJson);
        Assert.Contains("renamed", audit.NewValuesJson);
        Assert.DoesNotContain(secret, await AllAuditJsonAsync(api));
    }

    [Fact]
    public async Task UpdatingAClient_AppliesTheSameValidation()
    {
        await using var api = await StartWithScopesAsync();
        var created = await ExpectAsync(HttpStatusCode.Created, await api.PostAsync("/api/v1/clients", Root, Machine()));
        var id = created.GetProperty("client").GetProperty("id").GetString();

        await ExpectAsync(HttpStatusCode.BadRequest, await api.PutAsync($"/api/v1/clients/{id}", Root,
            new { displayName = "X", redirectUris = new[] { "https://x.example.com/cb" } }));
        await ExpectAsync(HttpStatusCode.BadRequest, await api.PutAsync($"/api/v1/clients/{id}", Root,
            new { displayName = "X", scopes = new[] { "unknown" } }));
        await ExpectAsync(HttpStatusCode.NotFound, await api.PutAsync("/api/v1/clients/nope", Root, new { displayName = "X" }));
    }

    // ---- rotate ----

    [Fact]
    public async Task RotatingTheSecret_InvalidatesTheOldOne_AndReturnsTheNewOneOnce()
    {
        await using var api = await StartWithScopesAsync();
        var created = await ExpectAsync(HttpStatusCode.Created, await api.PostAsync("/api/v1/clients", Root, Machine()));
        var id = created.GetProperty("client").GetProperty("id").GetString();
        var oldSecret = created.GetProperty("clientSecret").GetString()!;

        var response = await api.PostAsync($"/api/v1/clients/{id}/rotate-secret", Root);

        var rotated = await ExpectAsync(HttpStatusCode.OK, response);
        var newSecret = rotated.GetProperty("clientSecret").GetString()!;
        Assert.Equal("svc-erp", rotated.GetProperty("clientId").GetString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.NotEqual(oldSecret, newSecret);
        Assert.False(await SecretIsValidAsync(api, "svc-erp", oldSecret));
        Assert.True(await SecretIsValidAsync(api, "svc-erp", newSecret));

        var audit = Assert.Single(await api.AuditLogsAsync(), l => l.Action == "ROTATE_SECRET");
        Assert.Equal(Root, audit.UserId);
        var json = await AllAuditJsonAsync(api);
        Assert.DoesNotContain(oldSecret, json);
        Assert.DoesNotContain(newSecret, json);
    }

    [Fact]
    public async Task PublicClientsHaveNoSecret_AndUnknownClientsAreNotFound()
    {
        await using var api = await StartWithScopesAsync();
        var spa = await ExpectAsync(HttpStatusCode.Created, await api.PostAsync("/api/v1/clients", Root, new
        {
            clientId = "portal-spa", displayName = "Portal", type = "Spa", redirectUris = new[] { "https://portal.example.com/cb" },
        }));

        await ExpectAsync(HttpStatusCode.BadRequest, await api.PostAsync($"/api/v1/clients/{spa.GetProperty("client").GetProperty("id").GetString()}/rotate-secret", Root));
        await ExpectAsync(HttpStatusCode.NotFound, await api.PostAsync("/api/v1/clients/nope/rotate-secret", Root));
    }

    // ---- list / authorization ----

    [Fact]
    public async Task Clients_AreListedWithPaging()
    {
        await using var api = await StartWithScopesAsync();
        for (var i = 1; i <= 3; i++)
            await ExpectAsync(HttpStatusCode.Created, await api.PostAsync("/api/v1/clients", Root, Machine($"svc-{i}")));

        var page1 = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/clients?page=1&pageSize=2", Root));
        var page2 = await ExpectAsync(HttpStatusCode.OK, await api.GetAsync("/api/v1/clients?page=2&pageSize=2", Root));

        Assert.Equal(3, page1.GetProperty("totalCount").GetInt64());
        Assert.Equal(2, page1.GetProperty("items").GetArrayLength());
        Assert.Equal(1, page2.GetProperty("items").GetArrayLength());
        await ExpectAsync(HttpStatusCode.BadRequest, await api.GetAsync("/api/v1/clients?pageSize=500", Root));
    }

    [Theory]
    [InlineData(AdminA)]
    [InlineData(MemberA)]
    [InlineData(Outsider)]
    public async Task OnlyGlobalAdministrators_CanManageClients(string caller)
    {
        await using var api = await StartWithScopesAsync();
        var created = await ExpectAsync(HttpStatusCode.Created, await api.PostAsync("/api/v1/clients", Root, Machine()));
        var id = created.GetProperty("client").GetProperty("id").GetString();
        var secret = created.GetProperty("clientSecret").GetString()!;

        await ExpectAsync(HttpStatusCode.Forbidden, await api.GetAsync("/api/v1/clients", caller));
        await ExpectAsync(HttpStatusCode.Forbidden, await api.PostAsync("/api/v1/clients", caller, Machine("svc-other")));
        await ExpectAsync(HttpStatusCode.Forbidden, await api.PutAsync($"/api/v1/clients/{id}", caller, new { displayName = "Hijacked" }));
        await ExpectAsync(HttpStatusCode.Forbidden, await api.PostAsync($"/api/v1/clients/{id}/rotate-secret", caller));

        Assert.True(await SecretIsValidAsync(api, "svc-erp", secret)); // the secret was not rotated
    }
}
