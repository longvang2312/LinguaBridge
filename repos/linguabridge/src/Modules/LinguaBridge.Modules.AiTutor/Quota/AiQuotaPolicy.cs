using LinguaBridge.Application.Abstractions;
using LinguaBridge.Modules.AiTutor.Abstractions;
using LinguaBridge.Modules.AiTutor.Entities;

namespace LinguaBridge.Modules.AiTutor.Quota;

/// <summary>
/// The daily AI allowance. The day is the UTC day. A learner on a paid plan is limited by tokens (the plan's
/// <c>ai_daily_token_quota</c>); everyone else by number of requests (<see cref="AiTutorOptions.FreeDailyRequests"/>).
/// </summary>
public sealed class AiQuotaPolicy(IAiTutorRepository repository, IUserEntitlementSource entitlements, IDateTimeProvider clock, AiTutorOptions options)
{
    public const string FreePlan = "Free";
    public const string PaidPlan = "Paid";

    /// <summary>A rough size of the prompt: about four characters per token.</summary>
    public static int EstimateTokens(params string[] texts) => texts.Sum(t => (t.Length + 3) / 4);

    public async Task<AiQuotaDto> GetStatusAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var windowStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);

        var usage = await repository.GetUsageSinceAsync(userId, windowStart, cancellationToken);
        var paidQuota = await entitlements.GetPaidDailyTokenQuotaAsync(userId, now, cancellationToken);
        var resetsAt = windowStart.AddDays(1);

        return paidQuota is { } tokenLimit
            ? new AiQuotaDto(PaidPlan, null, usage.Requests, null, tokenLimit, usage.Tokens, Math.Max(0, tokenLimit - usage.Tokens), resetsAt)
            : new AiQuotaDto(FreePlan, options.FreeDailyRequests, usage.Requests, Math.Max(0, options.FreeDailyRequests - usage.Requests), null, usage.Tokens, null, resetsAt);
    }

    /// <summary>
    /// Claims the allowance for one request before the model is called, so requests arriving together cannot all see
    /// "not used yet". The claim is a ledger row holding the estimated tokens; the caller settles it with the real
    /// numbers, or removes it if the call fails.
    /// Free: refused once the day's requests are used. Paid: refused when tokens already used (claims included) plus
    /// this request's estimated prompt would pass the plan's quota.
    /// </summary>
    /// <exception cref="AiQuotaExceededException">Today's allowance does not cover this request.</exception>
    public async Task<AiUsageLedger> ReserveAsync(Guid userId, int estimatedTokens, string taskType, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var windowStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var paidQuota = await entitlements.GetPaidDailyTokenQuotaAsync(userId, now, cancellationToken);

        var reservation = new AiUsageLedger(userId, taskType, AiUsageLedger.PendingProvider, estimatedTokens, 0, 0m);
        var reserved = await repository.TryReserveAsync(
            reservation,
            windowStart,
            usage => paidQuota is { } tokenLimit
                ? usage.Tokens + estimatedTokens <= tokenLimit
                : usage.Requests < options.FreeDailyRequests,
            cancellationToken);

        return reserved ? reservation : throw new AiQuotaExceededException(windowStart.AddDays(1));
    }
}
