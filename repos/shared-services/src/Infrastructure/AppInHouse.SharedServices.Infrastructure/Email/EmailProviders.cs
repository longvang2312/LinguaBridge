using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Domain;
using Microsoft.Extensions.Logging;

namespace AppInHouse.SharedServices.Infrastructure.Email;

/// <summary>Where the email providers get their host settings from (section <c>Email</c>). None of it is stored per project.</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>
    /// Whether an SMTP host may be a loopback or private-network address (a local Mailpit, an internal relay). Off by default: with it
    /// off, "send test" cannot be used to probe the service's own network. Link-local and metadata addresses are refused either way.
    /// </summary>
    public bool AllowPrivateNetworkHosts { get; set; }

    public int SmtpTimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// The SendGrid API address. A host setting, deliberately not a per-configuration one: if it were, someone who can save a
    /// configuration could point it at a server of their own and receive the API key.
    /// </summary>
    public string SendGridBaseUrl { get; set; } = "https://api.sendgrid.com";

    public int SendGridTimeoutSeconds { get; set; } = 20;
}

/// <summary>The part every email provider shares: the test message, and "send test" as a timed send.</summary>
public abstract class EmailProviderBase : IEmailProvider
{
    public abstract ProviderType Type { get; }

    public abstract Task<EmailSendResult> SendAsync(EmailMessage message, ChannelRuntimeConfiguration config, CancellationToken ct = default);

    public async Task<EmailTestResult> TestConnectionAsync(ChannelRuntimeConfiguration config, string testRecipient, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await SendAsync(CreateTestMessage(config, testRecipient), config, ct);
        stopwatch.Stop();

        var latency = (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue);
        return result.Success
            ? new EmailTestResult(true, $"Test email accepted by the {Type} provider.", latency)
            : new EmailTestResult(false, ErrorText.Clean(result.Error, config.Secret), latency);
    }

    /// <summary>A short message that says what it is and which provider sent it. It never contains the configuration's secret.</summary>
    protected static EmailMessage CreateTestMessage(ChannelRuntimeConfiguration config, string recipient)
    {
        var provider = config.ProviderType.ToString();
        return new EmailMessage(
            recipient,
            "[AppInHouse] Email test",
            $"<p>This is a test email from <strong>AppInHouse Shared Services</strong>.</p><p>Provider: {System.Net.WebUtility.HtmlEncode(provider)}</p><p>If you can read this, the email configuration works.</p>",
            $"This is a test email from AppInHouse Shared Services.\nProvider: {provider}\nIf you can read this, the email configuration works.");
    }

    /// <summary>Settings must be complete before anything is sent; a problem is a result, not an exception.</summary>
    protected static EmailSendResult? CheckSettings(ChannelRuntimeConfiguration config)
    {
        var problems = EmailSettingsReader.Validate(config.ProviderType, config.Settings, complete: true);
        return problems.Count == 0 ? null : new EmailSendResult(false, Error: "The email settings are not complete: " + string.Join(" ", problems));
    }
}

/// <summary>
/// For development and tests with no mail server and no internet: keeps what would have been sent in memory
/// (<see cref="SentMessages"/>) and logs who it was for. The body is not logged.
/// </summary>
public sealed class MockEmailProvider(ILogger<MockEmailProvider> logger) : EmailProviderBase
{
    private const int MaxKept = 1000;
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public override ProviderType Type => ProviderType.Mock;

    /// <summary>The last emails "sent" (at most 1000, oldest dropped first).</summary>
    public IReadOnlyList<EmailMessage> SentMessages => [.. _sent];

    public void Clear() => _sent.Clear();

    public override Task<EmailSendResult> SendAsync(EmailMessage message, ChannelRuntimeConfiguration config, CancellationToken ct = default)
    {
        _sent.Enqueue(message);
        while (_sent.Count > MaxKept) _sent.TryDequeue(out _);

        logger.LogInformation("Mock email to {Recipient} (project {Project}): {Subject}", message.To, config.ProjectCode, message.Subject);
        return Task.FromResult(new EmailSendResult(true, ProviderMessageId: $"mock-{Guid.NewGuid():N}", ProviderResponse: "Stored by the mock provider"));
    }
}

/// <summary>SendGrid REST API v3 (<c>POST /v3/mail/send</c>). The API key is the configuration's decrypted secret.</summary>
public sealed class SendGridEmailProvider(HttpClient http, ILogger<SendGridEmailProvider> logger) : EmailProviderBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public override ProviderType Type => ProviderType.SendGrid;

    public override async Task<EmailSendResult> SendAsync(EmailMessage message, ChannelRuntimeConfiguration config, CancellationToken ct = default)
    {
        if (CheckSettings(config) is { } incomplete) return incomplete;
        if (string.IsNullOrWhiteSpace(config.Secret)) return new EmailSendResult(false, Error: "The SendGrid API key is missing.");

        var settings = EmailSettingsReader.Read(config.Settings);
        var content = new List<object>();
        if (!string.IsNullOrWhiteSpace(message.TextBody)) content.Add(new { type = "text/plain", value = message.TextBody });   // SendGrid wants text first
        if (!string.IsNullOrWhiteSpace(message.HtmlBody)) content.Add(new { type = "text/html", value = message.HtmlBody });

        var payload = new
        {
            personalizations = new[] { new { to = new[] { new { email = message.To } } } },
            from = new { email = settings.SenderEmail, name = settings.SenderName },
            subject = message.Subject,
            content,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "v3/mail/send") { Content = JsonContent.Create(payload, options: Json) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Secret);

        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                var id = response.Headers.TryGetValues("X-Message-Id", out var values) ? values.FirstOrDefault() : null;
                return new EmailSendResult(true, id, ProviderResponse: $"SendGrid accepted ({(int)response.StatusCode})" + (id is null ? string.Empty : $" id {id}"));
            }

            return new EmailSendResult(false, Error: await DescribeFailureAsync(response, config.Secret, ct));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new EmailSendResult(false, Error: "SendGrid did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Could not reach SendGrid.");
            return new EmailSendResult(false, Error: "Could not reach SendGrid.");
        }
    }

    private static async Task<string> DescribeFailureAsync(HttpResponseMessage response, string? secret, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) return "SendGrid rejected the API key (401).";
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden) return "SendGrid refused: the API key may not send mail, or the sender is not verified (403).";

        string? detail = null;
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0
                && errors[0].TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                detail = message.GetString();
        }
        catch (JsonException)
        {
            // Not the JSON error format: the status code alone has to do.
        }

        return ErrorText.Clean($"SendGrid answered {status}" + (detail is null ? "." : $": {detail}"), secret);
    }
}
