#nullable disable

using KHStrategyLab.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private DispatcherTimer _prevLimitBodyOpenRecoveryTimer;
        private bool _prevLimitBodyOpenRecoveryRunning = false;
        private DateTime _lastPrevLimitBodyOpenRecoverySummaryLogAt = DateTime.MinValue;

        private const string KrxPrevLimitBodyOpenRecoveryStrategyCode = "KRX_PREV_LIMIT_BODY_ESCAPE_OPEN_RECOVERY";
        private const string NxtPrevLimitBodyOpenRecoveryStrategyCode = "NXT_PREV_LIMIT_BODY_ESCAPE_OPEN_RECOVERY";
        private const string PrevLimitBodyOpenRecoveryStageWait = "WAIT_BODY_PULLBACK";
        private const string PrevLimitBodyOpenRecoveryStageEscapeReady = "ESCAPE_READY";
        private const string PrevLimitBodyOpenRecoveryStageSignal = "BUY_SIGNAL";
        private const double PrevLimitCloseChangeRateThresholdPercent = 28.0;

        private void InitializePrevLimitBodyOpenRecoveryTimer()
        {
            _prevLimitBodyOpenRecoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _prevLimitBodyOpenRecoveryTimer.Tick += async (s, e) => { await RunPrevLimitBodyOpenRecoveryAsync(); };
            _prevLimitBodyOpenRecoveryTimer.Start();
        }

        private async Task RunPrevLimitBodyOpenRecoveryAsync()
        {
            if (_prevLimitBodyOpenRecoveryRunning) return;
            if (!_isHunting) return;
            if (string.IsNullOrWhiteSpace(_token)) return;
            if (_watchCandidates.Count == 0) return;
            HashSet<string> activeDisplayCodes = SnapshotCondition00DisplayCodes();

            List<WatchCandidate> targets = [.. _watchCandidates.Values
                .Where(x => x != null)
                .Select(x => NormalizeStrategyCandidate(x, DateTime.Now))
                .Where(x => x != null)
                .Where(x => string.Equals(x.Sources, "조건00", StringComparison.OrdinalIgnoreCase))
                .Where(x => activeDisplayCodes.Count == 0 || activeDisplayCodes.Contains(NormalizeStockCode(x.Code)))
                .Where(x => string.IsNullOrWhiteSpace(x.BaseCandleDate) == false)
                .Where(x => x.BaseOpen > 0 && x.BaseClose > 0)
                .Where(x => x.PrevLimitBodyOpenRecoveryAlertAt == null)
                .Where(IsKnownStrategyCandidateMarket)
                .OrderBy(x => x.FirstSeen)];

            if (targets.Count == 0) return;

            _prevLimitBodyOpenRecoveryRunning = true;
            int checkedCount = 0;
            int krxCount = targets.Count(x => !IsNxtStrategyCandidate(x));
            int nxtCount = targets.Count(IsNxtStrategyCandidate);
            int prevLimitBodyCount = targets.Count(IsPrevLimitBodyCandidate);
            int prevLimitBodyKrxCount = targets.Count(x => !IsNxtStrategyCandidate(x) && IsPrevLimitBodyCandidate(x));
            int prevLimitBodyNxtCount = targets.Count(x => IsNxtStrategyCandidate(x) && IsPrevLimitBodyCandidate(x));
            int signalCount = 0;

            try
            {
                foreach (WatchCandidate candidate in targets)
                {
                    checkedCount++;
                    if (await EvaluatePrevLimitBodyOpenRecoveryAsync(candidate))
                        signalCount++;

                    await Task.Delay(80);
                }

                if (checkedCount > 0 && (DateTime.Now - _lastPrevLimitBodyOpenRecoverySummaryLogAt).TotalMinutes >= 5)
                {
                    _lastPrevLimitBodyOpenRecoverySummaryLogAt = DateTime.Now;
                    Log($"ℹ️ [전략집합] 전일상한가 시가회복 / 화면후보 {activeDisplayCodes.Count}개 / 전략대상 {checkedCount}개 / KRX {krxCount}개 / NXT {nxtCount}개");
                    Log($"📌 [전략] 전일상한가 몸통눌림 시가회복 점검: 전체 {checkedCount}개 / KRX {krxCount}개 / NXT {nxtCount}개 / 28%이상 종가등락률 {prevLimitBodyCount}개(KRX {prevLimitBodyKrxCount}개 / NXT {prevLimitBodyNxtCount}개) / 신규 매수신호 {signalCount}개 / 주문없음");
                }
            }
            catch (Exception ex)
            {
                Log($"❌ [전략 오류] 전일상한가 시가회복 / {ex.Message}");
            }
            finally
            {
                _prevLimitBodyOpenRecoveryRunning = false;
            }
        }

        private async Task<bool> EvaluatePrevLimitBodyOpenRecoveryAsync(WatchCandidate candidate)
        {
            if (candidate == null) return false;
            string market = IsNxtStrategyCandidate(candidate) ? "NXT" : "KRX";
            string strategyCode = market == "NXT"
                ? NxtPrevLimitBodyOpenRecoveryStrategyCode
                : KrxPrevLimitBodyOpenRecoveryStrategyCode;

            return await EvaluatePrevLimitBodyOpenRecoveryCoreAsync(candidate, market, strategyCode);
        }

        private async Task<bool> EvaluatePrevLimitBodyOpenRecoveryCoreAsync(WatchCandidate candidate, string market, string strategyCode)
        {
            string code = NormalizeStockCode(candidate.Code);
            if (string.IsNullOrWhiteSpace(code)) return false;

            if (!TryGetReadyCandidateMinuteCache(code, market, out CandidateMinuteCache minuteCache))
            {
                QueueLoadCandidateMinuteCache(code, market, "PREV_LIMIT_OPEN_RECOVERY_WAIT_MINUTE_LOAD");
                LogStrategyMinuteCacheNotReady(code, market, minuteCache);
                return false;
            }

            DateTime now = DateTime.Now;
            List<ChartCandle> completedFive = [.. (minuteCache.FiveMinuteCompletedCandles ?? [])
                .Where(x => x != null && x.Close > 0)
                .Where(x => !IsCurrentOrFutureMinuteCandleForStrategy(x, 5, now))
                .OrderBy(ParseMinuteCandleDateTime)];

            if (completedFive.Count < 2) return false;

            ChartCandle latestFive = completedFive[^1];
            ChartCandle previousFive = completedFive[^2];
            long todayOpen = ResolveTodayOpenFromFiveMinuteCandles(minuteCache, now);
            if (todayOpen <= 0) return false;

            long currentPrice = ResolvePrevLimitBodyOpenRecoveryCurrentPrice(candidate, minuteCache, latestFive);
            if (currentPrice <= 0) return false;

            if (!IsPrevLimitBodyCandidate(candidate)) return false;
            if (!IsInsidePrevLimitBody(candidate, currentPrice)) return false;

            // 목표가가 오늘 시가이므로, 이미 시가를 회복한 상태에서는 새 신호를 내지 않는다.
            if (currentPrice >= todayOpen) return false;

            candidate.PrevLimitTodayOpen = todayOpen;
            if (currentPrice > 0)
            {
                candidate.PrevLimitBodyPullbackLow = candidate.PrevLimitBodyPullbackLow > 0
                    ? Math.Min(candidate.PrevLimitBodyPullbackLow, currentPrice)
                    : currentPrice;
            }

            if (candidate.PrevLimitEscapeCandleHigh <= 0)
            {
                bool lowUpdateStopped = latestFive.Low >= previousFive.Low;
                if (!lowUpdateStopped) return false;

                bool bullishTurn = latestFive.Close > latestFive.Open;
                if (!bullishTurn) return false;

                bool recoveredMa5 = latestFive.MA5 > 0 && latestFive.Close > latestFive.MA5;
                if (!recoveredMa5) return false;

                string escapeTimeKey = BuildMinuteCandleKey(latestFive);
                candidate.PrevLimitEscapeCandleHigh = latestFive.High;
                candidate.PrevLimitEscapeCandleLow = latestFive.Low;
                candidate.PrevLimitEscapeCandleTime = escapeTimeKey;
                candidate.PrevLimitEscapeDetectedAt = DateTime.Now;
                candidate.PrevLimitBodyOpenRecoveryStage = PrevLimitBodyOpenRecoveryStageEscapeReady;
                candidate.PrevLimitBodyOpenRecoveryMemo =
                    $"탈출봉 저장 / 시장={market} / 고가={latestFive.High:N0} / 저가={latestFive.Low:N0} / TodayOpen={todayOpen:N0}";
                ApplyPrevLimitBodyOpenRecoveryStateToStoredCandidate(candidate);
                SaveWatchCandidates();

                Log($"🧩 [전일상한가 시가회복] 탈출봉 저장: {candidate.Name}({code}) / 시장={market} / 고가={latestFive.High:N0} / 저가={latestFive.Low:N0} / 목표={todayOpen:N0}");
            }

            if (candidate.PrevLimitEscapeCandleHigh <= 0) return false;
            if (currentPrice <= candidate.PrevLimitEscapeCandleHigh) return false;

            PrevLimitRiskReward riskReward = CalculatePrevLimitRiskReward(
                entryPrice: currentPrice,
                targetPrice: todayOpen,
                stopPrice: candidate.PrevLimitEscapeCandleLow);

            candidate.PrevLimitBodyOpenRecoveryStage = PrevLimitBodyOpenRecoveryStageSignal;
            candidate.PrevLimitBodyOpenRecoveryAlertAt = DateTime.Now;
            candidate.PrevLimitBodyOpenRecoveryMemo =
                $"매수후보 신호 / 시장={market} / 현재가={currentPrice:N0} / 탈출봉고가={candidate.PrevLimitEscapeCandleHigh:N0} / 목표={todayOpen:N0} / 손절={candidate.PrevLimitEscapeCandleLow:N0} / 손익비={riskReward.RatioText}";
            candidate.LastPrice = currentPrice;
            candidate.LastSeen = DateTime.Now;
            ApplyPrevLimitBodyOpenRecoveryStateToStoredCandidate(candidate);
            SaveWatchCandidates();

            string name = string.IsNullOrWhiteSpace(candidate.Name) ? code : candidate.Name;
            string message =
                $"🧩 [KHStrategyLab] {market} 전일상한가 몸통눌림 시가회복 매수후보\n" +
                $"{name}({code})\n" +
                $"전략: {strategyCode}\n" +
                $"현재가: {currentPrice:N0}\n" +
                $"전일 몸통: {candidate.BaseOpen:N0} ~ {candidate.BaseClose:N0}\n" +
                $"TodayOpen 목표가: {todayOpen:N0}\n" +
                $"탈출봉 고가: {candidate.PrevLimitEscapeCandleHigh:N0}\n" +
                $"손절가(탈출봉 저가): {candidate.PrevLimitEscapeCandleLow:N0}\n" +
                $"목표여유: {riskReward.Reward:N0} ({riskReward.RewardRateText})\n" +
                $"손절폭: {riskReward.Risk:N0} ({riskReward.RiskRateText})\n" +
                $"손익비: {riskReward.RatioText}\n" +
                $"참고 BodyPullbackLow: {candidate.PrevLimitBodyPullbackLow:N0}\n" +
                $"조건: 전일 상한가 몸통 안 눌림 + 현재가<TodayOpen + 5분 저점갱신 중단 + 5분 양봉 + 5분 MA5 회복 + 탈출봉 고가 돌파\n" +
                $"※ 실제 주문 여부는 실주문 스위치와 리스크 가드가 별도 판단";

            Log($"🧩 [{market} 전일상한가 시가회복 신호] {name}({code}) / 현재가 {currentPrice:N0} / 목표 {todayOpen:N0} / 손절 {candidate.PrevLimitEscapeCandleLow:N0} / 목표여유 {riskReward.RewardRateText} / 손절폭 {riskReward.RiskRateText} / 손익비 {riskReward.RatioText} / 주문레이어 전달");
            await SendTelegramMessageAsync(message);
            await TryExecuteLiveBuyAsync(new LiveOrderSignal
            {
                Code = code,
                Name = name,
                StrategyName = "전일상한가 몸통눌림 시가회복",
                StrategyCode = strategyCode,
                StrategyGroup = $"{market}_PREV_LIMIT_BODY_OPEN_RECOVERY",
                Market = market,
                SignalPrice = currentPrice,
                OrderPrice = currentPrice,
                TargetPrice = todayOpen,
                StopPrice = candidate.PrevLimitEscapeCandleLow,
                SignalTime = DateTime.Now,
                EntrySource = "PREV_LIMIT_BODY_OPEN_RECOVERY_SIGNAL",
                BaseCandleDate = NormalizeChartDate(candidate.BaseCandleDate)
            });
            return true;
        }

        private bool IsPrevLimitBodyCandidate(WatchCandidate candidate)
        {
            if (candidate == null) return false;
            if (candidate.BaseOpen <= 0 || candidate.BaseClose <= 0) return false;
            if (candidate.BaseClose <= candidate.BaseOpen) return false;

            double closeChangeRatePercent = candidate.BaseCloseChangeRatePercent;
            if (closeChangeRatePercent <= 0 && candidate.PreviousClose > 0 && candidate.BaseClose > 0)
            {
                closeChangeRatePercent =
                    ((candidate.BaseClose - candidate.PreviousClose) / (double)candidate.PreviousClose) * 100.0;
            }

            return closeChangeRatePercent >= PrevLimitCloseChangeRateThresholdPercent;
        }

        private bool IsInsidePrevLimitBody(WatchCandidate candidate, long currentPrice)
        {
            if (candidate == null || currentPrice <= 0) return false;
            return candidate.BaseOpen <= currentPrice && currentPrice < candidate.BaseClose;
        }

        private long ResolveTodayOpenFromFiveMinuteCandles(CandidateMinuteCache minuteCache, DateTime now)
        {
            IEnumerable<ChartCandle> candles = (minuteCache?.FiveMinuteCompletedCandles ?? [])
                .Concat(minuteCache?.FiveMinuteCurrentCandle != null ? [minuteCache.FiveMinuteCurrentCandle] : [])
                .Where(x => x != null && x.Open > 0)
                .OrderBy(ParseMinuteCandleDateTime);

            ChartCandle todayFirst = candles
                .FirstOrDefault(x => ParseMinuteCandleDateTime(x).Date == now.Date);

            return todayFirst?.Open ?? 0;
        }

        private long ResolvePrevLimitBodyOpenRecoveryCurrentPrice(
            WatchCandidate candidate,
            CandidateMinuteCache minuteCache,
            ChartCandle latestFive)
        {
            // 시장분리된 분봉 캐시 가격을 우선 사용해 NXT 후보가 KRX 가격으로 섞이지 않게 한다.
            if (minuteCache?.FiveMinuteCurrentCandle?.Close > 0) return minuteCache.FiveMinuteCurrentCandle.Close;
            if (latestFive?.Close > 0) return latestFive.Close;
            return candidate?.LastPrice > 0 ? candidate.LastPrice : 0;
        }

        private HashSet<string> SnapshotCondition00DisplayCodes()
        {
            HashSet<string> result = [];

            void Capture()
            {
                result = [.. _search00List
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Code))
                    .Where(x => (x.VolumeText ?? "").Contains("조건00"))
                    .Select(x => NormalizeStockCode(x.Code))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)];
            }

            if (Dispatcher.CheckAccess()) Capture();
            else Dispatcher.Invoke(Capture);

            return result;
        }

        private PrevLimitRiskReward CalculatePrevLimitRiskReward(long entryPrice, long targetPrice, long stopPrice)
        {
            long reward = targetPrice > entryPrice ? targetPrice - entryPrice : 0;
            long risk = entryPrice > stopPrice ? entryPrice - stopPrice : 0;
            double rewardRate = entryPrice > 0 ? reward / (double)entryPrice : 0;
            double riskRate = entryPrice > 0 ? risk / (double)entryPrice : 0;
            double ratio = risk > 0 ? reward / (double)risk : 0;

            return new PrevLimitRiskReward
            {
                Reward = reward,
                Risk = risk,
                RewardRateText = FormatPrevLimitPercent(rewardRate),
                RiskRateText = FormatPrevLimitPercent(riskRate),
                RatioText = risk > 0 ? ratio.ToString("0.00", CultureInfo.InvariantCulture) + "R" : "계산불가"
            };
        }

        private string FormatPrevLimitPercent(double value)
        {
            return (value * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%";
        }

        private void ApplyPrevLimitBodyOpenRecoveryStateToStoredCandidate(WatchCandidate candidate)
        {
            if (candidate == null) return;

            string code = NormalizeStockCode(candidate.Code);
            if (string.IsNullOrWhiteSpace(code)) return;

            if (_watchCandidates.TryGetValue(code, out WatchCandidate stored) && !ReferenceEquals(stored, candidate))
            {
                stored.PrevLimitBodyOpenRecoveryStage = candidate.PrevLimitBodyOpenRecoveryStage;
                stored.PrevLimitBodyPullbackLow = candidate.PrevLimitBodyPullbackLow;
                stored.PrevLimitTodayOpen = candidate.PrevLimitTodayOpen;
                stored.PrevLimitEscapeCandleHigh = candidate.PrevLimitEscapeCandleHigh;
                stored.PrevLimitEscapeCandleLow = candidate.PrevLimitEscapeCandleLow;
                stored.PrevLimitEscapeCandleTime = candidate.PrevLimitEscapeCandleTime;
                stored.PrevLimitEscapeDetectedAt = candidate.PrevLimitEscapeDetectedAt;
                stored.PrevLimitBodyOpenRecoveryAlertAt = candidate.PrevLimitBodyOpenRecoveryAlertAt;
                stored.PrevLimitBodyOpenRecoveryMemo = candidate.PrevLimitBodyOpenRecoveryMemo;
                stored.LastPrice = candidate.LastPrice;
                stored.LastSeen = candidate.LastSeen;
            }
        }

        private sealed class PrevLimitRiskReward
        {
            public long Reward { get; set; }
            public long Risk { get; set; }
            public string RewardRateText { get; set; } = "0.00%";
            public string RiskRateText { get; set; } = "0.00%";
            public string RatioText { get; set; } = "계산불가";
        }
    }
}
