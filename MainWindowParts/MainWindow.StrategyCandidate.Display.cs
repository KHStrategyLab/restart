#nullable disable

using System;

namespace KHStrategyLab
{
    public partial class MainWindow
    {
        private string ResolveStrategyCandidateTurnoverText(WatchCandidate candidate)
        {
            double? rate = candidate?.StockInfoTurnoverRatePercent;
            if (rate.HasValue && rate.Value > 0)
                return $"{rate.Value:0.00}%";

            return "-";
        }

        private bool IsStrategyMarketPlaceholderTurnoverText(string text)
        {
            text = (text ?? "").Trim();

            if (string.IsNullOrWhiteSpace(text))
                return true;

            if (text == "-" || text == "복원" || text == "조회중" || text == "시장확인중")
                return true;

            if (text.Contains("전략", StringComparison.OrdinalIgnoreCase))
                return true;

            if (text.Equals("KRX", StringComparison.OrdinalIgnoreCase) || text.Equals("NXT", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private void MergeStrategyCandidateStockInfo(WatchCandidate target, WatchCandidate source)
        {
            if (target == null || source == null)
                return;

            if (source.StockInfoChangeRatePercent.HasValue)
                target.StockInfoChangeRatePercent = source.StockInfoChangeRatePercent;

            if (source.StockInfoTurnoverRatePercent.HasValue && source.StockInfoTurnoverRatePercent.Value > 0)
                target.StockInfoTurnoverRatePercent = source.StockInfoTurnoverRatePercent;

            if (!string.IsNullOrWhiteSpace(source.StockInfoMarket))
                target.StockInfoMarket = source.StockInfoMarket;

            if (!string.IsNullOrWhiteSpace(source.StockInfoRequestCode))
                target.StockInfoRequestCode = source.StockInfoRequestCode;

            if (source.StockInfoCapturedAt.HasValue &&
                (target.StockInfoCapturedAt == null || source.StockInfoCapturedAt.Value > target.StockInfoCapturedAt.Value))
            {
                target.StockInfoCapturedAt = source.StockInfoCapturedAt;
            }
        }
    }
}
