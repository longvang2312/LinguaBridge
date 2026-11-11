using System.Net;
using System.Text.RegularExpressions;
using AppInHouse.SharedServices.ClientSdk;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AppInHouse.SharedServices.IntegrationTests;

public class ApiKeysPageTests : IClassFixture<WebFactory>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid ActiveId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ExpiredId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid RevokedId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
    private static readonly Guid OtherId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004");
    private readonly WebFactory _factory;

    public ApiKeysPageTests(WebFactory factory)
    {
        _factory = factory;
        var api = _factory.Api;
        api.Failure = null;
        api.Calls.Clear();
        api.ApiKeys =
        [
            new(ActiveId, "linguabridge", "LB main integration", "aih_live_act111", ["email:send", "storage:upload"], "Active", Now.AddDays(-3), Now.AddDays(60), Now.AddMinutes(-5)),
            new(ExpiredId, "linguabridge", "Old integration", "aih_live_exp222", ["config:read"], "Expired", Now.AddDays(-100), Now.AddDays(-10), null),
            new(RevokedId, "identity", "Identity (leaked)", "aih_live_rev333", ["email:send"], "Revoked", Now.AddDays(-50), null, Now.AddDays(-20)),
            new(OtherId, "identity", "Identity prod", "aih_live_oth444", ["email:send", "chat:send"], "Active", Now.AddDays(-1), null, null),
        ];
    }

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static string TokenIn(string html)
    {
        var match = Regex.Match(html, """name="__RequestVerificationToken"[^>]*?value="([^"]+)""", RegexOptions.IgnoreCase);
        Assert.True(match.Success, "no anti-forgery token on the page");
        return match.Groups[1].Value;
    }

    private async Task<(HttpClient Client, string Token, string Html)> OpenAsync(string url = "/ApiKeys")
    {
        var client = NewClient();
        var response = await client.GetAsync(url);
        var html = await response.Content.ReadAsStringAsync();
        return (client, TokenIn(html), html);
    }

    private static List<KeyValuePair<string, string>> CreateFields(string token, string project = "Identity", string name = "Identity staging", string[]? scopes = null, string days = "90")
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token), new("Form.ProjectCode", project), new("Form.Name", name), new("Form.ExpiresInDays", days),
        };
        fields.AddRange((scopes ?? ["email:send", "config:read"]).Select(s => new KeyValuePair<string, string>("Form.Scopes", s)));
        return fields;
    }

    // ------------------------------------------------------------------------------------------------ the list

    [Fact]
    public async Task TheTable_ShowsEveryKey_WithNamePrefixScopesStatusAndDates()
    {
        var (_, _, html) = await OpenAsync();

        Assert.Contains("id=\"keys-table\"", html);
        foreach (var text in new[] { "LB main integration", "Old integration", "Identity (leaked)", "Identity prod", "aih_live_act111…", "aih_live_exp222…" })
            Assert.Contains(text, html);
        Assert.Matches(@"(?s)LB main integration.*?email:send.*?storage:upload", html);   // the scopes of a key, as badges
        Assert.Contains("2026-11-08 08:00", html);        // created
        Assert.Contains("2027-01-10 08:00", html);        // expires
        Assert.Contains("2026-11-11 07:55", html);        // last used
        Assert.Contains("Không hết hạn", html);
        Assert.Contains("Chưa dùng", html);
        Assert.DoesNotContain("aih_live_act111Zk", html);   // only the prefix is ever shown
    }

    [Fact]
    public async Task TheStatus_IsABadge_GreenActive_GreyExpired_RedRevoked()
    {
        var (_, _, html) = await OpenAsync();

        Assert.Matches(@"data-key-id=""" + ActiveId + @"""[^>]*data-status=""Active""(?s).*?badge text-bg-success"">Đang hoạt động", html);
        Assert.Matches(@"data-key-id=""" + ExpiredId + @"""[^>]*data-status=""Expired""(?s).*?badge text-bg-secondary"">Hết hạn", html);
        Assert.Matches(@"data-key-id=""" + RevokedId + @"""[^>]*data-status=""Revoked""(?s).*?badge text-bg-danger"">Đã thu hồi", html);
    }

    [Fact]
    public async Task OnlyAnActiveKey_HasRevokeAndRotateButtons_BothAskForConfirmation()
    {
        var (_, _, html) = await OpenAsync();

        Assert.Equal(2, Regex.Matches(html, "class=\"btn btn-sm btn-outline-danger revoke-button\"").Count);   // the two active keys
        Assert.Equal(2, Regex.Matches(html, "rotate-button").Count);
        Assert.Contains("onsubmit=\"return confirm(", html);
        Assert.Matches(@"(?s)data-key-id=""" + RevokedId + @"""(?:(?!</tr>).)*?<td class=""text-nowrap text-end"">\s*</td>", html);   // a revoked key has nothing to press
    }

    [Fact]
    public async Task TheProjectFilter_ShowsOnlyThatProjectsKeys_AndKeepsAnAllOption()
    {
        var (_, _, all) = await OpenAsync();
        var (_, _, identity) = await OpenAsync("/ApiKeys?project=identity");

        Assert.Contains("Tất cả dự án", all);
        Assert.Contains("LB main integration", all);
        Assert.DoesNotContain("LB main integration", identity);
        Assert.Contains("Identity prod", identity);
        Assert.Contains("Identity (leaked)", identity);
        Assert.Matches("<option value=\"identity\" selected", identity);
        Assert.Contains("<option value=\"linguabridge\"", identity);
    }

    [Fact]
    public async Task AProjectWithoutKeys_SaysSo()
    {
        var (_, _, html) = await OpenAsync("/ApiKeys?project=nobody");

        Assert.Contains("id=\"no-keys\"", html);
        Assert.Contains("nobody", html);
    }

    [Fact]
    public async Task WhenTheApiIsDown_ThePageStillRenders_WithAWarning()
    {
        _factory.Api.Failure = new HttpRequestException("connection refused");

        var response = await NewClient().GetAsync("/ApiKeys");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"load-error\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ThePlaceholder_IsGone_AndThePageIsNeverCached()
    {
        var response = await NewClient().GetAsync("/ApiKeys");
        var html = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("coming-soon", html);
        Assert.DoesNotContain("Đang phát triển", html);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    // ------------------------------------------------------------------------------------------------ the form

    [Fact]
    public async Task TheForm_OffersTheProjectNameEverySevenScopes_AndTheFourLifetimes()
    {
        var (_, _, html) = await OpenAsync();

        foreach (var id in new[] { "Form_ProjectCode", "Form_Name", "Form_ExpiresInDays", "create-key-button" }) Assert.Contains($"id=\"{id}\"", html);
        foreach (var scope in new[] { "email:send", "chat:send", "storage:upload", "storage:read", "storage:delete", "config:read", "config:write" })
            Assert.Matches($"<input class=\"form-check-input\" type=\"checkbox\" id=\"scope_{scope.Replace(':', '_')}\" name=\"Form.Scopes\" value=\"{scope}\"", html);
        Assert.Equal(7, Regex.Matches(html, "name=\"Form.Scopes\"").Count);
        foreach (var label in new[] { "30 ngày", "90 ngày", "1 năm", "Không hết hạn" }) Assert.Contains(label, html);
        Assert.Matches("<option value=\"30\"", html);
        Assert.Matches("<option value=\"365\"", html);
    }

    // ------------------------------------------------------------------------------------------------ creating: the key is shown once

    [Fact]
    public async Task CreatingAKey_ShowsItInAModalWithACopyButtonAndAWarning_OnTheResponseItself()
    {
        var (client, token, _) = await OpenAsync();

        var response = await client.PostAsync("/ApiKeys?handler=Create", new FormUrlEncodedContent(CreateFields(token)));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);   // the page itself, not a redirect: the key is never kept in a cookie or a session
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Contains("id=\"new-key-modal\"", html);
        Assert.Contains($"id=\"plain-key\" value=\"{FakeSharedServicesClient.IssuedKey}\"", html);
        Assert.Contains("id=\"copy-key-button\"", html);
        Assert.Contains("id=\"one-time-warning\"", html);
        Assert.Contains("Chỉ hiển thị một lần", html);
        Assert.Contains("Identity staging", html);
    }

    [Fact]
    public async Task TheFormValues_GoToTheService_TheProjectLowerCased_ScopesAndLifetimeIncluded()
    {
        var (client, token, _) = await OpenAsync();

        await client.PostAsync("/ApiKeys?handler=Create", new FormUrlEncodedContent(CreateFields(token, "  Identity ", "  Staging  ", ["chat:send", "storage:read"], "30")));

        var sent = _factory.Api.LastCreateKey!;
        Assert.Equal(("identity", "Staging", 30), (sent.ProjectCode, sent.Name, sent.ExpiresInDays));
        Assert.Equal(["chat:send", "storage:read"], sent.Scopes);
    }

    [Fact]
    public async Task ChoosingNoExpiry_SendsNoLifetime()
    {
        var (client, token, _) = await OpenAsync();

        await client.PostAsync("/ApiKeys?handler=Create", new FormUrlEncodedContent(CreateFields(token, days: string.Empty)));

        Assert.Null(_factory.Api.LastCreateKey!.ExpiresInDays);
    }

    [Fact]
    public async Task TheKey_IsNotOnThePageAgain_NotAfterAReload_NotInTheListOfKeys()
    {
        var (client, token, _) = await OpenAsync();
        await client.PostAsync("/ApiKeys?handler=Create", new FormUrlEncodedContent(CreateFields(token)));

        var reloaded = await client.GetStringAsync("/ApiKeys");
        var other = await NewClient().GetStringAsync("/ApiKeys");

        foreach (var html in new[] { reloaded, other })
        {
            Assert.DoesNotContain(FakeSharedServicesClient.IssuedKey, html);
            Assert.DoesNotContain("id=\"new-key-modal\"", html);
            Assert.DoesNotContain("plain-key", html);
        }

        Assert.Contains("Identity staging", reloaded);   // the key is in the list now, by its prefix
    }

    [Fact]
    public async Task TheKey_IsNeverPutInACookieOrASession_NotEvenAnEncryptedOne()
    {
        var (client, token, _) = await OpenAsync();

        var created = await client.PostAsync("/ApiKeys?handler=Create", new FormUrlEncodedContent(CreateFields(token)));
        var rotated = await client.PostAsync($"/ApiKeys?handler=Rotate&id={ActiveId}&graceDays=7", new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));

        foreach (var response in new[] { created, rotated })
        {
            var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? string.Join('|', values) : string.Empty;
            // TempData would carry the key to the next request: only this response may hold it. (A TempData cookie that is being
            // cleared has an empty value, which is fine; one with a value is not.)
            Assert.DoesNotMatch(@"CookieTempDataProvider=[^;|\s]", cookies);
            Assert.DoesNotContain(FakeSharedServicesClient.IssuedKey, cookies);
            Assert.DoesNotContain("Session", cookies);
        }
    }

    [Fact]
    public async Task AKeyTheServiceRefuses_ShowsTheReason_AndNoModal()
    {
        var (client, token, _) = await OpenAsync();
        _factory.Api.Failure = new SharedServicesApiException(400, "Validation", new Dictionary<string, string[]> { ["Scopes"] = ["Choose at least one scope."] });

        var response = await client.PostAsync("/ApiKeys?handler=Create", new FormUrlEncodedContent(CreateFields(token, scopes: [])));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"form-error\"", html);
        Assert.Contains("Choose at least one scope.", html);
        Assert.DoesNotContain("id=\"new-key-modal\"", html);
    }

    [Fact]
    public async Task ThePageDoesNotCrashWhenTheApiFailsMidWay()
    {
        var (client, token, _) = await OpenAsync();
        _factory.Api.Failure = new HttpRequestException("down");

        var response = await client.PostAsync("/ApiKeys?handler=Create", new FormUrlEncodedContent(CreateFields(token)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ------------------------------------------------------------------------------------------------ CSRF

    [Theory]
    [InlineData("/ApiKeys?handler=Create")]
    [InlineData("/ApiKeys?handler=Revoke&id=aaaaaaaa-0000-0000-0000-000000000001")]
    [InlineData("/ApiKeys?handler=Rotate&id=aaaaaaaa-0000-0000-0000-000000000001")]
    public async Task APostWithoutAValidAntiForgeryToken_IsRefused_AndNothingIsCalled(string url)
    {
        var client = NewClient();
        await client.GetAsync("/ApiKeys");
        _factory.Api.Calls.Clear();

        var noToken = await client.PostAsync(url, new FormUrlEncodedContent(CreateFields("wrong")));
        var missing = await client.PostAsync(url, new FormUrlEncodedContent([new KeyValuePair<string, string>("Form.Name", "x")]));

        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("create-key:") || c.StartsWith("revoke-key:") || c.StartsWith("rotate-key:"));
    }

    // ------------------------------------------------------------------------------------------------ revoke and rotate

    [Fact]
    public async Task Revoke_CallsTheService_AndComesBackWithAMessage_AndTheKeyIsRevoked()
    {
        var (client, token, _) = await OpenAsync();

        var response = await client.PostAsync($"/ApiKeys?handler=Revoke&id={ActiveId}&project=linguabridge", new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("project=linguabridge", response.Headers.Location!.OriginalString);   // the filter survives
        Assert.Contains($"revoke-key:{ActiveId}", _factory.Api.Calls);
        var next = await client.GetStringAsync(response.Headers.Location.OriginalString);
        Assert.Contains("id=\"flash-message\"", next);
        Assert.Contains("Đã thu hồi key LB main integration", next);
        Assert.Matches(@"data-key-id=""" + ActiveId + @"""[^>]*data-status=""Revoked""", next);
    }

    [Fact]
    public async Task ARevokeTheServiceRefuses_ShowsItsReason()
    {
        var (client, token, _) = await OpenAsync();
        _factory.Api.Failure = new SharedServicesApiException(403, "Only the platform can revoke API keys.");

        var response = await client.PostAsync($"/ApiKeys?handler=Revoke&id={ActiveId}", new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));
        _factory.Api.Failure = null;
        var next = await client.GetStringAsync(response.Headers.Location!.OriginalString);

        Assert.Contains("id=\"flash-error\"", next);
        Assert.Contains("không đủ quyền", next);   // a 403 is explained in the portal's own words
    }

    [Fact]
    public async Task Rotate_ShowsTheNewKeyOnce_AndSaysUntilWhenTheOldOneWorks()
    {
        var (client, token, _) = await OpenAsync();

        var response = await client.PostAsync($"/ApiKeys?handler=Rotate&id={ActiveId}&graceDays=7", new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"rotate-key:{ActiveId}:7", _factory.Api.Calls);
        Assert.Contains($"id=\"plain-key\" value=\"{FakeSharedServicesClient.IssuedKey}\"", html);
        Assert.Contains("Key mới đã được tạo", html);
        Assert.Contains("id=\"retired-note\"", html);
        Assert.Contains("aih_live_act111…", html);
        Assert.DoesNotContain(FakeSharedServicesClient.IssuedKey, await client.GetStringAsync("/ApiKeys"));
    }
}

public class AuditPageTests : IClassFixture<WebFactory>
{
    private static readonly DateTimeOffset T = new(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);
    private readonly WebFactory _factory;

    public AuditPageTests(WebFactory factory)
    {
        _factory = factory;
        var api = _factory.Api;
        api.Failure = null;
        api.Calls.Clear();
        api.AuditActions = ["SendTest", "CreateApiKey", "RevokeApiKey", "CustomAction"];
        api.AuditLogs =
        [
            new(Guid.NewGuid(), "long@example.com", "ActivateVersion", "ChannelConfiguration", "11111111-1111-1111-1111-111111111111", """{"ProjectCode":"default","Version":2,"PreviousActiveVersion":1}""", T.AddMinutes(30)),
            new(Guid.NewGuid(), "apikey:aih_live_abc123", "CreateApiKey", "ApiKey", "22222222-2222-2222-2222-222222222222", """{"Name":"<script>alert(1)</script>","KeyPrefix":"aih_live_new111"}""", T.AddMinutes(20)),
            new(Guid.NewGuid(), "long@example.com", "RevokeApiKey", "ApiKey", "33333333-3333-3333-3333-333333333333", null, T.AddMinutes(10)),
            new(Guid.NewGuid(), "apikey:aih_live_abc123", "SendTest", "ChannelConfiguration", "unsaved", "{\"Provider\":\"Mock\",\"Success\":true,\"Saved\":false}", T.AddDays(-3)),
        ];
    }

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task TheTable_ShowsTimeActorActionResourceTypeAndId()
    {
        var html = await NewClient().GetStringAsync("/Audit");

        Assert.Contains("id=\"audit-table\"", html);
        foreach (var text in new[] { "2026-11-11 08:30:00", "long@example.com", "apikey:aih_live_abc123", "ActivateVersion", "ChannelConfiguration", "11111111-1111-1111-1111-111111111111", "ApiKey", "RevokeApiKey", "SendTest" })
            Assert.Contains(text, html);
        Assert.Contains("4 dòng", html);
        Assert.DoesNotContain("coming-soon", html);
    }

    [Fact]
    public async Task TheDetails_AreShownIndentedBehindAButton_AndAnEntryWithoutDetailsHasNone()
    {
        var html = await NewClient().GetStringAsync("/Audit");

        Assert.Equal(3, Regex.Matches(html, "<details class=\"audit-details\"").Count);   // the entry with no details has no button
        Assert.Contains("Xem chi tiết", html);
        // Indented, one property a line. The encoder writes a line break as &#xA; (or &#xD;&#xA; on Windows), which a browser shows as one.
        Assert.Matches(@"<pre[^>]*>\{(?:&#xD;|&#xA;|\s)+(?:&quot;|\x22)ProjectCode(?:&quot;|\x22): (?:&quot;|\x22)default(?:&quot;|\x22),(?:&#xD;|&#xA;|\s)+(?:&quot;|\x22)Version(?:&quot;|\x22): 2", html);
    }

    [Fact]
    public async Task TheDetails_AreShownAsText_NeverAsMarkup()
    {
        var html = await NewClient().GetStringAsync("/Audit");

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
    }

    [Fact]
    public async Task TheFilters_ArePassedToTheService_AndTheDatesAreWholeUtcDays()
    {
        await NewClient().GetStringAsync("/Audit?actor=apikey&action=SendTest&from=2026-11-01&to=2026-11-10");

        var query = _factory.Api.LastAuditQuery!.Value;
        Assert.Equal(("apikey", "SendTest"), (query.Actor, query.Action));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), query.From);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero), query.To);   // the whole of the 10th, so the end is the start of the 11th
        Assert.Equal(AppInHouse.SharedServices.Web.Pages.AuditModel.PageSize, query.Limit);
    }

    [Fact]
    public async Task FilteringByActionAndPeriod_NarrowsTheRows_AndTheFormKeepsTheChoices()
    {
        var html = await NewClient().GetStringAsync("/Audit?action=CreateApiKey&from=2026-11-11&to=2026-11-11");

        Assert.Contains("CreateApiKey", Regex.Replace(html, "(?s)<select.*?</select>", string.Empty));
        Assert.DoesNotContain("ActivateVersion", Regex.Replace(html, "(?s)<select.*?</select>", string.Empty));   // outside the filter
        Assert.Matches("<option value=\"CreateApiKey\" selected", html);
        Assert.Contains("value=\"2026-11-11\"", html);
        Assert.Contains("1 dòng", html);
    }

    [Fact]
    public async Task TheActionFilter_OffersTheKnownActions_AndWhateverTheLogHas()
    {
        var html = await NewClient().GetStringAsync("/Audit");

        foreach (var action in new[] { "CreateConfig", "ActivateVersion", "RollbackVersion", "SendTest", "CreateApiKey", "RevokeApiKey", "CustomAction" })
            Assert.Contains($"<option value=\"{action}\"", html);
    }

    [Fact]
    public async Task AnEmptyResult_SaysSo()
    {
        var html = await NewClient().GetStringAsync("/Audit?actor=nobody-at-all");

        Assert.Contains("id=\"no-audit\"", html);
        Assert.Contains("0 dòng", html);
    }

    [Fact]
    public async Task APeriodThatEndsBeforeItStarts_IsExplained_AndNothingIsAsked()
    {
        var html = await NewClient().GetStringAsync("/Audit?from=2026-11-10&to=2026-11-01");

        Assert.Contains("id=\"filter-error\"", html);
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("audit:"));
    }

    [Fact]
    public async Task WhenTheApiRefuses_ThePageStillRenders_WithAWarning()
    {
        _factory.Api.Failure = new SharedServicesApiException(403, "Only the platform can read the audit log.");

        var response = await NewClient().GetAsync("/Audit");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"load-error\"", html);
        Assert.Contains("không đủ quyền", html);
    }
}

/// <summary>The portal against the real API: creating, using, rotating and revoking keys, and seeing it in the audit log.</summary>
public class ApiKeysPageEndToEndTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static string TokenIn(string html) => Regex.Match(html, """name="__RequestVerificationToken"[^>]*?value="([^"]+)""").Groups[1].Value;

    private static string PlainKeyIn(string html) => WebUtility.HtmlDecode(Regex.Match(html, "id=\"plain-key\" value=\"([^\"]+)\"").Groups[1].Value);

    private async Task<HttpClient> PortalAsync()
    {
        var admin = factory.ClientWithKey((await factory.CreateKeyForProjectAsync("default", null, [.. ApiScopes.Assignable])).PlainKey);
        return new PortalOnRealApiFactory(admin).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
    }

    private static List<KeyValuePair<string, string>> Fields(string token, string project, string name) =>
    [
        new("__RequestVerificationToken", token), new("Form.ProjectCode", project), new("Form.Name", name), new("Form.ExpiresInDays", "30"),
        new("Form.Scopes", "config:read"), new("Form.Scopes", "email:send"),
    ];

    [Fact]
    public async Task CreateUseRotateAndRevokeAKey_ThroughThePortal_AndSeeEachStepInTheAuditLog()
    {
        var portal = await PortalAsync();
        var project = "p" + Guid.NewGuid().ToString("N")[..8];
        var name = "Portal key " + Guid.NewGuid().ToString("N")[..6];

        // Create: the modal shows the full key once, and the key really works against the API.
        var page = await portal.GetStringAsync("/ApiKeys");
        var created = await portal.PostAsync("/ApiKeys?handler=Create", new FormUrlEncodedContent(Fields(TokenIn(page), project, name)));
        var createdHtml = await created.Content.ReadAsStringAsync();
        var plain = PlainKeyIn(createdHtml);
        Assert.Matches("^aih_live_[a-z0-9]{6}[A-Za-z0-9_-]{43}$", plain);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientWithKey(plain).GetAsync($"/api/v1/api-keys?projectCode={project}")).StatusCode);

        // It is listed (by prefix), and the key is nowhere on the page any more.
        var listed = await portal.GetStringAsync($"/ApiKeys?project={project}");
        Assert.Contains(name, listed);
        Assert.Contains(plain[..15] + "…", listed);
        Assert.DoesNotContain(plain[15..], listed);
        Assert.DoesNotContain("id=\"new-key-modal\"", listed);

        // Rotate: a new key, the old one still valid for the grace period.
        var id = Guid.Parse(Regex.Match(listed, "data-key-id=\"([0-9a-f-]{36})\"").Groups[1].Value);
        var rotated = await portal.PostAsync($"/ApiKeys?handler=Rotate&id={id}&graceDays=7", new FormUrlEncodedContent([new("__RequestVerificationToken", TokenIn(listed))]));
        var newPlain = PlainKeyIn(await rotated.Content.ReadAsStringAsync());
        Assert.NotEqual(plain, newPlain);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientWithKey(plain).GetAsync($"/api/v1/api-keys?projectCode={project}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientWithKey(newPlain).GetAsync($"/api/v1/api-keys?projectCode={project}")).StatusCode);

        // Revoke the new key from the page: the very next call with it is refused.
        listed = await portal.GetStringAsync($"/ApiKeys?project={project}");
        var newId = Guid.Parse(Regex.Matches(listed, "data-key-id=\"([0-9a-f-]{36})\"[^>]*data-status=\"Active\"").Select(m => m.Groups[1].Value).Single(v => v != id.ToString()));
        var revoked = await portal.PostAsync($"/ApiKeys?handler=Revoke&id={newId}&project={project}", new FormUrlEncodedContent([new("__RequestVerificationToken", TokenIn(listed))]));
        Assert.Equal(HttpStatusCode.Redirect, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.ClientWithKey(newPlain).GetAsync($"/api/v1/api-keys?projectCode={project}")).StatusCode);
        Assert.Matches(@"data-key-id=""" + newId + @"""[^>]*data-status=""Revoked""", await portal.GetStringAsync($"/ApiKeys?project={project}"));

        // The audit page shows all of it, with the prefix and without any key.
        var audit = await portal.GetStringAsync("/Audit");
        foreach (var action in new[] { "CreateApiKey", "RotateApiKey", "RevokeApiKey" }) Assert.Contains($"data-action=\"{action}\"", audit);
        Assert.Contains(plain[..15], audit);
        Assert.DoesNotContain(plain[15..], audit);
        Assert.DoesNotContain(newPlain[15..], audit);
        var filtered = await portal.GetStringAsync("/Audit?action=RevokeApiKey");
        Assert.DoesNotContain("data-action=\"CreateApiKey\"", filtered);
        Assert.Contains("data-action=\"RevokeApiKey\"", filtered);
    }

    [Fact]
    public async Task AnUnknownScopeTheServiceRefuses_IsShownOnTheForm()
    {
        var portal = await PortalAsync();
        var page = await portal.GetStringAsync("/ApiKeys");
        var fields = Fields(TokenIn(page), "p" + Guid.NewGuid().ToString("N")[..8], "Bad scope");
        fields.Add(new("Form.Scopes", "nonsense:scope"));

        var html = await (await portal.PostAsync("/ApiKeys?handler=Create", new FormUrlEncodedContent(fields))).Content.ReadAsStringAsync();

        Assert.Contains("id=\"form-error\"", html);
        Assert.Contains("nonsense:scope", html);
        Assert.DoesNotContain("id=\"new-key-modal\"", html);
    }
}
