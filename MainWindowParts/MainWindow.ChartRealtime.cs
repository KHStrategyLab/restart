#nullable disable

using KHStrategyLab.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private const int MinuteChartRealtimeDrawIntervalMs = 500;
        private DateTime _lastMinuteChartRealtimeDrawAt = DateTime.MinValue;

        private bool TryApplyRealtimeTradeSnapshotToMinuteChart(RealtimeTradeSnapshot snapshot, string code)
        {
            try
            {
                if (snapshot == null || snapshot.CurrentPrice <= 0)
                    return false;

                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(() => TryApplyRealtimeTradeSnapshotToMinuteChart(snapshot, code)));
                    return true;
                }

                string baseCode = NormalizeStockCode(code);
                if (string.IsNullOrWhiteSpace(baseCode))
                    return false;

                ChartCandle latest = null;

                lock (_dailyChartLock)
                {
                    if (_lastDailyChartIsIndex)
                        return false;

                    if (!IsMinuteChartIntervalLabel(_lastChartIntervalLabel))
                        return false;

                    if (!string.Equals(NormalizeStockCode(_lastDailyChartCode), baseCode, StringComparison.OrdinalIgnoreCase))
                        return false;

                    if (!IsRealtimeSnapshotForCurrentChartMarket(snapshot, _lastChartMarketLabel))
                        return false;

                    int intervalMinutes = ResolveMinuteChartIntervalMinutes(_lastChartIntervalLabel);
                    if (intervalMinutes <= 0)
                        return false;

                    List<ChartCandle> candles = _lastDailyChartCandles ?? [];

                    DateTime tradeTime = snapshot.TradeTime == default ? DateTime.Now : snapshot.TradeTime;
                    DateTime candleStart = AlignMinuteChartCandleStart(tradeTime, intervalMinutes);
                    string candleDate = candleStart.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                    string candleTime = candleStart.ToString("HHmmss", CultureInfo.InvariantCulture);

                    ChartCandle current = candles.LastOrDefault(x => x != null && x.Close > 0);
                    DateTime currentStart = ParseMinuteCandleDateTime(current);
                    bool isNewCandle = current == null || currentStart != candleStart;

                    if (isNewCandle)
                    {
                        current = new ChartCandle
                        {
                            Date = candleDate,
                            Time = candleTime,
                            Open = snapshot.CurrentPrice,
                            High = snapshot.CurrentPrice,
                            Low = snapshot.CurrentPrice,
                            Close = snapshot.CurrentPrice,
                            Volume = Math.Max(0, snapshot.TradeQuantity),
                            TradingValue = snapshot.TradeQuantity > 0 ? snapshot.CurrentPrice * snapshot.TradeQuantity : 0
                        };
                        candles.Add(current);
                    }
                    else
                    {
                        current.Close = snapshot.CurrentPrice;
                        if (current.Open <= 0) current.Open = snapshot.CurrentPrice;
                        current.High = Math.Max(current.High > 0 ? current.High : snapshot.CurrentPrice, snapshot.CurrentPrice);
                        current.Low = Math.Min(current.Low > 0 ? current.Low : snapshot.CurrentPrice, snapshot.CurrentPrice);

                        if (snapshot.TradeQuantity > 0)
                        {
                            current.Volume += snapshot.TradeQuantity;
                            current.TradingValue += snapshot.CurrentPrice * snapshot.TradeQuantity;
                        }
                    }

                    if (candles.Count > MinuteChartStoreCandleCount)
                        candles = [.. candles.TakeLast(MinuteChartStoreCandleCount)];

                    DateTime now = DateTime.Now;
                    bool shouldDraw = isNewCandle ||
                        (now - _lastMinuteChartRealtimeDrawAt).TotalMilliseconds >= MinuteChartRealtimeDrawIntervalMs;

                    if (shouldDraw)
                    {
                        _lastMinuteChartRealtimeDrawAt = now;
                        FillMinuteChartMovingAveragesForStrategy(candles);
                    }

                    _lastDailyChartCandles = candles;
                    latest = candles.LastOrDefault();

                    if (!shouldDraw)
                        return true;
                }

                if (latest == null)
                    return false;

                SetTextBlock("TxtChartFooterPrice", latest.Close > 0 ? latest.Close.ToString("N0") : "---");
                SetTextBlock("TxtChartFooterCurrentPrice", snapshot.CurrentPrice > 0 ? snapshot.CurrentPrice.ToString("N0") : (latest.Close > 0 ? latest.Close.ToString("N0") : "---"));
                ApplyChartFooterPriceColor(ResolveChartPriceBrush(baseCode, isIndexChart: false));
                SetTextBlock("TxtChartFooterVolume", latest.Volume > 0 ? latest.Volume.ToString("N0") : "---");
                SetTextBlock("TxtChartFooterValue", latest.TradingValue > 0 ? FormatKoreanMoney(latest.TradingValue) : "---");
                SetTextBlock("TxtChartFooterTurnover", ResolveChartTurnoverText(baseCode));

                DrawDailyChartCanvas();
                return true;
            }
            catch (Exception ex)
            {
                Log($"⚠️ [차트 실시간 갱신 오류] {code} / {ex.Message}");
                return false;
            }
        }

        private bool IsMinuteChartIntervalLabel(string intervalLabel)
        {
            return (intervalLabel ?? "").EndsWith("분봉", StringComparison.OrdinalIgnoreCase);
        }

        private int ResolveMinuteChartIntervalMinutes(string intervalLabel)
        {
            string digits = NumberOnlyRegex().Replace(intervalLabel ?? "", "");
            return int.TryParse(digits, out int minutes) ? minutes : 0;
        }

        private bool IsRealtimeSnapshotForCurrentChartMarket(RealtimeTradeSnapshot snapshot, string chartMarket)
        {
            string market = (chartMarket ?? "").Trim().ToUpperInvariant();

            if (market == "NXT")
                return snapshot.IsNxtSnapshot;

            if (market == "KRX")
                return !snapshot.IsNxtSnapshot;

            return true;
        }

        private DateTime AlignMinuteChartCandleStart(DateTime tradeTime, int intervalMinutes)
        {
            if (tradeTime == default)
                tradeTime = DateTime.Now;

            int minute = (tradeTime.Minute / intervalMinutes) * intervalMinutes;
            return new DateTime(tradeTime.Year, tradeTime.Month, tradeTime.Day, tradeTime.Hour, minute, 0);
        }
    }
}
