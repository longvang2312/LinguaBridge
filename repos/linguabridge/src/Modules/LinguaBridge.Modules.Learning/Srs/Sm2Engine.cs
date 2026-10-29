using LinguaBridge.Modules.Learning.Entities;

namespace LinguaBridge.Modules.Learning.Srs;

/// <param name="EaseFactor">Current ease factor of the card (2.5 for a new one).</param>
/// <param name="IntervalDays">Days the card was last scheduled ahead (0 for a new one).</param>
/// <param name="Repetitions">Consecutive successful reviews since the last lapse.</param>
/// <param name="Today">The review date; the next review is counted from it.</param>
public readonly record struct Sm2Input(double EaseFactor, int IntervalDays, int Repetitions, ReviewRating Rating, DateOnly Today);

public readonly record struct Sm2Result(double EaseFactor, int IntervalDays, int Repetitions, DateOnly NextReviewDate, SrsState State);

/// <summary>
/// The SM-2 spaced-repetition scheduler as a pure function: no clock, no storage, same input gives the same output.
/// <para>
/// Rating effects (the four-grade variant the product specifies):
/// Again resets the card (repetitions 0, 1 day, ease -0.20); Hard keeps the repetition count, lengthens the interval
/// by 1.2 and lowers ease by 0.15; Good steps 1 day, 6 days, then interval x ease; Easy raises ease by 0.15 and
/// lengthens the interval by ease x 1.3 (new ease), always beating what Good would have given.
/// </para>
/// <para>Ease never drops below 1.3. Intervals are whole days, at least 1 and at most <see cref="MaxIntervalDays"/>.</para>
/// </summary>
public static class Sm2Engine
{
    public const double MinEaseFactor = 1.3;

    public const double AgainEasePenalty = 0.20;
    public const double HardEasePenalty = 0.15;
    public const double EasyEaseBonus = 0.15;

    public const double HardIntervalMultiplier = 1.2;
    public const double EasyIntervalBonus = 1.3;

    public const int FirstIntervalDays = 1;
    public const int SecondIntervalDays = 6;

    /// <summary>Repetitions after which a card is no longer "Learning" (it has passed the 1-day and 6-day steps).</summary>
    public const int ReviewAfterRepetitions = 2;

    /// <summary>An interval of this many days or more marks the card as graduated (the usual "mature card" line).</summary>
    public const int GraduationIntervalDays = 21;

    /// <summary>Guards against ever-growing intervals (and DateOnly overflow) on cards that are always rated Easy.</summary>
    public const int MaxIntervalDays = 365;

    public static Sm2Result Calculate(Sm2Input input)
    {
        if (!Enum.IsDefined(input.Rating))
            throw new ArgumentOutOfRangeException(nameof(input), input.Rating, "Rating must be 1 (Again), 2 (Hard), 3 (Good) or 4 (Easy).");
        ArgumentOutOfRangeException.ThrowIfNegative(input.IntervalDays, nameof(input.IntervalDays));
        ArgumentOutOfRangeException.ThrowIfNegative(input.Repetitions, nameof(input.Repetitions));

        var ease = Math.Max(MinEaseFactor, input.EaseFactor);
        var previous = input.IntervalDays;

        double newEase;
        int repetitions;
        int interval;

        switch (input.Rating)
        {
            case ReviewRating.Again:
                newEase = LowerEase(ease, AgainEasePenalty);
                repetitions = 0;
                interval = FirstIntervalDays;
                break;

            case ReviewRating.Hard:
                newEase = LowerEase(ease, HardEasePenalty);
                repetitions = input.Repetitions;
                interval = RoundDays(previous * HardIntervalMultiplier);
                break;

            case ReviewRating.Good:
                newEase = ease;
                repetitions = input.Repetitions + 1;
                interval = GoodInterval(input.Repetitions, previous, ease);
                break;

            case ReviewRating.Easy:
                newEase = RoundEase(ease + EasyEaseBonus);
                repetitions = input.Repetitions + 1;
                interval = Math.Max(
                    RoundDays(previous * newEase * EasyIntervalBonus),
                    GoodInterval(input.Repetitions, previous, ease) + 1);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(input), input.Rating, null);
        }

        interval = Math.Clamp(interval, FirstIntervalDays, MaxIntervalDays);

        return new Sm2Result(newEase, interval, repetitions, input.Today.AddDays(interval), StateFor(repetitions, interval));
    }

    /// <summary>
    /// What a Good review gives. Never shorter than the current interval: the fixed 6-day step would otherwise
    /// shorten a card that an earlier Easy had already pushed further out.
    /// </summary>
    private static int GoodInterval(int repetitions, int previous, double ease) => repetitions switch
    {
        0 => FirstIntervalDays,
        1 => Math.Max(SecondIntervalDays, previous),
        _ => RoundDays(previous * ease),
    };

    private static SrsState StateFor(int repetitions, int interval)
    {
        if (repetitions == 0)
            return SrsState.Learning;
        if (interval >= GraduationIntervalDays)
            return SrsState.Graduated;
        return repetitions >= ReviewAfterRepetitions ? SrsState.Review : SrsState.Learning;
    }

    private static double LowerEase(double ease, double penalty) => Math.Max(MinEaseFactor, RoundEase(ease - penalty));

    /// <summary>Two decimals, so repeated +/-0.15 steps do not accumulate floating-point noise (2.3499999...).</summary>
    private static double RoundEase(double ease) => Math.Round(ease, 2, MidpointRounding.AwayFromZero);

    private static int RoundDays(double days) => (int)Math.Min(int.MaxValue, Math.Round(days, MidpointRounding.AwayFromZero));
}
