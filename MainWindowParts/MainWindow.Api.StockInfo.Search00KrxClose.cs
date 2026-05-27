#nullable disable

using KHStrategyLab.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private DateTime _lastSearch00KrxCloseRefreshAt = DateTime.MinValue;
        private bool _search00KrxCloseRefreshRunning = false;
        private readonly object _search00KrxCloseRefreshLock = new();
        private DateTime _lastSearch00NxtCloseRefreshAt = DateTime.MinValue;
        private bool _search00NxtCloseRefreshRunning = false;
        private readonly object _search00NxtCloseRefreshLock = new();

        private Task RefreshSearch00KrxClosePricesAsync(string source)
        {
            try
            {
                bool shouldStart = false;
                lock (_search00KrxCloseRefreshLock)
                {
                    DateTime now = DateTime.Now;
                    if (!_search00KrxCloseRefreshRunning && (now - _lastSearch00KrxCloseRefreshAt).TotalSeconds >= 60)
                    {
                        _search00KrxCloseRefreshRunning = true;
                        _lastSearch00KrxCloseRefreshAt = now;
                        shouldStart = true;
                    }
                }

                if (!shouldStart) return Task.CompletedTask;

                return Task.Run(async () =>
                {
                    int targetCount = 0;
                    int requested = 0;

                    try
                    {
                        List<string> codes = [];
                        bool useNxtClose = AccountShouldUseNxtCloseNow();

                        void CollectOnUi()
                        {
                            codes = [.. _search00List
                                .Where(x => x != null)
                                .Where(x => !string.IsNullOrWhiteSpace(x.Code))
                                .Where(x => string.Equals((x.VolumeText ?? "").Trim(), "조건00표시", StringComparison.OrdinalIgnoreCase)
                                            || string.IsNullOrWhiteSpace(x.VolumeText)
                                            || (x.VolumeText ?? "").Contains("조건00"))
                                .Where(x => !useNxtClose || !IsNxtStrategyCandidateCode(x.Code))
                                .Select(x => NormalizeStockCode(x.Code))
                                .Where(x => !string.IsNullOrWhiteSpace(x))
                                .Distinct()
                                .Take(80)];
                        }

                        if (Dispatcher.CheckAccess()) CollectOnUi();
                        else Dispatcher.Invoke(CollectOnUi);

                        targetCount = codes.Count;
                        if (targetCount == 0)
                        {
                            if (useNxtClose)
                                _ = RefreshSearch00NxtClosePricesAsync($"{source} / KRX대상없음");
                            return;
                        }

                        foreach (string code in codes)
                        {
                            await RefreshStockInfoAsync(code, "KRX");
                            requested++;
                            await Task.Delay(80);
                        }

                        Log($"💾 [조건00 KRX종가보정] 표시행 {targetCount}개 / 조회요청 {requested}개 / {source}");
                        if (useNxtClose)
                            _ = RefreshSearch00NxtClosePricesAsync(source);
                    }
                    catch (Exception ex)
                    {
                        Log($"⚠️ [조건00 KRX종가보정 오류] {ex.Message} / {source}");
                    }
                    finally
                    {
                        lock (_search00KrxCloseRefreshLock)
                        {
                            _search00KrxCloseRefreshRunning = false;
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Log($"⚠️ [조건00 KRX종가보정 시작 오류] {ex.Message} / {source}");
                return Task.CompletedTask;
            }
        }

        private Task RefreshSearch00NxtClosePricesAsync(string source)
        {
            try
            {
                if (!AccountShouldUseNxtCloseNow()) return Task.CompletedTask;

                bool shouldStart = false;
                lock (_search00NxtCloseRefreshLock)
                {
                    DateTime now = DateTime.Now;
                    if (!_search00NxtCloseRefreshRunning && (now - _lastSearch00NxtCloseRefreshAt).TotalSeconds >= 60)
                    {
                        _search00NxtCloseRefreshRunning = true;
                        _lastSearch00NxtCloseRefreshAt = now;
                        shouldStart = true;
                    }
                }

                if (!shouldStart) return Task.CompletedTask;

                return Task.Run(async () =>
                {
                    int targetCount = 0;
                    int applied = 0;

                    try
                    {
                        List<(string Code, string Name)> targets = [];

                        void CollectOnUi()
                        {
                            targets = [.. _search00List
                                .Where(x => x != null)
                                .Where(x => !string.IsNullOrWhiteSpace(x.Code))
                                .Where(x => IsNxtStrategyCandidateCode(x.Code))
                                .Select(x => (Code: NormalizeStockCode(x.Code), Name: x.Name ?? ""))
                                .Where(x => !string.IsNullOrWhiteSpace(x.Code))
                                .Distinct()
                                .Take(80)];
                        }

                        if (Dispatcher.CheckAccess()) CollectOnUi();
                        else Dispatcher.Invoke(CollectOnUi);

                        targetCount = targets.Count;
                        if (targetCount == 0) return;

                        foreach ((string code, string name) in targets)
                        {
                            ChartCandle candle = await FetchSearch00NxtCloseCandleAsync(code, name);
                            long close = candle?.Close ?? 0;
                            if (close <= 0)
                                close = await AccountFetchNxtClosePriceFromStockInfoAsync(code);

                            if (close <= 0)
                            {
                                Log($"⚠️ [조건00 NXT종가고정] NXT 종가 조회 실패: {name}({code}) / {source}");
                                await Task.Delay(120);
                                continue;
                            }

                            void ApplyOnUi()
                            {
                                HoldingStock row = _search00List.FirstOrDefault(x => NormalizeStockCode(x.Code) == code);
                                if (row == null) return;

                                row.CurrentPrice = close;
                                if (candle?.Volume > 0)
                                    row.VolumeText = candle.Volume.ToString("N0");
                                if (candle?.TradingValue > 0)
                                    row.TradingValueText = FormatKoreanMoney(candle.TradingValue);
                                row.TurnoverRateText = "NXT종가";

                                if (_watchCandidates.TryGetValue(code, out WatchCandidate candidate))
                                {
                                    candidate.LastPrice = close;
                                    candidate.LastSeen = DateTime.Now;
                                }

                            }

                            if (Dispatcher.CheckAccess()) ApplyOnUi();
                            else Dispatcher.Invoke(ApplyOnUi);

                            applied++;
                            await Task.Delay(120);
                        }

                        if (applied > 0)
                        {
                            SaveWatchCandidates();
                            Log($"💾 [조건00 NXT종가고정] 화면 NXT 종가 반영 완료: 적용 {applied}개 / 대상 {targetCount}개 / source={source}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"⚠️ [조건00 NXT종가고정 오류] {ex.Message} / {source}");
                    }
                    finally
                    {
                        lock (_search00NxtCloseRefreshLock)
                        {
                            _search00NxtCloseRefreshRunning = false;
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Log($"⚠️ [조건00 NXT종가고정 시작 오류] {ex.Message} / {source}");
                return Task.CompletedTask;
            }
        }

        private async Task<ChartCandle> FetchSearch00NxtCloseCandleAsync(string code, string name)
        {
            code = NormalizeStockCode(code);
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(_token)) return null;

            DailyChartRequestOption option = CreateStockDailyChartRequestOption(
                code,
                $"{code}_NX",
                "NXT(_NX)",
                DateTime.Now.ToString("yyyyMMdd"));

            DailyChartLoadResult result = await RequestDailyChartCandlesAsync(
                option,
                string.IsNullOrWhiteSpace(name) ? code : name);

            return result?.Candles?
                .Where(x => x != null && x.Close > 0)
                .OrderBy(x => x.Date)
                .LastOrDefault();
        }

        private bool IsNxtStrategyCandidateCode(string code)
        {
            code = NormalizeStockCode(code);
            if (string.IsNullOrWhiteSpace(code)) return false;
            if (!_watchCandidates.TryGetValue(code, out WatchCandidate candidate)) return false;
            return string.Equals((candidate.StrategyMarket ?? "").Trim(), "NXT", StringComparison.OrdinalIgnoreCase);
        }
    }
}
