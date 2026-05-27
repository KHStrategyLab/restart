#nullable disable
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Text;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private void LoadConfig()
        {
            try
            {
                if (!File.Exists(_configPath))
                {
                    var cfg = CreateDefaultConfig();
                    File.WriteAllText(_configPath, cfg.ToString(Formatting.Indented), Encoding.UTF8);
                    Log("⚠️ [설정] config.json 생성됨. 키 입력 후 재실행 필요");

                    _entryBudget = ParsePositiveLongConfig(cfg["Budget"]?.ToString(), 300000);
                    _maxSlots = ParsePositiveIntConfig(cfg["MaxSlots"]?.ToString(), 3);
                    InputAmount.Text = _entryBudget.ToString();
                    InputMaxSlots.Text = _maxSlots.ToString();
                    bool defaultAutoBuy = cfg["AutoBuy"]?.ToObject<bool>() ?? false;
                    bool defaultLiveOrder = cfg["LiveOrderEnabled"]?.ToObject<bool>() ?? defaultAutoBuy;
                    ChkAutoBuy.IsChecked = defaultLiveOrder;
                    ApplyLiveOrderConfig(cfg);
                    ApplySettingsInputLock(false);
                    return;
                }

                JObject config = JObject.Parse(File.ReadAllText(_configPath, Encoding.UTF8));

                _entryBudget = ParsePositiveLongConfig(config["Budget"]?.ToString(), 300000);
                _maxSlots = ParsePositiveIntConfig(config["MaxSlots"]?.ToString(), 3);
                InputAmount.Text = _entryBudget.ToString();
                InputMaxSlots.Text = _maxSlots.ToString();
                bool autoBuy = config["AutoBuy"]?.ToObject<bool>() ?? false;
                bool liveOrder = config["LiveOrderEnabled"]?.ToObject<bool>() ?? autoBuy;
                ChkAutoBuy.IsChecked = liveOrder;

                _appKey = (config["AppKey"]?.ToString() ?? "").Trim();
                _secretKey = (config["SecretKey"]?.ToString() ?? "").Trim();
                _telegramToken = (config["TelegramToken"]?.ToString() ?? "").Trim();
                _telegramChatId = (config["TelegramChatId"]?.ToString() ?? "").Trim();
                _targetConditionSeq00 = config["ConditionSeq00"]?.ToString()?.Trim() ?? "0";
                _ignoredConditionSeq01 = config["ConditionSeq01"]?.ToString()?.Trim() ?? "1";
                ApplyLiveOrderConfig(config);

                ApplySettingsInputLock(_isHunting);
                Log($"✅ [설정] 진입예산 {InputAmount.Text}원 / 슬롯 {InputMaxSlots.Text}개 로드");
            }
            catch (Exception ex)
            {
                Log($"❌ [설정 로드 오류] {ex.Message}");
                ApplySettingsInputLock(false);
            }
        }

        private JObject CreateDefaultConfig()
        {
            return new JObject
            {
                ["Budget"] = "300000",
                ["MaxSlots"] = "3",
                ["AutoBuy"] = false,
                ["LiveOrderEnabled"] = false,
                ["OneShareLiveOrderTestMode"] = false,
                ["ConditionSeq00"] = "0",
                ["ConditionSeq01"] = "1",
                ["AppKey"] = "",
                ["SecretKey"] = "",
                ["TelegramToken"] = "",
                ["TelegramChatId"] = ""
            };
        }

        private bool SaveTradingSettingsFromUi()
        {
            try
            {
                string budgetText = NormalizePositiveNumberText(InputAmount.Text, "300000");
                string maxSlotsText = NormalizePositiveNumberText(InputMaxSlots.Text, "3");

                if (!long.TryParse(budgetText, out long budget) || budget <= 0)
                {
                    Log("❌ [설정 저장 실패] 진입예산은 1원 이상 숫자로 입력해야 합니다.");
                    return false;
                }

                if (!int.TryParse(maxSlotsText, out int maxSlots) || maxSlots <= 0)
                {
                    Log("❌ [설정 저장 실패] 슬롯제한은 1개 이상 숫자로 입력해야 합니다.");
                    return false;
                }

                JObject config;
                if (File.Exists(_configPath))
                {
                    try
                    {
                        config = JObject.Parse(File.ReadAllText(_configPath, Encoding.UTF8));
                    }
                    catch
                    {
                        config = CreateDefaultConfig();
                    }
                }
                else
                {
                    config = CreateDefaultConfig();
                }

                config["Budget"] = budgetText;
                config["MaxSlots"] = maxSlotsText;
                _entryBudget = budget;
                _maxSlots = maxSlots;

                _liveOrderEnabled = ChkAutoBuy.IsChecked == true;
                config["AutoBuy"] = _liveOrderEnabled;
                config["LiveOrderEnabled"] = _liveOrderEnabled;
                config["OneShareLiveOrderTestMode"] = _oneShareLiveOrderTestMode;

                // 기존 인증/텔레그램/조건검색 설정은 절대 날리지 않는다.
                if (config["ConditionSeq00"] == null) config["ConditionSeq00"] = _targetConditionSeq00 ?? "0";
                if (config["ConditionSeq01"] == null) config["ConditionSeq01"] = _ignoredConditionSeq01 ?? "1";
                if (config["AppKey"] == null) config["AppKey"] = _appKey ?? "";
                if (config["SecretKey"] == null) config["SecretKey"] = _secretKey ?? "";
                if (config["TelegramToken"] == null) config["TelegramToken"] = _telegramToken ?? "";
                if (config["TelegramChatId"] == null) config["TelegramChatId"] = _telegramChatId ?? "";

                File.WriteAllText(_configPath, config.ToString(Formatting.Indented), Encoding.UTF8);

                InputAmount.Text = budgetText;
                InputMaxSlots.Text = maxSlotsText;

                Log($"💾 [설정저장] 진입예산 {budget:N0}원 / 슬롯 {maxSlots}개 / 자동매매 {(ChkAutoBuy.IsChecked == true ? "ON" : "OFF")}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"❌ [설정 저장 오류] {ex.Message}");
                return false;
            }
        }

        private void ApplySettingsInputLock(bool locked)
        {
            try
            {
                InputAmount.IsReadOnly = locked;
                InputMaxSlots.IsReadOnly = locked;
                InputAmount.IsTabStop = !locked;
                InputMaxSlots.IsTabStop = !locked;
                ChkAutoBuy.IsEnabled = !locked;

                double opacity = locked ? 0.65 : 1.0;
                InputAmount.Opacity = opacity;
                InputMaxSlots.Opacity = opacity;
                ChkAutoBuy.Opacity = opacity;
            }
            catch
            {
                // XAML 초기화 전/종료 중에는 조용히 무시한다.
            }
        }

        private void ApplyLiveOrderConfig(JObject config)
        {
            _liveOrderEnabled = config?["LiveOrderEnabled"]?.ToObject<bool>() ?? false;
            _oneShareLiveOrderTestMode = config?["OneShareLiveOrderTestMode"]?.ToObject<bool>() ?? false;

            Log(_liveOrderEnabled
                ? "[실주문상태] LiveOrderEnabled=ON / KRX=KRX 주문 / NXT가능종목=SOR 주문"
                : "[실주문상태] LiveOrderEnabled=OFF / 신호만 발생 / 주문전송 없음");
        }

        private string NormalizePositiveNumberText(string value, string fallback)
        {
            string digits = NumberOnlyRegex().Replace(value ?? "", "").Trim();
            if (string.IsNullOrWhiteSpace(digits)) return fallback;

            digits = digits.TrimStart('0');
            return string.IsNullOrWhiteSpace(digits) ? fallback : digits;
        }

        private long ParsePositiveLongConfig(string value, long fallback)
        {
            string text = NormalizePositiveNumberText(value, fallback.ToString());
            return long.TryParse(text, out long parsed) && parsed > 0 ? parsed : fallback;
        }

        private int ParsePositiveIntConfig(string value, int fallback)
        {
            string text = NormalizePositiveNumberText(value, fallback.ToString());
            return int.TryParse(text, out int parsed) && parsed > 0 ? parsed : fallback;
        }
    }
}
