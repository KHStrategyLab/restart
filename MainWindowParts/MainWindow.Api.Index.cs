#nullable disable

using KHStrategyLab.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private bool _indexRefreshRunning = false;
        private DateTime _lastIndexRefreshAt = DateTime.MinValue;
        private bool _indexAfterHoursNoticeLogged = false;
        private string _lastFinalIndexSnapshotDate = "";

        private sealed class MarketIndexSnapshot
        {
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
            public decimal? Current { get; set; }
            public decimal? Diff { get; set; }
            public decimal? Rate { get; set; }
        }

        private async Task RefreshMarketIndexesAsync(bool force = false)
        {
            if (string.IsNullOrWhiteSpace(_token))
                return;

            if (_indexRefreshRunning)
                return;

            DateTime now = DateTime.Now;
            MarketStateSnapshot state = GetMarketState(now);
            if (state.ShouldUseFinalIndexSnapshot)
            {
                await RefreshFinalMarketIndexSnapshotOnceAsync(state, force);
                return;
            }

            // 로그인 직후 force=true 조회는 어느 시간이든 1회 허용한다.
            // 일반 타이머 조회는 08:00~18:00 사이에만 허용한다.
            // 18:00 이후에는 장마감 이후로 보고, 로그인 시 1회 표시만 유지한다.
            if (!force && !IsMarketIndexAutoRefreshTime(now))
            {
                if (!_indexAfterHoursNoticeLogged)
                {
                    _indexAfterHoursNoticeLogged = true;
                    Log("⏸ [지수] 자동 갱신 중지: 18:00 이후 / 로그인 시 1회 표시만 유지");
                }

                return;
            }

            if (!force && now - _lastIndexRefreshAt < TimeSpan.FromSeconds(25))
                return;

            _indexRefreshRunning = true;
            _lastIndexRefreshAt = now;

            try
            {
                MarketIndexSnapshot kospi = await FetchMarketIndexSnapshotAsync("001", "KOSPI");
                MarketIndexSnapshot kosdaq = await FetchMarketIndexSnapshotAsync("101", "KOSDAQ");

                Dispatcher.Invoke(() =>
                {
                    ApplyMarketIndexSnapshot(kospi, TxtKospi, TxtKospiDiff, TxtKospiRate);
                    ApplyMarketIndexSnapshot(kosdaq, TxtKosdaq, TxtKosdaqDiff, TxtKosdaqRate);
                });

                bool hasAnyValue =
                    (kospi?.Current).HasValue || (kospi?.Diff).HasValue || (kospi?.Rate).HasValue ||
                    (kosdaq?.Current).HasValue || (kosdaq?.Diff).HasValue || (kosdaq?.Rate).HasValue;

                if (hasAnyValue)
                    Log("✅ [지수] KOSPI/KOSDAQ 화면 반영 완료");
                else
                    Log("⚠️ [지수] KOSPI/KOSDAQ 조회 완료, 반영 가능한 숫자값 없음");
            }
            catch (Exception ex)
            {
                Log($"❌ [지수 조회 오류] {ex.Message}");
            }
            finally
            {
                _indexRefreshRunning = false;
            }
        }

        private async Task RefreshFinalMarketIndexSnapshotOnceAsync(MarketStateSnapshot state, bool force)
        {
            string finalDateText = state.RealizedProfitQueryDate.ToString("yyyyMMdd");

            if (!force && string.Equals(_lastFinalIndexSnapshotDate, finalDateText, StringComparison.Ordinal))
                return;

            _indexRefreshRunning = true;
            _lastIndexRefreshAt = state.Now;

            try
            {
                MarketIndexSnapshot kospi = await FetchMarketIndexFinalSnapshotAsync("001", "KOSPI", finalDateText);
                MarketIndexSnapshot kosdaq = await FetchMarketIndexFinalSnapshotAsync("101", "KOSDAQ", finalDateText);

                Dispatcher.Invoke(() =>
                {
                    ApplyMarketIndexSnapshot(kospi, TxtKospi, TxtKospiDiff, TxtKospiRate);
                    ApplyMarketIndexSnapshot(kosdaq, TxtKosdaq, TxtKosdaqDiff, TxtKosdaqRate);
                });

                _lastFinalIndexSnapshotDate = finalDateText;
                Log($"⏸ [지수] {finalDateText} 최종값 1회 표시 후 자동 갱신 대기 / 사유={state.Reason}");
            }
            catch (Exception ex)
            {
                Log($"❌ [지수 최종값 조회 오류] {ex.Message}");
            }
            finally
            {
                _indexRefreshRunning = false;
            }
        }

        private async Task<MarketIndexSnapshot> FetchMarketIndexFinalSnapshotAsync(string indexCode, string indexName, string baseDate)
        {
            DailyChartRequestOption option = new()
            {
                DisplayCode = indexCode,
                RequestCode = indexCode,
                ApiId = "ka20006",
                MarketLabel = "지수",
                Body = new
                {
                    inds_cd = indexCode,
                    base_dt = baseDate
                }
            };

            DailyChartLoadResult result = await RequestDailyChartCandlesAsync(option, indexName);
            if (result?.Candles == null || result.Candles.Count == 0)
                return new MarketIndexSnapshot { Code = indexCode, Name = indexName };

            ChartCandle latest = result.Candles
                .Where(x => string.CompareOrdinal(x.Date, baseDate) <= 0)
                .OrderByDescending(x => x.Date)
                .FirstOrDefault()
                ?? result.Candles.OrderByDescending(x => x.Date).FirstOrDefault();

            ChartCandle previous = result.Candles
                .Where(x => x != null && latest != null && string.CompareOrdinal(x.Date, latest.Date) < 0)
                .OrderByDescending(x => x.Date)
                .FirstOrDefault();

            decimal? current = latest != null ? latest.Close / 100m : null;
            decimal? diff = latest != null && previous != null ? (latest.Close - previous.Close) / 100m : null;
            decimal? rate = diff.HasValue && previous != null && previous.Close > 0
                ? Math.Round(diff.Value / (previous.Close / 100m) * 100m, 2, MidpointRounding.AwayFromZero)
                : null;

            return new MarketIndexSnapshot
            {
                Code = indexCode,
                Name = indexName,
                Current = current,
                Diff = diff,
                Rate = rate
            };
        }

        private bool IsMarketIndexAutoRefreshTime(DateTime now)
        {
            TimeSpan current = now.TimeOfDay;
            TimeSpan start = new(8, 0, 0);
            TimeSpan end = new(18, 0, 0);

            bool isOpenWindow = current >= start && current < end;

            if (isOpenWindow)
                _indexAfterHoursNoticeLogged = false;

            return isOpenWindow;
        }

        private async Task<MarketIndexSnapshot> FetchMarketIndexSnapshotAsync(string indexCode, string indexName)
        {
            string url = "https://api.kiwoom.com/api/dostk/sect";

            var body = new
            {
                mrkt_tp = indexCode,
                inds_cd = indexCode
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("authorization", $"Bearer {_token}");
            request.Headers.TryAddWithoutValidation("api-id", "ka20001");
            request.Headers.TryAddWithoutValidation("cont-yn", "N");
            request.Headers.TryAddWithoutValidation("next-key", "");
            request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await _http.SendAsync(request);
            string text = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _ = SaveRawAsync($"market_index_http_fail_{indexCode}", text);
                Log($"❌ [지수] HTTP 오류: {indexName}({indexCode}) / {(int)response.StatusCode} / {response.ReasonPhrase}");
                return null;
            }

            JObject json;
            try
            {
                json = JObject.Parse(text);
            }
            catch
            {
                _ = SaveRawAsync($"market_index_parse_fail_{indexCode}", text);
                Log($"⚠️ [지수] JSON 파싱 실패: {indexName}({indexCode})");
                return null;
            }

            string returnCode = json["return_code"]?.ToString() ?? "";
            string returnMsg = json["return_msg"]?.ToString() ?? "";

            if (!string.IsNullOrWhiteSpace(returnCode) && returnCode != "0")
            {
                _ = SaveRawAsync($"market_index_api_fail_{indexCode}", text);
                Log($"❌ [지수] 응답 오류: {indexName}({indexCode}) / code={returnCode} / msg={returnMsg}");
                return null;
            }

            string currentText = ReadIndexJsonText(json, "cur_prc", "now_prc", "indx", "close_pric", "현재가", "지수");
            string diffText = ReadIndexJsonText(json, "pred_pre", "pre_vrss", "diff", "전일대비");
            string rateText = ReadIndexJsonText(json, "flu_rt", "updown_rt", "prdy_ctrt", "rate", "등락률");

            // 중요: 0은 정상 수신값이다.
            // null/빈값/파싱 실패와 숫자 0을 구분해서 화면에 그대로 반영한다.
            return new MarketIndexSnapshot
            {
                Code = indexCode,
                Name = indexName,
                Current = ParseIndexDecimalNullable(currentText),
                Diff = ParseIndexDecimalNullable(diffText),
                Rate = ParseIndexDecimalNullable(rateText)
            };
        }

        private void ApplyMarketIndexSnapshot(MarketIndexSnapshot snapshot, TextBlock priceText, TextBlock diffText, TextBlock rateText)
        {
            if (snapshot == null)
                return;

            if (snapshot.Current.HasValue)
                priceText.Text = snapshot.Current.Value.ToString("N2");

            if (snapshot.Diff.HasValue)
                diffText.Text = FormatSignedDecimal(snapshot.Diff.Value, 2, suffix: "");

            if (snapshot.Rate.HasValue)
                rateText.Text = FormatSignedDecimal(snapshot.Rate.Value, 2, suffix: "%");

            decimal colorBase = snapshot.Rate ?? snapshot.Diff ?? 0m;
            Brush brush = colorBase > 0m
                ? Brushes.DeepPink
                : colorBase < 0m
                    ? Brushes.DeepSkyBlue
                    : Brushes.Gray;

            priceText.Foreground = brush;
            diffText.Foreground = brush;
            rateText.Foreground = brush;
        }

        private string ReadIndexJsonText(JToken token, params string[] keys)
        {
            if (token == null)
                return "";

            if (token is JObject obj)
            {
                foreach (string key in keys)
                {
                    foreach (JProperty prop in obj.Properties())
                    {
                        if (!string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase))
                            continue;

                        string value = prop.Value?.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(value))
                            return value.Trim();
                    }
                }

                foreach (JProperty prop in obj.Properties())
                {
                    string value = ReadIndexJsonText(prop.Value, keys);
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }

            if (token is JArray arr)
            {
                foreach (JToken item in arr)
                {
                    string value = ReadIndexJsonText(item, keys);
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }

            return "";
        }

        private decimal? ParseIndexDecimalNullable(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            string clean = value.Replace(",", "")
                                .Replace("%", "")
                                .Replace("+", "")
                                .Trim();

            if (decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal result))
                return result;

            if (decimal.TryParse(clean, NumberStyles.Any, CultureInfo.CurrentCulture, out result))
                return result;

            return null;
        }

        private string FormatSignedDecimal(decimal value, int decimals, string suffix)
        {
            string format = "N" + decimals;
            string prefix = value > 0m ? "+" : "";
            return $"{prefix}{value.ToString(format)}{suffix}";
        }
    }
}
