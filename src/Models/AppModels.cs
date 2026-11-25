using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace WarfAssistant.Models
{
    internal static class IconLoaderHelper
    {
        public static BitmapImage? GetOrRequest(
            string? url,
            ref BitmapImage? backingField,
            Action<BitmapImage> setAndNotify,
            int decodeWidth = 96)
        {
            if (backingField != null) return backingField;
            if (string.IsNullOrWhiteSpace(url)) return null;
            var cached = Services.ImageCacheService.Instance.TryGetUrlFromMemory(url, decodeWidth);
            if (cached != null)
            {
                backingField = cached;
                System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() => setAndNotify(cached)));
                return cached;
            }
            Services.ImageCacheService.Instance.RequestUrlBitmap(url, setAndNotify, decodeWidth);
            return null;
        }
    }

    // ==========================================
    // CRAFTING & FARM MODELS
    // ==========================================
    public class CraftResource : INotifyPropertyChanged
    {
        private int _currentAmount;
        private int _requiredAmount;
        private string _amountInputText = "0";
        private bool _suppressEditCallback;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        private bool _isOptionalUtility;
        public string UniqueName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string SourceHint { get; set; } = string.Empty;
        public bool HasSourceHint => !string.IsNullOrEmpty(SourceHint);
        public Action<string, int>? OnAmountEditedCallback { get; set; }
        public bool IsOptionalUtility
        {
            get => _isOptionalUtility;
            set
            {
                if (_isOptionalUtility != value)
                {
                    _isOptionalUtility = value;
                    OnPropertyChanged();
                    NotifyComputed();
                }
            }
        }

        public int RequiredAmount
        {
            get => _requiredAmount;
            set
            {
                if (_requiredAmount != value)
                {
                    _requiredAmount = value;
                    OnPropertyChanged();
                    NotifyComputed();
                }
            }
        }

        public int CurrentAmount
        {
            get => _currentAmount;
            set
            {
                int clamped = Math.Max(0, value);
                if (_currentAmount != clamped)
                {
                    _currentAmount = clamped;
                    OnPropertyChanged();
                    NotifyComputed();
                }

                string txt = clamped.ToString();
                if (_amountInputText != txt)
                {
                    _suppressEditCallback = true;
                    _amountInputText = txt;
                    OnPropertyChanged(nameof(AmountInputText));
                    _suppressEditCallback = false;
                }
            }
        }

        /// <summary>
        /// Safe string proxy for TextBox editing so clearing digits doesn't throw binding validation errors.
        /// </summary>
        public string AmountInputText
        {
            get => _amountInputText;
            set
            {
                string next = value ?? string.Empty;
                if (_amountInputText == next) return;
                _amountInputText = next;
                OnPropertyChanged();
                if (!_suppressEditCallback && int.TryParse(next.Replace(" ", "").Replace(",", ""), out int parsed))
                {
                    int clamped = Math.Max(0, parsed);
                    OnAmountEditedCallback?.Invoke(Name, clamped);
                }
            }
        }

        public void SetAmountSilently(int value)
        {
            _suppressEditCallback = true;
            CurrentAmount = value;
            _suppressEditCallback = false;
        }

        private void NotifyComputed()
        {
            OnPropertyChanged(nameof(DeficitAmount));
            OnPropertyChanged(nameof(IsSufficient));
            OnPropertyChanged(nameof(StatusColor));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(DeficitText));
            OnPropertyChanged(nameof(ProgressPercent));
        }

        private BitmapImage? _iconBitmap;
        public string? IconUrl { get; set; }
        public bool HasLoadedIcon => _iconBitmap != null;
        public BitmapImage? IconBitmap => IconLoaderHelper.GetOrRequest(IconUrl, ref _iconBitmap, bmp =>
        {
            _iconBitmap = bmp;
            OnPropertyChanged(nameof(IconBitmap));
            OnPropertyChanged(nameof(HasLoadedIcon));
        }, decodeWidth: 56);

        public string IconText { get; set; } = "💎";
        public int DeficitAmount => Math.Max(0, RequiredAmount - CurrentAmount);
        public bool IsSufficient => CurrentAmount >= RequiredAmount;
        public string StatusColor => IsOptionalUtility && RequiredAmount == 0 && CurrentAmount == 0
            ? "#38BDF8"
            : (IsSufficient ? "#10B981" : "#EF4444");
        public string StatusText => IsOptionalUtility && RequiredAmount == 0 && CurrentAmount == 0
            ? "В разборе (0)"
            : (IsSufficient ? "✓ Собрано" : $"Не хватает: {DeficitAmount:N0}");
        public string DeficitText => DeficitAmount > 0 ? $"-{DeficitAmount:N0}" : "0";
        public double ProgressPercent => IsOptionalUtility && RequiredAmount == 0
            ? (CurrentAmount > 0 ? 100.0 : 0.0)
            : (RequiredAmount > 0 ? Math.Min(100.0, (double)CurrentAmount / RequiredAmount * 100.0) : 100.0);
    }

    public class CraftStepInfo
    {
        public string Name { get; set; } = string.Empty;
        public string CanonicalStockKey { get; set; } = string.Empty;
        public int Quantity { get; set; } = 1;
        public int BatchYield { get; set; } = 1;
        public int PreOwnedCount { get; set; }
        public int CraftHours { get; set; }
        public int CreditsCost { get; set; }
        public bool IsRoot { get; set; }
        public bool CanTogglePreOwned => !IsRoot && !string.IsNullOrEmpty(CanonicalStockKey);
        public bool IsPreOwnedComplete => PreOwnedCount >= Quantity && Quantity > 0;
        public string TimeShort => IsPreOwnedComplete
            ? "Склад"
            : (CraftHours >= 24 ? $"{CraftHours / 24}д" : (CraftHours == 0 ? "0ч" : $"{CraftHours}ч"));
        public string DisplayText => Quantity > 1 ? $"{Name} ×{Quantity} ({TimeShort})" : $"{Name} ({TimeShort})";
        public string PillToolTip => IsPreOwnedComplete
            ? "Деталь отмечена как уже готовая (ресурсы пропущены). Нажмите чтобы сбросить."
            : "Нажмите, если эта деталь уже скрафчена (исключит её материалы и кредиты из расчёта).";
    }

    public class CraftQueueItem : INotifyPropertyChanged
    {
        private int _quantity = 1;
        private BitmapImage? _thumbBitmap;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public string UniqueName { get; set; } = string.Empty;
        public string BlueprintUniqueName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;

        public int Quantity
        {
            get => _quantity;
            set
            {
                int clamped = Math.Clamp(value, 1, 99);
                if (_quantity != clamped)
                {
                    _quantity = clamped;
                    OnPropertyChanged();
                }
            }
        }

        public int CraftTimeHours { get; set; }
        public int TotalCreditsCost { get; set; }
        public string CreditsDisplay => TotalCreditsCost > 0 ? $"💳 {TotalCreditsCost:N0}" : string.Empty;
        public bool HasCreditsCost => TotalCreditsCost > 0;
        public string CraftTimeDisplay => CraftTimeHours >= 24 ? $"⏳ {CraftTimeHours / 24}д ({CraftTimeHours}ч)" : (CraftTimeHours == 0 ? "⚡ Мгновенно" : $"⏳ {CraftTimeHours}ч");
        public string IconGlyph { get; set; } = "🔨";
        public string? ThumbnailUrl { get; set; }
        public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailUrl);
        public bool HasLoadedThumbnail => _thumbBitmap != null;
        public BitmapImage? ThumbnailBitmap => IconLoaderHelper.GetOrRequest(ThumbnailUrl, ref _thumbBitmap, bmp =>
        {
            _thumbBitmap = bmp;
            OnPropertyChanged(nameof(ThumbnailBitmap));
            OnPropertyChanged(nameof(HasLoadedThumbnail));
        }, decodeWidth: 72);
        private string _relicHintText = string.Empty;
        private string _marketPriceHint = string.Empty;
        public bool HasRealRecipe { get; set; }
        public bool IsPrime { get; set; }
        public bool IsVaulted { get; set; }
        public string RelicHintText
        {
            get => _relicHintText;
            set { if (_relicHintText != value) { _relicHintText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasRelicHint)); } }
        }
        public bool HasRelicHint => !string.IsNullOrEmpty(RelicHintText);
        public string MarketPriceHint
        {
            get => _marketPriceHint;
            set { if (_marketPriceHint != value) { _marketPriceHint = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasMarketPriceHint)); } }
        }
        public bool HasMarketPriceHint => !string.IsNullOrEmpty(MarketPriceHint);
        public string RecipeSourceBadge => HasRealRecipe ? "DE Рецепт" : "Справочник";
        public List<CraftStepInfo> IntermediateSteps { get; set; } = new();
        public List<CraftStepInfo> NonRootSteps => IntermediateSteps.FindAll(s => !s.IsRoot);
        public bool HasIntermediateSteps => IntermediateSteps.Count > 1;
        public bool HasNonRootSteps => IntermediateSteps.Exists(s => !s.IsRoot);
        public string StepsSummaryText => IntermediateSteps.Count > 1
            ? $"Этапы ({IntermediateSteps.Count}): " + string.Join(" → ", IntermediateSteps.Take(4).Select(s => s.DisplayText)) + (IntermediateSteps.Count > 4 ? " …" : "")
            : string.Empty;
        public List<CraftResource> Resources { get; set; } = new();
        public bool IsAllResourcesReady => Resources.TrueForAll(r => r.IsSufficient);
        public string ReadyStatusText => IsAllResourcesReady ? "✓ Готово к сборке" : "Требуются ресурсы";
        public string ReadyStatusColor => IsAllResourcesReady ? "#10B981" : "#F59E0B";

        public void NotifyResourcesUpdated()
        {
            OnPropertyChanged(nameof(IsAllResourcesReady));
            OnPropertyChanged(nameof(ReadyStatusText));
            OnPropertyChanged(nameof(ReadyStatusColor));
        }
    }

    public class FarmStrategyStep
    {
        public int StepNumber { get; set; }
        public string Node { get; set; } = string.Empty;
        public string Planet { get; set; } = string.Empty;
        public string MissionType { get; set; } = string.Empty;
        public string CoversText { get; set; } = string.Empty;
        public string SetupHint { get; set; } = string.Empty;
        public string? FissureNote { get; set; }
        public bool HasFissureNote => !string.IsNullOrEmpty(FissureNote);
    }

    public class FarmNodeRecommendation : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public int RouteOrder { get; set; }
        public bool IsOptimalPick { get; set; }
        public string Node { get; set; } = string.Empty;
        public string Planet { get; set; } = string.Empty;
        public string MissionType { get; set; } = string.Empty;
        public List<string> MatchedResources { get; set; } = new();
        public int MatchedCount => MatchedResources.Count;
        public string RouteBadgeText => IsOptimalPick
            ? $"ШАГ #{RouteOrder} • ОПТИМАЛЬНО ({MatchedCount})"
            : (MatchedCount > 1 ? $"ПЕРЕСЕЧЕНИЕ: {MatchedCount}" : $"РЕСУРС: {MatchedResources.FirstOrDefault()}");
        public string CardBorderColor => IsOptimalPick ? "#10B981" : "#223044";
        public string BadgeBgColor => IsOptimalPick ? "#133325" : "#1C2636";
        public string BadgeFgColor => IsOptimalPick ? "#34D399" : "#00E5FF";
        public double CommunityRating { get; set; }
        public List<string> Tags { get; set; } = new();
        public string RecommendedSetup { get; set; } = string.Empty;
        public string FarmingTip { get; set; } = string.Empty;
        public bool SteelPathViable { get; set; }
        public string ActiveFissureBonus { get; set; } = string.Empty;
        public bool HasActiveFissure => !string.IsNullOrEmpty(ActiveFissureBonus);
        public string LfgCopyText => $"[Рекрутинг]: Ищу группу на {Node} ({Planet}) {MissionType} фарм {string.Join('/', MatchedResources)} (Nekros/Khora/Nova)";
    }

    // ==========================================
    // WORLD TIMERS & NOTIFICATIONS
    // ==========================================
    public class WorldCycleInfo : INotifyPropertyChanged
    {
        private string _state = string.Empty;
        private string _timeRemaining = string.Empty;
        private bool _notifyEnabled;
        private bool _isDayOrWarm;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public string Id { get; set; } = string.Empty;
        public string LocationName { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public DateTime ExpiryUtc { get; set; }

        public string State
        {
            get => _state;
            set { _state = value; OnPropertyChanged(); }
        }

        public string TimeRemaining
        {
            get => _timeRemaining;
            set { _timeRemaining = value; OnPropertyChanged(); }
        }

        public bool IsDayOrWarm
        {
            get => _isDayOrWarm;
            set { _isDayOrWarm = value; OnPropertyChanged(); }
        }

        public bool NotifyEnabled
        {
            get => _notifyEnabled;
            set { _notifyEnabled = value; OnPropertyChanged(); }
        }

        public string NotifyTarget { get; set; } = "Смена фазы";
        public string IconText { get; set; } = "☀";

        public string FullToolTip => string.IsNullOrEmpty(Subtitle)
            ? $"{LocationName}\nФаза: {State} ({TimeRemaining})"
            : $"{LocationName}\n{Subtitle}\nТекущая фаза: {State} • {NotifyTarget}";
    }

    // ==========================================
    // DUVIRI CIRCUIT & MOOD
    // ==========================================
    public class DuviriMoodTrackOption : INotifyPropertyChanged
    {
        private bool _isTracked;
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public string Key { get; set; } = string.Empty;
        public string NameRu { get; set; } = string.Empty;
        public string BuffShort { get; set; } = string.Empty;
        public Action? OnChangedCallback { get; set; }

        public bool IsTracked
        {
            get => _isTracked;
            set
            {
                if (_isTracked != value)
                {
                    _isTracked = value;
                    OnPropertyChanged();
                    OnChangedCallback?.Invoke();
                }
            }
        }
    }

    public class DuviriMoodInfo : INotifyPropertyChanged
    {
        private string _currentMood = "Печаль (Sorrow)";
        private string _timeRemaining = "00м 00с";
        private string _moodDescription = string.Empty;
        private bool _isTrackedActive;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public DateTime ExpiryUtc { get; set; }
        public string RawStateKey { get; set; } = "sorrow";

        public string CurrentMood
        {
            get => _currentMood;
            set { _currentMood = value; OnPropertyChanged(); }
        }

        public string TimeRemaining
        {
            get => _timeRemaining;
            set { _timeRemaining = value; OnPropertyChanged(); }
        }

        public string MoodDescription
        {
            get => _moodDescription;
            set { _moodDescription = value; OnPropertyChanged(); }
        }

        public bool IsTrackedActive
        {
            get => _isTrackedActive;
            set { _isTrackedActive = value; OnPropertyChanged(); }
        }
    }

    public class DuviriCircuitTile : INotifyPropertyChanged
    {
        private BitmapImage? _iconBitmap;
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; set; } = string.Empty;
        public string ShortLabel { get; set; } = string.Empty;
        public string? ImageUrl { get; set; }
        public bool IsIncarnon { get; set; }
        public bool HasLoadedIcon => _iconBitmap != null;

        public void SetImageUrl(string? url)
        {
            if (string.Equals(ImageUrl, url, StringComparison.Ordinal)) return;
            ImageUrl = url;
            _iconBitmap = null;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageUrl)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconBitmap)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasLoadedIcon)));
        }

        public BitmapImage? IconBitmap => IconLoaderHelper.GetOrRequest(ImageUrl, ref _iconBitmap, bmp =>
        {
            _iconBitmap = bmp;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconBitmap)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasLoadedIcon)));
        }, decodeWidth: 96);
    }

    public class DuviriRotationWeek : INotifyPropertyChanged
    {
        private bool _hasUserBookmark;
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public int WeekNumber { get; set; }
        public string DateRange { get; set; } = string.Empty;
        public string RelativeWeekTag { get; set; } = string.Empty;
        public bool HasRelativeTag => !string.IsNullOrEmpty(RelativeWeekTag);
        public bool IsCurrentWeek { get; set; }
        public List<string> NormalWarframes { get; set; } = new();
        public List<string> SteelPathIncarnons { get; set; } = new();
        public ObservableCollection<DuviriCircuitTile> WarframeTiles { get; } = new();
        public ObservableCollection<DuviriCircuitTile> IncarnonTiles { get; } = new();

        public bool HasUserBookmark
        {
            get => _hasUserBookmark;
            set { _hasUserBookmark = value; OnPropertyChanged(); }
        }

        public void NotifyMetaChanged()
        {
            OnPropertyChanged(nameof(DateRange));
            OnPropertyChanged(nameof(IsCurrentWeek));
            OnPropertyChanged(nameof(RelativeWeekTag));
            OnPropertyChanged(nameof(HasRelativeTag));
        }
    }

    // ==========================================
    // MISSIONS: FISSURES, ALERTS, INVASIONS
    // ==========================================
    public class VoidFissureItem : INotifyPropertyChanged
    {
        private string _timeRemaining = string.Empty;
        private BitmapImage? _tierBitmap;
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public string Node { get; set; } = string.Empty;
        public string Planet { get; set; } = string.Empty;
        public string SystemRegion { get; set; } = "Звёздная карта";
        public string MissionType { get; set; } = string.Empty;
        public string Tier { get; set; } = "Lith";
        public string TierDisplay => Tier switch
        {
            "Lith" => "Лит",
            "Meso" => "Мезо",
            "Neo" => "Нео",
            "Axi" => "Акси",
            "Requiem" => "Реквием",
            "Omnia" => "Омни",
            _ => Tier
        };
        public int TierOrder { get; set; } = 1; // 1: Lith, 2: Meso, 3: Neo, 4: Axi, 5: Requiem, 6: Omnia
        public string TierColor { get; set; } = "#F59E0B";
        public string EnemyFaction { get; set; } = "Гринир";
        public DateTime ExpiryUtc { get; set; }

        public string TimeRemaining
        {
            get => _timeRemaining;
            set { _timeRemaining = value; OnPropertyChanged(); }
        }

        public bool IsSteelPath { get; set; }
        public bool IsVoidStorm { get; set; }
        public string? TierIconUrl { get; set; }
        public bool HasTierIcon => !string.IsNullOrEmpty(TierIconUrl);
        public bool HasTierBitmap => _tierBitmap != null;
        public BitmapImage? TierBitmap => IconLoaderHelper.GetOrRequest(TierIconUrl, ref _tierBitmap, bmp =>
        {
            _tierBitmap = bmp;
            OnPropertyChanged(nameof(TierBitmap));
            OnPropertyChanged(nameof(HasTierBitmap));
        }, decodeWidth: 56);
    }

    public class AlertItem : INotifyPropertyChanged
    {
        private string _timeRemaining = string.Empty;
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public string Title { get; set; } = string.Empty;
        public string Node { get; set; } = string.Empty;
        public string Planet { get; set; } = string.Empty;
        public string MissionType { get; set; } = string.Empty;
        public string Faction { get; set; } = string.Empty;
        public string Reward { get; set; } = string.Empty;
        public DateTime ExpiryUtc { get; set; }

        public string TimeRemaining
        {
            get => _timeRemaining;
            set { _timeRemaining = value; OnPropertyChanged(); }
        }

        public string RewardType { get; set; } = "Upgrade";
    }

    public class InvasionItem : INotifyPropertyChanged
    {
        private string _timeRemaining = string.Empty;
        private BitmapImage? _attackerBitmap;
        private BitmapImage? _defenderBitmap;
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public string Node { get; set; } = string.Empty;
        public string Planet { get; set; } = string.Empty;
        public string AttackerFaction { get; set; } = "Гринир";
        public string DefenderFaction { get; set; } = "Корпус";
        public string AttackerReward { get; set; } = "Катализатор Орокин";
        public string DefenderReward { get; set; } = "3x Полетрон";
        public string? AttackerRewardThumb { get; set; }
        public string? DefenderRewardThumb { get; set; }
        public string AttackerRewardIcon { get; set; } = "📦";
        public string DefenderRewardIcon { get; set; } = "📦";
        public bool HasAttackerThumb => !string.IsNullOrEmpty(AttackerRewardThumb);
        public bool HasDefenderThumb => !string.IsNullOrEmpty(DefenderRewardThumb);
        public BitmapImage? AttackerBitmap => IconLoaderHelper.GetOrRequest(AttackerRewardThumb, ref _attackerBitmap, bmp =>
        {
            _attackerBitmap = bmp;
            OnPropertyChanged(nameof(AttackerBitmap));
        }, decodeWidth: 52);
        public BitmapImage? DefenderBitmap => IconLoaderHelper.GetOrRequest(DefenderRewardThumb, ref _defenderBitmap, bmp =>
        {
            _defenderBitmap = bmp;
            OnPropertyChanged(nameof(DefenderBitmap));
        }, decodeWidth: 52);
        public double AttackerProgressPercent { get; set; } = 64.5;
        public DateTime ExpiryUtc { get; set; }

        public string TimeRemaining
        {
            get => _timeRemaining;
            set { _timeRemaining = value; OnPropertyChanged(); }
        }

        public bool HasValuableReward { get; set; } = true;
    }

    // ==========================================
    // PRIME RESURGENCE (VARZIA)
    // ==========================================
    public class VarziaItem : INotifyPropertyChanged
    {
        private BitmapImage? _thumbBitmap;
        private BitmapImage? _currencyBitmap;
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = "Варфрейм";
        public string Description { get; set; } = string.Empty;
        public string IconGlyph { get; set; } = "👑";
        public string? ThumbnailUrl { get; set; }
        public string? SecondaryThumbnailUrl { get; set; }
        public string? CurrencyIconUrl { get; set; }
        public string? CostLabelOverride { get; set; }
        public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailUrl);
        public bool HasSecondaryThumbnail => !string.IsNullOrEmpty(SecondaryThumbnailUrl);
        public bool HasDescription => !string.IsNullOrEmpty(Description);
        public int AyaCost { get; set; } = 0;
        public int RegalAyaCost { get; set; } = 0;
        public bool HasAyaCost => AyaCost > 0;
        public bool HasRegalAyaCost => RegalAyaCost > 0;
        public string CostDisplay => !string.IsNullOrEmpty(CostLabelOverride) ? CostLabelOverride : (HasAyaCost ? $"{AyaCost} Айя" : $"{RegalAyaCost} Королевских Айя");
        public string CostShort => !string.IsNullOrEmpty(CostLabelOverride) ? CostLabelOverride : (HasAyaCost ? $"{AyaCost} Айя" : $"{RegalAyaCost} К.Айя");
        public string CostBg => HasRegalAyaCost ? "#261E0D" : "#0D2430";
        public string CostBorder => HasRegalAyaCost ? "#D8B25E" : "#06B6D4";
        public string CostFg => HasRegalAyaCost ? "#FFE18D" : "#67E8F9";
        public bool IsRelic { get; set; }
        public bool HasLoadedThumb => _thumbBitmap != null;
        public BitmapImage? ThumbnailBitmap => IconLoaderHelper.GetOrRequest(ThumbnailUrl, ref _thumbBitmap, bmp =>
        {
            _thumbBitmap = bmp;
            OnPropertyChanged(nameof(ThumbnailBitmap));
            OnPropertyChanged(nameof(HasLoadedThumb));
        }, decodeWidth: 64);
        public BitmapImage? CurrencyBitmap => IconLoaderHelper.GetOrRequest(CurrencyIconUrl, ref _currencyBitmap, bmp =>
        {
            _currencyBitmap = bmp;
            OnPropertyChanged(nameof(CurrencyBitmap));
        }, decodeWidth: 36);
    }

    public class VarziaResurgence : INotifyPropertyChanged
    {
        private string _timeRemaining = string.Empty;
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public string CurrentRotationName { get; set; } = "Protea Prime & Ivara Prime";
        public DateTime ExpiryUtc { get; set; }

        public string TimeRemaining
        {
            get => _timeRemaining;
            set { _timeRemaining = value; OnPropertyChanged(); }
        }

        public string UpcomingRotationName { get; set; } = "Equinox Prime & Wukong Prime";
        public List<VarziaItem> FeaturedItems { get; set; } = new();
        public ObservableCollection<VarziaItem> DisplayItems { get; } = new();
        public VarziaItem? DualPackItem { get; set; }
        public bool HasDualPack => DualPackItem != null;
        public void RefreshAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    // ==========================================
    // OPERATIONS & EVENTS
    // ==========================================
    public class EventShopEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string IconGlyph { get; set; } = "📦";
        public string? ThumbnailUrl { get; set; }
        public string? SecondaryThumbnailUrl { get; set; }
        public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailUrl);
        public bool HasSecondaryThumbnail => !string.IsNullOrEmpty(SecondaryThumbnailUrl);
        public bool HasDescription => !string.IsNullOrEmpty(Description);
        public int Cost { get; set; }
        public string? PurchaseLimit { get; set; }
        public string Rarity { get; set; } = "Common";
        public string CostDisplay => $"🪙 {Cost}";
    }

    public class EventMilestoneEntry
    {
        public int ScoreRequired { get; set; }
        public string Reward { get; set; } = string.Empty;
        public string RewardType { get; set; } = string.Empty;
    }

    public class OperationEvent : INotifyPropertyChanged
    {
        private string _timeRemaining = string.Empty;
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public double ProgressPercent { get; set; }
        public DateTime ExpiryUtc { get; set; }

        public string TimeRemaining
        {
            get => _timeRemaining;
            set { _timeRemaining = value; OnPropertyChanged(); }
        }

        public string CurrencyName { get; set; } = string.Empty;
        public string? CurrencyIconUrl { get; set; }
        public bool HasCurrencyIcon => !string.IsNullOrEmpty(CurrencyIconUrl);
        public string VendorName { get; set; } = string.Empty;
        public bool IsGlobalOperation { get; set; }
        public bool IsSeasonalEvent { get; set; }
        public bool HasCommunityProgress { get; set; }
        public int SortPriority => IsGlobalOperation ? 1 : (IsSeasonalEvent ? 2 : 3);

        public List<EventMilestoneEntry> Milestones { get; set; } = new();
        public List<EventShopEntry> ShopItems { get; set; } = new();

        public bool HasShop => ShopItems.Count > 0;
        public bool HasMilestones => Milestones.Count > 0;
    }

    // ==========================================
    // NIGHTWAVE (НОЧНАЯ ВОЛНА)
    // ==========================================
    public class NightwaveChallenge : INotifyPropertyChanged
    {
        private string _timeRemaining = string.Empty;
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int Reputation { get; set; }
        public bool IsDaily { get; set; }
        public bool IsElite { get; set; }
        public DateTime ExpiryUtc { get; set; }

        public string TimeRemaining
        {
            get => _timeRemaining;
            set { _timeRemaining = value; OnPropertyChanged(); }
        }

        public string TypeBadge => IsElite ? "★ ЭЛИТНОЕ (7000)" : (IsDaily ? "ЕЖЕДНЕВНОЕ (1000)" : "ЕЖЕНЕДЕЛЬНОЕ (4500)");
        public string TypeShortLabel => IsElite ? "ЭЛИТНОЕ" : (IsDaily ? "ЕЖЕДНЕВНОЕ" : "ЕЖЕНЕДЕЛЬНОЕ");
        public string TypeBadgeColor => IsElite ? "#F59E0B" : (IsDaily ? "#10B981" : "#38BDF8");
        public string ReputationDisplay => Reputation.ToString("N0").Replace(",", " ");
    }

    public class NightwaveRewardRank : INotifyPropertyChanged
    {
        private BitmapImage? _iconBitmap;
        public event PropertyChangedEventHandler? PropertyChanged;

        public int Rank { get; set; }
        public int Count { get; set; } = 1;
        public bool IsPrestige { get; set; }
        public string RewardName { get; set; } = string.Empty;
        public string RewardType { get; set; } = string.Empty;
        public string? ImageUrl { get; set; }
        public bool HasImage => !string.IsNullOrEmpty(ImageUrl);
        public string IconGlyph { get; set; } = "🎁";
        public string RankBadge => IsPrestige ? $"П{Rank}" : $"УР. {Rank}";
        public string CountBadge => Count > 1 ? $"×{Count}" : string.Empty;
        public bool HasCountBadge => Count > 1;
        public bool HasLoadedIcon => _iconBitmap != null;
        public BitmapImage? IconBitmap => IconLoaderHelper.GetOrRequest(ImageUrl, ref _iconBitmap, bmp =>
        {
            _iconBitmap = bmp;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconBitmap)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasLoadedIcon)));
        }, decodeWidth: 128);
    }

    public class NightwaveOffering : INotifyPropertyChanged
    {
        private BitmapImage? _iconBitmap;
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = "Ауры и Моды";
        public string Description { get; set; } = string.Empty;
        public int CredCost { get; set; }
        public bool IsAlwaysAvailable { get; set; }
        public string? ImageUrl { get; set; }
        public bool HasImage => !string.IsNullOrEmpty(ImageUrl);
        public string IconGlyph { get; set; } = "📦";
        public bool HasLoadedIcon => _iconBitmap != null;
        public BitmapImage? IconBitmap => IconLoaderHelper.GetOrRequest(ImageUrl, ref _iconBitmap, bmp =>
        {
            _iconBitmap = bmp;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconBitmap)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasLoadedIcon)));
        }, decodeWidth: 128);
    }

    public class NightwaveInfo : INotifyPropertyChanged
    {
        private string _timeRemaining = string.Empty;
        private BitmapImage? _credBitmap;
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        private DateTime _expiryUtc;
        public string SeasonTitle { get; set; } = "Ночная Волна";
        public int SeasonNumber { get; set; } = 18;
        public int CurrentRank { get; set; } = 30;
        public DateTime ExpiryUtc
        {
            get => _expiryUtc;
            set
            {
                if (_expiryUtc != value)
                {
                    _expiryUtc = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasUncertainExpiry));
                    OnPropertyChanged(nameof(SeasonExpiryHint));
                }
            }
        }

        /// <summary>
        /// DE sets a far-future placeholder end date in SeasonInfo.Expiry until the official end date is announced.
        /// </summary>
        public bool HasUncertainExpiry => _expiryUtc > DateTime.UtcNow.AddDays(45);
        public string SeasonExpiryHint => HasUncertainExpiry
            ? $"Серверная дата-заглушка DE API ({_expiryUtc:dd.MM.yyyy} UTC): точная дата окончания этой Ночной Волны ещё не объявлена разработчиками и изменится перед завершением сезона."
            : $"Официальная дата окончания сезона: {_expiryUtc.ToLocalTime():dd.MM.yyyy HH:mm}";

        public string TimeRemaining
        {
            get => _timeRemaining;
            set { _timeRemaining = value; OnPropertyChanged(); }
        }

        public string? CredIconUrl { get; set; }
        public bool HasCredIcon => _credBitmap != null;
        public BitmapImage? CredIconBitmap => IconLoaderHelper.GetOrRequest(CredIconUrl, ref _credBitmap, bmp =>
        {
            _credBitmap = bmp;
            OnPropertyChanged(nameof(CredIconBitmap));
            OnPropertyChanged(nameof(HasCredIcon));
        }, decodeWidth: 36);
        public ObservableCollection<NightwaveChallenge> Challenges { get; } = new();
        public List<NightwaveRewardRank> RankRewards { get; } = new();
        public List<NightwaveOffering> AllOfferings { get; } = new();
        public ObservableCollection<NightwaveOffering> DisplayOfferings { get; } = new();
        public void RefreshAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    // ==========================================
    // COLLECTION & MASTER
    // ==========================================
    public class ItemCraftComponentBrief : INotifyPropertyChanged
    {
        private BitmapImage? _iconBitmap;
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; set; } = string.Empty;
        public string ShortLabel { get; set; } = string.Empty;
        public string IconGlyph { get; set; } = "⚙️";
        public string? ImageUrl { get; set; }
        public int Count { get; set; } = 1;
        public bool HasImage => !string.IsNullOrEmpty(ImageUrl);
        public bool HasLoadedIcon => _iconBitmap != null;

        public BitmapImage? IconBitmap
        {
            get
            {
                if (_iconBitmap != null) return _iconBitmap;
                if (!string.IsNullOrEmpty(ImageUrl))
                {
                    Services.ImageCacheService.Instance.RequestUrlBitmap(ImageUrl, bmp =>
                    {
                        if (!ReferenceEquals(_iconBitmap, bmp))
                        {
                            _iconBitmap = bmp;
                            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconBitmap)));
                            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasLoadedIcon)));
                        }
                    }, decodeWidth: 64);
                }
                return null;
            }
        }
    }

    public class CollectionItemModel : INotifyPropertyChanged
    {
        private bool _isOwned;
        private bool _isMastered;
        private bool _isSubsumed;
        private bool _hasIncarnon;
        private bool _isInCraftQueue;
        private WeakReference<BitmapImage>? _thumbnailRef;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public Action? OnChangedCallback { get; set; }

        public string UniqueName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string NameEn { get; set; } = string.Empty;
        public string Category { get; set; } = "Варфреймы";
        public string SubCategory { get; set; } = "Стандартный";
        public int MasteryXp { get; set; } = 3000;
        public int RequiredMr { get; set; } = 0;
        public bool IsVaulted { get; set; }
        public bool IsPrimeFlag { get; set; }
        public bool IsFounder { get; set; }
        public bool IsMasterable { get; set; } = true;
        public string SourceKind { get; set; } = "Foundry";
        public string SourceInfo { get; set; } = string.Empty;
        public string CostText { get; set; } = string.Empty;
        public string SlotTag { get; set; } = string.Empty;
        public string IconGlyph { get; set; } = "📦";
        public string? IconPath { get; set; }
        public string? ImageHash { get; set; }
        public string? FallbackImageName { get; set; }
        public string ImageCacheKey { get; set; } = string.Empty;
        public string? ThumbnailUrl { get; set; }
        public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailUrl);

        /// <summary>
        /// Lazy decoded & frozen BitmapImage loaded only for realized viewport cards.
        /// Held weakly so ImageCacheService's bounded LRU owns bitmap lifetime across 5700+ items.
        /// </summary>
        public BitmapImage? ThumbnailBitmap
        {
            get
            {
                if (_thumbnailRef != null && _thumbnailRef.TryGetTarget(out var live))
                    return live;
                if (string.IsNullOrEmpty(ImageCacheKey)) return null;

                var cached = Services.ImageCacheService.Instance.TryGetFromMemory(ImageCacheKey);
                if (cached != null)
                {
                    _thumbnailRef = new WeakReference<BitmapImage>(cached);
                    OnPropertyChanged(nameof(HasLoadedThumbnail));
                    return cached;
                }

                Services.ImageCacheService.Instance.RequestItemThumbnail(this);
                return null;
            }
        }

        public bool HasLoadedThumbnail => _thumbnailRef != null && _thumbnailRef.TryGetTarget(out _);

        public void SetLoadedBitmap(BitmapImage bmp)
        {
            BitmapImage? prev = null;
            _thumbnailRef?.TryGetTarget(out prev);
            if (!ReferenceEquals(prev, bmp))
            {
                _thumbnailRef = new WeakReference<BitmapImage>(bmp);
                OnPropertyChanged(nameof(ThumbnailBitmap));
                OnPropertyChanged(nameof(HasLoadedThumbnail));
            }
        }

        public bool CanBeSubsumed { get; set; }
        public bool CanHaveIncarnon { get; set; }
        public bool CanBeCrafted { get; set; } = true;
        public bool IsUsedAsIngredient { get; set; }
        public string UsedInCraftingFor { get; set; } = string.Empty;
        public string IngredientToolTip => $"Используется для создания: {UsedInCraftingFor}";

        public bool IsPrime => IsPrimeFlag || SubCategory == "Prime" || NameEn.EndsWith(" Prime", StringComparison.OrdinalIgnoreCase) || Name.EndsWith(" Prime", StringComparison.OrdinalIgnoreCase);
        public bool IsCosmetic =>
            !IsMasterable ||
            Category == "Эфемеры" ||
            Category == "Косметика" ||
            Category == "Украшения" ||
            Category == "Украшения Убежища" ||
            Category == "Косметика & Украшения" ||
            SubCategory == "Эфемера" ||
            SubCategory == "Сигна" ||
            SubCategory == "Броня" ||
            SubCategory == "Сандана" ||
            SubCategory == "Скин" ||
            SubCategory == "Шлем" ||
            SubCategory == "Эмоция" ||
            SubCategory == "Украшение" ||
            MasteryXp == 0;

        public bool CanBeMastered => !IsCosmetic && IsMasterable;
        public bool HasMrRequirement => RequiredMr > 0 && !IsCosmetic;
        public string MrText => $"MR {RequiredMr}";
        public bool HasXpDisplay => !IsCosmetic && MasteryXp > 0;
        public string XpDisplayText => HasXpDisplay ? $"+{MasteryXp:N0} XP" : string.Empty;

        public string SourceBadgeText
        {
            get
            {
                string shortSrc = ExtractShortSource(SourceInfo);
                return SourceKind switch
                {
                    "Founder" => "НАБОР ОСНОВАТЕЛЕЙ",
                    "RegalAya" => !string.IsNullOrEmpty(CostText) ? $"Варзия • {CostText}" : "Варзия (Корол. Айя)",
                    "RealMoney" => "За реал / Tennogen",
                    "Platinum" => !string.IsNullOrEmpty(CostText) ? $"{shortSrc} • {CostText}" : shortSrc,
                    "InGameCurrency" => !string.IsNullOrEmpty(CostText) ? $"{shortSrc} • {CostText}" : shortSrc,
                    "QuestOrDrop" => shortSrc,
                    _ => shortSrc
                };
            }
        }

        private static string ExtractShortSource(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Кузница / Детали";
            string s = raw.Trim();
            if (s.StartsWith("Игровая валюта / ", StringComparison.OrdinalIgnoreCase)) s = s["Игровая валюта / ".Length..].Trim();
            if (s.StartsWith("Игровая валюта:", StringComparison.OrdinalIgnoreCase)) s = s["Игровая валюта:".Length..].Trim();
            if (s.StartsWith("Игровая валюта", StringComparison.OrdinalIgnoreCase)) s = s["Игровая валюта".Length..].Trim(' ', '-', ':', '/');
            if (s.Length > 34)
            {
                int cut = s.IndexOf('(', 12);
                if (cut > 8 && cut <= 36) return s[..cut].Trim();
                int comma = s.IndexOf(',', 12);
                if (comma > 8 && comma <= 36) return s[..comma].Trim();
                return s[..32].TrimEnd() + "…";
            }
            return string.IsNullOrEmpty(s) ? "Внутриигровой магазин" : s;
        }

        public string SourceBadgeColor => SourceKind switch
        {
            "Founder" => "#F59E0B",
            "RegalAya" => "#A855F7",
            "RealMoney" => "#EC4899",
            "Platinum" => "#38BDF8",
            "InGameCurrency" => "#10B981",
            "QuestOrDrop" => "#F97316",
            _ => "#64748B"
        };

        public string BottomRightTag => HasMrRequirement
            ? $"MR {RequiredMr}"
            : (!string.IsNullOrEmpty(CostText) ? CostText : (IsCosmetic ? ExtractShortSource(SourceInfo) : ""));

        public bool HasBottomRightTag => !string.IsNullOrEmpty(BottomRightTag);

        public string CardToolTip =>
            string.IsNullOrEmpty(NameEn) || Name.Contains(NameEn, StringComparison.OrdinalIgnoreCase)
                ? $"{Name}\nКатегория: {Category} • {SubCategory}\nИсточник: {SourceInfo}"
                : $"{Name} ({NameEn})\nКатегория: {Category} • {SubCategory}\nИсточник: {SourceInfo}";

        public List<ItemCraftComponentBrief> Components { get; set; } = new();
        public IEnumerable<ItemCraftComponentBrief> CompactComponents => Components.Count <= 4 ? Components : Components.Take(4);
        public bool HasComponents => Components.Count > 0;

        public bool IsInCraftQueue
        {
            get => _isInCraftQueue;
            set
            {
                if (_isInCraftQueue != value)
                {
                    _isInCraftQueue = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CraftButtonText));
                    OnPropertyChanged(nameof(CraftButtonBackground));
                    OnPropertyChanged(nameof(CraftButtonBorder));
                    OnPropertyChanged(nameof(CraftButtonForeground));
                    OnPropertyChanged(nameof(CraftButtonToolTip));
                }
            }
        }

        public bool IsOwned
        {
            get => _isOwned;
            set
            {
                if (_isOwned != value)
                {
                    _isOwned = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(OwnedButtonText));
                    OnPropertyChanged(nameof(OwnedButtonBackground));
                    OnPropertyChanged(nameof(OwnedButtonBorder));
                    OnPropertyChanged(nameof(OwnedButtonForeground));
                    OnChangedCallback?.Invoke();
                }
            }
        }

        public bool IsMastered
        {
            get => _isMastered;
            set
            {
                if (_isMastered != value)
                {
                    _isMastered = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(MasteredLaurelBackground));
                    OnPropertyChanged(nameof(MasteredLaurelBorder));
                    OnPropertyChanged(nameof(MasteredLaurelForeground));
                    OnPropertyChanged(nameof(MasteredToolTip));
                    OnChangedCallback?.Invoke();
                }
            }
        }

        public bool IsSubsumed
        {
            get => _isSubsumed;
            set
            {
                if (_isSubsumed != value)
                {
                    _isSubsumed = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SubsumedBackground));
                    OnPropertyChanged(nameof(SubsumedBorder));
                    OnPropertyChanged(nameof(SubsumedForeground));
                    OnPropertyChanged(nameof(SubsumedToolTip));
                    OnChangedCallback?.Invoke();
                }
            }
        }

        public bool HasIncarnon
        {
            get => _hasIncarnon;
            set
            {
                if (_hasIncarnon != value)
                {
                    _hasIncarnon = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IncarnonBackground));
                    OnPropertyChanged(nameof(IncarnonBorder));
                    OnPropertyChanged(nameof(IncarnonForeground));
                    OnPropertyChanged(nameof(IncarnonToolTip));
                    OnChangedCallback?.Invoke();
                }
            }
        }

        // AlecaFrame Style Visual Properties:
        public string OwnedButtonText => IsOwned ? "✓ В наличии" : "+ В инвентарь";
        public string OwnedButtonBackground => IsOwned ? "#142E22" : "#101621";
        public string OwnedButtonBorder => IsOwned ? "#10B981" : "#223145";
        public string OwnedButtonForeground => IsOwned ? "#34D399" : "#8493A8";

        public string MasteredLaurelBackground => IsMastered ? "#382D13" : "#101621";
        public string MasteredLaurelBorder => IsMastered ? "#D4AF37" : "#223145";
        public string MasteredLaurelForeground => IsMastered ? "#F3CB52" : "#64748B";
        public string MasteredToolTip => IsMastered ? "Освоено (Ранг 30 достигнут)" : "Не освоено в ранг (Нажмите для отметки)";

        public string SubsumedBackground => IsSubsumed ? "#4A1521" : "#15161C";
        public string SubsumedBorder => IsSubsumed ? "#EF4444" : "#2C2025";
        public string SubsumedForeground => IsSubsumed ? "#F87171" : "#7A6870";
        public string SubsumedToolTip => IsSubsumed ? "Скормлен в систему Гельминта (Subsumed)" : "Не скормлен Гельминту (Нажмите для отметки)";

        public string IncarnonBackground => HasIncarnon ? "#102F38" : "#101621";
        public string IncarnonBorder => HasIncarnon ? "#00E5FF" : "#1B2A38";
        public string IncarnonForeground => HasIncarnon ? "#00E5FF" : "#567585";
        public string IncarnonToolTip => HasIncarnon ? "Инкарнон-адаптер установлен" : "Инкарнон не установлен (Нажмите для отметки)";

        // Craft Queue Button Dynamic State
        public string CraftButtonText => IsInCraftQueue ? "✓ В крафте" : "+ В крафт";
        public string CraftButtonBackground => IsInCraftQueue ? "#132D20" : "#101621";
        public string CraftButtonBorder => IsInCraftQueue ? "#10B981" : "#00E5FF";
        public string CraftButtonForeground => IsInCraftQueue ? "#34D399" : "#00E5FF";
        public string CraftButtonToolTip => IsInCraftQueue ? "Уже в списке Кузницы (Нажмите для удаления)" : "Добавить в список ресурсов Кузницы (В крафт)";

        public string BadgeColor => SubCategory switch
        {
            "Prime" => "#F59E0B",
            "Кува" => "#DC2626",
            "Догмат" => "#0EA5E9",
            "Кода 1999" => "#A855F7",
            "Эфемера" => "#8B5CF6",
            "Сигна" => "#F59E0B",
            "Броня" => "#3B82F6",
            "Сандана" => "#EC4899",
            "Скин" => "#10B981",
            "Шлем" => "#14B8A6",
            "Анимации" => "#EAB308",
            "Сигилы" => "#A855F7",
            "Фигурки" => "#F59E0B",
            "Флоффы" => "#EC4899",
            "Артикулы & Трофеи" => "#8B5CF6",
            "Дисплеи" => "#06B6D4",
            "Декор Орбитера" => "#6366F1",
            "Вандал" => "#10B981",
            "Врайт" => "#EF4444",
            "Призма" => "#38BDF8",
            "Дувири" => "#F59E0B",
            "Некромех" => "#F97316",
            "Китган" => "#06B6D4",
            "Зо" => "#F59E0B",
            "Усилитель" => "#A855F7",
            "К-Драйв" => "#10B981",
            "Арчвинг" => "#38BDF8",
            "Тяжелое оружие" => "#F97316",
            "Страж" => "#10B981",
            "Оружие стража" => "#38BDF8",
            "Вульпафила" => "#EC4899",
            "Кават" => "#F59E0B",
            "Кубрау" => "#84CC16",
            "МОА" => "#06B6D4",
            "Гончая" => "#6366F1",
            _ => "#64748B"
        };
    }

    public class SubTabItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public string Title { get; set; } = string.Empty;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(BackgroundBrush));
                    OnPropertyChanged(nameof(BorderBrush));
                    OnPropertyChanged(nameof(ForegroundBrush));
                }
            }
        }

        public string BackgroundBrush => IsSelected ? "#1A2E44" : "#101621";
        public string BorderBrush => IsSelected ? "#00E5FF" : "#223145";
        public string ForegroundBrush => IsSelected ? "#00E5FF" : "#8493A8";
    }

    public class MasterySummaryInfo : INotifyPropertyChanged
    {
        private int _totalMasteryXp;
        private int _currentRankNumber = 30;
        private string _rankTitle = "Великий Мастер (Grand Master)";
        private double _progressPercent;
        private int _totalItemsCount;
        private int _ownedCount;
        private int _masteredCount;
        private string _progressText = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public int TotalMasteryXp
        {
            get => _totalMasteryXp;
            set { _totalMasteryXp = value; OnPropertyChanged(); }
        }

        public int CurrentRankNumber
        {
            get => _currentRankNumber;
            set { _currentRankNumber = value; OnPropertyChanged(); }
        }

        public string RankTitle
        {
            get => _rankTitle;
            set { _rankTitle = value; OnPropertyChanged(); }
        }

        public double ProgressPercent
        {
            get => _progressPercent;
            set { _progressPercent = value; OnPropertyChanged(); }
        }

        public int TotalItemsCount
        {
            get => _totalItemsCount;
            set { _totalItemsCount = value; OnPropertyChanged(); }
        }

        public int OwnedCount
        {
            get => _ownedCount;
            set { _ownedCount = value; OnPropertyChanged(); }
        }

        public int MasteredCount
        {
            get => _masteredCount;
            set { _masteredCount = value; OnPropertyChanged(); }
        }

        public string ProgressText
        {
            get => _progressText;
            set { _progressText = value; OnPropertyChanged(); }
        }

        public string OwnedPercentText => TotalItemsCount > 0 ? $"{((double)OwnedCount / TotalItemsCount * 100):F1}%" : "0%";
        public string MasteredPercentText => TotalItemsCount > 0 ? $"{((double)MasteredCount / TotalItemsCount * 100):F1}%" : "0%";
    }

    public enum TargetPresetType
    {
        Normal,
        SteelPath,
        EximusOverguard,
        AcolyteBoss,
        DemolystBoss,
        ArchonBoss,
        EidolonBoss
    }

    public class DamageSimulationResult
    {
        public string TargetName { get; set; } = string.Empty;
        public int TargetLevel { get; set; }
        public string HealthType { get; set; } = string.Empty;
        public string FactionName { get; set; } = "Гринир";
        public double Health { get; set; }
        public double Shield { get; set; }
        public double Armor { get; set; }
        public double Overguard { get; set; }
        public double DamageReductionPercent { get; set; }
        public double FactionWeaknessMultiplier { get; set; } = 1.0;
        public double BurstDps { get; set; }
        public double ShieldDps { get; set; }
        public double EffectiveDps { get; set; }
        public double OverguardDps { get; set; }
        public double TimeToKillSeconds { get; set; }
        public string BreakdownNote { get; set; } = string.Empty;
    }

    public class ObservableRangeCollection<T> : ObservableCollection<T>
    {
        public ObservableRangeCollection() : base() { }
        public ObservableRangeCollection(IEnumerable<T> collection) : base(collection) { }

        public void ReplaceRange(IEnumerable<T> collection)
        {
            Items.Clear();
            foreach (var item in collection)
            {
                Items.Add(item);
            }
            OnPropertyChanged(new PropertyChangedEventArgs("Count"));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        }
    }

    // ==========================================
    // STAGE 3: NEWS, DAILY & WEEKLY ACTIVITIES, TRADERS
    // ==========================================
    public class NewsItemModel : INotifyPropertyChanged
    {
        private BitmapImage? _bannerBitmap;
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Title { get; set; } = string.Empty;
        public string DateDisplay { get; set; } = string.Empty;
        public DateTime PublishedUtc { get; set; }
        public string LinkUrl { get; set; } = string.Empty;
        public string? ImageUrl { get; set; }
        public string CategoryLabel { get; set; } = "Новость";
        public string CategoryColor { get; set; } = "#38BDF8";
        public bool IsPriority { get; set; }
        public bool HasBannerBitmap => _bannerBitmap != null;

        public BitmapImage? BannerBitmap => IconLoaderHelper.GetOrRequest(ImageUrl, ref _bannerBitmap, bmp =>
        {
            _bannerBitmap = bmp;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BannerBitmap)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasBannerBitmap)));
        }, decodeWidth: 440);
    }

    public class SortieStageModel
    {
        public int Index { get; set; }
        public string Node { get; set; } = string.Empty;
        public string MissionType { get; set; } = string.Empty;
        public string Modifier { get; set; } = string.Empty;
        public string ModifierDesc { get; set; } = string.Empty;
    }

    public class DailySortieModel : INotifyPropertyChanged
    {
        private string _timeRemaining = "00м 00с";
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public string Boss { get; set; } = "Загрузка...";
        public string Faction { get; set; } = string.Empty;
        public DateTime ExpiryUtc { get; set; }
        public ObservableCollection<SortieStageModel> Stages { get; } = new();
        public string TimeRemaining
        {
            get => _timeRemaining;
            set { _timeRemaining = value; OnPropertyChanged(); }
        }
        public void RefreshAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public class ArbitrationModel : INotifyPropertyChanged
    {
        private string _timeRemaining = "00м 00с";
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public string Node { get; set; } = "Синхронизация...";
        public string Planet { get; set; } = string.Empty;
        public string MissionType { get; set; } = string.Empty;
        public string Faction { get; set; } = string.Empty;
        public string SourceNote { get; set; } = string.Empty;
        public string FullLocation => string.IsNullOrEmpty(Planet) ? Node : $"{Node} ({Planet})";
        public string MissionHeader => string.IsNullOrEmpty(Faction)
            ? MissionType.ToUpperInvariant()
            : $"{MissionType.ToUpperInvariant()} - {Faction}";
        public DateTime ExpiryUtc { get; set; }
        public string TimeRemaining
        {
            get => _timeRemaining;
            set { _timeRemaining = value; OnPropertyChanged(); }
        }
        public void RefreshAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public class DarvoDealModel : INotifyPropertyChanged
    {
        private string _timeRemaining = "00м 00с";
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public string ItemName { get; set; } = string.Empty;
        public int OriginalPrice { get; set; }
        public int SalePrice { get; set; }
        public int DiscountPercent { get; set; }
        public int TotalCount { get; set; }
        public int SoldCount { get; set; }
        public int RemainingCount => Math.Max(0, TotalCount - SoldCount);
        public DateTime ExpiryUtc { get; set; }
        public bool HasDeal => !string.IsNullOrEmpty(ItemName);
        public string TimeRemaining
        {
            get => _timeRemaining;
            set { _timeRemaining = value; OnPropertyChanged(); }
        }
        public void RefreshAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public class GlobalBoosterModel : INotifyPropertyChanged
    {
        private string _timeRemaining = string.Empty;
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public string Title { get; set; } = string.Empty;
        public string EffectText { get; set; } = string.Empty;
        public DateTime ExpiryUtc { get; set; }
        public string TimeRemaining
        {
            get => _timeRemaining;
            set { _timeRemaining = value; OnPropertyChanged(); }
        }
    }

    public class ArchonHuntModel : INotifyPropertyChanged
    {
        private string _timeRemaining = "00д 00ч";
        private BitmapImage? _shardBaseBitmap;
        private BitmapImage? _shardBitmap;
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public string Boss { get; set; } = string.Empty;
        public string ShardTitle { get; set; } = string.Empty;
        public string ShardColor { get; set; } = "#EF4444";
        private string? _shardBaseIconUrl;
        public string? ShardBaseIconUrl
        {
            get => _shardBaseIconUrl;
            set
            {
                if (!string.Equals(_shardBaseIconUrl, value, StringComparison.Ordinal))
                {
                    _shardBaseIconUrl = value;
                    _shardBaseBitmap = null;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ShardBaseBitmap));
                    OnPropertyChanged(nameof(HasShardBaseBitmap));
                }
            }
        }
        public bool HasShardBaseBitmap => _shardBaseBitmap != null;
        public BitmapImage? ShardBaseBitmap => IconLoaderHelper.GetOrRequest(ShardBaseIconUrl, ref _shardBaseBitmap, bmp =>
        {
            _shardBaseBitmap = bmp;
            OnPropertyChanged(nameof(ShardBaseBitmap));
            OnPropertyChanged(nameof(HasShardBaseBitmap));
        }, decodeWidth: 84);

        private string? _shardIconUrl;
        public string? ShardIconUrl
        {
            get => _shardIconUrl;
            set
            {
                if (!string.Equals(_shardIconUrl, value, StringComparison.Ordinal))
                {
                    _shardIconUrl = value;
                    _shardBitmap = null;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ShardBitmap));
                    OnPropertyChanged(nameof(HasShardBitmap));
                }
            }
        }
        public bool HasShardBitmap => _shardBitmap != null;
        public BitmapImage? ShardBitmap => IconLoaderHelper.GetOrRequest(ShardIconUrl, ref _shardBitmap, bmp =>
        {
            _shardBitmap = bmp;
            OnPropertyChanged(nameof(ShardBitmap));
            OnPropertyChanged(nameof(HasShardBitmap));
        }, decodeWidth: 84);
        public DateTime ExpiryUtc { get; set; }
        public ObservableCollection<SortieStageModel> Stages { get; } = new();
        public string TimeRemaining
        {
            get => _timeRemaining;
            set { _timeRemaining = value; OnPropertyChanged(); }
        }
        public void RefreshAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public class ArchimedeaMissionModel
    {
        public int Index { get; set; }
        public string MissionType { get; set; } = string.Empty;
        public string Faction { get; set; } = string.Empty;
        public string DeviationName { get; set; } = string.Empty;
        public string DeviationDesc { get; set; } = string.Empty;
        public string RisksSummary { get; set; } = string.Empty;
        public List<string> Risks { get; } = new();
    }

    public class ArchimedeaPersonalModModel
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    public class ArchimedeaModel : INotifyPropertyChanged
    {
        private string _timeRemaining = string.Empty;
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public string Title { get; set; } = string.Empty;
        public string AccentColor { get; set; } = "#A855F7";
        public DateTime ExpiryUtc { get; set; }
        public ObservableCollection<ArchimedeaMissionModel> Missions { get; } = new();
        public ObservableCollection<ArchimedeaPersonalModModel> PersonalModifiers { get; } = new();
        public string TimeRemaining
        {
            get => _timeRemaining;
            set { _timeRemaining = value; OnPropertyChanged(); }
        }
    }

    public class DescendiaChallengeItem
    {
        public int Index { get; set; }
        public string MissionType { get; set; } = string.Empty;
        public string ChallengeName { get; set; } = string.Empty;
        public bool IsMiniBoss { get; set; }
        public string DetailTooltip { get; set; } = string.Empty;
        public bool IsCheckpoint => Index == 7 || Index == 14;
        public bool IsFinalBoss => Index == 21;
        public string BadgeLabel => IsFinalBoss ? "ФИНАЛ" : (IsCheckpoint ? "ЧЕКПОИНТ" : (IsMiniBoss ? "БОСС" : string.Empty));
        public bool HasBadge => !string.IsNullOrEmpty(BadgeLabel);
        public string RowBackground => IsFinalBoss ? "#241118" : (IsCheckpoint ? "#101E28" : (IsMiniBoss ? "#21190F" : "#0B0F17"));
        public string RowBorder => IsFinalBoss ? "#9F2B47" : (IsCheckpoint ? "#286680" : (IsMiniBoss ? "#78571E" : "#1E2838"));
        public string BadgeColor => IsFinalBoss ? "#FB7185" : (IsCheckpoint ? "#38BDF8" : "#F59E0B");
    }

    public class DescendiaModel : INotifyPropertyChanged
    {
        private string _timeRemaining = string.Empty;
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public DateTime ExpiryUtc { get; set; }
        public ObservableCollection<DescendiaChallengeItem> Challenges { get; } = new();
        public bool HasChallenges => Challenges.Count > 0;
        public string TimeRemaining
        {
            get => _timeRemaining;
            set { _timeRemaining = value; OnPropertyChanged(); }
        }
        public void RefreshAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public class CalendarDayEntry
    {
        public string DateLabel { get; set; } = string.Empty;
        public string EventKind { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public string BadgeColor { get; set; } = "#06B6D4";
    }

    public class HexCalendarModel : INotifyPropertyChanged
    {
        private string _timeRemaining = string.Empty;
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public string SeasonDisplay { get; set; } = "1999 Хекс";
        public DateTime ExpiryUtc { get; set; }
        public ObservableCollection<CalendarDayEntry> Prizes { get; } = new();
        public ObservableCollection<CalendarDayEntry> Tasks { get; } = new();
        public ObservableCollection<CalendarDayEntry> Overrides { get; } = new();
        public string TimeRemaining
        {
            get => _timeRemaining;
            set { _timeRemaining = value; OnPropertyChanged(); }
        }
        public void RefreshAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public class SteelPathRotationEntry
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Cost { get; set; }
        public bool IsCurrent { get; set; }
        public bool IsUmbraOrKey { get; set; }
        public string RowBackground => IsCurrent ? "#1E2D1E" : (IsUmbraOrKey ? "#261E10" : "#101621");
        public string RowBorder => IsCurrent ? "#10B981" : (IsUmbraOrKey ? "#6B5218" : "#1F2D3F");
    }

    public class SteelPathWeeklyModel : INotifyPropertyChanged
    {
        private string _timeRemaining = string.Empty;
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public string CurrentRewardName { get; set; } = string.Empty;
        public int CurrentRewardCost { get; set; }
        public DateTime ExpiryUtc { get; set; }
        public ObservableCollection<SteelPathRotationEntry> Rotation { get; } = new();
        public string TimeRemaining
        {
            get => _timeRemaining;
            set { _timeRemaining = value; OnPropertyChanged(); }
        }
        public void RefreshAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public class TraderInventoryItem : INotifyPropertyChanged
    {
        private BitmapImage? _iconBitmap;
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public int Ducats { get; set; }
        public int Credits { get; set; }
        public string UniqueName { get; set; } = string.Empty;
        public string? ThumbnailUrl { get; set; }
        public bool HasLoadedIcon => _iconBitmap != null;

        public BitmapImage? IconBitmap => IconLoaderHelper.GetOrRequest(ThumbnailUrl, ref _iconBitmap, bmp =>
        {
            _iconBitmap = bmp;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconBitmap)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasLoadedIcon)));
        }, decodeWidth: 72);
    }

    public class VoidTraderModel : INotifyPropertyChanged
    {
        private string _timeRemaining = string.Empty;
        private bool _notifyEnabled;
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public string CharacterName { get; set; } = "Баро Ки'Тиир";
        public string Location { get; set; } = "Реле (Ожидание)";
        public bool IsActive { get; set; }
        public bool NotifyEnabled
        {
            get => _notifyEnabled;
            set
            {
                if (_notifyEnabled != value)
                {
                    _notifyEnabled = value;
                    OnPropertyChanged();
                }
            }
        }
        public DateTime ActivationUtc { get; set; }
        public DateTime ExpiryUtc { get; set; }
        public string StatusTitle => IsActive ? "Уже на реле • улетает через:" : "Прибудет на реле через:";
        public string SearchHint => IsActive ? "Поиск в товарах Баро..." : "Баро сейчас в пути (поиск неактивен)";
        public DateTime ActiveTargetUtc => IsActive ? ExpiryUtc : ActivationUtc;
        public ObservableCollection<TraderInventoryItem> Inventory { get; } = new();
        public bool HasInventory => Inventory.Count > 0;
        public string TimeRemaining
        {
            get => _timeRemaining;
            set { _timeRemaining = value; OnPropertyChanged(); }
        }
        public void RefreshAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public class WeeklyChecklistItem : INotifyPropertyChanged
    {
        private bool _isCompleted;
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string RewardHint { get; set; } = string.Empty;
        public Action? OnChanged { get; set; }

        public bool IsCompleted
        {
            get => _isCompleted;
            set
            {
                if (_isCompleted != value)
                {
                    _isCompleted = value;
                    OnPropertyChanged();
                    OnChanged?.Invoke();
                }
            }
        }
    }

    public class ThreatProgressModel
    {
        public double FomorianPercent { get; set; }
        public double RazorbackPercent { get; set; }
        public string FomorianText => $"{FomorianPercent:F1}%";
        public string RazorbackText => $"{RazorbackPercent:F1}%";
    }
}
