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
        private const string KiwoomRealtimeRankQueryType = "1"; // 1: 1분
        private const int KiwoomRealtimeRankTopCount = 20;

        private bool _isKiwoomRealtimeRankRefreshing = false;
        private bool _kiwoomRealtimeRankRawSaved = false;
        private DateTime _lastKiwoomRealtimeRankLogTime = DateTime.MinValue;

        private async Task RefreshKiwoomRealtimeTop20Async(bool forceLog = false)
        {
            if (_isKiwoomRealtimeRankRefreshing)
                return;

            if (string.IsNullOrWhiteSpace(_token))
                return;

            _isKiwoomRealtimeRankRefreshing = true;

            try
            {
                bool shouldLog = forceLog || (DateTime.Now - _lastKiwoomRealtimeRankLogTime).TotalSeconds >= 60;

                if (shouldLog)
                {
                    _lastKiwoomRealtimeRankLogTime = DateTime.Now;
                    Log("📌 [0198 TOP20] 키움 실시간종목조회순위 요청: qry_tp=1(1분)");
                }

                string url = "https://api.kiwoom.com/api/dostk/stkinfo";

                var body = new
                {
                    qry_tp = KiwoomRealtimeRankQueryType
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.TryAddWithoutValidation("authorization", $"Bearer {_token}");
                request.Headers.TryAddWithoutValidation("api-id", "ka00198");
                request.Headers.TryAddWithoutValidation("cont-yn", "N");
                request.Headers.TryAddWithoutValidation("next-key", "");
                request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

                using HttpResponseMessage response = await _http.SendAsync(request);
                string text = await response.Content.ReadAsStringAsync();

                if (!_kiwoomRealtimeRankRawSaved)
                {
                    _kiwoomRealtimeRankRawSaved = true;
                    _ = SaveRawAsync("kiwoom_0198_realtime_rank_sample", text);
                }

                JObject json;

                try
                {
                    json = JObject.Parse(text);
                }
                catch
                {
                    Log("❌ [0198 TOP20] JSON 파싱 실패");
                    Log($"📦 [0198 TOP20 원본] {ShortenForLog(text)}");
                    return;
                }

                string returnCode = json["return_code"]?.ToString() ?? "";
                string returnMsg = json["return_msg"]?.ToString() ?? "";

                if (!response.IsSuccessStatusCode)
                {
                    Log($"❌ [0198 TOP20] HTTP 오류: {(int)response.StatusCode} / {response.ReasonPhrase}");
                    Log($"📦 [0198 TOP20 응답] {ShortenForLog(text)}");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(returnCode) && returnCode != "0")
                {
                    Log($"❌ [0198 TOP20] 응답 오류: code={returnCode} / msg={returnMsg}");
                    Log($"📦 [0198 TOP20 응답] {ShortenForLog(text)}");
                    return;
                }

                JArray rows = FindKiwoomRealtimeRankArray(json);

                if (rows == null || rows.Count == 0)
                {
                    if (shouldLog)
                    {
                        Log("ℹ️ [0198 TOP20] 수신 목록이 비어 있습니다. 장전/휴장/키움 응답 형태를 확인하세요.");
                        Log($"🔎 [0198 TOP20] 응답 최상위 키: {string.Join(", ", json.Properties().Select(p => p.Name))}");
                    }

                    return;
                }

                List<KiwoomRealtimeRankSnapshot> topList = [.. rows
                    .Select((item, index) => ParseKiwoomRealtimeRankItem(item, index + 1))
                    .Where(x => !string.IsNullOrWhiteSpace(x.Code))
                    .OrderBy(x => x.Rank)
                    .Take(KiwoomRealtimeRankTopCount)];

                Dispatcher.Invoke(() =>
                {
                    ApplyKiwoomRealtimeRankGrid(topList);
                });

                if (shouldLog)
                    Log($"✅ [0198 TOP20] 화면 표시 완료: {topList.Count}개");
            }
            catch (Exception ex)
            {
                Log($"❌ [0198 TOP20 오류] {ex.Message}");
            }
            finally
            {
                _isKiwoomRealtimeRankRefreshing = false;
            }
        }

        private JArray FindKiwoomRealtimeRankArray(JObject json)
        {
            string[] directKeys =
            [
                "item_inq_rank",
                "itemInqRank",
                "realtime_stk_inq_rank",
                "real_time_stk_inq_rank",
                "stk_inq_rank",
                "inq_rank",
                "rank_list",
                "list",
                "data",
                "output"
            ];

            foreach (string key in directKeys)
            {
                if (json[key] is JArray directArray)
                    return directArray;
            }

            foreach (JProperty prop in json.Properties())
            {
                if (prop.Value is JArray array && LooksLikeRankArray(array))
                    return array;
            }

            foreach (JProperty prop in json.Properties())
            {
                if (prop.Value is JObject child)
                {
                    foreach (JProperty childProp in child.Properties())
                    {
                        if (childProp.Value is JArray array && LooksLikeRankArray(array))
                            return array;
                    }
                }
            }

            return null;
        }

        private bool LooksLikeRankArray(JArray array)
        {
            if (array == null || array.Count == 0)
                return false;

            JToken first = array.First;

            if (first is JArray)
                return true;

            if (first is not JObject obj)
                return false;

            string[] rankKeys =
            [
                "stk_cd", "stkCd", "code", "stk_nm", "stkNm", "name",
                "bigd_rank", "rank_chg", "past_curr_prc", "base_comp_chgr", "prev_base_chgr"
            ];

            return obj.Properties().Any(p => rankKeys.Any(k => string.Equals(k, p.Name, StringComparison.OrdinalIgnoreCase)));
        }

        private KiwoomRealtimeRankSnapshot ParseKiwoomRealtimeRankItem(JToken item, int fallbackRank)
        {
            string rankText = ReadKiwoomRankValue(item, 0, "rank", "rnk", "ord", "순위");
            string code = ReadKiwoomRankValue(item, 1, "stk_cd", "stkCd", "code", "jmcode", "jm_code", "종목코드");
            string name = ReadKiwoomRankValue(item, 2, "stk_nm", "stkNm", "name", "jmname", "jm_name", "종목명");

            string priceText = ReadKiwoomRankValue(item, 3,
                "cur_prc", "curPrc", "current_price", "price", "now_prc", "past_curr_prc", "pastCurrPrc", "현재가", "과거현재가");

            string baseChangeRateText = ReadKiwoomRankValue(item, 4,
                "base_comp_chgr", "baseCompChgr", "flu_rt", "fluRt", "chg_rt", "change_rate", "등락률");

            string prevChangeRateText = ReadKiwoomRankValue(item, 5,
                "prev_base_chgr", "prevBaseChgr", "pre_rt", "전일대비율", "직전기준대비등락율");

            string baseSignText = ReadKiwoomRankValue(item, 6,
                "base_comp_sign", "baseCompSign", "pre_sig", "pred_pre_sig", "sign", "기준가대비부호");

            string prevSignText = ReadKiwoomRankValue(item, 7,
                "prev_base_sign", "prevBaseSign", "직전기준대비부호");

            string rankChangeText = ReadKiwoomRankValue(item, 8,
                "rank_chg", "rankChg", "rank_change", "순위변동");

            string rankChangeSignText = ReadKiwoomRankValue(item, 9,
                "rank_chg_sign", "rankChgSign", "rank_change_sign", "순위변동부호");

            string bigDataRankText = ReadKiwoomRankValue(item, 10,
                "bigd_rank", "bigdRank", "big_data_rank", "bigdata_rank", "빅데이터순위");

            int rank = ParseRankIntSafe(rankText);

            if (rank <= 0)
                rank = fallbackRank;

            code = NormalizeRankStockCode(code);

            if (string.IsNullOrWhiteSpace(name))
                name = code;

            string changeRateText = !string.IsNullOrWhiteSpace(baseChangeRateText)
                ? NormalizeRankRateText(baseChangeRateText, baseSignText)
                : NormalizeRankRateText(prevChangeRateText, prevSignText);

            return new KiwoomRealtimeRankSnapshot
            {
                Rank = rank,
                Code = code,
                Name = name,
                CurrentPrice = ParseRankLongSafe(priceText),
                ChangeRateText = changeRateText,
                RankChangeText = NormalizeRankChangeText(rankChangeText, rankChangeSignText),
                BigDataRankText = NormalizeRankPlainText(bigDataRankText)
            };
        }

        private string ReadKiwoomRankValue(JToken item, int arrayIndex, params string[] keys)
        {
            if (item == null)
                return "";

            if (item is JObject obj)
            {
                foreach (string key in keys)
                {
                    JToken value = obj[key];

                    if (value == null)
                    {
                        JProperty prop = obj.Properties().FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
                        value = prop?.Value;
                    }

                    string text = TokenToRankText(value);

                    if (!string.IsNullOrWhiteSpace(text))
                        return text.Trim();
                }
            }

            if (item is JArray arr && arr.Count > arrayIndex)
            {
                string value = arr[arrayIndex]?.ToString() ?? "";

                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return "";
        }

        private string TokenToRankText(JToken token)
        {
            if (token == null)
                return "";

            if (token is JArray arr)
            {
                if (arr.Count == 0)
                    return "";

                return arr[0]?.ToString() ?? "";
            }

            return token.ToString();
        }

        private void ApplyKiwoomRealtimeRankGrid(List<KiwoomRealtimeRankSnapshot> topList)
        {
            for (int i = 0; i < topList.Count; i++)
            {
                KiwoomRealtimeRankSnapshot item = topList[i];
                RankStock row;

                if (i < _rankList.Count)
                {
                    row = _rankList[i];
                }
                else
                {
                    row = new RankStock();
                    _rankList.Add(row);
                }

                row.Rank = item.Rank;
                row.Code = item.Code;
                row.Name = string.IsNullOrWhiteSpace(item.Name) ? item.Code : item.Name;
                row.CurrentPrice = item.CurrentPrice;
                row.TradingValueText = item.BigDataRankText;
                row.VolumeText = item.RankChangeText;
                row.ChangeRateText = item.ChangeRateText;
                row.ProfitRateText = item.ChangeRateText;
                row.ProfitColor = GetRankRateBrush(item.ChangeRateText);
                row.PriceColor = GetRankRateBrush(item.ChangeRateText);
                row.TurnoverRateText = "";
            }

            while (_rankList.Count > topList.Count)
                _rankList.RemoveAt(_rankList.Count - 1);
        }

        private int ParseRankIntSafe(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;

            value = value.Replace(",", "")
                         .Replace("+", "")
                         .Replace("-", "")
                         .Trim();

            if (int.TryParse(value, out int result))
                return Math.Abs(result);

            return 0;
        }

        private long ParseRankLongSafe(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;

            value = value.Replace(",", "")
                         .Replace("+", "")
                         .Replace("-", "")
                         .Trim();

            if (long.TryParse(value, out long result))
                return Math.Abs(result);

            return 0;
        }

        private string NormalizeRankRateText(string value, string signText = "")
        {
            if (string.IsNullOrWhiteSpace(value))
                return "-";

            value = value.Replace("%", "")
                         .Replace(",", "")
                         .Trim();

            if (string.IsNullOrWhiteSpace(value))
                return "-";

            string sign = GetKiwoomDirectionSign(signText);

            if (string.IsNullOrWhiteSpace(sign))
            {
                if (value.StartsWith("+") || value.StartsWith("-"))
                    return $"{value}%";

                return $"{value}%";
            }

            value = value.Replace("+", "").Replace("-", "");

            return $"{sign}{value}%";
        }

        private string NormalizeRankChangeText(string value, string signText)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "-";

            value = value.Replace(",", "")
                         .Replace("+", "")
                         .Replace("-", "")
                         .Trim();

            if (string.IsNullOrWhiteSpace(value) || value == "0")
                return "-";

            string arrow = GetRankArrow(signText);

            if (string.IsNullOrWhiteSpace(arrow))
                arrow = signText.Contains("-") ? "↓" : "↑";

            return $"{arrow}{value}";
        }

        private string NormalizeRankPlainText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "-";

            return value.Trim();
        }

        private string NormalizeRankStockCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return "";

            code = code.Trim();

            if (code.StartsWith("A", StringComparison.OrdinalIgnoreCase))
                code = code.Substring(1);

            if (code.Contains(":"))
                code = code.Substring(code.LastIndexOf(':') + 1);

            if (code.Contains("_"))
                code = code.Substring(0, code.IndexOf('_'));

            string digits = new([.. code.Where(char.IsDigit)]);

            return string.IsNullOrWhiteSpace(digits) ? code.Trim() : digits;
        }

        private string GetKiwoomDirectionSign(string signText)
        {
            if (string.IsNullOrWhiteSpace(signText))
                return "";

            signText = signText.Trim();

            if (signText.Contains("-") || signText == "4" || signText == "5" || signText.Contains("하락"))
                return "-";

            if (signText.Contains("+") || signText == "1" || signText == "2" || signText.Contains("상승"))
                return "+";

            return "";
        }

        private string GetRankArrow(string signText)
        {
            if (string.IsNullOrWhiteSpace(signText))
                return "";

            signText = signText.Trim();

            if (signText.Contains("-") || signText == "4" || signText == "5" || signText.Contains("하락"))
                return "↓";

            if (signText.Contains("+") || signText == "1" || signText == "2" || signText.Contains("상승"))
                return "↑";

            return "";
        }

        private Brush GetRankRateBrush(string rateText)
        {
            if (string.IsNullOrWhiteSpace(rateText) || rateText == "-")
                return Brushes.White;

            string value = rateText.Replace("%", "")
                                   .Replace(",", "")
                                   .Replace("+", "")
                                   .Trim();

            if (decimal.TryParse(value, out decimal rate))
            {
                if (rate > 0)
                    return Brushes.DeepPink;

                if (rate < 0)
                    return Brushes.DeepSkyBlue;
            }

            return Brushes.White;
        }

        private string ShortenForLog(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            text = text.Replace(Environment.NewLine, " ").Trim();

            if (text.Length <= 700)
                return text;

            return text.Substring(0, 700) + " ...";
        }

        private sealed class KiwoomRealtimeRankSnapshot
        {
            public int Rank { get; set; }
            public string Code { get; set; } = "";
            public string Name { get; set; } = "";
            public long CurrentPrice { get; set; }
            public string ChangeRateText { get; set; } = "-";
            public string RankChangeText { get; set; } = "-";
            public string BigDataRankText { get; set; } = "-";
        }
    }
}
