#nullable disable

using KHStrategyLab.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private readonly Dictionary<string, CandidateMinuteCache> _candidateMinuteCaches = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _candidateMinuteCacheLoadingKeys = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastMinuteCacheWaitLogAt = DateTime.MinValue;
        private DateTime _lastMinuteCacheUseLogAt = DateTime.MinValue;
        private DateTime _lastMarketUnresolvedBlockLogAt = DateTime.MinValue;

        private string NormalizeMinuteCacheMarket(string market)
        {
            return string.Equals(market, "NXT", StringComparison.OrdinalIgnoreCase) ? "NXT" : "KRX";
        }

        private bool IsKnownMinuteCacheMarket(string market)
        {
            string normalized = (market ?? "").Trim().ToUpperInvariant();
            return normalized == "KRX" || normalized == "NXT";
        }

        private string BuildMinuteCacheKey(string code, string market)
        {
            return $"{NormalizeStockCode(code)}|{NormalizeMinuteCacheMarket(market)}";
        }

        private CandidateMinuteCache GetCandidateMinuteCache(string code, string market)
        {
            string baseCode = NormalizeStockCode(code);
            string normalizedMarket = NormalizeMinuteCacheMarket(market);
            string key = BuildMinuteCacheKey(baseCode, normalizedMarket);

            if (_candidateMinuteCaches.TryGetValue(key, out CandidateMinuteCache cache)) return cache;

            cache = new CandidateMinuteCache
            {
                Code = baseCode,
                Market = normalizedMarket,
                LoadStatus = "WAIT_MINUTE_LOAD"
            };
            _candidateMinuteCaches[key] = cache;
            return cache;
        }

        private bool TryGetReadyCandidateMinuteCache(string code, string market, out CandidateMinuteCache cache)
        {
            if (!IsKnownMinuteCacheMarket(market))
            {
                cache = null;
                LogMarketUnresolvedStrategyBlocked(code);
                return false;
            }

            cache = GetCandidateMinuteCache(code, market);
            return cache.IsSeedReady
                && cache.TenMinuteCompletedCandles.Count >= BuySignalTenMinuteMaLong
                && cache.FiveMinuteCompletedCandles.Count >= BuySignalFiveMinuteBreakoutLookback;
        }

        private void QueueLoadCandidateMinuteCache(string code, string market, string reason = "UNKNOWN")
        {
            string baseCode = NormalizeStockCode(code);
            if (string.IsNullOrWhiteSpace(baseCode) || string.IsNullOrWhiteSpace(_token)) return;
            if (!IsKnownMinuteCacheMarket(market))
            {
                LogMarketUnresolvedStrategyBlocked(baseCode);
                return;
            }

            string normalizedMarket = NormalizeMinuteCacheMarket(market);
            string key = BuildMinuteCacheKey(baseCode, normalizedMarket);
            CandidateMinuteCache cache = GetCandidateMinuteCache(baseCode, normalizedMarket);

            if (_candidateMinuteCacheLoadingKeys.Contains(key)) return;
            if (cache.LastLoadAttemptAt != DateTime.MinValue &&
                (DateTime.Now - cache.LastLoadAttemptAt).TotalSeconds < 5)
            {
                return;
            }

            cache.LastLoadAttemptAt = DateTime.Now;
            cache.LoadStatus = "LOADING";
            _candidateMinuteCacheLoadingKeys.Add(key);
            _ = LoadCandidateMinuteCacheAsync(baseCode, normalizedMarket, key, reason);
        }

        private async Task<CandidateMinuteCache> EnsureCandidateMinuteCacheLoadedAsync(string code, string market, string reason = "UNKNOWN", bool force = false)
        {
            string baseCode = NormalizeStockCode(code);
            if (!IsKnownMinuteCacheMarket(market))
            {
                LogMarketUnresolvedStrategyBlocked(baseCode);
                return new CandidateMinuteCache
                {
                    Code = baseCode,
                    Market = "PENDING",
                    LoadStatus = "MARKET_PENDING"
                };
            }

            string normalizedMarket = NormalizeMinuteCacheMarket(market);
            string key = BuildMinuteCacheKey(baseCode, normalizedMarket);
            CandidateMinuteCache cache = GetCandidateMinuteCache(baseCode, normalizedMarket);

            if (!force && cache.IsSeedReady) return cache;
            if (!force && _candidateMinuteCacheLoadingKeys.Contains(key)) return cache;
            if (!force &&
                cache.LastLoadAttemptAt != DateTime.MinValue &&
                (DateTime.Now - cache.LastLoadAttemptAt).TotalSeconds < 5)
            {
                return cache;
            }

            cache.LastLoadAttemptAt = DateTime.Now;
            cache.LoadStatus = "LOADING";
            _candidateMinuteCacheLoadingKeys.Add(key);

            try
            {
                return await LoadCandidateMinuteCacheAsync(baseCode, normalizedMarket, key, reason);
            }
            finally
            {
                _candidateMinuteCacheLoadingKeys.Remove(key);
            }
        }

        private async Task<CandidateMinuteCache> LoadCandidateMinuteCacheAsync(string code, string market, string key, string reason)
        {
            CandidateMinuteCache cache = GetCandidateMinuteCache(code, market);

            try
            {
                StrategyMinuteChartBundle tenBundle = await RequestMinuteChartCandlesForStrategyAsync(code, 10, market);
                StrategyMinuteChartBundle fiveBundle = await RequestMinuteChartCandlesForStrategyAsync(code, 5, market);

                cache.RequestCode10m = tenBundle?.RequestCode ?? "";
                cache.RequestCode5m = fiveBundle?.RequestCode ?? "";

                List<ChartCandle> tenCandles = tenBundle?.Candles ?? [];
                List<ChartCandle> fiveCandles = fiveBundle?.Candles ?? [];
                DateTime now = DateTime.Now;

                SplitMinuteCandles(tenCandles, 10, now, out List<ChartCandle> tenCompleted, out ChartCandle tenCurrent);
                SplitMinuteCandles(fiveCandles, 5, now, out List<ChartCandle> fiveCompleted, out ChartCandle fiveCurrent);

                if (tenCompleted.Count < LeadingTenMinuteMaSeedCount && tenCandles.Count >= LeadingTenMinuteMaSeedCount)
                {
                    tenCompleted = [.. tenCandles
                        .Where(x => x != null && x.Close > 0)
                        .OrderBy(ParseMinuteCandleDateTime)
                        .TakeLast(LeadingTenMinuteCompletedSeedCount)];
                    tenCurrent = null;
                }

                cache.TenMinuteCompletedCandles = tenCompleted;
                cache.TenMinuteCurrentCandle = tenCurrent;
                cache.FiveMinuteCompletedCandles = fiveCompleted;
                cache.FiveMinuteCurrentCandle = fiveCurrent;

                RecalculateCandidateMinuteCache(cache);

                bool ready = cache.TenMinuteCompletedCandles.Count >= LeadingTenMinuteMaSeedCount - 1 &&
                             cache.FiveMinuteCompletedCandles.Count >= BuySignalFiveMinuteBreakoutLookback;

                cache.IsSeedReady = ready;
                cache.LoadStatus = ready ? "READY" : "LOAD_FAILED";
                cache.LoadedAt = ready ? DateTime.Now : cache.LoadedAt;

                if (ready)
                {
                    Log($"✅ [MinuteCache] 초기 로드 완료: {cache.Code} / 시장={cache.Market} / 10분={cache.RequestCode10m} / 5분={cache.RequestCode5m}");
                }
                else
                {
                    Log($"⚠️ [MinuteCache] 로드 실패: {cache.Code} / 시장={cache.Market} / 10분={tenCandles.Count}개 / 5분={fiveCandles.Count}개 / 사유={reason}");
                }
            }
            catch (Exception ex)
            {
                cache.IsSeedReady = false;
                cache.LoadStatus = "LOAD_FAILED";
                Log($"⚠️ [MinuteCache] 로드 오류: {code} / 시장={market} / {ex.Message}");
            }
            finally
            {
                _candidateMinuteCacheLoadingKeys.Remove(key);
            }

            return cache;
        }

        private void SplitMinuteCandles(
            List<ChartCandle> source,
            int tickScope,
            DateTime now,
            out List<ChartCandle> completed,
            out ChartCandle current)
        {
            List<ChartCandle> ordered = [.. (source ?? [])
                .Where(x => x != null && x.Close > 0)
                .OrderBy(ParseMinuteCandleDateTime)];

            completed = [.. ordered.Where(x => !IsCurrentOrFutureMinuteCandleForStrategy(x, tickScope, now))];
            current = ordered.Where(x => IsCurrentOrFutureMinuteCandleForStrategy(x, tickScope, now)).LastOrDefault();
        }

        private void RecalculateCandidateMinuteCache(CandidateMinuteCache cache)
        {
            if (cache == null) return;

            cache.TenMinuteCompletedCandles = [.. (cache.TenMinuteCompletedCandles ?? [])
                .Where(x => x != null && x.Close > 0)
                .GroupBy(BuildMinuteCandleKey)
                .Select(g => g.First())
                .OrderBy(ParseMinuteCandleDateTime)];

            cache.FiveMinuteCompletedCandles = [.. (cache.FiveMinuteCompletedCandles ?? [])
                .Where(x => x != null && x.Close > 0)
                .GroupBy(BuildMinuteCandleKey)
                .Select(g => g.First())
                .OrderBy(ParseMinuteCandleDateTime)];

            FillMinuteChartMovingAveragesForStrategy(cache.TenMinuteCompletedCandles);
            FillMinuteChartMovingAveragesForStrategy(cache.FiveMinuteCompletedCandles);

            cache.TenMinuteCompletedCloses = [.. cache.TenMinuteCompletedCandles.Select(x => x.Close).Where(x => x > 0)];
            cache.FiveMinuteCompletedCloses = [.. cache.FiveMinuteCompletedCandles.Select(x => x.Close).Where(x => x > 0)];
            cache.FiveMinuteCompletedHighs = [.. cache.FiveMinuteCompletedCandles.Select(x => x.High).Where(x => x > 0)];

            List<long> tenDisplayCloses = [.. cache.TenMinuteCompletedCloses.TakeLast(LeadingTenMinuteMaSeedCount - 1)];
            long currentClose = cache.TenMinuteCurrentCandle?.Close ?? 0;
            if (currentClose > 0) tenDisplayCloses.Add(currentClose);
            else if (cache.TenMinuteCompletedCloses.Count >= LeadingTenMinuteMaSeedCount)
                tenDisplayCloses = [.. cache.TenMinuteCompletedCloses.TakeLast(LeadingTenMinuteMaSeedCount)];

            cache.Ma5_10m = AverageLast(tenDisplayCloses, 5);
            cache.Ma20_10m = AverageLast(tenDisplayCloses, 20);
            cache.Ma60_10m = AverageLast(tenDisplayCloses, 60);
            cache.PrevMa5_10m = AverageLast([.. cache.TenMinuteCompletedCloses.TakeLast(LeadingTenMinuteMaSeedCount)], 5);
            cache.PrevMa20_10m = AverageLast([.. cache.TenMinuteCompletedCloses.TakeLast(LeadingTenMinuteMaSeedCount)], 20);
            cache.Ma20_5m = AverageLast([.. cache.FiveMinuteCompletedCloses.TakeLast(BuySignalFiveMinuteBreakoutLookback)], BuySignalFiveMinuteBreakoutLookback);
            cache.High20_5m = cache.FiveMinuteCompletedHighs
                .TakeLast(LeadingFiveMinuteHighSeedCount)
                .DefaultIfEmpty(0)
                .Max();
        }

        private void ApplyRealtimeTickToCandidateMinuteCache(string code, string market, long currentPrice)
        {
            string baseCode = NormalizeStockCode(code);
            if (string.IsNullOrWhiteSpace(baseCode) || currentPrice <= 0) return;

            CandidateMinuteCache cache = GetCandidateMinuteCache(baseCode, market);
            if (!cache.IsSeedReady) return;

            DateTime now = DateTime.Now;
            cache.LastRealtimeAt = now;

            UpdateRealtimeMinuteCandle(cache, tickScope: 10, currentPrice, now);
            UpdateRealtimeMinuteCandle(cache, tickScope: 5, currentPrice, now);
            RecalculateCandidateMinuteCache(cache);
        }

        private void UpdateRealtimeMinuteCandle(CandidateMinuteCache cache, int tickScope, long currentPrice, DateTime now)
        {
            DateTime bucket = FloorMinuteTimeForStrategy(now, tickScope);
            string date = bucket.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string time = bucket.ToString("HHmmss", CultureInfo.InvariantCulture);

            List<ChartCandle> completed = tickScope == 10
                ? cache.TenMinuteCompletedCandles
                : cache.FiveMinuteCompletedCandles;

            ChartCandle current = tickScope == 10
                ? cache.TenMinuteCurrentCandle
                : cache.FiveMinuteCurrentCandle;

            DateTime currentTime = ParseMinuteCandleDateTime(current);
            if (current != null && currentTime != DateTime.MinValue && currentTime < bucket)
            {
                completed.Add(current);
                current = null;
            }

            if (current == null || ParseMinuteCandleDateTime(current) != bucket)
            {
                current = new ChartCandle
                {
                    Date = date,
                    Time = time,
                    Open = currentPrice,
                    High = currentPrice,
                    Low = currentPrice,
                    Close = currentPrice
                };
            }
            else
            {
                if (current.Open <= 0) current.Open = currentPrice;
                current.High = Math.Max(current.High > 0 ? current.High : currentPrice, currentPrice);
                current.Low = current.Low > 0 ? Math.Min(current.Low, currentPrice) : currentPrice;
                current.Close = currentPrice;
            }

            if (tickScope == 10) cache.TenMinuteCurrentCandle = current;
            else cache.FiveMinuteCurrentCandle = current;
        }

        private void LogStrategyMinuteCacheNotReady(string code, string market, CandidateMinuteCache cache)
        {
            if ((DateTime.Now - _lastMinuteCacheWaitLogAt).TotalSeconds < 30) return;
            _lastMinuteCacheWaitLogAt = DateTime.Now;

            string status = string.IsNullOrWhiteSpace(cache?.LoadStatus) ? "WAIT_MINUTE_LOAD" : cache.LoadStatus;
            Log($"⏸ [전략점검] 분봉 캐시 미준비: {code} / 시장={market} / 상태={status} / 매수판단 차단");
        }

        private void LogStrategyMinuteCacheUsed(string name, string code, string market, string stage)
        {
            if ((DateTime.Now - _lastMinuteCacheUseLogAt).TotalMinutes < 5) return;
            _lastMinuteCacheUseLogAt = DateTime.Now;

            Log($"🧠 [전략점검] 캐시 사용: {name}({code}) / 시장={market} / Stage={stage}");
        }

        private void LogMarketUnresolvedStrategyBlocked(string code)
        {
            if ((DateTime.Now - _lastMarketUnresolvedBlockLogAt).TotalSeconds < 30) return;
            _lastMarketUnresolvedBlockLogAt = DateTime.Now;

            Log($"⏸ [전략점검] 시장미확정: {NormalizeStockCode(code)} / MinuteCache 로드 보류 / 매수판단 차단");
        }
    }
}
