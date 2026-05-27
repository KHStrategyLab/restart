namespace KHStrategyLab.Models;

public sealed class SavedBaseCandleCandidate
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime BaseDate { get; set; }

    public decimal PrevClose { get; set; }
    public decimal BaseHigh { get; set; }
    public decimal BaseLow { get; set; }
    public decimal BaseWaist { get; set; }
    public decimal RiseRateFromPrevClose { get; set; }
    public decimal Amount { get; set; }

    public bool IsNxtAvailable { get; set; }
    public bool IsUsed { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime ExpireAt { get; set; } = DateTime.Now.Date.AddDays(7);

    public bool IsValidNow(DateTime now)
    {
        return !IsUsed && now.Date <= ExpireAt.Date;
    }
}
