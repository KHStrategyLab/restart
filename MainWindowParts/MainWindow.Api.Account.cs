#nullable disable

using KHStrategyLab.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private readonly SemaphoreSlim _balanceSyncGate = new(1, 1);
        private DateTime _lastBalanceSyncAt = DateTime.MinValue;
        private static readonly TimeSpan BalanceSyncInterval = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan BalanceSyncAfterOrderDelay = TimeSpan.FromSeconds(3);

        private long _serverEstimatedAssetAmount = 0;
        private long _serverRealizedProfitAmount = 0;
        private long _serverTotalBuyAmount = 0;
        private long _serverTotalEvalAmount = 0;
        private long _serverTotalProfitAmount = 0;
        private double? _serverTotalProfitRate = null;
        private long _accountCommission = 0;
        private long _accountTax = 0;

        private async Task FetchAccountAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_token))
                {
                    Log("⚠️ [계좌조회] 토큰이 없습니다.");
                    AccountSetTextBlock("TxtAccountDisplay", "ACC: 토큰 없음");
                    return;
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.kiwoom.com/api/dostk/acnt");
                request.Headers.TryAddWithoutValidation("authorization", $"Bearer {_token}");
                request.Headers.TryAddWithoutValidation("api-id", "ka00001");
                request.Headers.TryAddWithoutValidation("cont-yn", "N");
                request.Headers.TryAddWithoutValidation("next-key", "");
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                using HttpResponseMessage response = await _http.SendAsync(request);
                string body = await response.Content.ReadAsStringAsync();

                await SaveRawAsync("ACCOUNT_ka00001", body);

                if (!response.IsSuccessStatusCode)
                {
                    Log($"❌ [계좌조회 실패] HTTP {(int)response.StatusCode} / {response.ReasonPhrase}");
                    Log($"❌ [계좌조회 응답] {body}");
                    AccountSetTextBlock("TxtAccountDisplay", "ACC: 조회 실패");
                    return;
                }

                JObject json = JObject.Parse(body);
                _accNo = AccountGetStringAny(json,
                    "acctNo", "acct_no", "acnt_no", "account_no", "acc_no", "계좌번호");

                if (string.IsNullOrWhiteSpace(_accNo))
                {
                    _accNo = AccountFindFirstStringRecursive(json,
                        "acctNo", "acct_no", "acnt_no", "account_no", "acc_no", "계좌번호");
                }

                if (string.IsNullOrWhiteSpace(_accNo))
                {
                    Log($"⚠️ [계좌조회] 계좌번호 필드 확인 필요: {body}");
                    AccountSetTextBlock("TxtAccountDisplay", "ACC: 필드 확인");
                    return;
                }

                AccountSetTextBlock("TxtAccountDisplay", $"ACC: {AccountMaskAccount(_accNo)}");
                Log($"✅ [계좌] 계좌번호 확인: {AccountMaskAccount(_accNo)}");

                await SyncBalanceSafelyAsync("로그인 직후", force: true);
            }
            catch (Exception ex)
            {
                Log($"❌ [계좌조회 오류] {ex.Message}");
                AccountSetTextBlock("TxtAccountDisplay", "ACC: 오류");
            }
        }

        private async Task SyncBalanceSafelyAsync(string reason, bool force = false)
        {
            if (string.IsNullOrWhiteSpace(_token))
                return;

            if (!force && DateTime.Now - _lastBalanceSyncAt < BalanceSyncInterval)
                return;

            if (!await _balanceSyncGate.WaitAsync(0))
            {
                Log($"⏭️ [잔고조회 생략] {reason} / 이전 잔고조회 진행 중");
                return;
            }

            try
            {
                await SyncBalanceWithServerAsync();
                _lastBalanceSyncAt = DateTime.Now;
            }
            finally
            {
                _balanceSyncGate.Release();
            }
        }

        private async Task SyncBalanceAfterOrderAsync(string reason)
        {
            await Task.Delay(BalanceSyncAfterOrderDelay);
            await SyncBalanceSafelyAsync($"{reason} / 주문 후 잔고 재조회", force: true);
        }

        private async Task SyncBalanceWithServerAsync()
        {
            if (string.IsNullOrWhiteSpace(_token))
                return;

            try
            {
                List<AccountKt00005Row> allRows = [];

                Log("📌 [kt00005] 통합 잔고는 KRX 단일 호출로 조회 / KRX+NXT 이중호출 금지");
                allRows.AddRange(await RequestKt00005RowsAllPagesAsync("KRX"));

                List<AccountKt00005AggregatedHolding> aggregated = BuildKt00005AggregatedHoldings(allRows);
                List<HoldingStock> newBalances = BuildHoldingStocksFromKt00005(aggregated);

                _serverTotalBuyAmount = aggregated.Sum(x => x.TotalBuyAmount);
                _serverTotalEvalAmount = aggregated.Sum(x => x.TotalEvalAmount);
                _serverTotalProfitAmount = aggregated.Sum(x => x.TotalProfitAmount);
                _serverTotalProfitRate = _serverTotalBuyAmount > 0
                    ? Math.Round(_serverTotalProfitAmount / (double)_serverTotalBuyAmount * 100.0, 4, MidpointRounding.AwayFromZero)
                    : null;

                _accountCommission = aggregated.Sum(x => x.EstimatedBuyCommission + x.EstimatedSellCommission);
                _accountTax = aggregated.Sum(x => x.EstimatedSellTax);

                Dispatcher.Invoke(() =>
                {
                    string selectedCode = GetSelectedGridCode(GridBalance);
                    bool restoreKeyboardFocus = IsGridKeyboardFocusWithin(GridBalance);
                    int selectedIndex = GetSelectedGridIndex(GridBalance);
                    Dictionary<string, long> oldStopLossByCode = [];

                    foreach (HoldingStock oldItem in _balance)
                    {
                        if (oldItem == null || string.IsNullOrWhiteSpace(oldItem.Code))
                            continue;

                        oldStopLossByCode[oldItem.Code] = oldItem.StopLossPrice;
                    }

                    _balance.Clear();

                    foreach (HoldingStock item in newBalances)
                    {
                        if (oldStopLossByCode.TryGetValue(item.Code, out long oldStopLoss) && oldStopLoss > 0)
                            item.StopLossPrice = oldStopLoss;

                        _balance.Add(item);
                    }

                    AccountSetSlotsText();
                    CollectionViewSource.GetDefaultView(_balance)?.Refresh();
                    RestoreGridSelection(GridBalance, selectedCode, selectedIndex, restoreKeyboardFocus);
                    AccountUpdateTopAccountSummaryFromBalance();
                });

                Log($"✅ [계좌동기화] kt00005 잔고 반영 완료 / 보유 {_balance.Count}종목 / 원본 {allRows.Count}행");
            }
            catch (Exception ex)
            {
                Log($"❌ [계좌동기화 오류] {ex.Message}");
            }
        }

        private async Task<List<AccountKt00005Row>> RequestKt00005RowsAllPagesAsync(string market)
        {
            List<AccountKt00005Row> rows = [];
            string contYn = "N";
            string nextKey = "";
            int page = 0;

            do
            {
                page++;

                AccountKt00005Page pageResult = await RequestKt00005PageAsync(market, contYn, nextKey, page);

                if (pageResult.Rows != null && pageResult.Rows.Count > 0)
                    rows.AddRange(pageResult.Rows);

                contYn = string.Equals(pageResult.ContYn, "Y", StringComparison.OrdinalIgnoreCase) ? "Y" : "N";
                nextKey = pageResult.NextKey ?? "";

                if (contYn == "Y" && string.IsNullOrWhiteSpace(nextKey))
                {
                    Log($"⚠️ [kt00005] {market} 연속조회 cont-yn=Y 이지만 next-key가 비어 있어 중단");
                    break;
                }

                if (page >= 30)
                {
                    Log($"⚠️ [kt00005] {market} 연속조회 30페이지 도달로 중단");
                    break;
                }
            }
            while (contYn == "Y");

            Log($"📌 [kt00005] {market} 잔고 원본 {rows.Count}행 수집 완료");
            return rows;
        }

        private async Task<AccountKt00005Page> RequestKt00005PageAsync(string market, string contYn, string nextKey, int page)
        {
            JObject requestBody = [];

            if (!string.IsNullOrWhiteSpace(market))
                requestBody["dmst_stex_tp"] = market;

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.kiwoom.com/api/dostk/acnt");
            request.Headers.TryAddWithoutValidation("authorization", $"Bearer {_token}");
            request.Headers.TryAddWithoutValidation("api-id", "kt00005");
            request.Headers.TryAddWithoutValidation("cont-yn", string.Equals(contYn, "Y", StringComparison.OrdinalIgnoreCase) ? "Y" : "N");
            request.Headers.TryAddWithoutValidation("next-key", nextKey ?? "");
            request.Content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await _http.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();

            await SaveRawAsync($"ACCOUNT_kt00005_{market}_{page}", body);

            if (!response.IsSuccessStatusCode)
            {
                Log($"❌ [kt00005 실패] {market} / page={page} / HTTP {(int)response.StatusCode} / {response.ReasonPhrase}");
                Log($"❌ [kt00005 응답] {body}");
                return new AccountKt00005Page();
            }

            JObject root = JObject.Parse(body);
            JArray arr = AccountGetArrayAny(root, "stk_cntr_remn", "stk_cntr_remn_array", "items", "data", "list", "output");

            List<AccountKt00005Row> rows = [];

            if (arr != null)
            {
                foreach (JToken token in arr)
                {
                    AccountKt00005Row row = ParseKt00005Row(token, market);

                    if (!string.IsNullOrWhiteSpace(row.Code) && row.Qty > 0)
                        rows.Add(row);
                }
            }

            string responseContYn = AccountGetHeaderValue(response, "cont-yn");
            string responseNextKey = AccountGetHeaderValue(response, "next-key");

            if (string.IsNullOrWhiteSpace(responseContYn))
                responseContYn = AccountGetStringAny(root, "cont-yn", "cont_yn", "contYn");

            if (string.IsNullOrWhiteSpace(responseNextKey))
                responseNextKey = AccountGetStringAny(root, "next-key", "next_key", "nextKey");

            Log($"📥 [kt00005] {market} page={page} / rows={rows.Count} / cont-yn={responseContYn} / next-key={(string.IsNullOrWhiteSpace(responseNextKey) ? "-" : "있음")}");

            return new AccountKt00005Page
            {
                Rows = rows,
                ContYn = responseContYn,
                NextKey = responseNextKey
            };
        }

        private AccountKt00005Row ParseKt00005Row(JToken token, string requestMarket)
        {
            string rawMarket = AccountGetStringAny(token, "stex_tp", "dmst_stex_tp", "market", "거래소구분");
            string market = string.IsNullOrWhiteSpace(rawMarket)
                ? "통합"
                : AccountNormalizeMarket(rawMarket);

            string rawCode = AccountGetStringAny(token, "stk_cd", "pdno", "code", "item", "isu_cd", "종목코드");
            string code = AccountNormalizeCode(rawCode);

            long qty = Math.Abs(AccountGetSignedLongAny(token, "cur_qty", "hldg_qty", "rmnd_qty", "poss_qty", "qty", "현재잔고", "보유수량"));
            long currentPrice = Math.Abs(AccountGetSignedLongAny(token, "cur_prc", "now_pric", "prpr", "current_price", "현재가"));
            long buyUnitPrice = Math.Abs(AccountGetSignedLongAny(token, "buy_uv", "pur_pric", "pchs_avg_pric", "avg_prc", "buy_price", "매입단가", "매입가"));
            long buyAmount = Math.Abs(AccountGetSignedLongAny(token, "pur_amt", "pchs_amt", "buy_amt", "매입금액"));
            long evalAmount = Math.Abs(AccountGetSignedLongAny(token, "evlt_amt", "evlu_amt", "eval_amt", "평가금액"));
            long profitAmount = AccountGetSignedLongAny(token, "evltv_prft", "evlt_prft", "evlu_pfls_amt", "profit_amt", "평가손익");
            double? apiProfitRate = AccountGetNullableDoubleAny(token, "pl_rt", "evlt_prft_rt", "evlu_pfls_rt", "prft_rt", "profit_rate", "손익률", "수익률");

            if (buyAmount <= 0 && buyUnitPrice > 0 && qty > 0)
                buyAmount = buyUnitPrice * qty;

            if (evalAmount <= 0 && currentPrice > 0 && qty > 0)
                evalAmount = currentPrice * qty;

            if (profitAmount == 0 && buyAmount > 0 && evalAmount > 0)
                profitAmount = evalAmount - buyAmount;

            AccountCostRates rates = AccountGetCostRates(market);

            long estimatedBuyCommission = AccountEstimateCost(buyAmount, rates.CommissionRate);
            long estimatedSellCommission = AccountEstimateCost(evalAmount, rates.CommissionRate);
            long estimatedSellTax = AccountEstimateCost(evalAmount, rates.SellTaxRate);

            return new AccountKt00005Row
            {
                Market = market,
                Code = code,
                Name = AccountGetStringAny(token, "stk_nm", "prdt_name", "name", "item_name", "isu_nm", "종목명"),
                Qty = qty,
                CurrentPrice = currentPrice,
                BuyUnitPrice = buyUnitPrice,
                BuyAmount = buyAmount,
                EvalAmount = evalAmount,
                ProfitAmount = profitAmount,
                ApiProfitRate = apiProfitRate,
                EstimatedBuyCommission = estimatedBuyCommission,
                EstimatedSellCommission = estimatedSellCommission,
                EstimatedSellTax = estimatedSellTax
            };
        }

        private List<AccountKt00005AggregatedHolding> BuildKt00005AggregatedHoldings(List<AccountKt00005Row> rows)
        {
            List<AccountKt00005AggregatedHolding> result = [];

            if (rows == null || rows.Count == 0)
                return result;

            foreach (IGrouping<string, AccountKt00005Row> group in rows.GroupBy(x => x.Code).OrderBy(x => x.Key))
            {
                List<AccountKt00005Row> sourceRows = [.. group.Where(x => x.Qty > 0)];

                if (sourceRows.Count == 0)
                    continue;

                long totalQty = sourceRows.Sum(x => x.Qty);
                long totalBuyAmount = sourceRows.Sum(x => x.BuyAmount);
                long totalEvalAmount = sourceRows.Sum(x => x.EvalAmount);
                long totalProfitAmount = sourceRows.Sum(x => x.ProfitAmount);

                if (totalQty <= 0)
                    continue;

                long avgBuyPrice = totalBuyAmount > 0
                    ? (long)Math.Round(totalBuyAmount / (double)totalQty, MidpointRounding.AwayFromZero)
                    : sourceRows.FirstOrDefault(x => x.BuyUnitPrice > 0)?.BuyUnitPrice ?? 0;

                long currentPriceForDisplay = sourceRows.Count == 1
                    ? sourceRows[0].CurrentPrice
                    : totalEvalAmount > 0
                        ? (long)Math.Round(totalEvalAmount / (double)totalQty, MidpointRounding.AwayFromZero)
                        : sourceRows.FirstOrDefault(x => x.CurrentPrice > 0)?.CurrentPrice ?? 0;

                double? summedProfitRate = totalBuyAmount > 0
                    ? Math.Round(totalProfitAmount / (double)totalBuyAmount * 100.0, 4, MidpointRounding.AwayFromZero)
                    : null;

                double displayProfitRate;
                string displayRateSource;

                if (sourceRows.Count == 1 && sourceRows[0].ApiProfitRate.HasValue)
                {
                    displayProfitRate = sourceRows[0].ApiProfitRate.Value;
                    displayRateSource = "kt00005.pl_rt";
                }
                else if (sourceRows.Count > 1 && summedProfitRate.HasValue)
                {
                    displayProfitRate = summedProfitRate.Value;
                    displayRateSource = "종목단위합산";
                }
                else if (sourceRows.FirstOrDefault(x => x.ApiProfitRate.HasValue)?.ApiProfitRate is double apiRate)
                {
                    displayProfitRate = apiRate;
                    displayRateSource = "kt00005.pl_rt fallback";
                }
                else
                {
                    displayProfitRate = summedProfitRate ?? 0;
                    displayRateSource = "evltv_prft/pur_amt";
                }

                string name = sourceRows.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Name))?.Name ?? group.Key;
                string markets = string.Join("+", sourceRows.Select(x => x.Market).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());

                AccountKt00005AggregatedHolding item = new()
                {
                    Code = group.Key,
                    Name = name,
                    Markets = markets,
                    TotalQty = totalQty,
                    TotalBuyAmount = totalBuyAmount,
                    TotalEvalAmount = totalEvalAmount,
                    TotalProfitAmount = totalProfitAmount,
                    AvgBuyPrice = avgBuyPrice,
                    CurrentPriceForDisplay = currentPriceForDisplay,
                    SummedProfitRate = summedProfitRate,
                    DisplayProfitRate = displayProfitRate,
                    DisplayRateSource = displayRateSource,
                    EstimatedBuyCommission = sourceRows.Sum(x => x.EstimatedBuyCommission),
                    EstimatedSellCommission = sourceRows.Sum(x => x.EstimatedSellCommission),
                    EstimatedSellTax = sourceRows.Sum(x => x.EstimatedSellTax),
                    SourceRows = sourceRows
                };

                result.Add(item);

                Log($"📊 [잔고집계] {item.Name}({item.Code}) / 시장={item.Markets} / 수량={item.TotalQty:N0} / 매입={item.TotalBuyAmount:N0} / 평가={item.TotalEvalAmount:N0} / 손익={item.TotalProfitAmount:N0} / 수익률={item.DisplayProfitRate:F4}% / 기준={item.DisplayRateSource}");
            }

            return result;
        }

        private List<HoldingStock> BuildHoldingStocksFromKt00005(List<AccountKt00005AggregatedHolding> aggregated)
        {
            List<HoldingStock> result = [];

            foreach (AccountKt00005AggregatedHolding item in aggregated.OrderByDescending(x => x.TotalEvalAmount))
            {
                HoldingStock holding = new()
                {
                    Code = item.Code,
                    Name = item.Name,
                    BuyPrice = item.AvgBuyPrice,
                    CurrentPrice = item.CurrentPriceForDisplay,
                    Volume = item.TotalQty,
                    VolumeText = $"{item.TotalQty:N0}주",
                    StopLossPrice = item.AvgBuyPrice > 0 ? (long)Math.Round(item.AvgBuyPrice * 0.97, MidpointRounding.AwayFromZero) : 0,
                    ProfitRateText = AccountFormatRate(item.DisplayProfitRate),
                    ProfitColor = AccountGetRateColor(item.DisplayProfitRate),
                    PriceColor = AccountGetRateColor(item.DisplayProfitRate),
                    TradingValueText = $"평가 {item.TotalEvalAmount:N0}",
                    TurnoverRateText = item.Markets
                };

                result.Add(holding);
            }

            return result;
        }

        private void AccountUpdateTopAccountSummaryFromBalance()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(AccountUpdateTopAccountSummaryFromBalance);
                return;
            }

            long displayBuy = _serverTotalBuyAmount;
            long displayAsset = _serverTotalEvalAmount;
            long displayProfitAmount = _serverTotalProfitAmount;
            double profitRate = _serverTotalProfitRate ?? 0;

            if (displayBuy <= 0 || displayAsset <= 0)
            {
                displayBuy = 0;
                displayAsset = 0;

                foreach (HoldingStock item in _balance)
                {
                    if (item == null || item.Volume <= 0)
                        continue;

                    long buyAmount = item.BuyPrice > 0 ? item.BuyPrice * item.Volume : 0;
                    long evalPrice = item.CurrentPrice > 0 ? item.CurrentPrice : item.BuyPrice;
                    long evalAmount = evalPrice > 0 ? evalPrice * item.Volume : 0;

                    displayBuy += buyAmount;
                    displayAsset += evalAmount;
                }

                displayProfitAmount = displayAsset - displayBuy;
                profitRate = displayBuy > 0
                    ? Math.Round(displayProfitAmount / (double)displayBuy * 100.0, 2, MidpointRounding.AwayFromZero)
                    : 0;
            }

            AccountSetTextBlock("TxtTotalAsset", $"₩ {displayAsset:N0}");
            AccountSetTextBlock("TxtTotalBuy", $"매입 ₩ {displayBuy:N0}");
            AccountSetTextBlock("TxtProfitRate", AccountFormatRate(profitRate));
            AccountSetTextBlock("TxtProfitAmt", displayProfitAmount >= 0
                ? $"+₩ {displayProfitAmount:N0}"
                : $"-₩ {Math.Abs(displayProfitAmount):N0}");

            AccountSetTextBlock("TxtRealizedProfit", _serverRealizedProfitAmount >= 0
                ? $"+₩ {_serverRealizedProfitAmount:N0}"
                : $"-₩ {Math.Abs(_serverRealizedProfitAmount):N0}");

            long estimatedAsset = _serverEstimatedAssetAmount > 0 ? _serverEstimatedAssetAmount : displayAsset;
            AccountSetTextBlock("TxtEstimatedAsset", $"추정자산 ₩ {estimatedAsset:N0}");

            Brush color = AccountGetRateColor(profitRate);
            AccountSetTextBlockBrush("TxtProfitRate", color);
            AccountSetTextBlockBrush("TxtProfitAmt", color);
            AccountSetTextBlockBrush("TxtRealizedProfit", AccountGetRateColor(_serverRealizedProfitAmount));
        }

        private void AccountSetSlotsText()
        {
            string maxSlotsText = "3";

            if (FindName("InputMaxSlots") is TextBox inputMaxSlots && !string.IsNullOrWhiteSpace(inputMaxSlots.Text))
                maxSlotsText = inputMaxSlots.Text;

            AccountSetTextBlock("TxtSlots", $"{_balance.Count} / {maxSlotsText}");
        }

        private AccountCostRates AccountGetCostRates(string market)
        {
            string normalized = AccountNormalizeMarket(market);

            AccountCostRates defaultRates = normalized == "NXT"
                ? new AccountCostRates { CommissionRate = 0.000145, SellTaxRate = 0.0020 }
                : new AccountCostRates { CommissionRate = 0.00015, SellTaxRate = 0.0020 };

            try
            {
                JObject config = AccountLoadConfigJson();

                if (config == null)
                    return defaultRates;

                JObject costRates = config["CostRates"] as JObject;

                if (costRates == null)
                    return defaultRates;

                JObject marketNode = costRates[normalized] as JObject
                    ?? costRates["Default"] as JObject
                    ?? costRates["DEFAULT"] as JObject;

                if (marketNode == null)
                    return defaultRates;

                double commissionRate = AccountGetNullableDoubleAny(marketNode, "CommissionRate", "commission_rate", "수수료율") ?? defaultRates.CommissionRate;
                double sellTaxRate = AccountGetNullableDoubleAny(marketNode, "SellTaxRate", "sell_tax_rate", "매도세율", "거래세율") ?? defaultRates.SellTaxRate;

                return new AccountCostRates
                {
                    CommissionRate = commissionRate,
                    SellTaxRate = sellTaxRate
                };
            }
            catch
            {
                return defaultRates;
            }
        }

        private JObject AccountLoadConfigJson()
        {
            string[] paths =
            [
                Path.Combine(AppContext.BaseDirectory, "config.json"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KHStrategyLab", "config.json"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KHStrategyLab", "config.json")
            ];

            foreach (string path in paths)
            {
                if (!File.Exists(path))
                    continue;

                string text = File.ReadAllText(path, Encoding.UTF8);

                if (!string.IsNullOrWhiteSpace(text))
                    return JObject.Parse(text);
            }

            return null;
        }

        private long AccountEstimateCost(long amount, double rate)
        {
            if (amount <= 0 || rate <= 0)
                return 0;

            return (long)Math.Ceiling(amount * rate);
        }

        private string AccountNormalizeMarket(string market)
        {
            if (string.IsNullOrWhiteSpace(market))
                return "KRX";

            string value = market.Trim().ToUpperInvariant();

            if (value.Contains("통합") || value.Contains("UNKNOWN") || value.Contains("미확인"))
                return "통합";

            if (value.Contains("NXT"))
                return "NXT";

            if (value.Contains("KRX"))
                return "KRX";

            if (value.Contains("통합") || value.Contains("SOR") || value == "%" || value == "ALL")
                return "KRX";

            return value;
        }

        private string AccountNormalizeCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return "";

            string normalized = code.Trim().ToUpperInvariant();
            normalized = normalized.Replace("_NX", "").Replace("_AL", "");

            if (normalized.StartsWith("A") && normalized.Length > 1 && char.IsDigit(normalized[1]))
                normalized = normalized.Substring(1);

            string digits = new([.. normalized.Where(char.IsDigit)]);

            if (digits.Length >= 6)
                return digits.Substring(digits.Length - 6);

            return digits;
        }

        private string AccountMaskAccount(string account)
        {
            if (string.IsNullOrWhiteSpace(account) || account.Length < 6)
                return "****";

            return account.Substring(0, 3) + "****" + account.Substring(account.Length - 2);
        }

        private string AccountFormatRate(double rate)
        {
            return $"{(rate > 0 ? "+" : "")}{rate:F2}%";
        }

        private Brush AccountGetRateColor(double rate)
        {
            try
            {
                if (rate > 0)
                    return (Brush)FindResource("BrandUp");

                if (rate < 0)
                    return (Brush)FindResource("BrandDown");
            }
            catch
            {
            }

            return Brushes.White;
        }

        private void AccountSetTextBlock(string name, string text)
        {
            void Apply()
            {
                if (FindName(name) is TextBlock textBlock)
                    textBlock.Text = text;
            }

            if (Dispatcher.CheckAccess())
                Apply();
            else
                Dispatcher.Invoke(Apply);
        }

        private void AccountSetTextBlockBrush(string name, Brush brush)
        {
            void Apply()
            {
                if (FindName(name) is TextBlock textBlock)
                    textBlock.Foreground = brush;
            }

            if (Dispatcher.CheckAccess())
                Apply();
            else
                Dispatcher.Invoke(Apply);
        }

        private string AccountGetHeaderValue(HttpResponseMessage response, string name)
        {
            if (response == null || string.IsNullOrWhiteSpace(name))
                return "";

            if (response.Headers.TryGetValues(name, out IEnumerable<string> values))
                return values.FirstOrDefault() ?? "";

            if (response.Content?.Headers != null && response.Content.Headers.TryGetValues(name, out IEnumerable<string> contentValues))
                return contentValues.FirstOrDefault() ?? "";

            return "";
        }

        private JArray AccountGetArrayAny(JObject obj, params string[] names)
        {
            if (obj == null)
                return null;

            foreach (string name in names)
            {
                JToken value = obj[name];

                if (value == null)
                {
                    JProperty prop = obj.Properties().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                    value = prop?.Value;
                }

                if (value is JArray arr)
                    return arr;

                if (value is JObject childObj)
                {
                    JArray childArray = childObj.Properties().Select(p => p.Value).OfType<JArray>().FirstOrDefault();

                    if (childArray != null)
                        return childArray;
                }
            }

            foreach (JProperty prop in obj.Properties())
            {
                if (prop.Value is JArray arr)
                    return arr;

                if (prop.Value is JObject child)
                {
                    JArray childArray = child.Properties().Select(p => p.Value).OfType<JArray>().FirstOrDefault();

                    if (childArray != null)
                        return childArray;
                }
            }

            return null;
        }

        private string AccountGetStringAny(JToken token, params string[] names)
        {
            if (token == null)
                return "";

            if (token is JObject obj)
            {
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
            }

            return "";
        }

        private string AccountFindFirstStringRecursive(JToken token, params string[] names)
        {
            if (token == null)
                return "";

            if (token is JObject obj)
            {
                string direct = AccountGetStringAny(obj, names);

                if (!string.IsNullOrWhiteSpace(direct))
                    return direct;

                foreach (JProperty prop in obj.Properties())
                {
                    string found = AccountFindFirstStringRecursive(prop.Value, names);

                    if (!string.IsNullOrWhiteSpace(found))
                        return found;
                }
            }
            else if (token is JArray arr)
            {
                foreach (JToken child in arr)
                {
                    string found = AccountFindFirstStringRecursive(child, names);

                    if (!string.IsNullOrWhiteSpace(found))
                        return found;
                }
            }

            return "";
        }

        private long AccountGetSignedLongAny(JToken token, params string[] names)
        {
            string text = AccountGetStringAny(token, names);
            return AccountParseSignedLong(text);
        }

        private double? AccountGetNullableDoubleAny(JToken token, params string[] names)
        {
            string text = AccountGetStringAny(token, names);

            if (string.IsNullOrWhiteSpace(text))
                return null;

            if (AccountTryParseDouble(text, out double value))
                return value;

            return null;
        }

        private long AccountParseSignedLong(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;

            string clean = value.Trim()
                .Replace(",", "")
                .Replace("원", "")
                .Replace("주", "")
                .Replace("%", "")
                .Replace("+", "");

            if (long.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out long result))
                return result;

            if (decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal decimalResult))
                return (long)Math.Round(decimalResult, MidpointRounding.AwayFromZero);

            return 0;
        }

        private bool AccountTryParseDouble(string value, out double result)
        {
            result = 0;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            string clean = value.Trim()
                .Replace(",", "")
                .Replace("원", "")
                .Replace("%", "")
                .Replace("+", "");

            if (double.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out result))
                return true;

            if (double.TryParse(clean, NumberStyles.Any, CultureInfo.CurrentCulture, out result))
                return true;

            result = 0;
            return false;
        }

        private sealed class AccountKt00005Page
        {
            public List<AccountKt00005Row> Rows { get; set; } = [];
            public string ContYn { get; set; } = "N";
            public string NextKey { get; set; } = "";
        }

        private sealed class AccountKt00005Row
        {
            public string Market { get; set; } = "";
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
            public long Qty { get; set; }
            public long CurrentPrice { get; set; }
            public long BuyUnitPrice { get; set; }
            public long BuyAmount { get; set; }
            public long EvalAmount { get; set; }
            public long ProfitAmount { get; set; }
            public double? ApiProfitRate { get; set; }
            public long EstimatedBuyCommission { get; set; }
            public long EstimatedSellCommission { get; set; }
            public long EstimatedSellTax { get; set; }
        }

        private sealed class AccountKt00005AggregatedHolding
        {
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
            public string Markets { get; set; } = "";
            public long TotalQty { get; set; }
            public long TotalBuyAmount { get; set; }
            public long TotalEvalAmount { get; set; }
            public long TotalProfitAmount { get; set; }
            public long AvgBuyPrice { get; set; }
            public long CurrentPriceForDisplay { get; set; }
            public double? SummedProfitRate { get; set; }
            public double DisplayProfitRate { get; set; }
            public string DisplayRateSource { get; set; } = "";
            public long EstimatedBuyCommission { get; set; }
            public long EstimatedSellCommission { get; set; }
            public long EstimatedSellTax { get; set; }
            public List<AccountKt00005Row> SourceRows { get; set; } = [];
        }

        private sealed class AccountCostRates
        {
            public double CommissionRate { get; set; }
            public double SellTaxRate { get; set; }
        }
    }
}
