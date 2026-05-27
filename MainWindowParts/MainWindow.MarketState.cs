#nullable disable

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private const string MarketSessionGroupNo = "901";
        private static readonly TimeSpan MarketSessionFreshness = TimeSpan.FromMinutes(30);

        private enum MarketSessionKind
        {
            Closed,
            Holiday,
            NxtPre,
            KrxPrimary,
            NxtAfter
        }

        private sealed class MarketStateSnapshot
        {
            public DateTime Now { get; set; }
            public MarketSessionKind Session { get; set; }
            public RealtimeMarketMode RealtimeMode { get; set; }
            public bool IsClosedDate { get; set; }
            public bool CanRegister0B { get; set; }
            public bool ShouldUseFinalIndexSnapshot { get; set; }
            public DateTime RealizedProfitQueryDate { get; set; }
            public string Reason { get; set; } = "";
            public string SessionCode { get; set; } = "";
            public DateTime SessionReceivedAt { get; set; }
        }

        private MarketStateSnapshot GetMarketStateNow()
        {
            return GetMarketState(DateTime.Now);
        }

        private MarketStateSnapshot GetMarketState(DateTime now)
        {
            DateTime date = now.Date;
            bool closedDate = IsMarketClosedDate(date);
            RealtimeMarketMode clockMode = ResolveRealtimeMarketModeByClock(now);
            bool shouldUseFinalIndexSnapshot = closedDate || now.TimeOfDay < new TimeSpan(8, 0, 0);
            DateTime queryDate = shouldUseFinalIndexSnapshot
                ? AccountGetPreviousBusinessDate(date.AddDays(-1))
                : date;

            if (closedDate)
            {
                string reason = GetMarketClosedReason(date);
                return new MarketStateSnapshot
                {
                    Now = now,
                    Session = MarketSessionKind.Holiday,
                    RealtimeMode = RealtimeMarketMode.Closed,
                    IsClosedDate = true,
                    CanRegister0B = false,
                    ShouldUseFinalIndexSnapshot = true,
                    RealizedProfitQueryDate = queryDate,
                    Reason = string.IsNullOrWhiteSpace(reason) ? "휴장일" : reason
                };
            }

            MarketSessionKind session = ToMarketSessionKind(clockMode);
            bool canRegister0B = clockMode != RealtimeMarketMode.Closed;
            string reasonText = ResolveMarketStateReason(now, clockMode, ref canRegister0B);

            return new MarketStateSnapshot
            {
                Now = now,
                Session = session,
                RealtimeMode = clockMode,
                IsClosedDate = false,
                CanRegister0B = canRegister0B,
                ShouldUseFinalIndexSnapshot = shouldUseFinalIndexSnapshot,
                RealizedProfitQueryDate = queryDate,
                Reason = reasonText,
                SessionCode = _latestMarketSessionCode,
                SessionReceivedAt = _latestMarketSessionReceivedAt
            };
        }

        private string ResolveMarketStateReason(DateTime now, RealtimeMarketMode clockMode, ref bool canRegister0B)
        {
            if (clockMode == RealtimeMarketMode.Closed)
                return "거래시간 외";

            if (clockMode == RealtimeMarketMode.NxtOnlyPre)
                return "NXT 장전 시간";

            if (clockMode == RealtimeMarketMode.NxtOnlyAfter)
                return "NXT 장후 시간";

            if (clockMode == RealtimeMarketMode.KrxPrimary)
            {
                if (IsFreshMarketSessionCode(now))
                {
                    if (IsKrxOpenSessionCode(_latestMarketSessionCode))
                        return $"0s 정규장 열림(code={_latestMarketSessionCode})";

                    if (IsKnownKrxClosedSessionCode(_latestMarketSessionCode))
                    {
                        canRegister0B = false;
                        return $"0s 정규장 미개시/마감(code={_latestMarketSessionCode})";
                    }

                    return $"0s 관찰값 기록(code={_latestMarketSessionCode}) / 시간표 기준";
                }

                return "시간표 기준 KRX 장중";
            }

            return "시간표 기준";
        }

        private RealtimeMarketMode ResolveRealtimeMarketModeByClock(DateTime now)
        {
            TimeSpan current = now.TimeOfDay;

            if (current >= new TimeSpan(8, 0, 0) && current < new TimeSpan(9, 0, 0))
                return RealtimeMarketMode.NxtOnlyPre;

            if (current >= new TimeSpan(9, 0, 0) && current < new TimeSpan(15, 40, 0))
                return RealtimeMarketMode.KrxPrimary;

            if (current >= new TimeSpan(15, 40, 0) && current < new TimeSpan(20, 0, 0))
                return RealtimeMarketMode.NxtOnlyAfter;

            return RealtimeMarketMode.Closed;
        }

        private MarketSessionKind ToMarketSessionKind(RealtimeMarketMode mode)
        {
            return mode switch
            {
                RealtimeMarketMode.NxtOnlyPre => MarketSessionKind.NxtPre,
                RealtimeMarketMode.KrxPrimary => MarketSessionKind.KrxPrimary,
                RealtimeMarketMode.NxtOnlyAfter => MarketSessionKind.NxtAfter,
                _ => MarketSessionKind.Closed
            };
        }

        private bool IsFreshMarketSessionCode(DateTime now)
        {
            return !string.IsNullOrWhiteSpace(_latestMarketSessionCode)
                && _latestMarketSessionReceivedAt != DateTime.MinValue
                && now - _latestMarketSessionReceivedAt <= MarketSessionFreshness;
        }

        private bool IsKrxOpenSessionCode(string code)
        {
            return string.Equals((code ?? "").Trim(), "3", StringComparison.Ordinal);
        }

        private bool IsKnownKrxClosedSessionCode(string code)
        {
            string value = (code ?? "").Trim();
            return value == "2" || value == "4" || value == "8" || value == "9";
        }

        private void RegisterMarketSessionStatus()
        {
            try
            {
                if (_ws == null || !_isWsAuthenticated) return;

                var packet = new
                {
                    trnm = "REG",
                    grp_no = MarketSessionGroupNo,
                    refresh = "1",
                    data = new[]
                    {
                        new
                        {
                            item = new[] { "" },
                            type = new[] { "0s" }
                        }
                    }
                };

                _ws.Send(JsonConvert.SerializeObject(packet));
                Log("📡 [시장상태] 0s 장운영구분 등록");
            }
            catch (Exception ex)
            {
                Log($"⚠️ [시장상태] 0s 등록 오류: {ex.Message}");
            }
        }

        private void UpdateMarketSessionStateFromRealtime(JObject res)
        {
            try
            {
                JArray data = res["data"] as JArray;
                if (data == null || data.Count == 0)
                {
                    TryApplyMarketSessionItem(res);
                    return;
                }

                foreach (JToken item in data)
                    TryApplyMarketSessionItem(item);
            }
            catch (Exception ex)
            {
                Log($"⚠️ [시장상태] 0s 처리 오류: {ex.Message}");
            }
        }

        private void TryApplyMarketSessionItem(JToken item)
        {
            if (item == null) return;

            JObject values = item["values"] as JObject;
            string type = ReadMarketSessionValue(item, values, "type", "real_type", "rt_type");
            if (!string.Equals(type, "0s", StringComparison.OrdinalIgnoreCase))
                return;

            string code = ReadMarketSessionValue(item, values, "215", "jang_gb", "market_state", "장운영구분");
            string time = ReadMarketSessionValue(item, values, "20", "time", "체결시간");
            if (string.IsNullOrWhiteSpace(code)) return;

            _latestMarketSessionCode = code.Trim();
            _latestMarketSessionTime = time.Trim();
            _latestMarketSessionReceivedAt = DateTime.Now;

            if (!string.Equals(_lastLoggedMarketSessionCode, _latestMarketSessionCode, StringComparison.Ordinal))
            {
                _lastLoggedMarketSessionCode = _latestMarketSessionCode;
                Log($"🧭 [시장상태] 0s 수신 / code={_latestMarketSessionCode} / time={_latestMarketSessionTime}");
            }
        }

        private string ReadMarketSessionValue(JToken item, JObject values, params string[] keys)
        {
            foreach (string key in keys)
            {
                string value = ReadMarketSessionValueFromToken(values, key);
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();

                value = ReadMarketSessionValueFromToken(item, key);
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }

            return "";
        }

        private string ReadMarketSessionValueFromToken(JToken token, string key)
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

        private void LogMarketStateBlockedOnce(MarketStateSnapshot state, string source)
        {
            DateTime now = DateTime.Now;
            if ((now - _lastMarketStateLogAt).TotalSeconds < 60)
                return;

            _lastMarketStateLogAt = now;
            Log($"⏸ [시장상태] {source} 보류 / 상태={state.Session} / 사유={state.Reason}");
        }
    }
}
