<#
.SYNOPSIS
    Serves the LinguaBridge web client (repos/linguabridge-web) on http://localhost:3000 and opens it in the browser.

.DESCRIPTION
    A small local web server, with nothing to install (Windows PowerShell 5.1 or later). It does three things:

      1. Serves the files of repos/linguabridge-web.
      2. Serves callback.html at /callback. The STS only knows http://localhost:3000/callback as the redirect address of the
         linguabridge-app client, so the sign-in page is reached under that name.
      3. Forwards two prefixes to the backend, because neither the STS nor the API sends CORS headers and a page on
         localhost:3000 therefore cannot call them directly:
             /sts/*  ->  the STS        (token endpoint)        e.g. /sts/connect/token -> {StsUrl}/connect/token
             /lb/*   ->  the LinguaBridge API                   e.g. /lb/api/v1/me      -> {ApiUrl}/api/v1/me
         Only these two fixed targets are reachable; nothing else is proxied and cookies are not forwarded.

    The server listens on the loopback address only. Stop it with Ctrl+C.
    Start the backend first: ./scripts/docker-all-up.ps1

.PARAMETER Port
    Port to listen on. The STS knows http://localhost:3000 (and :5173) for this client, so keep 3000 unless the STS is
    configured with other redirect addresses.

.PARAMETER ApiUrl
    The LinguaBridge API. Defaults to http://localhost:5020.

.PARAMETER StsUrl
    The STS, as the browser knows it. Defaults to http://localhost:5001: tokens are only accepted by the API
    when they were issued through this address.

.PARAMETER NoBrowser
    Do not open the browser.

.EXAMPLE
    ./scripts/start-web.ps1
#>
[CmdletBinding()]
param(
    [int] $Port = 3000,
    [string] $ApiUrl = 'http://localhost:5020',
    [string] $StsUrl = 'http://localhost:5001',
    [switch] $NoBrowser
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$webRoot = Join-Path $root 'repos\linguabridge-web'
if (-not (Test-Path (Join-Path $webRoot 'index.html'))) { throw "Web client not found at $webRoot." }

$owner = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue | Select-Object -First 1
if ($owner) {
    $process = Get-Process -Id $owner.OwningProcess -ErrorAction SilentlyContinue
    throw "Port $Port is already in use by process $($owner.OwningProcess) ($($process.ProcessName)); free it or pass -Port."
}

Add-Type -ReferencedAssemblies 'System.Net.Http' -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

public sealed class LinguaBridgeWebServer
{
    private static readonly Dictionary<string, string> MimeTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { ".html", "text/html; charset=utf-8" }, { ".css", "text/css; charset=utf-8" },
        { ".js", "text/javascript; charset=utf-8" }, { ".mjs", "text/javascript; charset=utf-8" },
        { ".json", "application/json; charset=utf-8" }, { ".svg", "image/svg+xml" }, { ".png", "image/png" },
        { ".jpg", "image/jpeg" }, { ".ico", "image/x-icon" }, { ".txt", "text/plain; charset=utf-8" },
        { ".woff2", "font/woff2" }
    };

    // Headers that belong to one connection or that the proxy must decide itself; everything else the browser sent is passed on.
    private static readonly HashSet<string> NotForwarded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Content-Length", "Cookie", "Origin", "Referer", "Accept-Encoding", "Transfer-Encoding", "Expect", "Keep-Alive"
    };

    private readonly HttpListener listener = new HttpListener();
    private readonly string webRoot;
    private readonly string apiUrl;
    private readonly string stsUrl;
    private readonly HttpClient upstream;
    private readonly ConcurrentQueue<string> messages = new ConcurrentQueue<string>();

    public LinguaBridgeWebServer(int port, string webRoot, string apiUrl, string stsUrl)
    {
        this.webRoot = Path.GetFullPath(webRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        this.apiUrl = apiUrl.TrimEnd('/');
        this.stsUrl = stsUrl.TrimEnd('/');
        listener.Prefixes.Add("http://localhost:" + port + "/");
        listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
        upstream = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(120) };
    }

    /// <summary>Request log lines, oldest first (written on worker threads, read by the script).</summary>
    public bool TryDequeueLog(out string line)
    {
        return messages.TryDequeue(out line);
    }

    public void Start()
    {
        listener.Start();
        Task.Run(new Func<Task>(AcceptLoop));
    }

    public void Stop()
    {
        listener.Close();
        upstream.Dispose();
    }

    private async Task AcceptLoop()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception) { return; }
            var ignored = Task.Run(() => Handle(context));
        }
    }

    private async Task Handle(HttpListenerContext context)
    {
        var started = DateTime.Now;
        string path = context.Request.Url.AbsolutePath;
        try
        {
            if (path.StartsWith("/sts/", StringComparison.Ordinal))
                await Proxy(context, stsUrl, path.Substring("/sts".Length));
            else if (path.StartsWith("/lb/", StringComparison.Ordinal))
                await Proxy(context, apiUrl, path.Substring("/lb".Length));
            else
                ServeFile(context, path);
        }
        catch (Exception ex)
        {
            try
            {
                if (context.Response.OutputStream.CanWrite && !context.Response.SendChunked)
                    WriteText(context, 502, "Bad gateway: " + ex.Message);
            }
            catch (Exception) { }
        }
        finally
        {
            try { context.Response.Close(); } catch (Exception) { }
            messages.Enqueue(string.Format("{0:HH:mm:ss} {1} {2} -> {3} ({4} ms)", started, context.Request.HttpMethod, path,
                context.Response.StatusCode, (int)(DateTime.Now - started).TotalMilliseconds));
        }
    }

    private void ServeFile(HttpListenerContext context, string path)
    {
        if (context.Request.HttpMethod != "GET" && context.Request.HttpMethod != "HEAD")
        {
            WriteText(context, 405, "Method not allowed");
            return;
        }

        if (path == "/") path = "/index.html";
        else if (path == "/callback") path = "/callback.html";   // the redirect address the STS has on record

        string relative = Uri.UnescapeDataString(path).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(webRoot, relative));
        if (!full.StartsWith(webRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))   // also stops ../ escapes
        {
            WriteText(context, 404, "Not found");
            return;
        }

        string type;
        if (!MimeTypes.TryGetValue(Path.GetExtension(full), out type)) type = "application/octet-stream";
        byte[] bytes = File.ReadAllBytes(full);
        context.Response.StatusCode = 200;
        context.Response.ContentType = type;
        context.Response.Headers["Cache-Control"] = "no-store";   // development: always the files on disk
        context.Response.ContentLength64 = bytes.Length;
        if (context.Request.HttpMethod == "GET") context.Response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    private async Task Proxy(HttpListenerContext context, string target, string pathAfterPrefix)
    {
        var request = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod), target + pathAfterPrefix + context.Request.Url.Query);

        foreach (string name in context.Request.Headers.AllKeys)
        {
            if (NotForwarded.Contains(name)) continue;
            string value = context.Request.Headers[name];
            if (!request.Headers.TryAddWithoutValidation(name, value) && request.Content != null)
                request.Content.Headers.TryAddWithoutValidation(name, value);
        }

        if (context.Request.HasEntityBody)
        {
            var buffer = new MemoryStream();
            context.Request.InputStream.CopyTo(buffer);
            request.Content = new ByteArrayContent(buffer.ToArray());
            if (context.Request.ContentType != null)
                request.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
        }

        using (var response = await upstream.SendAsync(request, HttpCompletionOption.ResponseContentRead))
        {
            byte[] body = await response.Content.ReadAsByteArrayAsync();
            context.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType != null)
                context.Response.ContentType = response.Content.Headers.ContentType.ToString();
            foreach (var header in response.Headers)
            {
                string name = header.Key;
                if (name == "Transfer-Encoding" || name == "Connection" || name == "Keep-Alive" || name == "Date" || name == "Server") continue;
                // AddHeader (not the indexer) because HttpListener refuses to set some headers, WWW-Authenticate among them, any other way.
                try { context.Response.AddHeader(name, string.Join(", ", header.Value)); }
                catch (ArgumentException) { }
            }
            context.Response.ContentLength64 = body.Length;
            context.Response.OutputStream.Write(body, 0, body.Length);
        }
    }

    private static void WriteText(HttpListenerContext context, int status, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
    }
}
'@

$server = New-Object LinguaBridgeWebServer($Port, $webRoot, $ApiUrl, $StsUrl)
$server.Start()

$url = "http://localhost:$Port"
Write-Host "LinguaBridge web client: $url" -ForegroundColor Green
Write-Host "  files    $webRoot"
Write-Host "  /lb/*    -> $ApiUrl"
Write-Host "  /sts/*   -> $StsUrl"
Write-Host 'Press Ctrl+C to stop.'
if (-not $NoBrowser) { Start-Process $url }

try {
    while ($true) {
        $line = $null
        while ($server.TryDequeueLog([ref] $line)) { Write-Host $line }
        Start-Sleep -Milliseconds 200
    }
}
finally {
    $server.Stop()
    Write-Host 'Stopped.'
}
