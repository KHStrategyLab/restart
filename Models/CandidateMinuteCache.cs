#nullable disable

using System;
using System.Collections.Generic;

namespace KHStrategyLab.Models
{
    public sealed class CandidateMinuteCache
    {
        public string Code { get; set; } = "";
        public string Market { get; set; } = "KRX";
        public string RequestCode10m { get; set; } = "";
        public string RequestCode5m { get; set; } = "";
        public bool IsSeedReady { get; set; }
        public string LoadStatus { get; set; } = "WAIT_MINUTE_LOAD";
        public DateTime LoadedAt { get; set; }
        public DateTime LastLoadAttemptAt { get; set; }
        public DateTime LastRealtimeAt { get; set; }
        public List<ChartCandle> TenMinuteCompletedCandles { get; set; } = [];
        public ChartCandle TenMinuteCurrentCandle { get; set; }
        public List<ChartCandle> FiveMinuteCompletedCandles { get; set; } = [];
        public ChartCandle FiveMinuteCurrentCandle { get; set; }
        public List<long> TenMinuteCompletedCloses { get; set; } = [];
        public List<long> FiveMinuteCompletedCloses { get; set; } = [];
        public List<long> FiveMinuteCompletedHighs { get; set; } = [];
        public double Ma5_10m { get; set; }
        public double Ma20_10m { get; set; }
        public double Ma60_10m { get; set; }
        public double PrevMa5_10m { get; set; }
        public double PrevMa20_10m { get; set; }
        public double Ma20_5m { get; set; }
        public long High20_5m { get; set; }
    }
}
