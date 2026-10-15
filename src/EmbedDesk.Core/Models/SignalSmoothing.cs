namespace EmbedDesk.Core.Models;

public enum SignalSmoothingMode { None, MovingAverage, Exponential }

/// <summary>Causal display filters. Input samples are never modified.</summary>
public static class SignalSmoothing
{
    public static double[] Apply(ReadOnlySpan<double> values, SignalSmoothingMode mode, int window = 5, double alpha = .3)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (window is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(window));
        if (!double.IsFinite(alpha) || alpha is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(alpha));
        var result = values.ToArray();
        if (mode == SignalSmoothingMode.None || values.IsEmpty) return result;
        double sum = 0, previous = values[0];
        for (var index = 0; index < values.Length; index++)
        {
            if (mode == SignalSmoothingMode.MovingAverage)
            {
                sum += values[index];
                if (index >= window) sum -= values[index - window];
                result[index] = sum / Math.Min(index + 1, window);
            }
            else
            {
                if (index != 0) previous += alpha * (values[index] - previous);
                result[index] = previous;
            }
        }
        return result;
    }
}
