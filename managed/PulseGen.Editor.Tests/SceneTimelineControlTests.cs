using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Input;
using PulseGen.Contracts;
using Xunit;

namespace PulseGen.Editor.Tests;

public sealed class SceneTimelineControlTests
{
    [Theory]
    [InlineData(0, 16)]
    [InlineData(3, 500)]
    [InlineData(6, 984)]
    [InlineData(-8, 16)]
    [InlineData(25, 984)]
    public void TimeAndPixelCoordinatesUseTheSameInsetTrack(double seconds, double expectedX)
    {
        var x = SceneTimelineLayout.TimeToX(seconds, 6, 1000);
        Assert.Equal(expectedX, x, 10);
        Assert.Equal(Math.Clamp(seconds, 0, 6), SceneTimelineLayout.XToTime(x, 6, 1000), 10);
    }

    [Theory]
    [InlineData(-400, 0)]
    [InlineData(16, 0)]
    [InlineData(984, 6)]
    [InlineData(2000, 6)]
    public void DraggingPastEitherEdgeClampsToTheTrack(double x, double expected)
    {
        Assert.Equal(expected, SceneTimelineLayout.XToTime(x, 6, 1000));
    }

    [Fact]
    public void InvalidDurationAndLayoutNeverProduceNonFiniteTimeOrCoordinates()
    {
        foreach (var duration in new[] { 0.0, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Equal(0, SceneTimelineLayout.XToTime(400, duration, 1000));
            Assert.True(double.IsFinite(SceneTimelineLayout.TimeToX(4, duration, 1000)));
            Assert.Empty(SceneTimelineLayout.CreateTicks(duration, 1000));
        }
        foreach (var width in new[] { 0.0, -1, 16, 32, double.NaN, double.PositiveInfinity })
        {
            Assert.Equal(0, SceneTimelineLayout.XToTime(400, 6, width));
            Assert.True(double.IsFinite(SceneTimelineLayout.TimeToX(4, 6, width)));
            Assert.Empty(SceneTimelineLayout.CreateTicks(6, width));
        }
        Assert.Equal(0, SceneTimelineLayout.XToTime(double.NaN, 6, 1000));
        Assert.Equal(0, SceneTimelineLayout.ClampTime(double.NaN, 6));
        Assert.Equal(0, SceneTimelineLayout.ClampTime(double.PositiveInfinity, 6));
    }

    [Theory]
    [InlineData(0.0035, 1000)]
    [InlineData(6, 1000)]
    [InlineData(17.25, 800)]
    [InlineData(86400, 640)]
    [InlineData(1.0e308, 1000)]
    public void RulerTicksAreBoundedSortedAndIncludeExactEndpoints(double duration, double width)
    {
        var ticks = SceneTimelineLayout.CreateTicks(duration, width);
        Assert.InRange(ticks.Count, 2, 102);
        Assert.Equal(0, ticks[0].TimeSeconds);
        Assert.Equal(duration, ticks[^1].TimeSeconds);
        Assert.All(ticks, tick =>
        {
            Assert.True(double.IsFinite(tick.TimeSeconds));
            Assert.InRange(tick.TimeSeconds, 0, duration);
            Assert.EndsWith(" s", tick.Label);
        });
        for (var index = 1; index < ticks.Count; index++)
        {
            Assert.True(ticks[index].TimeSeconds > ticks[index - 1].TimeSeconds);
        }
    }

    [Fact]
    public void NarrowRulerDoesNotCrowdTheEndpointLabels()
    {
        var ticks = SceneTimelineLayout.CreateTicks(6, 100);
        Assert.Single(ticks);
        Assert.Equal(0, ticks[0].TimeSeconds);
    }

    [Theory]
    [InlineData(Key.Home, KeyModifiers.None, 2.5, 0)]
    [InlineData(Key.End, KeyModifiers.None, 2.5, 6)]
    [InlineData(Key.Left, KeyModifiers.None, 2.5, 2.4)]
    [InlineData(Key.Right, KeyModifiers.None, 2.5, 2.6)]
    [InlineData(Key.Left, KeyModifiers.Shift, 2.5, 1.5)]
    [InlineData(Key.Right, KeyModifiers.Shift, 2.5, 3.5)]
    [InlineData(Key.Left, KeyModifiers.None, 0, 0)]
    [InlineData(Key.Right, KeyModifiers.Shift, 5.9, 6)]
    public void KeyboardSeekingHasFineAndCoarseStepsAndExplicitEndpoints(Key key, KeyModifiers modifiers, double current, double expected)
    {
        Assert.True(SceneTimelineLayout.TrySeekKey(key, modifiers, current, 6, out var target));
        Assert.Equal(expected, target, 10);
    }

    [Theory]
    [InlineData(Key.Space, KeyModifiers.None)]
    [InlineData(Key.Right, KeyModifiers.Control)]
    [InlineData(Key.Home, KeyModifiers.Alt)]
    [InlineData(Key.End, KeyModifiers.Meta)]
    public void UnrelatedKeyboardShortcutsAreNotConsumed(Key key, KeyModifiers modifiers)
    {
        Assert.False(SceneTimelineLayout.TrySeekKey(key, modifiers, 2, 6, out _));
    }

    [Fact]
    public void ScrubGestureRaisesOneStartAndCompletionWithoutEditingScene()
    {
        var scene = SampleScenes.CreateFlyby();
        var initial = ProjectStore.SerializeManifest(scene);
        var timeline = new SceneTimelineControl { Scene = scene };
        var events = new List<string>();
        var requested = new List<double>();
        timeline.SeekStarted += (_, _) => events.Add("start");
        timeline.SeekRequested += (_, seconds) =>
        {
            events.Add("seek");
            requested.Add(seconds);
        };
        timeline.SeekCompleted += (_, _) => events.Add("complete");

        Assert.True(timeline.BeginSeekAt(500, 1000));
        Assert.False(timeline.BeginSeekAt(600, 1000));
        timeline.ContinueSeekAt(2000, 1000);
        timeline.CompleteSeek();
        timeline.CompleteSeek();
        timeline.ContinueSeekAt(16, 1000);

        Assert.Equal(new[] { "start", "seek", "seek", "complete" }, events);
        Assert.Equal(new[] { 3.0, 6.0 }, requested);
        Assert.Equal(6, timeline.TimeSeconds);
        Assert.False(timeline.IsSeeking);
        Assert.Equal(initial, ProjectStore.SerializeManifest(scene));
    }

    [Fact]
    public void PassiveTimeUpdatesDoNotSeekOrStopPlayback()
    {
        var timeline = new SceneTimelineControl { Scene = SampleScenes.CreateFlyby() };
        var eventCount = 0;
        timeline.SeekStarted += (_, _) => eventCount++;
        timeline.SeekRequested += (_, _) => eventCount++;
        timeline.SeekCompleted += (_, _) => eventCount++;

        timeline.TimeSeconds = 3;
        timeline.TimeSeconds = 10;
        Assert.Equal(6, timeline.TimeSeconds);
        timeline.TimeSeconds = double.NaN;

        Assert.Equal(0, timeline.TimeSeconds);
        Assert.Equal(0, eventCount);
    }

    [Fact]
    public void KeyboardGestureCompletesAndSceneReplacementCancelsAnActiveDrag()
    {
        var timeline = new SceneTimelineControl { Scene = SampleScenes.CreateFlyby() };
        var starts = 0;
        var completions = 0;
        timeline.SeekStarted += (_, _) => starts++;
        timeline.SeekCompleted += (_, _) => completions++;
        Assert.True(timeline.SeekByKey(Key.End, KeyModifiers.None));
        Assert.Equal(6, timeline.TimeSeconds);
        Assert.Equal(1, starts);
        Assert.Equal(1, completions);

        Assert.True(timeline.BeginSeekAt(500, 1000));
        Assert.False(timeline.SeekByKey(Key.Home, KeyModifiers.None));
        timeline.Scene = null;

        Assert.False(timeline.IsSeeking);
        Assert.Equal(2, starts);
        Assert.Equal(2, completions);
        Assert.Equal(0, timeline.TimeSeconds);
        Assert.False(timeline.BeginSeekAt(500, 1000));
        Assert.False(timeline.SeekByKey(Key.Home, KeyModifiers.None));
    }

    [Fact]
    public void TimelineExposesItsNameAndAControllableTimeRangeForAssistiveTechnology()
    {
        var timeline = new SceneTimelineControl { Scene = SampleScenes.CreateFlyby() };
        Assert.True(timeline.Focusable);
        Assert.Equal("Scene timeline, seconds", AutomationProperties.GetName(timeline));
        var peer = ControlAutomationPeer.CreatePeerForElement(timeline);
        var range = Assert.IsAssignableFrom<IRangeValueProvider>(peer);
        Assert.Equal(0, range.Minimum);
        Assert.Equal(6, range.Maximum);
        Assert.Equal(0.1, range.SmallChange);
        range.SetValue(2.5);
        Assert.Equal(2.5, timeline.TimeSeconds);
        timeline.IsEnabled = false;
        Assert.True(range.IsReadOnly);
        range.SetValue(4);
        Assert.Equal(2.5, timeline.TimeSeconds);
        Assert.False(timeline.BeginSeekAt(500, 1000));
    }
}
