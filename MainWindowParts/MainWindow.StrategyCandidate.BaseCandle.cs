#nullable disable
using KHStrategyLab.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private static readonly TimeSpan StrategyCandidateBaseCandleSnapshotStartTime = new(15, 40, 0);
        private static readonly TimeSpan StrategyCandidateBaseCandleSnapshotCarryUntilTime = new(7, 0, 0);
        private static readonly TimeSpan StrategyCandidateEodFinalizeStartTime = new(16, 0, 0);
        private static readonly TimeSpan StrategyCandidateEodFinalizeCooldown = new(0, 15, 0);

        private sealed class StrategyCandidateBaseCandleSnapshot
        {
            public ChartCandle BaseCandle { get; set; }
            public ChartCandle PreviousCandle { get; set; }
            public long PreviousClose => PreviousCandle?.Close ?? 0;
            public string Market { get; set; } = "";
            public string RequestCode { get; set; } = "";
        }

        private void InitializeStrategyCandidateBaseCandleSnapshotTimer()
        {
            _strategyCandidateBaseCandleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _strategyCandidateBaseCandleTimer.Tick += async (s, e) =>
            {
                await RunStrategyCandidateBaseCandleSnapshotIfDueAsync();
            };
            _strategyCandidateBaseCandleTimer.Start();
        }

        private async Task RunStrategyCandidateBaseCandleSnapshotIfDueAsync(bool force = false)
        {
            if (_strategyCandidateBaseCandleSnapshotRunning) return;

            DateTime now = DateTime.Now;
            DateTime? targetTradeDate = ResolveStrategyCandidateBaseCandleSnapshotTradeDate(now);
            if (!force && targetTradeDate == null) return;

            DateTime tradeDate = targetTradeDate ?? now.Date;
            if (string.IsNullOrWhiteSpace(_token)) return;
            if (_watchCandidates.Count == 0) return;

            List<WatchCandidate> targets = [.. _watchCandidates.Values
                .Where(x => x != null)
                .Select(x => NormalizeStrategyCandidate(x, now))
                .Where(x => x != null)
                .Where(x => NeedsStrategyCandidateBaseCandleSnapshot(x, tradeDate))
                .GroupBy(x => NormalizeStockCode(x.Code))
                .Select(g => g.OrderBy(x => x.FirstSeen).First())];

            if (targets.Count == 0)
            {
                RunStrategyCandidateEodFinalizeIfDue(tradeDate, now, "NO_SNAPSHOT_TARGETS");
                return;
            }

            _strategyCandidateBaseCandleSnapshotRunning = true;
            try
            {
                int saved = 0;
                int failed = 0;
                var savedDetails = new List<string>();

                Log($"📌 [조건00 기준봉저장] 장마감 후 OHLC/전일종가 저장 시작: {targets.Count}개 / 기준일={tradeDate:yyyy-MM-dd} / 매수 기준가=전일종가");

                foreach (WatchCandidate candidate in targets)
                {
                    string code = NormalizeStockCode(candidate.Code);
                    if (string.IsNullOrWhiteSpace(code)) continue;

                    StrategyCandidateBaseCandleSnapshot snapshot = await FetchStrategyCandidateBaseCandleSnapshotAsync(candidate, tradeDate);
                    if (snapshot?.BaseCandle == null)
                    {
                        failed++;
                        await Task.Delay(120);
                        continue;
                    }

                    ApplyStrategyCandidateBaseCandleSnapshot(candidate, snapshot, now);

                    if (_watchCandidates.TryGetValue(code, out WatchCandidate stored))
                    {
                        ApplyStrategyCandidateBaseCandleSnapshot(stored, snapshot, now);
                    }

                    string market = string.IsNullOrWhiteSpace(snapshot.Market) ? "KRX" : snapshot.Market;
                    long prevClose = snapshot.PreviousClose;
                    double closeRate = prevClose > 0 && snapshot.BaseCandle.Close > 0
                        ? Math.Round(((snapshot.BaseCandle.Close - prevClose) / (double)prevClose) * 100.0, 4)
                        : 0;
                    string name = string.IsNullOrWhiteSpace(candidate.Name) ? code : candidate.Name;
                    savedDetails.Add(
                        $"🧩 [기준봉저장 상세] {name}({code}) / 시장={market} / 일자={NormalizeChartDate(snapshot.BaseCandle.Date)} / 시={snapshot.BaseCandle.Open:N0} 고={snapshot.BaseCandle.High:N0} 저={snapshot.BaseCandle.Low:N0} 종={snapshot.BaseCandle.Close:N0} / 전일종가={prevClose:N0} / 등락률={closeRate:0.####}%");

                    saved++;
                    await Task.Delay(120);
                }

                if (saved > 0)
                {
                    SaveWatchCandidates();
                }
                foreach (string detail in savedDetails)
                    Log(detail);

                Log($"✅ [조건00 기준봉저장] 완료: 저장 {saved}개 / 실패 {failed}개 / 매수 기준가=전일종가");
                RunStrategyCandidateEodFinalizeIfDue(tradeDate, now, "BASE_CANDLE_SAVED");
                await RunBaseCandleScoreIfDueAsync("BASE_CANDLE_SAVED");
            }
            catch (Exception ex)
            {
                Log($"❌ [조건00 기준봉저장 오류] {ex.Message}");
            }
            finally
            {
                _strategyCandidateBaseCandleSnapshotRunning = false;
            }
        }

        private DateTime? ResolveStrategyCandidateBaseCandleSnapshotTradeDate(DateTime now)
        {
            TimeSpan time = now.TimeOfDay;

            // 정규장 일봉이 완성된 뒤 저장한다.
            if (time >= StrategyCandidateBaseCandleSnapshotStartTime)
            {
                return ResolveLatestMarketOpenDateOnOrBefore(now.Date);
            }

            // 20시 이후 재실행/NXT 잔고 표시처럼, 새벽 07시 전까지는 전일 기준봉 저장 보완을 허용한다.
            if (time < StrategyCandidateBaseCandleSnapshotCarryUntilTime)
            {
                return ResolveLatestMarketOpenDateOnOrBefore(now.Date.AddDays(-1));
            }

            return null;
        }

        private DateTime ResolveLatestMarketOpenDateOnOrBefore(DateTime date)
        {
            DateTime cursor = date.Date;
            while (IsMarketClosedDate(cursor))
                cursor = cursor.AddDays(-1);

            return cursor;
        }

        private DateTime ResolveStrategyCandidateBaseCandleDate(WatchCandidate candidate, DateTime tradeDate)
        {
            DateTime firstSeenDate = candidate?.FirstSeen == default ? tradeDate.Date : candidate.FirstSeen.Date;
            if (firstSeenDate > tradeDate.Date || IsMarketClosedDate(firstSeenDate))
                return tradeDate.Date;

            return firstSeenDate;
        }

        private bool NeedsStrategyCandidateBaseCandleSnapshot(WatchCandidate candidate, DateTime tradeDate)
        {
            if (candidate == null) return false;
            string code = NormalizeStockCode(candidate.Code);
            if (string.IsNullOrWhiteSpace(code)) return false;

            DateTime firstSeenDate = ResolveStrategyCandidateBaseCandleDate(candidate, tradeDate);
            if (firstSeenDate > tradeDate.Date) return false;

            // 이미 기준봉과 전일종가가 저장되어 있으면 다시 덮어쓰지 않는다.
            if (!string.IsNullOrWhiteSpace(candidate.BaseCandleDate) &&
                candidate.BaseOpen > 0 && candidate.BaseHigh > 0 && candidate.BaseLow > 0 && candidate.BaseClose > 0 &&
                (candidate.PreviousClose > 0 || candidate.BasePrice > 0))
            {
                return false;
            }

            // 과거 저장분은 기준봉 OHLC는 있지만 PreviousClose/BasePrice가 없을 수 있다.
            // 이 경우 전일종가 필드만 보강할 수 있게 다시 조회한다.
            if (!string.IsNullOrWhiteSpace(candidate.BaseCandleDate) &&
                candidate.BaseOpen > 0 && candidate.BaseHigh > 0 && candidate.BaseLow > 0 && candidate.BaseClose > 0 &&
                candidate.PreviousClose <= 0 && candidate.BasePrice <= 0)
            {
                return true;
            }

            return true;
        }

        private async Task<StrategyCandidateBaseCandleSnapshot> FetchStrategyCandidateBaseCandleSnapshotAsync(WatchCandidate candidate, DateTime tradeDate)
        {
            string code = NormalizeStockCode(candidate.Code);
            if (string.IsNullOrWhiteSpace(code)) return null;

            DateTime candidateDate = ResolveStrategyCandidateBaseCandleDate(candidate, tradeDate);
            string candidateDateText = candidateDate.ToString("yyyyMMdd");

            // 조건00은 KRX 검색식 기준이므로 기준봉도 KRX 일봉으로 저장한다.
            DailyChartRequestOption selectedOption = CreateStockDailyChartRequestOption(
                displayCode: code,
                requestCode: code,
                marketLabel: "KRX",
                baseDate: tradeDate.ToString("yyyyMMdd"));

            DailyChartLoadResult result = await RequestDailyChartCandlesAsync(
                selectedOption,
                string.IsNullOrWhiteSpace(candidate.Name) ? code : candidate.Name);

            if (result?.Candles == null || result.Candles.Count == 0) return null;

            List<ChartCandle> ordered = [.. result.Candles
                .Where(x => x != null && !string.IsNullOrWhiteSpace(NormalizeChartDate(x.Date)))
                .OrderByDescending(x => NormalizeChartDate(x.Date))];

            if (ordered.Count == 0) return null;

            ChartCandle baseCandle = ordered.FirstOrDefault(x => NormalizeChartDate(x.Date) == candidateDateText);

            // 휴장/데이터 지연 등으로 정확한 날짜가 없으면 기준일 이전의 가장 가까운 일봉을 안전하게 사용한다.
            baseCandle ??= ordered
                .Where(x => string.Compare(NormalizeChartDate(x.Date), candidateDateText, StringComparison.Ordinal) <= 0)
                .OrderByDescending(x => NormalizeChartDate(x.Date))
                .FirstOrDefault();

            if (baseCandle == null) return null;

            string baseDateText = NormalizeChartDate(baseCandle.Date);
            ChartCandle previousCandle = ordered
                .Where(x => string.Compare(NormalizeChartDate(x.Date), baseDateText, StringComparison.Ordinal) < 0)
                .OrderByDescending(x => NormalizeChartDate(x.Date))
                .FirstOrDefault();

            return new StrategyCandidateBaseCandleSnapshot
            {
                BaseCandle = baseCandle,
                PreviousCandle = previousCandle,
                Market = "KRX",
                RequestCode = selectedOption?.RequestCode ?? code
            };
        }

        private void ApplyStrategyCandidateBaseCandleSnapshot(WatchCandidate candidate, StrategyCandidateBaseCandleSnapshot snapshot, DateTime now)
        {
            if (candidate == null || snapshot?.BaseCandle == null) return;

            ChartCandle candle = snapshot.BaseCandle;
            long previousClose = snapshot.PreviousClose;

            candidate.BaseCandleDate = NormalizeChartDate(candle.Date);
            candidate.BaseOpen = candle.Open;
            candidate.BaseHigh = candle.High;
            candidate.BaseLow = candle.Low;
            candidate.BaseClose = candle.Close;
            candidate.PreviousClose = previousClose;
            candidate.BaseCloseChangeRatePercent = previousClose > 0 && candle.Close > 0
                ? Math.Round(((candle.Close - previousClose) / (double)previousClose) * 100.0, 4)
                : 0;
            candidate.BaseVolume = candle.Volume;
            candidate.BaseTradingValue = candle.TradingValue;

            // BaseHalfPrice는 참고값으로만 저장한다.
            candidate.BaseHalfPrice = previousClose > 0 && candle.Close > 0
                ? (previousClose + candle.Close) / 2
                : (candle.High > 0 && candle.Low > 0 ? (candle.High + candle.Low) / 2 : 0);

            candidate.BaseBodyHalfPrice = candle.Open > 0 && candle.Close > 0
                ? (candle.Open + candle.Close) / 2
                : 0;
            candidate.BaseCandleMarket = string.IsNullOrWhiteSpace(snapshot.Market) ? "" : snapshot.Market;
            candidate.BaseCandleRequestCode = snapshot.RequestCode ?? "";

            // 신규 매수 기준가는 전일 종가로 확정한다.
            if (previousClose > 0)
            {
                candidate.BasePrice = previousClose;
                candidate.BasePriceSource = "PREVIOUS_CLOSE";
                candidate.BasePriceSavedAt = now;
                candidate.BaseCandleSource = "KRX_DAILY_CLOSE_AFTER_MARKET_PREVIOUS_CLOSE";
            }
            else
            {
                candidate.BasePrice = 0;
                candidate.BasePriceSource = "PREVIOUS_CLOSE_MISSING";
                candidate.BasePriceSavedAt = now;
                candidate.BaseCandleSource = "KRX_DAILY_CLOSE_AFTER_MARKET_PREVIOUS_CLOSE_MISSING";
            }

            candidate.BaseCandleSavedAt = now;
        }

        private void RunStrategyCandidateEodFinalizeIfDue(DateTime tradeDate, DateTime now, string reason)
        {
            try
            {
                if (!ShouldRunStrategyCandidateEodFinalize(tradeDate, now, out string skipReason))
                    return;

                string tradeDateText = tradeDate.ToString("yyyyMMdd");

                List<WatchCandidate> todayAdded = [.. _watchCandidates.Values
                    .Where(x => x != null)
                    .Select(x => NormalizeStrategyCandidate(x, now))
                    .Where(x => x != null)
                    .Where(x => x.FirstSeen.Date == tradeDate.Date)
                    .GroupBy(x => NormalizeStockCode(x.Code))
                    .Select(g => g.OrderByDescending(x => x.LastSeen).First())];

                if (todayAdded.Count == 0)
                {
                    MarkStrategyCandidateEodFinalizeDone(tradeDate, now, "NO_TODAY_CANDIDATES");
                    Log($"🧹 [조건00 종가정리] 기준일={tradeDateText} / 오늘유입 0개 / 사유={reason}");
                    return;
                }

                List<WatchCandidate> keep = [.. todayAdded.Where(IsValidCondition00BaseCandleCandidate)];
                List<WatchCandidate> drop = [.. todayAdded.Where(x => !IsValidCondition00BaseCandleCandidate(x))];

                foreach (WatchCandidate item in drop)
                {
                    string code = NormalizeStockCode(item.Code);
                    if (string.IsNullOrWhiteSpace(code)) continue;
                    _watchCandidates.Remove(code);

                    List<HoldingStock> rows = [.. _search00List
                        .Where(x => NormalizeStockCode(x.Code) == code)
                        .Where(x => (x.VolumeText ?? "").Contains("조건00", StringComparison.OrdinalIgnoreCase))];
                    foreach (HoldingStock row in rows)
                        _search00List.Remove(row);
                }

                if (drop.Count > 0)
                    SaveWatchCandidates();

                MarkStrategyCandidateEodFinalizeDone(tradeDate, now, "DONE");
                Log($"🧹 [조건00 종가정리] 기준일={tradeDateText} / 오늘유입 {todayAdded.Count}개 / 유지 {keep.Count}개 / 제거 {drop.Count}개 / 사유={reason}");
            }
            catch (Exception ex)
            {
                Log($"⚠️ [조건00 종가정리 오류] {ex.Message}");
            }
        }

        private bool ShouldRunStrategyCandidateEodFinalize(DateTime tradeDate, DateTime now, out string skipReason)
        {
            skipReason = "";
            bool isAfterClose = now.TimeOfDay >= StrategyCandidateEodFinalizeStartTime;
            bool isCarryWindow = now.TimeOfDay < StrategyCandidateBaseCandleSnapshotCarryUntilTime;
            if (!isAfterClose && !isCarryWindow)
            {
                skipReason = "NOT_EOD_WINDOW";
                return false;
            }

            string tradeDateKey = tradeDate.ToString("yyyyMMdd");
            if (!TryReadStrategyCandidateEodFinalizeState(out string lastKey, out DateTime lastAt))
                return true;

            if (string.Equals(lastKey, tradeDateKey, StringComparison.OrdinalIgnoreCase))
            {
                skipReason = "SAME_DATE_ALREADY_DONE";
                return false;
            }

            if (lastAt != default && (now - lastAt) < StrategyCandidateEodFinalizeCooldown)
            {
                skipReason = $"COOLDOWN_{StrategyCandidateEodFinalizeCooldown.TotalMinutes:N0}M";
                return false;
            }

            return true;
        }

        private bool IsValidCondition00BaseCandleCandidate(WatchCandidate candidate)
        {
            if (candidate == null) return false;
            if (candidate.BaseOpen <= 0 || candidate.BaseHigh <= 0 || candidate.BaseLow <= 0 || candidate.BaseClose <= 0)
                return false;
            if (candidate.PreviousClose <= 0)
                return false;
            if (candidate.BaseCloseChangeRatePercent < 20.0)
                return false;

            long range = candidate.BaseHigh - candidate.BaseLow;
            if (range <= 0)
                return false;

            double upperTailRate = ((candidate.BaseHigh - candidate.BaseClose) / (double)range) * 100.0;
            if (upperTailRate > 35.0)
                return false;

            return true;
        }

        private bool TryReadStrategyCandidateEodFinalizeState(out string key, out DateTime finalizedAt)
        {
            key = "";
            finalizedAt = default;

            try
            {
                if (!File.Exists(_candidateFinalizeStatePath))
                    return false;

                JObject json = JObject.Parse(File.ReadAllText(_candidateFinalizeStatePath));
                key = (json["LastFinalizeKey"]?.ToString() ?? "").Trim();
                DateTime.TryParse(json["LastFinalizeAt"]?.ToString() ?? "", out finalizedAt);
                return !string.IsNullOrWhiteSpace(key) || finalizedAt != default;
            }
            catch
            {
                return false;
            }
        }

        private void MarkStrategyCandidateEodFinalizeDone(DateTime tradeDate, DateTime now, string status)
        {
            try
            {
                Directory.CreateDirectory(_storageDir);

                JObject json = new()
                {
                    ["LastFinalizeKey"] = tradeDate.ToString("yyyyMMdd"),
                    ["LastFinalizeAt"] = now,
                    ["Status"] = status
                };

                File.WriteAllText(_candidateFinalizeStatePath, json.ToString());
            }
            catch (Exception ex)
            {
                Log($"⚠️ [조건00 종가정리 상태저장 오류] {ex.Message}");
            }
        }
    }
}
