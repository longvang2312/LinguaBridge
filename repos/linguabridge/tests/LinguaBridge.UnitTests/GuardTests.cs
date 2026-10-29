using LinguaBridge.Domain.Common;

namespace LinguaBridge.UnitTests;

public class GuardTests
{
    [Fact]
    public void Required_trims_and_rejects_blank_or_too_long()
    {
        Assert.Equal("abc", Guard.Required("  abc ", 3, "value"));
        Assert.Throws<ArgumentException>(() => Guard.Required("   ", 3, "value"));
        Assert.Throws<ArgumentException>(() => Guard.Required("abcd", 3, "value"));
    }

    [Fact]
    public void Optional_turns_blank_into_null()
    {
        Assert.Null(Guard.Optional(" ", 3, "value"));
        Assert.Throws<ArgumentException>(() => Guard.Optional("abcd", 3, "value"));
    }

    [Fact]
    public void RequiredId_rejects_empty_guid()
    {
        Assert.Throws<ArgumentException>(() => Guard.RequiredId(Guid.Empty, "id"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100)]
    public void Range_accepts_bounds_inclusively(int value)
    {
        Assert.Equal(value, Guard.Range(value, 0, 100, "score"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Range_rejects_values_outside(int value)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Guard.Range(value, 0, 100, "score"));
        Assert.Equal("score", ex.ParamName);
    }

    [Fact]
    public void Range_rejects_inverted_bounds()
    {
        Assert.Throws<ArgumentException>(() => Guard.Range(5, 10, 0, "score"));
    }
}
