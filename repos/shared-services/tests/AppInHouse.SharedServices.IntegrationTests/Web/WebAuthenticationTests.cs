using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using AppInHouse.SharedServices.ClientSdk;
using AppInHouse.SharedServices.IntegrationTests.Support;
using AppInHouse.SharedServices.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace AppInHouse.SharedServices.IntegrationTests.Web;

/// <summary>
/// The portal with sign-in switched on, without an identity server: the metadata the OpenID Connect handler would download is
/// supplied, and a request is "signed in" by headers read by a test-only scheme (which stands in for the session cookie; the
/// challenge, forbid and sign-out behaviour under test is the real one).
/// </summary>
public sealed class SecuredPortalFactory : WebApplicationFactory<WebEntryPoint>
{
    private readonly string requiredRole;
    private readonly bool enabled;

    public SecuredPortalFactory() : this("GlobalAdmin", true) { }

    private SecuredPortalFactory(string requiredRole, bool enabled)
    {
        this.requiredRole = requiredRole;
        this.enabled = enabled;
    }

    public const string Authority = "http://sts.test";

    public static SecuredPortalFactory Create(string requiredRole, bool enabled = true) => new(requiredRole, enabled);

    private readonly string _keys = Path.Combine(Path.GetTempPath(), "ss-auth-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("DataProtection:KeysPath", _keys);
        builder.UseSetting("Authentication:Enabled", enabled ? "true" : "false");
        builder.UseSetting("Authentication:Authority", Authority);
        builder.UseSetting("Authentication:ClientId", "shared-services-portal");
        builder.UseSetting("Authentication:RequireHttpsMetadata", "false");
        builder.UseSetting("Authentication:RequiredRole", requiredRole);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISharedServicesClient>();
            services.AddSingleton<ISharedServicesClient>(new FakeSharedServicesClient());
            if (!enabled) return;

            services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                var configuration = new OpenIdConnectConfiguration
                {
                    Issuer = Authority,
                    AuthorizationEndpoint = Authority + "/connect/authorize",
                    EndSessionEndpoint = Authority + "/connect/logout",
                    TokenEndpoint = Authority + "/connect/token",
                };
                options.Configuration = configuration;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            });
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, HeaderSignInHandler>(HeaderSignInHandler.SchemeName, null);
            services.PostConfigure<AuthenticationOptions>(options => options.DefaultAuthenticateScheme = HeaderSignInHandler.SchemeName);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try { if (Directory.Exists(_keys)) Directory.Delete(_keys, recursive: true); } catch (IOException) { }
    }

    public HttpClient Browser(string? email = null, string? name = null, params string[] roles)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        if (email is not null)
        {
            client.DefaultRequestHeaders.Add(HeaderSignInHandler.UserHeader, email);
            client.DefaultRequestHeaders.Add(HeaderSignInHandler.NameHeader, name ?? email);
            client.DefaultRequestHeaders.Add(HeaderSignInHandler.RolesHeader, string.Join(',', roles));
        }

        return client;
    }

    private sealed class HeaderSignInHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "HeaderSignIn";
        public const string UserHeader = "X-Test-Email";
        public const string NameHeader = "X-Test-Name";
        public const string RolesHeader = "X-Test-Roles";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var email)) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new("sub", "u1"), new("email", email.ToString()), new("name", Request.Headers[NameHeader].ToString()) };
            claims.AddRange(Request.Headers[RolesHeader].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim("role", r)));
            var identity = new ClaimsIdentity(claims, SchemeName, "name", "role");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}

public sealed partial class WebAuthenticationTests : IClassFixture<SecuredPortalFactory>
{
    private readonly SecuredPortalFactory _portal;

    public WebAuthenticationTests(SecuredPortalFactory portal) => _portal = portal;

    public static TheoryData<string> Pages => new() { "/", "/Email", "/Chat", "/Storage", "/ApiKeys", "/Audit" };

    private static string LocationOf(HttpResponseMessage response) => response.Headers.Location?.ToString() ?? string.Empty;

    // The cookie handler redirects with an absolute address; what matters is the path.
    private static string PathOf(HttpResponseMessage response) => new Uri(new Uri("http://localhost"), response.Headers.Location!).AbsolutePath;

    // ------------------------------------------------------------------------------------------------ not signed in

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task WithoutASession_EveryPage_SendsTheBrowserToTheIdentityServer(string path)
    {
        var response = await _portal.Browser().GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("http://sts.test/connect/authorize?", LocationOf(response));
    }

    [Fact]
    public async Task TheSignInRequest_IsAnAuthorizationCodeFlowWithPkce_ForThePortalClient_WithTheFourScopes()
    {
        var response = await _portal.Browser().GetAsync("/");
        var query = System.Web.HttpUtility.ParseQueryString(response.Headers.Location!.Query);

        Assert.Equal("code", query["response_type"]);
        Assert.Equal("shared-services-portal", query["client_id"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrEmpty(query["code_challenge"]));
        Assert.Equal("http://localhost/signin-oidc", query["redirect_uri"]);
        Assert.Equal(["openid", "profile", "email", "roles"], query["scope"]!.Split(' '));
        Assert.False(string.IsNullOrEmpty(query["state"]));
        Assert.False(string.IsNullOrEmpty(query["nonce"]));
    }

    [Fact]
    public async Task TheShortLivedSignInCookies_AreHttpOnlyAndLax_AndSecureOutsideDevelopment()
    {
        var response = await _portal.Browser().GetAsync("/");
        var cookies = response.Headers.GetValues("Set-Cookie").Where(c => c.Contains("Correlation") || c.Contains("Nonce")).ToList();

        Assert.NotEmpty(cookies);
        foreach (var cookie in cookies)
        {
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task TheHealthCheck_AnswersWithoutASession_NotARedirect()
    {
        var response = await _portal.Browser().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/Account/AccessDenied")]
    [InlineData("/Account/Logout?signedOut=true")]
    [InlineData("/Account/Login?error=1")]
    public async Task TheAccountPages_AreOpenToAnyone_SoNoRedirectLoopIsPossible(string path)
    {
        var response = await _portal.Browser().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheLoginPage_StartsTheSignIn_AndAFailedSignInEndsOnAPageThatSaysSo()
    {
        var start = await _portal.Browser().GetAsync("/Account/Login?returnUrl=%2FAudit");
        var failed = await _portal.Browser().GetAsync("/Account/Login?error=1");
        var html = await failed.Content.ReadAsStringAsync();

        Assert.StartsWith("http://sts.test/connect/authorize?", LocationOf(start));
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        Assert.Contains("id=\"login-error\"", html);
        Assert.Contains("Đăng nhập lại", html);
    }

    [Fact]
    public async Task SwitchingAccount_AsksTheIdentityServerForANewLogin()
    {
        var response = await _portal.Browser().GetAsync("/Account/Login?switchAccount=true");

        Assert.Equal("login", System.Web.HttpUtility.ParseQueryString(response.Headers.Location!.Query)["prompt"]);
    }

    [Fact]
    public async Task AFailedCallback_GoesToTheLoginPage_NotToAnErrorPage()
    {
        // The identity server answers with an error (the user refused): the portal must land on its own explanation.
        var response = await _portal.Browser().GetAsync("/signin-oidc?error=access_denied&state=x");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Login?error=1", LocationOf(response));
    }

    // ------------------------------------------------------------------------------------------------ signed in

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task AnAdministrator_OpensEveryPage(string path)
    {
        var response = await _portal.Browser("admin@appinhouse.local", "System Administrator", "GlobalAdmin").GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheTopbar_ShowsTheNameTheEmailAndALogoutButton()
    {
        var html = await (await _portal.Browser("admin@appinhouse.local", "System Administrator", "GlobalAdmin").GetAsync("/")).Content.ReadAsStringAsync();

        Assert.Contains("id=\"current-user-name\">System Administrator<", html);
        Assert.Contains("id=\"current-user-email\">admin@appinhouse.local<", html);
        Assert.Contains("id=\"logout-button\"", html);
        Assert.DoesNotContain("auth-disabled-badge", html);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task ASignedInUserWithoutTheRole_IsSentToTheAccessDeniedPage(string path)
    {
        var response = await _portal.Browser("someone@appinhouse.local", "Someone", "Student").GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/AccessDenied", PathOf(response));
    }

    [Fact]
    public async Task ASignedInUserWithNoRoleAtAll_IsSentThereToo()
    {
        var response = await _portal.Browser("someone@appinhouse.local", "Someone").GetAsync("/ApiKeys");

        Assert.Equal("/Account/AccessDenied", PathOf(response));
    }

    [Fact]
    public async Task TheAccessDeniedPage_IsFriendly_NamesTheAccount_AndOffersAnotherAccountOrTheHomePage()
    {
        var browser = _portal.Browser("someone@appinhouse.local", "Someone", "Student");
        var html = await (await browser.GetAsync("/Account/AccessDenied")).Content.ReadAsStringAsync();

        Assert.Contains("Bạn không có quyền quản trị", html);
        Assert.Contains("someone@appinhouse.local", html);
        Assert.Contains("<code>GlobalAdmin</code>", html);
        Assert.Contains("Đăng nhập bằng tài khoản khác", html);
        Assert.Contains("Quay lại trang chủ", html);
        Assert.Contains("switchAccount=true", html);
        Assert.DoesNotContain("id=\"sidebar\"", html);   // no menu of pages the user may not open
    }

    [Fact]
    public async Task TheRoleComparison_IsExact_SoAnotherRoleThatContainsTheNameDoesNotCount()
    {
        var response = await _portal.Browser("x@appinhouse.local", "X", "NotGlobalAdmin", "globaladmin").GetAsync("/");

        Assert.Equal("/Account/AccessDenied", PathOf(response));
    }

    [Fact]
    public async Task TheRequiredRole_CanBeConfigured()
    {
        using var portal = SecuredPortalFactory.Create("Operations");

        var operations = await portal.Browser("o@appinhouse.local", "Ops", "Operations").GetAsync("/");
        var globalAdmin = await portal.Browser("g@appinhouse.local", "Admin", "GlobalAdmin").GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, operations.StatusCode);
        Assert.Equal("/Account/AccessDenied", PathOf(globalAdmin));
    }

    // ------------------------------------------------------------------------------------------------ logout

    [GeneratedRegex("""<form[^>]*action="/Account/Logout"[^>]*>.*?name="__RequestVerificationToken"[^>]*?value="([^"]+)""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex LogoutToken();

    [Fact]
    public async Task Logout_ClearsTheSession_AndSendsTheBrowserToTheEndSessionEndpointOfTheIdentityServer()
    {
        var browser = _portal.Browser("admin@appinhouse.local", "System Administrator", "GlobalAdmin");
        var page = await (await browser.GetAsync("/")).Content.ReadAsStringAsync();
        var token = LogoutToken().Match(page).Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), "the topbar form has no anti-forgery token");

        var response = await browser.PostAsync("/Account/Logout", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("http://sts.test/connect/logout?", LocationOf(response));
        var query = System.Web.HttpUtility.ParseQueryString(response.Headers.Location!.Query);
        Assert.Equal("http://localhost/signout-callback-oidc", query["post_logout_redirect_uri"]);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("AppInHouse.SharedServices.Session=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Logout_WithoutTheAntiForgeryToken_IsRefused_SoAnotherSiteCannotSignTheAdministratorOut()
    {
        var response = await _portal.Browser("admin@appinhouse.local", "System Administrator", "GlobalAdmin")
            .PostAsync("/Account/Logout", new FormUrlEncodedContent(new Dictionary<string, string>()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Logout_ByGet_OnlyAsksForConfirmation_AndNeverSignsOut()
    {
        var response = await _portal.Browser("admin@appinhouse.local", "System Administrator", "GlobalAdmin").GetAsync("/Account/Logout");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"confirm-logout\"", html);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith("AppInHouse.SharedServices.Session", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AfterTheIdentityServerSendsTheBrowserBack_ThePortalSaysTheUserIsSignedOut_AndOffersToSignInAgain()
    {
        var html = await (await _portal.Browser().GetAsync("/Account/Logout?signedOut=true")).Content.ReadAsStringAsync();

        Assert.Contains("Bạn đã đăng xuất", html);
        Assert.Contains("id=\"sign-in-again\"", html);
    }

    // ------------------------------------------------------------------------------------------------ development mode

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task WithSignInSwitchedOff_EveryPageOpensWithoutAnyLogin(string path)
    {
        using var portal = SecuredPortalFactory.Create("GlobalAdmin", enabled: false);

        var response = await portal.Browser().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WithSignInSwitchedOff_TheTopbarShowsTheDevAdministrator_AWarningBadge_AndNoLogoutButton()
    {
        using var portal = SecuredPortalFactory.Create("GlobalAdmin", enabled: false);

        var html = await (await portal.Browser().GetAsync("/")).Content.ReadAsStringAsync();

        Assert.Contains("Dev Admin", html);
        Assert.Contains("dev-admin@appinhouse.local", html);
        Assert.Contains("id=\"auth-disabled-badge\"", html);
        Assert.DoesNotContain("id=\"logout-button\"", html);
    }

    [Fact]
    public async Task WithSignInSwitchedOff_TheLoginPageJustGoesBackToThePortal()
    {
        using var portal = SecuredPortalFactory.Create("GlobalAdmin", enabled: false);

        var response = await portal.Browser().GetAsync("/Account/Login");

        Assert.Equal("/", LocationOf(response));
    }

    // ------------------------------------------------------------------------------------------------ configuration

    private static IConfiguration Settings(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => KeyValuePair.Create<string, string?>("Authentication:" + v.Key, v.Value))).Build();

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    public void SignInIsOnByDefault_AndTheRoleDefaultsToGlobalAdmin()
    {
        var options = PortalAuthenticationOptions.From(Settings(("Authority", "https://id.example.com"), ("ClientId", "c")), new Env("Production"));

        Assert.True(options.Enabled);
        Assert.Equal("GlobalAdmin", options.RequiredRole);
        Assert.Equal(CookieSecurePolicy.Always, options.CookieSecurePolicy);
    }

    [Theory]
    [InlineData("true", CookieSecurePolicy.Always)]
    [InlineData("false", CookieSecurePolicy.SameAsRequest)]
    public void CookiesAreSecureOutsideDevelopment_UnlessThePortalIsExplicitlyServedOverPlainHttp(string flag, CookieSecurePolicy expected)
    {
        var options = PortalAuthenticationOptions.From(Settings(("Authority", "https://id.example.com"), ("ClientId", "c"), ("SecureCookies", flag)), new Env("Production"));

        Assert.Equal(expected, options.CookieSecurePolicy);
    }

    [Fact]
    public void InDevelopment_CookiesFollowTheRequest_ByDefault()
    {
        var options = PortalAuthenticationOptions.From(Settings(("Enabled", "false")), new Env("Development"));

        Assert.Equal(CookieSecurePolicy.SameAsRequest, options.CookieSecurePolicy);
    }

    [Theory]
    [InlineData("", "c", "false")]
    [InlineData("not a url", "c", "false")]
    [InlineData("ftp://id.example.com", "c", "false")]
    [InlineData("https://id.example.com", "", "false")]
    [InlineData("http://id.example.com", "c", "true")]   // plain HTTP metadata is refused unless RequireHttpsMetadata is switched off
    public void ASignInThatCouldNotWork_StopsThePortalAtStartup_InsteadOfFailingOnTheFirstVisitor(string authority, string clientId, string requireHttps)
    {
        Assert.Throws<InvalidOperationException>(() =>
            PortalAuthenticationOptions.From(Settings(("Authority", authority), ("ClientId", clientId), ("RequireHttpsMetadata", requireHttps)), new Env("Production")));
    }

    [Fact]
    public void WithSignInSwitchedOff_NoIdentityServerSettingsAreNeeded()
    {
        var options = PortalAuthenticationOptions.From(Settings(("Enabled", "false")), new Env("Production"));

        Assert.False(options.Enabled);
    }

    [Fact]
    public void ThePortalDoesNotStart_WhenSignInIsOnAndNoIdentityServerIsConfigured()
    {
        using var factory = new SecuredPortalFactory().WithWebHostBuilder(builder => builder.UseSetting("Authentication:Authority", ""));

        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }
}
