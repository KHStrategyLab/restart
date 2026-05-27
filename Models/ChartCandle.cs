#nullable disable

namespace KHStrategyLab.Models
{
    public class ChartCandle
    {
        public string Date { get; set; } = "";
        public string Time { get; set; } = "";
        public long Open { get; set; }
        public long High { get; set; }
        public long Low { get; set; }
        public long Close { get; set; }
        public long Volume { get; set; }
        public long TradingValue { get; set; }
        public double MA5 { get; set; }
        public double MA10 { get; set; }
        public double MA20 { get; set; }
        public double MA60 { get; set; }
        public double MA200 { get; set; }
        public double MA480 { get; set; }
    }
}
