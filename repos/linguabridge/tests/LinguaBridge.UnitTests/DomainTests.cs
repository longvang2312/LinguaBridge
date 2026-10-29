using LinguaBridge.Domain.Common;
using LinguaBridge.Domain.Enums;

namespace LinguaBridge.UnitTests;

public class DomainTests
{
    [Fact]
    public void Entities_with_same_type_and_id_are_equal()
    {
        var id = Guid.NewGuid();

        Assert.Equal(new SampleAggregate(id), new SampleAggregate(id));
        Assert.NotEqual(new SampleAggregate(Guid.NewGuid()), new SampleAggregate(Guid.NewGuid()));
    }

    [Fact]
    public void Entity_rejects_empty_id()
    {
        Assert.Throws<ArgumentException>(() => new SampleAggregate(Guid.Empty));
    }

    [Fact]
    public void Aggregate_collects_and_clears_domain_events()
    {
        var aggregate = new SampleAggregate(Guid.NewGuid());

        aggregate.DoSomething();
        Assert.Single(aggregate.DomainEvents);

        aggregate.ClearDomainEvents();
        Assert.Empty(aggregate.DomainEvents);
    }

    [Theory]
    [InlineData(TargetLanguage.English, "en")]
    [InlineData(TargetLanguage.Chinese, "zh")]
    public void Target_language_maps_to_iso_code(TargetLanguage language, string code)
    {
        Assert.Equal(code, language.ToCode());
    }

    [Theory]
    [InlineData(UiLocale.ViVn, "vi-VN")]
    [InlineData(UiLocale.ZhCn, "zh-CN")]
    public void Ui_locale_maps_to_culture_name(UiLocale locale, string code)
    {
        Assert.Equal(code, locale.ToCode());
    }

    [Theory]
    [InlineData(LanguageLevel.B2, TargetLanguage.English, true)]
    [InlineData(LanguageLevel.B2, TargetLanguage.Chinese, false)]
    [InlineData(LanguageLevel.Hsk3, TargetLanguage.Chinese, true)]
    [InlineData(LanguageLevel.Hsk3, TargetLanguage.English, false)]
    public void Level_scale_matches_target_language(LanguageLevel level, TargetLanguage language, bool expected)
    {
        Assert.Equal(expected, level.AppliesTo(language));
    }

    private sealed record SomethingHappened(DateTimeOffset OccurredAt) : IDomainEvent;

    private sealed class SampleAggregate(Guid id) : AggregateRoot(id)
    {
        public void DoSomething() => RaiseDomainEvent(new SomethingHappened(DateTimeOffset.UtcNow));
    }
}
