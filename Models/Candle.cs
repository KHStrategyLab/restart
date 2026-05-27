namespace KHStrategyLab.Models;

public sealed class Candle
{
    public DateTime Time { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long Volume { get; set; }
    public decimal Amount { get; set; }

    public bool IsBull => Close > Open;
    public bool IsBear => Close < Open;

    public decimal BodyRatePercent
    {
        get
        {
            if (Open <= 0) return 0m;
            return Math.Abs(Close - Open) / Open * 100m;
        }
    }

    public decimal RangeRatePercent
    {
        get
        {
            if (Open <= 0) return 0m;
            return (High - Low) / Open * 100m;
        }
    }

    public bool Touches(decimal price)
    {
        return Low <= price && price <= High;
    }
}
