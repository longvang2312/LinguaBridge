using System.Net;
using System.Text.RegularExpressions;
using AppInHouse.SharedServices.Application.Common;

namespace AppInHouse.SharedServices.Application.Email;

/// <summary>A built-in email: subject and bodies with <c>{{variable}}</c> placeholders.</summary>
public sealed record EmailTemplate(
    string Name, string Subject, string Html, string Text, IReadOnlyList<string> Required, IReadOnlyDictionary<string, string> Defaults);

public static class EmailTemplates
{
    public const string EmailVerification = nameof(EmailVerification);
    public const string PasswordReset = nameof(PasswordReset);
    public const string SystemNotification = nameof(SystemNotification);

    private static readonly IReadOnlyDictionary<string, string> CommonDefaults = new Dictionary<string, string>
    {
        ["app_name"] = "AppInHouse",
        ["expires_in_minutes"] = "15",
    };

    private static string Page(string title, string body) => $$"""
        <!DOCTYPE html>
        <html lang="vi"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>{{title}}</title></head>
        <body style="margin:0;background:#f4f6f9;font-family:Segoe UI,Arial,sans-serif;color:#1f2937;">
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr><td align="center" style="padding:24px 12px;">
        <table role="presentation" width="560" cellpadding="0" cellspacing="0" style="max-width:560px;background:#ffffff;border-radius:8px;overflow:hidden;">
        <tr><td style="background:#1f3a5f;color:#ffffff;padding:16px 24px;font-size:18px;font-weight:600;">{{"{{app_name}}"}}</td></tr>
        <tr><td style="padding:24px;font-size:15px;line-height:1.6;">
        {{body}}
        </td></tr>
        <tr><td style="padding:16px 24px;font-size:12px;color:#6b7280;border-top:1px solid #e5e7eb;">Email này được gửi tự động, vui lòng không trả lời.</td></tr>
        </table></td></tr></table></body></html>
        """;

    public static IReadOnlyList<EmailTemplate> All { get; } =
    [
        new(EmailVerification,
            "Xác thực tài khoản {{app_name}}",
            Page("Xác thực tài khoản", """
                <p>Xin chào {{name}},</p>
                <p>Mã xác thực tài khoản của bạn là:</p>
                <p style="font-size:28px;font-weight:700;letter-spacing:6px;margin:16px 0;">{{code}}</p>
                <p>Mã có hiệu lực trong {{expires_in_minutes}} phút.</p>
                {{#if link}}<p>Hoặc nhấn vào liên kết: <a href="{{link}}">{{link}}</a></p>{{/if}}
                <p>Nếu bạn không yêu cầu, hãy bỏ qua email này.</p>
                """),
            "Xin chào {{name}},\n\nMã xác thực tài khoản của bạn là: {{code}}\nMã có hiệu lực trong {{expires_in_minutes}} phút.\n{{#if link}}Hoặc mở liên kết: {{link}}\n{{/if}}\nNếu bạn không yêu cầu, hãy bỏ qua email này.",
            ["name", "code"], CommonDefaults),

        new(PasswordReset,
            "Khôi phục mật khẩu {{app_name}}",
            Page("Khôi phục mật khẩu", """
                <p>Xin chào {{name}},</p>
                <p>Chúng tôi nhận được yêu cầu đặt lại mật khẩu cho tài khoản của bạn.</p>
                <p><a href="{{link}}" style="display:inline-block;background:#3b82f6;color:#ffffff;text-decoration:none;padding:10px 20px;border-radius:6px;">Đặt lại mật khẩu</a></p>
                <p>Liên kết có hiệu lực trong {{expires_in_minutes}} phút. Nếu bạn không yêu cầu, hãy bỏ qua email này; mật khẩu hiện tại vẫn giữ nguyên.</p>
                """),
            "Xin chào {{name}},\n\nĐể đặt lại mật khẩu, mở liên kết: {{link}}\nLiên kết có hiệu lực trong {{expires_in_minutes}} phút.\nNếu bạn không yêu cầu, hãy bỏ qua email này.",
            ["name", "link"], CommonDefaults),

        new(SystemNotification,
            "[{{app_name}}] {{title}}",
            Page("Thông báo hệ thống", """
                <h2 style="margin:0 0 12px;font-size:18px;">{{title}}</h2>
                <p style="white-space:pre-line;">{{message}}</p>
                {{#if details}}<p style="background:#f3f4f6;border-radius:6px;padding:10px 12px;font-family:Consolas,monospace;font-size:13px;white-space:pre-wrap;">{{details}}</p>{{/if}}
                """),
            "{{title}}\n\n{{message}}\n{{#if details}}\n{{details}}\n{{/if}}",
            ["title", "message"], CommonDefaults),
    ];

    public static EmailTemplate? Find(string? name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The small template language: <c>{{name}}</c> is replaced by the variable's value (escaped when the target is HTML, so a value can
/// never inject markup), and <c>{{#if name}}...{{/if}}</c> keeps its content only when the variable is not empty. A placeholder
/// without a value becomes empty.
/// </summary>
public static partial class EmailTemplateEngine
{
    [GeneratedRegex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\{\{#if\s+([A-Za-z_][A-Za-z0-9_]*)\s*\}\}(.*?)\{\{/if\}\}", RegexOptions.Singleline)]
    private static partial Regex Conditional();

    public static string Render(string template, IReadOnlyDictionary<string, string> variables, bool htmlEncode, ICollection<string>? missing = null)
    {
        bool Present(string name) => variables.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value);

        // Conditional blocks first (they may contain placeholders), then the placeholders themselves.
        var withBlocks = Conditional().Replace(template, match => Present(match.Groups[1].Value) ? match.Groups[2].Value : string.Empty);

        return Placeholder().Replace(withBlocks, match =>
        {
            var name = match.Groups[1].Value;
            if (!variables.TryGetValue(name, out var value))
            {
                missing?.Add(name);
                return string.Empty;
            }

            return htmlEncode ? WebUtility.HtmlEncode(value) : value;
        });
    }

    /// <summary>The variables a template mentions, in the order they first appear.</summary>
    public static IReadOnlyList<string> Placeholders(string template) =>
        [.. Placeholder().Matches(template).Select(m => m.Groups[1].Value)
            .Concat(Conditional().Matches(template).Select(m => m.Groups[1].Value)).Distinct(StringComparer.Ordinal)];
}

/// <summary>An email ready to send: the subject (one line), the HTML and the plain-text body.</summary>
public sealed record RenderedEmail(string Subject, string HtmlBody, string TextBody);

public sealed partial class EmailTemplateRenderer
{
    public const int MaxVariables = 50;
    public const int MaxVariableLength = 4000;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex VariableName();

    /// <summary>A built-in template by name, with its defaults filled in and its required variables checked.</summary>
    public RenderedEmail RenderTemplate(string templateName, IReadOnlyDictionary<string, string>? variables)
    {
        var template = EmailTemplates.Find(templateName)
            ?? throw new ValidationException(new Dictionary<string, string[]>
            {
                ["Template"] = [$"Unknown template '{templateName}'. Available: {string.Join(", ", EmailTemplates.All.Select(t => t.Name))}."],
            });

        var values = Merge(variables, template.Defaults);
        var absent = template.Required.Where(name => !values.TryGetValue(name, out var v) || string.IsNullOrWhiteSpace(v)).ToList();
        if (absent.Count > 0)
            throw new ValidationException(new Dictionary<string, string[]> { ["Variables"] = [$"The {template.Name} template needs: {string.Join(", ", absent)}."] });

        return Render(template.Subject, template.Html, template.Text, values);
    }

    /// <summary>The caller's own subject and bodies; the same <c>{{variables}}</c> are replaced in them.</summary>
    public RenderedEmail RenderCustom(string subject, string? htmlBody, string? textBody, IReadOnlyDictionary<string, string>? variables) =>
        Render(subject, htmlBody ?? string.Empty, textBody ?? string.Empty, Merge(variables, new Dictionary<string, string>()));

    private static RenderedEmail Render(string subject, string html, string text, IReadOnlyDictionary<string, string> values) =>
        new(
            // A subject is one line: a line break in it would let a value add mail headers.
            Regex.Replace(EmailTemplateEngine.Render(subject, values, htmlEncode: false), @"\s*[\r\n]+\s*", " ").Trim(),
            EmailTemplateEngine.Render(html, values, htmlEncode: true),
            EmailTemplateEngine.Render(text, values, htmlEncode: false));

    private static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string>? supplied, IReadOnlyDictionary<string, string> defaults)
    {
        var errors = new List<string>();
        var merged = new Dictionary<string, string>(defaults, StringComparer.Ordinal);

        if (supplied is { Count: > MaxVariables }) errors.Add($"At most {MaxVariables} variables.");
        foreach (var (name, value) in supplied ?? new Dictionary<string, string>())
        {
            if (!VariableName().IsMatch(name)) errors.Add($"'{name}' is not a valid variable name (letters, digits and '_').");
            else if (value is { Length: > MaxVariableLength }) errors.Add($"'{name}' is longer than {MaxVariableLength} characters.");
            else if (IsLink(name) && !string.IsNullOrEmpty(value) && !(Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
                errors.Add($"'{name}' must be an absolute http or https address.");   // it ends up in an href: no javascript: links
            else merged[name] = value ?? string.Empty;
        }

        if (errors.Count > 0) throw new ValidationException(new Dictionary<string, string[]> { ["Variables"] = [.. errors] });
        return merged;
    }

    private static bool IsLink(string name) => name.Equals("link", StringComparison.Ordinal) || name.EndsWith("_url", StringComparison.Ordinal) || name.EndsWith("_link", StringComparison.Ordinal);
}
