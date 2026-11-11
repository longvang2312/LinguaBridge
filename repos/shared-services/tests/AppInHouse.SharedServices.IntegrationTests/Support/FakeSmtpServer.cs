using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AppInHouse.SharedServices.IntegrationTests.Support;

/// <summary>
/// A small SMTP server on the loopback address, just enough for MailKit to log in and hand over a message (EHLO, AUTH PLAIN, MAIL, RCPT,
/// DATA, QUIT). It records the logins and the messages it received, so a test can see what the service really put on the wire.
/// </summary>
public sealed class FakeSmtpServer : IAsyncDisposable
{
    public sealed record Mail(string From, string To, string Data);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<Mail> _mails = new();
    private readonly ConcurrentQueue<(string User, string Password)> _logins = new();
    private Task? _loop;

    /// <summary>When set, a login with anything else is refused (535). Null accepts any login.</summary>
    public (string User, string Password)? Accept { get; set; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public IReadOnlyList<Mail> Mails => [.. _mails];

    public IReadOnlyList<(string User, string Password)> Logins => [.. _logins];

    public FakeSmtpServer Start()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoop);
        return this;
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };

        string from = string.Empty, to = string.Empty;
        try
        {
            await writer.WriteLineAsync("220 fake.test ESMTP ready");
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                var command = line.Split(' ', 2)[0].ToUpperInvariant();
                switch (command)
                {
                    case "EHLO":
                        await writer.WriteLineAsync("250-fake.test");
                        await writer.WriteLineAsync("250-AUTH PLAIN");
                        await writer.WriteLineAsync("250-8BITMIME");
                        await writer.WriteLineAsync("250 SIZE 10485760");
                        break;
                    case "HELO":
                        await writer.WriteLineAsync("250 fake.test");
                        break;
                    case "AUTH":
                        var encoded = line.Length > 11 ? line[11..].Trim() : null;
                        if (encoded is null)
                        {
                            await writer.WriteLineAsync("334 ");
                            encoded = (await reader.ReadLineAsync(_stop.Token))?.Trim();
                        }

                        var parts = Encoding.UTF8.GetString(Convert.FromBase64String(encoded ?? string.Empty)).Split('\0');
                        var (user, password) = (parts.ElementAtOrDefault(1) ?? string.Empty, parts.ElementAtOrDefault(2) ?? string.Empty);
                        _logins.Enqueue((user, password));
                        if (Accept is { } expected && expected != (user, password)) await writer.WriteLineAsync("535 5.7.8 Authentication credentials invalid");
                        else await writer.WriteLineAsync("235 2.7.0 Authentication successful");
                        break;
                    case "MAIL":
                        from = Between(line);
                        await writer.WriteLineAsync("250 2.1.0 OK");
                        break;
                    case "RCPT":
                        to = Between(line);
                        await writer.WriteLineAsync("250 2.1.5 OK");
                        break;
                    case "DATA":
                        await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                        var data = new StringBuilder();
                        while (await reader.ReadLineAsync(_stop.Token) is { } dataLine && dataLine != ".") data.AppendLine(dataLine);
                        _mails.Enqueue(new Mail(from, to, data.ToString()));
                        await writer.WriteLineAsync("250 2.0.0 OK queued as FAKE123");
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 2.0.0 Bye");
                        return;
                    default:
                        await writer.WriteLineAsync("250 OK");
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException or FormatException)
        {
            // The client went away, or the test is over.
        }
    }

    private static string Between(string line)
    {
        var start = line.IndexOf('<');
        var end = line.IndexOf('>');
        return start >= 0 && end > start ? line[(start + 1)..end] : string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        if (_loop is not null) await Task.WhenAny(_loop, Task.Delay(500));
        _stop.Dispose();
    }
}

/// <summary>Stands in for api.sendgrid.com: records every request and answers as told (202 by default).</summary>
public sealed class RecordingHandler : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.Accepted) { Headers = { { "X-Message-Id", "sg-integration-1" } } };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls.Add((request, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
        return Respond(request);
    }
}

/// <summary>Secrets of the shape each chat provider expects, built from pieces so no real-looking token or webhook address sits in a source file.</summary>
public static class FakeChatSecrets
{
    public static string TelegramToken(string suffix = "0123456789") => "123456" + ":" + "fake-token-for-tests-" + suffix;
    public static string DiscordWebhook(string suffix = "0123456789") => "https://discord.com/api/webhooks/" + "123456789012345678" + "/" + "fake-webhook-token-for-tests-" + suffix;
    public static string SlackWebhook(string suffix = "0123") => "https://hooks.slack.com/services/" + "T0FAKE000" + "/" + "B0FAKE000" + "/" + "fakewebhooktokenfortests" + suffix;
}
