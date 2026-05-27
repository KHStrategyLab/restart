#nullable disable

namespace KHStrategyLab.Models
{
    public sealed class MinuteBar
    {
        public string Date { get; set; } = "";
        public string Time { get; set; } = "";
        public long Open { get; set; }
        public long High { get; set; }
        public long Low { get; set; }
        public long Close { get; set; }
        public long Volume { get; set; }
        public long TradingValue { get; set; }
    }
}
