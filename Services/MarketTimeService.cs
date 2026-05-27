namespace KHStrategyLab.Services;

public sealed class MarketTimeService
{
    public bool IsRegularMarketTime(DateTime now)
    {
        var t = now.TimeOfDay;
        return t >= new TimeSpan(9, 0, 0) && t <= new TimeSpan(15, 30, 0);
    }

    public bool IsNxtEarlyTime(DateTime now)
    {
        var t = now.TimeOfDay;
        return t >= new TimeSpan(8, 0, 0) && t < new TimeSpan(9, 0, 0);
    }

    public bool IsAfterMarketTime(DateTime now)
    {
        var t = now.TimeOfDay;
        return t > new TimeSpan(15, 30, 0) && t <= new TimeSpan(20, 0, 0);
    }
}
