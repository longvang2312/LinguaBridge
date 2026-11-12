using System.Security.Cryptography;
using System.Text;
using AppInHouse.SharedServices.Application.Jobs;
using AppInHouse.SharedServices.Application.Platform;
using AppInHouse.SharedServices.Application.Webhooks;
using AppInHouse.SharedServices.Domain;

namespace AppInHouse.SharedServices.UnitTests;

public class WebhookSignerTests
{
    private const string Secret = "whsec_unit-test-secret-0123456789";

    [Fact]
    public void TheSignature_IsSha256_AndTheLowerCaseHexOfTheHmacOfTheExactBody()
    {
        const string body = """{"id":"1","event":"email.sent","data":{"to":"a@b.co"}}""";
        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body)));

        var signature = WebhookSigner.Sign(Secret, body);

        Assert.Equal(expected, signature);
        Assert.Matches("^sha256=[0-9a-f]{64}$", signature);
    }

    [Fact]
    public void TheSignature_OfTheBytesAndOfTheText_AreTheSame()
    {
        Assert.Equal(WebhookSigner.Sign(Secret, "héllo"), WebhookSigner.Sign(Secret, Encoding.UTF8.GetBytes("héllo")));
    }

    [Fact]
    public void ARightSignature_IsValid()
    {
        var body = Encoding.UTF8.GetBytes("payload");
        Assert.True(WebhookSigner.IsValid(Secret, body, WebhookSigner.Sign(Secret, body)));
        Assert.True(WebhookSigner.IsValid(Secret, body, "  " + WebhookSigner.Sign(Secret, body) + " "));   // surrounding blanks of a header are not part of it
    }

    [Theory]
    [InlineData("other-secret-0123456789")]
    [InlineData("")]
    public void ASignatureMadeWithAnotherKey_IsNotValid(string otherSecret)
    {
        var body = Encoding.UTF8.GetBytes("payload");
        Assert.False(WebhookSigner.IsValid(Secret, body, otherSecret.Length == 0 ? "sha256=" : WebhookSigner.Sign(otherSecret, body)));
    }

    [Fact]
    public void ABodyThatWasChangedByOneByte_IsNotValid()
    {
        var signature = WebhookSigner.Sign(Secret, "payload");
        Assert.False(WebhookSigner.IsValid(Secret, Encoding.UTF8.GetBytes("payloae"), signature));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=")]
    [InlineData("sha1=abcdef")]
    [InlineData("not a signature at all")]
    public void AMissingOrMalformedHeader_IsNotValid_AndNeverThrows(string? header)
    {
        Assert.False(WebhookSigner.IsValid(Secret, Encoding.UTF8.GetBytes("payload"), header));
    }

    [Fact]
    public void AnUpperCaseHexSignature_IsNotValid_BecauseTheServiceSendsLowerCaseAndComparesExactly()
    {
        var body = Encoding.UTF8.GetBytes("payload");
        var signature = WebhookSigner.Sign(Secret, body);
        Assert.False(WebhookSigner.IsValid(Secret, body, "sha256=" + signature["sha256=".Length..].ToUpperInvariant()));
    }

    [Fact]
    public void WithoutASecret_NothingIsValid()
    {
        Assert.False(WebhookSigner.IsValid(string.Empty, Encoding.UTF8.GetBytes("x"), WebhookSigner.Sign("anything", "x")));
    }
}

public class InboundVerificationTests
{
    private const string Secret = "inbound-secret-0123456789";
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"update_id":1}""");

    private static Dictionary<string, string> Headers(params (string Name, string Value)[] headers) =>
        headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase);

    // ---- telegram

    [Fact]
    public void Telegram_AcceptsTheSecretToken_AnyCaseOfTheHeaderName()
    {
        Assert.Null(InboundSources.Verify("telegram", Secret, Headers(("x-telegram-bot-api-secret-token", Secret)), Body, Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("inbound-secret-012345678")]
    [InlineData("inbound-secret-01234567890")]
    [InlineData("INBOUND-SECRET-0123456789")]
    public void Telegram_RefusesAMissingOrDifferentToken(string? token)
    {
        var headers = token is null ? Headers() : Headers(("X-Telegram-Bot-Api-Secret-Token", token));
        Assert.NotNull(InboundSources.Verify("telegram", Secret, headers, Body, Now));
    }

    // ---- github

    [Fact]
    public void GitHub_AcceptsTheHmacOfTheBody()
    {
        Assert.Null(InboundSources.Verify("github", Secret, Headers(("X-Hub-Signature-256", WebhookSigner.Sign(Secret, Body))), Body, Now));
    }

    [Fact]
    public void GitHub_RefusesATamperedBody_AWrongSecret_AndNoHeader()
    {
        var signature = WebhookSigner.Sign(Secret, Body);
        Assert.NotNull(InboundSources.Verify("github", Secret, Headers(("X-Hub-Signature-256", signature)), Encoding.UTF8.GetBytes("""{"update_id":2}"""), Now));
        Assert.NotNull(InboundSources.Verify("github", Secret, Headers(("X-Hub-Signature-256", WebhookSigner.Sign("other-secret-0123456789", Body))), Body, Now));
        Assert.NotNull(InboundSources.Verify("github", Secret, Headers(), Body, Now));
    }

    // ---- stripe

    private static string StripeHeader(string secret, DateTimeOffset at, byte[] body, params string[] extraV1)
    {
        var t = at.ToUnixTimeSeconds();
        var signed = Encoding.UTF8.GetBytes(t + ".").Concat(body).ToArray();
        var v1 = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed));
        return $"t={t},v1={v1}" + string.Concat(extraV1.Select(e => ",v1=" + e));
    }

    [Fact]
    public void Stripe_AcceptsTheSignedTimestampAndBody()
    {
        Assert.Null(InboundSources.Verify("stripe", Secret, Headers(("Stripe-Signature", StripeHeader(Secret, Now, Body))), Body, Now));
    }

    [Fact]
    public void Stripe_AcceptsWhenAnyOfSeveralV1SignaturesMatches()
    {
        var header = StripeHeader(Secret, Now, Body).Replace("v1=", "v1=" + new string('0', 64) + ",v1=");
        Assert.Null(InboundSources.Verify("stripe", Secret, Headers(("Stripe-Signature", header)), Body, Now));
    }

    [Fact]
    public void Stripe_RefusesAStaleTimestamp_AFutureOne_AndAcceptsFourMinutes()
    {
        Assert.NotNull(InboundSources.Verify("stripe", Secret, Headers(("Stripe-Signature", StripeHeader(Secret, Now.AddMinutes(-6), Body))), Body, Now));
        Assert.NotNull(InboundSources.Verify("stripe", Secret, Headers(("Stripe-Signature", StripeHeader(Secret, Now.AddMinutes(6), Body))), Body, Now));
        Assert.Null(InboundSources.Verify("stripe", Secret, Headers(("Stripe-Signature", StripeHeader(Secret, Now.AddMinutes(-4), Body))), Body, Now));
    }

    [Fact]
    public void Stripe_RefusesAnotherSecret_ATamperedBody_AndAMalformedHeader()
    {
        Assert.NotNull(InboundSources.Verify("stripe", Secret, Headers(("Stripe-Signature", StripeHeader("other-secret-0123456789", Now, Body))), Body, Now));
        Assert.NotNull(InboundSources.Verify("stripe", Secret, Headers(("Stripe-Signature", StripeHeader(Secret, Now, Body))), Encoding.UTF8.GetBytes("{}"), Now));
        Assert.NotNull(InboundSources.Verify("stripe", Secret, Headers(("Stripe-Signature", "garbage")), Body, Now));
        Assert.NotNull(InboundSources.Verify("stripe", Secret, Headers(("Stripe-Signature", "t=abc,v1=00")), Body, Now));
        Assert.NotNull(InboundSources.Verify("stripe", Secret, Headers(), Body, Now));
    }

    [Fact]
    public void AnUnknownSource_IsNeverVerified()
    {
        Assert.NotNull(InboundSources.Verify("paypal", Secret, Headers(), Body, Now));
    }
}

public class CronScheduleTests
{
    private static DateTimeOffset At(int hour, int minute, int second = 0) => new(2026, 11, 12, hour, minute, second, TimeSpan.Zero);

    [Theory]
    [InlineData("*/5 * * * *", 10, 3, 10, 5)]
    [InlineData("*/5 * * * *", 10, 5, 10, 10)]   // strictly after: a run at 10:05 is next at 10:10
    [InlineData("*/5 * * * *", 10, 58, 11, 0)]
    [InlineData("0 2 * * *", 1, 59, 2, 0)]
    [InlineData("0 2 * * *", 2, 0, 2, 0)]        // checked below: the next 02:00 is tomorrow
    [InlineData("30 2 * * *", 3, 0, 2, 30)]      // checked below: tomorrow
    [InlineData("15 * * * *", 10, 14, 10, 15)]
    [InlineData("15 * * * *", 10, 15, 11, 15)]
    public void TheNextRun_IsTheFirstMatchingMinuteStrictlyAfter(string cron, int hour, int minute, int expectedHour, int expectedMinute)
    {
        var next = CronSchedule.Parse(cron).NextAfter(At(hour, minute, second: 30));

        Assert.Equal(expectedMinute, next.Minute);
        Assert.Equal(expectedHour, next.Hour);
        Assert.True(next > At(hour, minute, 30));
        Assert.Equal(0, next.Second);
    }

    [Fact]
    public void ADailyTaskThatAlreadyRanToday_RunsTomorrow()
    {
        var next = CronSchedule.Parse("0 2 * * *").NextAfter(At(2, 0));

        Assert.Equal(new DateTimeOffset(2026, 11, 13, 2, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void TheTimeIsUtc_WhateverOffsetItIsGivenIn()
    {
        var local = new DateTimeOffset(2026, 11, 12, 9, 58, 0, TimeSpan.FromHours(7));   // 02:58 UTC

        var next = CronSchedule.Parse("0 3 * * *").NextAfter(local);

        Assert.Equal(new DateTimeOffset(2026, 11, 12, 3, 0, 0, TimeSpan.Zero), next);
    }

    [Theory]
    [InlineData("")]
    [InlineData("* * * * *")]          // every minute: not one of the two supported forms
    [InlineData("*/0 * * * *")]
    [InlineData("*/60 * * * *")]
    [InlineData("*/5 3 * * *")]
    [InlineData("61 * * * *")]
    [InlineData("0 24 * * *")]
    [InlineData("0 2 1 * *")]          // a day of the month is not supported, and must not be silently ignored
    [InlineData("0 2 * 1 *")]
    [InlineData("0 2 * * 1")]
    [InlineData("0 2 * *")]
    [InlineData("a b * * *")]
    public void AnExpressionItCannotHonour_IsRefusedInsteadOfMisread(string cron)
    {
        Assert.Throws<FormatException>(() => CronSchedule.Parse(cron));
    }

    [Fact]
    public void TheTasksOfTheService_UseSchedulesThatParse()
    {
        var retention = new JobSettings();
        IScheduledTask[] tasks = [new LogRetentionTask(null!, retention, TimeProvider.System), new JobHousekeepingTask(null!, retention, TimeProvider.System), new MetricsAggregationTask(null!)];

        foreach (var task in tasks) Assert.True(CronSchedule.Parse(task.Cron).NextAfter(At(0, 0)) > At(0, 0), task.Id);
        Assert.Equal(["log-retention", "job-housekeeping", "metrics-aggregate"], tasks.Select(t => t.Id));
        Assert.Equal("*/5 * * * *", tasks[2].Cron);
    }
}

public class JobSettingsTests
{
    [Fact]
    public void ByDefault_ThereAreFourAttempts_WaitingOneFiveAndFifteenSeconds()
    {
        var settings = new JobSettings();

        Assert.Equal(4, settings.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(1), settings.DelayAfter(1));
        Assert.Equal(TimeSpan.FromSeconds(5), settings.DelayAfter(2));
        Assert.Equal(TimeSpan.FromSeconds(15), settings.DelayAfter(3));
    }

    [Fact]
    public void TheWaitsAreConfigurable_AndTheNumberOfAttemptsFollowsThem()
    {
        var settings = new JobSettings { RetryDelaysSeconds = [2, 4] };

        Assert.Equal(3, settings.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(4), settings.DelayAfter(2));
    }

    [Fact]
    public void WithNoWaitsAtAll_ThereIsOneAttempt()
    {
        var settings = new JobSettings { RetryDelaysSeconds = [] };

        Assert.Equal(1, settings.MaxAttempts);
        Assert.Equal(TimeSpan.Zero, settings.DelayAfter(1));
    }

    [Fact]
    public void AnAttemptCountBeyondTheLastWait_KeepsTheLastWait()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), new JobSettings().DelayAfter(9));
    }
}

public class BackgroundJobRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static BackgroundJobRecord NewJob(int maxAttempts = 4) => BackgroundJobRecord.Create(JobQueues.Email, "send", "{}", maxAttempts, Now);

    [Fact]
    public void ANewJob_IsPendingAndDueAtOnce()
    {
        var job = NewJob();

        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(0, job.Attempts);
        Assert.Equal(Now, job.NextAttemptAt);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("")]
    [InlineData("Email")]   // queue names are the lower-case ones
    public void AJobNeedsOneOfTheFiveQueues(string queue)
    {
        Assert.Throws<DomainException>(() => BackgroundJobRecord.Create(queue, "send", "{}", 4, Now));
    }

    [Fact]
    public void AJobNeedsATypeAndAtLeastOneAttempt()
    {
        Assert.Throws<DomainException>(() => BackgroundJobRecord.Create(JobQueues.Email, " ", "{}", 4, Now));
        Assert.Throws<DomainException>(() => BackgroundJobRecord.Create(JobQueues.Email, "send", "{}", 0, Now));
    }

    [Fact]
    public void AFailedAttempt_WithAttemptsLeft_WaitsForTheRetry()
    {
        var job = NewJob();

        var again = job.MarkAttemptFailed("provider down", Now, Now.AddSeconds(5));

        Assert.True(again);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(Now.AddSeconds(5), job.NextAttemptAt);
        Assert.Equal("provider down", job.LastError);
    }

    [Fact]
    public void TheLastAttempt_ThatFails_FailsTheJob()
    {
        var job = NewJob(maxAttempts: 2);
        SetAttempts(job, 2);

        var again = job.MarkAttemptFailed("still down", Now, Now.AddSeconds(5));

        Assert.False(again);
        Assert.Equal(JobStatus.Failed, job.Status);
    }

    [Fact]
    public void APermanentFailure_FailsTheJobAtOnce_WhateverIsLeft()
    {
        var job = NewJob();
        SetAttempts(job, 1);

        var again = job.MarkAttemptFailed("no such configuration", Now, Now, permanent: true);

        Assert.False(again);
        Assert.Equal(JobStatus.Failed, job.Status);
    }

    [Fact]
    public void AnErrorMessage_IsCutToFiveHundredCharacters()
    {
        var job = NewJob();

        job.MarkAttemptFailed(new string('x', 5000), Now, Now);

        Assert.Equal(BackgroundJobRecord.MaxErrorLength, job.LastError!.Length);
    }

    [Fact]
    public void ACompletedJob_ForgetsItsLastError_AndRemembersWhenItFinished()
    {
        var job = NewJob();
        job.MarkAttemptFailed("blip", Now, Now);

        job.MarkCompleted(Now.AddSeconds(3));

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Null(job.LastError);
        Assert.Equal(Now.AddSeconds(3), job.CompletedAt);
    }

    [Fact]
    public void OnlyAFailedJob_CanBeRetried_AndItStartsOverWithAllItsAttempts()
    {
        var job = NewJob();
        Assert.Throws<DomainException>(() => job.Requeue(Now));

        SetAttempts(job, 4);
        job.MarkAttemptFailed("down", Now, Now);
        Assert.Equal(JobStatus.Failed, job.Status);

        job.Requeue(Now.AddMinutes(1));

        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(0, job.Attempts);
        Assert.Equal(Now.AddMinutes(1), job.NextAttemptAt);
    }

    [Fact]
    public void AJobCutOffInTheMiddle_WaitsAgain_AndOtherJobsAreLeftAlone()
    {
        var running = NewJob();
        SetStatus(running, JobStatus.Processing);
        var done = NewJob();
        done.MarkCompleted(Now);

        running.ResetInterrupted(Now.AddMinutes(2));
        done.ResetInterrupted(Now.AddMinutes(2));

        Assert.Equal(JobStatus.Pending, running.Status);
        Assert.Equal(JobStatus.Completed, done.Status);
    }

    private static void SetAttempts(BackgroundJobRecord job, int attempts) =>
        typeof(BackgroundJobRecord).GetProperty(nameof(BackgroundJobRecord.Attempts))!.SetValue(job, attempts);

    private static void SetStatus(BackgroundJobRecord job, JobStatus status) =>
        typeof(BackgroundJobRecord).GetProperty(nameof(BackgroundJobRecord.Status))!.SetValue(job, status);
}

public class PlatformEntityRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    // ---- projects

    [Fact]
    public void AProject_StartsActive_AndAQuotaLeftEmptyMeansNoLimit()
    {
        var project = ProjectTenant.Create("crm", "CRM", null, ProjectEnvironment.Staging, null, null, null, Now);

        Assert.True(project.IsActive);
        Assert.Null(project.DailyEmailQuota);
        Assert.Null(project.StorageQuotaBytes);
        Assert.Equal(string.Empty, project.Description);
    }

    [Theory]
    [InlineData(-1, null, null)]
    [InlineData(null, -1, null)]
    [InlineData(null, null, -1L)]
    public void ANegativeQuota_IsRefused(int? emails, int? chats, long? bytes)
    {
        Assert.Throws<DomainException>(() => ProjectTenant.Create("crm", "CRM", null, ProjectEnvironment.Staging, emails, chats, bytes, Now));
    }

    [Fact]
    public void AQuotaOfZero_IsALimit_NotNoLimit()
    {
        var project = ProjectTenant.Create("crm", "CRM", null, ProjectEnvironment.Staging, 0, 0, 0, Now);

        Assert.Equal(0, project.DailyEmailQuota);
        Assert.Equal(0L, project.StorageQuotaBytes);
    }

    [Fact]
    public void ANameIsRequired_AndTheDescriptionBounded()
    {
        Assert.Throws<DomainException>(() => ProjectTenant.Create("crm", " ", null, ProjectEnvironment.Staging, null, null, null, Now));
        Assert.Throws<DomainException>(() => ProjectTenant.Create("crm", new string('n', 101), null, ProjectEnvironment.Staging, null, null, null, Now));
        Assert.Throws<DomainException>(() => ProjectTenant.Create("crm", "CRM", new string('d', 501), ProjectEnvironment.Staging, null, null, null, Now));
    }

    [Fact]
    public void ThePlatformProject_CannotBeDeactivated_ButAnotherOneCan()
    {
        var platform = ProjectTenant.Create("default", "Platform", null, ProjectEnvironment.Production, null, null, null, Now);
        var other = ProjectTenant.Create("crm", "CRM", null, ProjectEnvironment.Staging, null, null, null, Now);

        Assert.Throws<DomainException>(() => platform.Update("Platform", null, ProjectEnvironment.Production, null, null, null, isActive: false));
        other.Update("CRM", null, ProjectEnvironment.Staging, null, null, null, isActive: false);

        Assert.True(platform.IsActive);
        Assert.False(other.IsActive);
    }

    [Fact]
    public void AFailedUpdate_ChangesNothing()
    {
        var project = ProjectTenant.Create("crm", "CRM", "old", ProjectEnvironment.Staging, 10, 10, 10, Now);

        Assert.Throws<DomainException>(() => project.Update("New name", "new", ProjectEnvironment.Production, -5, 20, 20, true));

        Assert.Equal("CRM", project.Name);
        Assert.Equal(10, project.DailyEmailQuota);
        Assert.Equal(ProjectEnvironment.Staging, project.Environment);
    }

    // ---- webhook subscriptions

    private static WebhookSubscription Subscribe(string url = "https://app.example.com/hook", string secret = "whsec_0123456789abcdef", params string[] events) =>
        WebhookSubscription.Create("crm", "App", url, "protected", secret, events.Length == 0 ? [WebhookEvents.EmailSent] : events, Now);

    [Theory]
    [InlineData("ftp://app.example.com/hook")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("app.example.com/hook")]
    [InlineData("https://user:pass@app.example.com/hook")]
    [InlineData("")]
    public void ATarget_MustBeAnHttpAddress_WithoutCredentials(string url)
    {
        Assert.Throws<DomainException>(() => Subscribe(url));
    }

    [Fact]
    public void ASecretShorterThanSixteenCharacters_IsRefused()
    {
        Assert.Throws<DomainException>(() => Subscribe(secret: "short-secret-15c"[..15]));
        Assert.NotNull(Subscribe(secret: new string('s', 16)));
    }

    [Fact]
    public void AnUnknownEvent_IsRefused_AndAtLeastOneIsNeeded()
    {
        Assert.Throws<DomainException>(() => Subscribe(events: ["email.sent", "user.deleted"]));
        Assert.Throws<DomainException>(() => WebhookSubscription.Create("crm", "App", "https://a.example.com", "p", new string('s', 16), [" "], Now));
    }

    [Fact]
    public void TheSecretIsNeverStoredInClear_OnlyItsLastFourCharactersAsAHint()
    {
        var subscription = Subscribe(secret: "whsec_super-secret-ABCD");

        Assert.Equal("ABCD", subscription.SecretHint);
        Assert.Equal("protected", subscription.ProtectedSecret);
        Assert.DoesNotContain("super-secret", subscription.SecretHint + subscription.ProtectedSecret + subscription.Name + subscription.TargetUrl);
    }

    [Fact]
    public void ASubscription_WantsTheEventsItAskedFor_OrAllOfThemWithAStar()
    {
        var some = Subscribe(events: [WebhookEvents.EmailSent, WebhookEvents.FileUploaded]);
        var all = Subscribe(events: [WebhookEvents.All]);

        Assert.True(some.Wants("email.sent"));
        Assert.True(some.Wants("file.uploaded"));
        Assert.False(some.Wants("chat.sent"));
        Assert.True(all.Wants("chat.failed"));
        Assert.True(all.Wants("anything"));
    }

    [Fact]
    public void TheDeliveryCounters_FollowEachAttempt_AndTheLastStatusIsBounded()
    {
        var subscription = Subscribe();

        subscription.RecordDelivery(true, "200", Now);
        subscription.RecordDelivery(false, new string('e', 400), Now.AddSeconds(1));

        Assert.Equal(1, subscription.SuccessCount);
        Assert.Equal(1, subscription.FailureCount);
        Assert.Equal(100, subscription.LastStatus!.Length);
        Assert.Equal(Now.AddSeconds(1), subscription.LastDeliveryAt);
    }

    // ---- notification templates

    [Theory]
    [InlineData("UserRegistered", true)]
    [InlineData("A1", true)]
    [InlineData("Password_Reset", true)]
    [InlineData("a", false)]
    [InlineData("1Bad", false)]
    [InlineData("has space", false)]
    [InlineData("has-dash", false)]
    [InlineData("", false)]
    public void ATemplateCode_IsALetterThenLettersDigitsAndUnderscores(string code, bool valid)
    {
        Assert.Equal(valid, NotificationTemplate.IsValidCode(code));
    }

    [Fact]
    public void TheStockTemplates_AreTheFiveForApplicationsAndTheTwoOfTheChat_AndAllOfThemAreValid()
    {
        Assert.Equal(["UserRegistered", "PasswordReset", "SecurityAlert", "SystemMaintenance", "PaymentSuccess", "ChatMessageOfflineEmail", "ChatMessageOfflineChat"], PlatformDefaults.Templates.Select(t => t.Code));
        foreach (var (code, name, subject, body, channel, priority) in PlatformDefaults.Templates)
            Assert.Equal(code, NotificationTemplate.Create(code, name, subject, body, channel, priority, Now).Code);
    }

    [Fact]
    public void TheStockProjects_AreDefaultIdentityLinguabridgeAndCrm_OnlyTheDefaultOneIsUnlimited()
    {
        Assert.Equal(["default", "identity", "linguabridge", "crm"], PlatformDefaults.Projects.Select(p => p.Code));
        Assert.Equal([ProjectEnvironment.Production, ProjectEnvironment.Production, ProjectEnvironment.Production, ProjectEnvironment.Staging], PlatformDefaults.Projects.Select(p => p.Environment));
        Assert.Null(PlatformDefaults.Projects[0].Emails);
        Assert.All(PlatformDefaults.Projects.Skip(1), p => Assert.NotNull(p.Emails));
    }

    [Fact]
    public void AQueueNameIsKnownOnlyInItsExactForm()
    {
        Assert.Equal(["email", "chat", "media-thumbnail", "outbox", "webhook"], JobQueues.All);
        Assert.True(JobQueues.IsKnown("media-thumbnail"));
        Assert.False(JobQueues.IsKnown("Email"));
        Assert.False(JobQueues.IsKnown(null));
    }

    [Fact]
    public void AnInboundLogOfARefusedCall_NeverKeepsWhatTheCallCarried_AndAnAcceptedOneIsCut()
    {
        var rejected = InboundWebhookLog.Rejected("github", "bad signature", 120, Now);
        var accepted = InboundWebhookLog.Accepted("github", new string('p', InboundWebhookLog.MaxStoredPayloadLength + 500), 70_000, Now);

        Assert.Null(rejected.Payload);
        Assert.False(rejected.SignatureValid);
        Assert.Equal(InboundWebhookStatus.Rejected, rejected.Status);
        Assert.Equal(InboundWebhookLog.MaxStoredPayloadLength, accepted.Payload!.Length);
        Assert.Equal(70_000, accepted.PayloadSize);
    }
}
