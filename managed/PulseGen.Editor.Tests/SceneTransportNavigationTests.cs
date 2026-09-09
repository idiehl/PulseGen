using Xunit;

namespace PulseGen.Editor.Tests;

public sealed class SceneTransportNavigationTests
{
    [Theory]
    [InlineData(-2, 6, 0)]
    [InlineData(12, 6, 6)]
    [InlineData(1.25, 6, 1.25)]
    [InlineData(double.NaN, 6, 0)]
    [InlineData(1, double.PositiveInfinity, 0)]
    public void Seeking_is_bounded_and_finite(double time, double duration, double expected) =>
        Assert.Equal(expected, SceneTransportNavigation.Clamp(time, duration));

    [Fact]
    public void Play_at_end_rewinds_but_pause_position_is_preserved()
    {
        Assert.Equal(0, SceneTransportNavigation.PlayStart(6, 6));
        Assert.Equal(2.75, SceneTransportNavigation.PlayStart(2.75, 6));
        Assert.Equal(0, SceneTransportNavigation.PlayStart(double.NaN, 6));
    }

    [Fact]
    public void One_second_controls_clamp_to_both_ends()
    {
        Assert.Equal(0, SceneTransportNavigation.Step(0.5, 6, -1));
        Assert.Equal(6, SceneTransportNavigation.Step(5.5, 6, 1));
        Assert.Equal(3.25, SceneTransportNavigation.Step(2.25, 6, 1));
    }
}
