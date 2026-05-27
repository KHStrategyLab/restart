#nullable disable
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace KHStrategyLab.Models
{
    public class StockGridRow : INotifyPropertyChanged
    {
        private string _code = "";
        private string _name = "";
        private long _currentPrice;
        private long _buyPrice;
        private long _volume;
        private string _volumeText = "";
        private long _stopLossPrice;
        private string _tradingValueText = "";
        private string _turnoverRateText = "";
        private string _changeRateText = "";
        private string _profitRateText = "0.00%";
        private Brush _priceColor = Brushes.White;
        private Brush _profitColor = Brushes.White;

        // Thin MA Signal 표시용 필드.
        // 추적중인 매수후보 그리드에서 10분봉 5/20/60선 숫자와 신호등을 보여준다.
        private double _ma5Value;
        private double _ma20Value;
        private double _ma60Value;
        private string _ma5Text = "-";
        private string _ma20Text = "-";
        private string _ma60Text = "-";
        private string _maSignalText = "-";
        private Brush _maSignalBrush = Brushes.White;
        private string _baseCandleGradeText = "-";
        private Brush _baseCandleGradeBrush = Brushes.White;

        public string Code
        {
            get => _code;
            set { if (_code == value) return; _code = value; OnPropertyChanged(); }
        }

        public string Name
        {
            get => _name;
            set { if (_name == value) return; _name = value; OnPropertyChanged(); }
        }

        public long CurrentPrice
        {
            get => _currentPrice;
            set { if (_currentPrice == value) return; _currentPrice = value; OnPropertyChanged(); }
        }

        public long BuyPrice
        {
            get => _buyPrice;
            set { if (_buyPrice == value) return; _buyPrice = value; OnPropertyChanged(); }
        }

        public long Volume
        {
            get => _volume;
            set { if (_volume == value) return; _volume = value; OnPropertyChanged(); }
        }

        public string VolumeText
        {
            get => _volumeText;
            set { if (_volumeText == value) return; _volumeText = value; OnPropertyChanged(); }
        }

        public long StopLossPrice
        {
            get => _stopLossPrice;
            set { if (_stopLossPrice == value) return; _stopLossPrice = value; OnPropertyChanged(); }
        }

        public string TradingValueText
        {
            get => _tradingValueText;
            set { if (_tradingValueText == value) return; _tradingValueText = value; OnPropertyChanged(); }
        }

        public string TurnoverRateText
        {
            get => _turnoverRateText;
            set { if (_turnoverRateText == value) return; _turnoverRateText = value; OnPropertyChanged(); }
        }

        public string ChangeRateText
        {
            get => _changeRateText;
            set { if (_changeRateText == value) return; _changeRateText = value; OnPropertyChanged(); }
        }

        public string ProfitRateText
        {
            get => _profitRateText;
            set { if (_profitRateText == value) return; _profitRateText = value; OnPropertyChanged(); }
        }

        public Brush ProfitColor
        {
            get => _profitColor;
            set { if (_profitColor == value) return; _profitColor = value; OnPropertyChanged(); }
        }

        public Brush PriceColor
        {
            get => _priceColor;
            set { if (_priceColor == value) return; _priceColor = value; OnPropertyChanged(); }
        }

        public double Ma5Value
        {
            get => _ma5Value;
            set { if (_ma5Value == value) return; _ma5Value = value; OnPropertyChanged(); }
        }

        public double Ma20Value
        {
            get => _ma20Value;
            set { if (_ma20Value == value) return; _ma20Value = value; OnPropertyChanged(); }
        }

        public double Ma60Value
        {
            get => _ma60Value;
            set { if (_ma60Value == value) return; _ma60Value = value; OnPropertyChanged(); }
        }

        public string Ma5Text
        {
            get => _ma5Text;
            set { if (_ma5Text == value) return; _ma5Text = value; OnPropertyChanged(); }
        }

        public string Ma20Text
        {
            get => _ma20Text;
            set { if (_ma20Text == value) return; _ma20Text = value; OnPropertyChanged(); }
        }

        public string Ma60Text
        {
            get => _ma60Text;
            set { if (_ma60Text == value) return; _ma60Text = value; OnPropertyChanged(); }
        }

        public string MaSignalText
        {
            get => _maSignalText;
            set { if (_maSignalText == value) return; _maSignalText = value; OnPropertyChanged(); }
        }

        public Brush MaSignalBrush
        {
            get => _maSignalBrush;
            set { if (_maSignalBrush == value) return; _maSignalBrush = value; OnPropertyChanged(); }
        }

        public string BaseCandleGradeText
        {
            get => _baseCandleGradeText;
            set { if (_baseCandleGradeText == value) return; _baseCandleGradeText = value; OnPropertyChanged(); }
        }

        public Brush BaseCandleGradeBrush
        {
            get => _baseCandleGradeBrush;
            set { if (_baseCandleGradeBrush == value) return; _baseCandleGradeBrush = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public class HoldingStock : StockGridRow
    {
    }

    public class RankStock : StockGridRow
    {
        private int _rank;

        public int Rank
        {
            get => _rank;
            set { if (_rank == value) return; _rank = value; OnPropertyChanged(); }
        }
    }
}
