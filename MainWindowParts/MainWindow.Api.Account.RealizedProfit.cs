#nullable disable
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private readonly object _todayRealizedProfitRefreshLock = new();
        private bool _todayRealizedProfitRefreshRunning = false;
        private DateTime _lastTodayRealizedProfitRefreshAt = DateTime.MinValue;
        private long? _lastLoggedTodayRealizedProfit = null;
        private long? _lastLoggedTodayTradeCommission = null;
        private long? _lastLoggedTodayTradeTax = null;
        private string _lastLoggedTodayRealizedProfitDate = "";

        private void AccountRequestTodayRealizedProfitRefresh(string source, bool force = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_token)) return;

                bool shouldStart = false;
                lock (_todayRealizedProfitRefreshLock)
                {
                    DateTime now = DateTime.Now;
                    if (_todayRealizedProfitRefreshRunning) return;
                    if (!force && (now - _lastTodayRealizedProfitRefreshAt).TotalSeconds < 30) return;

                    _todayRealizedProfitRefreshRunning = true;
                    _lastTodayRealizedProfitRefreshAt = now;
                    shouldStart = true;
                }

                if (!shouldStart) return;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await AccountFetchAndApplyTodayRealizedProfitAsync(source);
                    }
                    catch (Exception ex)
                    {
                        Log($"⚠️ [실현손익 조회 오류] {ex.Message}");
                    }
                    finally
                    {
                        lock (_todayRealizedProfitRefreshLock)
                        {
                            _todayRealizedProfitRefreshRunning = false;
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Log($"⚠️ [실현손익 갱신 시작 오류] {ex.Message}");
            }
        }

        private async Task AccountFetchAndApplyTodayRealizedProfitAsync(string source)
        {
            if (string.IsNullOrWhiteSpace(_token)) return;

            string queryDate = GetMarketStateNow().RealizedProfitQueryDate.ToString("yyyyMMdd");
            var requestBody = new JObject
            {
                ["strt_dt"] = queryDate,
                ["end_dt"] = queryDate
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.kiwoom.com/api/dostk/acnt");
            request.Headers.TryAddWithoutValidation("authorization", $"Bearer {_token}");
            request.Headers.TryAddWithoutValidation("api-id", "ka10074");
            request.Headers.TryAddWithoutValidation("cont-yn", "N");
            request.Headers.TryAddWithoutValidation("next-key", "");
            request.Content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await _http.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body)) return;

            JObject json;
            try
            {
                json = JObject.Parse(body);
            }
            catch
            {
                Log($"⚠️ [실현손익 응답 파싱 오류] ka10074 JSON 확인 필요 / {body}");
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                Log($"⚠️ [실현손익 조회 실패] HTTP {(int)response.StatusCode} / {response.ReasonPhrase} / {body}");
                return;
            }

            string returnCode = AccountRealizedGetStringAny(json, "return_code", "returnCode");
            if (!string.IsNullOrWhiteSpace(returnCode) && returnCode != "0")
            {
                string returnMsg = AccountRealizedGetStringAny(json, "return_msg", "returnMsg", "msg");
                Log($"⚠️ [실현손익 조회 실패] ka10074 return_code={returnCode} / {returnMsg}");
                return;
            }

            long realizedProfit = AccountRealizedGetLongAny(json,
                "rlzt_pl", "realized_pl", "realizedProfit", "todayRealizedProfit", "실현손익");
            long tradeCommission = AccountRealizedGetLongAny(json,
                "trde_cmsn", "trade_commission", "매매수수료");
            long tradeTax = AccountRealizedGetLongAny(json,
                "trde_tax", "trade_tax", "매매세금");

            // 일부 응답에서 상단 합계가 비어 있으면 dt_rlzt_pl 배열의 당일 손익을 합산한다.
            JArray rows = json["dt_rlzt_pl"] as JArray
                ?? json["dtRlztPl"] as JArray
                ?? json["realized_profit_by_date"] as JArray;

            if (realizedProfit == 0 && rows != null && rows.Count > 0)
            {
                realizedProfit = rows.OfType<JObject>().Sum(row => AccountRealizedGetLongAny(row, "tdy_sel_pl", "rlzt_pl", "todayRealizedProfit", "당일매도손익", "실현손익"));
                tradeCommission = rows.OfType<JObject>().Sum(row => AccountRealizedGetLongAny(row, "tdy_trde_cmsn", "trde_cmsn", "당일매매수수료", "매매수수료"));
                tradeTax = rows.OfType<JObject>().Sum(row => AccountRealizedGetLongAny(row, "tdy_trde_tax", "trde_tax", "당일매매세금", "매매세금"));
            }

            _serverRealizedProfitAmount = realizedProfit;

            void ApplyOnUi()
            {
                AccountSetTextBlock("TxtRealizedProfit", realizedProfit >= 0
                    ? $"+₩ {realizedProfit:N0}"
                    : $"-₩ {Math.Abs(realizedProfit):N0}");

                Brush color = AccountGetRateColor(realizedProfit);
                AccountSetTextBlockBrush("TxtRealizedProfit", color);
            }

            if (Dispatcher.CheckAccess()) ApplyOnUi();
            else Dispatcher.Invoke(ApplyOnUi);

            bool changed = !_lastLoggedTodayRealizedProfit.HasValue
                || _lastLoggedTodayRealizedProfit.Value != realizedProfit
                || !_lastLoggedTodayTradeCommission.HasValue
                || _lastLoggedTodayTradeCommission.Value != tradeCommission
                || !_lastLoggedTodayTradeTax.HasValue
                || _lastLoggedTodayTradeTax.Value != tradeTax
                || !string.Equals(_lastLoggedTodayRealizedProfitDate, queryDate, StringComparison.Ordinal);
            bool isStartup = string.Equals(source, "startup", StringComparison.OrdinalIgnoreCase);
            if (changed || isStartup)
            {
                _lastLoggedTodayRealizedProfit = realizedProfit;
                _lastLoggedTodayTradeCommission = tradeCommission;
                _lastLoggedTodayTradeTax = tradeTax;
                _lastLoggedTodayRealizedProfitDate = queryDate;
                Log($"💰 [실현손익] ka10074 기준일 {queryDate} 실현손익 반영 / 실현 {realizedProfit:N0} / 수수료 {tradeCommission:N0} / 세금 {tradeTax:N0} / {source}");
            }

            if (changed && !isStartup)
            {
                Log($"🔁 [잔고동기화] 실현손익 변경 감지 → kt00005 보유잔고 재조회 / 기준일={queryDate} / {source}");
                await SyncBalanceSafelyAsync("실현손익 변경 감지 / 보유잔고 재조회", force: true);
            }
        }

        private DateTime AccountGetPreviousBusinessDate(DateTime date)
        {
            while (IsMarketClosedDate(date))
                date = date.AddDays(-1);

            return date;
        }

        private string AccountRealizedGetStringAny(JToken token, params string[] names)
        {
            if (token == null || names == null) return "";

            foreach (string name in names)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                JToken found = AccountRealizedFindTokenRecursive(token, name);
                if (found == null) continue;
                string value = found.Type == JTokenType.String ? found.ToString() : found.ToString(Formatting.None);
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }

            return "";
        }

        private long AccountRealizedGetLongAny(JToken token, params string[] names)
        {
            string value = AccountRealizedGetStringAny(token, names);
            return AccountRealizedParseLong(value);
        }

        private JToken AccountRealizedFindTokenRecursive(JToken token, string name)
        {
            if (token == null || string.IsNullOrWhiteSpace(name)) return null;

            if (token is JObject obj)
            {
                foreach (JProperty prop in obj.Properties())
                {
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                        return prop.Value;

                    JToken child = AccountRealizedFindTokenRecursive(prop.Value, name);
                    if (child != null) return child;
                }
            }
            else if (token is JArray arr)
            {
                foreach (JToken item in arr)
                {
                    JToken child = AccountRealizedFindTokenRecursive(item, name);
                    if (child != null) return child;
                }
            }

            return null;
        }

        private long AccountRealizedParseLong(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;

            string value = text.Trim();
            bool negative = value.Contains("-");
            string digits = new([.. value.Where(char.IsDigit)]);
            if (string.IsNullOrWhiteSpace(digits)) return 0;
            if (!long.TryParse(digits, out long parsed)) return 0;
            return negative ? -parsed : parsed;
        }
    }
}
