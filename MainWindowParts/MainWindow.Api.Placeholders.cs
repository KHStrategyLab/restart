#nullable disable

using KHStrategyLab.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private const string DefaultChartCode = "005930";
        private const string DefaultChartName = "삼성전자";
        private const int DailyChartStoreCandleCount = 120;
        private const int DailyChartVisibleCandleCount = 100;
        private const int MinuteChartStoreCandleCount = 500;
        private const int MinuteChartVisibleCandleCount = 500;
        private const int FiveMinuteChartVisibleCandleCount = 60;

        private readonly object _dailyChartLock = new();
        private List<ChartCandle> _lastDailyChartCandles = [];
        private string _lastDailyChartCode = "";
        private string _lastDailyChartName = "";
        private bool _lastDailyChartIsIndex = false;
        private string _lastChartIntervalLabel = "일봉";
        private string _lastChartMarketLabel = "";
        private int _dailyChartRequestSeq = 0;

        private async Task LoadDefaultDailyChartAsync()
        {
            await FetchAndDrawDailyChartAsync(DefaultChartCode, DefaultChartName);
        }

        private async Task FetchAndDrawDailyChartAsync(string code, string name)
        {
            await FetchAndDrawDailyChartCoreAsync(code, name, isIndexChart: false);
        }

        private async Task FetchAndDrawIndexDailyChartAsync(string indexCode, string indexName)
        {
            await FetchAndDrawDailyChartCoreAsync(indexCode, indexName, isIndexChart: true);
        }

        private async Task ReloadCurrentChartAsDailyAsync()
        {
            string code;
            string name;
            bool isIndexChart;

            lock (_dailyChartLock)
            {
                code = _lastDailyChartCode;
                name = _lastDailyChartName;
                isIndexChart = _lastDailyChartIsIndex;
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                await LoadDefaultDailyChartAsync();
                return;
            }

            if (isIndexChart)
                await FetchAndDrawIndexDailyChartAsync(code, name);
            else
                await FetchAndDrawDailyChartAsync(code, name);
        }

        private async Task ReloadCurrentChartAsFiveMinuteAsync()
        {
            await ReloadCurrentChartAsMinuteAsync(5);
        }

        private async Task ReloadCurrentChartAsTenMinuteAsync()
        {
            await ReloadCurrentChartAsMinuteAsync(10);
        }

        private async Task ReloadCurrentChartAsThirtyMinuteAsync()
        {
            await ReloadCurrentChartAsMinuteAsync(30);
        }

        private async Task ReloadCurrentChartAsMinuteAsync(int minute)
        {
            string code;
            string name;
            bool isIndexChart;

            lock (_dailyChartLock)
            {
                code = _lastDailyChartCode;
                name = _lastDailyChartName;
                isIndexChart = _lastDailyChartIsIndex;
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                await FetchAndDrawMinuteChartAsync(DefaultChartCode, DefaultChartName, minute);
                return;
            }

            if (isIndexChart)
            {
                SetTextBlock("TxtChartTitle", $" {name} ({code}) | {minute}분봉 미지원");
                Log($"⚠️ [차트] 지수 {minute}분봉은 현재 지원하지 않습니다: {name}({code})");
                return;
            }

            await FetchAndDrawMinuteChartAsync(code, name, minute);
        }

        private async Task FetchAndDrawDailyChartCoreAsync(string code, string name, bool isIndexChart)
        {
            int requestSeq = Interlocked.Increment(ref _dailyChartRequestSeq);

            string displayCode = isIndexChart ? (code ?? "").Trim() : NormalizeStockCode(code);
            name = string.IsNullOrWhiteSpace(name) ? displayCode : name.Trim();

            if (string.IsNullOrWhiteSpace(displayCode))
                return;

            if (string.IsNullOrWhiteSpace(_token))
            {
                SetTextBlock("TxtChartTitle", $" {name} ({displayCode}) | 토큰 대기");
                Log($"⚠️ [{(isIndexChart ? "지수차트" : "차트")}] 토큰 없음: {name}({displayCode})");
                return;
            }

            try
            {
                Dispatcher.Invoke(() =>
                {
                    SetTextBlock("TxtChartTitle", $" {name} ({displayCode}) | 일봉 조회중...");
                    SetTextBlock("TxtChartFooterPrice", "---");
                    SetTextBlock("TxtChartFooterVolume", "---");
                    SetTextBlock("TxtChartFooterValue", "---");
                    SetTextBlock("TxtChartFooterTurnover", isIndexChart ? "---" : ResolveChartTurnoverText(displayCode));
                });

                if (!isIndexChart)
                    _ = Task.Run(() => RefreshStockInfoAsync(displayCode));

                List<DailyChartRequestOption> requestOptions = await BuildDailyChartRequestOptionsAsync(displayCode, isIndexChart);

                foreach (DailyChartRequestOption option in requestOptions)
                {
                    Log($"📈 [{(isIndexChart ? "지수차트" : "차트")}] 일봉 조회 요청: {name}({displayCode}) / 요청코드={option.RequestCode} / 시장={option.MarketLabel} / api-id={option.ApiId}");

                    DailyChartLoadResult result = await RequestDailyChartCandlesAsync(option, name);

                    if (result == null || result.Candles == null || result.Candles.Count == 0)
                        continue;

                    if (requestSeq != _dailyChartRequestSeq)
                        return;

                    lock (_dailyChartLock)
                    {
                        _lastDailyChartCode = displayCode;
                        _lastDailyChartName = name;
                        _lastDailyChartIsIndex = isIndexChart;
                        _lastChartIntervalLabel = "일봉";
                        _lastChartMarketLabel = result.MarketLabel;
                        _lastDailyChartCandles = result.Candles;
                    }

                    Dispatcher.Invoke(() =>
                    {
                        ChartCandle last = result.Candles.Last();

                        SetTextBlock("TxtChartTitle", $" {name} ({displayCode}) | {result.MarketLabel} | 일봉");
                        SetTextBlock("TxtChartFooterPrice", last.Close >= 0 ? FormatChartPriceText(last.Close, isIndexChart) : "---");
                        ApplyChartFooterPriceColor(ResolveChartPriceBrush(displayCode, isIndexChart));
                        SetTextBlock("TxtChartFooterVolume", last.Volume > 0 ? last.Volume.ToString("N0") : "---");
                        SetTextBlock("TxtChartFooterValue", last.TradingValue > 0 ? FormatKoreanMoney(last.TradingValue) : "---");
                        SetTextBlock("TxtChartFooterTurnover", isIndexChart ? "---" : ResolveChartTurnoverText(displayCode));

                        DrawDailyChartCanvas();
                    });

                    Log($"✅ [{(isIndexChart ? "지수차트" : "차트")}] 일봉 표시 완료: {name}({displayCode}) / 시장={result.MarketLabel}");
                    return;
                }

                Log($"⚠️ [{(isIndexChart ? "지수차트" : "차트")}] 표시 가능한 일봉 없음: {name}({displayCode})");
            }
            catch (Exception ex)
            {
                Log($"❌ [{(isIndexChart ? "지수차트 오류" : "차트 오류")}] {name}({displayCode}) / {ex.Message}");
            }
        }

        private async Task FetchAndDrawTenMinuteChartAsync(string code, string name)
        {
            await FetchAndDrawMinuteChartAsync(code, name, 10);
        }

        private async Task FetchAndDrawMinuteChartAsync(string code, string name, int minute)
        {
            int requestSeq = Interlocked.Increment(ref _dailyChartRequestSeq);
            minute = minute <= 0 ? 10 : minute;
            string chartLabel = $"{minute}분봉";
            string logLabel = $"{minute}분차트";

            string displayCode = NormalizeStockCode(code);
            name = string.IsNullOrWhiteSpace(name) ? displayCode : name.Trim();

            if (string.IsNullOrWhiteSpace(displayCode))
                return;

            if (string.IsNullOrWhiteSpace(_token))
            {
                SetTextBlock("TxtChartTitle", $" {name} ({displayCode}) | 토큰 대기");
                Log($"⚠️ [{logLabel}] 토큰 없음: {name}({displayCode})");
                return;
            }

            try
            {
                Dispatcher.Invoke(() =>
                {
                    SetTextBlock("TxtChartTitle", $" {name} ({displayCode}) | {chartLabel} 조회중...");
                    SetTextBlock("TxtChartFooterPrice", "---");
                    SetTextBlock("TxtChartFooterVolume", "---");
                    SetTextBlock("TxtChartFooterValue", "---");
                    SetTextBlock("TxtChartFooterTurnover", ResolveChartTurnoverText(displayCode));
                });

                _ = Task.Run(() => RefreshStockInfoAsync(displayCode));

                List<string> markets = await ResolveMinuteChartMarketsAsync(displayCode);

                foreach (string market in markets)
                {
                    Log($"📈 [{logLabel}] 조회 요청: {name}({displayCode}) / 시장={market}");

                    StrategyMinuteChartBundle result = await RequestMinuteChartCandlesForStrategyAsync(displayCode, minute, market);

                    if (result == null || result.Candles == null || result.Candles.Count == 0)
                        continue;

                    if (requestSeq != _dailyChartRequestSeq)
                        return;

                    List<ChartCandle> candles = [.. result.Candles
                        .Where(x => x != null && x.Close > 0)
                        .OrderBy(ParseMinuteCandleDateTime)
                        .TakeLast(MinuteChartStoreCandleCount)];

                    if (candles.Count == 0)
                        continue;

                    lock (_dailyChartLock)
                    {
                        _lastDailyChartCode = displayCode;
                        _lastDailyChartName = name;
                        _lastDailyChartIsIndex = false;
                        _lastChartIntervalLabel = chartLabel;
                        _lastChartMarketLabel = market;
                        _lastDailyChartCandles = candles;
                    }

                    Dispatcher.Invoke(() =>
                    {
                        ChartCandle last = candles.Last();

                        SetTextBlock("TxtChartTitle", $" {name} ({displayCode}) | {chartLabel} | {result.RequestCode}");
                        SetTextBlock("TxtChartFooterPrice", last.Close > 0 ? last.Close.ToString("N0") : "---");
                        ApplyChartFooterPriceColor(ResolveChartPriceBrush(displayCode, isIndexChart: false));
                        SetTextBlock("TxtChartFooterVolume", last.Volume > 0 ? last.Volume.ToString("N0") : "---");
                        SetTextBlock("TxtChartFooterValue", last.TradingValue > 0 ? FormatKoreanMoney(last.TradingValue) : "---");
                        SetTextBlock("TxtChartFooterTurnover", ResolveChartTurnoverText(displayCode));

                        DrawDailyChartCanvas();
                    });

                    Log($"✅ [{logLabel}] 표시 완료: {name}({displayCode}) / 시장={market} / 요청코드={result.RequestCode}");
                    return;
                }

                Log($"⚠️ [{logLabel}] 표시 가능한 데이터 없음: {name}({displayCode}) / 시장후보={string.Join(",", markets)}");
            }
            catch (Exception ex)
            {
                Log($"❌ [{logLabel} 오류] {name}({displayCode}) / {ex.Message}");
            }
        }

        private async Task<List<string>> ResolveMinuteChartMarketsAsync(string displayCode)
        {
            var markets = new List<string>();
            string code = NormalizeStockCode(displayCode);

            if (!string.IsNullOrWhiteSpace(code) && _watchCandidates.TryGetValue(code, out WatchCandidate candidate))
            {
                string market = (candidate.StrategyMarket ?? "").Trim().ToUpperInvariant();
                if (market == "KRX" || market == "NXT")
                    markets.Add(market);
            }

            if (await IsNxtEnabledAsync(code))
                markets.Add("NXT");

            markets.Add("KRX");

            return [.. markets
                .Where(x => x == "KRX" || x == "NXT")
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        private async Task<List<DailyChartRequestOption>> BuildDailyChartRequestOptionsAsync(string displayCode, bool isIndexChart)
        {
            string baseDate = DateTime.Now.ToString("yyyyMMdd");
            List<DailyChartRequestOption> options = [];

            if (isIndexChart)
            {
                options.Add(new DailyChartRequestOption
                {
                    DisplayCode = displayCode,
                    RequestCode = displayCode,
                    ApiId = "ka20006",
                    MarketLabel = "지수",
                    Body = new
                    {
                        inds_cd = displayCode,
                        base_dt = baseDate
                    }
                });

                return options;
            }

            bool isNxtEnabled = await IsNxtEnabledAsync(displayCode);

            if (isNxtEnabled)
            {
                options.Add(CreateStockDailyChartRequestOption(displayCode, $"{displayCode}_AL", "SOR(_AL)", baseDate));
                options.Add(CreateStockDailyChartRequestOption(displayCode, $"{displayCode}_NX", "NXT(_NX)", baseDate));
                options.Add(CreateStockDailyChartRequestOption(displayCode, displayCode, "KRX", baseDate));
            }
            else
            {
                options.Add(CreateStockDailyChartRequestOption(displayCode, displayCode, "KRX", baseDate));
            }

            return options;
        }

        private DailyChartRequestOption CreateStockDailyChartRequestOption(string displayCode, string requestCode, string marketLabel, string baseDate)
        {
            return new DailyChartRequestOption
            {
                DisplayCode = displayCode,
                RequestCode = requestCode,
                ApiId = "ka10081",
                MarketLabel = marketLabel,
                Body = new
                {
                    stk_cd = requestCode,
                    base_dt = baseDate,
                    upd_stkpc_tp = "1"
                }
            };
        }

        private async Task<DailyChartLoadResult> RequestDailyChartCandlesAsync(DailyChartRequestOption option, string name)
        {
            string url = "https://api.kiwoom.com/api/dostk/chart";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);

            request.Headers.TryAddWithoutValidation("authorization", $"Bearer {_token}");
            request.Headers.TryAddWithoutValidation("api-id", option.ApiId);
            request.Headers.TryAddWithoutValidation("cont-yn", "N");
            request.Headers.TryAddWithoutValidation("next-key", "");
            request.Content = new StringContent(JsonConvert.SerializeObject(option.Body), Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await _http.SendAsync(request);
            string text = await response.Content.ReadAsStringAsync();

            JObject json;

            try
            {
                json = JObject.Parse(text);
            }
            catch
            {
                _ = SaveRawAsync($"daily_chart_parse_fail_{SafeRawFileCode(option.RequestCode)}", text);
                Log($"⚠️ [차트] JSON 파싱 실패: {name}({option.DisplayCode}) / 요청코드={option.RequestCode}");
                return null;
            }

            string returnCode = json["return_code"]?.ToString() ?? "";
            string returnMsg = json["return_msg"]?.ToString() ?? "";

            if (!response.IsSuccessStatusCode)
            {
                _ = SaveRawAsync($"daily_chart_http_fail_{SafeRawFileCode(option.RequestCode)}", text);
                Log($"⚠️ [차트] HTTP 오류: {name}({option.DisplayCode}) / 요청코드={option.RequestCode} / {(int)response.StatusCode} / {response.ReasonPhrase}");
                return null;
            }

            if (!string.IsNullOrWhiteSpace(returnCode) && returnCode != "0")
            {
                _ = SaveRawAsync($"daily_chart_api_fail_{SafeRawFileCode(option.RequestCode)}", text);
                Log($"⚠️ [차트] 응답 오류: {name}({option.DisplayCode}) / 요청코드={option.RequestCode} / code={returnCode} / msg={returnMsg}");
                return null;
            }

            JArray rows = FindDailyChartArray(json);

            if (rows == null || rows.Count == 0)
            {
                Log($"⚠️ [차트] 일봉 데이터 없음: {name}({option.DisplayCode}) / 요청코드={option.RequestCode}");
                return null;
            }

            bool isIndexChart = option.ApiId == "ka20006";

            List<ChartCandle> candles = [.. rows
                .Select(x => ParseDailyChartCandle(x, isIndexChart))
                .Where(x => x != null && x.Close > 0)
                .GroupBy(x => x.Date)
                .Select(g => g.First())
                .OrderBy(x => x.Date)
                .TakeLast(DailyChartStoreCandleCount)];

            if (candles.Count == 0)
                return null;

            FillDailyChartMovingAverages(candles);

            return new DailyChartLoadResult
            {
                RequestCode = option.RequestCode,
                MarketLabel = option.MarketLabel,
                Candles = candles
            };
        }

        private string SafeRawFileCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return "unknown";

            return code.Replace("/", "_").Replace("\\", "_").Replace(":", "_");
        }

        private sealed class DailyChartRequestOption
        {
            public string DisplayCode { get; set; } = "";
            public string RequestCode { get; set; } = "";
            public string ApiId { get; set; } = "";
            public string MarketLabel { get; set; } = "";
            public object Body { get; set; }
        }

        private sealed class DailyChartLoadResult
        {
            public string RequestCode { get; set; } = "";
            public string MarketLabel { get; set; } = "";
            public List<ChartCandle> Candles { get; set; } = [];
        }

        private JArray FindDailyChartArray(JObject json)
        {
            string[] directKeys =
            [
                "stk_dt_pole_chart_qry",
                "stk_dt_chart_qry",
                "inds_dt_pole_chart_qry",
                "inds_dt_chart_qry",
                "inds_day_chart_qry",
                "upjong_dt_chart_qry",
                "output",
                "data",
                "list",
                "chart",
                "items"
            ];

            foreach (string key in directKeys)
            {
                if (json[key] is JArray directArray)
                    return directArray;
            }

            foreach (JProperty prop in json.Properties())
            {
                if (prop.Value is JArray array)
                    return array;
            }

            foreach (JProperty prop in json.Properties())
            {
                if (prop.Value is JObject child)
                {
                    foreach (JProperty childProp in child.Properties())
                    {
                        if (childProp.Value is JArray array)
                            return array;
                    }
                }
            }

            return null;
        }

        private ChartCandle ParseDailyChartCandle(JToken token, bool isIndexChart)
        {
            if (token == null)
                return null;

            string date = NormalizeChartDate(ReadDailyChartText(token, "dt", "date", "base_dt", "stck_bsop_date", "일자"));

            long open = ParseDailyChartPriceLong(ReadDailyChartText(token, "open_pric", "open", "stck_oprc", "시가"), isIndexChart);
            long high = ParseDailyChartPriceLong(ReadDailyChartText(token, "high_pric", "high", "stck_hgpr", "고가"), isIndexChart);
            long low = ParseDailyChartPriceLong(ReadDailyChartText(token, "low_pric", "low", "stck_lwpr", "저가"), isIndexChart);
            long close = ParseDailyChartPriceLong(ReadDailyChartText(token, "cur_prc", "close_pric", "close", "stck_clpr", "현재가", "종가"), isIndexChart);
            long volume = Math.Abs(ParseDailyChartLong(ReadDailyChartText(token, "trde_qty", "cntg_vol", "acml_vol", "volume", "거래량")));
            long tradingValue = Math.Abs(ParseDailyChartLong(ReadDailyChartText(token, "trde_prica", "trde_prc", "acml_tr_pbmn", "acc_trdval", "trading_value", "거래대금")));

            if (string.IsNullOrWhiteSpace(date))
                date = DateTime.Now.ToString("yyyyMMdd");

            if (open <= 0 && close > 0)
                open = close;

            if (high <= 0)
                high = Math.Max(open, close);

            if (low <= 0)
                low = Math.Min(open, close);

            if (close <= 0)
                return null;

            long estimatedTradingValue = close > 0 && volume > 0 ? close * volume : 0;

            if (estimatedTradingValue > 0 && (tradingValue <= 0 || tradingValue < estimatedTradingValue / 1000))
                tradingValue = estimatedTradingValue;

            return new ChartCandle
            {
                Date = date,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume,
                TradingValue = tradingValue
            };
        }

        private string ReadDailyChartText(JToken token, params string[] keys)
        {
            if (token == null)
                return "";

            if (token is JObject obj)
            {
                foreach (string key in keys)
                {
                    JToken value = obj[key];

                    if (value == null)
                    {
                        JProperty prop = obj.Properties().FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
                        value = prop?.Value;
                    }

                    string text = value?.ToString() ?? "";

                    if (!string.IsNullOrWhiteSpace(text))
                        return text.Trim();
                }
            }

            return "";
        }

        private long ParseDailyChartPriceLong(string value, bool isIndexChart)
        {
            if (!isIndexChart)
                return Math.Abs(ParseDailyChartLong(value));

            if (string.IsNullOrWhiteSpace(value))
                return 0;

            string clean = value.Replace(",", "")
                                .Replace("+", "")
                                .Trim();

            if (decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal result))
                return (long)Math.Round(Math.Abs(result) * 100m, MidpointRounding.AwayFromZero);

            if (decimal.TryParse(clean, NumberStyles.Any, CultureInfo.CurrentCulture, out result))
                return (long)Math.Round(Math.Abs(result) * 100m, MidpointRounding.AwayFromZero);

            return 0;
        }

        private long ParseDailyChartLong(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;

            string clean = value.Replace(",", "")
                                .Replace("+", "")
                                .Trim();

            if (long.TryParse(clean, out long result))
                return result;

            return 0;
        }

        private string NormalizeChartDate(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            string digits = NumberOnlyRegex().Replace(value, "");

            if (digits.Length >= 8)
                return digits.Substring(0, 8);

            return digits;
        }

        private void FillDailyChartMovingAverages(List<ChartCandle> candles)
        {
            if (candles == null || candles.Count == 0)
                return;

            for (int i = 0; i < candles.Count; i++)
            {
                candles[i].MA5 = CalculateDailyChartMa(candles, i, 5);
                candles[i].MA10 = CalculateDailyChartMa(candles, i, 10);
                candles[i].MA20 = CalculateDailyChartMa(candles, i, 20);
                candles[i].MA60 = CalculateDailyChartMa(candles, i, 60);
                candles[i].MA200 = CalculateDailyChartMa(candles, i, 200);
                candles[i].MA480 = CalculateDailyChartMa(candles, i, 480);
            }
        }

        private double CalculateDailyChartMa(List<ChartCandle> candles, int index, int period)
        {
            if (candles == null || index < period - 1)
                return 0;

            return candles.Skip(index - period + 1).Take(period).Average(x => (double)x.Close);
        }

        private string ResolveChartTurnoverText(string code)
        {
            code = NormalizeStockCode(code);

            StockGridRow row = _search00List.Cast<StockGridRow>()
                .Concat(_rankList.Cast<StockGridRow>())
                .Concat(_balance.Cast<StockGridRow>())
                .FirstOrDefault(x => NormalizeStockCode(x.Code) == code);

            if (row == null || string.IsNullOrWhiteSpace(row.TurnoverRateText))
                return "---";

            string turnover = row.TurnoverRateText.Trim();

            // GridRank(0198 실시간순위)처럼 회전율이 아닌 분류/메모 문자가 섞여 들어오면
            // 차트 하단 회전율에는 표시하지 않는다.
            if (!turnover.Contains("%"))
                return "---";

            return turnover;
        }

        private Brush ResolveChartPriceBrush(string code, bool isIndexChart)
        {
            if (isIndexChart)
                return Brushes.White;

            code = NormalizeStockCode(code);

            StockGridRow row = _search00List.Cast<StockGridRow>()
                .Concat(_rankList.Cast<StockGridRow>())
                .Concat(_balance.Cast<StockGridRow>())
                .FirstOrDefault(x => NormalizeStockCode(x.Code) == code);

            return row?.PriceColor ?? Brushes.White;
        }

        private void ApplyChartFooterPriceColor(Brush brush)
        {
            void Apply()
            {
                if (FindName("TxtChartFooterPrice") is TextBlock txt)
                    txt.Foreground = brush ?? Brushes.White;
            }

            if (Dispatcher.CheckAccess()) Apply();
            else Dispatcher.Invoke(Apply);
        }

        private void DrawDailyChartCanvas()
        {
            try
            {
                if (ChartCanvas == null)
                    return;

                List<ChartCandle> candles;
                string chartCode;
                string chartName;
                bool isIndexChart;
                string intervalLabel;
                string marketLabel;

                lock (_dailyChartLock)
                {
                    candles = _lastDailyChartCandles?.ToList() ?? [];
                    chartCode = _lastDailyChartCode;
                    chartName = _lastDailyChartName;
                    isIndexChart = _lastDailyChartIsIndex;
                    intervalLabel = _lastChartIntervalLabel;
                    marketLabel = _lastChartMarketLabel;
                }

                ChartCanvas.Children.Clear();

                double width = ChartCanvas.ActualWidth;
                double height = ChartCanvas.ActualHeight;

                if (width < 100 || height < 100)
                    return;

                if (candles.Count == 0)
                {
                    AddChartMessage("종목을 선택하면 차트가 표시됩니다.");
                    return;
                }

                bool isMinuteChart = (intervalLabel ?? "").EndsWith("분봉", StringComparison.OrdinalIgnoreCase);
                int visibleCount = isMinuteChart
                    ? ResolveMinuteChartVisibleCount(intervalLabel)
                    : DailyChartVisibleCandleCount;
                List<ChartCandle> visible = [.. candles.TakeLast(visibleCount)];

                double left = 10;
                double right = 58;
                double top = 14;
                double bottom = 20;
                double gap = 8;
                double volumeHeight = Math.Max(45, Math.Min(80, height * 0.22));
                double priceHeight = height - top - bottom - gap - volumeHeight;

                if (priceHeight < 60)
                    return;

                double chartWidth = width - left - right;
                double volumeTop = top + priceHeight + gap;

                double maxPrice = visible.Max(x => (double)x.High);
                double minPrice = visible.Min(x => (double)x.Low);

                IncludeMaRange(visible, x => x.MA5, ref minPrice, ref maxPrice);
                IncludeMaRange(visible, x => x.MA10, ref minPrice, ref maxPrice);
                IncludeMaRange(visible, x => x.MA20, ref minPrice, ref maxPrice);
                IncludeMaRange(visible, x => x.MA60, ref minPrice, ref maxPrice);
                if (isMinuteChart)
                {
                    IncludeMaRange(visible, x => x.MA200, ref minPrice, ref maxPrice);
                    IncludeMaRange(visible, x => x.MA480, ref minPrice, ref maxPrice);
                }

                if (maxPrice <= minPrice)
                {
                    maxPrice += 1;
                    minPrice -= 1;
                }

                double pricePadding = (maxPrice - minPrice) * 0.06;
                maxPrice += pricePadding;
                minPrice = Math.Max(0, minPrice - pricePadding);

                double maxVolume = Math.Max(1, visible.Max(x => (double)x.Volume));
                double step = chartWidth / Math.Max(1, visible.Count);
                double candleWidth = Math.Max(1, Math.Min(10, step * 0.58));

                double PriceY(double price)
                {
                    return top + ((maxPrice - price) / (maxPrice - minPrice)) * priceHeight;
                }

                double VolumeY(double volume)
                {
                    return volumeTop + volumeHeight - (volume / maxVolume) * volumeHeight;
                }

                DrawChartGrid(left, right, top, priceHeight, width, minPrice, maxPrice, isIndexChart);

                for (int i = 0; i < visible.Count; i++)
                {
                    ChartCandle c = visible[i];
                    double x = left + (i * step) + (step / 2.0);

                    Brush candleBrush = c.Close >= c.Open ? Brushes.IndianRed : Brushes.DodgerBlue;

                    double highY = PriceY(c.High);
                    double lowY = PriceY(c.Low);
                    double openY = PriceY(c.Open);
                    double closeY = PriceY(c.Close);
                    double bodyTop = Math.Min(openY, closeY);
                    double bodyHeight = Math.Max(1, Math.Abs(openY - closeY));

                    ChartCanvas.Children.Add(new Line
                    {
                        X1 = x,
                        X2 = x,
                        Y1 = highY,
                        Y2 = lowY,
                        Stroke = candleBrush,
                        StrokeThickness = 1
                    });

                    ChartCanvas.Children.Add(new Rectangle
                    {
                        Width = candleWidth,
                        Height = bodyHeight,
                        Fill = candleBrush,
                        Stroke = candleBrush,
                        StrokeThickness = 1
                    });

                    Rectangle body = (Rectangle)ChartCanvas.Children[ChartCanvas.Children.Count - 1];
                    Canvas.SetLeft(body, x - candleWidth / 2.0);
                    Canvas.SetTop(body, bodyTop);

                    double volumeY = VolumeY(c.Volume);
                    double volumeBarHeight = Math.Max(1, volumeTop + volumeHeight - volumeY);
                    Rectangle volumeBar = new()
                    {
                        Width = Math.Max(1, candleWidth),
                        Height = volumeBarHeight,
                        Fill = candleBrush,
                        Opacity = 0.45
                    };
                    ChartCanvas.Children.Add(volumeBar);
                    Canvas.SetLeft(volumeBar, x - candleWidth / 2.0);
                    Canvas.SetTop(volumeBar, volumeY);
                }

                DrawMaLine(visible, x => x.MA5, Brushes.DeepPink, left, step, PriceY);
                DrawMaLine(visible, x => x.MA10, Brushes.DeepSkyBlue, left, step, PriceY);
                DrawMaLine(visible, x => x.MA20, Brushes.Gold, left, step, PriceY);
                DrawMaLine(visible, x => x.MA60, Brushes.LimeGreen, left, step, PriceY);
                if (isMinuteChart)
                {
                    DrawMaLine(visible, x => x.MA200, new SolidColorBrush(Color.FromRgb(249, 115, 22)), left, step, PriceY);
                    DrawMaLine(visible, x => x.MA480, Brushes.WhiteSmoke, left, step, PriceY);
                }

                if (isMinuteChart && !isIndexChart)
                {
                    ChartCandle last = visible.LastOrDefault();
                    long currentPrice = ResolveChartCurrentPriceValue(chartCode, last?.Close ?? 0);
                    if (currentPrice > 0)
                        DrawCurrentPriceAxisMarker(currentPrice, PriceY, left, width, right, top, priceHeight, ResolveChartPriceBrush(chartCode, isIndexChart: false));
                }

                DrawChartBottomDates(visible, left, step, volumeTop, volumeHeight, isMinuteChart);
            }
            catch (Exception ex)
            {
                Log($"⚠️ [차트 그리기 오류] {ex.Message}");
            }
        }

        private long ResolveChartCurrentPriceValue(string code, long fallbackClose)
        {
            code = NormalizeStockCode(code);
            if (string.IsNullOrWhiteSpace(code))
                return fallbackClose;

            StockGridRow row = _search00List.Cast<StockGridRow>()
                .Concat(_rankList.Cast<StockGridRow>())
                .Concat(_balance.Cast<StockGridRow>())
                .FirstOrDefault(x => NormalizeStockCode(x.Code) == code);

            long currentPrice = row?.CurrentPrice ?? 0;
            return currentPrice > 0 ? currentPrice : fallbackClose;
        }

        private void DrawCurrentPriceAxisMarker(long currentPrice, Func<double, double> priceY, double left, double width, double right, double top, double priceHeight, Brush markerBrush)
        {
            double y = priceY(currentPrice);
            double minY = top + 2;
            double maxY = top + priceHeight - 2;
            y = Math.Max(minY, Math.Min(maxY, y));
            markerBrush ??= Brushes.White;

            ChartCanvas.Children.Add(new Line
            {
                X1 = left,
                X2 = width - right,
                Y1 = y,
                Y2 = y,
                Stroke = markerBrush,
                StrokeThickness = 1,
                Opacity = 0.85,
                StrokeDashArray = [3, 3]
            });

            TextBlock label = new()
            {
                Text = $"{currentPrice:N0}",
                Foreground = markerBrush,
                FontSize = 11,
                FontWeight = FontWeights.Bold
            };

            ChartCanvas.Children.Add(label);
            Canvas.SetLeft(label, width - right + 6);
            Canvas.SetTop(label, y - 8);
        }

        private void IncludeMaRange(List<ChartCandle> candles, Func<ChartCandle, double> selector, ref double minPrice, ref double maxPrice)
        {
            IEnumerable<double> values = candles.Select(selector).Where(x => x > 0);

            if (!values.Any())
                return;

            minPrice = Math.Min(minPrice, values.Min());
            maxPrice = Math.Max(maxPrice, values.Max());
        }

        private void DrawChartGrid(double left, double right, double top, double priceHeight, double width, double minPrice, double maxPrice, bool isIndexChart)
        {
            for (int i = 0; i <= 4; i++)
            {
                double y = top + (priceHeight / 4.0 * i);
                double price = maxPrice - ((maxPrice - minPrice) / 4.0 * i);

                ChartCanvas.Children.Add(new Line
                {
                    X1 = left,
                    X2 = width - right,
                    Y1 = y,
                    Y2 = y,
                    Stroke = new SolidColorBrush(Color.FromRgb(55, 65, 81)),
                    StrokeThickness = 0.6,
                    Opacity = 0.8
                });

                TextBlock label = new()
                {
                    Text = FormatChartAxisPrice(price, isIndexChart),
                    Foreground = Brushes.Gray,
                    FontSize = 11
                };

                ChartCanvas.Children.Add(label);
                Canvas.SetLeft(label, width - right + 6);
                Canvas.SetTop(label, y - 8);
            }
        }

        private void DrawMaLine(List<ChartCandle> candles, Func<ChartCandle, double> selector, Brush brush, double left, double step, Func<double, double> priceY)
        {
            Point? previous = null;

            for (int i = 0; i < candles.Count; i++)
            {
                double value = selector(candles[i]);

                if (value <= 0)
                {
                    previous = null;
                    continue;
                }

                Point current = new(left + (i * step) + (step / 2.0), priceY(value));

                if (previous.HasValue)
                {
                    ChartCanvas.Children.Add(new Line
                    {
                        X1 = previous.Value.X,
                        Y1 = previous.Value.Y,
                        X2 = current.X,
                        Y2 = current.Y,
                        Stroke = brush,
                        StrokeThickness = 1.35,
                        Opacity = 0.95
                    });
                }

                previous = current;
            }
        }

        private void DrawChartBottomDates(List<ChartCandle> candles, double left, double step, double volumeTop, double volumeHeight, bool isMinuteChart)
        {
            if (candles == null || candles.Count == 0)
                return;

            int interval = Math.Max(12, candles.Count / 4);

            for (int i = 0; i < candles.Count; i += interval)
            {
                string date = isMinuteChart
                    ? FormatMinuteChartBottomLabel(candles[i])
                    : candles[i].Date;

                if (!isMinuteChart && date.Length == 8)
                    date = $"{date.Substring(4, 2)}/{date.Substring(6, 2)}";

                TextBlock label = new()
                {
                    Text = date,
                    Foreground = Brushes.Gray,
                    FontSize = 10
                };

                ChartCanvas.Children.Add(label);
                Canvas.SetLeft(label, left + (i * step));
                Canvas.SetTop(label, volumeTop + volumeHeight + 2);
            }
        }

        private int ResolveMinuteChartVisibleCount(string intervalLabel)
        {
            string digits = NumberOnlyRegex().Replace(intervalLabel ?? "", "");
            if (int.TryParse(digits, out int minutes) && minutes == 5)
                return FiveMinuteChartVisibleCandleCount;

            return MinuteChartVisibleCandleCount;
        }

        private string FormatMinuteChartBottomLabel(ChartCandle candle)
        {
            if (candle == null)
                return "";

            string date = NormalizeChartDate(candle.Date);
            string time = NumberOnlyRegex().Replace(candle.Time ?? "", "");

            if (time.Length >= 4)
                return $"{time.Substring(0, 2)}:{time.Substring(2, 2)}";

            if (date.Length == 8)
                return $"{date.Substring(4, 2)}/{date.Substring(6, 2)}";

            return date;
        }

        private void AddChartMessage(string text)
        {
            TextBlock msg = new()
            {
                Text = text,
                Foreground = Brushes.Gray,
                FontSize = 14,
                FontWeight = FontWeights.Bold
            };

            ChartCanvas.Children.Add(msg);
            Canvas.SetLeft(msg, 20);
            Canvas.SetTop(msg, 20);
        }

        private string FormatChartPriceText(long value, bool isIndexChart)
        {
            if (isIndexChart)
                return (value / 100.0).ToString("N2");

            return value.ToString("N0");
        }

        private string FormatChartAxisPrice(double value, bool isIndexChart)
        {
            if (isIndexChart)
                return (value / 100.0).ToString("N2");

            return value.ToString("N0");
        }

        private void UpdateStatus(string text, Brush color)
        {
            void Apply()
            {
                if (FindName("TxtStatus") is TextBlock txt)
                {
                    txt.Text = text;
                    txt.Foreground = color;
                }
            }

            if (Dispatcher.CheckAccess())
                Apply();
            else
                Dispatcher.Invoke(Apply);
        }

        private void SetTextBlock(string name, string text)
        {
            void Apply()
            {
                if (FindName(name) is TextBlock txt)
                    txt.Text = text;
            }

            if (Dispatcher.CheckAccess())
                Apply();
            else
                Dispatcher.Invoke(Apply);
        }
    }
}
