#nullable disable
using KHStrategyLab.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Media;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private readonly Dictionary<string, string> _realtimeBalanceMarketByCode = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastRealtimeBalanceSummaryLogAt = DateTime.MinValue;
        private DateTime _lastNxtCloseFetchAttemptAt = DateTime.MinValue;
        private bool _nxtCloseFetchRunning = false;
        private readonly object _nxtCloseFetchLock = new();

        private string _nxtCloseFetchCompletedWindowKey = "";

        private void AccountApplyRealtimeBalancePrice(HoldingStock holding, long realtimePrice, bool isNxtSnapshot)
        {
            AccountApplyRealtimeBalancePrice(holding, realtimePrice, isNxtSnapshot, persistNxtSnapshot: true, source: "0B");
        }

        private void AccountApplyRealtimeBalancePrice(HoldingStock holding, long realtimePrice, bool isNxtSnapshot, bool persistNxtSnapshot, string source)
        {
            if (holding == null || realtimePrice <= 0) return;

            string code = AccountRealtimeNormalizeCode(holding.Code);
            if (string.IsNullOrWhiteSpace(code)) return;

            // kt00005가 KRX 기준 현재가를 주더라도, NXT 0B/NXT 마지막 평가가격가 들어오면
            // 보유행 현재가를 해당 가격으로 먼저 바꾼 뒤 평가금액/손익률을 다시 계산한다.
            holding.CurrentPrice = realtimePrice;

            string market = isNxtSnapshot ? "NXT" : "KRX";
            _realtimeBalanceMarketByCode[code] = market;

            // NXT 가격은 화면/잔고에 즉시 반영한다. 파일 저장은 하지 않는다.
            // 20:00 이후 재실행 시에는 AccountFetchAndApplyNxtCloseBalancePricesAsync가
            // 보유 NXT 종목의 마지막 평가가격을 다시 조회해 화면에 바로 반영한다.

            AccountRealtimeValuation rowValue = AccountBuildRealtimeValuation(holding, market);
            if (rowValue.BuyAmount <= 0 || rowValue.GrossEvalAmount <= 0) return;

            string sourceTag = isNxtSnapshot ? "NXT" : "KRX";
            holding.TradingValueText = $"평가 {rowValue.GrossEvalAmount:N0} / 비용 {rowValue.TotalCost:N0} / {sourceTag}";
            holding.ProfitRateText = AccountFormatRate(rowValue.ProfitRate);
            holding.ProfitColor = AccountGetRateColor(rowValue.ProfitRate);

            AccountUpdateTopAccountSummaryFromRealtimeBalance();
        }

        // 장외(20:00~다음날 07:00 전)에는 자동추적/신규 0B 등록만 멈추고,
        // 보유잔고 화면은 NXT 마지막 평가가격으로 보정한다.
        // 파일 캐시는 쓰지 않는다. 프로그램을 20:00 이후 다시 켠 경우에만 보유 NXT 종목의
        // SOR/NXT 종목정보(_AL/_NX)를 조회해 화면에 바로 반영한다.
        private void AccountEnsureNxtCloseBalancePrices(string source)
        {
            try
            {
                if (!AccountShouldUseNxtCloseNow()) return;

                string windowKey = AccountGetNxtCloseWindowKey();
                if (!string.IsNullOrWhiteSpace(windowKey)
                    && string.Equals(_nxtCloseFetchCompletedWindowKey, windowKey, StringComparison.OrdinalIgnoreCase))
                    return;

                bool shouldStart = false;
                lock (_nxtCloseFetchLock)
                {
                    DateTime now = DateTime.Now;
                    if (!_nxtCloseFetchRunning && (now - _lastNxtCloseFetchAttemptAt).TotalSeconds >= 60)
                    {
                        _nxtCloseFetchRunning = true;
                        _lastNxtCloseFetchAttemptAt = now;
                        shouldStart = true;
                    }
                }

                if (!shouldStart) return;

                Log($"🌙 [NXT잔고보정] 파일저장 없이 보유 NXT 종목 마지막 평가가격 조회 시도 / {source}");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await AccountFetchAndApplyNxtCloseBalancePricesAsync(source);
                    }
                    catch (Exception ex)
                    {
                        Log($"⚠️ [NXT잔고보정 오류] {ex.Message}");
                    }
                    finally
                    {
                        lock (_nxtCloseFetchLock)
                        {
                            _nxtCloseFetchRunning = false;
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Log($"⚠️ [NXT잔고보정 시작 오류] {ex.Message}");
            }
        }

        private async Task AccountFetchAndApplyNxtCloseBalancePricesAsync(string source)
        {
            var targets = new List<AccountNxtCloseFetchTarget>();

            try
            {
                void CollectOnUi()
                {
                    foreach (HoldingStock holding in _balance)
                    {
                        if (holding == null || holding.Volume <= 0) continue;
                        string code = AccountRealtimeNormalizeCode(holding.Code);
                        if (string.IsNullOrWhiteSpace(code)) continue;

                        targets.Add(new AccountNxtCloseFetchTarget
                        {
                            Code = code,
                            Name = string.IsNullOrWhiteSpace(holding.Name) ? code : holding.Name,
                            Quantity = holding.Volume
                        });
                    }
                }

                if (Dispatcher.CheckAccess()) CollectOnUi();
                else Dispatcher.Invoke(CollectOnUi);
            }
            catch
            {
                return;
            }

            targets = [.. targets
                .GroupBy(x => x.Code)
                .Select(g => g.First())];

            if (targets.Count == 0) return;

            int nxtEligible = 0;
            int applied = 0;

            foreach (AccountNxtCloseFetchTarget target in targets)
            {
                try
                {
                    bool isNxt = await IsNxtEnabledAsync(target.Code);
                    if (!isNxt) continue;
                    nxtEligible++;

                    long price = await AccountFetchNxtClosePriceFromStockInfoAsync(target.Code);
                    if (price <= 0)
                    {
                        Log($"ℹ️ [NXT잔고보정] NXT 가격 조회 실패/값없음: {target.Name}({target.Code})");
                        continue;
                    }

                    void ApplyOnUi()
                    {
                        HoldingStock row = _balance.FirstOrDefault(x => AccountRealtimeNormalizeCode(x.Code) == target.Code);
                        if (row == null) return;
                        AccountApplyRealtimeBalancePrice(row, price, isNxtSnapshot: true, persistNxtSnapshot: false, source: "NXT_CLOSE_FETCH");
                    }

                    if (Dispatcher.CheckAccess()) ApplyOnUi();
                    else Dispatcher.Invoke(ApplyOnUi);

                    applied++;
                    await Task.Delay(120);
                }
                catch (Exception ex)
                {
                    Log($"⚠️ [NXT잔고보정 종목 오류] {target.Code} / {ex.Message}");
                }
            }

            if (applied > 0)
            {
                Dispatcher.Invoke(() =>
                {
                    AccountUpdateTopAccountSummaryFromRealtimeBalance();
                });

                _nxtCloseFetchCompletedWindowKey = AccountGetNxtCloseWindowKey();
                Log($"🌙 [NXT잔고보정] 보유 NXT 마지막 평가가격 화면반영 완료: 적용 {applied}종목 / NXT가능 {nxtEligible}종목 / 파일저장 없음 / {source}");
            }
            else
            {
                Log($"ℹ️ [NXT잔고보정] 적용된 보유 NXT 가격 없음: NXT가능 {nxtEligible}종목 / {source}");
            }
        }

        private async Task<long> AccountFetchNxtClosePriceFromStockInfoAsync(string code)
        {
            code = AccountRealtimeNormalizeCode(code);
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(_token)) return 0;

            // SOR(_AL)를 먼저 시도하고, 혹시 실시간/NXT 식별자가 먹는 계정이면 _NX도 한 번 더 시도한다.
            // 6자리 KRX 코드는 여기서 쓰지 않는다. 장외 NXT 보정값에 KRX 값을 섞지 않기 위해서다.
            string[] requestCodes = [code + "_AL", code + "_NX"];

            foreach (string requestCode in requestCodes)
            {
                try
                {
                    JObject json = await AccountRequestStockInfoJsonAsync(requestCode);
                    if (json == null) continue;

                    string returnCode = json["return_code"]?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(returnCode) && returnCode != "0") continue;

                    string priceText = ReadJsonValue(json, "cur_prc", "curPrc", "price", "now_prc", "현재가", "10");
                    long price = Math.Abs(ParseLongSafe(priceText));
                    if (price > 0) return price;
                }
                catch
                {
                    // 다음 식별자로 재시도한다.
                }
            }

            return 0;
        }

        private async Task<JObject> AccountRequestStockInfoJsonAsync(string requestCode)
        {
            if (string.IsNullOrWhiteSpace(requestCode) || string.IsNullOrWhiteSpace(_token)) return null;

            string url = "https://api.kiwoom.com/api/dostk/stkinfo";
            var body = new { stk_cd = requestCode };
            string bodyJson = JsonConvert.SerializeObject(body);

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("authorization", $"Bearer {_token}");
            request.Headers.TryAddWithoutValidation("api-id", "ka10001");
            request.Headers.TryAddWithoutValidation("cont-yn", "N");
            request.Headers.TryAddWithoutValidation("next-key", "");
            request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await _stockInfoHttp.SendAsync(request);
            string text = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(text)) return null;

            JObject json;
            try
            {
                json = JObject.Parse(text);
            }
            catch
            {
                return null;
            }

            if (!response.IsSuccessStatusCode) return null;
            return json;
        }

        private bool AccountShouldUseNxtCloseNow()
        {
            TimeSpan now = DateTime.Now.TimeOfDay;
            return now >= new TimeSpan(20, 0, 0) || now < new TimeSpan(7, 0, 0);
        }

        private string AccountGetNxtCloseWindowKey()
        {
            DateTime now = DateTime.Now;
            if (now.TimeOfDay >= new TimeSpan(20, 0, 0))
                return now.ToString("yyyyMMdd");

            if (now.TimeOfDay < new TimeSpan(7, 0, 0))
                return now.AddDays(-1).ToString("yyyyMMdd");

            return "";
        }

        private void AccountUpdateTopAccountSummaryFromRealtimeBalance()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(AccountUpdateTopAccountSummaryFromRealtimeBalance);
                return;
            }

            long totalBuy = 0;
            long totalGrossEval = 0;
            long totalNetEvalAfterSellCost = 0;
            long totalProfitAfterCost = 0;
            long totalCost = 0;
            int nxtRows = 0;

            foreach (HoldingStock item in _balance)
            {
                if (item == null || item.Volume <= 0) continue;

                string code = AccountRealtimeNormalizeCode(item.Code);
                string market = "KRX";
                if (!string.IsNullOrWhiteSpace(code) && _realtimeBalanceMarketByCode.TryGetValue(code, out string savedMarket))
                    market = savedMarket;
                else if (!string.IsNullOrWhiteSpace(item.TurnoverRateText) && item.TurnoverRateText.IndexOf("NXT", StringComparison.OrdinalIgnoreCase) >= 0)
                    market = "NXT";

                if (string.Equals(market, "NXT", StringComparison.OrdinalIgnoreCase)) nxtRows++;

                AccountRealtimeValuation v = AccountBuildRealtimeValuation(item, market);
                totalBuy += v.BuyAmount;
                totalGrossEval += v.GrossEvalAmount;
                totalNetEvalAfterSellCost += v.NetEvalAfterSellCost;
                totalProfitAfterCost += v.ProfitAfterCost;
                totalCost += v.TotalCost;
            }

            double profitRate = totalBuy > 0
                ? Math.Round(totalProfitAfterCost / (double)totalBuy * 100.0, 2, MidpointRounding.AwayFromZero)
                : 0;

            AccountSetTextBlock("TxtTotalAsset", $"₩ {totalGrossEval:N0}");
            AccountSetTextBlock("TxtTotalBuy", $"매입 ₩ {totalBuy:N0} / 비용 ₩ {totalCost:N0}");
            AccountSetTextBlock("TxtProfitRate", AccountFormatRate(profitRate));
            AccountSetTextBlock("TxtProfitAmt", totalProfitAfterCost >= 0 ? $"+₩ {totalProfitAfterCost:N0}" : $"-₩ {Math.Abs(totalProfitAfterCost):N0}");
            AccountSetTextBlock("TxtRealizedProfit", _serverRealizedProfitAmount >= 0 ? $"+₩ {_serverRealizedProfitAmount:N0}" : $"-₩ {Math.Abs(_serverRealizedProfitAmount):N0}");
            AccountSetTextBlock("TxtEstimatedAsset", $"실시간추정 ₩ {totalNetEvalAfterSellCost:N0} / NXT {nxtRows}종목");

            Brush color = AccountGetRateColor(profitRate);
            AccountSetTextBlockBrush("TxtProfitRate", color);
            AccountSetTextBlockBrush("TxtProfitAmt", color);
            AccountSetTextBlockBrush("TxtRealizedProfit", AccountGetRateColor(_serverRealizedProfitAmount));

            DateTime now = DateTime.Now;
            if ((now - _lastRealtimeBalanceSummaryLogAt).TotalSeconds >= 15)
            {
                _lastRealtimeBalanceSummaryLogAt = now;
                Log($"💹 [잔고실시간] 0B/NXT종가 기준 총잔고 재계산 / 평가 {totalGrossEval:N0} / 비용 {totalCost:N0} / 손익 {totalProfitAfterCost:N0} / 수익률 {profitRate:F2}% / NXT {nxtRows}종목");
            }
        }

        private AccountRealtimeValuation AccountBuildRealtimeValuation(HoldingStock item, string market)
        {
            var result = new AccountRealtimeValuation();
            if (item == null || item.Volume <= 0) return result;

            long qty = item.Volume;
            long buyPrice = Math.Abs(item.BuyPrice);
            long currentPrice = Math.Abs(item.CurrentPrice);
            if (currentPrice <= 0) currentPrice = buyPrice;

            long buyAmount = buyPrice > 0 ? buyPrice * qty : 0;
            long grossEval = currentPrice > 0 ? currentPrice * qty : 0;

            var rates = AccountGetCostRates(string.IsNullOrWhiteSpace(market) ? "KRX" : market);
            long buyCommission = AccountEstimateCost(buyAmount, rates.CommissionRate);
            long sellCommission = AccountEstimateCost(grossEval, rates.CommissionRate);
            long sellTax = AccountEstimateCost(grossEval, rates.SellTaxRate);
            long totalCost = buyCommission + sellCommission + sellTax;
            long netEvalAfterSellCost = grossEval - sellCommission - sellTax;
            long profitAfterCost = grossEval - buyAmount - totalCost;
            double profitRate = buyAmount > 0
                ? Math.Round(profitAfterCost / (double)buyAmount * 100.0, 2, MidpointRounding.AwayFromZero)
                : 0;

            result.BuyAmount = buyAmount;
            result.GrossEvalAmount = grossEval;
            result.BuyCommission = buyCommission;
            result.SellCommission = sellCommission;
            result.SellTax = sellTax;
            result.TotalCost = totalCost;
            result.NetEvalAfterSellCost = netEvalAfterSellCost;
            result.ProfitAfterCost = profitAfterCost;
            result.ProfitRate = profitRate;
            return result;
        }

        private string AccountRealtimeNormalizeCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return "";
            string value = code.Trim().ToUpperInvariant();
            value = value.Replace("_NX", "").Replace("_AL", "");
            if (value.StartsWith("A", StringComparison.OrdinalIgnoreCase)) value = value.Substring(1);
            string digits = new([.. value.Where(char.IsDigit)]);
            if (digits.Length >= 6) return digits.Substring(digits.Length - 6);
            return digits;
        }

        private sealed class AccountNxtCloseFetchTarget
        {
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
            public long Quantity { get; set; }
        }

        private sealed class AccountRealtimeValuation
        {
            public long BuyAmount { get; set; }
            public long GrossEvalAmount { get; set; }
            public long BuyCommission { get; set; }
            public long SellCommission { get; set; }
            public long SellTax { get; set; }
            public long TotalCost { get; set; }
            public long NetEvalAfterSellCost { get; set; }
            public long ProfitAfterCost { get; set; }
            public double ProfitRate { get; set; }
        }
    }
}
