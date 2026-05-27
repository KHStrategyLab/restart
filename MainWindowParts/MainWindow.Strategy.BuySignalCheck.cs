#nullable disable

using KHStrategyLab.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private DispatcherTimer _strategyCandidateBuySignalCheckTimer;
        private bool _strategyCandidateBuySignalCheckRunning = false;
        private DateTime _lastStrategyCandidateBuySignalCheckSummaryLogAt = DateTime.MinValue;

        private const int BuySignalTenMinuteMaFast = 5;
        private const int BuySignalTenMinuteMaMiddle = 20;
        private const int BuySignalTenMinuteMaLong = 60;
        private const int BuySignalFiveMinuteBreakoutLookback = 20;
        private static readonly TimeSpan StrategyMinuteChartMinRequestInterval = TimeSpan.FromMilliseconds(210);

        // 20선이 급락 중이면 회복 초입 매수에서 제외한다.
        // 0.998 = 직전 확정봉 MA20 대비 -0.2% 이상 급락하면 제외.
        private const double BuySignalMa20HardFallLimit = 0.998;

        private const string BuyStageWaitPullback = "WAIT_PULLBACK";
        private const string BuyStageBelowMa60 = "BELOW_10M_MA60";
        private const string BuyStageRecoveredMa60 = "RECOVERED_10M_MA60";
        private const string BuyStageGreenReady = "GREEN_READY";
        private const string BuyStageBuySignal = "BUY_SIGNAL";

        private void InitializeStrategyCandidateBuySignalCheckTimer()
        {
            _strategyCandidateBuySignalCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _strategyCandidateBuySignalCheckTimer.Tick += async (s, e) => { await RunStrategyCandidateBuySignalCheckAsync(); };
            _strategyCandidateBuySignalCheckTimer.Start();
        }

        private async Task RunStrategyCandidateBuySignalCheckAsync()
        {
            if (_strategyCandidateBuySignalCheckRunning) return;
            if (!_isHunting) return;
            if (string.IsNullOrWhiteSpace(_token)) return;
            if (_watchCandidates.Count == 0) return;

            foreach (WatchCandidate pending in _watchCandidates.Values
                .Where(x => x != null)
                .Where(x => string.Equals(x.Sources, "조건00", StringComparison.OrdinalIgnoreCase))
                .Where(x => !IsKnownStrategyCandidateMarket(x))
                .Take(1))
            {
                LogMarketUnresolvedStrategyBlocked(pending.Code);
            }

            List<WatchCandidate> targets = [.. _watchCandidates.Values
                .Where(x => x != null)
                .Select(x => NormalizeStrategyCandidate(x, DateTime.Now))
                .Where(x => x != null)
                .Where(x => string.Equals(x.Sources, "조건00", StringComparison.OrdinalIgnoreCase))
                .Where(x => string.IsNullOrWhiteSpace(x.BaseCandleDate) == false)
                .Where(x => x.BaseLow > 0)
                .Where(x => x.LastAlert == null)
                .Where(IsKnownStrategyCandidateMarket)
                .OrderBy(x => x.FirstSeen)];

            if (targets.Count == 0) return;

            _strategyCandidateBuySignalCheckRunning = true;
            int checkedCount = 0;
            int krxCount = 0;
            int nxtCount = 0;
            int signalCount = 0;

            try
            {
                foreach (WatchCandidate candidate in targets)
                {
                    checkedCount++;
                    bool signaled = await EvaluateStrategyCandidateBuySignalAsync(candidate);

                    if (IsNxtStrategyCandidate(candidate)) nxtCount++;
                    else krxCount++;

                    if (signaled) signalCount++;

                    // 키움 REST와 노트북 부하를 고려해 후보별 분봉 조회 사이를 살짝 띄운다.
                    await Task.Delay(120);
                }

                if (checkedCount > 0 && (DateTime.Now - _lastStrategyCandidateBuySignalCheckSummaryLogAt).TotalMinutes >= 5)
                {
                    _lastStrategyCandidateBuySignalCheckSummaryLogAt = DateTime.Now;
                    Log($"📌 [전략] 조건00 MA신호 점검: 전체 {checkedCount}개 / KRX {krxCount}개 / NXT {nxtCount}개 / 신규 매수신호 {signalCount}개 / 주문없음");
                }
            }
            catch (Exception ex)
            {
                Log($"❌ [전략 오류] 조건00 MA신호 / {ex.Message}");
            }
            finally
            {
                _strategyCandidateBuySignalCheckRunning = false;
            }
        }

        private bool IsKnownStrategyCandidateMarket(WatchCandidate candidate)
        {
            string market = (candidate?.StrategyMarket ?? "").Trim().ToUpperInvariant();
            return market == "KRX" || market == "NXT";
        }

        private bool IsNxtStrategyCandidate(WatchCandidate candidate)
        {
            string market = (candidate?.StrategyMarket ?? "").Trim().ToUpperInvariant();
            return market == "NXT";
        }

        private async Task<bool> EvaluateStrategyCandidateBuySignalAsync(WatchCandidate candidate)
        {
            if (candidate == null) return false;

            if (IsNxtStrategyCandidate(candidate))
                return await EvaluateNxtStrategyCandidateMaBreakoutAsync(candidate);

            return await EvaluateKrxStrategyCandidateMaBreakoutAsync(candidate);
        }

        // KRX 전략 파일은 NXT와 현재 조건이 같아도 별도 전략처럼 둔다.
        // 나중에 KRX 전용 거래대금/시간/손익비 조건을 따로 바꿀 수 있게 하기 위해서다.
        private async Task<bool> EvaluateKrxStrategyCandidateMaBreakoutAsync(WatchCandidate candidate)
        {
            return await EvaluateStrategyCandidateMaBreakoutCoreAsync(
                candidate,
                market: "KRX",
                strategyCode: "KRX_CONDITION00_MA_SIGNAL_BREAKOUT",
                strategyGroup: "KRX_CONDITION00");
        }

        // NXT 전략 파일은 KRX와 현재 조건이 같아도 별도 전략처럼 둔다.
        // 핵심 차이는 NXT 분봉으로 10분 MA5/MA20/MA60, 5분 High20을 따로 만든다는 점이다.
        private async Task<bool> EvaluateNxtStrategyCandidateMaBreakoutAsync(WatchCandidate candidate)
        {
            return await EvaluateStrategyCandidateMaBreakoutCoreAsync(
                candidate,
                market: "NXT",
                strategyCode: "NXT_CONDITION00_MA_SIGNAL_BREAKOUT",
                strategyGroup: "NXT_CONDITION00");
        }

        private async Task<bool> EvaluateStrategyCandidateMaBreakoutCoreAsync(
            WatchCandidate candidate,
            string market,
            string strategyCode,
            string strategyGroup)
        {
            string code = NormalizeStockCode(candidate.Code);
            if (string.IsNullOrWhiteSpace(code)) return false;

            // 전략 루프에서는 REST를 다시 조회하지 않는다. 초기/복구에서 채운 공통 분봉 캐시만 사용한다.
            if (!TryGetReadyCandidateMinuteCache(code, market, out CandidateMinuteCache minuteCache))
            {
                QueueLoadCandidateMinuteCache(code, market, "STRATEGY_WAIT_MINUTE_LOAD");
                LogStrategyMinuteCacheNotReady(code, market, minuteCache);
                return false;
            }

            List<ChartCandle> tenMinute = minuteCache.TenMinuteCompletedCandles;
            if (tenMinute.Count < BuySignalTenMinuteMaLong) return false;

            List<ChartCandle> fiveMinute = minuteCache.FiveMinuteCompletedCandles;
            if (fiveMinute.Count < BuySignalFiveMinuteBreakoutLookback) return false;

            string name = string.IsNullOrWhiteSpace(candidate.Name) ? code : candidate.Name;
            LogStrategyMinuteCacheUsed(name, code, market, candidate.BuyStage ?? BuyStageWaitPullback);

            DateTime now = DateTime.Now;
            DateTime startTime = ResolveStrategyCandidateStartTime(candidate);
            DateTime pullbackWatchStartTime = ResolveCondition00PullbackWatchStartTime(candidate);

            // MA 계산은 기준봉 이전 60개 분봉도 필요하다.
            // 따라서 '기준봉 이후 봉 개수'가 60개 미만이라고 차단하지 않는다.
            ChartCandle confirmedTen = SelectLatestCompletedMinuteCandleForStrategy(tenMinute, 10, now);
            if (confirmedTen == null || confirmedTen.MA5 <= 0 || confirmedTen.MA20 <= 0 || confirmedTen.MA60 <= 0) return false;
            if (ParseMinuteCandleDateTime(confirmedTen) < startTime) return false;

            ChartCandle previousTen = SelectPreviousCompletedMinuteCandleForStrategy(tenMinute, 10, now, confirmedTen);
            if (previousTen == null || previousTen.MA5 <= 0 || previousTen.MA20 <= 0) return false;

            bool isGreenSignal = confirmedTen.MA5 > confirmedTen.MA20 && confirmedTen.MA5 > confirmedTen.MA60;
            bool isStrongGreenSignal = confirmedTen.MA5 > confirmedTen.MA20 && confirmedTen.MA20 > confirmedTen.MA60;
            bool isMa5Rising = confirmedTen.MA5 > previousTen.MA5;
            bool isMa20NotHardFalling = confirmedTen.MA20 >= previousTen.MA20 * BuySignalMa20HardFallLimit;

            List<ChartCandle> completedTenAfterPullbackStart = [.. tenMinute
                .OrderBy(ParseMinuteCandleDateTime)
                .Where(x => !IsCurrentOrFutureMinuteCandleForStrategy(x, 10, now))
                .Where(x => ParseMinuteCandleDateTime(x) >= pullbackWatchStartTime)
                .Where(x => x.MA5 > 0 && x.MA20 > 0 && x.MA60 > 0)];

            // 최종 조건만 맞는 종목은 제외한다.
            // 기준봉 이후 다음날/며칠 뒤 10분 60선 아래 이탈 → 60선 회복 → 5선이 20선과 60선 위로 올라오는 흐름을 먼저 통과해야 한다.
            if (!UpdateCondition00RecoveryStage(candidate, completedTenAfterPullbackStart, market)) return false;

            if (!isGreenSignal) return false;
            if (!isMa5Rising) return false;
            if (!isMa20NotHardFalling) return false;

            // 기준봉 저가가 깨진 후보는 이번 신호에서는 제외한다.
            if (confirmedTen.Close <= candidate.BaseLow) return false;

            List<ChartCandle> completedFive = [.. fiveMinute
                .OrderBy(ParseMinuteCandleDateTime)
                .Where(x => !IsCurrentOrFutureMinuteCandleForStrategy(x, 5, now))];

            if (completedFive.Count < BuySignalFiveMinuteBreakoutLookback) return false;

            ChartCandle latestCompletedFive = completedFive.LastOrDefault();
            if (latestCompletedFive == null || latestCompletedFive.MA20 <= 0) return false;

            List<ChartCandle> previousFive = [.. completedFive.Skip(Math.Max(0, completedFive.Count - BuySignalFiveMinuteBreakoutLookback))];

            if (previousFive.Count < BuySignalFiveMinuteBreakoutLookback) return false;

            long high20 = previousFive.Max(x => x.High);
            long triggerPrice = candidate.LastPrice > 0 ? candidate.LastPrice : confirmedTen.Close;
            if (triggerPrice <= 0) return false;

            // 10분봉 기준: 현재가 > 60선
            if (triggerPrice <= confirmedTen.MA60) return false;

            // 5분봉 기준: 현재가 > 20선
            if (triggerPrice <= latestCompletedFive.MA20) return false;

            // 5분봉 20봉 신고가 돌파
            bool breakout = triggerPrice > high20;
            if (!breakout) return false;

            long basePrice = ResolveCondition00BasePriceForBuySignal(candidate);
            if (basePrice <= 0) return false;

            // 신규 매수 순간에는 기준가 위인지 확인한다.
            // 기준가는 최종 확정대로 전일종가다.
            if (triggerPrice <= basePrice) return false;

            SetCondition00BuyStage(candidate, BuyStageBuySignal, $"매수신호 발생 / {market} / 현재가={triggerPrice:N0} / 기준가={basePrice:N0}", DateTime.Now, saveImmediately: false);

            candidate.LastAlert = DateTime.Now;
            candidate.LastPrice = triggerPrice;
            candidate.LastSeen = DateTime.Now;
            candidate.StrategyMarket = market;
            candidate.StrategyCode = strategyCode;
            candidate.StrategyGroup = strategyGroup;
            candidate.MinuteChartMarket = market;
            candidate.RealtimePriceMarket = market;
            candidate.DisplayMarket = market;
            candidate.Ma5_10m = confirmedTen.MA5;
            candidate.Ma20_10m = confirmedTen.MA20;
            candidate.Ma60_10m = confirmedTen.MA60;
            candidate.MaSignal = isStrongGreenSignal ? "강" : "가능";
            candidate.High20_5m = high20;
            candidate.CloseSnapshotPrice = triggerPrice;
            candidate.MaSnapshotMarket = market;
            candidate.MaSnapshotSource = "STRATEGY_CONFIRMED_BAR";
            candidate.MaSnapshotAt = DateTime.Now;

            if (_watchCandidates.TryGetValue(code, out WatchCandidate stored))
            {
                stored.LastAlert = candidate.LastAlert;
                stored.LastPrice = candidate.LastPrice;
                stored.LastSeen = candidate.LastSeen;
                stored.StrategyMarket = candidate.StrategyMarket;
                stored.StrategyCode = candidate.StrategyCode;
                stored.StrategyGroup = candidate.StrategyGroup;
                stored.MinuteChartMarket = candidate.MinuteChartMarket;
                stored.RealtimePriceMarket = candidate.RealtimePriceMarket;
                stored.DisplayMarket = candidate.DisplayMarket;
                stored.Ma5_10m = candidate.Ma5_10m;
                stored.Ma20_10m = candidate.Ma20_10m;
                stored.Ma60_10m = candidate.Ma60_10m;
                stored.MaSignal = candidate.MaSignal;
                stored.High20_5m = candidate.High20_5m;
                stored.CloseSnapshotPrice = candidate.CloseSnapshotPrice;
                stored.MaSnapshotMarket = candidate.MaSnapshotMarket;
                stored.MaSnapshotSource = candidate.MaSnapshotSource;
                stored.MaSnapshotAt = candidate.MaSnapshotAt;
                stored.BuyStage = candidate.BuyStage;
                stored.HasBrokenBelowMa60 = candidate.HasBrokenBelowMa60;
                stored.BrokenBelowMa60At = candidate.BrokenBelowMa60At;
                stored.HasRecoveredMa60 = candidate.HasRecoveredMa60;
                stored.RecoveredMa60At = candidate.RecoveredMa60At;
                stored.HasGreenReady = candidate.HasGreenReady;
                stored.GreenReadyAt = candidate.GreenReadyAt;
                stored.BuyStageChangedAt = candidate.BuyStageChangedAt;
                stored.BuyStageMemo = candidate.BuyStageMemo;
            }

            SaveWatchCandidates();

            string greenText = isStrongGreenSignal
                ? "10분봉 완전 정배열(5>20>60)"
                : "10분봉 매수가능 배열(5>20 && 5>60)";
            string basePriceSource = ResolveCondition00BasePriceSourceForBuySignal(candidate);

            string message =
                $"🚦 [KHStrategyLab] {market} 매수신호 발생\n" +
                $"{name}({code})\n" +
                $"전략: {strategyCode}\n" +
                $"분봉기준: {market} / 10분봉={minuteCache.RequestCode10m} / 5분봉={minuteCache.RequestCode5m}\n" +
                $"현재가: {triggerPrice:N0}\n" +
                $"상태: 기준봉 이후 10분60 이탈 → 60선 회복 → 10분 녹색 확인 통과\n" +
                $"조건: {greenText} + 5선상승 + 20선급락아님 + 현재가>10분60 + 현재가>5분20 + 5분20 신고가 돌파 + 기준가 위\n" +
                $"기준가(기준봉 발생 전일 종가): {basePrice:N0} / {basePriceSource}\n" +
                $"기준봉저가: {candidate.BaseLow:N0}\n" +
                $"10분5선: {Math.Round(confirmedTen.MA5, MidpointRounding.AwayFromZero):N0}\n" +
                $"10분20선: {Math.Round(confirmedTen.MA20, MidpointRounding.AwayFromZero):N0}\n" +
                $"10분60선: {Math.Round(confirmedTen.MA60, MidpointRounding.AwayFromZero):N0}\n" +
                $"5분20선: {Math.Round(latestCompletedFive.MA20, MidpointRounding.AwayFromZero):N0}\n" +
                $"5분20고가: {high20:N0}\n" +
                $"※ 실제 주문 여부는 실주문 스위치와 리스크 가드가 별도 판단";

            Log($"🚦 [{market} 매수신호 발생] {name}({code}) / 상태흐름 통과 / 현재가 {triggerPrice:N0} / 기준가 {basePrice:N0} / 5분20고가 {high20:N0} / {greenText} / 주문레이어 전달");
            await SendTelegramMessageAsync(message);
            await TryExecuteLiveBuyAsync(new LiveOrderSignal
            {
                Code = code,
                Name = name,
                StrategyName = "조건00 MA 회복초입",
                StrategyCode = strategyCode,
                StrategyGroup = strategyGroup,
                Market = market,
                SignalPrice = triggerPrice,
                OrderPrice = triggerPrice,
                TargetPrice = 0,
                StopPrice = 0,
                SignalTime = DateTime.Now,
                EntrySource = "CONDITION00_MA_SIGNAL",
                BaseCandleDate = NormalizeChartDate(candidate.BaseCandleDate),
                ExitMode = "TEN_MIN_CLOSE_BELOW_MA60_TRAILING_5_2_80",
                TrailingStartRatePercent = 5.0,
                TrailingDropRatePercent = 2.0,
                TrailingSellPercent = 80
            });
            return true;
        }

        private bool UpdateCondition00RecoveryStage(WatchCandidate candidate, List<ChartCandle> completedTenAfterPullbackStart, string market)
        {
            if (candidate == null) return false;

            NormalizeCondition00BuyStage(candidate);

            if (completedTenAfterPullbackStart == null || completedTenAfterPullbackStart.Count == 0)
                return IsCondition00RecoveryStageReady(candidate);

            bool changed = false;

            foreach (ChartCandle candle in completedTenAfterPullbackStart.OrderBy(ParseMinuteCandleDateTime))
            {
                DateTime candleTime = ParseMinuteCandleDateTime(candle);
                if (candleTime == DateTime.MinValue) continue;
                if (candle.MA5 <= 0 || candle.MA20 <= 0 || candle.MA60 <= 0) continue;

                if (!candidate.HasBrokenBelowMa60)
                {
                    // 기준봉 이후 바로 추격하지 않고, 다음날/며칠 뒤 10분 60선 아래로 내려온 이력을 먼저 요구한다.
                    if (candle.Close < candle.MA60)
                    {
                        candidate.HasBrokenBelowMa60 = true;
                        candidate.BrokenBelowMa60At = candleTime;
                        SetCondition00BuyStage(candidate, BuyStageBelowMa60,
                            $"10분 60선 아래 이탈 확인 / {market} / 종가={candle.Close:N0} / MA60={Math.Round(candle.MA60, MidpointRounding.AwayFromZero):N0}",
                            candleTime, saveImmediately: false);
                        changed = true;
                    }

                    continue;
                }

                if (!candidate.HasRecoveredMa60)
                {
                    if (candidate.BrokenBelowMa60At.HasValue && candleTime <= candidate.BrokenBelowMa60At.Value)
                        continue;

                    if (candle.Close > candle.MA60)
                    {
                        candidate.HasRecoveredMa60 = true;
                        candidate.RecoveredMa60At = candleTime;
                        SetCondition00BuyStage(candidate, BuyStageRecoveredMa60,
                            $"10분 60선 회복 확인 / {market} / 종가={candle.Close:N0} / MA60={Math.Round(candle.MA60, MidpointRounding.AwayFromZero):N0}",
                            candleTime, saveImmediately: false);
                        changed = true;
                    }

                    continue;
                }

                if (!candidate.HasGreenReady)
                {
                    if (candidate.RecoveredMa60At.HasValue && candleTime <= candidate.RecoveredMa60At.Value)
                        continue;

                    bool greenReady = candle.MA5 > candle.MA20 && candle.MA5 > candle.MA60;
                    if (greenReady)
                    {
                        candidate.HasGreenReady = true;
                        candidate.GreenReadyAt = candleTime;
                        SetCondition00BuyStage(candidate, BuyStageGreenReady,
                            $"10분 녹색 확인 / {market} / MA5>{Math.Round(candle.MA20, MidpointRounding.AwayFromZero):N0}, MA5>{Math.Round(candle.MA60, MidpointRounding.AwayFromZero):N0}",
                            candleTime, saveImmediately: false);
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                ApplyCondition00BuyStageToStoredCandidate(candidate);
                SaveWatchCandidates();
            }

            return IsCondition00RecoveryStageReady(candidate);
        }

        private void NormalizeCondition00BuyStage(WatchCandidate candidate)
        {
            if (candidate == null) return;

            PullCondition00BuyStageFromStoredCandidate(candidate);

            if (string.IsNullOrWhiteSpace(candidate.BuyStage))
                candidate.BuyStage = BuyStageWaitPullback;

            if (candidate.HasGreenReady)
            {
                if (string.IsNullOrWhiteSpace(candidate.BuyStage) || candidate.BuyStage == BuyStageWaitPullback || candidate.BuyStage == BuyStageBelowMa60 || candidate.BuyStage == BuyStageRecoveredMa60)
                    candidate.BuyStage = BuyStageGreenReady;
                return;
            }

            if (candidate.HasRecoveredMa60)
            {
                if (candidate.BuyStage == BuyStageWaitPullback || candidate.BuyStage == BuyStageBelowMa60)
                    candidate.BuyStage = BuyStageRecoveredMa60;
                return;
            }

            if (candidate.HasBrokenBelowMa60)
            {
                if (candidate.BuyStage == BuyStageWaitPullback)
                    candidate.BuyStage = BuyStageBelowMa60;
            }
        }

        private bool IsCondition00RecoveryStageReady(WatchCandidate candidate)
        {
            return candidate != null
                && candidate.HasBrokenBelowMa60
                && candidate.HasRecoveredMa60
                && candidate.HasGreenReady;
        }

        private void SetCondition00BuyStage(WatchCandidate candidate, string stage, string memo, DateTime changedAt, bool saveImmediately)
        {
            if (candidate == null) return;

            string previousStage = candidate.BuyStage ?? "";
            candidate.BuyStage = string.IsNullOrWhiteSpace(stage) ? BuyStageWaitPullback : stage;
            candidate.BuyStageChangedAt = changedAt == DateTime.MinValue ? DateTime.Now : changedAt;
            candidate.BuyStageMemo = memo ?? "";

            ApplyCondition00BuyStageToStoredCandidate(candidate);

            if (!string.Equals(previousStage, candidate.BuyStage, StringComparison.OrdinalIgnoreCase))
            {
                string name = string.IsNullOrWhiteSpace(candidate.Name) ? candidate.Code : candidate.Name;
                Log($"📍 [전략단계] {name}({candidate.Code}) / {previousStage} → {candidate.BuyStage} / {candidate.BuyStageMemo}");
            }

            if (saveImmediately)
                SaveWatchCandidates();
        }

        private void PullCondition00BuyStageFromStoredCandidate(WatchCandidate candidate)
        {
            if (candidate == null) return;

            string code = NormalizeStockCode(candidate.Code);
            if (string.IsNullOrWhiteSpace(code)) return;

            if (_watchCandidates.TryGetValue(code, out WatchCandidate stored) && !ReferenceEquals(stored, candidate))
            {
                candidate.BuyStage = string.IsNullOrWhiteSpace(stored.BuyStage) ? candidate.BuyStage : stored.BuyStage;
                candidate.HasBrokenBelowMa60 = candidate.HasBrokenBelowMa60 || stored.HasBrokenBelowMa60;
                candidate.BrokenBelowMa60At = candidate.BrokenBelowMa60At ?? stored.BrokenBelowMa60At;
                candidate.HasRecoveredMa60 = candidate.HasRecoveredMa60 || stored.HasRecoveredMa60;
                candidate.RecoveredMa60At = candidate.RecoveredMa60At ?? stored.RecoveredMa60At;
                candidate.HasGreenReady = candidate.HasGreenReady || stored.HasGreenReady;
                candidate.GreenReadyAt = candidate.GreenReadyAt ?? stored.GreenReadyAt;
                candidate.BuyStageChangedAt = candidate.BuyStageChangedAt ?? stored.BuyStageChangedAt;
                candidate.BuyStageMemo = string.IsNullOrWhiteSpace(candidate.BuyStageMemo) ? stored.BuyStageMemo : candidate.BuyStageMemo;
            }
        }

        private void ApplyCondition00BuyStageToStoredCandidate(WatchCandidate candidate)
        {
            if (candidate == null) return;

            string code = NormalizeStockCode(candidate.Code);
            if (string.IsNullOrWhiteSpace(code)) return;

            if (_watchCandidates.TryGetValue(code, out WatchCandidate stored) && !ReferenceEquals(stored, candidate))
            {
                stored.BuyStage = candidate.BuyStage;
                stored.HasBrokenBelowMa60 = candidate.HasBrokenBelowMa60;
                stored.BrokenBelowMa60At = candidate.BrokenBelowMa60At;
                stored.HasRecoveredMa60 = candidate.HasRecoveredMa60;
                stored.RecoveredMa60At = candidate.RecoveredMa60At;
                stored.HasGreenReady = candidate.HasGreenReady;
                stored.GreenReadyAt = candidate.GreenReadyAt;
                stored.BuyStageChangedAt = candidate.BuyStageChangedAt;
                stored.BuyStageMemo = candidate.BuyStageMemo;
            }
        }

        private DateTime ResolveCondition00PullbackWatchStartTime(WatchCandidate candidate)
        {
            DateTime baseDate = ResolveStrategyCandidateStartTime(candidate).Date;

            // 기준봉 발생 당일 추격매수 방지.
            // 다음 거래일 또는 며칠 뒤 눌림부터 60선 이탈/회복 흐름을 추적한다.
            return baseDate.AddDays(1);
        }

        private long ResolveCondition00BasePriceForBuySignal(WatchCandidate candidate)
        {
            if (candidate == null) return 0;

            // 최종 기준가 위 조건은 전일 종가 기준이다.
            // 새 저장분은 BasePrice=PreviousClose로 들어오고, 기존 저장분은 PreviousClose 또는 BaseHalfPrice에서 복원한다.
            if (candidate.BasePrice > 0) return candidate.BasePrice;
            if (candidate.PreviousClose > 0) return candidate.PreviousClose;

            long derivedPreviousClose = TryDerivePreviousCloseFromStoredBaseHalf(candidate);
            if (derivedPreviousClose > 0) return derivedPreviousClose;

            // 전일 종가를 알 수 없으면 매수신호를 내지 않는다.
            // BaseOpen/BaseLow 같은 임시 fallback은 사용하지 않는다.
            return 0;
        }

        private string ResolveCondition00BasePriceSourceForBuySignal(WatchCandidate candidate)
        {
            if (candidate == null) return "PREVIOUS_CLOSE_UNKNOWN";
            if (candidate.BasePrice > 0 && !string.IsNullOrWhiteSpace(candidate.BasePriceSource)) return candidate.BasePriceSource;
            if (candidate.BasePrice > 0) return "PREVIOUS_CLOSE_BASE_PRICE";
            if (candidate.PreviousClose > 0) return "PREVIOUS_CLOSE";
            if (TryDerivePreviousCloseFromStoredBaseHalf(candidate) > 0) return "PREVIOUS_CLOSE_DERIVED_FROM_BASE_HALF";
            return "PREVIOUS_CLOSE_UNKNOWN";
        }

        private long TryDerivePreviousCloseFromStoredBaseHalf(WatchCandidate candidate)
        {
            if (candidate == null) return 0;
            if (candidate.BaseHalfPrice <= 0 || candidate.BaseClose <= 0) return 0;
            if (string.IsNullOrWhiteSpace(candidate.BaseCandleSource) ||
                !candidate.BaseCandleSource.Contains("PREV_CLOSE", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            long derived = (candidate.BaseHalfPrice * 2) - candidate.BaseClose;
            return derived > 0 ? derived : 0;
        }

        private ChartCandle SelectLatestCompletedMinuteCandleForStrategy(List<ChartCandle> candles, int tickScope, DateTime now)
        {
            if (candles == null || candles.Count == 0) return null;

            List<ChartCandle> ordered = [.. candles
                .Where(x => x != null && x.Close > 0)
                .OrderBy(ParseMinuteCandleDateTime)];

            if (ordered.Count == 0) return null;

            ChartCandle completed = ordered
                .Where(x => !IsCurrentOrFutureMinuteCandleForStrategy(x, tickScope, now))
                .LastOrDefault();

            return completed ?? ordered.LastOrDefault();
        }

        private ChartCandle SelectPreviousCompletedMinuteCandleForStrategy(
            List<ChartCandle> candles,
            int tickScope,
            DateTime now,
            ChartCandle currentCompleted)
        {
            if (candles == null || currentCompleted == null) return null;

            DateTime currentTime = ParseMinuteCandleDateTime(currentCompleted);
            return candles
                .Where(x => x != null && x.Close > 0)
                .Where(x => !IsCurrentOrFutureMinuteCandleForStrategy(x, tickScope, now))
                .Where(x => ParseMinuteCandleDateTime(x) < currentTime)
                .OrderBy(ParseMinuteCandleDateTime)
                .LastOrDefault();
        }

        private bool IsCurrentOrFutureMinuteCandleForStrategy(ChartCandle candle, int tickScope, DateTime now)
        {
            DateTime candleTime = ParseMinuteCandleDateTime(candle);
            if (candleTime == DateTime.MinValue) return false;

            DateTime bucketStart = FloorMinuteTimeForStrategy(now, tickScope);
            return candleTime >= bucketStart;
        }

        private DateTime FloorMinuteTimeForStrategy(DateTime value, int minuteUnit)
        {
            if (minuteUnit <= 0) minuteUnit = 1;
            int minute = value.Minute - (value.Minute % minuteUnit);
            return new DateTime(value.Year, value.Month, value.Day, value.Hour, minute, 0);
        }

        private DateTime ResolveStrategyCandidateStartTime(WatchCandidate candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate.BaseCandleDate))
            {
                string date = NormalizeChartDate(candidate.BaseCandleDate);
                if (DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
                    return parsed.Date;
            }

            if (candidate.FirstSeen != default) return candidate.FirstSeen.Date;
            return DateTime.Now.Date.AddDays(-6);
        }

        private async Task<StrategyMinuteChartBundle> RequestMinuteChartCandlesForStrategyAsync(string code, int tickScope, string market)
        {
            string normalized = NormalizeStockCode(code);
            string strategyMarket = string.Equals(market, "NXT", StringComparison.OrdinalIgnoreCase) ? "NXT" : "KRX";
            List<string> requestCodes = BuildMinuteChartRequestCodesForStrategy(normalized, strategyMarket);

            foreach (string requestCode in requestCodes)
            {
                List<ChartCandle> candles = await RequestMinuteChartCandlesRawForStrategyAsync(requestCode, tickScope, strategyMarket);
                if (candles.Count > 0)
                {
                    return new StrategyMinuteChartBundle
                    {
                        Market = strategyMarket,
                        RequestCode = requestCode,
                        Candles = candles
                    };
                }
            }

            return new StrategyMinuteChartBundle
            {
                Market = strategyMarket,
                RequestCode = requestCodes.FirstOrDefault() ?? normalized,
                Candles = []
            };
        }

        private List<string> BuildMinuteChartRequestCodesForStrategy(string code, string market)
        {
            var result = new List<string>();
            code = NormalizeStockCode(code);
            if (string.IsNullOrWhiteSpace(code)) return result;

            if (string.Equals(market, "NXT", StringComparison.OrdinalIgnoreCase))
            {
                // 가이드AI 확인 기준:
                // NXT ka10080 분봉은 stk_cd에 _NX를 붙인다. KRX fallback은 시장 혼선을 만들 수 있으므로 쓰지 않는다.
                result.Add(code + "_NX");
            }
            else
            {
                result.Add(code);
            }

            return [.. result.Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        private async Task<List<ChartCandle>> RequestMinuteChartCandlesRawForStrategyAsync(string requestCode, int tickScope, string market)
        {
            var result = new List<ChartCandle>();

            try
            {
                if (string.IsNullOrWhiteSpace(requestCode) || string.IsNullOrWhiteSpace(_token)) return result;

                string url = "https://api.kiwoom.com/api/dostk/chart";
                var body = new
                {
                    stk_cd = requestCode,
                    tic_scope = tickScope.ToString(CultureInfo.InvariantCulture),
                    upd_stkpc_tp = "1"
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.TryAddWithoutValidation("authorization", $"Bearer {_token}");
                request.Headers.TryAddWithoutValidation("api-id", "ka10080");
                request.Headers.TryAddWithoutValidation("cont-yn", "N");
                request.Headers.TryAddWithoutValidation("next-key", "");
                request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

                await _strategyMinuteChartRequestGate.WaitAsync();
                HttpResponseMessage response = null;
                try
                {
                    TimeSpan elapsed = DateTime.Now - _lastStrategyMinuteChartRequestAt;
                    if (elapsed < StrategyMinuteChartMinRequestInterval)
                        await Task.Delay(StrategyMinuteChartMinRequestInterval - elapsed);

                    response = await _http.SendAsync(request);
                    _lastStrategyMinuteChartRequestAt = DateTime.Now;
                }
                finally
                {
                    _strategyMinuteChartRequestGate.Release();
                }

                using (response)
                {
                    string text = await response.Content.ReadAsStringAsync();

                    JObject json;
                    try
                    {
                        json = JObject.Parse(text);
                    }
                    catch
                    {
                        Log($"⚠️ [전략분봉] JSON 파싱 실패: {requestCode} / {tickScope}분봉 / 시장={market}");
                        return result;
                    }

                    string returnCode = json["return_code"]?.ToString() ?? "";
                    string returnMsg = json["return_msg"]?.ToString() ?? "";

                    if (!response.IsSuccessStatusCode)
                    {
                        Log($"⚠️ [전략분봉] HTTP 오류: {requestCode} / {tickScope}분봉 / 시장={market} / {(int)response.StatusCode} / {response.ReasonPhrase}");
                        return result;
                    }

                    if (!string.IsNullOrWhiteSpace(returnCode) && returnCode != "0")
                    {
                        Log($"⚠️ [전략분봉] 응답 오류: {requestCode} / {tickScope}분봉 / 시장={market} / code={returnCode} / msg={returnMsg}");
                        return result;
                    }

                    JArray rows = FindMinuteChartArrayForStrategy(json);
                    if (rows == null || rows.Count == 0) return result;

                    // ka10080은 최신순으로 오므로, 내부 계산은 과거→최신 순서로 정렬한다.
                    result = [.. rows
                        .Select(ParseMinuteChartCandleForStrategy)
                        .Where(x => x != null && x.Close > 0)
                        .GroupBy(BuildMinuteCandleKey)
                        .Select(g => g.First())
                        .OrderBy(ParseMinuteCandleDateTime)];

                    FillMinuteChartMovingAveragesForStrategy(result);
                    return result;
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ [전략분봉 오류] {requestCode} / {tickScope}분봉 / 시장={market} / {ex.Message}");
                return result;
            }
        }

        private JArray FindMinuteChartArrayForStrategy(JObject json)
        {
            if (json == null) return null;

            string[] directKeys =
            [
                "stk_min_pole_chart_qry",
                "stk_min_chart_qry",
                "min_chart_qry",
                "output",
                "data",
                "list",
                "chart",
                "items"
            ];

            foreach (string key in directKeys)
            {
                if (json[key] is JArray directArray) return directArray;
            }

            foreach (JProperty prop in json.Properties())
            {
                if (prop.Value is JArray array) return array;
            }

            foreach (JProperty prop in json.Properties())
            {
                if (prop.Value is JObject child)
                {
                    foreach (JProperty childProp in child.Properties())
                    {
                        if (childProp.Value is JArray array) return array;
                    }
                }
            }

            return null;
        }

        private ChartCandle ParseMinuteChartCandleForStrategy(JToken token)
        {
            if (token == null) return null;

            string rawTime = ReadMinuteChartTextForStrategy(token, "cntr_tm", "time", "tm", "체결시간", "일시", "dt", "date", "base_dt");
            string digits = NumberOnlyRegex().Replace(rawTime ?? "", "");

            string date;
            string time;

            if (digits.Length >= 14)
            {
                date = digits.Substring(0, 8);
                time = digits.Substring(8, 6);
            }
            else if (digits.Length >= 12)
            {
                date = digits.Substring(0, 8);
                time = digits.Substring(8).PadRight(6, '0').Substring(0, 6);
            }
            else if (digits.Length >= 8)
            {
                date = digits.Substring(0, 8);
                time = digits.Length > 8 ? digits.Substring(8).PadRight(6, '0').Substring(0, 6) : "000000";
            }
            else
            {
                date = DateTime.Now.ToString("yyyyMMdd");
                time = digits.PadRight(6, '0').Substring(0, 6);
            }

            long open = Math.Abs(ParseMinuteChartLongForStrategy(ReadMinuteChartTextForStrategy(token, "open_pric", "open", "stck_oprc", "시가")));
            long high = Math.Abs(ParseMinuteChartLongForStrategy(ReadMinuteChartTextForStrategy(token, "high_pric", "high", "stck_hgpr", "고가")));
            long low = Math.Abs(ParseMinuteChartLongForStrategy(ReadMinuteChartTextForStrategy(token, "low_pric", "low", "stck_lwpr", "저가")));
            long close = Math.Abs(ParseMinuteChartLongForStrategy(ReadMinuteChartTextForStrategy(token, "cur_prc", "close_pric", "close", "stck_clpr", "현재가", "종가")));
            long volume = Math.Abs(ParseMinuteChartLongForStrategy(ReadMinuteChartTextForStrategy(token, "trde_qty", "cntg_vol", "acml_vol", "volume", "거래량")));
            long tradingValue = Math.Abs(ParseMinuteChartLongForStrategy(ReadMinuteChartTextForStrategy(token, "trde_prica", "trde_prc", "acml_tr_pbmn", "acc_trdval", "trading_value", "거래대금")));

            if (open <= 0 && close > 0) open = close;
            if (high <= 0) high = Math.Max(open, close);
            if (low <= 0) low = Math.Min(open, close);
            if (close <= 0) return null;

            long estimatedTradingValue = close > 0 && volume > 0 ? close * volume : 0;
            if (estimatedTradingValue > 0 && (tradingValue <= 0 || tradingValue < estimatedTradingValue / 1000))
            {
                tradingValue = estimatedTradingValue;
            }

            return new ChartCandle
            {
                Date = date,
                Time = time,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume,
                TradingValue = tradingValue
            };
        }

        private string ReadMinuteChartTextForStrategy(JToken token, params string[] keys)
        {
            if (token == null) return "";

            if (token is JObject obj)
            {
                foreach (string key in keys)
                {
                    JToken value = obj[key];
                    if (value == null)
                    {
                        JProperty prop = obj.Properties()
                            .FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
                        value = prop?.Value;
                    }

                    string text = value?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                }
            }

            return "";
        }

        private long ParseMinuteChartLongForStrategy(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            string clean = value.Replace(",", "").Replace("+", "").Trim();

            if (long.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out long result)) return result;
            if (long.TryParse(clean, NumberStyles.Any, CultureInfo.CurrentCulture, out result)) return result;
            return 0;
        }

        private void FillMinuteChartMovingAveragesForStrategy(List<ChartCandle> candles)
        {
            if (candles == null || candles.Count == 0) return;

            for (int i = 0; i < candles.Count; i++)
            {
                candles[i].MA5 = CalculateMinuteMaForStrategy(candles, i, BuySignalTenMinuteMaFast);
                candles[i].MA10 = CalculateMinuteMaForStrategy(candles, i, 10);
                candles[i].MA20 = CalculateMinuteMaForStrategy(candles, i, BuySignalTenMinuteMaMiddle);
                candles[i].MA60 = CalculateMinuteMaForStrategy(candles, i, BuySignalTenMinuteMaLong);
                candles[i].MA200 = CalculateMinuteMaForStrategy(candles, i, 200);
                candles[i].MA480 = CalculateMinuteMaForStrategy(candles, i, 480);
            }
        }

        private double CalculateMinuteMaForStrategy(List<ChartCandle> candles, int index, int period)
        {
            if (candles == null || index < period - 1) return 0;
            return candles.Skip(index - period + 1).Take(period).Average(x => (double)x.Close);
        }

        private string BuildMinuteCandleKey(ChartCandle candle)
        {
            if (candle == null) return "";
            return $"{NormalizeChartDate(candle.Date)}_{NumberOnlyRegex().Replace(candle.Time ?? "", "")}";
        }

        private DateTime ParseMinuteCandleDateTime(ChartCandle candle)
        {
            if (candle == null) return DateTime.MinValue;

            string date = NormalizeChartDate(candle.Date);
            string time = NumberOnlyRegex().Replace(candle.Time ?? "", "");

            if (string.IsNullOrWhiteSpace(time)) time = "000000";
            if (time.Length < 6) time = time.PadRight(6, '0');
            if (time.Length > 6) time = time.Substring(0, 6);

            string value = date + time;
            if (DateTime.TryParseExact(value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
                return parsed;

            if (DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                return parsed.Date;

            return DateTime.MinValue;
        }

        private sealed class StrategyMinuteChartBundle
        {
            public string Market { get; set; } = "KRX";
            public string RequestCode { get; set; } = "";
            public List<ChartCandle> Candles { get; set; } = [];
        }
    }
}
