#nullable disable

using KHStrategyLab.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private bool _leadingMaSignalGridColumnsInitialized = false;
        private DispatcherTimer _leadingMaSignalBootstrapTimer;
        private readonly Dictionary<string, LeadingMaSignalState> _leadingMaSignalStates = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _leadingMaSignalLoadingCodes = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastLeadingMaSignalLoadLogAt = DateTime.MinValue;
        private DateTime _lastLeadingMaSignalBootstrapAt = DateTime.MinValue;
        private DateTime _leadingMaSignalBootstrapAllowedAt = DateTime.MinValue;
        private bool _leadingMaSignalBootstrapDelayLogged = false;

        private const int LeadingTenMinuteCompletedSeedCount = 100;
        private const int LeadingTenMinuteMaSeedCount = 60;
        private const int LeadingFiveMinuteHighSeedCount = 20;
        private const int LeadingReloadGapSeconds = 90;
        private const int LeadingLoadRetrySeconds = 60;

        private void InitializeLeadingMaSignalGridColumns()
        {
            try
            {
                if (!_leadingMaSignalGridColumnsInitialized)
                {
                    if (GridLeading == null) return;

                    _leadingMaSignalGridColumnsInitialized = true;
                    AddLeadingSignalColumnIfMissing("기준봉", "BaseCandleGradeText", "BaseCandleGradeBrush", 68);
                    // AddLeadingTextColumnIfMissing("10분5", "Ma5Text", 72);
                    // AddLeadingTextColumnIfMissing("10분20", "Ma20Text", 72);
                    // AddLeadingTextColumnIfMissing("10분60", "Ma60Text", 72);
                    AddLeadingSignalColumnIfMissing("신호", "MaSignalText", "MaSignalBrush", 92);
                }

                StartLeadingMaSignalBootstrapTimer();
            }
            catch (Exception ex)
            {
                Log($"⚠️ [MA신호등 컬럼 초기화 오류] {ex.Message}");
            }
        }

        private void AddLeadingTextColumnIfMissing(string header, string bindingPath, double width)
        {
            if (GridLeading.Columns.Any(c => string.Equals(c.Header?.ToString(), header, StringComparison.OrdinalIgnoreCase))) return;

            GridLeading.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Width = new DataGridLength(width),
                Binding = new Binding(bindingPath),
                ElementStyle = CreateLeadingMaTextStyle(TextAlignment.Right, Brushes.White),
                IsReadOnly = true
            });
        }

        private void AddLeadingSignalColumnIfMissing(string header, string bindingPath, string brushPath, double width)
        {
            if (GridLeading.Columns.Any(c => string.Equals(c.Header?.ToString(), header, StringComparison.OrdinalIgnoreCase))) return;

            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(0)));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(brushPath)));
            style.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));

            GridLeading.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Width = new DataGridLength(width),
                Binding = new Binding(bindingPath),
                ElementStyle = style,
                IsReadOnly = true
            });
        }

        private static Style CreateLeadingMaTextStyle(TextAlignment alignment, Brush foreground)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, alignment));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(0)));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, foreground));
            return style;
        }

        private void StartLeadingMaSignalBootstrapTimer()
        {
            if (_leadingMaSignalBootstrapTimer != null) return;

            _leadingMaSignalBootstrapTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };

            _leadingMaSignalBootstrapTimer.Tick += async (s, e) =>
            {
                await RefreshLeadingMaSignalGridOnceAsync("BOOTSTRAP_TIMER");
            };

            _leadingMaSignalBootstrapTimer.Start();
        }

        private void DelayLeadingMaSignalBootstrap(TimeSpan delay, string reason)
        {
            try
            {
                _leadingMaSignalBootstrapAllowedAt = DateTime.Now.Add(delay);
                _leadingMaSignalBootstrapDelayLogged = false;
                Log($"🚦 [MA신호등] 시장분리 대기 후 초기로드 예정 / 대기={delay.TotalSeconds:N0}초 / 사유={reason}");
            }
            catch
            {
                // 초기화 보조 함수라서 실패해도 프로그램 흐름을 막지 않는다.
            }
        }

        private void ReleaseLeadingMaSignalBootstrap(string reason)
        {
            try
            {
                _leadingMaSignalBootstrapAllowedAt = DateTime.MinValue;
                _leadingMaSignalBootstrapDelayLogged = false;
                Log($"🚦 [MA신호등] 초기로드 잠금 해제 / 사유={reason}");
                _ = RefreshLeadingMaSignalGridOnceAsync(reason);
            }
            catch (Exception ex)
            {
                Log($"⚠️ [MA신호등 초기로드 해제 오류] {reason} / {ex.Message}");
            }
        }

        // MainWindow.xaml.cs Loaded 이후, 또는 부트스트랩 타이머가 호출한다.
        // 조건00 후보의 분봉 씨앗을 후보+시장별로 1회 로드한다.
        private async Task RefreshLeadingMaSignalGridOnceAsync(string reason)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_token)) return;
                if (_search00List == null || _search00List.Count == 0) return;

                if (_leadingMaSignalBootstrapAllowedAt != DateTime.MinValue &&
                    DateTime.Now < _leadingMaSignalBootstrapAllowedAt)
                {
                    if (!_leadingMaSignalBootstrapDelayLogged)
                    {
                        _leadingMaSignalBootstrapDelayLogged = true;
                        Log($"🚦 [MA신호등] 조건00 시장분리 대기 중 / 허용시각={_leadingMaSignalBootstrapAllowedAt:HH:mm:ss} / 사유={reason}");
                    }
                    return;
                }

                if ((DateTime.Now - _lastLeadingMaSignalBootstrapAt).TotalSeconds < 10) return;
                _lastLeadingMaSignalBootstrapAt = DateTime.Now;

                List<(string Code, string Market)> targets = [.. _search00List
                    .Select(x => new { Row = x, Code = NormalizeStockCode(x.Code) })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Code))
                    .Select(x =>
                    {
                        bool resolved = TryResolveKnownLeadingMaSignalMarket(x.Code, out string market);
                        return (Code: x.Code, Market: resolved ? market : "");
                    })
                    .Where(x => x.Market == "KRX" || x.Market == "NXT")
                    .Distinct()];

                if (targets.Count == 0) return;

                bool queuedAny = false;
                foreach (var target in targets)
                {
                    string key = BuildLeadingMaSignalKey(target.Code, target.Market);
                    LeadingMaSignalState state = GetLeadingMaSignalState(target.Code, target.Market);

                    if (state.IsSeedReady) continue;

                    string oppositeMarket = target.Market == "NXT" ? "KRX" : "NXT";
                    string oppositeKey = BuildLeadingMaSignalKey(target.Code, oppositeMarket);
                    if (_leadingMaSignalStates.TryGetValue(oppositeKey, out LeadingMaSignalState oppositeState) && oppositeState.IsSeedReady)
                    {
                        Log($"🚦 [MA신호등] 시장보정 로드 예약: {target.Code} / 기존={oppositeMarket} → 확정={target.Market} / 사유={reason}");
                    }

                    if (state.LastLoadAttemptAt != DateTime.MinValue &&
                        (DateTime.Now - state.LastLoadAttemptAt).TotalSeconds < LeadingLoadRetrySeconds)
                    {
                        continue;
                    }

                    QueueLoadLeadingMaSignalInitialBars(target.Code, target.Market, reason);
                    queuedAny = true;
                }

                if (queuedAny)
                {
                    Log($"🚦 [MA신호등] 초기/복구 분봉 로드 예약 / 사유={reason} / 대상={targets.Count}개 / 미로드·실패·시장보정 종목만 처리");
                }

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Log($"⚠️ [MA신호등 초기/복구 예약 오류] {reason} / {ex.Message}");
            }
        }

        // MainWindow.WebSocket.RealtimeTrade.cs에서 0B 수신 후 호출한다.
        // TOP20은 여기로 오지 않고, 조건00 추적후보(_search00List)만 MA 신호등을 갱신한다.
        private void UpdateLeadingMaSignalFromRealtimeTick(RealtimeTradeSnapshot snapshot, string code)
        {
            try
            {
                if (snapshot == null) return;
                UpdateLeadingMaSignalFromRealtimeTick(code, snapshot.CurrentPrice, snapshot.IsNxtSnapshot);
            }
            catch (Exception ex)
            {
                Log($"⚠️ [MA신호등 0B 갱신 오류] {code} / {ex.Message}");
            }
        }

        private void UpdateLeadingMaSignalFromRealtimeTick(string code, long currentPrice, bool isNxtSnapshot)
        {
            try
            {
                string baseCode = NormalizeStockCode(code);
                if (string.IsNullOrWhiteSpace(baseCode) || currentPrice <= 0) return;

                HoldingStock row = _search00List.FirstOrDefault(x => NormalizeStockCode(x.Code) == baseCode);
                if (row == null) return;

                if (!TryResolveKnownLeadingMaSignalMarket(baseCode, out string market))
                {
                    row.Ma5Text = "-";
                    row.Ma20Text = "-";
                    row.Ma60Text = "-";
                    row.MaSignalText = "시장대기";
                    row.MaSignalBrush = Brushes.Gray;
                    LogMarketUnresolvedStrategyBlocked(baseCode);
                    return;
                }

                LeadingMaSignalState state = GetLeadingMaSignalState(baseCode, market);
                CandidateMinuteCache cache = GetCandidateMinuteCache(baseCode, market);

                DateTime now = DateTime.Now;
                bool hadLongGap = state.LastRealtimeAt != DateTime.MinValue &&
                                  (now - state.LastRealtimeAt).TotalSeconds >= LeadingReloadGapSeconds;

                state.LastPrice = currentPrice;
                state.LastRealtimeAt = now;

                if (hadLongGap)
                {
                    state.IsSeedReady = false;
                    state.LoadStatus = "RELOAD_REQUIRED_AFTER_GAP";
                    cache.IsSeedReady = false;
                    cache.LoadStatus = "RELOAD_REQUIRED_AFTER_GAP";
                    // 갭 복구를 예약하는 시점의 현재시각을 캐시에도 기록해,
                    // 로드 완료 후 과거 LastRealtimeAt 값으로 되돌아가며 재로드 루프가 나는 것을 막는다.
                    cache.LastRealtimeAt = now;
                    row.MaSignalText = "재로드";
                    row.MaSignalBrush = Brushes.Gray;
                    Log($"⚠️ [MinuteCache] 재로드 예약: {baseCode} / 시장={market} / 사유=REALTIME_GAP_RELOAD");
                    QueueLoadLeadingMaSignalInitialBars(baseCode, market, "REALTIME_GAP_RELOAD");
                    return;
                }

                if (!state.IsSeedReady)
                {
                    row.Ma5Text = state.LoadStatus == "LOAD_FAILED" ? "실패" : "로딩";
                    row.Ma20Text = state.LoadStatus == "LOAD_FAILED" ? "실패" : "로딩";
                    row.Ma60Text = state.LoadStatus == "LOAD_FAILED" ? "실패" : "로딩";
                    row.MaSignalText = state.LoadStatus == "LOAD_FAILED" ? "분봉없음" : "대기";
                    row.MaSignalBrush = Brushes.Gray;
                    QueueLoadLeadingMaSignalInitialBars(baseCode, market, "REALTIME_FIRST_TICK_LOAD");
                    return;
                }

                ApplyRealtimeTickToCandidateMinuteCache(baseCode, market, currentPrice);
                CopyMinuteCacheToLeadingState(cache, state);
                ApplyLeadingMaSignalToRowOnUi(row, state, currentPrice, includeCurrentPrice: true);
            }
            catch (Exception ex)
            {
                Log($"⚠️ [MA신호등 갱신 오류] {code} / {ex.Message}");
            }
        }

        private bool TryResolveKnownLeadingMaSignalMarket(string code, out string market)
        {
            market = "";

            try
            {
                string baseCode = NormalizeStockCode(code);
                if (string.IsNullOrWhiteSpace(baseCode) || !_watchCandidates.TryGetValue(baseCode, out WatchCandidate candidate))
                    return false;

                string[] candidates =
                [
                    candidate.StrategyMarket,
                    candidate.MinuteChartMarket,
                    candidate.RealtimePriceMarket
                ];

                foreach (string value in candidates)
                {
                    string normalized = (value ?? "").Trim().ToUpperInvariant();
                    if (normalized == "NXT" || normalized == "KRX")
                    {
                        market = normalized;
                        return true;
                    }
                }
            }
            catch
            {
                market = "";
            }

            return false;
        }

        private void QueueLoadLeadingMaSignalForResolvedCandidate(string code, string reason)
        {
            if (string.IsNullOrWhiteSpace(_token)) return;

            string baseCode = NormalizeStockCode(code);
            if (string.IsNullOrWhiteSpace(baseCode)) return;
            if (!TryResolveKnownLeadingMaSignalMarket(baseCode, out string market)) return;

            QueueLoadLeadingMaSignalInitialBars(baseCode, market, reason);
        }

        private LeadingMaSignalState GetLeadingMaSignalState(string code, string market)
        {
            string key = BuildLeadingMaSignalKey(code, market);
            if (_leadingMaSignalStates.TryGetValue(key, out LeadingMaSignalState state)) return state;

            state = new LeadingMaSignalState
            {
                Code = NormalizeStockCode(code),
                Market = string.Equals(market, "NXT", StringComparison.OrdinalIgnoreCase) ? "NXT" : "KRX"
            };
            _leadingMaSignalStates[key] = state;
            return state;
        }

        private string BuildLeadingMaSignalKey(string code, string market)
        {
            return $"{NormalizeStockCode(code)}|{(string.Equals(market, "NXT", StringComparison.OrdinalIgnoreCase) ? "NXT" : "KRX")}";
        }

        private void QueueLoadLeadingMaSignalInitialBars(string code, string market, string reason = "UNKNOWN")
        {
            string baseCode = NormalizeStockCode(code);
            if (string.IsNullOrWhiteSpace(baseCode)) return;

            string key = BuildLeadingMaSignalKey(baseCode, market);
            LeadingMaSignalState state = GetLeadingMaSignalState(baseCode, market);

            if (_leadingMaSignalLoadingCodes.Contains(key)) return;
            if (state.LastLoadAttemptAt != DateTime.MinValue &&
                (DateTime.Now - state.LastLoadAttemptAt).TotalSeconds < 5)
            {
                return;
            }

            state.LastLoadAttemptAt = DateTime.Now;
            state.LoadStatus = "LOADING";
            _leadingMaSignalLoadingCodes.Add(key);
            _ = LoadLeadingMaSignalInitialBarsAsync(baseCode, market, key, reason);
        }

        private async Task LoadLeadingMaSignalInitialBarsAsync(string code, string market, string key, string reason)
        {
            try
            {
                CandidateMinuteCache cache = await EnsureCandidateMinuteCacheLoadedAsync(code, market, reason);

                if (cache == null || !cache.IsSeedReady)
                {
                    MarkLeadingMaLoadFailed(code, market, cache?.TenMinuteCompletedCandles?.Count ?? 0, reason);
                    return;
                }

                LeadingMaSignalState state = GetLeadingMaSignalState(code, market);
                CopyMinuteCacheToLeadingState(cache, state);

                HoldingStock row = _search00List.FirstOrDefault(x => NormalizeStockCode(x.Code) == NormalizeStockCode(code));
                if (row != null)
                {
                    long price = row.CurrentPrice > 0 ? row.CurrentPrice : state.CurrentTenMinuteClose;
                    if (price <= 0) price = state.CompletedTenMinuteCloses.LastOrDefault();
                    if (price > 0)
                    {
                        ApplyLeadingMaSignalToRowOnUi(row, state, price, includeCurrentPrice: true, refreshGrid: true);

                        Log($"🚦 [MA신호등] 캐시 적용: {row.Name}({code}) / 시장={market} / MA5={row.Ma5Text} / MA20={row.Ma20Text} / MA60={row.Ma60Text} / 신호={row.MaSignalText} / 사유={reason}");
                    }
                }

                Log($"✅ [MA신호등] 초기/복구 분봉 캐시 적용 완료: {code} / 시장={market} / 10분={state.RequestCode} / 10분완성={state.CompletedTenMinuteCloses.Count} / 5분High20={state.FiveMinuteHigh20:N0} / 사유={reason}");
            }
            catch (Exception ex)
            {
                Log($"⚠️ [MA신호등 초기분봉 로드 오류] {code} / 시장={market} / {ex.Message}");
            }
            finally
            {
                _leadingMaSignalLoadingCodes.Remove(key);
            }
        }

        private void CopyMinuteCacheToLeadingState(CandidateMinuteCache cache, LeadingMaSignalState state)
        {
            if (cache == null || state == null) return;

            state.RequestCode = cache.RequestCode10m ?? "";
            state.FiveMinuteRequestCode = cache.RequestCode5m ?? "";
            state.CompletedTenMinuteCloses.Clear();
            state.CompletedTenMinuteCloses.AddRange(
                (cache.TenMinuteCompletedCloses ?? [])
                    .Where(x => x > 0)
                    .TakeLast(LeadingTenMinuteCompletedSeedCount));

            state.CurrentTenMinuteClose = cache.TenMinuteCurrentCandle?.Close ?? state.CompletedTenMinuteCloses.LastOrDefault();
            state.FiveMinuteHigh20 = cache.High20_5m;
            state.LoadedAt = cache.LoadedAt;
            state.LastLoadAttemptAt = cache.LastLoadAttemptAt;
            // 실시간 틱 처리에서 더 최신 시각이 이미 잡혀 있으면 과거 값으로 덮어쓰지 않는다.
            if (cache.LastRealtimeAt > state.LastRealtimeAt)
                state.LastRealtimeAt = cache.LastRealtimeAt;
            state.LoadStatus = cache.LoadStatus;
            state.IsSeedReady = cache.IsSeedReady;
        }

        private void MarkLeadingMaLoadFailed(string code, string market, int rawCount, string reason)
        {
            LeadingMaSignalState state = GetLeadingMaSignalState(code, market);
            state.IsSeedReady = false;
            state.LoadStatus = "LOAD_FAILED";

            HoldingStock row = _search00List.FirstOrDefault(x => NormalizeStockCode(x.Code) == NormalizeStockCode(code));
            if (row != null)
            {
                void ApplyFailed()
                {
                    row.Ma5Text = "-";
                    row.Ma20Text = "-";
                    row.Ma60Text = "-";
                    row.MaSignalText = "분봉없음";
                    row.MaSignalBrush = Brushes.Gray;
                }

                if (Dispatcher.CheckAccess()) ApplyFailed();
                else Dispatcher.Invoke(ApplyFailed);
            }

            if ((DateTime.Now - _lastLeadingMaSignalLoadLogAt).TotalSeconds >= 30)
            {
                _lastLeadingMaSignalLoadLogAt = DateTime.Now;
                Log($"⚠️ [MA신호등] 10분봉 부족: {code} / 시장={market} / 수량={rawCount} / 사유={reason}");
            }
        }

        private void ApplyLeadingMaSignalToRowOnUi(HoldingStock row, LeadingMaSignalState state, long currentPrice, bool includeCurrentPrice, bool refreshGrid = false)
        {
            if (row == null || state == null) return;

            void Apply()
            {
                ApplyLeadingMaSignalToRow(row, state, currentPrice, includeCurrentPrice);

            }

            if (Dispatcher.CheckAccess()) Apply();
            else Dispatcher.Invoke(Apply);
        }

        private void ApplyLeadingMaSignalToRow(HoldingStock row, LeadingMaSignalState state, long currentPrice, bool includeCurrentPrice)
        {
            if (row == null || state == null) return;

            List<long> closes = BuildCurrentTenMinuteCloseList(state, currentPrice, includeCurrentPrice);
            if (closes.Count < LeadingTenMinuteMaSeedCount)
            {
                row.Ma5Text = "-";
                row.Ma20Text = "-";
                row.Ma60Text = "-";
                row.MaSignalText = "대기";
                row.MaSignalBrush = Brushes.Gray;
                return;
            }

            double ma5 = AverageLast(closes, 5);
            double ma20 = AverageLast(closes, 20);
            double ma60 = AverageLast(closes, 60);

            row.Ma5Value = ma5;
            row.Ma20Value = ma20;
            row.Ma60Value = ma60;
            row.Ma5Text = FormatLeadingMa(ma5);
            row.Ma20Text = FormatLeadingMa(ma20);
            row.Ma60Text = FormatLeadingMa(ma60);

            LeadingMaSignalResult signal = ResolveLeadingMaSignal(ma5, ma20, ma60);
            row.MaSignalText = signal.Text;
            row.MaSignalBrush = signal.Brush;
        }

        private List<long> BuildCurrentTenMinuteCloseList(LeadingMaSignalState state, long currentPrice, bool includeCurrentPrice)
        {
            var completed = new List<long>();
            if (state?.CompletedTenMinuteCloses != null)
            {
                completed.AddRange(state.CompletedTenMinuteCloses.Where(x => x > 0));
            }

            if (completed.Count == 0) return completed;

            if (includeCurrentPrice && currentPrice > 0)
            {
                // 화면 신호등은 현재봉 포함 실시간 참고값이다.
                // 확정봉 배열은 건드리지 않고, 최근 59개 확정봉 + 0B 현재가로 MA60을 표시한다.
                var values = completed.TakeLast(LeadingTenMinuteMaSeedCount - 1).ToList();
                values.Add(currentPrice);
                return values;
            }

            return [.. completed.TakeLast(LeadingTenMinuteMaSeedCount)];
        }

        private double AverageLast(List<long> values, int period)
        {
            if (values == null || values.Count < period) return 0;
            return values.Skip(values.Count - period).Take(period).Average(x => (double)x);
        }

        private string FormatLeadingMa(double value)
        {
            if (value <= 0) return "-";
            return Math.Round(value, 0, MidpointRounding.AwayFromZero).ToString("N0", CultureInfo.InvariantCulture);
        }

        private LeadingMaSignalResult ResolveLeadingMaSignal(double ma5, double ma20, double ma60)
        {
            if (ma5 <= 0 || ma20 <= 0 || ma60 <= 0) return new LeadingMaSignalResult("대기", Brushes.Gray);

            if (ma5 > ma20 && ma20 > ma60)
                return new LeadingMaSignalResult("🟢🟢 강", Brushes.LimeGreen);

            if (ma5 > ma20 && ma5 > ma60)
                return new LeadingMaSignalResult("🟢 가능", Brushes.LimeGreen);

            if (ma60 > ma5 && ma60 > ma20)
                return new LeadingMaSignalResult("🔴 약", Brushes.OrangeRed);

            return new LeadingMaSignalResult("🟡 관망", Brushes.Gold);
        }

        private sealed class LeadingMaSignalState
        {
            public string Code { get; set; } = "";
            public string Market { get; set; } = "KRX";
            public string RequestCode { get; set; } = "";
            public string FiveMinuteRequestCode { get; set; } = "";
            public List<long> CompletedTenMinuteCloses { get; } = [];
            public long CurrentTenMinuteClose { get; set; }
            public long FiveMinuteHigh20 { get; set; }
            public long LastPrice { get; set; }
            public DateTime LoadedAt { get; set; }
            public DateTime LastLoadAttemptAt { get; set; }
            public DateTime LastRealtimeAt { get; set; }
            public bool IsSeedReady { get; set; }
            public string LoadStatus { get; set; } = "WAIT_MINUTE_LOAD";
        }

        private sealed class LeadingMaSignalResult
        {
            public LeadingMaSignalResult(string text, Brush brush)
            {
                Text = text;
                Brush = brush;
            }

            public string Text { get; }
            public Brush Brush { get; }
        }
    }
}
