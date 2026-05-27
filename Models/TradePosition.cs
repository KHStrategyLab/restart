namespace KHStrategyLab.Models;

public sealed class TradePosition
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsHolding { get; set; }
    public int Quantity { get; set; }
    public decimal AvgPrice { get; set; }
    public EntrySource EntrySource { get; set; } = EntrySource.Unknown;
    public string EntryStrategyName { get; set; } = string.Empty;
    public DateTime? EntryTime { get; set; }

    public bool IsProgramManaged =>
        IsHolding &&
        Quantity > 0 &&
        (EntrySource == EntrySource.Strategy || EntrySource == EntrySource.TelegramApproved);
}
