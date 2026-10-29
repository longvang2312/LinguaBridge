using FluentValidation;
using LinguaBridge.Application.Abstractions;
using LinguaBridge.Modules.Billing.Abstractions;
using LinguaBridge.Modules.Billing.Entities;
using LinguaBridge.Modules.Billing.Webhooks;

namespace LinguaBridge.Modules.Billing.Commands;

/// <param name="Id">A readable code such as <c>vip_monthly</c>: lower-case letters, digits and underscores.</param>
public sealed record CreatePlanCommand(string Id, string Name, decimal PriceVnd, int DurationDays, int AiDailyTokenQuota);

public sealed class CreatePlanValidator : AbstractValidator<CreatePlanCommand>
{
    public CreatePlanValidator()
    {
        RuleFor(c => c.Id)
            .Must(PlanCodes.IsValid)
            .WithMessage($"The plan id must be 1-{SubscriptionPlan.IdMaxLength} characters: lower-case letters, digits and underscores.");
        RuleFor(c => c.Name).NotEmpty().MaximumLength(SubscriptionPlan.NameMaxLength);
        RuleFor(c => c.PriceVnd)
            .GreaterThanOrEqualTo(0)
            .Must(price => decimal.Truncate(price) == price).WithMessage("VND prices have no fractional part.");
        RuleFor(c => c.DurationDays).GreaterThan(0);
        RuleFor(c => c.AiDailyTokenQuota).GreaterThanOrEqualTo(0);
    }
}

public sealed class CreatePlanHandler(IBillingRepository repository, IValidator<CreatePlanCommand> validator)
{
    public async Task<Result<PlanDto>> HandleAsync(CreatePlanCommand command, CancellationToken cancellationToken = default)
    {
        if (await validator.CheckAsync(command, cancellationToken) is { } invalid)
            return invalid;

        if (await repository.GetPlanAsync(command.Id, cancellationToken) is not null)
            return Error.Conflict("plan.already_exists", $"A plan with id '{command.Id}' already exists.");

        var plan = new SubscriptionPlan(command.Id, command.Name.Trim(), command.PriceVnd, command.DurationDays, command.AiDailyTokenQuota);
        repository.Add(plan);

        try
        {
            await repository.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            // Another request created the same id between the check and the save.
            return Error.Conflict("plan.already_exists", $"A plan with id '{command.Id}' already exists.");
        }

        return plan.ToDto();
    }
}

internal static class BillingSupport
{
    /// <summary>Runs the validator; returns the failure to hand back, or null when the command is valid.</summary>
    public static async Task<Error?> CheckAsync<T>(this IValidator<T> validator, T command, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(command, cancellationToken);
        if (validation.IsValid)
            return null;

        var details = validation.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());
        return Error.Validation(details);
    }

    public static PlanDto ToDto(this SubscriptionPlan plan) => new(plan.Id, plan.Name, plan.PriceVnd, plan.DurationDays, plan.AiDailyTokenQuota);
}
