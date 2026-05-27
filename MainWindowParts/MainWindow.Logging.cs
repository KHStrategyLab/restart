#nullable disable

using System;
using System.Threading.Tasks;
using System.Windows.Documents;
using System.Windows.Media;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private const int MaxLogLineCount = 500;

        private void Log(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";

            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (LogParagraph == null) return;

                    LogParagraph.Inlines.Add(new Run(line + Environment.NewLine)
                    {
                        Foreground = GetLogBrush(message)
                    });

                    TrimLogLines();
                    TxtLog?.ScrollToEnd();
                });
            }
            catch
            {
            }
        }

        private void TrimLogLines()
        {
            try
            {
                while (LogParagraph.Inlines.Count > MaxLogLineCount)
                {
                    Inline first = LogParagraph.Inlines.FirstInline;
                    if (first == null) break;
                    LogParagraph.Inlines.Remove(first);
                }
            }
            catch
            {
            }
        }

        private Brush GetLogBrush(string message)
        {
            if (message.Contains("❌")) return Brushes.LightCoral;
            if (message.Contains("⚠️")) return Brushes.Gold;
            if (message.Contains("✅")) return Brushes.LightGreen;
            if (message.Contains("📌") || message.Contains("📈") || message.Contains("📡") || message.Contains("💹")) return Brushes.DeepSkyBlue;
            if (message.Contains("▶") || message.Contains("🚀")) return Brushes.White;
            if (message.Contains("■")) return Brushes.LightGray;
            return Brushes.White;
        }

        // 기존 API/WS 코드의 SaveRawAsync 호출 호환용.
        // 점검용 원문 파일은 생성하지 않고 즉시 완료한다.
        private Task SaveRawAsync(string name, string body)
        {
            return Task.CompletedTask;
        }
    }
}
