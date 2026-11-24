using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace WarfAssistant.Models
{
    public sealed class WfmItemSummary : INotifyPropertyChanged
    {
        private BitmapImage? _iconBitmap;
        public event PropertyChangedEventHandler? PropertyChanged;

        [JsonPropertyName("slug")] public string Slug { get; set; } = string.Empty;
        [JsonPropertyName("ref")] public string GameRef { get; set; } = string.Empty;
        [JsonPropertyName("ru")] public string NameRu { get; set; } = string.Empty;
        [JsonPropertyName("en")] public string NameEn { get; set; } = string.Empty;
        [JsonPropertyName("icon")] public string IconPath { get; set; } = string.Empty;
        [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
        [JsonPropertyName("ducats")] public int Ducats { get; set; }

        [JsonIgnore] public string DisplayName => !string.IsNullOrEmpty(NameRu) ? $"{NameRu} ({NameEn})" : NameEn;
        [JsonIgnore] public bool IsPrimeSet => Tags.Contains("set");
        [JsonIgnore] public string? IconUrl => string.IsNullOrEmpty(IconPath) ? null : $"https://warframe.market/static/assets/{IconPath}";
        [JsonIgnore] public bool HasDucats => Ducats > 0;
        [JsonIgnore] public string DucatTag => Ducats > 0 ? $"🪙 {Ducats}" : string.Empty;

        [JsonIgnore]
        public BitmapImage? IconBitmap
        {
            get
            {
                if (_iconBitmap != null) return _iconBitmap;
                if (!string.IsNullOrEmpty(IconUrl))
                {
                    Services.ImageCacheService.Instance.RequestUrlBitmap(IconUrl, bmp =>
                    {
                        _iconBitmap = bmp;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconBitmap)));
                    }, 96);
                }
                return null;
            }
        }

        public override string ToString() => DisplayName;
    }

    public sealed class WfmOrderEntry
    {
        public string OrderType { get; set; } = "sell"; // sell / buy
        public int Platinum { get; set; }
        public int Quantity { get; set; } = 1;
        public int ModRank { get; set; }
        public bool HasRank { get; set; }
        public string UserIngameName { get; set; } = string.Empty;
        public string UserStatus { get; set; } = "ingame";
        public int Reputation { get; set; }
        public string ItemNameForWhisper { get; set; } = string.Empty;

        public string WhisperCommand => OrderType == "sell"
            ? $"/w {UserIngameName} Hi! I want to buy: \"{ItemNameForWhisper}\" for {Platinum} platinum. (warframe.market)"
            : $"/w {UserIngameName} Hi! I want to sell: \"{ItemNameForWhisper}\" for {Platinum} platinum. (warframe.market)";
        public string RankLabel => HasRank ? $"R{ModRank}" : "";
    }

    public sealed class MarketSetPartPrice
    {
        public string Name { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public int MinSellPrice { get; set; }
        public int Count { get; set; } = 1;
    }

    public sealed class MarketItemAnalysis
    {
        public WfmItemSummary? Item { get; set; }
        public int MinSellPrice { get; set; }
        public int MedianSellPrice { get; set; }
        public int MaxBuyPrice { get; set; }
        public double Avg48hPrice { get; set; }
        public int Volume48h { get; set; }
        public double Avg90dPrice { get; set; }
        public int Volume90d { get; set; }
        public double PlatPerDucat { get; set; }
        public string? ArbitrageSummary { get; set; }
        public bool HasArbitrage => !string.IsNullOrEmpty(ArbitrageSummary);
        public List<WfmOrderEntry> SellOrders { get; set; } = new();
        public List<WfmOrderEntry> BuyOrders { get; set; } = new();
        public List<MarketSetPartPrice> SetParts { get; set; } = new();
    }

    public sealed class DucatArbitrageEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public int Ducats { get; set; }
        public int MinSellPrice { get; set; }
        public double DucatsPerPlat => MinSellPrice > 0 ? Math.Round((double)Ducats / MinSellPrice, 1) : 0;
        public string RatioDisplay => $"{DucatsPerPlat:F1} Дук/Пл ({Ducats} дук. за {MinSellPrice} Пл)";
    }

    public sealed class RivenVariantOption
    {
        public string Name { get; set; } = string.Empty;
        public double Disposition { get; set; } = 1.0;
        public string DisplayName => $"{Name} ({Disposition:F2})";
        public override string ToString() => DisplayName;
    }

    public sealed class MarketWatchlistItem
    {
        public string Slug { get; set; } = string.Empty;
        public string NameRu { get; set; } = string.Empty;
        public string NameEn { get; set; } = string.Empty;
        public int LastMinSell { get; set; }
        public int TargetPrice { get; set; }
        public string UpdatedAt { get; set; } = string.Empty;
    }

    // ==========================================
    // STAGE 6: RIVEN HELPER MODELS
    // ==========================================
    public sealed class RivenWeaponOption : INotifyPropertyChanged
    {
        private BitmapImage? _iconBitmap;
        public event PropertyChangedEventHandler? PropertyChanged;

        [JsonPropertyName("slug")] public string Slug { get; set; } = string.Empty;
        [JsonPropertyName("ref")] public string GameRef { get; set; } = string.Empty;
        [JsonPropertyName("ru")] public string NameRu { get; set; } = string.Empty;
        [JsonPropertyName("en")] public string NameEn { get; set; } = string.Empty;
        [JsonPropertyName("grp")] public string Group { get; set; } = string.Empty;
        [JsonPropertyName("rtype")] public string RivenType { get; set; } = "rifle";
        [JsonPropertyName("disp")] public double Disposition { get; set; } = 1.0;
        [JsonPropertyName("mr")] public int ReqMr { get; set; } = 8;

        [JsonIgnore] public string? IconUrl { get; set; }
        [JsonIgnore] public bool HasLoadedIcon => _iconBitmap != null;
        [JsonIgnore]
        public BitmapImage? IconBitmap => IconLoaderHelper.GetOrRequest(IconUrl, ref _iconBitmap, bmp =>
        {
            _iconBitmap = bmp;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconBitmap)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasLoadedIcon)));
        }, decodeWidth: 96);

        [JsonIgnore] public string DisplayName => !string.IsNullOrEmpty(NameRu) && !NameRu.Equals(NameEn, StringComparison.OrdinalIgnoreCase)
            ? $"{NameRu} ({NameEn})"
            : NameEn;

        [JsonIgnore] public string DispositionDots
        {
            get
            {
                int filled = Disposition switch
                {
                    >= 1.31 => 5,
                    >= 1.11 => 4,
                    >= 0.90 => 3,
                    >= 0.70 => 2,
                    _ => 1
                };
                return new string('●', filled) + new string('○', 5 - filled) + $" ({Disposition:F2})";
            }
        }

        public override string ToString() => DisplayName;
    }

    public sealed class RivenAttrOption
    {
        [JsonPropertyName("slug")] public string Slug { get; set; } = string.Empty;
        [JsonPropertyName("ru")] public string EffectRu { get; set; } = string.Empty;
        [JsonPropertyName("en")] public string EffectEn { get; set; } = string.Empty;
        [JsonPropertyName("posOnly")] public bool PositiveOnly { get; set; }
        [JsonPropertyName("negOnly")] public bool NegativeOnly { get; set; }
        [JsonPropertyName("excl")] public List<string> ExclusiveTo { get; set; } = new();
        [JsonPropertyName("unit")] public string Unit { get; set; } = "percent";

        public override string ToString() => EffectRu;
    }

    public sealed class RivenStatSlot : INotifyPropertyChanged
    {
        private RivenAttrOption? _selectedAttr;
        private double _value;
        private double _minRange;
        private double _maxRange;
        private bool _isSlotActive = true;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        public int Index { get; set; }
        public bool IsNegative { get; set; }
        public string SlotLabel => IsNegative ? "МИНУС (ПРОКЛЯТИЕ)" : $"БАФ #{Index}";
        public Action? OnUpdated { get; set; }

        public bool IsSlotActive
        {
            get => _isSlotActive;
            set { if (_isSlotActive != value) { _isSlotActive = value; OnChanged(); } }
        }

        public RivenAttrOption? SelectedAttr
        {
            get => _selectedAttr;
            set { _selectedAttr = value; OnChanged(); OnUpdated?.Invoke(); }
        }

        public double Value
        {
            get => _value;
            set { _value = value; OnChanged(); OnChanged(nameof(QualityPercent)); OnChanged(nameof(QualityColor)); OnUpdated?.Invoke(); }
        }

        public double MinRange { get => _minRange; set { _minRange = value; OnChanged(); OnChanged(nameof(RangeText)); OnChanged(nameof(QualityPercent)); } }
        public double MaxRange { get => _maxRange; set { _maxRange = value; OnChanged(); OnChanged(nameof(RangeText)); OnChanged(nameof(QualityPercent)); } }

        public string RangeText => MaxRange != 0 || MinRange != 0 ? $"Диапазон: {MinRange:F1} .. {MaxRange:F1}" : "Не задано";
        public double QualityPercent
        {
            get
            {
                double a = Math.Abs(Value);
                double lo = Math.Min(Math.Abs(MinRange), Math.Abs(MaxRange));
                double hi = Math.Max(Math.Abs(MinRange), Math.Abs(MaxRange));
                if (hi <= lo || a <= 0) return 0;
                return Math.Clamp((a - lo) / (hi - lo) * 100.0, 0, 100);
            }
        }

        public string QualityColor => QualityPercent >= 75 ? "#10B981" : (QualityPercent >= 40 ? "#FBBF24" : "#F87171");
    }

    public sealed class RivenEvaluationResult
    {
        public string Grade { get; set; } = "B";
        public string GradeColor { get; set; } = "#38BDF8";
        public int Score { get; set; }
        public string Verdict { get; set; } = "Выберите характеристики мода";
        public string KuvaSummary { get; set; } = string.Empty;
        public string CurseVerdict { get; set; } = string.Empty;
        public string CurseColor { get; set; } = "#94A3B8";
        public Dictionary<string, double> BuilderStatMap { get; set; } = new();
    }

    public sealed class WeeklyRivenPriceStats
    {
        [JsonPropertyName("comp")] public string Compatibility { get; set; } = string.Empty;
        [JsonPropertyName("type")] public string ItemType { get; set; } = string.Empty;
        [JsonPropertyName("rerolled")] public bool Rerolled { get; set; }
        [JsonPropertyName("avg")] public double Avg { get; set; }
        [JsonPropertyName("med")] public double Median { get; set; }
        [JsonPropertyName("min")] public int Min { get; set; }
        [JsonPropertyName("max")] public int Max { get; set; }
        [JsonPropertyName("pop")] public int Popularity { get; set; }
    }

    public sealed class RivenAuctionListing
    {
        public int Price { get; set; }
        public bool IsDirectBuy { get; set; }
        public int Rerolls { get; set; }
        public int ModRank { get; set; }
        public int MasteryLevel { get; set; }
        public string OwnerName { get; set; } = string.Empty;
        public string OwnerStatus { get; set; } = string.Empty;
        public string AttributesText { get; set; } = string.Empty;
        public string Polarity { get; set; } = string.Empty;
        public string PriceBadge => IsDirectBuy ? $"💰 {Price} Пл" : $"🔨 Ставка {Price} Пл";
        public string WhisperText { get; set; } = string.Empty;
    }

    public sealed class VeiledChallengeEntry
    {
        public string Title { get; set; } = string.Empty;
        public string OptimalFrame { get; set; } = string.Empty;
        public string OptimalNode { get; set; } = string.Empty;
        public string Tip { get; set; } = string.Empty;
    }

    public sealed class SavedRivenEntry
    {
        public string WeaponSlug { get; set; } = string.Empty;
        public string WeaponName { get; set; } = string.Empty;
        public string Config { get; set; } = "3+ / 1-";
        public string Grade { get; set; } = "A";
        public int Rerolls { get; set; }
        public string StatsSummary { get; set; } = string.Empty;
        public string SavedAt { get; set; } = string.Empty;
    }

    public sealed class ActiveBuilderRiven
    {
        public string WeaponName { get; set; } = string.Empty;
        public string WeaponNameEn { get; set; } = string.Empty;
        public string RivenType { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public Dictionary<string, double> Stats { get; set; } = new();
    }
}
