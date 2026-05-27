#nullable disable

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        /// <summary>
        /// 이전 완성본의 SOR/NXT 판단 흐름을 현재 분할 구조로 옮긴 메서드.
        /// 차트/전략/주문에서 같은 판단 기준을 재사용하기 위해 별도 파일로 분리한다.
        /// </summary>
        private static readonly TimeSpan NxtEnableMinRequestInterval = TimeSpan.FromMilliseconds(220);

        private async Task<bool> IsNxtEnabledAsync(string code, bool forceRefresh = false)
        {
            NxtResolveResult result = await TryResolveNxtEnabledAsync(code, forceRefresh);
            return result.IsKnown && result.IsNxtEnabled;
        }

        private async Task<NxtResolveResult> TryResolveNxtEnabledAsync(string code, bool forceRefresh = false)
        {
            code = NormalizeStockCode(code);

            if (string.IsNullOrWhiteSpace(code))
                return NxtResolveResult.Failed("EMPTY_CODE");

            if (!forceRefresh && _nxtEnableCache.TryGetValue(code, out bool cached))
                return NxtResolveResult.Known(cached, "CACHE");

            if (string.IsNullOrWhiteSpace(_token))
            {
                return NxtResolveResult.Failed("TOKEN_EMPTY");
            }

            try
            {
                await _nxtEnableRequestGate.WaitAsync();

                try
                {
                    TimeSpan elapsed = DateTime.Now - _lastNxtEnableRequestAt;
                    if (_lastNxtEnableRequestAt != DateTime.MinValue && elapsed < NxtEnableMinRequestInterval)
                    {
                        await Task.Delay(NxtEnableMinRequestInterval - elapsed);
                    }

                    _lastNxtEnableRequestAt = DateTime.Now;

                    string url = "https://api.kiwoom.com/api/dostk/stkinfo";
                    var body = new { stk_cd = code };

                    using var request = new HttpRequestMessage(HttpMethod.Post, url);
                    request.Headers.TryAddWithoutValidation("authorization", $"Bearer {_token}");
                    request.Headers.TryAddWithoutValidation("api-id", "ka10100");
                    request.Headers.TryAddWithoutValidation("cont-yn", "N");
                    request.Headers.TryAddWithoutValidation("next-key", "");
                    request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

                    using HttpResponseMessage response = await _http.SendAsync(request);
                    string text = await response.Content.ReadAsStringAsync();

                    _ = SaveRawAsync($"nxt_enable_{code}", text);

                    if (!response.IsSuccessStatusCode)
                    {
                        Log($"⚠️ [SOR/NXT] NXT 여부 조회 HTTP 오류: {code} / {(int)response.StatusCode}");
                        return NxtResolveResult.Failed("HTTP_ERROR");
                    }

                    JObject json;

                    try
                    {
                        json = JObject.Parse(text);
                    }
                    catch
                    {
                        Log($"⚠️ [SOR/NXT] NXT 여부 JSON 파싱 실패: {code}");
                        return NxtResolveResult.Failed("JSON_PARSE_FAILED");
                    }

                    string returnCode = ReadSorJsonText(json, "return_code", "returnCode");
                    string returnMsg = ReadSorJsonText(json, "return_msg", "returnMsg", "msg");

                    if (!string.IsNullOrWhiteSpace(returnCode) && returnCode != "0")
                    {
                        Log($"⚠️ [SOR/NXT] NXT 여부 응답 오류: {code} / code={returnCode} / msg={returnMsg}");
                        return NxtResolveResult.Failed("RETURN_CODE_ERROR");
                    }

                    string nxtText = ReadSorJsonText(json,
                        "nxtEnable",
                        "nxt_enable",
                        "nxt_yn",
                        "nxtYn",
                        "nxt_trde_psbl_yn",
                        "nxtTradePossibleYn");

                    if (string.IsNullOrWhiteSpace(nxtText))
                    {
                        Log($"⚠️ [SOR/NXT] NXT 여부 필드 없음: {code}");
                        return NxtResolveResult.Failed("NXT_FIELD_MISSING");
                    }

                    string normalizedNxtText = nxtText.Trim();
                    bool enabled = string.Equals(nxtText, "Y", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(nxtText, "1", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(nxtText, "true", StringComparison.OrdinalIgnoreCase);

                    bool disabled = string.Equals(normalizedNxtText, "N", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(normalizedNxtText, "0", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(normalizedNxtText, "false", StringComparison.OrdinalIgnoreCase);

                    if (!enabled && !disabled)
                    {
                        Log($"⚠️ [SOR/NXT] NXT 여부 알 수 없는 값: {code} / value={nxtText}");
                        return NxtResolveResult.Failed("NXT_FIELD_UNKNOWN");
                    }

                    _nxtEnableCache[code] = enabled;
                    TryLogNxtProbeResult(code, enabled ? "Y" : "N");
                    return NxtResolveResult.Known(enabled, "ka10100");
                }
                finally
                {
                    _nxtEnableRequestGate.Release();
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ [SOR/NXT] NXT 여부 조회 실패: {code} / {ex.Message}");
                return NxtResolveResult.Failed("EXCEPTION");
            }
        }

        private string ReadSorJsonText(JToken token, params string[] keys)
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
                    string value = ReadSorJsonText(prop.Value, keys);

                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }

            if (token is JArray arr)
            {
                foreach (JToken item in arr)
                {
                    string value = ReadSorJsonText(item, keys);

                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }

            return "";
        }

        private void TryLogNxtProbeResult(string code, string state)
        {
            code = NormalizeStockCode(code);
            state = string.IsNullOrWhiteSpace(state) ? "?" : state.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(code))
                return;

            bool shouldLog = false;
            DateTime now = DateTime.Now;

            lock (_nxtProbeLogLock)
            {
                _nxtProbeLastLoggedState.TryGetValue(code, out string prevState);
                _nxtProbeLastLoggedAt.TryGetValue(code, out DateTime prevAt);

                if (!string.Equals(prevState, state, StringComparison.OrdinalIgnoreCase))
                    shouldLog = true;
                else if ((now - prevAt).TotalSeconds >= 30)
                    shouldLog = true;

                if (shouldLog)
                {
                    _nxtProbeLastLoggedState[code] = state;
                    _nxtProbeLastLoggedAt[code] = now;
                }
            }

            if (shouldLog)
                Log($"🔎 [SOR/NXT] {code} / NXT={state}");
        }

        private sealed class NxtResolveResult
        {
            public bool IsKnown { get; init; }
            public bool IsNxtEnabled { get; init; }
            public string Source { get; init; } = "";
            public string FailureReason { get; init; } = "";

            public static NxtResolveResult Known(bool enabled, string source)
            {
                return new NxtResolveResult
                {
                    IsKnown = true,
                    IsNxtEnabled = enabled,
                    Source = string.IsNullOrWhiteSpace(source) ? "ka10100" : source
                };
            }

            public static NxtResolveResult Failed(string reason)
            {
                return new NxtResolveResult
                {
                    IsKnown = false,
                    FailureReason = string.IsNullOrWhiteSpace(reason) ? "UNKNOWN" : reason
                };
            }
        }
    }
}
