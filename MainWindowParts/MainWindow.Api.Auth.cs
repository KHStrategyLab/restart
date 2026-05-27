#nullable disable

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private async Task AutoLoginAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_appKey) || string.IsNullOrWhiteSpace(_secretKey))
                {
                    UpdateStatus("설정 대기", Brushes.Gold);
                    Log("⚠️ [로그인] AppKey / SecretKey 입력 전입니다.");
                    return;
                }

                UpdateStatus("토큰 요청", Brushes.Gold);
                Log("🔐 [로그인] 키움 REST 토큰 발급 요청");

                string url = "https://api.kiwoom.com/oauth2/token";

                var body = new
                {
                    grant_type = "client_credentials",
                    appkey = _appKey,
                    secretkey = _secretKey
                };

                string jsonBody = JsonConvert.SerializeObject(body);

                using var content = new StringContent(
                    jsonBody,
                    Encoding.UTF8,
                    "application/json"
                );

                using HttpResponseMessage response = await _http.PostAsync(url, content);
                string responseText = await response.Content.ReadAsStringAsync();

                await SaveRawAsync("oauth_token", responseText);

                if (!response.IsSuccessStatusCode)
                {
                    UpdateStatus("로그인 실패", Brushes.OrangeRed);
                    Log($"❌ [로그인 실패] HTTP {(int)response.StatusCode} / {response.ReasonPhrase}");
                    Log($"❌ [응답] {responseText}");
                    return;
                }

                JObject result = JObject.Parse(responseText);

                string token = result["token"]?.ToString() ?? "";
                string tokenType = result["token_type"]?.ToString() ?? "";
                string expiresDt = result["expires_dt"]?.ToString() ?? "";

                if (string.IsNullOrWhiteSpace(token))
                {
                    UpdateStatus("토큰 없음", Brushes.OrangeRed);
                    Log($"❌ [로그인 실패] 응답에 token이 없습니다: {responseText}");
                    return;
                }

                _token = token;

                UpdateStatus("로그인 완료", Brushes.LightGreen);
                Log($"✅ [로그인] 토큰 발급 완료 / type={tokenType} / 만료={expiresDt}");

                await FetchAccountAsync();
                await InitializeConditionWebSocketAsync();
                StartDailyTrackedMarketRecheck();

                // 로그인 직후 0198 TOP20을 한 번 바로 채운다.
                // 이후에는 Timer가 15초마다 갱신한다.
                _ = RefreshKiwoomRealtimeTop20Async(true);
            }
            catch (Exception ex)
            {
                UpdateStatus("로그인 오류", Brushes.OrangeRed);
                Log($"❌ [로그인 오류] {ex.Message}");
            }
        }
    }
}
