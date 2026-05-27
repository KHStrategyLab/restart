#nullable disable

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private readonly object _liveOrderStateLock = new();
        private readonly Dictionary<string, LiveOrderPendingState> _liveOrderInProgressByCode = [];
        private readonly Dictionary<string, ProgramManagedPosition> _programManagedPositionsByCode = [];
        private readonly HashSet<string> _tradedStrategyKeysToday = [];
        private DateTime _liveOrderStateDate = DateTime.Today;

        private string LiveOrderStatePath => Path.Combine(_storageDir, "program_live_order_state.json");

        private void LoadLiveOrderState()
        {
            try
            {
                lock (_liveOrderStateLock)
                {
                    _liveOrderInProgressByCode.Clear();
                    _programManagedPositionsByCode.Clear();
                    _tradedStrategyKeysToday.Clear();
                    _liveOrderStateDate = DateTime.Today;

                    if (!File.Exists(LiveOrderStatePath))
                        return;

                    string text = File.ReadAllText(LiveOrderStatePath, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(text))
                        return;

                    LiveOrderStateFile state = JsonConvert.DeserializeObject<LiveOrderStateFile>(text) ?? new LiveOrderStateFile();

                    if (DateTime.TryParse(state.StateDate, out DateTime stateDate))
                        _liveOrderStateDate = stateDate.Date;

                    foreach (ProgramManagedPosition position in state.ProgramPositions ?? [])
                    {
                        string code = NormalizeStockCode(position.Code);
                        if (string.IsNullOrWhiteSpace(code))
                            continue;

                        position.Code = code;
                        _programManagedPositionsByCode[code] = position;
                    }

                    if (_liveOrderStateDate == DateTime.Today)
                    {
                        foreach (string key in (state.TradedStrategyKeysToday ?? []).Concat(state.OrderedStrategyKeysToday ?? []))
                        {
                            if (!string.IsNullOrWhiteSpace(key))
                                _tradedStrategyKeysToday.Add(key);
                        }
                    }
                }

                Log("[실주문상태] 프로그램 매수 종목 관리 상태 로드 완료");
            }
            catch (Exception ex)
            {
                Log($"⚠️ [실주문상태] 프로그램 매수 상태 로드 실패: {ex.Message}");
            }
        }

        private void SaveLiveOrderState()
        {
            try
            {
                LiveOrderStateFile state;

                lock (_liveOrderStateLock)
                {
                    ResetDailyLiveOrderStateIfNeededLocked();

                    state = new LiveOrderStateFile
                    {
                        StateDate = _liveOrderStateDate.ToString("yyyy-MM-dd"),
                        ProgramPositions = [.. _programManagedPositionsByCode.Values.OrderBy(x => x.Code)],
                        TradedStrategyKeysToday = [.. _tradedStrategyKeysToday.OrderBy(x => x)]
                    };
                }

                Directory.CreateDirectory(_storageDir);
                File.WriteAllText(LiveOrderStatePath, JsonConvert.SerializeObject(state, Formatting.Indented), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log($"⚠️ [실주문상태] 프로그램 매수 상태 저장 실패: {ex.Message}");
            }
        }

        private void ResetDailyLiveOrderStateIfNeededLocked()
        {
            if (_liveOrderStateDate == DateTime.Today)
                return;

            _liveOrderStateDate = DateTime.Today;
            _tradedStrategyKeysToday.Clear();
            _liveOrderInProgressByCode.Clear();
        }

        private string BuildOrderedStrategyKey(DateTime date, string code, string strategyCode)
        {
            return $"{date:yyyyMMdd}|{NormalizeStockCode(code)}|{(strategyCode ?? "").Trim().ToUpperInvariant()}";
        }

        private sealed class LiveOrderSignal
        {
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
            public string StrategyName { get; set; } = "";
            public string StrategyCode { get; set; } = "";
            public string StrategyGroup { get; set; } = "";
            public string Market { get; set; } = "";
            public long SignalPrice { get; set; }
            public long OrderPrice { get; set; }
            public long TargetPrice { get; set; }
            public long StopPrice { get; set; }
            public DateTime SignalTime { get; set; } = DateTime.Now;
            public string EntrySource { get; set; } = "STRATEGY_SIGNAL";
            public string BaseCandleDate { get; set; } = "";
            public string ExitMode { get; set; } = "";
            public double TrailingStartRatePercent { get; set; }
            public double TrailingDropRatePercent { get; set; }
            public int TrailingSellPercent { get; set; }
        }

        private sealed class LiveOrderRiskResult
        {
            public bool Allowed { get; set; }
            public string BlockReason { get; set; } = "";
            public string OrderMarket { get; set; } = "";
            public long Budget { get; set; }
            public long OriginalBudget { get; set; }
            public int BudgetPercent { get; set; } = 100;
            public string BudgetSource { get; set; } = "DEFAULT";
            public string BudgetScoreGrade { get; set; } = "";
            public double BudgetScorePercent { get; set; }
            public int Quantity { get; set; }
            public long OrderPrice { get; set; }
            public long ExpectedAmount { get; set; }
            public bool IsHolding { get; set; }
            public bool IsOrderInProgress { get; set; }
            public bool IsAlreadyOrderedToday { get; set; }
            public int CurrentSlotCount { get; set; }
            public int MaxSlots { get; set; }
        }

        private sealed class LiveOrderPendingState
        {
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
            public string StrategyCode { get; set; } = "";
            public DateTime LastBuySignalAt { get; set; }
            public string LastOrderNo { get; set; } = "";
            public string LastOrderStrategyCode { get; set; } = "";
            public DateTime LastOrderRequestedAt { get; set; }
            public string LastOrderResultMessage { get; set; } = "";
            public string Side { get; set; } = "";
        }

        private sealed class ProgramManagedPosition
        {
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
            public string StrategyCode { get; set; } = "";
            public string StrategyGroup { get; set; } = "";
            public string Market { get; set; } = "";
            public long EntryPrice { get; set; }
            public int EntryQuantity { get; set; }
            public string BuyOrderNo { get; set; } = "";
            public DateTime EntryTime { get; set; }
            public long TargetPrice { get; set; }
            public long StopPrice { get; set; }
            public string EntrySource { get; set; } = "PROGRAM_LIVE_ORDER";
            public string ExitMode { get; set; } = "";
            public double TrailingStartRatePercent { get; set; }
            public double TrailingDropRatePercent { get; set; }
            public int TrailingSellPercent { get; set; }
            public bool TrailingActivated { get; set; }
            public bool TrailingPartialSellDone { get; set; }
            public long TrailingHighPrice { get; set; }
            public bool LiveOrder { get; set; }
            public bool SellCompleted { get; set; }
            public bool SellOrderInProgress { get; set; }
            public string SellOrderNo { get; set; } = "";
            public DateTime? SellRequestedAt { get; set; }
            public string ExitReason { get; set; } = "";
        }

        private sealed class LiveOrderStateFile
        {
            public string StateDate { get; set; } = "";
            public List<ProgramManagedPosition> ProgramPositions { get; set; } = [];
            public List<string> OrderedStrategyKeysToday { get; set; } = [];
            public List<string> TradedStrategyKeysToday { get; set; } = [];
        }
    }
}
