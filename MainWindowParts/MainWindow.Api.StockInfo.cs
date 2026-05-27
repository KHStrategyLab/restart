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

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private static readonly HttpClient _stockInfoHttp = new();
        private readonly object _stockInfoRequestLock = new();
        private readonly Dictionary<string, DateTime> _stockInfoRequestTimes = [];

        private async Task RefreshStockInfoAsync(string code, string preferredMarket = "")
        {
            try
            {
                code = NormalizeStockCode(code);
                if (string.IsNullOrWhiteSpace(code)) return;

                if (string.IsNullOrWhiteSpace(_token))
                {
                    Log($"?좑툘 [醫낅ぉ?뺣낫] ?좏겙 ?놁쓬: {code}");
                    return;
                }

                lock (_stockInfoRequestLock)
                {
                    if (_stockInfoRequestTimes.TryGetValue(code, out DateTime lastTime))
                    {
                        if ((DateTime.Now - lastTime).TotalSeconds < 10) return;
                    }

                    _stockInfoRequestTimes[code] = DateTime.Now;
                }

                string market = NormalizeRequestedMarket(preferredMarket);

                List<(string RequestCode, string Market)> requestPlan = BuildStockInfoRequestPlan(code, market);
                if (requestPlan.Count == 0) return;

                StockInfoSnapshot snapshot = null;
                foreach ((string requestCode, string requestMarket) in requestPlan)
                {
                    string url = "https://api.kiwoom.com/api/dostk/stkinfo";
                    var body = new { stk_cd = requestCode };
                    string bodyJson = JsonConvert.SerializeObject(body);

                    using var request = new HttpRequestMessage(HttpMethod.Post, url);
                    request.Headers.TryAddWithoutValidation("authorization", $"Bearer {_token}");
                    request.Headers.TryAddWithoutValidation("api-id", "ka10001");
                    request.Headers.TryAddWithoutValidation("cont-yn", "N");
                    request.Headers.TryAddWithoutValidation("next-key", "");
                    request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

                    Log($"🔎 [종목정보] 조회 요청: {code} / 요청코드={requestCode} / 시장={requestMarket}");

                    using HttpResponseMessage response = await _stockInfoHttp.SendAsync(request);
                    string text = await response.Content.ReadAsStringAsync();
                    _ = SaveRawAsync($"stock_info_{requestCode}", text);

                    JObject json;
                    try
                    {
                        json = JObject.Parse(text);
                    }
                    catch
                    {
                        Log($"⚠️ [종목정보] JSON 파싱 실패: {code} / 요청코드={requestCode}");
                        continue;
                    }

                    string returnCode = json["return_code"]?.ToString() ?? "";
                    string returnMsg = json["return_msg"]?.ToString() ?? "";

                    if (!response.IsSuccessStatusCode)
                    {
                        Log($"⚠️ [종목정보] HTTP 오류: {code} / 요청코드={requestCode} / {(int)response.StatusCode} / {response.ReasonPhrase}");
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(returnCode) && returnCode != "0")
                    {
                        Log($"⚠️ [종목정보] 응답 오류: {code} / 요청코드={requestCode} / code={returnCode} / msg={returnMsg}");
                        continue;
                    }

                    snapshot = ParseStockInfoSnapshot(json, code);
                    if (snapshot == null) continue;
                    snapshot.RequestCode = requestCode;
                    snapshot.Market = requestMarket;
                    break;
                }

                if (snapshot == null)
                {
                    Log($"⚠️ [종목정보] 조회 실패: {code} / 요청시장={(string.IsNullOrWhiteSpace(market) ? "AUTO" : market)}");
                    return;
                }

                ApplyStockInfoToGrid(snapshot);
            }
            catch (Exception ex)
            {
                Log($"??[醫낅ぉ?뺣낫 ?ㅻ쪟] {code} / {ex.Message}");
            }
        }

        private List<(string RequestCode, string Market)> BuildStockInfoRequestPlan(string code, string market)
        {
            var result = new List<(string RequestCode, string Market)>();
            string normalizedCode = NormalizeStockCode(code);
            if (string.IsNullOrWhiteSpace(normalizedCode)) return result;

            string normalizedMarket = NormalizeRequestedMarket(market);
            if (normalizedMarket == "NXT")
            {
                result.Add(($"{normalizedCode}_AL", "NXT"));
                result.Add(($"{normalizedCode}_NX", "NXT"));
                return result;
            }

            result.Add((normalizedCode, "KRX"));
            return result;
        }

        private string NormalizeRequestedMarket(string market)
        {
            string normalized = (market ?? "").Trim().ToUpperInvariant();
            return normalized == "NXT" ? "NXT" : normalized == "KRX" ? "KRX" : "";
        }

        private StockInfoSnapshot ParseStockInfoSnapshot(JObject json, string fallbackCode)
        {
            string code = ReadJsonValue(json, "stk_cd", "stkCd", "code");
            string name = ReadJsonValue(
                json,
                "stk_nm",
                "stkNm",
                "name",
                "stk_name",
                "hname",
                "item_name",
                "nm",
                "kor_name");
            string priceText = ReadJsonValue(json, "cur_prc", "curPrc", "price", "now_prc");
            string volumeText = ReadJsonValue(json, "trde_qty", "trdeQty", "acc_trde_qty", "accTrdeQty", "acml_vol", "acmlVol", "volume", "거래량");
            string tradingValueText = ReadJsonValue(json, "trde_prica", "trde_amt", "acc_trde_prica", "acc_trde_amt", "accTrdeAmt", "acml_tr_pbmn", "trading_value", "거래대금");
            string changeRateText = ReadJsonValue(json, "flu_rt", "fluRt", "chg_rt", "change_rate");
            string turnoverRateText = ReadJsonValue(json, "turnover_rt", "trde_rt", "turnoverRate", "회전율");
            string listedSharesText = ReadJsonValue(json,
                "flo_stk",
                "floStk",
                "lst_stk_cnt",
                "lstStkCnt",
                "list_stkcnt",
                "listed_shares",
                "listedShares",
                "stk_cnt",
                "stkCnt",
                "상장주식",
                "상장주식수",
                "유통주식");

            code = NormalizeStockCode(string.IsNullOrWhiteSpace(code) ? fallbackCode : code);

            long currentPrice = ParseLongSafe(priceText);
            long volume = ParseLongSafe(volumeText);
            long tradingValue = ParseLongSafe(tradingValueText);
            long listedSharesRaw = ParseLongSafe(listedSharesText);
            bool isEstimatedTradingValue = false;

            if (tradingValue <= 0 && currentPrice > 0 && volume > 0)
            {
                tradingValue = currentPrice * volume;
                isEstimatedTradingValue = true;
            }

            turnoverRateText = NormalizeStockInfoTurnoverRateText(turnoverRateText, volume, listedSharesRaw);

            if (!string.IsNullOrWhiteSpace(changeRateText) && !changeRateText.Contains("%"))
                changeRateText = $"{changeRateText}%";
            double changeRatePercent = ParsePercentOrNumber(changeRateText);
            double turnoverRatePercent = ParsePercentOrNumber(turnoverRateText);

            return new StockInfoSnapshot
            {
                Code = code,
                Name = string.IsNullOrWhiteSpace(name) ? "醫낅ぉ紐낆“?뚯쨷" : name.Trim(),
                CurrentPrice = currentPrice,
                Volume = volume,
                TradingValue = tradingValue,
                ChangeRateText = string.IsNullOrWhiteSpace(changeRateText) ? "-" : changeRateText.Trim(),
                ChangeRatePercent = changeRatePercent,
                TurnoverRateText = string.IsNullOrWhiteSpace(turnoverRateText) ? "-" : turnoverRateText,
                TurnoverRatePercent = turnoverRatePercent,
                IsEstimatedTradingValue = isEstimatedTradingValue
            };
        }

        private string NormalizeStockInfoTurnoverRateText(string turnoverRateText, long volume, long listedSharesRaw)
        {
            turnoverRateText = (turnoverRateText ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(turnoverRateText))
            {
                if (turnoverRateText.Contains("%"))
                    return turnoverRateText;

                double rate = ParsePercentOrNumber(turnoverRateText);
                if (Math.Abs(rate) > 0)
                    return $"{rate:0.00}%";
            }

            if (volume <= 0 || listedSharesRaw <= 0)
                return "";

            long listedShares = listedSharesRaw;

            // ka10001의 flo_stk/상장주식 값은 보통 천 주 단위로 온다.
            if (listedShares < 100_000_000)
                listedShares *= 1000;

            if (listedShares <= 0)
                return "";

            decimal turnoverRate = ((decimal)volume / listedShares) * 100m;
            return $"{turnoverRate:0.00}%";
        }

        private double ParsePercentOrNumber(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            string clean = text.Replace("%", "").Replace(",", "").Trim();
            return double.TryParse(clean, out double value) ? value : 0;
        }

        private string ReadJsonValue(JObject json, params string[] keys)
        {
            foreach (string key in keys)
            {
                string value = json[key]?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }

            return "";
        }

        private void ApplyStockInfoToGrid(StockInfoSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Code)) return;

            Dispatcher.Invoke(() =>
            {
                string code = NormalizeStockCode(snapshot.Code);
                HoldingStock searchStock = _search00List.FirstOrDefault(x => NormalizeStockCode(x.Code) == code);
                RankStock rankStock = _rankList.FirstOrDefault(x => NormalizeStockCode(x.Code) == code);
                HoldingStock balanceStock = _balance.FirstOrDefault(x => NormalizeStockCode(x.Code) == code);
                bool applied = false;

                if (searchStock != null)
                {
                    ApplyStockInfoToRow(searchStock, snapshot, keepVolumeText: false, keepProfitRateText: false);
                    applied = true;
                }

                if (rankStock != null)
                {
                    ApplyStockInfoToRow(rankStock, snapshot, keepVolumeText: true, keepProfitRateText: false);
                    applied = true;
                }

                if (balanceStock != null)
                {
                    ApplyStockInfoToRow(balanceStock, snapshot, keepVolumeText: true, keepProfitRateText: true);
                    applied = true;
                }

                bool candidateUpdated = false;
                string fallbackName = ResolveFallbackStockName(code);
                if (_watchCandidates.TryGetValue(code, out WatchCandidate candidate))
                {
                    if (IsUsableResolvedName(snapshot.Name, code))
                        candidate.Name = snapshot.Name;
                    else if (IsUsableResolvedName(fallbackName, code))
                        candidate.Name = fallbackName;
                    if (snapshot.CurrentPrice > 0) candidate.LastPrice = snapshot.CurrentPrice;
                    candidate.StockInfoChangeRatePercent = snapshot.ChangeRatePercent;
                    candidate.StockInfoTurnoverRatePercent = snapshot.TurnoverRatePercent > 0 ? snapshot.TurnoverRatePercent : null;
                    candidate.StockInfoMarket = snapshot.Market;
                    candidate.StockInfoRequestCode = snapshot.RequestCode;
                    candidate.StockInfoCapturedAt = DateTime.Now;
                    candidate.LastSeen = DateTime.Now;
                    candidateUpdated = true;
                }

                if (!applied && !candidateUpdated) return;

                SaveWatchCandidates();
                if (applied)
                {
                    RefreshRealtimeRankWaitingGridFromStockInfo();
                }

                if (!_lastDailyChartIsIndex && NormalizeStockCode(_lastDailyChartCode) == code)
                    SetTextBlock("TxtChartFooterTurnover", ResolveChartTurnoverText(code));

                string displayName = IsUsableResolvedName(snapshot.Name, code)
                    ? snapshot.Name
                    : (IsUsableResolvedName(fallbackName, code) ? fallbackName : snapshot.Name);
                if (!IsUsableResolvedName(snapshot.Name, code))
                    Log($"⚠️ [종목정보] 종목명 미확정 응답: {code} / 요청코드={snapshot.RequestCode} / fallback={(IsUsableResolvedName(fallbackName, code) ? "USED" : "NONE")}");

                Log($"✅ [종목정보] 반영 완료: {displayName}({snapshot.Code}) / 시장={snapshot.Market} / 요청코드={snapshot.RequestCode} / 현재가 {snapshot.CurrentPrice:N0} / 거래량 {snapshot.Volume:N0} / 거래대금 {FormatStockInfoTradingValue(snapshot)} / 등락률 {snapshot.ChangeRateText} / 회전율 {snapshot.TurnoverRateText}");

            });
        }

        private bool IsUsableResolvedName(string name, string code)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string trimmed = name.Trim();
            if (trimmed == "-" || trimmed == "N/A") return false;
            if (NormalizeStockCode(trimmed) == NormalizeStockCode(code)) return false;
            if (trimmed.Length <= 1) return false;
            if (trimmed.Contains("조회", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("로딩", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("대기", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private string ResolveFallbackStockName(string code)
        {
            string normalized = NormalizeStockCode(code);
            if (string.IsNullOrWhiteSpace(normalized)) return "";

            StockGridRow row = _search00List.Cast<StockGridRow>()
                .Concat(_rankList.Cast<StockGridRow>())
                .Concat(_balance.Cast<StockGridRow>())
                .FirstOrDefault(x => NormalizeStockCode(x.Code) == normalized);

            return row?.Name ?? "";
        }

        private void ApplyStockInfoToRow(StockGridRow row, StockInfoSnapshot snapshot, bool keepVolumeText, bool keepProfitRateText)
        {
            if (row == null || snapshot == null) return;

            if (IsUsableResolvedName(snapshot.Name, row.Code))
                row.Name = snapshot.Name;

            if (snapshot.CurrentPrice > 0)
                row.CurrentPrice = snapshot.CurrentPrice;

            if (!keepVolumeText && snapshot.Volume > 0)
                row.VolumeText = snapshot.Volume > 0 ? snapshot.Volume.ToString("N0") : "-";

            string tradingValueText = FormatStockInfoTradingValue(snapshot);
            if (!string.IsNullOrWhiteSpace(tradingValueText) && tradingValueText != "-")
                row.TradingValueText = tradingValueText;

            if (!string.IsNullOrWhiteSpace(snapshot.ChangeRateText) && snapshot.ChangeRateText != "-")
            {
                row.ChangeRateText = snapshot.ChangeRateText;
                row.PriceColor = ResolveRateBrush(snapshot.ChangeRateText);
            }

            if (!string.IsNullOrWhiteSpace(snapshot.TurnoverRateText) && snapshot.TurnoverRateText != "-")
                row.TurnoverRateText = snapshot.TurnoverRateText;

            if (!keepProfitRateText && !string.IsNullOrWhiteSpace(snapshot.ChangeRateText) && snapshot.ChangeRateText != "-")
            {
                row.ProfitRateText = snapshot.ChangeRateText;
                row.ProfitColor = ResolveRateBrush(snapshot.ChangeRateText);
            }
        }

        private string FormatStockInfoTradingValue(StockInfoSnapshot snapshot)
        {
            if (snapshot == null || snapshot.TradingValue <= 0) return "-";
            string prefix = snapshot.IsEstimatedTradingValue ? "" : "";
            return $"{prefix}{FormatKoreanMoney(snapshot.TradingValue)}";
        }

        private System.Windows.Media.Brush ResolveRateBrush(string rateText)
        {
            if (string.IsNullOrWhiteSpace(rateText))
                return System.Windows.Media.Brushes.White;

            string clean = rateText.Replace("%", "").Replace(",", "").Trim();
            if (decimal.TryParse(clean, out decimal rate))
            {
                if (rate > 0) return System.Windows.Media.Brushes.DeepPink;
                if (rate < 0) return System.Windows.Media.Brushes.DeepSkyBlue;
            }

            return System.Windows.Media.Brushes.White;
        }

        private string FormatKoreanMoney(long value)
        {
            if (value >= 100_000_000) return $"{value / 100_000_000.0:0.0}억";
            if (value >= 10_000) return $"{value / 10_000.0:0.0}만";
            return value.ToString("N0");
        }

        private class StockInfoSnapshot
        {
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
            public long CurrentPrice { get; set; }
            public long Volume { get; set; }
            public long TradingValue { get; set; }
            public string ChangeRateText { get; set; } = "";
            public double ChangeRatePercent { get; set; }
            public string TurnoverRateText { get; set; } = "";
            public double TurnoverRatePercent { get; set; }
            public bool IsEstimatedTradingValue { get; set; }
            public string Market { get; set; } = "";
            public string RequestCode { get; set; } = "";
        }
    }
}
