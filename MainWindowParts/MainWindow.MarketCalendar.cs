#nullable disable

using Newtonsoft.Json.Linq;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private bool IsMarketAutoRefreshBusinessDay(DateTime now)
        {
            return !IsMarketClosedDate(now.Date);
        }

        private bool IsMarketClosedDate(DateTime date)
        {
            return IsWeekend(date) || GetMarketHolidayDates().Contains(date.ToString("yyyyMMdd"));
        }

        private string GetMarketClosedReason(DateTime date)
        {
            if (date.DayOfWeek == DayOfWeek.Saturday) return "토요일";
            if (date.DayOfWeek == DayOfWeek.Sunday) return "일요일";
            if (GetMarketHolidayDates().Contains(date.ToString("yyyyMMdd"))) return "휴장일";
            return "";
        }

        private HashSet<string> GetMarketHolidayDates()
        {
            DateTime now = DateTime.Now;
            if (_marketHolidayDates != null && (now - _marketHolidayDatesLoadedAt).TotalMinutes < 10)
                return _marketHolidayDates;

            HashSet<string> dates = [];
            AddBuiltInMarketHolidayDates(dates);

            try
            {
                if (File.Exists(_marketHolidayPath))
                {
                    string json = File.ReadAllText(_marketHolidayPath, Encoding.UTF8);
                    JToken token = JToken.Parse(json);
                    foreach (JToken item in FlattenMarketHolidayTokens(token))
                    {
                        string value = NormalizeMarketHolidayDate(item?.ToString() ?? "");
                        if (!string.IsNullOrWhiteSpace(value))
                            dates.Add(value);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ [휴장일] market_holidays.json 읽기 오류: {ex.Message}");
            }

            _marketHolidayDates = dates;
            _marketHolidayDatesLoadedAt = now;
            return _marketHolidayDates;
        }

        private void AddBuiltInMarketHolidayDates(HashSet<string> dates)
        {
            if (dates == null)
                return;

            // market_holidays.json이 아직 없을 때도 주요 휴장일은 안전하게 막는다.
            // 필요하면 Storage/market_holidays.json에 날짜를 추가해 확장한다.
            string[] builtInDates =
            [
                "20260525"
            ];

            foreach (string date in builtInDates)
                dates.Add(date);
        }

        private IEnumerable<JToken> FlattenMarketHolidayTokens(JToken token)
        {
            if (token == null) yield break;

            if (token is JArray arr)
            {
                foreach (JToken item in arr)
                {
                    foreach (JToken child in FlattenMarketHolidayTokens(item))
                        yield return child;
                }

                yield break;
            }

            if (token is JObject obj)
            {
                JToken holidays = obj["holidays"] ?? obj["dates"] ?? obj["closedDates"];
                if (holidays != null)
                {
                    foreach (JToken child in FlattenMarketHolidayTokens(holidays))
                        yield return child;
                }

                JToken date = obj["date"] ?? obj["dt"] ?? obj["일자"];
                if (date != null)
                    yield return date;

                yield break;
            }

            yield return token;
        }

        private string NormalizeMarketHolidayDate(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";

            string digits = new([.. value.Where(char.IsDigit)]);
            return digits.Length == 8 ? digits : "";
        }

        private bool IsWeekend(DateTime date)
        {
            return date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;
        }
    }
}
