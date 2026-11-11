using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

// Development host of the web client. The STS knows http://localhost:3000/callback as the redirect address of the
// "linguabridge-app" client, so keep port 3000 (see launchSettings.json).
//
//   /sts/*  ->  Web:StsUrl   (the token endpoint; the STS sends no CORS headers)
//   /lb/*   ->  Web:ApiUrl   (the LinguaBridge API; no CORS headers either)
//   /callback  ->  callback.html (the address the STS has on record)
//   anything else: the files of the parent folder (repos/linguabridge-web)
var builder = WebApplication.CreateBuilder(args);

var webRoot = Path.GetFullPath(builder.Configuration["Web:Root"] is { Length: > 0 } configured
    ? Path.Combine(builder.Environment.ContentRootPath, configured)
    : Path.Combine(builder.Environment.ContentRootPath, ".."));
if (!File.Exists(Path.Combine(webRoot, "index.html")))
    throw new InvalidOperationException($"Web client not found at '{webRoot}' (Web:Root).");

var stsUrl = (builder.Configuration["Web:StsUrl"] ?? "http://localhost:5001").TrimEnd('/');
var apiUrl = (builder.Configuration["Web:ApiUrl"] ?? "http://localhost:5020").TrimEnd('/');

// No automatic redirects or cookies: the browser must see the upstream's answer as it is, and must not share cookies with it.
builder.Services.AddHttpClient("upstream")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(120));

var app = builder.Build();

// Headers that belong to one connection, or that the proxy must decide itself; everything else is passed on.
var notForwarded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "Host", "Connection", "Content-Length", "Cookie", "Origin", "Referer", "Accept-Encoding", "Transfer-Encoding", "Expect", "Keep-Alive",
};
var notReturned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Transfer-Encoding", "Connection", "Keep-Alive", "Date", "Server" };

async Task Proxy(HttpContext context, string target, string path)
{
    var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target + path + context.Request.QueryString);
    var hasBody = context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey(HeaderNames.TransferEncoding);
    if (hasBody) request.Content = new StreamContent(context.Request.Body);

    foreach (var (name, values) in context.Request.Headers)
    {
        if (notForwarded.Contains(name)) continue;
        if (!request.Headers.TryAddWithoutValidation(name, values.ToArray()))
            request.Content?.Headers.TryAddWithoutValidation(name, values.ToArray());
    }

    var client = context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient("upstream");
    try
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Headers.Concat(response.Content.Headers))
            if (!notReturned.Contains(header.Key)) context.Response.Headers[header.Key] = header.Value.ToArray();
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
    catch (HttpRequestException ex)
    {
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        await context.Response.WriteAsync($"Bad gateway: {ex.Message} (is {target} running?)");
    }
}

app.Map("/sts/{**path}", (HttpContext context, string? path) => Proxy(context, stsUrl, "/" + path));
app.Map("/lb/{**path}", (HttpContext context, string? path) => Proxy(context, apiUrl, "/" + path));

// "/" is the home page and "/callback" is callback.html; the other pages are reached by their file name.
app.Use((context, next) =>
{
    if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
    {
        var path = context.Request.Path.Value;
        if (path == "/") context.Request.Path = "/index.html";
        else if (path == "/callback") context.Request.Path = "/callback.html";
    }
    return next();
});

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(webRoot),
    ServeUnknownFileTypes = false,
    // Development: always the files on disk.
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-store",
});

app.Logger.LogInformation("Web client {Root}: /lb -> {Api}, /sts -> {Sts}", webRoot, apiUrl, stsUrl);
app.Run();
