#nullable disable
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private async Task SendTelegramMessageAsync(string message)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_telegramToken) || string.IsNullOrWhiteSpace(_telegramChatId))
                {
                    Log("⚠️ [텔레그램] TelegramToken 또는 TelegramChatId가 비어 있어 알림을 보내지 않았습니다.");
                    return;
                }

                string[] chatIds = [.. _telegramChatId
                    .Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct()];

                if (chatIds.Length == 0)
                {
                    Log("⚠️ [텔레그램] 전송 가능한 ChatId가 없습니다.");
                    return;
                }

                string url = $"https://api.telegram.org/bot{_telegramToken}/sendMessage";

                foreach (string chatId in chatIds)
                {
                    var payload = new
                    {
                        chat_id = chatId,
                        text = message,
                        disable_web_page_preview = true
                    };

                    using var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                    using HttpResponseMessage response = await _http.PostAsync(url, content);
                    string body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        Log($"⚠️ [텔레그램] 전송 실패: {(int)response.StatusCode} / {body}");
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"❌ [텔레그램 오류] {ex.Message}");
            }
        }
    }
}
