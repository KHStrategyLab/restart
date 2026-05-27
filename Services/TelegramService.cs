using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace KHStrategyLab.Services;

public sealed class TelegramService
{
    private readonly HttpClient _http = new();
    private readonly AppLogger _logger;

    public string BotToken { get; set; } = string.Empty;
    public List<string> ChatIds { get; } = [];

    public TelegramService(AppLogger logger)
    {
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BotToken) && ChatIds.Count > 0;

    public async Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.Warn("텔레그램 설정이 비어 있어 메시지를 보내지 않았습니다.");
            return;
        }

        foreach (var chatId in ChatIds.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            await SendOneAsync(chatId, message, cancellationToken);
        }
    }

    private async Task SendOneAsync(string chatId, string message, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://api.telegram.org/bot{BotToken}/sendMessage";
            var payload = new
            {
                chat_id = chatId,
                text = message
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, content, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "텔레그램 전송 실패");
        }
    }
}
