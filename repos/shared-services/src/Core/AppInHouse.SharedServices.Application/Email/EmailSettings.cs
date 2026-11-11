using System.Net;
using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.Application.Email;

public enum SmtpSecurity
{
    /// <summary>Plain text, no encryption. Only for a relay on a trusted network.</summary>
    None,

    /// <summary>Let the library choose from the port (465: SSL on connect; otherwise STARTTLS when offered).</summary>
    Auto,

    SslOnConnect,
    StartTls,
}

/// <summary>The non-secret part of an email configuration (the keys of <c>SettingsJson</c>). The password or API key is separate.</summary>
public sealed record EmailSettings(
    string? Host, int Port, SmtpSecurity Security, string? Username, string? SenderEmail, string? SenderName)
{
    public const int DefaultPort = 587;
}

/// <summary>
/// Reads and checks the settings of an email configuration:
/// <c>host, port, useSsl (None | Auto | SslOnConnect | StartTls), username, senderEmail, senderName</c> for SMTP and
/// <c>senderEmail, senderName</c> for SendGrid. Saving a draft only checks the shape of what is there; sending or testing needs it complete.
/// </summary>
public static partial class EmailSettingsReader
{
    [GeneratedRegex(@"^(?=.{1,253}$)[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex HostName();

    public static EmailSettings Read(JsonElement settings)
    {
        string? Text(string name) =>
            settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim()
                : null;

        var port = EmailSettings.DefaultPort;
        if (settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("port", out var portValue) && portValue.ValueKind == JsonValueKind.Number && portValue.TryGetInt32(out var parsed))
            port = parsed;

        var security = Enum.TryParse<SmtpSecurity>(Text("useSsl"), ignoreCase: true, out var ssl) ? ssl : SmtpSecurity.Auto;
        return new EmailSettings(Text("host"), port, security, Text("username"), Text("senderEmail"), Text("senderName"));
    }

    /// <summary>The problems with these settings; empty when they are fine. With <paramref name="complete"/> the required values must be present.</summary>
    public static IReadOnlyList<string> Validate(ProviderType provider, JsonElement settings, bool complete)
    {
        var problems = new List<string>();
        if (settings.ValueKind != JsonValueKind.Object) return ["The settings must be a JSON object."];

        bool Has(string name) => settings.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;

        if (Has("port") && !(settings.GetProperty("port").ValueKind == JsonValueKind.Number && settings.GetProperty("port").TryGetInt32(out var port) && port is >= 1 and <= 65535))
            problems.Add("port must be a number from 1 to 65535.");
        if (Has("useSsl") && !(settings.GetProperty("useSsl").ValueKind == JsonValueKind.String
                               && Enum.TryParse<SmtpSecurity>(settings.GetProperty("useSsl").GetString(), ignoreCase: true, out _)))
            problems.Add("useSsl must be one of None, Auto, SslOnConnect, StartTls.");

        var read = Read(settings);
        if (read.Host is { Length: > 0 } host && !IsHost(host)) problems.Add("host must be a host name or an IP address, without a scheme, port or path.");
        if (read.SenderEmail is { Length: > 0 } sender && !IsAddress(sender)) problems.Add("senderEmail must be a single email address.");
        if (read.SenderName is { Length: > 0 } name && (name.Length > 100 || name.Any(char.IsControl))) problems.Add("senderName must be at most 100 characters, on one line.");
        if (read.Username is { Length: > 0 } user && (user.Length > 254 || user.Any(char.IsControl))) problems.Add("username is not valid.");

        if (complete)
        {
            if (provider == ProviderType.Smtp && string.IsNullOrWhiteSpace(read.Host)) problems.Add("host is required for SMTP.");
            if (provider is ProviderType.Smtp or ProviderType.SendGrid or ProviderType.Brevo && string.IsNullOrWhiteSpace(read.SenderEmail))
                problems.Add("senderEmail is required.");
        }

        return problems;
    }

    /// <summary>One plain address (no display name, no list, no line breaks), as the sender and the recipients must be.</summary>
    public static bool IsAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 254 || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is ',' or ';' or '<' or '>'))
            return false;
        return MailAddress.TryCreate(value, out var address) && string.Equals(address.Address, value, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsHost(string value) => IPAddress.TryParse(value.Trim('[', ']'), out _) || HostName().IsMatch(value);
}
