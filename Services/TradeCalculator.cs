using KHStrategyLab.Models;

namespace KHStrategyLab.Services;

public static class TradeCalculator
{
    public static decimal? Average(IReadOnlyList<Candle> candles, int index, int period, Func<Candle, decimal> selector)
    {
        if (period <= 0) return null;
        if (index < period - 1) return null;

        decimal sum = 0m;
        for (var i = index - period + 1; i <= index; i++)
            sum += selector(candles[i]);

        return sum / period;
    }

    public static decimal? HighestClose(IReadOnlyList<Candle> candles, int index, int lookback, int offset = 1)
    {
        var end = index - offset;
        var start = end - lookback + 1;
        if (lookback <= 0 || start < 0 || end < 0) return null;

        decimal max = candles[start].Close;
        for (var i = start + 1; i <= end; i++)
            if (candles[i].Close > max) max = candles[i].Close;

        return max;
    }

    public static bool CrossUp(decimal prevValue, decimal prevLine, decimal value, decimal line)
    {
        return prevValue <= prevLine && value > line;
    }

    public static (decimal target, decimal bandLow, decimal bandHigh) CalculateTargetBand(
        decimal baseLow,
        decimal baseHigh,
        decimal bandPercent = 2m)
    {
        var move = baseHigh - baseLow;
        var target = baseHigh + move;
        var bandLow = target * (1m - bandPercent / 100m);
        var bandHigh = target * (1m + bandPercent / 100m);
        return (target, bandLow, bandHigh);
    }

    public static decimal Waist(decimal low, decimal high)
    {
        return (low + high) / 2m;
    }
}
