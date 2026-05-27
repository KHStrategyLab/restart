#nullable disable

using KHStrategyLab.Models;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Globalization;
using System.Linq;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private LiveOrderRiskResult EvaluateLiveBuyRiskGuard(LiveOrderSignal signal)
        {
            LiveOrderRiskResult result = BuildInitialRiskResult(signal);

            if (signal == null)
                return BlockLiveOrder(result, "신호 객체 없음");

            string code = NormalizeStockCode(signal.Code);
            if (string.IsNullOrWhiteSpace(code))
                return BlockLiveOrder(result, "종목코드 비정상");

            result.OrderMarket = NormalizeLiveOrderMarketText(signal.Market);
            if (string.IsNullOrWhiteSpace(result.OrderMarket) || result.OrderMarket == "PENDING" || result.OrderMarket == "UNKNOWN")
                return BlockLiveOrder(result, "시장구분 미확정(PENDING/UNKNOWN)");

            long orderPrice = signal.OrderPrice > 0 ? signal.OrderPrice : signal.SignalPrice;
            if (orderPrice <= 0)
                return BlockLiveOrder(result, "현재가 또는 주문기준가 0");

            long originalBudget = GetLiveOrderBudget();
            LiveOrderBudgetAdjustment budgetAdjustment = ResolveLiveOrderBudgetAdjustment(signal, originalBudget);
            long budget = budgetAdjustment.Budget;
            result.OriginalBudget = originalBudget;
            result.Budget = budget;
            result.BudgetPercent = budgetAdjustment.Percent;
            result.BudgetSource = budgetAdjustment.Source;
            result.BudgetScoreGrade = budgetAdjustment.Grade;
            result.BudgetScorePercent = budgetAdjustment.ScorePercent;
            result.OrderPrice = orderPrice;

            if (budget <= 0)
                return BlockLiveOrder(result, "사용자 설정 진입예산 0");

            int maxSlots = GetLiveOrderMaxSlots();
            result.MaxSlots = maxSlots;
            result.CurrentSlotCount = GetCurrentSlotCount();

            if (maxSlots <= 0)
                return BlockLiveOrder(result, "최대 슬롯 설정 오류");

            if (result.CurrentSlotCount >= maxSlots)
                return BlockLiveOrder(result, $"최대 슬롯 초과 / 현재={result.CurrentSlotCount} / 제한={maxSlots}");

            int quantity = _oneShareLiveOrderTestMode ? 1 : (int)(budget / orderPrice);
            long expectedAmount = quantity * orderPrice;
            result.Quantity = quantity;
            result.ExpectedAmount = expectedAmount;

            if (quantity <= 0)
                return BlockLiveOrder(result, $"계산 수량 0 / 예산={budget:N0} / 기준가={orderPrice:N0}");

            if (expectedAmount <= 0 || expectedAmount > budget)
                return BlockLiveOrder(result, $"예상 주문금액 초과 / 예상={expectedAmount:N0} / 예산={budget:N0}");

            if (!_liveOrderEnabled)
                return BlockLiveOrder(result, "LiveOrderEnabled=OFF");

            if (string.IsNullOrWhiteSpace(_token))
                return BlockLiveOrder(result, "토큰 없음");

            if (string.IsNullOrWhiteSpace(_accNo))
                return BlockLiveOrder(result, "계좌번호 없음");

            MarketStateSnapshot marketState = GetMarketStateNow();
            if (marketState == null || !marketState.CanRegister0B)
                return BlockLiveOrder(result, $"주문 가능 시장 아님 / 상태={marketState?.Session} / 사유={marketState?.Reason}");

            if (!TryResolveLiveOrderMarket(signal.Market, out string orderMarket, out string marketBlockReason))
                return BlockLiveOrder(result, marketBlockReason);

            result.OrderMarket = orderMarket;

            if (!AllowsDynamicExitWithoutFixedTargetStop(signal) &&
                (signal.TargetPrice <= 0 || signal.StopPrice <= 0))
                return BlockLiveOrder(result, "전략 목표가/손절가 미정");

            lock (_liveOrderStateLock)
            {
                ResetDailyLiveOrderStateIfNeededLocked();

                result.IsOrderInProgress = _liveOrderInProgressByCode.ContainsKey(code);
                if (result.IsOrderInProgress)
                    return BlockLiveOrder(result, "종목 주문 진행 중");

                result.IsAlreadyOrderedToday = _tradedStrategyKeysToday.Contains(BuildOrderedStrategyKey(DateTime.Today, code, signal.StrategyCode));
                if (result.IsAlreadyOrderedToday)
                    return BlockLiveOrder(result, "오늘 같은 전략으로 이미 주문");

                if (_programManagedPositionsByCode.TryGetValue(code, out ProgramManagedPosition position) &&
                    position != null &&
                    !position.SellCompleted)
                {
                    return BlockLiveOrder(result, "프로그램 매수 포지션 이미 관리 중");
                }
            }

            result.IsHolding = IsHoldingStock(code);
            if (result.IsHolding)
                return BlockLiveOrder(result, "이미 보유 중인 종목");

            result.Allowed = true;
            result.BlockReason = "";
            return result;
        }

        private LiveOrderRiskResult BuildInitialRiskResult(LiveOrderSignal signal)
        {
            return new LiveOrderRiskResult
            {
                OrderMarket = NormalizeLiveOrderMarketText(signal?.Market),
                OriginalBudget = GetLiveOrderBudget(),
                Budget = GetLiveOrderBudget(),
                OrderPrice = signal?.OrderPrice > 0 ? signal.OrderPrice : signal?.SignalPrice ?? 0,
                CurrentSlotCount = GetCurrentSlotCount(),
                MaxSlots = GetLiveOrderMaxSlots()
            };
        }

        private LiveOrderRiskResult BlockLiveOrder(LiveOrderRiskResult result, string reason)
        {
            result.Allowed = false;
            result.BlockReason = string.IsNullOrWhiteSpace(reason) ? "차단 사유 미상" : reason;
            return result;
        }

        private bool TryResolveLiveOrderMarket(string rawMarket, out string orderMarket, out string blockReason)
        {
            orderMarket = NormalizeLiveOrderMarketText(rawMarket);
            blockReason = "";

            if (string.IsNullOrWhiteSpace(orderMarket) || orderMarket == "PENDING" || orderMarket == "UNKNOWN")
            {
                blockReason = "시장구분 미확정(PENDING/UNKNOWN)";
                return false;
            }

            if (orderMarket != "KRX" && orderMarket != "NXT" && orderMarket != "SOR")
            {
                blockReason = $"시장구분 비정상: {rawMarket}";
                return false;
            }

            if (orderMarket == "NXT")
                orderMarket = "SOR";

            return true;
        }

        private string NormalizeLiveOrderMarketText(string rawMarket)
        {
            string value = (rawMarket ?? "").Trim().ToUpperInvariant();
            if (value.Contains("NXT")) return "NXT";
            if (value.Contains("SOR")) return "SOR";
            if (value.Contains("KRX")) return "KRX";
            if (value.Contains("PENDING")) return "PENDING";
            if (value.Contains("UNKNOWN")) return "UNKNOWN";
            return value;
        }

        private bool IsHoldingStock(string code)
        {
            string normalized = NormalizeStockCode(code);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            bool holding = false;
            Dispatcher.Invoke(() =>
            {
                holding = _balance.Any(x => x != null && NormalizeStockCode(x.Code) == normalized && x.Volume > 0);
            });

            return holding;
        }

        private int GetCurrentSlotCount()
        {
            int balanceCount = 0;
            Dispatcher.Invoke(() =>
            {
                balanceCount = _balance.Count(x => x != null && x.Volume > 0);
            });

            int programOpenCount;
            lock (_liveOrderStateLock)
            {
                programOpenCount = _programManagedPositionsByCode.Values.Count(x => x != null && !x.SellCompleted);
            }

            return Math.Max(balanceCount, programOpenCount);
        }

        private long GetLiveOrderBudget()
        {
            return _entryBudget;
        }

        private bool AllowsDynamicExitWithoutFixedTargetStop(LiveOrderSignal signal)
        {
            return string.Equals(signal?.ExitMode, "TEN_MIN_CLOSE_BELOW_MA60_TRAILING_5_2_80", StringComparison.OrdinalIgnoreCase);
        }

        private LiveOrderBudgetAdjustment ResolveLiveOrderBudgetAdjustment(LiveOrderSignal signal, long originalBudget)
        {
            bool useBaseCandleBudget = ShouldUseBaseCandleBudgetAdjustment(signal);
            var fallback = new LiveOrderBudgetAdjustment
            {
                Percent = useBaseCandleBudget ? 50 : 100,
                Source = useBaseCandleBudget ? "BASE_CANDLE_SCORE_MISSING" : "DEFAULT",
                Budget = ApplyLiveOrderBudgetPercent(originalBudget, useBaseCandleBudget ? 50 : 100)
            };

            if (signal == null || !useBaseCandleBudget)
                return fallback;

            string code = NormalizeStockCode(signal.Code);
            string baseDate = NormalizeChartDate(signal.BaseCandleDate);
            if (string.IsNullOrWhiteSpace(baseDate) &&
                _watchCandidates.TryGetValue(code, out WatchCandidate candidate))
            {
                baseDate = NormalizeChartDate(candidate.BaseCandleDate);
            }

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(baseDate) || !File.Exists(_baseCandleScorePath))
                return fallback;

            try
            {
                JObject root = JObject.Parse(File.ReadAllText(_baseCandleScorePath));
                JArray candidates = root["Dates"]?[baseDate]?["Candidates"] as JArray;
                if (candidates == null)
                    return fallback;

                JObject item = candidates
                    .OfType<JObject>()
                    .FirstOrDefault(x => string.Equals(NormalizeStockCode(x["Code"]?.ToString()), code, StringComparison.OrdinalIgnoreCase));

                if (item == null)
                    return fallback;

                int percent = ReadLiveOrderBudgetPercent(item);
                if (percent <= 0)
                    percent = 50;

                double scorePercent = ReadLiveOrderDouble(item["ScorePercent"]);
                int finalRank = ReadLiveOrderInt(item["FinalRank"]);
                string grade = ResolveBaseCandleScoreGrade(scorePercent);

                return new LiveOrderBudgetAdjustment
                {
                    Percent = percent,
                    Budget = ApplyLiveOrderBudgetPercent(originalBudget, percent),
                    Source = $"BASE_CANDLE_SCORE:{baseDate}",
                    Grade = BuildBaseCandleScoreGradeRankText(grade, finalRank),
                    ScorePercent = scorePercent
                };
            }
            catch (Exception ex)
            {
                Log($"⚠️ [비중조절기] 기준봉 점수 조회 실패: {signal?.Name}({code}) / {ex.Message}");
                return fallback;
            }
        }

        private bool ShouldUseBaseCandleBudgetAdjustment(LiveOrderSignal signal)
        {
            string source = (signal?.EntrySource ?? "").Trim().ToUpperInvariant();
            string group = (signal?.StrategyGroup ?? "").Trim().ToUpperInvariant();
            string code = (signal?.StrategyCode ?? "").Trim().ToUpperInvariant();

            return source.Contains("CONDITION00") ||
                   source.Contains("PREV_LIMIT_BODY") ||
                   group.Contains("CONDITION00") ||
                   code.Contains("CONDITION00") ||
                   code.Contains("PREV_LIMIT");
        }

        private long ApplyLiveOrderBudgetPercent(long originalBudget, int percent)
        {
            if (originalBudget <= 0) return 0;
            percent = Math.Clamp(percent, 1, 100);
            return Math.Max(1, (long)Math.Floor(originalBudget * (percent / 100.0)));
        }

        private int ReadLiveOrderBudgetPercent(JObject item)
        {
            if (item == null) return 0;
            if (int.TryParse(item["SuggestedBudgetPercent"]?.ToString() ?? "", NumberStyles.Any, CultureInfo.InvariantCulture, out int value))
                return value;
            return 0;
        }

        private double ReadLiveOrderDouble(JToken token)
        {
            if (token == null) return 0;
            if (double.TryParse(token.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double value))
                return value;
            return 0;
        }

        private int ReadLiveOrderInt(JToken token)
        {
            if (token == null) return 0;
            if (int.TryParse(token.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out int value))
                return value;
            return 0;
        }

        private int GetLiveOrderMaxSlots()
        {
            return _maxSlots;
        }

        private sealed class LiveOrderBudgetAdjustment
        {
            public int Percent { get; set; } = 100;
            public long Budget { get; set; }
            public string Source { get; set; } = "DEFAULT";
            public string Grade { get; set; } = "";
            public double ScorePercent { get; set; }
        }

    }
}
