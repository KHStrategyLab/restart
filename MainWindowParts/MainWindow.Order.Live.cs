#nullable disable

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private async Task TryExecuteLiveBuyAsync(LiveOrderSignal signal)
        {
            if (signal == null)
                return;

            signal.Code = NormalizeStockCode(signal.Code);
            signal.Market = NormalizeLiveOrderMarketText(signal.Market);
            signal.OrderPrice = signal.OrderPrice > 0 ? signal.OrderPrice : signal.SignalPrice;

            LiveOrderRiskResult risk = EvaluateLiveBuyRiskGuard(signal);
            LogLiveBuyPreflight(signal, risk);

            if (!risk.Allowed)
            {
                string blockLog = risk.BlockReason == "LiveOrderEnabled=OFF"
                    ? "[실주문상태] LiveOrderEnabled=OFF / 신호만 발생 / 주문전송 없음"
                    : $"⛔ [실주문 차단] {signal.Name}({signal.Code}) / 전략={signal.StrategyCode} / 사유={risk.BlockReason}";

                Log(blockLog);

                string telegram =
                    risk.BlockReason == "LiveOrderEnabled=OFF"
                        ? $"[KHStrategyLab] 실주문 OFF\n{signal.Name}({signal.Code})\n전략: {signal.StrategyCode}\n신호만 발생했고 주문은 전송하지 않음"
                        : $"[KHStrategyLab] 리스크 가드 차단\n{signal.Name}({signal.Code})\n전략: {signal.StrategyCode}\n사유: {risk.BlockReason}";

                await SendTelegramMessageAsync(telegram);
                return;
            }

            string code = signal.Code;
            lock (_liveOrderStateLock)
            {
                ResetDailyLiveOrderStateIfNeededLocked();

                if (_liveOrderInProgressByCode.ContainsKey(code))
                {
                    Log($"⛔ [실주문 중복차단] {signal.Name}({code}) / 이미 주문 진행 중");
                    return;
                }

                _liveOrderInProgressByCode[code] = new LiveOrderPendingState
                {
                    Code = code,
                    Name = signal.Name,
                    StrategyCode = signal.StrategyCode,
                    LastBuySignalAt = signal.SignalTime,
                    LastOrderStrategyCode = signal.StrategyCode,
                    LastOrderRequestedAt = DateTime.Now,
                    Side = "BUY"
                };
            }

            try
            {
                LiveOrderApiResult orderResult = await SendKiwoomOrderAsync(
                    apiId: "kt10000",
                    side: "BUY",
                    market: risk.OrderMarket,
                    code: code,
                    quantity: risk.Quantity,
                    orderPrice: risk.OrderPrice,
                    tradeType: "0");

                if (!orderResult.Success)
                {
                    MarkLiveBuyOrderAttemptedToday(code, signal.StrategyCode);
                    string failMessage = $"❌ [매수 주문 실패] {signal.Name}({code}) / 전략={signal.StrategyCode} / 사유={orderResult.Message}";
                    Log(failMessage);
                    await SendTelegramMessageAsync($"[KHStrategyLab] 매수 주문 실패\n{signal.Name}({code})\n전략: {signal.StrategyCode}\n사유: {orderResult.Message}");
                    return;
                }

                lock (_liveOrderStateLock)
                {
                    ResetDailyLiveOrderStateIfNeededLocked();
                    _tradedStrategyKeysToday.Add(BuildOrderedStrategyKey(DateTime.Today, code, signal.StrategyCode));
                    _programManagedPositionsByCode[code] = new ProgramManagedPosition
                    {
                        Code = code,
                        Name = signal.Name,
                        StrategyCode = signal.StrategyCode,
                        StrategyGroup = signal.StrategyGroup,
                        Market = risk.OrderMarket,
                        EntryPrice = risk.OrderPrice,
                        EntryQuantity = risk.Quantity,
                        BuyOrderNo = orderResult.OrderNo,
                        EntryTime = DateTime.Now,
                        TargetPrice = signal.TargetPrice,
                        StopPrice = signal.StopPrice,
                        EntrySource = signal.EntrySource,
                        ExitMode = signal.ExitMode,
                        TrailingStartRatePercent = signal.TrailingStartRatePercent,
                        TrailingDropRatePercent = signal.TrailingDropRatePercent,
                        TrailingSellPercent = signal.TrailingSellPercent,
                        TrailingHighPrice = risk.OrderPrice,
                        LiveOrder = true,
                        SellCompleted = false
                    };

                    if (_liveOrderInProgressByCode.TryGetValue(code, out LiveOrderPendingState pending))
                    {
                        pending.LastOrderNo = orderResult.OrderNo;
                        pending.LastOrderResultMessage = orderResult.Message;
                    }
                }

                SaveLiveOrderState();

                Log($"✅ [매수 주문 접수] {signal.Name}({code}) / 시장={risk.OrderMarket} / 전략={signal.StrategyCode} / 수량={risk.Quantity:N0} / 가격={risk.OrderPrice:N0} / 주문번호={orderResult.OrderNo} / 잔고 재조회 예약");
                await SendTelegramMessageAsync(
                    $"[KHStrategyLab] 매수 주문 접수\n{signal.Name}({code})\n시장: {risk.OrderMarket}\n전략: {signal.StrategyCode}\n수량: {risk.Quantity:N0}\n가격: {risk.OrderPrice:N0}\n주문번호: {orderResult.OrderNo}\n잔고 재조회 예약");

                await SyncBalanceAfterOrderAsync($"매수 주문 접수 {signal.Name}({code})");
            }
            finally
            {
                lock (_liveOrderStateLock)
                {
                    _liveOrderInProgressByCode.Remove(code);
                }
            }
        }

        private async Task<LiveOrderApiResult> SendKiwoomOrderAsync(
            string apiId,
            string side,
            string market,
            string code,
            int quantity,
            long orderPrice,
            string tradeType)
        {
            try
            {
                JObject body = new()
                {
                    ["dmst_stex_tp"] = market,
                    ["stk_cd"] = code,
                    ["ord_qty"] = quantity.ToString(),
                    ["ord_uv"] = orderPrice > 0 ? orderPrice.ToString() : "",
                    ["trde_tp"] = string.IsNullOrWhiteSpace(tradeType) ? "0" : tradeType,
                    ["cond_uv"] = ""
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.kiwoom.com/api/dostk/ordr");
                request.Headers.TryAddWithoutValidation("authorization", $"Bearer {_token}");
                request.Headers.TryAddWithoutValidation("api-id", apiId);
                request.Headers.TryAddWithoutValidation("cont-yn", "N");
                request.Headers.TryAddWithoutValidation("next-key", "");
                request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

                using HttpResponseMessage response = await _http.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();
                await SaveOrderRawAsync(apiId, code, responseBody);

                if (!response.IsSuccessStatusCode)
                    return LiveOrderApiResult.Fail($"HTTP {(int)response.StatusCode} / {response.ReasonPhrase}");

                JObject json;
                try
                {
                    json = JObject.Parse(responseBody);
                }
                catch
                {
                    return LiveOrderApiResult.Fail("주문 응답 JSON 파싱 실패");
                }

                string returnCode = ReadOrderStringAny(json, "return_code", "returnCode", "code", "rt_cd");
                string returnMsg = ReadOrderStringAny(json, "return_msg", "returnMsg", "msg", "message");

                if (!string.IsNullOrWhiteSpace(returnCode) && returnCode != "0")
                    return LiveOrderApiResult.Fail($"응답 오류 code={returnCode} / msg={returnMsg}");

                string orderNo = ReadOrderStringRecursive(json, "ord_no", "ordNo", "order_no", "orderNo", "주문번호");
                if (string.IsNullOrWhiteSpace(orderNo))
                    return LiveOrderApiResult.Fail("주문번호 없음");

                return LiveOrderApiResult.Ok(orderNo, string.IsNullOrWhiteSpace(returnMsg) ? $"{side} 주문 접수" : returnMsg);
            }
            catch (Exception ex)
            {
                return LiveOrderApiResult.Fail(ex.Message);
            }
        }

        private void LogLiveBuyPreflight(LiveOrderSignal signal, LiveOrderRiskResult risk)
        {
            Log(
                $"🧾 [실주문 사전점검] {signal.Name}({signal.Code}) / 전략명={signal.StrategyName} / 전략코드={signal.StrategyCode} / " +
                $"시장={risk.OrderMarket} / 신호가={signal.SignalPrice:N0} / 주문기준가={risk.OrderPrice:N0} / " +
                $"진입예산={risk.Budget:N0}(원예산 {risk.OriginalBudget:N0} × {risk.BudgetPercent}% / {risk.BudgetSource} {risk.BudgetScoreGrade} {risk.BudgetScorePercent:0.##}%) / 계산수량={risk.Quantity:N0} / 예상금액={risk.ExpectedAmount:N0} / " +
                $"청산모드={(string.IsNullOrWhiteSpace(signal.ExitMode) ? "FIXED_TARGET_STOP" : signal.ExitMode)} / " +
                $"보유={risk.IsHolding} / 진행중={risk.IsOrderInProgress} / 당일주문={risk.IsAlreadyOrderedToday} / " +
                $"슬롯={risk.CurrentSlotCount}/{risk.MaxSlots} / LiveOrderEnabled={(_liveOrderEnabled ? "ON" : "OFF")} / " +
                $"주문가능={risk.Allowed} / 차단사유={(string.IsNullOrWhiteSpace(risk.BlockReason) ? "-" : risk.BlockReason)}");
        }

        private void MarkLiveBuyOrderAttemptedToday(string code, string strategyCode)
        {
            lock (_liveOrderStateLock)
            {
                ResetDailyLiveOrderStateIfNeededLocked();
                _tradedStrategyKeysToday.Add(BuildOrderedStrategyKey(DateTime.Today, code, strategyCode));
            }

            SaveLiveOrderState();
        }

        private async Task SaveOrderRawAsync(string apiId, string code, string raw)
        {
            try
            {
                Directory.CreateDirectory(_rawLogDir);
                string safeCode = NormalizeStockCode(code);
                string fileName = $"ORDER_{apiId}_{safeCode}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.json";
                string path = Path.Combine(_rawLogDir, fileName);
                await File.WriteAllTextAsync(path, raw ?? "", Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log($"⚠️ [주문 원문저장 실패] {apiId} / {code} / {ex.Message}");
            }
        }

        private string ReadOrderStringRecursive(JToken token, params string[] names)
        {
            if (token == null)
                return "";

            string direct = ReadOrderStringAny(token, names);
            if (!string.IsNullOrWhiteSpace(direct))
                return direct;

            if (token is JObject obj)
            {
                foreach (JProperty prop in obj.Properties())
                {
                    string found = ReadOrderStringRecursive(prop.Value, names);
                    if (!string.IsNullOrWhiteSpace(found))
                        return found;
                }
            }

            if (token is JArray arr)
            {
                foreach (JToken child in arr)
                {
                    string found = ReadOrderStringRecursive(child, names);
                    if (!string.IsNullOrWhiteSpace(found))
                        return found;
                }
            }

            return "";
        }

        private string ReadOrderStringAny(JToken token, params string[] names)
        {
            if (token is not JObject obj)
                return "";

            foreach (string name in names)
            {
                JToken value = obj[name];
                if (value == null)
                {
                    JProperty prop = obj.Properties().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                    value = prop?.Value;
                }

                string text = value?.ToString()?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }

            return "";
        }

        private sealed class LiveOrderApiResult
        {
            public bool Success { get; set; }
            public string OrderNo { get; set; } = "";
            public string Message { get; set; } = "";

            public static LiveOrderApiResult Ok(string orderNo, string message)
            {
                return new LiveOrderApiResult { Success = true, OrderNo = orderNo, Message = message };
            }

            public static LiveOrderApiResult Fail(string message)
            {
                return new LiveOrderApiResult { Success = false, Message = message };
            }
        }
    }
}
