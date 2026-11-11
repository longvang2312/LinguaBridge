using System.Net;
using System.Net.Sockets;
using AppInHouse.SharedServices.Application.Configurations;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Domain;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace AppInHouse.SharedServices.Infrastructure.Email;

/// <summary>Where and how to connect to an SMTP server, and the login (the password is the configuration's decrypted secret).</summary>
public sealed record SmtpEndpoint(string Host, int Port, SecureSocketOptions Security, string? Username, string? Password);

/// <summary>Delivers one MIME message to an SMTP server. Separate from the provider so the provider's decisions can be tested without a network.</summary>
public interface ISmtpSender
{
    Task SendAsync(SmtpEndpoint endpoint, MimeMessage message, CancellationToken cancellationToken);
}

public sealed class MailKitSmtpSender : ISmtpSender
{
    public async Task SendAsync(SmtpEndpoint endpoint, MimeMessage message, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient();
        await client.ConnectAsync(endpoint.Host, endpoint.Port, endpoint.Security, cancellationToken);
        try
        {
            if (!string.IsNullOrEmpty(endpoint.Username))
                await client.AuthenticateAsync(endpoint.Username, endpoint.Password ?? string.Empty, cancellationToken);
            await client.SendAsync(message, cancellationToken);
        }
        finally
        {
            if (client.IsConnected) await client.DisconnectAsync(quit: true, CancellationToken.None);
        }
    }
}

/// <summary>Turns a host name into addresses. Separate so the outbound policy can be tested without a network.</summary>
public interface IHostAddressResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class DnsHostAddressResolver : IHostAddressResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>
/// Refuses hosts the service must not connect to, for email (SMTP) and chat (webhooks) alike: link-local, metadata, multicast and
/// unspecified addresses always, loopback and private networks unless <see cref="EmailOptions.AllowPrivateNetworkHosts"/> is on.
/// The name is resolved here and again when connecting, so a name that changes its answer in between is not caught.
/// </summary>
public sealed class OutboundHostPolicy
{
    private readonly IHostAddressResolver _resolver;
    private readonly Func<bool> _allowPrivateNetworkHosts;

    /// <summary>The policy of the email and chat providers: private networks are allowed only when <c>Email:AllowPrivateNetworkHosts</c> says so.</summary>
    public OutboundHostPolicy(IOptions<EmailOptions> options, IHostAddressResolver? resolver = null)
        : this(() => options.Value.AllowPrivateNetworkHosts, resolver)
    {
    }

    /// <summary>A policy with its own switch for private networks, so each kind of outbound call (mail, storage) is allowed to reach them independently.</summary>
    public OutboundHostPolicy(Func<bool> allowPrivateNetworkHosts, IHostAddressResolver? resolver = null)
    {
        _allowPrivateNetworkHosts = allowPrivateNetworkHosts;
        _resolver = resolver ?? new DnsHostAddressResolver();
    }

    public async Task<string?> CheckAsync(string host, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await _resolver.ResolveAsync(host, cancellationToken);
        }
        catch (SocketException)
        {
            return $"The host name {host} cannot be resolved.";
        }

        foreach (var address in addresses)
        {
            var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            if (IsAlwaysBlocked(ip)) return $"The address of {host} is not allowed (link-local, multicast or unspecified).";
            if (!_allowPrivateNetworkHosts() && IsPrivate(ip)) return $"{host} is on a private or loopback network, which this service is set not to contact.";
        }

        return null;
    }

    private static bool IsAlwaysBlocked(IPAddress ip)
    {
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6Multicast || ip.IsIPv6LinkLocal) return true;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 169 && b[1] == 254                  // link-local, which includes the cloud metadata address 169.254.169.254
               || b[0] >= 224;                              // multicast and reserved
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6SiteLocal) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;   // fc00::/7
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || b[0] == 100 && b[1] is >= 64 and <= 127;
    }
}

/// <summary>
/// SMTP through MailKit/MimeKit. Settings: <c>host, port, useSsl (None | Auto | SslOnConnect | StartTls), username, senderEmail, senderName</c>;
/// the password is the configuration's secret, decrypted only for this call. Every failure becomes a short message with no secret in it.
/// </summary>
public sealed class MailKitSmtpEmailProvider(
    ISmtpSender sender, OutboundHostPolicy hostPolicy, IOptions<EmailOptions> options, ILogger<MailKitSmtpEmailProvider> logger) : EmailProviderBase
{
    public override ProviderType Type => ProviderType.Smtp;

    public override async Task<EmailSendResult> SendAsync(EmailMessage message, ChannelRuntimeConfiguration config, CancellationToken ct = default)
    {
        if (CheckSettings(config) is { } incomplete) return incomplete;

        var settings = EmailSettingsReader.Read(config.Settings);
        if (!string.IsNullOrEmpty(settings.Username) && string.IsNullOrEmpty(config.Secret))
            return new EmailSendResult(false, Error: "The SMTP username is set but there is no password.");

        var blocked = await hostPolicy.CheckAsync(settings.Host!, ct);
        if (blocked is not null) return new EmailSendResult(false, Error: blocked);

        var endpoint = new SmtpEndpoint(settings.Host!, settings.Port, ToSocketOptions(settings.Security), settings.Username, config.Secret);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.SmtpTimeoutSeconds));

        try
        {
            await sender.SendAsync(endpoint, Build(message, settings), timeout.Token);
            return new EmailSendResult(true, ProviderResponse: $"SMTP server {settings.Host}:{settings.Port} accepted the message");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new EmailSendResult(false, Error: $"The SMTP server {settings.Host}:{settings.Port} did not answer in time.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "SMTP send to {Host}:{Port} failed.", settings.Host, settings.Port);
            return new EmailSendResult(false, Error: Describe(ex, settings, config.Secret));
        }
    }

    public static SecureSocketOptions ToSocketOptions(SmtpSecurity security) => security switch
    {
        SmtpSecurity.None => SecureSocketOptions.None,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.Auto,
    };

    public static MimeMessage Build(EmailMessage message, EmailSettings settings)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(settings.SenderName ?? string.Empty, settings.SenderEmail!));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;

        var body = new BodyBuilder();
        if (!string.IsNullOrWhiteSpace(message.HtmlBody)) body.HtmlBody = message.HtmlBody;
        if (!string.IsNullOrWhiteSpace(message.TextBody)) body.TextBody = message.TextBody;
        mime.Body = body.ToMessageBody();
        return mime;
    }

    // What went wrong, in a sentence a person can act on. The raw exception text can echo server replies; it is only ever cleaned, never trusted.
    private static string Describe(Exception ex, EmailSettings settings, string? secret) => ex switch
    {
        AuthenticationException => "The SMTP server rejected the login (check the username and password).",
        SslHandshakeException => "The secure connection to the SMTP server failed (check the SSL setting and the port).",
        SmtpCommandException command => ErrorText.Clean($"The SMTP server refused ({(int)command.StatusCode} {command.ErrorCode}): {command.Message}", secret),
        SmtpProtocolException => "The SMTP server answered in a way that was not understood (is the port an SMTP port?).",
        ServiceNotConnectedException or ServiceNotAuthenticatedException => "The SMTP connection was not ready.",
        SocketException or IOException => $"Could not connect to {settings.Host}:{settings.Port}.",
        _ => ErrorText.Clean(ex.Message, secret),
    };
}
