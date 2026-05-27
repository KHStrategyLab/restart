#nullable disable

using KHStrategyLab.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private DispatcherTimer _livePositionExitTimer;
        private bool _livePositionExitRunning = false;

        private void InitializeLivePositionExitTimer()
        {
            _livePositionExitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _livePositionExitTimer.Tick += async (s, e) => { await RunLivePositionExitMonitorAsync(); };
            _livePositionExitTimer.Start();
        }

        private async Task RunLivePositionExitMonitorAsync()
        {
            if (_livePositionExitRunning) return;
            if (!_isHunting) return;

            List<ProgramManagedPosition> positions;
            lock (_liveOrderStateLock)
            {
                positions = [.. _programManagedPositionsByCode.Values
                    .Where(x => x != null)
                    .Where(x => !x.SellCompleted)
                    .Where(x => !x.SellOrderInProgress)];
            }

            if (positions.Count == 0)
                return;

            _livePositionExitRunning = true;

            try
            {
                foreach (ProgramManagedPosition position in positions)
                {
                    long currentPrice = ResolveProgramPositionCurrentPrice(position);
                    if (currentPrice <= 0)
                        continue;

                    UpdateTrailingHighPrice(position, currentPrice);

                    string exitReason = "";
                    int sellQuantity = position.EntryQuantity;
                    bool partialSell = false;

                    if (IsMaRecoveryDynamicExitPosition(position))
                    {
                        exitReason = ResolveMaRecoveryDynamicExitReason(position, currentPrice, out sellQuantity, out partialSell);
                    }
                    else
                    {
                        if (position.StopPrice > 0 && currentPrice <= position.StopPrice)
                            exitReason = "STOP_LOSS";
                        else if (position.TargetPrice > 0 && currentPrice >= position.TargetPrice)
                            exitReason = "TARGET_REACHED";
                    }

                    if (string.IsNullOrWhiteSpace(exitReason))
                        continue;

                    Log($"🚨 [자동매도 조건] {position.Name}({position.Code}) / 사유={exitReason} / 현재가={currentPrice:N0} / 목표={position.TargetPrice:N0} / 손절={position.StopPrice:N0} / 수량={sellQuantity:N0}{(partialSell ? " / 부분매도" : "")}");
                    await SendTelegramMessageAsync(
                        $"[KHStrategyLab] 매도 조건 발생\n{position.Name}({position.Code})\n전략: {position.StrategyCode}\n사유: {exitReason}\n현재가: {currentPrice:N0}\n목표가: {position.TargetPrice:N0}\n손절가: {position.StopPrice:N0}");

                    await TryExecuteLiveSellAsync(position, exitReason, currentPrice, sellQuantity, partialSell);
                    await Task.Delay(100);
                }
            }
            finally
            {
                _livePositionExitRunning = false;
            }
        }

        private async Task TryExecuteLiveSellAsync(ProgramManagedPosition position, string exitReason, long currentPrice, int sellQuantity = 0, bool partialSell = false)
        {
            if (position == null)
                return;

            string code = NormalizeStockCode(position.Code);
            if (string.IsNullOrWhiteSpace(code))
                return;

            if (!_liveOrderEnabled)
            {
                Log($"[실주문상태] LiveOrderEnabled=OFF / 자동매도 조건만 발생 / 주문전송 없음 / {position.Name}({code}) / 사유={exitReason}");
                await SendTelegramMessageAsync($"[KHStrategyLab] 실주문 OFF 매도차단\n{position.Name}({code})\n사유: {exitReason}\n주문은 전송하지 않음");
                return;
            }

            if (!TryResolveLiveOrderMarket(position.Market, out string orderMarket, out string blockReason))
            {
                Log($"⛔ [매도 주문 차단] {position.Name}({code}) / 사유={blockReason}");
                await SendTelegramMessageAsync($"[KHStrategyLab] 매도 주문 차단\n{position.Name}({code})\n사유: {blockReason}");
                return;
            }

            sellQuantity = sellQuantity > 0 ? Math.Min(sellQuantity, position.EntryQuantity) : position.EntryQuantity;
            if (sellQuantity <= 0)
            {
                Log($"⛔ [매도 주문 차단] {position.Name}({code}) / 사유=매도수량 0");
                return;
            }

            lock (_liveOrderStateLock)
            {
                if (_liveOrderInProgressByCode.ContainsKey(code) || position.SellOrderInProgress)
                {
                    Log($"⛔ [매도 중복차단] {position.Name}({code}) / 이미 매도 주문 진행 중");
                    return;
                }

                position.SellOrderInProgress = true;
                position.SellRequestedAt = DateTime.Now;
                position.ExitReason = exitReason;
                _liveOrderInProgressByCode[code] = new LiveOrderPendingState
                {
                    Code = code,
                    Name = position.Name,
                    StrategyCode = position.StrategyCode,
                    LastOrderStrategyCode = position.StrategyCode,
                    LastOrderRequestedAt = DateTime.Now,
                    Side = "SELL"
                };
            }

            try
            {
                LiveOrderApiResult orderResult = await SendKiwoomOrderAsync(
                    apiId: "kt10001",
                    side: "SELL",
                    market: orderMarket,
                    code: code,
                    quantity: sellQuantity,
                    orderPrice: currentPrice,
                    tradeType: "0");

                if (!orderResult.Success)
                {
                    Log($"❌ [매도 주문 실패] {position.Name}({code}) / 전략={position.StrategyCode} / 사유={exitReason} / 응답={orderResult.Message}");
                    await SendTelegramMessageAsync($"[KHStrategyLab] 매도 주문 실패\n{position.Name}({code})\n전략: {position.StrategyCode}\n사유: {exitReason}\n응답: {orderResult.Message}");
                    return;
                }

                lock (_liveOrderStateLock)
                {
                    _tradedStrategyKeysToday.Add(BuildOrderedStrategyKey(DateTime.Today, code, position.StrategyCode));
                    position.SellOrderNo = orderResult.OrderNo;
                    position.SellOrderInProgress = !partialSell;
                    if (partialSell && sellQuantity < position.EntryQuantity)
                    {
                        position.EntryQuantity -= sellQuantity;
                        position.TrailingPartialSellDone = true;
                        position.ExitReason = exitReason;
                    }
                    else
                    {
                        position.SellCompleted = true;
                    }
                }

                SaveLiveOrderState();

                Log($"✅ [매도 주문 접수] {position.Name}({code}) / 전략={position.StrategyCode} / 사유={exitReason} / 현재가={currentPrice:N0} / 목표={position.TargetPrice:N0} / 손절={position.StopPrice:N0} / 수량={sellQuantity:N0}{(partialSell ? $" / 잔량={position.EntryQuantity:N0}" : "")} / 시장={orderMarket} / 주문번호={orderResult.OrderNo} / 잔고 재조회 예약");
                await SendTelegramMessageAsync(
                    $"[KHStrategyLab] 매도 주문 접수\n{position.Name}({code})\n전략: {position.StrategyCode}\n사유: {exitReason}\n수량: {sellQuantity:N0}{(partialSell ? $"\n잔량: {position.EntryQuantity:N0}" : "")}\n시장: {orderMarket}\n주문번호: {orderResult.OrderNo}\n잔고 재조회 예약");

                await SyncBalanceAfterOrderAsync($"매도 주문 접수 {position.Name}({code})");
            }
            finally
            {
                lock (_liveOrderStateLock)
                {
                    _liveOrderInProgressByCode.Remove(code);

                    if (string.IsNullOrWhiteSpace(position.SellOrderNo))
                        position.SellOrderInProgress = false;
                }
            }
        }

        private long ResolveProgramPositionCurrentPrice(ProgramManagedPosition position)
        {
            if (position == null)
                return 0;

            string code = NormalizeStockCode(position.Code);
            long currentPrice = 0;

            Dispatcher.Invoke(() =>
            {
                HoldingStock holding = _balance.FirstOrDefault(x => x != null && NormalizeStockCode(x.Code) == code);
                if (holding?.CurrentPrice > 0)
                    currentPrice = holding.CurrentPrice;
            });

            if (currentPrice > 0)
                return currentPrice;

            if (_watchCandidates.TryGetValue(code, out WatchCandidate candidate) && candidate?.LastPrice > 0)
                return candidate.LastPrice;

            return position.EntryPrice;
        }

        private bool IsMaRecoveryDynamicExitPosition(ProgramManagedPosition position)
        {
            return string.Equals(position?.ExitMode, "TEN_MIN_CLOSE_BELOW_MA60_TRAILING_5_2_80", StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateTrailingHighPrice(ProgramManagedPosition position, long currentPrice)
        {
            if (position == null || currentPrice <= 0) return;

            if (position.TrailingHighPrice <= 0)
                position.TrailingHighPrice = Math.Max(position.EntryPrice, currentPrice);
            else if (currentPrice > position.TrailingHighPrice)
                position.TrailingHighPrice = currentPrice;
        }

        private string ResolveMaRecoveryDynamicExitReason(ProgramManagedPosition position, long currentPrice, out int sellQuantity, out bool partialSell)
        {
            sellQuantity = position.EntryQuantity;
            partialSell = false;

            if (IsTenMinuteCloseBelowLatestMa60(position, out string maReason))
                return $"TEN_MIN_CLOSE_BELOW_MA60 / {maReason}";

            if (!position.TrailingPartialSellDone &&
                position.EntryPrice > 0 &&
                position.TrailingStartRatePercent > 0 &&
                currentPrice >= position.EntryPrice * (1.0 + position.TrailingStartRatePercent / 100.0))
            {
                if (!position.TrailingActivated)
                {
                    position.TrailingActivated = true;
                    position.TrailingHighPrice = Math.Max(position.TrailingHighPrice, currentPrice);
                    SaveLiveOrderState();
                    Log($"📈 [트레일링 활성] {position.Name}({position.Code}) / 진입={position.EntryPrice:N0} / 현재={currentPrice:N0} / 시작={position.TrailingStartRatePercent:0.##}% / 고가={position.TrailingHighPrice:N0}");
                }
            }

            if (position.TrailingActivated &&
                !position.TrailingPartialSellDone &&
                position.TrailingHighPrice > 0 &&
                position.TrailingDropRatePercent > 0)
            {
                double dropRate = (position.TrailingHighPrice - currentPrice) / (double)position.TrailingHighPrice * 100.0;
                if (dropRate >= position.TrailingDropRatePercent)
                {
                    partialSell = true;
                    int percent = position.TrailingSellPercent > 0 ? position.TrailingSellPercent : 80;
                    sellQuantity = Math.Max(1, (int)Math.Floor(position.EntryQuantity * (percent / 100.0)));
                    return $"TRAILING_{position.TrailingStartRatePercent:0.##}_DROP_{position.TrailingDropRatePercent:0.##}_SELL_{percent}% / 고가={position.TrailingHighPrice:N0} / 하락={dropRate:0.##}%";
                }
            }

            return "";
        }

        private bool IsTenMinuteCloseBelowLatestMa60(ProgramManagedPosition position, out string reason)
        {
            reason = "";
            string code = NormalizeStockCode(position?.Code);
            string market = ResolveProgramPositionMinuteCacheMarket(position);

            if (string.IsNullOrWhiteSpace(code) || !IsKnownMinuteCacheMarket(market))
            {
                reason = "시장미확정";
                return false;
            }

            if (!TryGetReadyCandidateMinuteCache(code, market, out CandidateMinuteCache cache) ||
                cache?.TenMinuteCompletedCandles == null ||
                cache.TenMinuteCompletedCandles.Count == 0)
            {
                QueueLoadCandidateMinuteCache(code, market, "POSITION_EXIT_MA60_WAIT");
                reason = "분봉캐시 미준비";
                return false;
            }

            ChartCandle latest = cache.TenMinuteCompletedCandles
                .Where(x => x != null && x.Close > 0 && x.MA60 > 0)
                .OrderBy(ParseMinuteCandleDateTime)
                .LastOrDefault();

            if (latest == null)
            {
                reason = "10분 완성봉 MA60 부족";
                return false;
            }

            reason = $"종가={latest.Close:N0} / MA60={latest.MA60:N0} / 시장={market}";
            return latest.Close < latest.MA60;
        }

        private string ResolveProgramPositionMinuteCacheMarket(ProgramManagedPosition position)
        {
            string market = (position?.Market ?? "").Trim().ToUpperInvariant();
            if (market == "KRX" || market == "NXT")
                return market;

            string code = NormalizeStockCode(position?.Code);
            if (_watchCandidates.TryGetValue(code, out WatchCandidate candidate))
            {
                string candidateMarket = (candidate?.StrategyMarket ?? "").Trim().ToUpperInvariant();
                if (candidateMarket == "KRX" || candidateMarket == "NXT")
                    return candidateMarket;
            }

            return market == "SOR" ? "NXT" : "KRX";
        }
    }
}
