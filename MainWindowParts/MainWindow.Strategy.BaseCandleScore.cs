#nullable disable
using KHStrategyLab.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private static readonly TimeSpan BaseCandleScoreStartTime = new(18, 0, 0);
        private static readonly TimeSpan BaseCandleScoreCarryUntilTime = new(7, 0, 0);

        private void InitializeBaseCandleScoreTimer()
        {
            _baseCandleScoreTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _baseCandleScoreTimer.Tick += async (s, e) =>
            {
                await RunBaseCandleScoreIfDueAsync("TIMER");
            };
            _baseCandleScoreTimer.Start();
        }

        private async Task RunBaseCandleScoreIfDueAsync(string reason = "UNKNOWN", bool force = false)
        {
            if (_baseCandleScoreRunning) return;

            DateTime now = DateTime.Now;
            DateTime? targetTradeDate = ResolveBaseCandleScoreTradeDate(now);
            string baseDate = targetTradeDate?.ToString("yyyyMMdd") ?? "";
            if (!force && string.IsNullOrWhiteSpace(baseDate))
            {
                baseDate = ResolveMissingBaseCandleScoreDate();
                if (string.IsNullOrWhiteSpace(baseDate)) return;
                reason = $"{reason}_MISSING_SCORE_BACKFILL";
            }
            if (!force && _strategyCandidateBaseCandleSnapshotRunning) return;
            if (_watchCandidates.Count == 0) return;

            if (force && string.IsNullOrWhiteSpace(baseDate))
            {
                DateTime tradeDate = ResolveLatestMarketOpenDateOnOrBefore(now.Date);
                baseDate = tradeDate.ToString("yyyyMMdd");
            }

            List<WatchCandidate> candidates = [.. _watchCandidates.Values
                .Where(x => x != null)
                .Select(x => NormalizeStrategyCandidate(x, now))
                .Where(x => x != null)
                .Where(x => string.Equals(NormalizeChartDate(x.BaseCandleDate), baseDate, StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => NormalizeStockCode(x.Code))
                .Select(g => g.OrderByDescending(x => x.LastSeen).First())];

            if (candidates.Count == 0) return;

            string runKey = BuildBaseCandleScoreRunKey(baseDate, candidates);
            if (!force && string.Equals(_lastBaseCandleScoreRunKey, runKey, StringComparison.OrdinalIgnoreCase))
                return;

            _baseCandleScoreRunning = true;
            try
            {
                BaseCandleScoreDayResult result = BuildBaseCandleScoreDayResult(baseDate, candidates, now, reason);
                SaveBaseCandleScoreDayResult(result);
                RunBaseCandleFollowupScoreIfDue(now, reason);
                ApplyBaseCandleScoreToLeadingGrid();
                _lastBaseCandleScoreRunKey = runKey;

                Log($"🧮 [기준봉점수] D0 평가 완료: 기준일={baseDate} / 준비 {result.ReadyCount}개 / 보류 {result.PendingCount}개 / 저장=base_candle_scores.json / 사유={reason}");

                foreach (BaseCandleScoreItem item in result.Candidates
                    .Where(x => string.Equals(x.Status, "READY", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.FinalRank)
                    .ThenByDescending(x => x.RawScore)
                    .Take(5))
                {
                    string gradeText = BuildBaseCandleScoreGradeRankText(item.Grade, item.FinalRank);
                    Log($"🏆 [기준봉점수] {item.FinalRank}위 {item.Name}({item.Code}) / {item.RawScore}/{item.MaxRawScore}점({item.ScorePercent:0.##}%) / {gradeText} / 권장비중 {item.SuggestedBudgetPercent}% / {item.Summary}");
                }

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Log($"❌ [기준봉점수 오류] {baseDate} / {ex.Message}");
            }
            finally
            {
                _baseCandleScoreRunning = false;
            }
        }

        private DateTime? ResolveBaseCandleScoreTradeDate(DateTime now)
        {
            TimeSpan time = now.TimeOfDay;

            if (time >= BaseCandleScoreStartTime)
                return ResolveLatestMarketOpenDateOnOrBefore(now.Date);

            if (time < BaseCandleScoreCarryUntilTime)
                return ResolveLatestMarketOpenDateOnOrBefore(now.Date.AddDays(-1));

            return null;
        }

        private string ResolveMissingBaseCandleScoreDate()
        {
            List<string> baseDates = [.. _watchCandidates.Values
                .Where(x => x != null)
                .Select(x => NormalizeChartDate(x.BaseCandleDate))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x)];

            foreach (string baseDate in baseDates)
            {
                if (!HasBaseCandleScoreDate(baseDate))
                    return baseDate;
            }

            return "";
        }

        private bool HasBaseCandleScoreDate(string baseDate)
        {
            if (string.IsNullOrWhiteSpace(baseDate) || !File.Exists(_baseCandleScorePath))
                return false;

            try
            {
                JObject root = JObject.Parse(File.ReadAllText(_baseCandleScorePath));
                JObject dates = root["Dates"] as JObject;
                return dates?[baseDate] != null;
            }
            catch
            {
                return false;
            }
        }

        private string BuildBaseCandleScoreRunKey(string baseDate, List<WatchCandidate> candidates)
        {
            long latestTick = candidates
                .SelectMany(x => new[]
                {
                    x.BaseCandleSavedAt?.Ticks ?? 0,
                    x.StockInfoCapturedAt?.Ticks ?? 0,
                    x.LastSeen.Ticks
                })
                .DefaultIfEmpty(0)
                .Max();

            return $"{baseDate}|{candidates.Count}|{latestTick}";
        }

        private void RunBaseCandleFollowupScoreIfDue(DateTime now, string reason)
        {
            List<string> baseDates = [.. _watchCandidates.Values
                .Where(x => x != null)
                .Select(x => NormalizeChartDate(x.BaseCandleDate))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)];

            if (baseDates.Count == 0) return;

            DateTime latestOpen = ResolveLatestMarketOpenDateOnOrBefore(now.Date);
            string latestOpenText = latestOpen.ToString("yyyyMMdd");

            foreach (string baseDate in baseDates)
            {
                if (string.Compare(baseDate, latestOpenText, StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                List<WatchCandidate> candidates = [.. _watchCandidates.Values
                    .Where(x => x != null)
                    .Select(x => NormalizeStrategyCandidate(x, now))
                    .Where(x => x != null)
                    .Where(x => string.Equals(NormalizeChartDate(x.BaseCandleDate), baseDate, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(x => NormalizeStockCode(x.Code))
                    .Select(g => g.OrderByDescending(x => x.LastSeen).First())];

                if (candidates.Count == 0) continue;

                string runKey = BuildBaseCandleFollowupRunKey(baseDate, candidates);
                if (string.Equals(_lastBaseCandleFollowupRunKey, runKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (HasBaseCandleFollowupDate(baseDate))
                    continue;

                BaseCandleFollowupDayResult result = BuildBaseCandleFollowupDayResult(baseDate, candidates, now, reason);
                if (result.ReadyCount <= 0)
                    continue;

                SaveBaseCandleFollowupDayResult(result);
                _lastBaseCandleFollowupRunKey = runKey;

                Log($"🧮 [기준봉점수] D1 평가 완료: 기준일={baseDate} / 준비 {result.ReadyCount}개 / 보류 {result.PendingCount}개 / 사유={reason}");
                foreach (BaseCandleFollowupScoreItem item in result.Candidates
                    .Where(x => string.Equals(x.Status, "READY", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.FinalRank)
                    .ThenByDescending(x => x.RawScore)
                    .Take(5))
                {
                    string gradeText = BuildBaseCandleScoreGradeRankText(item.Grade, item.FinalRank);
                    Log($"🏅 [기준봉점수] D1 {item.Name}({item.Code}) / {item.RawScore}/{item.MaxRawScore}점({item.ScorePercent:0.##}%) / {gradeText} / {item.Summary}");
                }
            }
        }

        private string BuildBaseCandleFollowupRunKey(string baseDate, List<WatchCandidate> candidates)
        {
            long latestTick = candidates
                .SelectMany(x => new[]
                {
                    x.BaseCandleSavedAt?.Ticks ?? 0,
                    x.LastSeen.Ticks
                })
                .DefaultIfEmpty(0)
                .Max();

            return $"D1|{baseDate}|{candidates.Count}|{latestTick}";
        }

        private bool HasBaseCandleFollowupDate(string baseDate)
        {
            if (string.IsNullOrWhiteSpace(baseDate) || !File.Exists(_baseCandleScorePath))
                return false;

            try
            {
                JObject root = JObject.Parse(File.ReadAllText(_baseCandleScorePath));
                JToken token = root["Dates"]?[baseDate]?["FollowupD1"];
                return token != null;
            }
            catch
            {
                return false;
            }
        }

        private BaseCandleScoreDayResult BuildBaseCandleScoreDayResult(string baseDate, List<WatchCandidate> candidates, DateTime now, string reason)
        {
            var inputs = candidates
                .Select(x => BuildBaseCandleScoreInput(x))
                .OrderBy(x => x.Name)
                .ThenBy(x => x.Code)
                .ToList();

            List<BaseCandleScoreInput> ready = [.. inputs.Where(x => x.Ready)];
            int readyCount = ready.Count;
            int itemCount = 6;
            int maxRawScore = readyCount * itemCount;

            var tradingValueScores = BuildCompetitiveRankScores(ready, x => x.BaseTradingValue, higherBetter: true);
            var changeRateScores = BuildCompetitiveRankScores(ready, x => x.ChangeRatePercent, higherBetter: true);
            var turnoverScores = BuildCompetitiveRankScores(ready, x => x.TurnoverRatePercent, higherBetter: true);
            var upperTailScores = BuildCompetitiveRankScores(ready, x => x.UpperTailRate, higherBetter: false);
            var closeNearHighScores = BuildCompetitiveRankScores(ready, x => x.CloseNearHighRate, higherBetter: true);
            var bodyStrengthScores = BuildCompetitiveRankScores(ready, x => x.BodyStrengthRate, higherBetter: true);

            var items = new List<BaseCandleScoreItem>();

            foreach (BaseCandleScoreInput input in inputs)
            {
                var item = new BaseCandleScoreItem
                {
                    Status = input.Ready ? "READY" : "PENDING",
                    PendingReason = input.Ready ? "" : input.PendingReason,
                    Code = input.Code,
                    Name = input.Name,
                    BaseDate = baseDate,
                    ScoreMarket = "KRX",
                    StrategyMarket = input.StrategyMarket,
                    NxtEnabled = input.NxtEnabled,
                    BaseTradingValue = input.BaseTradingValue,
                    ChangeRatePercent = input.ChangeRatePercent,
                    TurnoverRatePercent = input.TurnoverRatePercent,
                    UpperTailRate = input.UpperTailRate,
                    CloseNearHighRate = input.CloseNearHighRate,
                    BodyStrengthRate = input.BodyStrengthRate,
                    MaxRawScore = maxRawScore
                };

                if (input.Ready)
                {
                    ApplyRankScore(item, tradingValueScores[input.Code], "TradingValue");
                    ApplyRankScore(item, changeRateScores[input.Code], "ChangeRate");
                    ApplyRankScore(item, turnoverScores[input.Code], "Turnover");
                    ApplyRankScore(item, upperTailScores[input.Code], "UpperTail");
                    ApplyRankScore(item, closeNearHighScores[input.Code], "CloseNearHigh");
                    ApplyRankScore(item, bodyStrengthScores[input.Code], "BodyStrength");

                    item.RawScore =
                        item.TradingValueScore + item.ChangeRateScore + item.TurnoverScore +
                        item.UpperTailScore + item.CloseNearHighScore + item.BodyStrengthScore;
                    item.ScorePercent = maxRawScore > 0 ? Math.Round((item.RawScore / (double)maxRawScore) * 100.0, 2) : 0;
                    item.Grade = ResolveBaseCandleScoreGrade(item.ScorePercent);
                    item.SuggestedBudgetPercent = ResolveBaseCandleSuggestedBudgetPercent(item.ScorePercent);
                    item.Summary = BuildBaseCandleScoreSummary(item);
                }

                items.Add(item);
            }

            ApplyFinalRanks(items);

            return new BaseCandleScoreDayResult
            {
                ScoreDate = now.ToString("yyyyMMdd"),
                BaseDate = baseDate,
                Mode = "D0",
                ScoreMarket = "KRX",
                Reason = reason,
                CandidateCount = candidates.Count,
                ReadyCount = readyCount,
                PendingCount = inputs.Count(x => !x.Ready),
                ItemCount = itemCount,
                MaxRawScore = maxRawScore,
                EvaluatedAt = now,
                Candidates = items
            };
        }

        private BaseCandleFollowupDayResult BuildBaseCandleFollowupDayResult(string baseDate, List<WatchCandidate> candidates, DateTime now, string reason)
        {
            var inputs = candidates
                .Select(x => BuildBaseCandleFollowupInput(x))
                .OrderBy(x => x.Name)
                .ThenBy(x => x.Code)
                .ToList();

            List<BaseCandleFollowupInput> ready = [.. inputs.Where(x => x.Ready)];
            int readyCount = ready.Count;
            int itemCount = 4;
            int maxRawScore = readyCount * itemCount;

            var closeVsBaseScores = BuildCompetitiveFollowupRankScores(ready, x => x.CloseVsBaseCloseRate, higherBetter: true);
            var valueCompressionScores = BuildCompetitiveFollowupRankScores(ready, x => x.TradingValueCompressionRate, higherBetter: true);
            var upperTailScores = BuildCompetitiveFollowupRankScores(ready, x => x.UpperTailRate, higherBetter: false);
            var closeInRangeScores = BuildCompetitiveFollowupRankScores(ready, x => x.CloseInRangeRate, higherBetter: true);

            var items = new List<BaseCandleFollowupScoreItem>();

            foreach (BaseCandleFollowupInput input in inputs)
            {
                var item = new BaseCandleFollowupScoreItem
                {
                    Status = input.Ready ? "READY" : "PENDING",
                    PendingReason = input.Ready ? "" : input.PendingReason,
                    Code = input.Code,
                    Name = input.Name,
                    BaseDate = baseDate,
                    FollowupDate = input.FollowupDate,
                    CloseVsBaseCloseRate = input.CloseVsBaseCloseRate,
                    TradingValueCompressionRate = input.TradingValueCompressionRate,
                    UpperTailRate = input.UpperTailRate,
                    CloseInRangeRate = input.CloseInRangeRate,
                    MaxRawScore = maxRawScore
                };

                if (input.Ready)
                {
                    ApplyFollowupRankScore(item, closeVsBaseScores[input.Code], "CloseVsBaseClose");
                    ApplyFollowupRankScore(item, valueCompressionScores[input.Code], "TradingValueCompression");
                    ApplyFollowupRankScore(item, upperTailScores[input.Code], "UpperTail");
                    ApplyFollowupRankScore(item, closeInRangeScores[input.Code], "CloseInRange");

                    item.RawScore = item.CloseVsBaseCloseScore + item.TradingValueCompressionScore + item.UpperTailScore + item.CloseInRangeScore;
                    item.ScorePercent = maxRawScore > 0 ? Math.Round((item.RawScore / (double)maxRawScore) * 100.0, 2) : 0;
                    item.Grade = ResolveBaseCandleScoreGrade(item.ScorePercent);
                    item.Summary = BuildBaseCandleFollowupSummary(item);
                }

                items.Add(item);
            }

            ApplyFollowupFinalRanks(items);

            return new BaseCandleFollowupDayResult
            {
                ScoreDate = now.ToString("yyyyMMdd"),
                BaseDate = baseDate,
                Mode = "D1",
                Reason = reason,
                CandidateCount = candidates.Count,
                ReadyCount = readyCount,
                PendingCount = inputs.Count(x => !x.Ready),
                ItemCount = itemCount,
                MaxRawScore = maxRawScore,
                EvaluatedAt = now,
                Candidates = items
            };
        }

        private BaseCandleScoreInput BuildBaseCandleScoreInput(WatchCandidate candidate)
        {
            string code = NormalizeStockCode(candidate.Code);
            string name = IsUsableResolvedName(candidate.Name, code) ? candidate.Name : code;
            double turnover = candidate.StockInfoTurnoverRatePercent ?? ResolveSearch00TurnoverRatePercent(code);

            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(code)) missing.Add("code");
            if (candidate.BaseOpen <= 0 || candidate.BaseHigh <= 0 || candidate.BaseLow <= 0 || candidate.BaseClose <= 0) missing.Add("base_ohlc");
            if (candidate.BaseTradingValue <= 0) missing.Add("base_trading_value");
            if (Math.Abs(candidate.BaseCloseChangeRatePercent) <= 0) missing.Add("change_rate");
            if (turnover <= 0) missing.Add("turnover");

            long range = Math.Max(0, candidate.BaseHigh - candidate.BaseLow);
            double upperTailRate = range > 0
                ? Math.Round(((candidate.BaseHigh - candidate.BaseClose) / (double)range) * 100.0, 4)
                : 100;
            double closeNearHighRate = range > 0
                ? Math.Round(((candidate.BaseClose - candidate.BaseLow) / (double)range) * 100.0, 4)
                : 0;
            double bodyStrengthRate = range > 0
                ? Math.Round((Math.Abs(candidate.BaseClose - candidate.BaseOpen) / (double)range) * 100.0, 4)
                : 0;

            return new BaseCandleScoreInput
            {
                Code = code,
                Name = name,
                Ready = missing.Count == 0,
                PendingReason = string.Join(",", missing),
                StrategyMarket = string.IsNullOrWhiteSpace(candidate.StrategyMarket) ? "PENDING" : candidate.StrategyMarket,
                NxtEnabled = candidate.NxtEnabled,
                BaseTradingValue = candidate.BaseTradingValue,
                ChangeRatePercent = candidate.BaseCloseChangeRatePercent,
                TurnoverRatePercent = turnover,
                UpperTailRate = upperTailRate,
                CloseNearHighRate = closeNearHighRate,
                BodyStrengthRate = bodyStrengthRate
            };
        }

        private BaseCandleFollowupInput BuildBaseCandleFollowupInput(WatchCandidate candidate)
        {
            string code = NormalizeStockCode(candidate.Code);
            string name = IsUsableResolvedName(candidate.Name, code) ? candidate.Name : code;
            string baseDate = NormalizeChartDate(candidate.BaseCandleDate);
            string followupDate = "";

            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(code)) missing.Add("code");
            if (string.IsNullOrWhiteSpace(baseDate)) missing.Add("base_date");
            if (candidate.BaseOpen <= 0 || candidate.BaseHigh <= 0 || candidate.BaseLow <= 0 || candidate.BaseClose <= 0) missing.Add("base_ohlc");

            ChartCandle followupCandle = TryResolveBaseCandleFollowupCandle(code, baseDate);
            if (followupCandle == null)
                missing.Add("followup_candle");

            if (missing.Count > 0 || followupCandle == null)
            {
                return new BaseCandleFollowupInput
                {
                    Code = code,
                    Name = name,
                    Ready = false,
                    PendingReason = string.Join(",", missing)
                };
            }

            followupDate = NormalizeChartDate(followupCandle.Date);
            long followupTradingValue = followupCandle.TradingValue;
            long followupRange = Math.Max(0, followupCandle.High - followupCandle.Low);
            double upperTailRate = followupRange > 0
                ? Math.Round(((followupCandle.High - followupCandle.Close) / (double)followupRange) * 100.0, 4)
                : 100;
            double closeInRangeRate = followupRange > 0
                ? Math.Round(((followupCandle.Close - followupCandle.Low) / (double)followupRange) * 100.0, 4)
                : 0;
            double closeVsBaseCloseRate = candidate.BaseClose > 0
                ? Math.Round(((followupCandle.Close - candidate.BaseClose) / (double)candidate.BaseClose) * 100.0, 4)
                : 0;
            double tradingValueCompressionRate = candidate.BaseTradingValue > 0
                ? Math.Round((candidate.BaseTradingValue / (double)Math.Max(1, followupTradingValue)) * 100.0, 4)
                : 0;

            return new BaseCandleFollowupInput
            {
                Code = code,
                Name = name,
                Ready = true,
                PendingReason = "",
                FollowupDate = followupDate,
                CloseVsBaseCloseRate = closeVsBaseCloseRate,
                TradingValueCompressionRate = tradingValueCompressionRate,
                UpperTailRate = upperTailRate,
                CloseInRangeRate = closeInRangeRate
            };
        }

        private ChartCandle TryResolveBaseCandleFollowupCandle(string code, string baseDate)
        {
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(baseDate))
                return null;

            try
            {
                DailyChartRequestOption option = CreateStockDailyChartRequestOption(
                    displayCode: code,
                    requestCode: code,
                    marketLabel: "KRX",
                    baseDate: DateTime.Now.ToString("yyyyMMdd"));

                DailyChartLoadResult result = Task.Run(() => RequestDailyChartCandlesAsync(option, code)).GetAwaiter().GetResult();
                if (result?.Candles == null || result.Candles.Count == 0)
                    return null;

                List<ChartCandle> ordered = [.. result.Candles
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(NormalizeChartDate(x.Date)))
                    .OrderBy(x => NormalizeChartDate(x.Date))];

                int index = ordered.FindIndex(x => string.Equals(NormalizeChartDate(x.Date), baseDate, StringComparison.OrdinalIgnoreCase));
                if (index < 0 || index + 1 >= ordered.Count)
                    return null;

                return ordered[index + 1];
            }
            catch
            {
                return null;
            }
        }

        private double ResolveSearch00TurnoverRatePercent(string code)
        {
            HoldingStock row = _search00List.FirstOrDefault(x => NormalizeStockCode(x.Code) == NormalizeStockCode(code));
            return ParsePercentOrNumber(row?.TurnoverRateText);
        }

        private Dictionary<string, BaseCandleRankScore> BuildCompetitiveRankScores(
            List<BaseCandleScoreInput> inputs,
            Func<BaseCandleScoreInput, double> selector,
            bool higherBetter)
        {
            int count = inputs.Count;
            var result = new Dictionary<string, BaseCandleRankScore>(StringComparer.OrdinalIgnoreCase);

            foreach (BaseCandleScoreInput item in inputs)
            {
                double value = selector(item);
                int betterCount = inputs.Count(other =>
                {
                    double otherValue = selector(other);
                    return higherBetter ? otherValue > value : otherValue < value;
                });
                int rank = betterCount + 1;
                int score = Math.Max(1, count - rank + 1);
                result[item.Code] = new BaseCandleRankScore { Rank = rank, Score = score };
            }

            return result;
        }

        private Dictionary<string, BaseCandleRankScore> BuildCompetitiveFollowupRankScores(
            List<BaseCandleFollowupInput> inputs,
            Func<BaseCandleFollowupInput, double> selector,
            bool higherBetter)
        {
            int count = inputs.Count;
            var result = new Dictionary<string, BaseCandleRankScore>(StringComparer.OrdinalIgnoreCase);

            foreach (BaseCandleFollowupInput item in inputs)
            {
                double value = selector(item);
                int betterCount = inputs.Count(other =>
                {
                    double otherValue = selector(other);
                    return higherBetter ? otherValue > value : otherValue < value;
                });
                int rank = betterCount + 1;
                int score = Math.Max(1, count - rank + 1);
                result[item.Code] = new BaseCandleRankScore { Rank = rank, Score = score };
            }

            return result;
        }

        private void ApplyRankScore(BaseCandleScoreItem item, BaseCandleRankScore rankScore, string field)
        {
            switch (field)
            {
                case "TradingValue":
                    item.TradingValueRank = rankScore.Rank;
                    item.TradingValueScore = rankScore.Score;
                    break;
                case "ChangeRate":
                    item.ChangeRateRank = rankScore.Rank;
                    item.ChangeRateScore = rankScore.Score;
                    break;
                case "Turnover":
                    item.TurnoverRank = rankScore.Rank;
                    item.TurnoverScore = rankScore.Score;
                    break;
                case "UpperTail":
                    item.UpperTailRank = rankScore.Rank;
                    item.UpperTailScore = rankScore.Score;
                    break;
                case "CloseNearHigh":
                    item.CloseNearHighRank = rankScore.Rank;
                    item.CloseNearHighScore = rankScore.Score;
                    break;
                case "BodyStrength":
                    item.BodyStrengthRank = rankScore.Rank;
                    item.BodyStrengthScore = rankScore.Score;
                    break;
            }
        }

        private void ApplyFinalRanks(List<BaseCandleScoreItem> items)
        {
            List<BaseCandleScoreItem> ready = [.. items.Where(x => string.Equals(x.Status, "READY", StringComparison.OrdinalIgnoreCase))];

            foreach (BaseCandleScoreItem item in ready)
            {
                int betterCount = ready.Count(other => other.RawScore > item.RawScore);
                item.FinalRank = betterCount + 1;
            }
        }

        private void ApplyFollowupRankScore(BaseCandleFollowupScoreItem item, BaseCandleRankScore rankScore, string field)
        {
            switch (field)
            {
                case "CloseVsBaseClose":
                    item.CloseVsBaseCloseRank = rankScore.Rank;
                    item.CloseVsBaseCloseScore = rankScore.Score;
                    break;
                case "TradingValueCompression":
                    item.TradingValueCompressionRank = rankScore.Rank;
                    item.TradingValueCompressionScore = rankScore.Score;
                    break;
                case "UpperTail":
                    item.UpperTailRank = rankScore.Rank;
                    item.UpperTailScore = rankScore.Score;
                    break;
                case "CloseInRange":
                    item.CloseInRangeRank = rankScore.Rank;
                    item.CloseInRangeScore = rankScore.Score;
                    break;
            }
        }

        private void ApplyFollowupFinalRanks(List<BaseCandleFollowupScoreItem> items)
        {
            List<BaseCandleFollowupScoreItem> ready = [.. items.Where(x => string.Equals(x.Status, "READY", StringComparison.OrdinalIgnoreCase))];
            foreach (BaseCandleFollowupScoreItem item in ready)
            {
                int betterCount = ready.Count(other => other.RawScore > item.RawScore);
                item.FinalRank = betterCount + 1;
            }
        }

        private string ResolveBaseCandleScoreGrade(double percent)
        {
            return percent >= 70 ? "A" : "B";
        }

        private string BuildBaseCandleScoreGradeRankText(string grade, int rank)
        {
            grade = string.IsNullOrWhiteSpace(grade) ? "-" : grade.Trim().ToUpperInvariant();
            return rank > 0 && grade != "-" ? $"{grade}{rank}" : grade;
        }

        private int ResolveBaseCandleSuggestedBudgetPercent(double percent)
        {
            if (percent >= 70) return 100;
            return 50;
        }

        private string BuildBaseCandleScoreSummary(BaseCandleScoreItem item)
        {
            var reasons = new List<string>();
            if (item.TradingValueRank == 1) reasons.Add("거래대금 1위");
            if (item.ChangeRateRank == 1) reasons.Add("등락률 1위");
            if (item.TurnoverRank == 1) reasons.Add("회전율 1위");
            if (item.UpperTailRate <= 1) reasons.Add("윗꼬리 짧음");
            if (item.CloseNearHighRate >= 95) reasons.Add("종가 고가권");
            if (item.BodyStrengthRate >= 70) reasons.Add("몸통 강함");
            return reasons.Count == 0 ? "상대순위 기준 보통" : string.Join(", ", reasons);
        }

        private void SaveBaseCandleScoreDayResult(BaseCandleScoreDayResult result)
        {
            Directory.CreateDirectory(_storageDir);

            JObject root;
            if (File.Exists(_baseCandleScorePath))
            {
                try
                {
                    root = JObject.Parse(File.ReadAllText(_baseCandleScorePath));
                }
                catch
                {
                    root = new JObject();
                }
            }
            else
            {
                root = new JObject();
            }

            root["UpdatedAt"] = DateTime.Now;
            root["ScoreMarket"] = "KRX";
            JObject dates = root["Dates"] as JObject ?? new JObject();
            dates[result.BaseDate] = JObject.FromObject(result);
            root["Dates"] = dates;

            File.WriteAllText(_baseCandleScorePath, root.ToString(Formatting.Indented));
        }

        private void SaveBaseCandleFollowupDayResult(BaseCandleFollowupDayResult result)
        {
            Directory.CreateDirectory(_storageDir);

            JObject root;
            if (File.Exists(_baseCandleScorePath))
            {
                try
                {
                    root = JObject.Parse(File.ReadAllText(_baseCandleScorePath));
                }
                catch
                {
                    root = new JObject();
                }
            }
            else
            {
                root = new JObject();
            }

            root["UpdatedAt"] = DateTime.Now;
            root["ScoreMarket"] = "KRX";
            JObject dates = root["Dates"] as JObject ?? new JObject();
            JObject day = dates[result.BaseDate] as JObject ?? new JObject();
            day["FollowupD1"] = JObject.FromObject(result);
            dates[result.BaseDate] = day;
            root["Dates"] = dates;

            File.WriteAllText(_baseCandleScorePath, root.ToString(Formatting.Indented));
        }

        private void ApplyBaseCandleScoreToLeadingGrid()
        {
            if (_search00List == null || _search00List.Count == 0)
                return;

            void Apply()
            {
                foreach (HoldingStock row in _search00List)
                {
                    if (row == null) continue;

                    if (TryResolveBaseCandleGridScore(row.Code, out BaseCandleGridScore score))
                    {
                        row.BaseCandleGradeText = score.Text;
                        row.BaseCandleGradeBrush = score.Brush;
                    }
                    else
                    {
                        row.BaseCandleGradeText = "-";
                        row.BaseCandleGradeBrush = Brushes.White;
                    }
                }
            }

            if (Dispatcher.CheckAccess())
                Apply();
            else
                Dispatcher.Invoke(Apply);
        }

        private bool TryResolveBaseCandleGridScore(string rawCode, out BaseCandleGridScore score)
        {
            score = null;

            string code = NormalizeStockCode(rawCode);
            if (string.IsNullOrWhiteSpace(code) || !File.Exists(_baseCandleScorePath))
                return false;

            string baseDate = "";
            if (_watchCandidates.TryGetValue(code, out WatchCandidate candidate))
                baseDate = NormalizeChartDate(candidate.BaseCandleDate);

            try
            {
                JObject root = JObject.Parse(File.ReadAllText(_baseCandleScorePath));
                JObject dates = root["Dates"] as JObject;
                if (dates == null)
                    return false;

                if (string.IsNullOrWhiteSpace(baseDate))
                    return false;

                JObject day = dates[baseDate] as JObject;
                JObject itemD0 = FindBaseCandleScoreItem(day?["Candidates"] as JArray, code);
                JObject itemD1 = FindBaseCandleScoreItem(day?["FollowupD1"]?["Candidates"] as JArray, code);

                if (itemD0 == null || !string.Equals(itemD0["Status"]?.ToString(), "READY", StringComparison.OrdinalIgnoreCase))
                    return false;

                double scorePercentD0 = ReadBaseCandleScoreDouble(itemD0["ScorePercent"]);
                int rankD0 = ReadBaseCandleScoreInt(itemD0["FinalRank"]);
                string gradeD0 = ResolveBaseCandleScoreGrade(scorePercentD0);
                string text = BuildBaseCandleScoreGradeRankText(gradeD0, rankD0);
                Brush brush = scorePercentD0 >= 70 ? Brushes.LimeGreen : Brushes.Gold;

                if (itemD1 != null && string.Equals(itemD1["Status"]?.ToString(), "READY", StringComparison.OrdinalIgnoreCase))
                {
                    double scorePercentD1 = ReadBaseCandleScoreDouble(itemD1["ScorePercent"]);
                    int rankD1 = ReadBaseCandleScoreInt(itemD1["FinalRank"]);
                    string gradeD1 = ResolveBaseCandleScoreGrade(scorePercentD1);
                    string d1Text = BuildBaseCandleScoreGradeRankText(gradeD1, rankD1);
                    text = MergeBaseCandleGradeText(text, d1Text);
                }

                score = new BaseCandleGridScore
                {
                    Text = text,
                    Brush = brush
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private JObject FindBaseCandleScoreItem(JArray candidates, string code)
        {
            if (candidates == null || string.IsNullOrWhiteSpace(code))
                return null;

            return candidates
                .OfType<JObject>()
                .FirstOrDefault(x => string.Equals(NormalizeStockCode(x["Code"]?.ToString()), code, StringComparison.OrdinalIgnoreCase));
        }

        private double ReadBaseCandleScoreDouble(JToken token)
        {
            if (token == null) return 0;
            return double.TryParse(token.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double value)
                ? value
                : 0;
        }

        private int ReadBaseCandleScoreInt(JToken token)
        {
            if (token == null) return 0;
            return int.TryParse(token.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out int value)
                ? value
                : 0;
        }

        private string MergeBaseCandleGradeText(string d0Text, string d1Text)
        {
            d0Text = (d0Text ?? "").Trim().ToUpperInvariant();
            d1Text = (d1Text ?? "").Trim().ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(d0Text)) return d1Text;
            if (string.IsNullOrWhiteSpace(d1Text)) return d0Text;

            // 등급 문자는 유지하지 않고, D1은 "순위 숫자"만 뒤에 붙인다.
            // 예: B4 + B4 => B44, A1 + B4 => A14
            string d1RankOnly = new string(d1Text.Where(char.IsDigit).ToArray());
            if (string.IsNullOrWhiteSpace(d1RankOnly))
                return d0Text;

            return $"{d0Text}{d1RankOnly}";
        }

        private string BuildBaseCandleFollowupSummary(BaseCandleFollowupScoreItem item)
        {
            var reasons = new List<string>();
            if (item.CloseVsBaseCloseRank == 1) reasons.Add("전일종가 지지 상위");
            if (item.TradingValueCompressionRank == 1) reasons.Add("거래대금 소화 상위");
            if (item.UpperTailRank == 1) reasons.Add("윗꼬리 안정 상위");
            if (item.CloseInRangeRank == 1) reasons.Add("종가 위치 상위");
            return reasons.Count == 0 ? "다음날 상대순위 보통" : string.Join(", ", reasons);
        }

        private sealed class BaseCandleScoreInput
        {
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
            public bool Ready { get; set; }
            public string PendingReason { get; set; } = "";
            public string StrategyMarket { get; set; } = "";
            public bool NxtEnabled { get; set; }
            public long BaseTradingValue { get; set; }
            public double ChangeRatePercent { get; set; }
            public double TurnoverRatePercent { get; set; }
            public double UpperTailRate { get; set; }
            public double CloseNearHighRate { get; set; }
            public double BodyStrengthRate { get; set; }
        }

        private sealed class BaseCandleFollowupInput
        {
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
            public bool Ready { get; set; }
            public string PendingReason { get; set; } = "";
            public string FollowupDate { get; set; } = "";
            public double CloseVsBaseCloseRate { get; set; }
            public double TradingValueCompressionRate { get; set; }
            public double UpperTailRate { get; set; }
            public double CloseInRangeRate { get; set; }
        }

        private sealed class BaseCandleRankScore
        {
            public int Rank { get; set; }
            public int Score { get; set; }
        }

        private sealed class BaseCandleScoreDayResult
        {
            public string ScoreDate { get; set; } = "";
            public string BaseDate { get; set; } = "";
            public string Mode { get; set; } = "";
            public string ScoreMarket { get; set; } = "";
            public string Reason { get; set; } = "";
            public int CandidateCount { get; set; }
            public int ReadyCount { get; set; }
            public int PendingCount { get; set; }
            public int ItemCount { get; set; }
            public int MaxRawScore { get; set; }
            public DateTime EvaluatedAt { get; set; }
            public List<BaseCandleScoreItem> Candidates { get; set; } = [];
        }

        private sealed class BaseCandleScoreItem
        {
            public string Status { get; set; } = "";
            public string PendingReason { get; set; } = "";
            public int FinalRank { get; set; }
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
            public string BaseDate { get; set; } = "";
            public string ScoreMarket { get; set; } = "";
            public string StrategyMarket { get; set; } = "";
            public bool NxtEnabled { get; set; }
            public long BaseTradingValue { get; set; }
            public double ChangeRatePercent { get; set; }
            public double TurnoverRatePercent { get; set; }
            public double UpperTailRate { get; set; }
            public double CloseNearHighRate { get; set; }
            public double BodyStrengthRate { get; set; }
            public int TradingValueRank { get; set; }
            public int TradingValueScore { get; set; }
            public int ChangeRateRank { get; set; }
            public int ChangeRateScore { get; set; }
            public int TurnoverRank { get; set; }
            public int TurnoverScore { get; set; }
            public int UpperTailRank { get; set; }
            public int UpperTailScore { get; set; }
            public int CloseNearHighRank { get; set; }
            public int CloseNearHighScore { get; set; }
            public int BodyStrengthRank { get; set; }
            public int BodyStrengthScore { get; set; }
            public int RawScore { get; set; }
            public int MaxRawScore { get; set; }
            public double ScorePercent { get; set; }
            public string Grade { get; set; } = "";
            public int SuggestedBudgetPercent { get; set; }
            public string Summary { get; set; } = "";
        }

        private sealed class BaseCandleFollowupDayResult
        {
            public string ScoreDate { get; set; } = "";
            public string BaseDate { get; set; } = "";
            public string Mode { get; set; } = "";
            public string Reason { get; set; } = "";
            public int CandidateCount { get; set; }
            public int ReadyCount { get; set; }
            public int PendingCount { get; set; }
            public int ItemCount { get; set; }
            public int MaxRawScore { get; set; }
            public DateTime EvaluatedAt { get; set; }
            public List<BaseCandleFollowupScoreItem> Candidates { get; set; } = [];
        }

        private sealed class BaseCandleFollowupScoreItem
        {
            public string Status { get; set; } = "";
            public string PendingReason { get; set; } = "";
            public int FinalRank { get; set; }
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
            public string BaseDate { get; set; } = "";
            public string FollowupDate { get; set; } = "";
            public double CloseVsBaseCloseRate { get; set; }
            public double TradingValueCompressionRate { get; set; }
            public double UpperTailRate { get; set; }
            public double CloseInRangeRate { get; set; }
            public int CloseVsBaseCloseRank { get; set; }
            public int CloseVsBaseCloseScore { get; set; }
            public int TradingValueCompressionRank { get; set; }
            public int TradingValueCompressionScore { get; set; }
            public int UpperTailRank { get; set; }
            public int UpperTailScore { get; set; }
            public int CloseInRangeRank { get; set; }
            public int CloseInRangeScore { get; set; }
            public int RawScore { get; set; }
            public int MaxRawScore { get; set; }
            public double ScorePercent { get; set; }
            public string Grade { get; set; } = "";
            public string Summary { get; set; } = "";
        }

        private sealed class BaseCandleGridScore
        {
            public string Text { get; set; } = "-";
            public Brush Brush { get; set; } = Brushes.White;
        }
    }
}
