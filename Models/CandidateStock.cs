namespace KHStrategyLab.Models;

public sealed class CandidateStock
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public MarketKind Market { get; set; } = MarketKind.Unknown;
    public bool IsNxtAvailable { get; set; }

    public string SourceConditionNo { get; set; } = "00";
    public DateTime FirstSeenAt { get; set; } = DateTime.Now;
    public DateTime LastSeenAt { get; set; } = DateTime.Now;

    public decimal CurrentPrice { get; set; }
    public decimal PrevClose { get; set; }
    public decimal DayHigh { get; set; }
    public decimal DayLow { get; set; }
    public long DayVolume { get; set; }
    public decimal DayAmount { get; set; }

    public bool IsBaseCandleCandidate { get; set; }
    public DateTime? BaseCandleDate { get; set; }
    public decimal BaseCandleHigh { get; set; }
    public decimal BaseCandleLow { get; set; }
    public decimal BaseCandleWaist { get; set; }

    public CandidateStatus Status { get; set; } = CandidateStatus.Watching;
    public string Memo { get; set; } = string.Empty;

    public string Key => Code;
}
