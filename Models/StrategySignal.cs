namespace KHStrategyLab.Models;

public sealed class StrategySignal
{
    public StrategySignalType Type { get; set; } = StrategySignalType.None;
    public string StrategyName { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public decimal SignalPrice { get; set; }
    public decimal? StopLossPrice { get; set; }
    public decimal? TargetPrice { get; set; }
    public decimal? TargetBandLow { get; set; }
    public decimal? TargetBandHigh { get; set; }

    public string Reason { get; set; } = string.Empty;
    public string LockKey { get; set; } = string.Empty;
    public bool IsTriggered => Type != StrategySignalType.None;

    public static StrategySignal None(string strategyName, string code = "", string name = "")
    {
        return new StrategySignal
        {
            Type = StrategySignalType.None,
            StrategyName = strategyName,
            Code = code,
            Name = name,
            Reason = "조건 불충족"
        };
    }
}
