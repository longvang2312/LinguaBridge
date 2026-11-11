using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AppInHouse.SharedServices.ClientSdk;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.SharedServices.IntegrationTests;

public class StoragePageTests : IClassFixture<WebFactory>
{
    private const string Mask = "••••••••";
    private static readonly Guid PngId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PdfId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private readonly WebFactory _factory;

    public StoragePageTests(WebFactory factory)
    {
        _factory = factory;
        var api = _factory.Api;
        api.Failure = null;
        api.Calls.Clear();
        api.StorageTestResult = null;
        api.FileBytes = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        api.Items =
        [
            Storage("Default storage", 2, "Active", "default", "LocalDisk", """{"rootPath":"uploads","maxFileSizeMb":10,"publicUrlBase":"https://cdn.example.com/files"}""", hasSecret: false),
            Storage("Default storage", 1, "Archived", "default", "LocalDisk", """{"rootPath":"old"}""", hasSecret: false),
            Storage("Default storage", 1, "Active", "linguabridge", "S3", """{"bucket":"lb-media","region":"ap-southeast-1","serviceUrl":"http://minio:9000","forcePathStyle":true,"accessKeyId":"lb-key-id"}"""),
            Storage("Default storage", 1, "Active", "mocked", "Mock", "{}", hasSecret: false),
        ];
        api.StoredFiles =
        [
            new(PngId, "default", "LocalDisk", "holiday.png", "image/png", 2048, new string('a', 64), false, true, new DateTimeOffset(2026, 11, 11, 8, 30, 0, TimeSpan.Zero), $"/api/v1/storage/files/{PngId}", $"/api/v1/storage/files/{PngId}/thumbnail"),
            new(PdfId, "default", "LocalDisk", "report <b>.pdf", "application/pdf", 3 * 1024 * 1024, new string('b', 64), true, false, new DateTimeOffset(2026, 11, 11, 8, 20, 0, TimeSpan.Zero), $"/api/v1/storage/files/{PdfId}", null),
        ];
        api.MediaLogs =
        [
            new(Guid.NewGuid(), "default", PngId, "Upload", "Success", null, new DateTimeOffset(2026, 11, 11, 8, 30, 5, TimeSpan.Zero)),
            new(Guid.NewGuid(), "default", null, "Upload", "Failed", "This kind of file is not allowed.", new DateTimeOffset(2026, 11, 11, 8, 25, 0, TimeSpan.Zero)),
            new(Guid.NewGuid(), "default", PdfId, "Delete", "Success", null, new DateTimeOffset(2026, 11, 11, 8, 10, 0, TimeSpan.Zero)),
        ];
    }

    private static ChannelConfigurationResponse Storage(string name, int version, string status, string project, string provider, string settings, bool hasSecret = true) =>
        WebFactory.Config(name, version, status, project: project, channel: "Storage", provider: provider, hasSecret: hasSecret) with { Settings = JsonDocument.Parse(settings).RootElement.Clone() };

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static string TokenIn(string html)
    {
        var match = Regex.Match(html, """name="__RequestVerificationToken"[^>]*?value="([^"]+)""", RegexOptions.IgnoreCase);
        Assert.True(match.Success, "no anti-forgery token on the page");
        return match.Groups[1].Value;
    }

    private static List<KeyValuePair<string, string>> FormFields(string token, Dictionary<string, string>? changes = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Form.ProjectCode"] = "default",
            ["Form.Name"] = "Default storage",
            ["Form.ProviderType"] = "LocalDisk",
            ["Form.RootPath"] = "uploads",
            ["Form.MaxFileSizeMb"] = "10",
            ["Form.PublicUrlBase"] = "https://cdn.example.com/files",
            ["Form.Secret"] = Mask,
        };
        foreach (var (key, value) in changes ?? []) fields[key] = value;
        return [.. fields];
    }

    private async Task<(HttpClient Client, string Token, string Html)> OpenAsync(string url = "/Storage")
    {
        var client = NewClient();
        var html = await client.GetStringAsync(url);
        return (client, TokenIn(html), html);
    }

    private static async Task<JsonElement> PostTestAsync(HttpClient client, string token, Dictionary<string, string>? changes = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Storage?handler=SendTest") { Content = new FormUrlEncodedContent(FormFields(token, changes)) };
        request.Headers.Add("RequestVerificationToken", token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    // ------------------------------------------------------------------------------------------------ the configuration tab

    [Fact]
    public async Task ThePage_HasTheProvidersAndTheFieldsOfEach()
    {
        var (_, _, html) = await OpenAsync();

        Assert.Contains("Lưu trữ &amp; Media Browser", html);
        foreach (var provider in new[] { "LocalDisk", "S3", "Mock" })
            Assert.Contains($"<option value=\"{provider}\"", html);
        foreach (var id in new[] { "Form_RootPath", "Form_MaxFileSizeMb", "Form_PublicUrlBase", "Form_Bucket", "Form_Region", "Form_ServiceUrl", "Form_ForcePathStyle", "Form_AccessKeyId", "Form_Secret", "send-test-button", "save-draft", "save-activate", "mock-note" })
            Assert.Contains($"id=\"{id}\"", html);
        Assert.Contains("Kiểm Tra Kết Nối", html);
        Assert.Contains("Lưu Bản Nháp", html);
    }

    [Fact]
    public async Task TheS3Secret_IsNeverOnThePage_OnlyTheMask()
    {
        var (_, _, html) = await OpenAsync("/Storage?project=linguabridge");

        Assert.Contains($"value=\"{Mask}\"", html);
        Assert.Matches("<input[^>]*id=\"Form_Secret\"[^>]*type=\"password\"|<input[^>]*type=\"password\"[^>]*id=\"Form_Secret\"", html);
        Assert.Matches("<option value=\"S3\"[^>]*selected", html);
        Assert.Contains("lb-media", html);
        Assert.Contains("lb-key-id", html);
        Assert.Contains("http://minio:9000", html);
    }

    [Fact]
    public async Task EachProject_ShowsItsOwnProviderAndSettings()
    {
        var (_, _, local) = await OpenAsync("/Storage");
        var (_, _, mock) = await OpenAsync("/Storage?project=mocked");

        Assert.Matches("<option value=\"LocalDisk\"[^>]*selected", local);
        Assert.Contains("uploads", local);
        Assert.Contains("https://cdn.example.com/files", local);
        Assert.Matches("<option value=\"Mock\"[^>]*selected", mock);
    }

    [Fact]
    public async Task AProjectWithoutAConfiguration_SaysItUsesTheDefaultOne()
    {
        var (_, _, html) = await OpenAsync("/Storage?project=brandnew");

        Assert.Contains("chưa có cấu hình lưu trữ", html);
        Assert.Contains("brandnew", html);
    }

    [Fact]
    public async Task TheFormShowsTheNewestVersion_EvenWhenItIsAnUnactivatedDraft()
    {
        _factory.Api.Items = [.. _factory.Api.Items, Storage("Default storage", 3, "Draft", "default", "LocalDisk", """{"rootPath":"draft-root"}""", hasSecret: false)];

        var (_, _, html) = await OpenAsync();

        Assert.Contains("draft-root", html);
        Assert.DoesNotContain("value=\"uploads\"", html);
        Assert.Contains("id=\"live-version\"", html);   // and it says which version is live
    }

    [Fact]
    public async Task WhenTheApiIsDown_ThePageStillRenders_WithAWarning()
    {
        _factory.Api.Failure = new HttpRequestException("connection refused");

        var response = await NewClient().GetAsync("/Storage");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"load-error\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheSidebar_LightsTheStorageEntry_AndTheOldConfigurationListStillWorks()
    {
        var html = await NewClient().GetStringAsync("/Storage");

        Assert.Matches("class=\"nav-link active\"[^>]*href=\"/Storage\"", html);
        Assert.Equal(HttpStatusCode.OK, (await NewClient().GetAsync("/Configurations?channel=Storage")).StatusCode);
    }

    // ------------------------------------------------------------------------------------------------ saving

    [Fact]
    public async Task SaveDraft_PostsTheFormValues_AsAStorageConfiguration_AndDoesNotActivate()
    {
        var (client, token, _) = await OpenAsync();

        var response = await client.PostAsync("/Storage?handler=SaveDraft", new FormUrlEncodedContent(FormFields(token, new() { ["Form.RootPath"] = "new-root", ["Form.MaxFileSizeMb"] = "20" })));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var saved = _factory.Api.LastSave!;
        Assert.Equal(("Storage", "LocalDisk", "Default storage"), (saved.ChannelType, saved.ProviderType, saved.Name));
        Assert.Equal("new-root", saved.Settings!.Value.GetProperty("rootPath").GetString());
        Assert.Equal(20, saved.Settings.Value.GetProperty("maxFileSizeMb").GetInt32());
        Assert.Null(saved.Secret);   // the mask means "keep the stored secret"
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("activate:"));
    }

    [Fact]
    public async Task SaveAndActivate_SavesThenActivates_AndATypedSecretIsSent()
    {
        var (client, token, _) = await OpenAsync();

        var response = await client.PostAsync("/Storage?handler=SaveActivate", new FormUrlEncodedContent(FormFields(token, new()
        {
            ["Form.ProviderType"] = "S3", ["Form.Bucket"] = "new-bucket", ["Form.Region"] = "us-east-1", ["Form.AccessKeyId"] = "new-key-id", ["Form.ForcePathStyle"] = "true", ["Form.Secret"] = "Typed-S3-Secret-Access-Key-0123456789",
        })));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var saved = _factory.Api.LastSave!;
        Assert.Equal("Typed-S3-Secret-Access-Key-0123456789", saved.Secret);
        Assert.Equal("new-bucket", saved.Settings!.Value.GetProperty("bucket").GetString());
        Assert.True(saved.Settings.Value.GetProperty("forcePathStyle").GetBoolean());
        var calls = _factory.Api.Calls.ToList();
        Assert.True(calls.FindIndex(c => c.StartsWith("save:")) < calls.FindIndex(c => c.StartsWith("activate:")));
    }

    [Fact]
    public async Task FieldsOfAnotherProvider_AreNotSaved()
    {
        var (client, token, _) = await OpenAsync();

        await client.PostAsync("/Storage?handler=SaveDraft", new FormUrlEncodedContent(FormFields(token, new()
        {
            ["Form.ProviderType"] = "Mock", ["Form.RootPath"] = "ignored", ["Form.Bucket"] = "ignored-too",
        })));

        Assert.Equal(JsonValueKind.Object, _factory.Api.LastSave!.Settings!.Value.ValueKind);
        Assert.Empty(_factory.Api.LastSave.Settings.Value.EnumerateObject());
    }

    [Fact]
    public async Task ASaveThatTheApiRefuses_ShowsItsReason_AndDoesNotCrash()
    {
        var (client, token, _) = await OpenAsync();
        _factory.Api.Failure = new SharedServicesApiException(400, "Validation", new Dictionary<string, string[]> { ["SettingsJson"] = ["rootPath must not contain '..'."] });

        var save = await client.PostAsync("/Storage?handler=SaveDraft", new FormUrlEncodedContent(FormFields(token)));
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
        _factory.Api.Failure = null;
        var next = await client.GetStringAsync(save.Headers.Location!.OriginalString);

        Assert.Contains("id=\"flash-error\"", next);
        Assert.Contains("must not contain", next);
    }

    [Theory]
    [InlineData("/Storage?handler=SaveDraft")]
    [InlineData("/Storage?handler=SaveActivate")]
    [InlineData("/Storage?handler=Delete&id=11111111-1111-1111-1111-111111111111")]
    public async Task APostWithoutTheAntiForgeryToken_IsRefused_AndNothingIsCalled(string url)
    {
        var client = NewClient();
        await client.GetAsync("/Storage");
        _factory.Api.Calls.Clear();

        var response = await client.PostAsync(url, new FormUrlEncodedContent(FormFields("wrong")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("save:") || c.StartsWith("activate:") || c.StartsWith("delete-file:"));
    }

    [Fact]
    public async Task AnUploadWithoutTheAntiForgeryToken_IsRefused_AndNothingIsUploaded()
    {
        var client = NewClient();
        await client.GetAsync("/Storage");
        _factory.Api.Calls.Clear();
        using var form = new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "upload", "a.png" } };

        var response = await client.PostAsync("/Storage?handler=Upload", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("upload:"));
    }

    // ------------------------------------------------------------------------------------------------ Test Connection

    [Fact]
    public async Task ATypedSecret_IsTriedFromMemory_WithoutAConfigurationId()
    {
        var (client, token, _) = await OpenAsync("/Storage?project=linguabridge");

        var result = await PostTestAsync(client, token, new()
        {
            ["Form.ProjectCode"] = "linguabridge", ["Form.ProviderType"] = "S3", ["Form.Bucket"] = "tried-bucket", ["Form.Region"] = "us-east-1", ["Form.AccessKeyId"] = "tried-key-id",
            ["Form.Secret"] = "Typed-S3-Secret-Access-Key-0123456789",
        });

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Contains("chưa lưu", result.GetProperty("message").GetString());
        var sent = _factory.Api.LastStorageTestRequest!;
        Assert.Null(sent.ConfigurationId);
        Assert.Equal(("S3", "Typed-S3-Secret-Access-Key-0123456789", "tried-bucket"), (sent.ProviderType, sent.Secret, sent.Settings!.Value.GetProperty("bucket").GetString()));
        Assert.DoesNotContain("Typed-S3-Secret", result.GetRawText());
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("save:") || c.StartsWith("activate:"));
    }

    [Fact]
    public async Task ALocalDiskNeedsNoSecret_SoItIsTriedFromMemory()
    {
        var (client, token, _) = await OpenAsync();

        var result = await PostTestAsync(client, token, new() { ["Form.RootPath"] = "tried-root" });

        Assert.True(result.GetProperty("success").GetBoolean());
        var sent = _factory.Api.LastStorageTestRequest!;
        Assert.Null(sent.ConfigurationId);
        Assert.Equal(("LocalDisk", "tried-root"), (sent.ProviderType, sent.Settings!.Value.GetProperty("rootPath").GetString()));
    }

    [Fact]
    public async Task WithTheMaskAndUnchangedS3Settings_TheSavedVersionIsTested()
    {
        var (client, token, _) = await OpenAsync("/Storage?project=linguabridge");
        var saved = _factory.Api.Items.Single(i => i.ProjectCode == "linguabridge");

        await PostTestAsync(client, token, new()
        {
            ["Form.ProjectCode"] = "linguabridge", ["Form.ProviderType"] = "S3", ["Form.Bucket"] = "lb-media", ["Form.Region"] = "ap-southeast-1", ["Form.ServiceUrl"] = "http://minio:9000",
            ["Form.ForcePathStyle"] = "true", ["Form.AccessKeyId"] = "lb-key-id", ["Form.MaxFileSizeMb"] = string.Empty, ["Form.PublicUrlBase"] = string.Empty,
        });

        var sent = _factory.Api.LastStorageTestRequest!;
        Assert.Equal(saved.Id, sent.ConfigurationId);
        Assert.Null(sent.Secret);
        Assert.Null(sent.ProviderType);
    }

    [Fact]
    public async Task WithTheMaskButChangedS3Settings_NothingIsSent_AndTheUserIsToldToRetypeTheSecret()
    {
        var (client, token, _) = await OpenAsync("/Storage?project=linguabridge");

        var result = await PostTestAsync(client, token, new()
        {
            ["Form.ProjectCode"] = "linguabridge", ["Form.ProviderType"] = "S3", ["Form.Bucket"] = "another-bucket", ["Form.Region"] = "ap-southeast-1", ["Form.AccessKeyId"] = "lb-key-id",
        });

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("Secret Access Key", result.GetProperty("message").GetString());
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("test-storage:"));
    }

    [Fact]
    public async Task WithTheMaskButNoSavedS3Version_NothingIsSent()
    {
        var (client, token, _) = await OpenAsync();   // the default project has a LocalDisk configuration, not an S3 one

        var result = await PostTestAsync(client, token, new() { ["Form.ProviderType"] = "S3", ["Form.Bucket"] = "b-1", ["Form.Region"] = "us-east-1", ["Form.AccessKeyId"] = "key-id" });

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("test-storage:"));
    }

    [Fact]
    public async Task AFailedTest_ShowsTheServicesAnswer()
    {
        var (client, token, _) = await OpenAsync();
        _factory.Api.StorageTestResult = new StorageTestResponse(false, "The service has no permission to write in the folder 'uploads'.", 12, "LocalDisk", false, null);

        var result = await PostTestAsync(client, token);

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("no permission", result.GetProperty("message").GetString());
    }

    [Fact]
    public async Task TheTestHandler_NeedsTheAntiForgeryToken()
    {
        var (client, _, _) = await OpenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Storage?handler=SendTest") { Content = new FormUrlEncodedContent(FormFields("wrong")) };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("test-storage:"));
    }

    // ------------------------------------------------------------------------------------------------ the media browser

    [Fact]
    public async Task TheMediaTab_ListsTheFiles_WithThumbnailsIconsSizesTypesAndActions()
    {
        var (_, _, html) = await OpenAsync("/Storage?tab=media");

        Assert.Matches("id=\"tab-media\"[^>]*>|class=\"nav-link active\"[^>]*id=\"tab-media\"", html);
        Assert.Matches("<div class=\"tab-pane fade show active\" id=\"pane-media\"", html);
        Assert.Contains("holiday.png", html);
        Assert.Matches($@"/Storage\?(?:handler=Thumb&amp;id={PngId}|id={PngId}&amp;handler=Thumb)", html);   // an image shows its thumbnail
        Assert.Contains("bi-file-earmark-pdf", html);                            // a PDF shows an icon
        Assert.Contains("2 KB", html);
        Assert.Contains("3 MB", html);
        Assert.Contains("application/pdf", html);
        Assert.Contains("2026-11-11 08:30 UTC", html);
        Assert.Matches($@"/Storage\?(?:handler=File&amp;id={PngId}|id={PngId}&amp;handler=File)""", html);   // view
        Assert.Matches($@"/Storage\?[^""]*{PngId}[^""]*download=True|/Storage\?[^""]*download=True[^""]*{PngId}", html);   // download
        Assert.Contains("Công khai", html);                                      // the public one is marked
        Assert.Equal(2, Regex.Matches(html, "class=\"btn btn-sm btn-outline-danger delete-button\"").Count);
        Assert.Contains("id=\"file-count\">2<", html);
        Assert.Contains("files:default:60", string.Join('|', _factory.Api.Calls));
    }

    [Fact]
    public async Task AFileNameWithMarkup_IsShownAsText()
    {
        var (_, _, html) = await OpenAsync("/Storage?tab=media");

        Assert.DoesNotContain("report <b>.pdf", html);
        Assert.Contains("report &lt;b&gt;.pdf", html);
    }

    [Fact]
    public async Task TheMediaLogs_AreShown_WithTheirOutcome()
    {
        var (_, _, html) = await OpenAsync("/Storage?tab=media");

        Assert.Contains("id=\"media-logs\"", html);
        Assert.Contains("data-action=\"Upload\" data-status=\"Failed\"", html);
        Assert.Contains("data-action=\"Delete\" data-status=\"Success\"", html);
        Assert.Contains("This kind of file is not allowed.", html);
        Assert.Contains("media-logs:default:20", string.Join('|', _factory.Api.Calls));
    }

    [Fact]
    public async Task AProjectWithoutFiles_SaysSo()
    {
        _factory.Api.StoredFiles = [];
        _factory.Api.MediaLogs = [];

        var (_, _, html) = await OpenAsync("/Storage?tab=media");

        Assert.Contains("id=\"no-files\"", html);
        Assert.Contains("id=\"no-logs\"", html);
    }

    [Fact]
    public async Task TheQuickUpload_SendsTheFile_WithItsNameTypeFlagsAndProject_AndGoesBackToTheMediaTab()
    {
        var (client, token, _) = await OpenAsync("/Storage?tab=media");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(token), "__RequestVerificationToken");
        form.Add(new StringContent("linguabridge"), "project");
        form.Add(new StringContent("true"), "isPublic");
        var file = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 9, 9]);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
        form.Add(file, "upload", "Quick.png");

        var response = await client.PostAsync("/Storage?handler=Upload", form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("tab=media", response.Headers.Location!.OriginalString);
        Assert.Contains("project=linguabridge", response.Headers.Location.OriginalString);
        var (request, content) = _factory.Api.LastUpload!.Value;
        Assert.Equal(("Quick.png", "image/png", true, "linguabridge"), (request.FileName, request.ContentType, request.IsPublic, request.ProjectCode));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 9, 9 }, content);
        var next = await client.GetStringAsync(response.Headers.Location.OriginalString);
        Assert.Contains("id=\"flash-message\"", next);
        Assert.Contains("Quick.png", next);
    }

    [Fact]
    public async Task AnUploadTheServiceRefuses_ShowsItsReason()
    {
        var (client, token, _) = await OpenAsync("/Storage?tab=media");
        _factory.Api.Failure = new SharedServicesApiException(400, "Validation", new Dictionary<string, string[]> { ["Content"] = ["This kind of file is not allowed."] });
        using var form = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" }, { new ByteArrayContent([0x4D, 0x5A]), "upload", "invoice.png" } };

        var response = await client.PostAsync("/Storage?handler=Upload", form);
        _factory.Api.Failure = null;
        var next = await client.GetStringAsync(response.Headers.Location!.OriginalString);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("id=\"flash-error\"", next);
        Assert.Contains("not allowed", next);
    }

    [Fact]
    public async Task AnUploadWithoutAFile_AsksForOne_AndCallsNothing()
    {
        var (client, token, _) = await OpenAsync("/Storage?tab=media");
        _factory.Api.Calls.Clear();
        using var form = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } };

        var response = await client.PostAsync("/Storage?handler=Upload", form);
        var next = await client.GetStringAsync(response.Headers.Location!.OriginalString);

        Assert.Contains("Chọn một tệp", next);
        Assert.DoesNotContain(_factory.Api.Calls, c => c.StartsWith("upload:"));
    }

    [Fact]
    public async Task Delete_CallsTheService_AndGoesBackToTheMediaTab()
    {
        var (client, token, _) = await OpenAsync("/Storage?tab=media");

        var response = await client.PostAsync($"/Storage?handler=Delete&id={PdfId}&project=default", new FormUrlEncodedContent([new KeyValuePair<string, string>("__RequestVerificationToken", token)]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("tab=media", response.Headers.Location!.OriginalString);
        Assert.Contains($"delete-file:{PdfId}", _factory.Api.Calls);
        Assert.DoesNotContain(_factory.Api.StoredFiles, f => f.Id == PdfId);
        Assert.Contains("Đã xóa", await client.GetStringAsync(response.Headers.Location.OriginalString));
    }

    [Fact]
    public async Task ViewingAFile_StreamsItThroughThePortal_WithItsTypeAndNosniff()
    {
        var response = await NewClient().GetAsync($"/Storage?handler=File&id={PngId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 }, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Null(response.Content.Headers.ContentDisposition?.DispositionType is "attachment" ? "attachment" : null);
    }

    [Fact]
    public async Task DownloadingAFile_IsAnAttachmentWithItsName()
    {
        var response = await NewClient().GetAsync($"/Storage?handler=File&id={PngId}&download=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("holiday.png", response.Content.Headers.ContentDisposition.FileName?.Trim('"') ?? response.Content.Headers.ContentDisposition.FileNameStar);
    }

    [Fact]
    public async Task TheThumbnail_IsServedAsWebp_AndAFileWithoutOneIs404()
    {
        var client = NewClient();

        var thumb = await client.GetAsync($"/Storage?handler=Thumb&id={PngId}");
        var none = await client.GetAsync($"/Storage?handler=Thumb&id={PdfId}");

        Assert.Equal(HttpStatusCode.OK, thumb.StatusCode);
        Assert.Equal("image/webp", thumb.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
    }

    [Fact]
    public async Task AnUnknownFile_Is404_AndAnApiFailureIs502_NotACrash()
    {
        var client = NewClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Storage?handler=File&id={Guid.NewGuid()}")).StatusCode);

        _factory.Api.Failure = new HttpRequestException("down");
        Assert.Equal(HttpStatusCode.BadGateway, (await client.GetAsync($"/Storage?handler=File&id={PngId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Storage?handler=Thumb&id={PngId}")).StatusCode);
    }

    [Fact]
    public async Task TheTabChoice_SurvivesAProjectChange()
    {
        var (_, _, html) = await OpenAsync("/Storage?tab=media");

        Assert.Matches("<input type=\"hidden\" name=\"tab\" value=\"media\"", html);
    }
}

/// <summary>The portal against the real API: the page and the service together, with real files on a real folder.</summary>
public class StoragePageEndToEndTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Mask = "••••••••";

    private static string NewProject() => "p" + Guid.NewGuid().ToString("N")[..8];

    private async Task<HttpClient> AdminApiAsync() =>
        factory.ClientWithKey((await factory.CreateKeyForProjectAsync("default", null,
            ApiScopes.ConfigRead, ApiScopes.ConfigWrite, ApiScopes.StorageUpload, ApiScopes.StorageRead, ApiScopes.StorageDelete)).PlainKey);

    private static string TokenIn(string html) => Regex.Match(html, """name="__RequestVerificationToken"[^>]*?value="([^"]+)""").Groups[1].Value;

    private static string Attribute(string html, string id)
    {
        var tag = Regex.Match(html, $"<(?:input|select)[^>]*id=\"{id}\"[^>]*>", RegexOptions.Singleline).Value;
        Assert.NotEmpty(tag);
        return WebUtility.HtmlDecode(Regex.Match(tag, "value=\"([^\"]*)\"").Groups[1].Value);
    }

    private static List<KeyValuePair<string, string>> FormAsLoaded(string html, string project) =>
    [
        new("__RequestVerificationToken", TokenIn(html)),
        new("Form.ProjectCode", project),
        new("Form.Name", Attribute(html, "Form_Name")),
        new("Form.ProviderType", Regex.Match(Regex.Match(html, "<select[^>]*id=\"Form_ProviderType\"[^>]*>(.*?)</select>", RegexOptions.Singleline).Groups[1].Value, "<option value=\"([^\"]*)\" selected").Groups[1].Value),
        new("Form.RootPath", Attribute(html, "Form_RootPath")),
        new("Form.MaxFileSizeMb", Attribute(html, "Form_MaxFileSizeMb")),
        new("Form.Secret", Attribute(html, "Form_Secret")),
    ];

    private static List<KeyValuePair<string, string>> With(List<KeyValuePair<string, string>> fields, params (string Key, string Value)[] changes)
    {
        var map = fields.ToDictionary(f => f.Key, f => f.Value);
        foreach (var (key, value) in changes) map[key] = value;
        return [.. map];
    }

    private static string[] FilesUnder(string folder) => Directory.Exists(folder) ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories) : [];

    private static async Task<Guid> SaveActiveAsync(HttpClient admin, string project, object settings)
    {
        var created = await admin.PostAsJsonAsync("/api/v1/channel-configurations", new { projectCode = project, channelType = "Storage", providerType = "LocalDisk", name = "Default storage", settings });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/channel-configurations/{id}/activate", null)).StatusCode);
        return id;
    }

    private static HttpClient Browser(PortalOnRealApiFactory portal) => portal.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    [Fact]
    public async Task SaveDraftThenActivate_ActivatesTheDraftsValues_OnAProjectThatAlreadyHasAnActiveVersion()
    {
        var project = NewProject();
        var admin = await AdminApiAsync();
        await SaveActiveAsync(admin, project, new { rootPath = "old-" + project, maxFileSizeMb = 5 });   // already ACTIVE
        using var portal = new PortalOnRealApiFactory(admin);
        var browser = Browser(portal);

        // 1. Change only the folder (the secret stays as shown), Save draft.
        var html = await browser.GetStringAsync($"/Storage?project={project}");
        Assert.Equal("old-" + project, Attribute(html, "Form_RootPath"));
        Assert.Equal(HttpStatusCode.Redirect, (await browser.PostAsync("/Storage?handler=SaveDraft", new FormUrlEncodedContent(With(FormAsLoaded(html, project), ("Form.RootPath", "new-" + project))))).StatusCode);

        // 2. The page now shows the DRAFT, so what is shown is what Save & activate will activate.
        html = await browser.GetStringAsync($"/Storage?project={project}");
        Assert.Equal("new-" + project, Attribute(html, "Form_RootPath"));
        Assert.Contains("id=\"live-version\"", html);

        // 3. Save & activate straight from what is shown.
        Assert.Equal(HttpStatusCode.Redirect, (await browser.PostAsync("/Storage?handler=SaveActivate", new FormUrlEncodedContent(FormAsLoaded(html, project)))).StatusCode);

        // 4. An upload now goes to the draft's folder, not the old one.
        var uploader = factory.ClientWithKey((await factory.CreateKeyForProjectAsync(project, null, ApiScopes.StorageUpload)).PlainKey);
        var upload = await uploader.PostAsync("/api/v1/storage/upload", new MultipartFormDataContent { { new ByteArrayContent(TestFiles.Pdf()), "file", "a.pdf" } });
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        Assert.NotEmpty(FilesUnder(Path.Combine(factory.StoragePath, "new-" + project)));
        Assert.Empty(FilesUnder(Path.Combine(factory.StoragePath, "old-" + project)));
    }

    [Fact]
    public async Task APortalTest_FromMemory_LeavesEverySavedVersionUntouched()
    {
        var project = NewProject();
        var admin = await AdminApiAsync();
        await SaveActiveAsync(admin, project, new { rootPath = "keep-" + project });
        var before = await factory.WithDbAsync(db => db.ChannelConfigurations.Where(c => c.ProjectCode == project).Select(c => new { c.Id, c.Version, c.Status, c.SettingsJson, c.EncryptedSecret, c.LastTestStatus }).ToListAsync());
        using var portal = new PortalOnRealApiFactory(admin);
        var browser = Browser(portal);
        var html = await browser.GetStringAsync($"/Storage?project={project}");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Storage?handler=SendTest") { Content = new FormUrlEncodedContent(With(FormAsLoaded(html, project), ("Form.RootPath", "tried-" + project))) };
        request.Headers.Add("RequestVerificationToken", TokenIn(html));

        var response = await browser.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("success").GetBoolean());
        var after = await factory.WithDbAsync(db => db.ChannelConfigurations.Where(c => c.ProjectCode == project).Select(c => new { c.Id, c.Version, c.Status, c.SettingsJson, c.EncryptedSecret, c.LastTestStatus }).ToListAsync());
        Assert.Equal(before, after);
        Assert.Empty(FilesUnder(Path.Combine(factory.StoragePath, "tried-" + project)));
    }

    [Fact]
    public async Task UploadViewDownloadAndDelete_ThroughThePortal_WorkOnRealFiles_AndTheMediaLogShowsEachStep()
    {
        var project = NewProject();
        var admin = await AdminApiAsync();
        var root = "r-" + project;
        await SaveActiveAsync(admin, project, new { rootPath = root });
        using var portal = new PortalOnRealApiFactory(admin);
        var browser = Browser(portal);
        var png = TestFiles.Png(800, 400);

        // Upload a real PNG from the media tab.
        var page = await browser.GetStringAsync($"/Storage?project={project}&tab=media");
        using var form = new MultipartFormDataContent { { new StringContent(TokenIn(page)), "__RequestVerificationToken" }, { new StringContent(project), "project" } };
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
        form.Add(file, "upload", "Portal.png");
        var uploaded = await browser.PostAsync("/Storage?handler=Upload", form);
        Assert.Equal(HttpStatusCode.Redirect, uploaded.StatusCode);
        Assert.Equal(2, FilesUnder(Path.Combine(factory.StoragePath, root)).Length);   // the file and its thumbnail

        // It appears in the grid with a thumbnail, and the bytes come back through the portal.
        page = await browser.GetStringAsync(uploaded.Headers.Location!.OriginalString);
        var id = Guid.Parse(Regex.Match(page, @"/Storage\?[^""]*?id=([0-9a-f-]{36})[^""]*?handler=Thumb|/Storage\?[^""]*?handler=Thumb[^""]*?id=([0-9a-f-]{36})") is { Success: true } m ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : string.Empty);
        Assert.Contains("Portal.png", page);
        Assert.Equal(png, await (await browser.GetAsync($"/Storage?handler=File&id={id}")).Content.ReadAsByteArrayAsync());
        var thumb = await browser.GetAsync($"/Storage?handler=Thumb&id={id}");
        Assert.Equal("image/webp", thumb.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", (await browser.GetAsync($"/Storage?handler=File&id={id}&download=true")).Content.Headers.ContentDisposition!.DispositionType);

        // The log shows what happened.
        page = await browser.GetStringAsync($"/Storage?project={project}&tab=media");
        foreach (var action in new[] { "Upload", "Thumbnail", "Download" }) Assert.Contains($"data-action=\"{action}\" data-status=\"Success\"", page);

        // Delete it.
        var deleted = await browser.PostAsync($"/Storage?handler=Delete&id={id}&project={project}", new FormUrlEncodedContent([new KeyValuePair<string, string>("__RequestVerificationToken", TokenIn(page))]));
        Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode);
        Assert.Empty(FilesUnder(Path.Combine(factory.StoragePath, root)));
        page = await browser.GetStringAsync($"/Storage?project={project}&tab=media");
        Assert.Contains("id=\"no-files\"", page);
        Assert.Contains("data-action=\"Delete\" data-status=\"Success\"", page);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/Storage?handler=File&id={id}")).StatusCode);
    }

    [Fact]
    public async Task AnExecutableCalledAPng_IsRefusedByThePortalUpload_WithTheReasonShown_AndNothingIsStored()
    {
        var project = NewProject();
        var admin = await AdminApiAsync();
        var root = "r-" + project;
        await SaveActiveAsync(admin, project, new { rootPath = root });
        using var portal = new PortalOnRealApiFactory(admin);
        var browser = Browser(portal);
        var page = await browser.GetStringAsync($"/Storage?project={project}&tab=media");
        using var form = new MultipartFormDataContent { { new StringContent(TokenIn(page)), "__RequestVerificationToken" }, { new StringContent(project), "project" } };
        var file = new ByteArrayContent(TestFiles.Executable());
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
        form.Add(file, "upload", "invoice.png");

        var response = await browser.PostAsync("/Storage?handler=Upload", form);
        var next = await browser.GetStringAsync(response.Headers.Location!.OriginalString);

        Assert.Contains("id=\"flash-error\"", next);
        Assert.Contains("not allowed", next);
        Assert.Empty(FilesUnder(Path.Combine(factory.StoragePath, root)));
        Assert.Contains("data-action=\"Upload\" data-status=\"Failed\"", next);
    }
}
