using LinguaBridge.Application.Abstractions;
using LinguaBridge.Application.Messaging;
using LinguaBridge.Modules.Billing.Abstractions;
using LinguaBridge.Modules.Billing.Entities;
using LinguaBridge.Modules.Billing.Events;
using LinguaBridge.Modules.Billing.Webhooks;
using Microsoft.Extensions.Logging;

namespace LinguaBridge.Modules.Billing.Commands;

/// <param name="Provider">As named in the URL, any casing.</param>
public sealed record ProcessWebhookCommand(string Provider, string Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// Applies a payment webhook exactly once.
/// <list type="number">
/// <item>The signature is checked before anything is read or stored; a bad one is refused and leaves no trace.</item>
/// <item>The learner is locked, then the inbox is consulted: an event already processed is acknowledged and not applied again.</item>
/// <item>Entitlement change and inbox row are saved in <b>one</b> transaction. There is no moment where the payment is
/// recorded but not applied (or applied but not recorded), so a crash or a concurrent redelivery cannot double-apply it;
/// the unique (provider, event id) index stays as the last line of defence.</item>
/// <item>Only after the commit is <see cref="EntitlementUpgradedEvent"/> published. Delivery is best effort: a failing
/// subscriber is logged and does not undo or fail the payment.</item>
/// </list>
/// A verified payment that cannot be applied (unknown plan, wrong amount) is stored as Failed and reported as such; a
/// redelivery of it is tried again, so it heals once the cause (for example a missing plan) is fixed.
/// </summary>
public sealed class ProcessWebhookHandler(
    IEnumerable<IWebhookProvider> providers,
    IBillingRepository repository,
    IEventBus eventBus,
    IDateTimeProvider clock,
    ILogger<ProcessWebhookHandler> logger)
{
    /// <summary>The unique index on (provider, event id); a violation of it means "already received".</summary>
    public const string InboxUniqueIndex = "uq_provider_event";

    public async Task<Result<WebhookResultDto>> HandleAsync(ProcessWebhookCommand command, CancellationToken cancellationToken = default)
    {
        var provider = providers.FirstOrDefault(p => string.Equals(p.Name, command.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
            return Error.NotFound("webhook.provider_unsupported", $"Webhooks from '{command.Provider}' are not supported.");

        var verified = provider.Verify(new WebhookRequest(command.Body, command.Headers));
        if (!verified.IsSuccess)
            return verified.Error!;

        switch (verified.Value)
        {
            case IgnoredNotification ignored:
                logger.LogInformation("{Provider} webhook ignored: {Reason}", provider.Name, ignored.Reason);
                return new WebhookResultDto(WebhookStatuses.Ignored, ignored.Reason);

            case PaymentConfirmed payment:
                return await ApplyAsync(provider.Name, payment, command.Body, cancellationToken);

            default:
                throw new InvalidOperationException($"Unknown notification type {verified.Value.GetType().Name}.");
        }
    }

    private async Task<Result<WebhookResultDto>> ApplyAsync(string provider, PaymentConfirmed payment, string body, CancellationToken cancellationToken)
    {
        EntitlementUpgradedEvent? upgraded;
        WebhookResultDto result;

        await using (var transaction = await repository.BeginAsync(payment.UserId, cancellationToken))
        {
            var inbox = await repository.GetInboxAsync(provider, payment.EventId, cancellationToken);
            if (inbox?.Status == WebhookStatus.Processed)
                return new WebhookResultDto(WebhookStatuses.Duplicate);

            if (inbox is null)
            {
                inbox = new WebhookInbox(provider, payment.EventId, body);
                repository.Add(inbox);
            }

            (upgraded, var failure) = await TryActivateAsync(payment, cancellationToken);
            if (failure is null)
            {
                inbox.MarkProcessed();
                result = new WebhookResultDto(WebhookStatuses.Processed);
            }
            else
            {
                inbox.MarkFailed(failure);
                result = new WebhookResultDto(WebhookStatuses.Failed, failure);
                logger.LogWarning("{Provider} payment {EventId} for {UserId} could not be applied: {Reason}", provider, payment.EventId, payment.UserId, failure);
            }

            try
            {
                await repository.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (UniqueConstraintViolationException ex) when (ex.ConstraintName == InboxUniqueIndex)
            {
                // Someone else stored this very event first: it is being, or has been, applied by them.
                return new WebhookResultDto(WebhookStatuses.Duplicate);
            }
            catch (UniqueConstraintViolationException)
            {
                return Error.Conflict("request.concurrent_update", "The same data was changed by another request at the same moment. Please retry.");
            }
        }

        if (upgraded is not null)
            await PublishAsync(upgraded, cancellationToken);

        return result;
    }

    /// <summary>Activates the plan; returns the event to publish, or the reason the payment cannot be applied.</summary>
    private async Task<(EntitlementUpgradedEvent? Upgraded, string? Failure)> TryActivateAsync(PaymentConfirmed payment, CancellationToken cancellationToken)
    {
        var plan = await repository.GetPlanAsync(payment.PlanId, cancellationToken);
        if (plan is null)
            return (null, $"Plan '{payment.PlanId}' does not exist.");

        if (payment.AmountVnd != plan.PriceVnd)
            return (null, $"The amount paid ({payment.AmountVnd?.ToString("0") ?? "unknown"} VND) does not match the price of plan '{plan.Id}' ({plan.PriceVnd:0} VND).");

        var entitlement = await repository.GetEntitlementAsync(payment.UserId, cancellationToken);
        if (entitlement is null)
        {
            entitlement = new UserEntitlement(payment.UserId);
            repository.Add(entitlement);
        }

        entitlement.ActivatePlan(plan);

        return (new EntitlementUpgradedEvent(Guid.NewGuid(), clock.UtcNow, payment.UserId, plan.Id, entitlement.ExpiresAt!.Value), null);
    }

    private async Task PublishAsync(EntitlementUpgradedEvent upgraded, CancellationToken cancellationToken)
    {
        try
        {
            await eventBus.PublishAsync(upgraded, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Publishing EntitlementUpgradedEvent for {UserId} failed; the plan is already active.", upgraded.UserId);
        }
    }
}
