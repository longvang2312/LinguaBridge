using System.Net;
using System.Text;
using AppInHouse.SharedServices.Application.Storage;
using AppInHouse.SharedServices.Domain;
using AppInHouse.SharedServices.Infrastructure.Email;
using AppInHouse.SharedServices.Infrastructure.Storage;
using AppInHouse.SharedServices.UnitTests.Support;
using Amazon.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AppInHouse.SharedServices.UnitTests;

public class MockStorageProviderTests
{
    [Fact]
    public async Task AFileIsKeptInMemory_ReadBack_AndDeleted()
    {
        var provider = new MockStorageProvider();
        var config = RuntimeConfig.Make(ProviderType.Mock, "{}", null);

        await provider.PutAsync(config, "uploads/p/a.png", new MemoryStream([1, 2, 3]), "image/png");

        Assert.True(provider.Contains("uploads/p/a.png"));
        await using (var read = await provider.OpenReadAsync(config, "uploads/p/a.png"))
        {
            using var copy = new MemoryStream();
            await read!.CopyToAsync(copy);
            Assert.Equal(new byte[] { 1, 2, 3 }, copy.ToArray());
        }

        await provider.DeleteAsync(config, "uploads/p/a.png");
        await provider.DeleteAsync(config, "uploads/p/a.png");   // already gone: fine
        Assert.Null(await provider.OpenReadAsync(config, "uploads/p/a.png"));
        Assert.Equal(0, provider.Count);
    }

    [Fact]
    public async Task TheTest_AlwaysWorks()
    {
        var outcome = await new MockStorageProvider().TestConnectionAsync(RuntimeConfig.Make(ProviderType.Mock, "{}", null));

        Assert.True(outcome.Success);
        Assert.Contains("memory", outcome.Message);
    }
}

public sealed class LocalDiskStorageProviderTests : IDisposable
{
    private readonly string _baseFolder = Path.Combine(Path.GetTempPath(), "ss-disk-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_baseFolder)) Directory.Delete(_baseFolder, recursive: true); } catch (IOException) { }
    }

    private LocalDiskStorageProvider Provider(bool allowAbsolute = false) =>
        new(Options.Create(new StorageOptions { BasePath = _baseFolder, AllowAbsolutePaths = allowAbsolute }));

    private static ChannelRuntimeConfigurationHolder Config(string rootPath) => new(RuntimeConfig.Make(ProviderType.LocalDisk, JsonSerializerHelper.Serialize(new { rootPath }), null));

    private sealed record ChannelRuntimeConfigurationHolder(AppInHouse.SharedServices.Application.Configurations.ChannelRuntimeConfiguration Value);

    private static class JsonSerializerHelper
    {
        public static string Serialize(object value) => System.Text.Json.JsonSerializer.Serialize(value);
    }

    [Fact]
    public async Task AFile_IsWritten_UnderTheRootInsideTheBaseFolder_ReadBack_AndDeleted()
    {
        var provider = Provider();
        var config = Config("uploads").Value;

        await provider.PutAsync(config, "uploads/p/2026/11/abc.png", new MemoryStream([9, 8, 7]), "image/png");

        var path = Path.Combine(_baseFolder, "uploads", "uploads", "p", "2026", "11", "abc.png");
        Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(path));
        await using (var read = await provider.OpenReadAsync(config, "uploads/p/2026/11/abc.png"))
            Assert.Equal(3, read!.Length);

        await provider.DeleteAsync(config, "uploads/p/2026/11/abc.png");
        Assert.False(File.Exists(path));
        await provider.DeleteAsync(config, "uploads/p/2026/11/abc.png");   // a file that is already gone is fine
        Assert.Null(await provider.OpenReadAsync(config, "uploads/p/2026/11/abc.png"));
    }

    [Fact]
    public async Task AnObjectIsNeverOverwritten()
    {
        var provider = Provider();
        var config = Config("r").Value;
        await provider.PutAsync(config, "k/a.bin", new MemoryStream([1]), "x");

        await Assert.ThrowsAsync<IOException>(() => provider.PutAsync(config, "k/a.bin", new MemoryStream([2]), "x"));

        Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(Path.Combine(_baseFolder, "r", "k", "a.bin")));
    }

    [Theory]
    [InlineData("../evil.png")]
    [InlineData("uploads/../../evil.png")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("uploads\\..\\evil.png")]
    [InlineData("uploads//double.png")]
    [InlineData("./a.png")]
    [InlineData("")]
    public async Task AKeyThatCouldLeaveTheRoot_IsRefused_ForEveryOperation(string key)
    {
        var provider = Provider();
        var config = Config("uploads").Value;

        await Assert.ThrowsAsync<StorageProviderException>(() => provider.PutAsync(config, key, new MemoryStream([1]), "x"));
        await Assert.ThrowsAsync<StorageProviderException>(() => provider.OpenReadAsync(config, key));
        await Assert.ThrowsAsync<StorageProviderException>(() => provider.DeleteAsync(config, key));
        Assert.False(File.Exists(Path.Combine(_baseFolder, "evil.png")));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("a/../../outside")]
    public async Task ARootThatLeavesTheBaseFolder_IsRefused(string root)
    {
        var provider = Provider();

        await Assert.ThrowsAsync<StorageProviderException>(() => provider.PutAsync(Config(root).Value, "k/a.bin", new MemoryStream([1]), "x"));
    }

    [Fact]
    public async Task AnAbsoluteRoot_IsRefusedUnlessTheHostAllowsIt()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), "ss-abs-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Assert.ThrowsAsync<StorageProviderException>(() => Provider().PutAsync(Config(elsewhere).Value, "k/a.bin", new MemoryStream([1]), "x"));
            Assert.False(Directory.Exists(elsewhere));

            await Provider(allowAbsolute: true).PutAsync(Config(elsewhere).Value, "k/a.bin", new MemoryStream([1]), "x");
            Assert.True(File.Exists(Path.Combine(elsewhere, "k", "a.bin")));
        }
        finally
        {
            if (Directory.Exists(elsewhere)) Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Fact]
    public async Task ASiblingFolderWithTheSamePrefix_IsNotInsideTheBase()
    {
        // "<base>-evil" starts with "<base>" as text but is not inside it.
        var provider = Provider();

        await Assert.ThrowsAsync<StorageProviderException>(() => provider.PutAsync(Config("../" + Path.GetFileName(_baseFolder) + "-evil").Value, "k/a.bin", new MemoryStream([1]), "x"));
    }

    [Fact]
    public async Task TheTest_WritesReadsAndDeletesACanary_AndLeavesNothingBehind()
    {
        var outcome = await Provider().TestConnectionAsync(Config("canary-root").Value);

        Assert.True(outcome.Success);
        Assert.Contains("canary-root", outcome.Message);
        Assert.False(Directory.Exists(Path.Combine(_baseFolder, "canary-root")));   // the folder the test made is taken away again, not left empty
        Assert.DoesNotContain(_baseFolder, outcome.Message);   // no server path is shown
    }

    [Fact]
    public async Task TheTest_TakesAwayEveryFolderItMade_EvenANestedRoot()
    {
        var outcome = await Provider().TestConnectionAsync(Config("a/b/c").Value);

        Assert.True(outcome.Success);
        Assert.False(Directory.Exists(Path.Combine(_baseFolder, "a")));
        Assert.False(Directory.Exists(Path.Combine(_baseFolder, "a", "b", "c")));
    }

    [Fact]
    public async Task TheTest_LeavesAFolderThatAlreadyExisted_AndOnlyRemovesWhatItAdded()
    {
        Directory.CreateDirectory(Path.Combine(_baseFolder, "keep"));
        await File.WriteAllTextAsync(Path.Combine(_baseFolder, "keep", "important.txt"), "data");

        var outcome = await Provider().TestConnectionAsync(Config("keep/new-sub").Value);

        Assert.True(outcome.Success);
        Assert.True(File.Exists(Path.Combine(_baseFolder, "keep", "important.txt")));   // what was there is untouched
        Assert.False(Directory.Exists(Path.Combine(_baseFolder, "keep", "new-sub")));   // what the test added is gone
    }

    [Fact]
    public async Task TheTest_OfAnExistingRoot_DoesNotRemoveIt()
    {
        var provider = Provider();
        await provider.PutAsync(Config("live").Value, "k/a.bin", new MemoryStream([1]), "x");   // a root that is in use

        var outcome = await provider.TestConnectionAsync(Config("live").Value);

        Assert.True(outcome.Success);
        Assert.True(File.Exists(Path.Combine(_baseFolder, "live", "k", "a.bin")));
    }

    [Fact]
    public async Task TheTest_OfAFolderThatCannotBeUsed_SaysSo_WithoutThrowing()
    {
        Directory.CreateDirectory(_baseFolder);
        await File.WriteAllTextAsync(Path.Combine(_baseFolder, "iamafile"), "x");   // a file where the folder should be

        var outcome = await Provider().TestConnectionAsync(Config("iamafile/sub").Value);

        Assert.False(outcome.Success);
        Assert.Contains("iamafile/sub", outcome.Message);
    }

    [Fact]
    public async Task TheTest_OfARefusedRoot_ExplainsWhy()
    {
        var outcome = await Provider().TestConnectionAsync(Config("../outside").Value);

        Assert.False(outcome.Success);
        Assert.Contains("outside", outcome.Message);
    }

    [Fact]
    public async Task TheTest_WithoutARoot_Fails() =>
        Assert.False((await Provider().TestConnectionAsync(RuntimeConfig.Make(ProviderType.LocalDisk, "{}", null))).Success);
}

public sealed class S3StorageProviderTests
{
    private const string AccessKeyId = "test-access-key-id";
    private const string SecretKey = "fake-secret-access-key-for-tests-0123456789";
    private const string Settings = """{"bucket":"media-bucket","serviceUrl":"https://s3.example.test","forcePathStyle":true,"accessKeyId":"test-access-key-id","region":"us-east-1"}""";

    private sealed class RecordingS3Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, byte[] Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            Calls.Add((request.Method, request.RequestUri!, headers, request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken)));
            return respond(request);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(handler, disposeHandler: false);
    }

    private static bool Carries(byte[] body, byte[] expected) => body.AsSpan().IndexOf(expected) >= 0;

    private static HttpResponseMessage Xml(HttpStatusCode status, string xml) =>
        new(status) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };

    private static HttpResponseMessage S3Error(HttpStatusCode status, string code) =>
        Xml(status, $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>{code}</Code><Message>details that may mention {SecretKey}</Message><RequestId>r1</RequestId></Error>");

    private const string EmptyListing =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Name>media-bucket</Name><KeyCount>0</KeyCount><MaxKeys>1</MaxKeys><IsTruncated>false</IsTruncated></ListBucketResult>";

    private static (S3StorageProvider Provider, RecordingS3Handler Handler) Make(Func<HttpRequestMessage, HttpResponseMessage>? respond = null, string host = "93.184.216.34")
    {
        var handler = new RecordingS3Handler(respond ?? (request => request.Method.Method switch
        {
            "GET" when request.RequestUri!.Query.Contains("list-type") => Xml(HttpStatusCode.OK, EmptyListing),
            "GET" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x42]) },
            "DELETE" => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"abc\"") } },
        }));
        var options = Options.Create(new StorageOptions());
        var policy = new OutboundHostPolicy(Options.Create(new EmailOptions()), new FakeHostResolver(new() { ["s3.example.test"] = [host] }));
        var factory = new S3ClientFactory(options, new StubHttpClientFactory(handler));
        return (new S3StorageProvider(factory, policy, NullLogger<S3StorageProvider>.Instance), handler);
    }

    private static AppInHouse.SharedServices.Application.Configurations.ChannelRuntimeConfiguration Config(string settings = Settings, string? secret = SecretKey) =>
        RuntimeConfig.Make(ProviderType.S3, settings, secret);

    [Fact]
    public async Task APut_GoesToTheBucketWithPathStyle_Signed_WithTheBody_AndNoSecretOnTheWire()
    {
        var (provider, handler) = Make();

        await provider.PutAsync(Config(), "uploads/p/2026/11/a.png", new MemoryStream([1, 2, 3, 4]), "image/png");

        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Put, call.Method);
        Assert.Equal("/media-bucket/uploads/p/2026/11/a.png", call.Uri.AbsolutePath);
        Assert.Equal("s3.example.test", call.Uri.Host);
        Assert.True(Carries(call.Body, [1, 2, 3, 4]));   // the SDK sends it as signed chunks (aws-chunked); the bytes are inside
        Assert.StartsWith("AWS4-HMAC-SHA256", call.Headers["Authorization"]);
        Assert.Contains($"Credential={AccessKeyId}/", call.Headers["Authorization"]);
        Assert.DoesNotContain(SecretKey, string.Join('|', call.Headers.Values) + call.Uri);   // the key signs the request; it is never sent
    }

    [Fact]
    public async Task APut_WithAStreamThatCannotSeek_StillWorks()
    {
        var (provider, handler) = Make();

        await provider.PutAsync(Config(), "k/a.bin", new NonSeekableStream([5, 6, 7]), "application/octet-stream");

        Assert.True(Carries(Assert.Single(handler.Calls).Body, [5, 6, 7]));
    }

    [Fact]
    public async Task AGet_ReturnsTheBytes_AndAMissingKeyIsNull()
    {
        var (provider, _) = Make();
        await using (var read = await provider.OpenReadAsync(Config(), "k/a.bin"))
            Assert.Equal(1, read!.Length);

        var (missing, _) = Make(_ => S3Error(HttpStatusCode.NotFound, "NoSuchKey"));
        Assert.Null(await missing.OpenReadAsync(Config(), "k/gone.bin"));
    }

    [Fact]
    public async Task ADelete_IsSentToTheKey_AndAMissingKeyIsNotAnError()
    {
        var (provider, handler) = Make();

        await provider.DeleteAsync(Config(), "k/a.bin");

        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Delete, call.Method);
        Assert.Equal("/media-bucket/k/a.bin", call.Uri.AbsolutePath);
    }

    [Fact]
    public async Task TheTest_ListsTheBucket_WritesACanary_ReadsItBack_AndDeletesIt()
    {
        var (provider, handler) = Make();

        var outcome = await provider.TestConnectionAsync(Config());

        Assert.True(outcome.Success);
        Assert.Contains("media-bucket", outcome.Message);
        Assert.Equal(["GET", "PUT", "GET", "DELETE"], handler.Calls.Select(c => c.Method.Method));
        Assert.Contains("list-type=2", handler.Calls[0].Uri.Query);
        var canary = handler.Calls[1].Uri.AbsolutePath;
        Assert.StartsWith("/media-bucket/.canary/", canary);
        Assert.Equal(canary, handler.Calls[3].Uri.AbsolutePath);   // the canary that was written is the one deleted
        Assert.True(Carries(handler.Calls[1].Body, [0x42]));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "InvalidAccessKeyId", "access key id")]
    [InlineData(HttpStatusCode.Forbidden, "SignatureDoesNotMatch", "secret access key is wrong")]
    [InlineData(HttpStatusCode.NotFound, "NoSuchBucket", "bucket does not exist")]
    [InlineData(HttpStatusCode.Forbidden, "AccessDenied", "not allowed")]
    public async Task TheTest_ExplainsWhatS3Refused_WithoutRepeatingS3sOwnText(HttpStatusCode status, string code, string expected)
    {
        var (provider, _) = Make(_ => S3Error(status, code));

        var outcome = await provider.TestConnectionAsync(Config());

        Assert.False(outcome.Success);
        Assert.Contains(expected, outcome.Message);
        Assert.DoesNotContain(SecretKey, outcome.Message);
        Assert.DoesNotContain("details that may mention", outcome.Message);
    }

    [Fact]
    public async Task AnUnknownS3Error_IsReportedByItsStatusAndCode()
    {
        var (provider, _) = Make(_ => S3Error(HttpStatusCode.BadRequest, "SomethingNew"));

        var outcome = await provider.TestConnectionAsync(Config());

        Assert.False(outcome.Success);
        Assert.Contains("400 SomethingNew", outcome.Message);
    }

    [Fact]
    public async Task ANetworkFailure_IsReported_WithoutTheExceptionText()
    {
        var (provider, _) = Make(_ => throw new HttpRequestException($"connection refused to s3.example.test using {SecretKey}"));

        var outcome = await provider.TestConnectionAsync(Config());

        Assert.False(outcome.Success);
        Assert.Contains("cannot be reached", outcome.Message);
        Assert.DoesNotContain(SecretKey, outcome.Message);
    }

    [Theory]
    [InlineData("169.254.169.254")]    // the cloud metadata address
    [InlineData("224.0.0.1")]          // multicast
    [InlineData("127.0.0.1")]          // loopback: blocked unless the host allows private networks
    [InlineData("10.0.0.5")]           // private network
    public async Task AServiceUrlThatResolvesToAForbiddenAddress_IsNeverContacted(string address)
    {
        var (provider, handler) = Make(host: address);

        var outcome = await provider.TestConnectionAsync(Config());

        Assert.False(outcome.Success);
        Assert.Empty(handler.Calls);
        await Assert.ThrowsAsync<StorageProviderException>(() => provider.PutAsync(Config(), "k/a", new MemoryStream([1]), "x"));
        Assert.Empty(handler.Calls);
    }

    private static (S3StorageProvider Provider, RecordingS3Handler Handler) MakeWithBucketHost(string bucketHostAddress)
    {
        var handler = new RecordingS3Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x42]) });
        var policy = new OutboundHostPolicy(Options.Create(new EmailOptions()), new FakeHostResolver(new()
        {
            ["s3.example.test"] = ["93.184.216.34"],                 // the service address itself looks harmless
            ["media-bucket.s3.example.test"] = [bucketHostAddress],  // but the name the SDK connects to without path style does not
        }));
        return (new S3StorageProvider(new S3ClientFactory(Options.Create(new StorageOptions()), new StubHttpClientFactory(handler)), policy, NullLogger<S3StorageProvider>.Instance), handler);
    }

    private const string VirtualHostedSettings = """{"bucket":"media-bucket","serviceUrl":"https://s3.example.test","accessKeyId":"test-access-key-id","region":"us-east-1"}""";
    private const string PathStyleSettings = """{"bucket":"media-bucket","serviceUrl":"https://s3.example.test","forcePathStyle":true,"accessKeyId":"test-access-key-id","region":"us-east-1"}""";

    [Theory]
    [InlineData("169.254.169.254")]   // the cloud metadata address
    [InlineData("10.0.0.5")]          // a private network
    [InlineData("127.0.0.1")]
    public async Task WithoutPathStyle_TheBucketSubdomainTheSdkConnectsTo_IsCheckedToo_ForEveryOperation(string address)
    {
        var (provider, handler) = MakeWithBucketHost(address);

        var test = await provider.TestConnectionAsync(Config(VirtualHostedSettings));
        await Assert.ThrowsAsync<StorageProviderException>(() => provider.PutAsync(Config(VirtualHostedSettings), "k/a", new MemoryStream([1]), "x"));
        await Assert.ThrowsAsync<StorageProviderException>(() => provider.OpenReadAsync(Config(VirtualHostedSettings), "k/a"));
        await Assert.ThrowsAsync<StorageProviderException>(() => provider.DeleteAsync(Config(VirtualHostedSettings), "k/a"));

        Assert.False(test.Success);
        Assert.Contains("media-bucket.s3.example.test", test.Message);
        Assert.Empty(handler.Calls);   // nothing was sent to either name
    }

    [Fact]
    public async Task WithPathStyle_OnlyTheServiceHostIsContacted_SoTheBucketNameIsNeverLookedUp()
    {
        var (provider, handler) = MakeWithBucketHost("169.254.169.254");

        var outcome = await provider.TestConnectionAsync(Config(PathStyleSettings));

        Assert.NotEmpty(handler.Calls);
        Assert.All(handler.Calls, c => Assert.Equal("s3.example.test", c.Uri.Host));
        Assert.DoesNotContain("media-bucket.s3.example.test", outcome.Message);
    }

    [Fact]
    public async Task AnIpAddressAsServiceUrl_NeedsNoBucketSubdomain_EvenWithoutPathStyle()
    {
        var handler = new RecordingS3Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x42]) });
        var policy = new OutboundHostPolicy(Options.Create(new EmailOptions { AllowPrivateNetworkHosts = true }), new FakeHostResolver([]));
        var provider = new S3StorageProvider(new S3ClientFactory(Options.Create(new StorageOptions()), new StubHttpClientFactory(handler)), policy, NullLogger<S3StorageProvider>.Instance);

        await provider.DeleteAsync(Config("""{"bucket":"media-bucket","serviceUrl":"http://10.0.0.5:9000","accessKeyId":"test-access-key-id","region":"us-east-1"}"""), "k/a");

        Assert.Equal("10.0.0.5", Assert.Single(handler.Calls).Uri.Host);
    }

    [Fact]
    public async Task APrivateServiceUrl_IsAllowedWhenTheHostAllowsPrivateNetworks()
    {
        var handler = new RecordingS3Handler(request => request.Method == HttpMethod.Get && request.RequestUri!.Query.Contains("list-type") ? Xml(HttpStatusCode.OK, EmptyListing) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x42]) });
        var policy = new OutboundHostPolicy(Options.Create(new EmailOptions { AllowPrivateNetworkHosts = true }), new FakeHostResolver(new() { ["s3.example.test"] = ["10.0.0.5"] }));
        var provider = new S3StorageProvider(new S3ClientFactory(Options.Create(new StorageOptions()), new StubHttpClientFactory(handler)), policy, NullLogger<S3StorageProvider>.Instance);

        Assert.True((await provider.TestConnectionAsync(Config())).Success);
    }

    [Theory]
    [InlineData("{}", "incomplete")]
    [InlineData("""{"bucket":"media-bucket"}""", "accessKeyId is required")]
    public async Task IncompleteSettings_AreReported_BeforeAnyRequest(string settings, string expected)
    {
        var (provider, handler) = Make();

        var outcome = await provider.TestConnectionAsync(Config(settings));

        Assert.False(outcome.Success);
        Assert.Contains(expected, outcome.Message);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task AMissingSecret_IsReported_BeforeAnyRequest()
    {
        var (provider, handler) = Make();

        var outcome = await provider.TestConnectionAsync(Config(secret: null));

        Assert.False(outcome.Success);
        Assert.Contains("secret access key is missing", outcome.Message);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task AWriteThatS3Refuses_ThrowsASafeMessage()
    {
        var (provider, _) = Make(_ => S3Error(HttpStatusCode.Forbidden, "AccessDenied"));

        var error = await Assert.ThrowsAsync<StorageProviderException>(() => provider.PutAsync(Config(), "k/a", new MemoryStream([1]), "x"));

        Assert.DoesNotContain(SecretKey, error.Message);
        Assert.Contains("write the file", error.Message);
    }

    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
