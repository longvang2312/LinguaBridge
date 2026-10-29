using LinguaBridge.Modules.Learning.Entities;
using LinguaBridge.Modules.Learning.Srs;

namespace LinguaBridge.UnitTests.Learning;

public class Sm2EngineTests
{
    private static readonly DateOnly Today = new(2026, 10, 29);

    private static Sm2Result Calc(double ease, int interval, int repetitions, ReviewRating rating, DateOnly? today = null) =>
        Sm2Engine.Calculate(new Sm2Input(ease, interval, repetitions, rating, today ?? Today));

    // ---- Good: 1 day, 6 days, then interval x ease ---------------------------------------------------------

    [Fact]
    public void Good_on_a_new_card_schedules_one_day()
    {
        var result = Calc(2.5, 0, 0, ReviewRating.Good);

        Assert.Equal(1, result.IntervalDays);
        Assert.Equal(1, result.Repetitions);
        Assert.Equal(Today.AddDays(1), result.NextReviewDate);
        Assert.Equal(SrsState.Learning, result.State);
    }

    [Fact]
    public void Good_on_the_second_success_schedules_six_days()
    {
        var result = Calc(2.5, 1, 1, ReviewRating.Good);

        Assert.Equal(6, result.IntervalDays);
        Assert.Equal(2, result.Repetitions);
        Assert.Equal(SrsState.Review, result.State);
    }

    [Fact]
    public void Good_after_that_multiplies_the_previous_interval_by_the_ease_factor()
    {
        var result = Calc(2.5, 6, 2, ReviewRating.Good);

        Assert.Equal(15, result.IntervalDays);
        Assert.Equal(3, result.Repetitions);
        Assert.Equal(SrsState.Review, result.State);
    }

    [Fact]
    public void Good_rounds_half_days_up()
    {
        // 3 x 2.5 = 7.5
        Assert.Equal(8, Calc(2.5, 3, 2, ReviewRating.Good).IntervalDays);
    }

    [Theory]
    [InlineData(1.3)]
    [InlineData(2.0)]
    [InlineData(2.5)]
    [InlineData(3.1)]
    public void Good_leaves_the_ease_factor_alone(double ease)
    {
        Assert.Equal(ease, Calc(ease, 6, 2, ReviewRating.Good).EaseFactor);
    }

    [Fact]
    public void Good_never_shortens_a_card_an_earlier_easy_pushed_further_out()
    {
        // Second success normally means 6 days, but this card is already scheduled 7 days ahead.
        Assert.Equal(7, Calc(2.5, 7, 1, ReviewRating.Good).IntervalDays);
    }

    // ---- Again: reset ---------------------------------------------------------------------------------------

    [Fact]
    public void Again_resets_repetitions_and_schedules_one_day()
    {
        var result = Calc(2.5, 15, 3, ReviewRating.Again);

        Assert.Equal(0, result.Repetitions);
        Assert.Equal(1, result.IntervalDays);
        Assert.Equal(Today.AddDays(1), result.NextReviewDate);
        Assert.Equal(SrsState.Learning, result.State);
    }

    [Fact]
    public void Again_lowers_the_ease_factor_by_point_two()
    {
        Assert.Equal(2.3, Calc(2.5, 6, 2, ReviewRating.Again).EaseFactor);
    }

    [Fact]
    public void Again_sends_a_graduated_card_back_to_learning()
    {
        var result = Calc(2.5, 120, 8, ReviewRating.Again);

        Assert.Equal(SrsState.Learning, result.State);
        Assert.Equal(1, result.IntervalDays);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(5, 40)]
    [InlineData(20, 365)]
    public void Again_always_resets_whatever_the_history(int repetitions, int interval)
    {
        var result = Calc(2.5, interval, repetitions, ReviewRating.Again);

        Assert.Equal(0, result.Repetitions);
        Assert.Equal(1, result.IntervalDays);
    }

    // ---- Hard ---------------------------------------------------------------------------------------------

    [Fact]
    public void Hard_lowers_ease_by_point_one_five_and_lengthens_the_interval_by_a_fifth()
    {
        var result = Calc(2.5, 10, 3, ReviewRating.Hard);

        Assert.Equal(2.35, result.EaseFactor);
        Assert.Equal(12, result.IntervalDays);
        Assert.Equal(SrsState.Review, result.State);
    }

    [Fact]
    public void Hard_does_not_count_as_a_repetition()
    {
        Assert.Equal(3, Calc(2.5, 10, 3, ReviewRating.Hard).Repetitions);
        Assert.Equal(0, Calc(2.5, 0, 0, ReviewRating.Hard).Repetitions);
    }

    [Fact]
    public void Hard_on_a_new_card_still_schedules_at_least_one_day()
    {
        var result = Calc(2.5, 0, 0, ReviewRating.Hard);

        Assert.Equal(1, result.IntervalDays);
        Assert.Equal(Today.AddDays(1), result.NextReviewDate);
        Assert.Equal(SrsState.Learning, result.State);
    }

    [Fact]
    public void Hard_on_a_one_day_interval_stays_at_one_day()
    {
        Assert.Equal(1, Calc(2.5, 1, 1, ReviewRating.Hard).IntervalDays);
    }

    // ---- Easy ---------------------------------------------------------------------------------------------

    [Fact]
    public void Easy_raises_ease_by_point_one_five_and_uses_the_new_ease_with_a_bonus()
    {
        var result = Calc(2.5, 15, 3, ReviewRating.Easy);

        Assert.Equal(2.65, result.EaseFactor);
        Assert.Equal(52, result.IntervalDays); // 15 x 2.65 x 1.3 = 51.675
        Assert.Equal(4, result.Repetitions);
        Assert.Equal(SrsState.Graduated, result.State);
    }

    [Fact]
    public void Easy_on_a_new_card_beats_good_by_a_day()
    {
        var easy = Calc(2.5, 0, 0, ReviewRating.Easy);

        Assert.Equal(2, easy.IntervalDays);
        Assert.Equal(1, easy.Repetitions);
        Assert.Equal(SrsState.Learning, easy.State);
    }

    [Fact]
    public void Easy_on_the_second_success_beats_the_six_day_step()
    {
        var easy = Calc(2.5, 1, 1, ReviewRating.Easy);

        Assert.Equal(7, easy.IntervalDays);
        Assert.Equal(SrsState.Review, easy.State);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 6)]
    [InlineData(3, 15)]
    [InlineData(6, 90)]
    public void Easy_is_always_longer_than_good_and_good_longer_than_hard(int repetitions, int interval)
    {
        var hard = Calc(2.5, interval, repetitions, ReviewRating.Hard).IntervalDays;
        var good = Calc(2.5, interval, repetitions, ReviewRating.Good).IntervalDays;
        var easy = Calc(2.5, interval, repetitions, ReviewRating.Easy).IntervalDays;

        Assert.True(easy > good, $"easy {easy} should beat good {good}");
        Assert.True(good >= hard, $"good {good} should not be shorter than hard {hard}");
    }

    // ---- Ease factor limits -------------------------------------------------------------------------------

    [Theory]
    [InlineData(ReviewRating.Again, 1.4)]
    [InlineData(ReviewRating.Again, 1.3)]
    [InlineData(ReviewRating.Hard, 1.35)]
    [InlineData(ReviewRating.Hard, 1.3)]
    public void Ease_factor_never_drops_below_one_point_three(ReviewRating rating, double ease)
    {
        Assert.Equal(1.3, Calc(ease, 6, 2, rating).EaseFactor);
    }

    [Theory]
    [InlineData(ReviewRating.Again)]
    [InlineData(ReviewRating.Hard)]
    [InlineData(ReviewRating.Good)]
    [InlineData(ReviewRating.Easy)]
    public void An_ease_factor_stored_below_the_floor_is_treated_as_the_floor(ReviewRating rating)
    {
        Assert.True(Calc(1.0, 6, 2, rating).EaseFactor >= Sm2Engine.MinEaseFactor);
    }

    [Fact]
    public void Repeated_steps_do_not_accumulate_floating_point_noise()
    {
        var ease = 2.5;
        var eases = new List<double>();
        for (var i = 0; i < 3; i++)
        {
            ease = Calc(ease, 10, 3, ReviewRating.Hard).EaseFactor;
            eases.Add(ease);
        }

        Assert.Equal([2.35, 2.2, 2.05], eases);

        Assert.Equal(2.8, Calc(Calc(2.5, 10, 3, ReviewRating.Easy).EaseFactor, 10, 3, ReviewRating.Easy).EaseFactor);
    }

    // ---- State: Learning, Review, Graduated -----------------------------------------------------------------

    [Fact]
    public void A_card_moves_from_learning_to_review_to_graduated_with_consecutive_good_answers()
    {
        var ease = 2.5;
        var interval = 0;
        var repetitions = 0;
        var states = new List<SrsState>();
        var intervals = new List<int>();

        for (var i = 0; i < 4; i++)
        {
            var result = Calc(ease, interval, repetitions, ReviewRating.Good);
            (ease, interval, repetitions) = (result.EaseFactor, result.IntervalDays, result.Repetitions);
            states.Add(result.State);
            intervals.Add(result.IntervalDays);
        }

        Assert.Equal([1, 6, 15, 38], intervals);
        Assert.Equal([SrsState.Learning, SrsState.Review, SrsState.Review, SrsState.Graduated], states);
    }

    [Fact]
    public void A_lapse_after_graduating_means_starting_the_steps_again()
    {
        var graduated = Calc(2.5, 15, 3, ReviewRating.Good);
        Assert.Equal(SrsState.Graduated, graduated.State);

        var lapsed = Calc(graduated.EaseFactor, graduated.IntervalDays, graduated.Repetitions, ReviewRating.Again);
        var first = Calc(lapsed.EaseFactor, lapsed.IntervalDays, lapsed.Repetitions, ReviewRating.Good);
        var second = Calc(first.EaseFactor, first.IntervalDays, first.Repetitions, ReviewRating.Good);

        Assert.Equal(SrsState.Learning, lapsed.State);
        Assert.Equal(1, first.IntervalDays);
        Assert.Equal(6, second.IntervalDays);
        Assert.Equal(SrsState.Review, second.State);
    }

    [Theory]
    [InlineData(2.1, 9, 19, SrsState.Review)]      // 18.9 days: just short of graduating
    [InlineData(2.1, 10, 21, SrsState.Graduated)]  // exactly 21 days graduates
    [InlineData(2.5, 8, 20, SrsState.Review)]
    [InlineData(2.5, 9, 23, SrsState.Graduated)]
    public void Graduation_happens_at_twenty_one_days(double ease, int previous, int expectedInterval, SrsState expectedState)
    {
        var result = Calc(ease, previous, 2, ReviewRating.Good);

        Assert.Equal(expectedInterval, result.IntervalDays);
        Assert.Equal(expectedState, result.State);
    }

    [Fact]
    public void A_hard_answer_can_graduate_a_card_by_pushing_it_past_three_weeks()
    {
        Assert.Equal(SrsState.Graduated, Calc(2.5, 20, 5, ReviewRating.Hard).State);
    }

    // ---- Limits and dates ---------------------------------------------------------------------------------

    [Fact]
    public void Intervals_are_capped_at_a_year()
    {
        var result = Calc(2.5, 300, 5, ReviewRating.Good);

        Assert.Equal(Sm2Engine.MaxIntervalDays, result.IntervalDays);
        Assert.Equal(Today.AddDays(365), result.NextReviewDate);
    }

    [Fact]
    public void An_always_easy_card_cannot_overflow_or_run_past_the_cap()
    {
        var ease = 2.5;
        var interval = 0;
        var repetitions = 0;

        for (var i = 0; i < 200; i++)
        {
            var result = Calc(ease, interval, repetitions, ReviewRating.Easy);
            (ease, interval, repetitions) = (result.EaseFactor, result.IntervalDays, result.Repetitions);
        }

        Assert.Equal(Sm2Engine.MaxIntervalDays, interval);
    }

    [Fact]
    public void The_next_review_date_is_counted_from_the_review_date_across_month_and_year_ends()
    {
        var result = Calc(2.5, 1, 1, ReviewRating.Good, new DateOnly(2026, 12, 30));

        Assert.Equal(new DateOnly(2027, 1, 5), result.NextReviewDate);
    }

    [Theory]
    [InlineData(ReviewRating.Again)]
    [InlineData(ReviewRating.Hard)]
    [InlineData(ReviewRating.Good)]
    [InlineData(ReviewRating.Easy)]
    public void The_interval_is_always_at_least_one_day(ReviewRating rating)
    {
        Assert.True(Calc(2.5, 0, 0, rating).IntervalDays >= 1);
    }

    // ---- Purity and input checks ------------------------------------------------------------------------

    [Fact]
    public void The_same_input_always_gives_the_same_result()
    {
        var input = new Sm2Input(2.35, 12, 4, ReviewRating.Easy, Today);

        Assert.Equal(Sm2Engine.Calculate(input), Sm2Engine.Calculate(input));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void An_unknown_rating_is_rejected(int rating)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Calc(2.5, 1, 1, (ReviewRating)rating));
    }

    [Fact]
    public void Negative_history_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Calc(2.5, -1, 0, ReviewRating.Good));
        Assert.Throws<ArgumentOutOfRangeException>(() => Calc(2.5, 1, -1, ReviewRating.Good));
    }

    // ---- The card applies the result ----------------------------------------------------------------------

    [Fact]
    public void A_card_stores_the_outcome_of_a_calculation()
    {
        var card = new UserVocabularyProgress(Guid.NewGuid(), Guid.NewGuid(), LinguaBridge.Domain.Enums.TargetLanguage.English, Today);
        var reviewedAt = new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

        var result = Calc(card.EaseFactor, card.IntervalDays, card.Repetitions, ReviewRating.Good);
        card.ApplyReview(result, reviewedAt);

        Assert.Equal(1, card.IntervalDays);
        Assert.Equal(1, card.Repetitions);
        Assert.Equal(2.5, card.EaseFactor);
        Assert.Equal(Today.AddDays(1), card.NextReviewDate);
        Assert.Equal(SrsState.Learning, card.State);
        Assert.Equal(reviewedAt, card.LastReviewDate);
    }

    [Fact]
    public void A_card_is_due_on_and_after_its_review_date_but_not_before()
    {
        var card = new UserVocabularyProgress(Guid.NewGuid(), Guid.NewGuid(), LinguaBridge.Domain.Enums.TargetLanguage.English, Today);

        Assert.False(card.IsDueOn(Today.AddDays(-1)));
        Assert.True(card.IsDueOn(Today));
        Assert.True(card.IsDueOn(Today.AddDays(30)));
    }
}
