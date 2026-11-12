using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Application.Webhooks;
using AppInHouse.SharedServices.Infrastructure.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AppInHouse.SharedServices.Infrastructure.Webhooks;

/// <summary>Host settings, section <c>Webhooks</c>. The secrets of the inbound sources are in <c>Webhooks:Inbound:{source}:Secret</c> (<see cref="ConfigurationInboundSecretProvider"/>).</summary>
public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";

    /// <summary>
    /// Whether outbound webhooks may go to a private or loopback address. Off by default: a subscription is made through the API, and without
    /// this switch it could be used to make the service call machines on its own network. Switch it on where the applications are on the
    /// same private network as the service (as they are on the LAN server).
    /// </summary>
    public bool AllowPrivateNetworkHosts { get; set; }

    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// Posts a webhook with an <see cref="HttpClient"/> that follows no redirect (an application cannot send the service somewhere else), has
/// a short time limit and logs nothing (the address may carry a token). The reply body is never read: only its status matters.
/// </summary>
public sealed class HttpWebhookSender : IWebhookSender
{
    private readonly HttpClient _http;
    private readonly OutboundHostPolicy _policy;

    public HttpWebhookSender(HttpClient http, IOptions<WebhookOptions> options, IHostAddressResolver resolver)
    {
        _http = http;
        _policy = new OutboundHostPolicy(() => options.Value.AllowPrivateNetworkHosts, resolver);
    }

    public async Task<string?> CheckTargetAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "The target must be an http or https address.";
        return await _policy.CheckAsync(uri.Host, cancellationToken);
    }

    public async Task<WebhookHttpResult> PostAsync(string url, string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AppInHouse-SharedServices-Webhooks", "1.0"));
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return new WebhookHttpResult((int)response.StatusCode, null);
        }
        catch (HttpRequestException ex)
        {
            return new WebhookHttpResult(null, ErrorText.Clean(ex.Message));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WebhookHttpResult(null, "The application did not answer in time.");
        }
    }
}

public sealed class ConfigurationInboundSecretProvider(IConfiguration configuration) : IInboundSecretProvider
{
    public string? GetSecret(string source) => configuration[$"{WebhookOptions.SectionName}:Inbound:{source}:Secret"];
}
