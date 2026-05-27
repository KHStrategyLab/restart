namespace KHStrategyLab.Models;

public sealed class AccumulationCandleSetup
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime CandleTime { get; set; }
    public int Minute { get; set; } = 5;

    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long Volume { get; set; }
    public decimal Amount { get; set; }

    public bool BreakUsed { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime ExpireAt { get; set; } = DateTime.Now.Date.AddDays(7);

    public bool IsActive(DateTime now)
    {
        return !BreakUsed && now <= ExpireAt;
    }
}
