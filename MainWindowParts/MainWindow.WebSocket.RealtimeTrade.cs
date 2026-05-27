#nullable disable

using KHStrategyLab.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private const string RealtimeTradeGroupNo = "900";

        private readonly object _realtimeTradeLock = new();
        private readonly HashSet<string> _realtimeTradeRegisteredCodes = [];
        private bool _realtimeTradeFirstSampleLogged = false;
        private DateTime _lastRealtimeTradeGridRefreshAt = DateTime.MinValue;
        private DateTime _lastRealtimeTradeSummaryLogAt = DateTime.MinValue;
        private DateTime _lastVisibleRowsRealtimeRegisterLogAt = DateTime.MinValue;
        private DateTime _lastRealtimeMarketRouterLogAt = DateTime.MinValue;
        private string _lastRealtimeMarketRouterSignature = "";
        private DateTime _lastNxtRealtimeRegisterRefreshAt = DateTime.MinValue;
        private RealtimeMarketMode _lastRealtimeMarketMode = RealtimeMarketMode.Closed;

        private enum RealtimeMarketMode
        {
            Closed,
            NxtOnlyPre,
            KrxPrimary,
            NxtOnlyAfter
        }

        private void RegisterRealtimeTrade(string code)
        {
            try
            {
                string baseCode = NormalizeStockCode(code);
                if (string.IsNullOrWhiteSpace(baseCode)) return;

                MarketStateSnapshot state = GetMarketStateNow();
                if (!state.CanRegister0B)
                {
                    LogMarketStateBlockedOnce(state, "단일종목 0B 등록");
                    return;
                }

                RealtimeMarketMode mode = state.RealtimeMode;
                if (mode == RealtimeMarketMode.Closed)
                {
                    LogRealtimeMarketRouterStatus(mode, 1, 0, 0, 0, "단일종목 신규등록 보류");
                    return;
                }

                // 단일 편입/클릭 시에도 900번 그룹을 조각 등록하지 않는다.
                // 화면의 0B 등록대상(보유+조건표시/추적)을 다시 묶어서 등록해야 기존 등록이 끊기지 않는다.
                // TOP20 전용 종목은 0B 등록과 실시간 현재가 보정을 모두 하지 않는 Thin 화면용 종목이다.
                int registered = RegisterVisibleScreenRowsForRealtimeTrade();
                if (registered > 0) return;

                if (IsRankOnlyRealtimeCode(baseCode))
                {
                    Log($"ℹ️ [실시간체결] TOP20 전용 종목 0B 단일등록 생략: {baseCode}");
                    return;
                }

                // 아직 화면 컬렉션에 들어오기 전 호출된 예외 상황만 단일 추가등록으로 보완한다.
                RegisterKrxRealtimeItems([baseCode], sendImmediately: true, source: "단일종목 예외", replaceExistingIfFirstGroup: true);

                if (mode == RealtimeMarketMode.NxtOnlyPre || mode == RealtimeMarketMode.NxtOnlyAfter)
                    QueueNxtRealtimeRegistration([baseCode], mode, "단일종목 예외", 1);
            }
            catch (Exception ex)
            {
                Log($"❌ [실시간체결 등록 오류] {code} / {ex.Message}");
            }
        }

        private bool AddRealtimeTradeCodeToRegistry(string code)
        {
            code = NormalizeRealtimeRegisterItem(code);
            if (string.IsNullOrWhiteSpace(code)) return false;

            lock (_realtimeTradeLock)
            {
                return _realtimeTradeRegisteredCodes.Add(code);
            }
        }

        // 현재 화면에 보이는 종목 중 0B가 필요한 종목만 시간대에 맞는 등록 목록에 올린다.
        // 핵심 원칙:
        // - 보유종목 + 조건표시/추적 종목은 1분마다 한 묶음으로 다시 등록한다.
        // - TOP20 전용 종목은 조회 화면으로만 사용하고, 0B 등록/장중 실시간 보정/장후 NXT 오버레이 보정을 하지 않는다.
        // - 신규 종목만 refresh=0으로 조각 추가하는 방식은 키움 서버의 900번 그룹과 내부 HashSet이 어긋날 수 있다.
        // - 장중은 KRX 6자리 등록, 장전/장후는 KRX 등록 후 NXT 가능 종목 _NX를 refresh=0으로 오버레이한다.
        //   단, 이 오버레이 대상 역시 보유/조건표시·추적 종목으로 제한한다.
        private int RegisterVisibleScreenRowsForRealtimeTrade()
        {
            try
            {
                MarketStateSnapshot state = GetMarketStateNow();
                if (!state.CanRegister0B)
                {
                    LogMarketStateBlockedOnce(state, "화면종목 0B 등록");
                    return 0;
                }

                RealtimeCodeBuckets buckets = CollectVisibleScreenRealtimeBuckets();
                List<string> registerCodes = buckets.RealtimeRegisterCodes;
                int visibleCount = buckets.AllCodes.Count;

                // 20:00 이후~다음날 07:00 전에는 신규 0B 등록은 멈추되,
                // 보유 NXT 종목의 마지막 평가가격은 파일 없이 조회해 잔고 화면에 바로 반영한다.
                AccountEnsureNxtCloseBalancePrices("화면종목 0B 등록 전");

                RealtimeMarketMode mode = state.RealtimeMode;

                if (registerCodes.Count == 0)
                {
                    if (mode == RealtimeMarketMode.Closed)
                        _ = RefreshSearch00KrxClosePricesAsync("장외 CLOSED 화면종목 등록 보류");

                    LogRealtimeMarketRouterStatus(mode, visibleCount, 0, 0, 0, $"0B 등록대상 없음 / {buckets.SummaryText}");
                    return 0;
                }

                if (mode == RealtimeMarketMode.Closed)
                {
                    _ = RefreshSearch00KrxClosePricesAsync("장외 CLOSED 화면종목 등록 보류");
                    LogRealtimeMarketRouterStatus(mode, visibleCount, 0, 0, 0, $"화면종목 신규등록 보류 / 조건00 KRX종가 보정 / {buckets.SummaryText}");
                    return 0;
                }

                int krxRegistered = ReplaceVisibleKrxRealtimeGroup(registerCodes, mode, $"화면종목 / {buckets.SummaryText}");

                if (mode == RealtimeMarketMode.NxtOnlyPre || mode == RealtimeMarketMode.NxtOnlyAfter)
                {
                    QueueNxtRealtimeRegistration(registerCodes, mode, $"화면종목 / {buckets.SummaryText}", krxRegistered);
                    return krxRegistered;
                }

                if ((DateTime.Now - _lastVisibleRowsRealtimeRegisterLogAt).TotalSeconds >= 60)
                {
                    _lastVisibleRowsRealtimeRegisterLogAt = DateTime.Now;
                    Log($" [실시간체결] 장중 KRX 0B 화면전체 재등록: {buckets.SummaryText} / 화면합계 {visibleCount}개 / 0B대상 {registerCodes.Count}개 / 등록 {krxRegistered}개 / 전체등록 {GetRealtimeRegisteredCount()}개");
                }

                return krxRegistered;
            }
            catch (Exception ex)
            {
                Log($"❌ [실시간체결 화면종목 등록대상 오류] {ex.Message}");
                return 0;
            }
        }

        private int ReplaceVisibleKrxRealtimeGroup(IEnumerable<string> codes, RealtimeMarketMode mode, string source)
        {
            if (codes == null) return 0;

            List<string> krxCodes = [.. codes
                .Select(NormalizeRealtimeRegisterItem)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Where(x => !x.EndsWith("_NX", StringComparison.OrdinalIgnoreCase))
                .Distinct()];

            if (krxCodes.Count == 0) return 0;

            lock (_realtimeTradeLock)
            {
                _realtimeTradeRegisteredCodes.Clear();
                foreach (string item in krxCodes)
                    _realtimeTradeRegisteredCodes.Add(item);

                _lastRealtimeMarketMode = mode;
            }

            if (_ws == null || !_isWsAuthenticated)
            {
                Log($"⏳ [실시간체결] WebSocket LOGIN 전이라 화면전체 KRX 등록 대기: {source} {krxCodes.Count}개");
                return krxCodes.Count;
            }

            SendRealtimeTradeRegisterChunks(krxCodes, replaceExisting: true);
            return krxCodes.Count;
        }

        private int RegisterKrxRealtimeItems(IEnumerable<string> codes, bool sendImmediately, string source, bool replaceExistingIfFirstGroup = false)
        {
            if (codes == null) return 0;

            var newItems = new List<string>();

            foreach (string code in codes)
            {
                string item = NormalizeRealtimeRegisterItem(code);
                if (string.IsNullOrWhiteSpace(item)) continue;
                if (item.EndsWith("_NX", StringComparison.OrdinalIgnoreCase)) continue;

                if (AddRealtimeTradeCodeToRegistry(item))
                    newItems.Add(item);
            }

            if (newItems.Count == 0) return 0;

            if (_ws == null || !_isWsAuthenticated)
            {
                Log($"⏳ [실시간체결] WebSocket LOGIN 전이라 KRX 등록 대기: {source} {newItems.Count}개");
                return newItems.Count;
            }

            if (sendImmediately)
            {
                bool replaceExistingGroup = replaceExistingIfFirstGroup && ShouldReplaceRealtimeGroupForAddedItems(newItems.Count);
                SendRealtimeTradeRegisterChunks(newItems, replaceExisting: replaceExistingGroup);
                Log($" [실시간체결] KRX 0B 등록: {string.Join(",", newItems.Take(5))}{(newItems.Count > 5 ? "..." : "")} / {(replaceExistingGroup ? "그룹교체" : "추가등록")} / {source}");
            }

            return newItems.Count;
        }

        private void EnsureRealtimeMarketModeRegistry(RealtimeMarketMode mode, bool clearAlways)
        {
            lock (_realtimeTradeLock)
            {
                if (clearAlways || _lastRealtimeMarketMode != mode)
                {
                    _realtimeTradeRegisteredCodes.Clear();
                    _lastRealtimeMarketMode = mode;
                }
            }
        }

        private int GetRealtimeRegisteredCount()
        {
            lock (_realtimeTradeLock)
            {
                return _realtimeTradeRegisteredCodes.Count;
            }
        }

        private List<string> CollectVisibleScreenRealtimeCodes()
        {
            return CollectVisibleScreenRealtimeBuckets().RealtimeRegisterCodes;
        }

        private void SetManualChartRealtimeCode(string code)
        {
            _manualChartRealtimeCode = NormalizeStockCode(code);
        }

        private RealtimeCodeBuckets CollectVisibleScreenRealtimeBuckets()
        {
            var buckets = new RealtimeCodeBuckets();

            try
            {
                Dispatcher.Invoke(() =>
                {
                    buckets.ConditionCodes.AddRange(_search00List
                        .Select(x => NormalizeStockCode(x.Code))
                        .Where(x => !string.IsNullOrWhiteSpace(x)));

                    buckets.RankCodes.AddRange(_rankList
                        .Select(x => NormalizeStockCode(x.Code))
                        .Where(x => !string.IsNullOrWhiteSpace(x)));

                    buckets.BalanceCodes.AddRange(_balance
                        .Select(x => NormalizeStockCode(x.Code))
                        .Where(x => !string.IsNullOrWhiteSpace(x)));

                    if (!string.IsNullOrWhiteSpace(_manualChartRealtimeCode))
                        buckets.ManualChartCodes.Add(_manualChartRealtimeCode);
                });
            }
            catch
            {
                // UI 종료/초기화 중이면 조용히 무시한다.
            }

            buckets.Normalize();
            return buckets;
        }

        private bool IsRankOnlyRealtimeCode(string code)
        {
            string baseCode = NormalizeStockCode(code);
            if (string.IsNullOrWhiteSpace(baseCode)) return false;

            bool inRank = false;
            bool inRealtimeTarget = false;

            try
            {
                Action checkAction = () =>
                {
                    inRank = _rankList.Any(x => NormalizeStockCode(x.Code) == baseCode);
                    inRealtimeTarget =
                        _search00List.Any(x => NormalizeStockCode(x.Code) == baseCode) ||
                        _balance.Any(x => NormalizeStockCode(x.Code) == baseCode) ||
                        NormalizeStockCode(_manualChartRealtimeCode) == baseCode;
                };

                if (Dispatcher.CheckAccess())
                    checkAction();
                else
                    Dispatcher.Invoke(checkAction);
            }
            catch
            {
                return false;
            }

            return inRank && !inRealtimeTarget;
        }

        private void ReRegisterRealtimeTrades()
        {
            try
            {
                if (_ws == null || !_isWsAuthenticated) return;
                MarketStateSnapshot state = GetMarketStateNow();
                if (!state.CanRegister0B)
                {
                    LogMarketStateBlockedOnce(state, "LOGIN 이후 0B 재등록");
                    return;
                }

                RealtimeCodeBuckets buckets = CollectVisibleScreenRealtimeBuckets();
                List<string> registerCodes = buckets.RealtimeRegisterCodes;
                int visibleCount = buckets.AllCodes.Count;

                // 로그인/재연결 직후에도 장외 시간이면 보유 NXT 종목의 마지막 평가가격을 조회해 보정한다.
                AccountEnsureNxtCloseBalancePrices("WS LOGIN 이후 0B 재등록 전");

                RealtimeMarketMode mode = state.RealtimeMode;

                if (registerCodes.Count == 0)
                {
                    if (mode == RealtimeMarketMode.Closed)
                        _ = RefreshSearch00KrxClosePricesAsync("LOGIN 이후 장외 CLOSED");

                    LogRealtimeMarketRouterStatus(mode, visibleCount, 0, 0, 0, $"LOGIN 이후 0B 등록대상 없음 / {buckets.SummaryText}");
                    return;
                }

                if (mode == RealtimeMarketMode.Closed)
                {
                    _ = RefreshSearch00KrxClosePricesAsync("LOGIN 이후 장외 CLOSED");
                    LogRealtimeMarketRouterStatus(mode, visibleCount, 0, 0, 0, $"LOGIN 이후 0B 재등록 보류 / 조건00 KRX종가 보정 / {buckets.SummaryText}");
                    return;
                }

                int krxRegistered = ReplaceVisibleKrxRealtimeGroup(registerCodes, mode, $"LOGIN 이후 화면종목 / {buckets.SummaryText}");
                string modeText = mode == RealtimeMarketMode.KrxPrimary ? "장중" : "NXT시간 기본";
                Log($" [실시간체결] LOGIN 이후 {modeText} KRX 0B 화면전체 재등록 완료: {krxRegistered}개 / 그룹교체 / 화면 {visibleCount}개 / 0B대상 {registerCodes.Count}개");

                if (mode == RealtimeMarketMode.NxtOnlyPre || mode == RealtimeMarketMode.NxtOnlyAfter)
                    QueueNxtRealtimeRegistration(registerCodes, mode, $"LOGIN 이후 화면종목 / {buckets.SummaryText}", krxRegistered);
            }
            catch (Exception ex)
            {
                Log($"❌ [실시간체결 재등록 오류] {ex.Message}");
            }
        }

        private bool ShouldReplaceRealtimeGroupForAddedItems(int addedCount)
        {
            if (addedCount <= 0) return false;

            lock (_realtimeTradeLock)
            {
                return _realtimeTradeRegisteredCodes.Count <= addedCount;
            }
        }

        private void SendRealtimeTradeRegisterChunks(List<string> codes)
        {
            SendRealtimeTradeRegisterChunks(codes, replaceExisting: true);
        }

        private void SendRealtimeTradeRegisterChunks(List<string> codes, bool replaceExisting)
        {
            if (codes == null || codes.Count == 0) return;

            bool firstChunk = true;
            foreach (List<string> chunk in SplitRealtimeCodes(codes, 90))
            {
                string refresh = replaceExisting && firstChunk ? "1" : "0";
                SendRealtimeTradeRegisterPacket(chunk, refresh);
                firstChunk = false;
            }
        }

        private List<List<string>> SplitRealtimeCodes(List<string> codes, int chunkSize)
        {
            var result = new List<List<string>>();
            if (codes == null || codes.Count == 0) return result;
            if (chunkSize <= 0) chunkSize = 90;

            for (int i = 0; i < codes.Count; i += chunkSize)
                result.Add([.. codes.Skip(i).Take(chunkSize)]);

            return result;
        }

        private void SendRealtimeTradeRegisterPacket(IEnumerable<string> codes, string refresh)
        {
            string[] items = [.. codes
                .Select(NormalizeRealtimeRegisterItem)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()];

            if (items.Length == 0) return;

            var packet = new
            {
                trnm = "REG",
                grp_no = RealtimeTradeGroupNo,
                refresh = string.IsNullOrWhiteSpace(refresh) ? "0" : refresh,
                data = new[]
                {
                    new
                    {
                        item = items,
                        type = new[] { "0B" }
                    }
                }
            };

            string json = JsonConvert.SerializeObject(packet);
            _ws.Send(json);
        }

        private string ResolveRefreshFlagForRealtimeRegister()
        {
            // 호환용.
            // NXT 오버레이 추가등록은 기존 그룹을 지우면 안 되므로 항상 refresh=0을 사용한다.
            return "0";
        }

        private void QueueNxtRealtimeRegistration(IEnumerable<string> codes, RealtimeMarketMode mode, string source, int krxAddedCount)
        {
            try
            {
                if (mode != RealtimeMarketMode.NxtOnlyPre && mode != RealtimeMarketMode.NxtOnlyAfter) return;
                if (codes == null) return;

                List<string> baseCodes = [.. codes
                    .Select(NormalizeStockCode)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct()];

                if (baseCodes.Count == 0) return;

                bool refreshExistingNxtItems = ShouldRefreshExistingNxtRealtimeItems();

                _ = Task.Run(async () =>
                {
                    int eligible = 0;
                    int added = 0;
                    int resent = 0;

                    foreach (string baseCode in baseCodes)
                    {
                        NxtRealtimeRegistrationResult result = await RegisterNxtRealtimeIfEligibleAsync(baseCode, mode, refreshExistingNxtItems);
                        if (!result.Eligible) continue;

                        eligible++;
                        if (result.Added) added++;
                        if (result.ResentExisting) resent++;
                    }

                    LogRealtimeMarketRouterStatus(mode, baseCodes.Count, eligible, krxAddedCount, added, source);

                    if (added > 0 || resent > 0)
                    {
                        string modeText = mode == RealtimeMarketMode.NxtOnlyPre ? "장전" : "장후";
                        Log($" [실시간체결] {modeText} NXT 0B 오버레이 등록/재전송: 신규 {added}개 / 재전송 {resent}개 / refresh=0");
                    }
                });
            }
            catch (Exception ex)
            {
                Log($"⚠️ [실시간체결 NXT 등록예약 오류] {ex.Message}");
            }
        }

        private bool ShouldRefreshExistingNxtRealtimeItems()
        {
            DateTime now = DateTime.Now;

            lock (_realtimeTradeLock)
            {
                if ((now - _lastNxtRealtimeRegisterRefreshAt).TotalSeconds < 60) return false;

                _lastNxtRealtimeRegisterRefreshAt = now;
                return true;
            }
        }

        private async Task<NxtRealtimeRegistrationResult> RegisterNxtRealtimeIfEligibleAsync(string code, RealtimeMarketMode mode, bool refreshExisting)
        {
            var result = new NxtRealtimeRegistrationResult();

            try
            {
                string baseCode = NormalizeStockCode(code);
                if (string.IsNullOrWhiteSpace(baseCode)) return result;
                if (mode != RealtimeMarketMode.NxtOnlyPre && mode != RealtimeMarketMode.NxtOnlyAfter) return result;

                bool isNxtEnabled = await IsNxtEnabledAsync(baseCode);
                if (!isNxtEnabled) return result;

                result.Eligible = true;

                string nxtItem = NormalizeRealtimeRegisterItem(baseCode + "_NX");
                if (string.IsNullOrWhiteSpace(nxtItem)) return result;

                bool added = AddRealtimeTradeCodeToRegistry(nxtItem);
                result.Added = added;

                bool shouldSend = added || refreshExisting;
                if (!shouldSend) return result;

                if (_ws != null && _isWsAuthenticated)
                {
                    // _NX는 기존 KRX 기본 그룹 위에 얹는 오버레이이므로 절대 refresh=1로 보내지 않는다.
                    SendRealtimeTradeRegisterPacket([nxtItem], "0");
                    if (!added) result.ResentExisting = true;
                }
                else
                {
                    Log($"⏳ [실시간체결] WebSocket LOGIN 전이라 NXT 등록 대기: {nxtItem}");
                }

                return result;
            }
            catch (Exception ex)
            {
                Log($"⚠️ [실시간체결 NXT 등록 오류] {code} / {ex.Message}");
                return result;
            }
        }

        private RealtimeMarketMode GetRealtimeMarketModeNow()
        {
            return GetMarketStateNow().RealtimeMode;
        }

        private void LogRealtimeMarketRouterStatus(RealtimeMarketMode mode, int visibleCount, int eligibleCount, int krxAddedCount, int nxtAddedCount, string source)
        {
            try
            {
                DateTime now = DateTime.Now;
                int registeredTotal = GetRealtimeRegisteredCount();
                string signature = $"{mode}|{visibleCount}|{eligibleCount}|{krxAddedCount}|{nxtAddedCount}|{registeredTotal}";
                bool signatureChanged = !string.Equals(_lastRealtimeMarketRouterSignature, signature, StringComparison.Ordinal);
                double elapsedSec = (now - _lastRealtimeMarketRouterLogAt).TotalSeconds;
                if (!signatureChanged && elapsedSec < 30)
                    return;
                if (signatureChanged && elapsedSec < 2)
                    return;
                _lastRealtimeMarketRouterLogAt = now;
                _lastRealtimeMarketRouterSignature = signature;

                string modeText;
                switch (mode)
                {
                    case RealtimeMarketMode.NxtOnlyPre:
                        modeText = "장전 KRX_BASE+NXT_OVERLAY";
                        break;
                    case RealtimeMarketMode.KrxPrimary:
                        modeText = "장중 KRX_PRIMARY";
                        break;
                    case RealtimeMarketMode.NxtOnlyAfter:
                        modeText = "장후 KRX_BASE+NXT_OVERLAY";
                        break;
                    default:
                        modeText = "장외 CLOSED";
                        break;
                }

                Log($" [실시간체결 등록모드] {modeText} / {source} / 화면 {visibleCount}개 / KRX등록 {krxAddedCount}개 / NXT가능 {eligibleCount}개 / NXT신규 {nxtAddedCount}개 / 전체등록 {GetRealtimeRegisteredCount()}개");
            }
            catch
            {
                // 로그 보조 함수라서 예외는 무시한다.
            }
        }

        private string NormalizeRealtimeRegisterItem(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";

            string value = raw.Trim().ToUpperInvariant();
            if (value.Contains(":"))
                value = value.Substring(value.LastIndexOf(':') + 1);

            bool isNxt = value.EndsWith("_NX", StringComparison.OrdinalIgnoreCase);
            bool isSor = value.EndsWith("_AL", StringComparison.OrdinalIgnoreCase);

            string body = value;
            if (isNxt || isSor)
                body = value.Substring(0, value.LastIndexOf('_'));

            if (body.StartsWith("A", StringComparison.OrdinalIgnoreCase))
                body = body.Substring(1);

            string digits = new([.. body.Where(char.IsDigit)]);
            if (digits.Length >= 6)
                digits = digits.Substring(digits.Length - 6);
            else if (digits.Length > 0)
                digits = digits.PadLeft(6, '0');

            if (string.IsNullOrWhiteSpace(digits)) return "";

            if (isNxt) return digits + "_NX";
            if (isSor) return digits + "_AL";
            return digits;
        }

        private void HandleRealtimeTradeMessage(JObject res)
        {
            try
            {
                if (res == null) return;

                if (!_realtimeTradeFirstSampleLogged)
                {
                    _realtimeTradeFirstSampleLogged = true;
                    Log(" [실시간체결] 첫 REAL 수신 확인 / 0B 화면갱신 시작");
                }

                List<RealtimeTradeSnapshot> snapshots = ParseRealtimeTradeSnapshots(res);
                if (snapshots.Count == 0) return;

                Dispatcher.Invoke(() =>
                {
                    bool anyApplied = false;

                    foreach (RealtimeTradeSnapshot snapshot in snapshots)
                        anyApplied |= ApplyRealtimeTradeSnapshot(snapshot);

                    if (!anyApplied) return;

                    DateTime now = DateTime.Now;

                    if ((now - _lastRealtimeTradeSummaryLogAt).TotalSeconds >= 10)
                    {
                        _lastRealtimeTradeSummaryLogAt = now;
                        Log($" [실시간체결] 화면 현재가 갱신 중: {snapshots.Count}건 수신");
                    }
                });
            }
            catch (Exception ex)
            {
                Log($"❌ [실시간체결 처리 오류] {ex.Message}");
            }
        }

        private List<RealtimeTradeSnapshot> ParseRealtimeTradeSnapshots(JObject res)
        {
            var result = new List<RealtimeTradeSnapshot>();
            JArray data = res["data"] as JArray;

            if (data == null || data.Count == 0)
            {
                RealtimeTradeSnapshot single = ParseRealtimeTradeSnapshot(res, res["trnm"]?.ToString() ?? "");
                if (single != null) result.Add(single);
                return result;
            }

            foreach (JToken item in data)
            {
                RealtimeTradeSnapshot snapshot = ParseRealtimeTradeSnapshot(item, res["trnm"]?.ToString() ?? "");
                if (snapshot != null) result.Add(snapshot);
            }

            return result;
        }

        private RealtimeTradeSnapshot ParseRealtimeTradeSnapshot(JToken item, string parentTrnm)
        {
            if (item == null) return null;

            JObject values = item["values"] as JObject;
            string type = ReadRealtimeValue(item, values, "type", "real_type", "rt_type");
            if (!string.IsNullOrWhiteSpace(type) && type != "0B" && parentTrnm != "0B")
                return null;

            string rawCode = ReadRealtimeValue(item, values, "item", "stk_cd", "stkCd", "code", "jmcode", "jm_code", "stk_code", "종목코드", "9001");
            bool isNxtSnapshot = !string.IsNullOrWhiteSpace(rawCode) && rawCode.Trim().ToUpperInvariant().Contains("_NX");

            string code = NormalizeStockCode(rawCode);
            if (string.IsNullOrWhiteSpace(code)) return null;

            string priceText = ReadRealtimeValue(item, values, "cur_prc", "curPrc", "now_prc", "price", "현재가", "10");
            long currentPrice = ParseLongSafe(priceText);
            if (currentPrice <= 0) return null;

            string volumeText = ReadRealtimeValue(item, values, "acc_trde_qty", "trde_qty", "volume", "누적거래량", "거래량", "13", "15");
            string tradeQuantityText = ReadRealtimeValue(item, values, "cntr_qty", "trade_qty", "체결량", "15");
            string tradingValueText = ReadRealtimeValue(item, values, "acc_trde_prica", "acc_trde_amt", "trde_prica", "trde_amt", "trading_value", "누적거래대금", "거래대금", "14");
            string changeRateText = ReadRealtimeValue(item, values, "flu_rt", "fluRt", "chg_rt", "change_rate", "등락률", "12");
            string tradeTimeText = ReadRealtimeValue(item, values, "cntr_tm", "trade_time", "체결시간", "20");
            changeRateText = NormalizeRealtimeRateText(changeRateText);

            long volume = ParseLongSafe(volumeText);
            long tradingValueRaw = ParseLongSafe(tradingValueText);

            return new RealtimeTradeSnapshot
            {
                Code = code,
                IsNxtSnapshot = isNxtSnapshot,
                CurrentPrice = currentPrice,
                Volume = volume,
                TradeQuantity = ParseLongSafe(tradeQuantityText),
                TradingValue = NormalizeRealtimeTradingValue(tradingValueRaw, currentPrice, volume),
                TradeTime = ParseRealtimeTradeTime(tradeTimeText),
                ChangeRateText = changeRateText
            };
        }

        private long NormalizeRealtimeTradingValue(long rawTradingValue, long currentPrice, long volume)
        {
            if (rawTradingValue <= 0)
                return 0;

            if (currentPrice <= 0 || volume <= 0)
                return rawTradingValue;

            decimal expectedWon = (decimal)currentPrice * volume;
            if (expectedWon <= 0)
                return rawTradingValue;

            decimal rawWon = rawTradingValue;
            decimal rawMillionWon = rawWon * 1_000_000m;

            // 키움 0B 14번 누적거래대금은 장중 실시간에서 백만원 단위로 내려오는 경우가 많다.
            // 예: 현재가 27,200원 × 누적거래량 6,625,692주 ≒ 1,802억원
            //     0B 14번 값 183,266 → 183,266백만원 ≒ 1,832억원
            // raw 값이 현재가×거래량 대비 너무 작고, 백만원 보정값이 더 자연스러우면 원 단위로 보정한다.
            decimal rawRatio = rawWon / expectedWon;
            decimal millionRatio = rawMillionWon / expectedWon;

            if (rawRatio >= 0.1m && rawRatio <= 10m)
                return SafeDecimalToLong(rawWon);

            if (millionRatio >= 0.1m && millionRatio <= 10m)
                return SafeDecimalToLong(rawMillionWon);

            // 그래도 판단이 애매하면, 거래대금 표시가 지나치게 작아지는 것을 막기 위해
            // 현재가×거래량보다 100분의 1 이하인 값은 백만원 단위로 본다.
            if (rawWon < expectedWon / 100m)
                return SafeDecimalToLong(rawMillionWon);

            return SafeDecimalToLong(rawWon);
        }

        private long SafeDecimalToLong(decimal value)
        {
            if (value <= 0)
                return 0;

            if (value >= long.MaxValue)
                return long.MaxValue;

            return (long)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        private DateTime ParseRealtimeTradeTime(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return DateTime.Now;

            string digits = new([.. value.Trim().Where(char.IsDigit)]);
            if (digits.Length >= 14)
            {
                digits = digits.Substring(0, 14);
                if (DateTime.TryParseExact(digits, "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime parsed))
                    return parsed;
            }

            if (digits.Length >= 6)
            {
                digits = digits.Substring(0, 6);
                DateTime today = DateTime.Now.Date;
                if (int.TryParse(digits.Substring(0, 2), out int hour) &&
                    int.TryParse(digits.Substring(2, 2), out int minute) &&
                    int.TryParse(digits.Substring(4, 2), out int second) &&
                    hour >= 0 && hour <= 23 &&
                    minute >= 0 && minute <= 59 &&
                    second >= 0 && second <= 59)
                {
                    return today.AddHours(hour).AddMinutes(minute).AddSeconds(second);
                }
            }

            return DateTime.Now;
        }

        private string ReadRealtimeValue(JToken item, JObject values, params string[] keys)
        {
            foreach (string key in keys)
            {
                string value = ReadRealtimeValueFromToken(item, key);
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();

                if (values != null)
                {
                    value = ReadRealtimeValueFromToken(values, key);
                    if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                }
            }

            return "";
        }

        private string ReadRealtimeValueFromToken(JToken token, string key)
        {
            if (token == null || string.IsNullOrWhiteSpace(key)) return "";

            if (token is JObject obj)
            {
                JToken exact = obj[key];
                if (exact != null && exact.Type != JTokenType.Null) return exact.ToString();

                JProperty found = obj.Properties()
                    .FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));

                if (found != null && found.Value.Type != JTokenType.Null)
                    return found.Value.ToString();
            }

            return "";
        }

        private bool ApplyRealtimeTradeSnapshot(RealtimeTradeSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Code)) return false;

            string code = NormalizeStockCode(snapshot.Code);

            // 20:00 이후에는 신규 실시간 반영은 멈추고, 마지막 NXT 평가가격만 다음날 07:00 전까지 보유잔고 화면에 유지한다.
            MarketStateSnapshot state = GetMarketStateNow();
            RealtimeMarketMode mode = state.RealtimeMode;
            if (!state.CanRegister0B || mode == RealtimeMarketMode.Closed)
            {
                AccountEnsureNxtCloseBalancePrices("장외 CLOSED 0B 수신 중");
                return false;
            }

            // 키움 응답이 _NX 접미사를 그대로 주면 NXT로 확정한다.
            // 일부 응답이 6자리 코드만 주는 경우에는, 장전/장후 오버레이 시간대에 _NX 등록된 종목이면
            // NXT 체결값으로 보고 같은 화면 행과 보유잔고를 덮어쓴다.
            if ((mode == RealtimeMarketMode.NxtOnlyPre || mode == RealtimeMarketMode.NxtOnlyAfter) &&
                !snapshot.IsNxtSnapshot &&
                IsNxtRealtimeItemRegistered(code))
            {
                snapshot.IsNxtSnapshot = true;
            }

            bool displayApplied = ApplyRealtimeTradeSnapshotToDisplayRows(snapshot, code);

            // 조건00/추적 후보 행은 0B 현재가를 이용해 10분봉 5/20/60선 신호등을 갱신한다.
            // TOP20은 ApplyRealtimeTradeSnapshotToDisplayRows()에서 제외되므로 여기에도 연결되지 않는다.
            if (displayApplied)
                UpdateLeadingMaSignalFromRealtimeTick(code, snapshot.CurrentPrice, snapshot.IsNxtSnapshot);

            bool balanceApplied = ApplyRealtimeTradeSnapshotToBalanceRows(snapshot, code);
            ApplyRealtimeTradeSnapshotToWatchCandidate(snapshot, code);
            TryApplyRealtimeTradeSnapshotToMinuteChart(snapshot, code);

            return displayApplied || balanceApplied;
        }

        private bool ApplyRealtimeTradeSnapshotToDisplayRows(RealtimeTradeSnapshot snapshot, string code)
        {
            bool applied = false;

            HoldingStock searchStock = _search00List.FirstOrDefault(x => NormalizeStockCode(x.Code) == code);
            if (searchStock != null)
            {
                // 추적리스트 거래대금은 장중 KRX 0B 누적거래대금으로 실시간 갱신한다.
                // 단, NXT/SOR 오버레이 값이 KRX 일거래대금을 덮으면 클릭 전후 금액 편차가 생기므로
                // NXT 스냅샷일 때는 거래대금 칸을 유지한다.
                ApplyRealtimeTradeSnapshotToRow(searchStock, snapshot, keepVolumeText: false, keepProfitRateText: false, keepTradingValueText: snapshot.IsNxtSnapshot);
                applied = true;
            }

            // Thin 기준:
            // TOP20(_rankList)은 ka00198 조회 결과 그대로 표시한다.
            // 같은 종목이 보유/조건 때문에 0B를 수신하더라도 TOP20 행에는 장중 KRX 실시간값이나 장후 NXT 오버레이값을 덮지 않는다.
            return applied;
        }

        private bool ApplyRealtimeTradeSnapshotToBalanceRows(RealtimeTradeSnapshot snapshot, string code)
        {
            HoldingStock balanceStock = _balance.FirstOrDefault(x => NormalizeStockCode(x.Code) == code);
            if (balanceStock == null) return false;

            // 보유종목은 kt00005 초기값을 기준으로 두되, 0B 현재가가 들어오면
            // 보유수량 × 현재가로 평가금액/손익률/총잔고를 실시간 재계산한다.
            ApplyRealtimeTradeSnapshotToRow(balanceStock, snapshot, keepVolumeText: true, keepProfitRateText: true, keepTradingValueText: true);
            AccountApplyRealtimeBalancePrice(balanceStock, snapshot.CurrentPrice, snapshot.IsNxtSnapshot);
            return true;
        }

        private void ApplyRealtimeTradeSnapshotToWatchCandidate(RealtimeTradeSnapshot snapshot, string code)
        {
            if (_watchCandidates.TryGetValue(code, out WatchCandidate candidate))
            {
                candidate.LastPrice = snapshot.CurrentPrice;
                candidate.LastSeen = DateTime.Now;
            }
        }

        private bool IsNxtRealtimeItemRegistered(string code)
        {
            string baseCode = NormalizeStockCode(code);
            if (string.IsNullOrWhiteSpace(baseCode)) return false;

            string nxtItem = NormalizeRealtimeRegisterItem(baseCode + "_NX");

            lock (_realtimeTradeLock)
            {
                return _realtimeTradeRegisteredCodes.Contains(nxtItem);
            }
        }

        private void ApplyRealtimeTradeSnapshotToRow(StockGridRow row, RealtimeTradeSnapshot snapshot, bool keepVolumeText, bool keepProfitRateText, bool keepTradingValueText = false)
        {
            if (row == null || snapshot == null) return;

            if (snapshot.CurrentPrice > 0)
                row.CurrentPrice = snapshot.CurrentPrice;

            if (!keepVolumeText && snapshot.Volume > 0)
                row.VolumeText = snapshot.Volume.ToString("N0");

            if (!keepTradingValueText && snapshot.TradingValue > 0)
                row.TradingValueText = FormatKoreanMoney(snapshot.TradingValue);

            if (!string.IsNullOrWhiteSpace(snapshot.ChangeRateText))
            {
                row.ChangeRateText = snapshot.ChangeRateText;
                row.PriceColor = ResolveRateBrush(snapshot.ChangeRateText);

                if (!keepProfitRateText)
                {
                    row.ProfitRateText = snapshot.ChangeRateText;
                    row.ProfitColor = ResolveRateBrush(snapshot.ChangeRateText);
                }
            }
        }

        private string NormalizeRealtimeRateText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";

            value = value.Trim().Replace(",", "");
            if (value.Contains("%")) return value;
            return $"{value}%";
        }

        // 이전 패치 파일에서 호출하던 이름을 남겨 둔다.
        // 현재 GridRank는 ka00198 TOP20 조회 결과만 표시하므로 여기서는 아무 작업도 하지 않는다.
        private void RefreshRealtimeRankWaitingGridFromStockInfo()
        {
        }

        private class RealtimeCodeBuckets
        {
            public List<string> BalanceCodes { get; } = [];
            public List<string> ConditionCodes { get; } = [];
            public List<string> RankCodes { get; } = [];
            public List<string> ManualChartCodes { get; } = [];

            public List<string> AllCodes
            {
                get
                {
                    return [.. BalanceCodes
                        .Concat(ConditionCodes)
                        .Concat(ManualChartCodes)
                        .Concat(RankCodes)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct()];
                }
            }

            // 실제 0B 등록용 목록.
            // TOP20은 순위 조회 화면으로만 사용한다.
            // 같은 종목이 보유/조건에도 있으면 그 역할 때문에 0B 등록대상에 남지만, TOP20 행 자체는 실시간값으로 보정하지 않는다.
            public List<string> RealtimeRegisterCodes
            {
                get
                {
                    return [.. BalanceCodes
                        .Concat(ConditionCodes)
                        .Concat(ManualChartCodes)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct()];
                }
            }

            public string SummaryText => $"보유 {BalanceCodes.Count}개 / 조건표시·추적 {ConditionCodes.Count}개 / 수동차트 {ManualChartCodes.Count}개 / TOP20 {RankCodes.Count}개(0B·실시간보정 제외)";

            public void Normalize()
            {
                ReplaceWithDistinct(BalanceCodes);
                ReplaceWithDistinct(ConditionCodes);
                ReplaceWithDistinct(ManualChartCodes);
                ReplaceWithDistinct(RankCodes);
            }

            private static void ReplaceWithDistinct(List<string> list)
            {
                if (list == null) return;

                List<string> distinct = [.. list
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct()];

                list.Clear();
                list.AddRange(distinct);
            }
        }

        private class NxtRealtimeRegistrationResult
        {
            public bool Eligible { get; set; }
            public bool Added { get; set; }
            public bool ResentExisting { get; set; }
        }

        private class RealtimeTradeSnapshot
        {
            public string Code { get; set; } = "";
            public bool IsNxtSnapshot { get; set; }
            public long CurrentPrice { get; set; }
            public long Volume { get; set; }
            public long TradeQuantity { get; set; }
            public long TradingValue { get; set; }
            public DateTime TradeTime { get; set; }
            public string ChangeRateText { get; set; } = "";
        }
    }
}
