using System.Text.Json;
using AppInHouse.SharedServices.Application.Common;
using AppInHouse.SharedServices.Application.Email;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.UnitTests;

public class EmailTemplateEngineTests
{
    private static IReadOnlyDictionary<string, string> Vars(params (string, string)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);

    [Fact]
    public void Placeholders_AreReplaced_InSubjectAndBodies()
    {
        var variables = Vars(("name", "Lan"), ("code", "482913"));

        Assert.Equal("Xin chào Lan, mã 482913", EmailTemplateEngine.Render("Xin chào {{name}}, mã {{code}}", variables, htmlEncode: false));
        Assert.Equal("<p>Lan: <b>482913</b></p>", EmailTemplateEngine.Render("<p>{{name}}: <b>{{code}}</b></p>", variables, htmlEncode: true));
    }

    [Theory]
    [InlineData("{{name}}")]
    [InlineData("{{ name }}")]
    [InlineData("{{\tname\t}}")]
    public void Whitespace_InsideTheBraces_IsAllowed(string template) =>
        Assert.Equal("Lan", EmailTemplateEngine.Render(template, Vars(("name", "Lan")), htmlEncode: false));

    [Fact]
    public void ThePlaceholder_ThatHasNoValue_BecomesEmpty_AndIsReported()
    {
        var missing = new List<string>();

        var text = EmailTemplateEngine.Render("Hi {{name}} {{surname}}!", Vars(("name", "Lan")), htmlEncode: false, missing);

        Assert.Equal("Hi Lan !", text);
        Assert.Equal(["surname"], missing);
    }

    [Fact]
    public void ValuesAreHtmlEscaped_InHtml_SoTheyCannotInjectMarkup()
    {
        var html = EmailTemplateEngine.Render("<p>{{name}}</p>", Vars(("name", "<script>alert('x')</script> & \"q\"")), htmlEncode: true);

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&amp;", html);
        Assert.StartsWith("<p>", html);   // the template's own markup is untouched
    }

    [Fact]
    public void ValuesAreNotEscaped_InPlainText()
    {
        Assert.Equal("a & b <c>", EmailTemplateEngine.Render("{{x}}", Vars(("x", "a & b <c>")), htmlEncode: false));
    }

    [Fact]
    public void AValueThatLooksLikeAPlaceholder_IsNotReplacedAgain()
    {
        // One pass only: a value cannot pull in another variable (or recurse).
        var text = EmailTemplateEngine.Render("{{a}}", Vars(("a", "{{b}}"), ("b", "SECRET")), htmlEncode: false);

        Assert.Equal("{{b}}", text);
    }

    [Fact]
    public void ConditionalBlocks_AreKeptOnlyWhenTheVariableIsNotEmpty()
    {
        const string template = "A{{#if link}} [{{link}}]{{/if}}B";

        Assert.Equal("A [https://x.test]B", EmailTemplateEngine.Render(template, Vars(("link", "https://x.test")), htmlEncode: false));
        Assert.Equal("AB", EmailTemplateEngine.Render(template, Vars(("link", "")), htmlEncode: false));
        Assert.Equal("AB", EmailTemplateEngine.Render(template, Vars(), htmlEncode: false));
    }

    [Fact]
    public void ConditionalBlocks_CanSpanLines()
    {
        var text = EmailTemplateEngine.Render("x\n{{#if d}}\n{{d}}\n{{/if}}\ny", Vars(("d", "details")), htmlEncode: false);

        Assert.Equal("x\n\ndetails\n\ny", text);
    }

    [Fact]
    public void Placeholders_ListsEveryVariableOnce()
    {
        var names = EmailTemplateEngine.Placeholders("{{a}} {{b}} {{a}} {{#if c}}{{c}}{{/if}}");

        Assert.Equal(["a", "b", "c"], names.Order());
    }
}

public class EmailTemplateRendererTests
{
    private readonly EmailTemplateRenderer _renderer = new();

    private static Dictionary<string, string> Vars(params (string, string)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);

    [Fact]
    public void TheThreeBuiltInTemplates_Exist()
    {
        Assert.Equal(["EmailVerification", "PasswordReset", "SystemNotification"], EmailTemplates.All.Select(t => t.Name).Order());
        Assert.NotNull(EmailTemplates.Find("emailverification"));   // case does not matter
        Assert.Null(EmailTemplates.Find("Nope"));
    }

    [Fact]
    public void EmailVerification_ShowsTheCode_AndTheLinkWhenThereIsOne()
    {
        var withLink = _renderer.RenderTemplate("EmailVerification", Vars(("name", "Lan"), ("code", "482913"), ("link", "https://app.test/verify?t=1&u=2")));
        var without = _renderer.RenderTemplate("EmailVerification", Vars(("name", "Lan"), ("code", "482913")));

        Assert.Equal("Xác thực tài khoản AppInHouse", withLink.Subject);   // app_name has a default
        Assert.Contains("482913", withLink.HtmlBody);
        Assert.Contains("482913", withLink.TextBody);
        Assert.Contains("Xin chào Lan", withLink.HtmlBody);
        Assert.Contains("https://app.test/verify?t=1&amp;u=2", withLink.HtmlBody);   // escaped in HTML
        Assert.Contains("https://app.test/verify?t=1&u=2", withLink.TextBody);      // as it is in text
        Assert.DoesNotContain("href", without.HtmlBody);
        Assert.DoesNotContain("liên kết", without.TextBody);
        Assert.Contains("15 phút", without.TextBody);                                // expires_in_minutes default
    }

    [Fact]
    public void PasswordReset_NeedsANameAndALink()
    {
        var rendered = _renderer.RenderTemplate("PasswordReset", Vars(("name", "Lan"), ("link", "https://app.test/reset?t=abc"), ("app_name", "LinguaBridge"), ("expires_in_minutes", "30")));

        Assert.Equal("Khôi phục mật khẩu LinguaBridge", rendered.Subject);
        Assert.Contains("href=\"https://app.test/reset?t=abc\"", rendered.HtmlBody);
        Assert.Contains("30 phút", rendered.TextBody);

        var error = Assert.Throws<ValidationException>(() => _renderer.RenderTemplate("PasswordReset", Vars(("name", "Lan"))));
        Assert.Contains("link", error.Errors["Variables"][0]);
    }

    [Fact]
    public void SystemNotification_ShowsTheTitleAndTheMessage_AndOptionalDetails()
    {
        var rendered = _renderer.RenderTemplate("SystemNotification", Vars(("title", "Disk 90%"), ("message", "Dung lượng sắp đầy."), ("details", "/dev/sda1 90%")));

        Assert.Equal("[AppInHouse] Disk 90%", rendered.Subject);
        Assert.Contains("Dung lượng sắp đầy.", rendered.HtmlBody);
        Assert.Contains("/dev/sda1 90%", rendered.HtmlBody);
        Assert.DoesNotContain("/dev/sda1", _renderer.RenderTemplate("SystemNotification", Vars(("title", "t"), ("message", "m"))).HtmlBody);
    }

    [Fact]
    public void ARequiredVariableThatIsMissingOrBlank_IsRefused_NamingIt()
    {
        var error = Assert.Throws<ValidationException>(() => _renderer.RenderTemplate("EmailVerification", Vars(("name", "  "))));

        Assert.Contains("name", error.Errors["Variables"][0]);
        Assert.Contains("code", error.Errors["Variables"][0]);
    }

    [Fact]
    public void AnUnknownTemplate_IsRefused_ListingTheKnownOnes()
    {
        var error = Assert.Throws<ValidationException>(() => _renderer.RenderTemplate("Spam", null));

        Assert.Contains("EmailVerification", error.Errors["Template"][0]);
    }

    [Fact]
    public void HtmlInAValue_IsEscaped_InTheRenderedTemplate()
    {
        var rendered = _renderer.RenderTemplate("EmailVerification", Vars(("name", "<img src=x onerror=alert(1)>"), ("code", "1")));

        Assert.DoesNotContain("<img", rendered.HtmlBody);
        Assert.Contains("&lt;img", rendered.HtmlBody);
        Assert.Contains("<img src=x onerror=alert(1)>", rendered.TextBody);   // plain text keeps what was given
    }

    [Fact]
    public void ALineBreakInTheSubject_CannotAddMailHeaders()
    {
        var rendered = _renderer.RenderCustom("Hello {{name}}", "<p>x</p>", null, Vars(("name", "Lan\r\nBcc: attacker@evil.test")));

        Assert.DoesNotContain('\r', rendered.Subject);
        Assert.DoesNotContain('\n', rendered.Subject);
        Assert.Equal("Hello Lan Bcc: attacker@evil.test", rendered.Subject);   // one line: it is text now, not a header
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,AAAA")]
    [InlineData("/relative/path")]
    [InlineData("ftp://host/file")]
    public void ALinkVariable_MustBeAnAbsoluteHttpAddress(string link)
    {
        var error = Assert.Throws<ValidationException>(() => _renderer.RenderTemplate("PasswordReset", Vars(("name", "Lan"), ("link", link))));

        Assert.Contains("link", error.Errors["Variables"][0]);
    }

    [Fact]
    public void AnyVariableEndingInUrlOrLink_GetsTheSameCheck()
    {
        Assert.Throws<ValidationException>(() => _renderer.RenderCustom("s", "<a href=\"{{home_url}}\">x</a>", null, Vars(("home_url", "javascript:alert(1)"))));
        Assert.Throws<ValidationException>(() => _renderer.RenderCustom("s", "<a href=\"{{docs_link}}\">x</a>", null, Vars(("docs_link", "vbscript:x"))));
        _ = _renderer.RenderCustom("s", "<a href=\"{{home_url}}\">x</a>", null, Vars(("home_url", "https://ok.test")));
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("1abc")]
    [InlineData("a-b")]
    [InlineData("")]
    public void AVariableNeedsAProperName(string name) =>
        Assert.Throws<ValidationException>(() => _renderer.RenderCustom("s", "b", null, Vars((name, "v"))));

    [Fact]
    public void TooManyOrTooLongVariables_AreRefused()
    {
        var many = Enumerable.Range(0, EmailTemplateRenderer.MaxVariables + 1).ToDictionary(i => $"v{i}", i => "x");

        Assert.Throws<ValidationException>(() => _renderer.RenderCustom("s", "b", null, many));
        Assert.Throws<ValidationException>(() => _renderer.RenderCustom("s", "b", null, Vars(("big", new string('x', EmailTemplateRenderer.MaxVariableLength + 1)))));
    }

    [Fact]
    public void CustomContent_GetsItsVariablesReplaced_Too()
    {
        var rendered = _renderer.RenderCustom("Hi {{n}}", "<p>{{n}}</p>", "Plain {{n}}", Vars(("n", "Lan <3")));

        Assert.Equal("Hi Lan <3", rendered.Subject);
        Assert.Equal("<p>Lan &lt;3</p>", rendered.HtmlBody);
        Assert.Equal("Plain Lan <3", rendered.TextBody);
    }
}

public class EmailSettingsReaderTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void SmtpSettings_AreRead()
    {
        var settings = EmailSettingsReader.Read(Json("""{"host":"smtp.example.com","port":465,"useSsl":"SslOnConnect","username":"u","senderEmail":"a@b.co","senderName":"Name"}"""));

        Assert.Equal(("smtp.example.com", 465, SmtpSecurity.SslOnConnect, "u", "a@b.co", "Name"),
            (settings.Host, settings.Port, settings.Security, settings.Username, settings.SenderEmail, settings.SenderName));
    }

    [Fact]
    public void MissingValues_GetTheDefaults()
    {
        var settings = EmailSettingsReader.Read(Json("{}"));

        Assert.Equal((null, 587, SmtpSecurity.Auto, null, null, null), (settings.Host, settings.Port, settings.Security, settings.Username, settings.SenderEmail, settings.SenderName));
    }

    [Theory]
    [InlineData("none", SmtpSecurity.None)]
    [InlineData("STARTTLS", SmtpSecurity.StartTls)]
    [InlineData("sslonconnect", SmtpSecurity.SslOnConnect)]
    [InlineData("Auto", SmtpSecurity.Auto)]
    public void TheSecurityMode_IsCaseInsensitive(string value, SmtpSecurity expected) =>
        Assert.Equal(expected, EmailSettingsReader.Read(Json($$"""{"useSsl":"{{value}}"}""")).Security);

    [Fact]
    public void GoodSettings_HaveNoProblems()
    {
        Assert.Empty(EmailSettingsReader.Validate(ProviderType.Smtp, Json("""{"host":"smtp.example.com","port":587,"useSsl":"StartTls","senderEmail":"noreply@example.com"}"""), complete: true));
        Assert.Empty(EmailSettingsReader.Validate(ProviderType.SendGrid, Json("""{"senderEmail":"noreply@example.com","senderName":"AppInHouse"}"""), complete: true));
        Assert.Empty(EmailSettingsReader.Validate(ProviderType.Mock, Json("{}"), complete: true));
    }

    [Theory]
    [InlineData("""{"port":0}""")]
    [InlineData("""{"port":65536}""")]
    [InlineData("""{"port":"587"}""")]
    [InlineData("""{"port":1.5}""")]
    [InlineData("""{"useSsl":"Always"}""")]
    [InlineData("""{"useSsl":1}""")]
    [InlineData("""{"host":"smtp://host:25"}""")]
    [InlineData("""{"host":"host with space"}""")]
    [InlineData("""{"host":"host/path"}""")]
    [InlineData("""{"senderEmail":"not-an-email"}""")]
    [InlineData("""{"senderEmail":"Name <a@b.co>"}""")]
    [InlineData("""{"senderEmail":"a@b.co, c@d.co"}""")]
    [InlineData("""{"senderName":"line\nbreak"}""")]
    public void BadSettings_AreReported_EvenForADraft(string settings) =>
        Assert.NotEmpty(EmailSettingsReader.Validate(ProviderType.Smtp, Json(settings), complete: false));

    [Fact]
    public void ADraftMayBeIncomplete_ButSendingNeedsTheRequiredValues()
    {
        Assert.Empty(EmailSettingsReader.Validate(ProviderType.Smtp, Json("{}"), complete: false));

        var smtp = EmailSettingsReader.Validate(ProviderType.Smtp, Json("{}"), complete: true);
        Assert.Contains(smtp, p => p.Contains("host"));
        Assert.Contains(smtp, p => p.Contains("senderEmail"));
        Assert.Contains(EmailSettingsReader.Validate(ProviderType.SendGrid, Json("{}"), complete: true), p => p.Contains("senderEmail"));
        Assert.Empty(EmailSettingsReader.Validate(ProviderType.Mock, Json("{}"), complete: true));
    }

    [Fact]
    public void SettingsThatAreNotAnObject_AreRefused() =>
        Assert.Single(EmailSettingsReader.Validate(ProviderType.Smtp, Json("[1]"), complete: false));

    [Theory]
    [InlineData("a@b.co", true)]
    [InlineData("first.last+tag@sub.example.com", true)]
    [InlineData("a@b.co\r\nBcc: x@y.z", false)]
    [InlineData("a@b.co\nBcc: x@y.z", false)]
    [InlineData("Name <a@b.co>", false)]
    [InlineData("a@b.co,c@d.co", false)]
    [InlineData("a@b.co;c@d.co", false)]
    [InlineData("a b@c.co", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("no-at-sign", false)]
    public void IsAddress_AcceptsOnePlainAddress_AndNothingElse(string? value, bool expected) =>
        Assert.Equal(expected, EmailSettingsReader.IsAddress(value));

    [Fact]
    public void AnAddressLongerThan254_IsRefused() =>
        Assert.False(EmailSettingsReader.IsAddress(new string('a', 250) + "@b.co"));
}

public class ErrorTextTests
{
    [Fact]
    public void ASecretInTheMessage_IsRemoved()
    {
        var text = ErrorText.Clean("Login failed for user with password hunter2-secret at host", "hunter2-secret");

        Assert.DoesNotContain("hunter2-secret", text);
        Assert.Contains("••••", text);
    }

    [Fact]
    public void EverySecretGiven_IsRemoved_AndNullOnesAreIgnored()
    {
        var text = ErrorText.Clean("a SECRET-ONE b SECRET-TWO c", "SECRET-ONE", null, "", "SECRET-TWO");

        Assert.DoesNotContain("SECRET-ONE", text);
        Assert.DoesNotContain("SECRET-TWO", text);
    }

    [Fact]
    public void AVeryShortSecret_IsLeftAlone_SoTheMessageStaysReadable() =>
        Assert.Equal("abc abc", ErrorText.Clean("abc abc", "abc"));

    [Fact]
    public void LineBreaksAndControlCharacters_BecomeOneLine() =>
        Assert.Equal("first second third", ErrorText.Clean("first\r\n\r\nsecond\t\tthird\u0000"));

    [Fact]
    public void ALongMessage_IsCutAtTheLimit()
    {
        var text = ErrorText.Clean(new string('x', 1000));

        Assert.Equal(ErrorText.MaxLength, text.Length);
        Assert.EndsWith("…", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingToSay_BecomesAGenericMessage(string? text) =>
        Assert.Equal("Unknown error.", ErrorText.Clean(text));
}
