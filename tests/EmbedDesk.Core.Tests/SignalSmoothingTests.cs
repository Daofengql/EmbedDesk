using EmbedDesk.Core.Models;
using Xunit;

namespace EmbedDesk.Core.Tests;

public sealed class SignalSmoothingTests
{
    [Fact]
    public void MovingAverageUsesTrailingWindowAndPreservesFractionsAndInput()
    {
        double[] input = [0, 1, 0, 5];
        Assert.Equal(new[] { 0d, .5, .5, 2.5 }, SignalSmoothing.Apply(input, SignalSmoothingMode.MovingAverage, 2));
        Assert.Equal(new[] { 0d, 1, 0, 5 }, input);
        Assert.Equal(new[] { 0d, .5, 1d / 3, 1.5 }, SignalSmoothing.Apply(input, SignalSmoothingMode.MovingAverage, 20));
    }

    [Fact]
    public void ExponentialUsesFirstSampleAsSeedAndExplicitAlpha()
    {
        double[] input = [2, 4, 0];
        Assert.Equal(new[] { 2d, 2.5, 1.875 }, SignalSmoothing.Apply(input, SignalSmoothingMode.Exponential, alpha: .25));
        Assert.Equal(input, SignalSmoothing.Apply(input, SignalSmoothingMode.Exponential, alpha: 1));
        Assert.Empty(SignalSmoothing.Apply([], SignalSmoothingMode.Exponential));
    }

    [Fact]
    public void FilterOffReturnsIndependentRawCopyAndRejectsInvalidSettings()
    {
        double[] input = [int.MaxValue, int.MinValue];
        var result = SignalSmoothing.Apply(input, SignalSmoothingMode.None);
        Assert.Equal(input, result); Assert.NotSame(input, result);
        Assert.Equal(-.5, SignalSmoothing.Apply(input, SignalSmoothingMode.MovingAverage, 2)[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => SignalSmoothing.Apply(input, SignalSmoothingMode.MovingAverage, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SignalSmoothing.Apply(input, SignalSmoothingMode.Exponential, alpha: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => SignalSmoothing.Apply(input, SignalSmoothingMode.Exponential, alpha: 0));
    }
}
