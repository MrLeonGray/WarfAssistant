using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using WarfAssistant.Models;
using WarfAssistant.Services;

namespace WarfAssistant.ViewModels
{
    /// <summary>
    /// Modular ViewModel for Worldstate Summary, Timers, News, Daily/Weekly Activities,
    /// Fissures/Alerts/Invasions, Traders (Baro & Varzia), and Nightwave.
    /// </summary>
    public sealed class EventsViewModel : ViewModelBase
    {
        private readonly Func<IReadOnlyList<CollectionItemModel>> _getCatalog;
        private readonly Action<string> _statusCallback;
        private readonly object _fetchLock = new();
        private Task? _activeFetchTask;
        private volatile bool _isFetching;
        private DateTime _lastAutoSyncUtc = DateTime.MinValue;
        private string _currentWeekKey = string.Empty;
        private bool _eventsSeedLoaded;

        private readonly Dictionary<string, string> _simarisGuides = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _archonIconPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string Name, string Cat, string Icon)> _varziaBundles = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<VarziaItem> _varziaPermanentAya = new();
        private string? _ayaIconUrl;
        private string? _regalAyaIconUrl;
        private string? _nightwaveCredIconUrl;

        private string _subTab = "Timers"; // Legacy compatibility
        public string EventsSubTab
        {
            get => _subTab;
            set => SetProperty(ref _subTab, value);
        }

        private string _activeHudPanel = "None"; // "Traders", "Events", "Alerts", "Invasions", "Fissures", "Sortie", "Archon", "Duviri", "None"
        public string ActiveHudPanel
        {
            get => _activeHudPanel;
            set
            {
                if (SetProperty(ref _activeHudPanel, value))
                {
                    OnPropertyChanged(nameof(IsHudPanelOpen));
                    OnPropertyChanged(nameof(ActiveHudPanelTitle));
                }
            }
        }

        public bool IsHudPanelOpen => !string.Equals(_activeHudPanel, "None", StringComparison.OrdinalIgnoreCase);

        public string ActiveHudPanelTitle => _activeHudPanel switch
        {
            "Traders" => "ТОРГОВЦЫ БЕЗДНЫ: БАРО КИ'ТИИР И ВАРЗИЯ",
            "Events" => "СОБЫТИЯ, БУСТЕРЫ И СКИДКИ",
            "Alerts" => "СИГНАЛЫ ТРЕВОГИ",
            "Invasions" => "ВТОРЖЕНИЯ И УГРОЗЫ СИСТЕМЕ",
            "Fissures" => "РАЗРЫВЫ БЕЗДНЫ",
            "Sortie" => "ВЫЛАЗКА ДНЯ",
            "Archon" => "ОХОТА НА АРХОНТА",
            "Duviri" => "ДУВИРИ — СПИРАЛЬ И РОТАЦИИ ЦЕПИ",
            _ => "СТАТУС МИРА"
        };

        private string _leftWorkspaceTab = "Endgame"; // "Endgame", "News" (kept for compatibility)
        public string LeftWorkspaceTab
        {
            get => _leftWorkspaceTab;
            set => SetProperty(ref _leftWorkspaceTab, value);
        }

        private string _endgameSubTab = "Archimedea"; // "Archimedea", "Descendia", "Calendar", "SteelPath"
        public string EndgameSubTab
        {
            get => _endgameSubTab;
            set
            {
                if (SetProperty(ref _endgameSubTab, value))
                    OnPropertyChanged(nameof(ActiveEndgameTimeRemaining));
            }
        }

        public string ActiveEndgameTimeRemaining => _endgameSubTab switch
        {
            "Descendia" => Descendia.TimeRemaining,
            "Calendar" => HexCalendar.TimeRemaining,
            "SteelPath" => SteelPath.TimeRemaining,
            _ => Archimedeas.FirstOrDefault()?.TimeRemaining ?? WeeklyResetRemaining
        };

        private bool _isNightwaveOpen;
        public bool IsNightwaveOpen
        {
            get => _isNightwaveOpen;
            set => SetProperty(ref _isNightwaveOpen, value);
        }

        private string _nightwaveTab = "Acts"; // "Acts", "Shop"
        public string NightwaveTab
        {
            get => _nightwaveTab;
            set
            {
                if (SetProperty(ref _nightwaveTab, value))
                    OnPropertyChanged(nameof(NightwaveHeaderTitle));
            }
        }

        public string NightwaveHeaderTitle => string.Equals(_nightwaveTab, "Shop", StringComparison.OrdinalIgnoreCase)
            ? "НОЧНАЯ ВОЛНА / ПРЕДЛОЖЕНИЯ ЗА ДОВЕРИЕ"
            : "НОЧНАЯ ВОЛНА / ДЕЯНИЯ";

        public int HudEventsCount => GlobalBoosters.Count + Operations.Count + (DarvoDeal.HasDeal ? 1 : 0);
        public int HudAlertsCount => 1 + ActiveAlerts.Count + WeeklyChecklist.Count(w => !w.IsCompleted);
        public int HudInvasionsCount => ActiveInvasions.Count;
        public int HudFissuresCount => AllFissures.Count;
        public int HudSortieCount => DailySortie.Stages.Count > 0 ? DailySortie.Stages.Count : 0;
        public int HudArchonCount => ArchonHunt.Stages.Count > 0 ? 1 : 0;
        public int HudTradersCount => Baro.IsActive ? 1 : 0;
        public bool HasActiveAlertItems => ActiveAlerts.Count > 0;

        public BitmapImage? CreditsIconBitmap { get; private set; }
        public BitmapImage? UiIconNavEvents { get; private set; }
        public BitmapImage? UiIconNavAlerts { get; private set; }
        public BitmapImage? UiIconNavInvasions { get; private set; }
        public BitmapImage? UiIconNavFissures { get; private set; }
        public BitmapImage? UiIconNavSortie { get; private set; }
        public BitmapImage? UiIconNavArchon { get; private set; }
        public BitmapImage? UiIconNavTraders { get; private set; }
        public BitmapImage? UiIconNightwave { get; private set; }
        public BitmapImage? UiIconArbitrationHexis { get; private set; }
        public BitmapImage? UiIconArbitrationBadge { get; private set; }
        public BitmapImage? UiIconDuviriBanner { get; private set; }
        public BitmapImage? UiIconFissureOrigin { get; private set; }
        public BitmapImage? UiIconFissureSteelPath { get; private set; }
        public BitmapImage? UiIconFissureProxima { get; private set; }

        public bool HasUiIconNavEvents => UiIconNavEvents != null;
        public bool HasUiIconNavAlerts => UiIconNavAlerts != null;
        public bool HasUiIconNavInvasions => UiIconNavInvasions != null;
        public bool HasUiIconNavFissures => UiIconNavFissures != null;
        public bool HasUiIconNavSortie => UiIconNavSortie != null;
        public bool HasUiIconNavArchon => UiIconNavArchon != null;
        public bool HasUiIconNavTraders => UiIconNavTraders != null;
        public bool HasUiIconNightwave => UiIconNightwave != null;
        public bool HasUiIconArbitrationHexis => UiIconArbitrationHexis != null;
        public bool HasUiIconArbitrationBadge => UiIconArbitrationBadge != null;
        public bool HasUiIconDuviriBanner => UiIconDuviriBanner != null;
        public bool HasUiIconFissureOrigin => UiIconFissureOrigin != null;
        public bool HasUiIconFissureSteelPath => UiIconFissureSteelPath != null;
        public bool HasUiIconFissureProxima => UiIconFissureProxima != null;

        private string _syncLabel = "Локальный кэш";
        public string SyncLabel
        {
            get => _syncLabel;
            set => SetProperty(ref _syncLabel, value);
        }

        private bool _isRefreshing;
        public bool IsRefreshing
        {
            get => _isRefreshing;
            set => SetProperty(ref _isRefreshing, value);
        }

        private string _dailyResetRemaining = "--ч --м";
        public string DailyResetRemaining
        {
            get => _dailyResetRemaining;
            set => SetProperty(ref _dailyResetRemaining, value);
        }

        private string _weeklyResetRemaining = "--д --ч";
        public string WeeklyResetRemaining
        {
            get => _weeklyResetRemaining;
            set => SetProperty(ref _weeklyResetRemaining, value);
        }

        private string _simarisTargetRaw = string.Empty;
        private string _simarisTarget = "Сканируется...";
        public string SimarisTarget
        {
            get => _simarisTarget;
            set
            {
                if (SetProperty(ref _simarisTarget, value))
                    UpdateSimarisTooltip();
            }
        }

        private string _simarisLocationTooltip = "Наведите на цель для просмотра точных миссий появления.";
        public string SimarisLocationTooltip
        {
            get => _simarisLocationTooltip;
            set => SetProperty(ref _simarisLocationTooltip, value);
        }

        private string _sentientOutpostText = "Нет активной аномалии";
        public string SentientOutpostText
        {
            get => _sentientOutpostText;
            set => SetProperty(ref _sentientOutpostText, value);
        }

        // Duviri & Incarnon Tracker Filters
        private string _incarnonSearchText = string.Empty;
        public string IncarnonSearchText
        {
            get => _incarnonSearchText;
            set { if (SetProperty(ref _incarnonSearchText, value)) ApplyIncarnonFilter(); }
        }

        // Fissure Filters (click-to-select, re-click-to-deselect back to "All")
        private string _selectedFissureTier = "All";
        public string SelectedFissureTier
        {
            get => _selectedFissureTier;
            set { if (SetProperty(ref _selectedFissureTier, value)) ApplyFissuresFilter(); }
        }

        private string _fissureCategoryFilter = "All"; // "All", "Origin", "SteelPath", "Proxima"
        public string FissureCategoryFilter
        {
            get => _fissureCategoryFilter;
            set { if (SetProperty(ref _fissureCategoryFilter, value)) ApplyFissuresFilter(); }
        }

        private string _fissureSteelPathMode = "All";
        public string FissureSteelPathMode
        {
            get => _fissureSteelPathMode;
            set { if (SetProperty(ref _fissureSteelPathMode, value)) ApplyFissuresFilter(); }
        }

        private string _fissureMissionTypeMode = "All";
        public string FissureMissionTypeMode
        {
            get => _fissureMissionTypeMode;
            set { if (SetProperty(ref _fissureMissionTypeMode, value)) ApplyFissuresFilter(); }
        }

        private string _fissureRegionFilter = "All";
        public string FissureRegionFilter
        {
            get => _fissureRegionFilter;
            set { if (SetProperty(ref _fissureRegionFilter, value)) ApplyFissuresFilter(); }
        }

        // Trader & Nightwave Filters
        private string _varziaFilterMode = "All";
        public string VarziaFilterMode
        {
            get => _varziaFilterMode;
            set { if (SetProperty(ref _varziaFilterMode, value)) ApplyVarziaFilter(); }
        }

        private string _baroSearchText = string.Empty;
        public string BaroSearchText
        {
            get => _baroSearchText;
            set { if (SetProperty(ref _baroSearchText, value)) ApplyBaroFilter(); }
        }

        private string _nightwaveOfferingFilter = "All";
        public string NightwaveOfferingFilter
        {
            get => _nightwaveOfferingFilter;
            set { if (SetProperty(ref _nightwaveOfferingFilter, value)) ApplyNightwaveOfferingsFilter(); }
        }

        private string _nightwaveShopSearchText = string.Empty;
        public string NightwaveShopSearchText
        {
            get => _nightwaveShopSearchText;
            set { if (SetProperty(ref _nightwaveShopSearchText, value)) ApplyNightwaveOfferingsFilter(); }
        }

        // Collections & Live Models
        public ObservableCollection<WorldCycleInfo> WorldCycles { get; } = new();
        public DuviriMoodInfo DuviriMood { get; } = new();
        public ObservableCollection<DuviriMoodTrackOption> DuviriMoodOptions { get; } = new();
        public ObservableCollection<DuviriCircuitTile> DuviriCurrentNormalFrames { get; } = new();
        public bool HasDuviriCurrentNormalFrames => DuviriCurrentNormalFrames.Count > 0;
        public ObservableCollection<DuviriRotationWeek> AllDuviriRotations { get; } = new();
        public ObservableCollection<DuviriRotationWeek> FilteredDuviriRotations { get; } = new();

        public ObservableCollection<NewsItemModel> NewsItems { get; } = new();
        public ObservableCollection<GlobalBoosterModel> GlobalBoosters { get; } = new();
        public bool HasGlobalBoosters => GlobalBoosters.Count > 0;

        public DailySortieModel DailySortie { get; } = new();
        public ArbitrationModel Arbitration { get; } = new();
        public DarvoDealModel DarvoDeal { get; } = new();
        public ArchonHuntModel ArchonHunt { get; } = new();
        public ObservableCollection<ArchimedeaModel> Archimedeas { get; } = new();
        public DescendiaModel Descendia { get; } = new();
        public HexCalendarModel HexCalendar { get; } = new();
        public SteelPathWeeklyModel SteelPath { get; } = new();
        public ObservableCollection<WeeklyChecklistItem> WeeklyChecklist { get; } = new();
        public ThreatProgressModel Threats { get; private set; } = new();

        public List<VoidFissureItem> AllFissures { get; } = new();
        public ObservableCollection<VoidFissureItem> DisplayFissures { get; } = new();
        public ObservableCollection<FissureRegionFilterOption> AvailableFissureRegions { get; } = new();
        public ObservableCollection<AlertItem> ActiveAlerts { get; } = new();
        public ObservableCollection<InvasionItem> ActiveInvasions { get; } = new();
        public ObservableCollection<OperationEvent> Operations { get; } = new();
        public bool HasActiveOperations => Operations.Count > 0;

        public VoidTraderModel Baro { get; } = new();
        public List<TraderInventoryItem> AllBaroItems { get; } = new();
        public ObservableCollection<TraderInventoryItem> DisplayBaroItems { get; } = new();

        public VarziaResurgence Varzia { get; } = new();
        public NightwaveInfo Nightwave { get; } = new();

        // Commands
        public ICommand SwitchSubTabCommand { get; }
        public ICommand SelectHudPanelCommand { get; }
        public ICommand CloseHudPanelCommand { get; }
        public ICommand SwitchLeftTabCommand { get; }
        public ICommand SwitchEndgameSubTabCommand { get; }
        public ICommand ToggleNightwaveCommand { get; }
        public ICommand CloseNightwaveCommand { get; }
        public ICommand SwitchNightwaveTabCommand { get; }
        public ICommand RefreshWorldstateCommand { get; }
        public ICommand ToggleCycleNotificationCommand { get; }
        public ICommand ToggleBaroNotifyCommand { get; }
        public ICommand ToggleMoodTrackCommand { get; }
        public ICommand ToggleIncarnonBookmarkCommand { get; }
        public ICommand FilterFissureTierCommand { get; }
        public ICommand SetFissureCategoryCommand { get; }
        public ICommand SetFissureSteelPathModeCommand { get; }
        public ICommand SetFissureMissionTypeModeCommand { get; }
        public ICommand FilterFissureRegionCommand { get; }
        public ICommand FilterVarziaCommand { get; }
        public ICommand FilterNightwaveOfferingsCommand { get; }
        public ICommand OpenNewsLinkCommand { get; }
        public ICommand ToggleWeeklyChecklistCommand { get; }

        public EventsViewModel(Func<IReadOnlyList<CollectionItemModel>> getCatalog, Action<string> statusCallback)
        {
            _getCatalog = getCatalog;
            _statusCallback = statusCallback;

            SwitchSubTabCommand = new RelayCommand(p => { if (p is string t) EventsSubTab = t; });
            SelectHudPanelCommand = new RelayCommand(p =>
            {
                if (p is string panel)
                {
                    IsNightwaveOpen = false;
                    ActiveHudPanel = string.Equals(ActiveHudPanel, panel, StringComparison.OrdinalIgnoreCase)
                        ? "None"
                        : panel;
                }
            });
            CloseHudPanelCommand = new RelayCommand(() => ActiveHudPanel = "None");
            SwitchLeftTabCommand = new RelayCommand(p => { if (p is string tab) LeftWorkspaceTab = tab; });
            SwitchEndgameSubTabCommand = new RelayCommand(p => { if (p is string tab) EndgameSubTab = tab; });
            ToggleNightwaveCommand = new RelayCommand(() =>
            {
                if (!IsNightwaveOpen) NightwaveTab = "Acts";
                IsNightwaveOpen = !IsNightwaveOpen;
            });
            CloseNightwaveCommand = new RelayCommand(() => IsNightwaveOpen = false);
            SwitchNightwaveTabCommand = new RelayCommand(p => { if (p is string t) NightwaveTab = t; });
            RefreshWorldstateCommand = new RelayCommand(() => _ = FetchLiveWorldstateAsync(forceNotify: true));

            ToggleCycleNotificationCommand = new RelayCommand(p =>
            {
                if (p is WorldCycleInfo c)
                {
                    c.NotifyEnabled = !c.NotifyEnabled;
                    SaveNotificationPreferences();
                    _statusCallback(c.NotifyEnabled
                        ? $"🔔 Уведомление при смене фазы включено: {c.LocationName}"
                        : $"🔕 Уведомление отключено: {c.LocationName}");
                }
            });

            ToggleBaroNotifyCommand = new RelayCommand(() =>
            {
                Baro.NotifyEnabled = !Baro.NotifyEnabled;
                SaveNotificationPreferences();
                _statusCallback(Baro.NotifyEnabled
                    ? "🔔 Уведомление о прибытии Баро Ки'Тиира включено"
                    : "🔕 Уведомление о Баро отключено");
            });

            ToggleMoodTrackCommand = new RelayCommand(p =>
            {
                if (p is DuviriMoodTrackOption opt)
                {
                    opt.IsTracked = !opt.IsTracked;
                }
            });

            ToggleIncarnonBookmarkCommand = new RelayCommand(p =>
            {
                if (p is DuviriRotationWeek week)
                {
                    week.HasUserBookmark = !week.HasUserBookmark;
                    var ids = AllDuviriRotations.Where(w => w.HasUserBookmark).Select(w => w.WeekNumber);
                    StorageService.Instance.SaveBookmarkedIncarnonWeeks(ids);
                    _statusCallback(week.HasUserBookmark
                        ? $"★ Неделя #{week.WeekNumber} добавлена в закладки Инкарнонов"
                        : $"Закладка снята с недели #{week.WeekNumber}");
                }
            });

            FilterFissureTierCommand = new RelayCommand(p => { if (p is string t) SelectedFissureTier = ToggleFilter(SelectedFissureTier, t); });
            SetFissureCategoryCommand = new RelayCommand(p => { if (p is string c) FissureCategoryFilter = ToggleFilter(FissureCategoryFilter, c); });
            SetFissureSteelPathModeCommand = new RelayCommand(p => { if (p is string m) FissureSteelPathMode = ToggleFilter(FissureSteelPathMode, m); });
            SetFissureMissionTypeModeCommand = new RelayCommand(p => { if (p is string m) FissureMissionTypeMode = ToggleFilter(FissureMissionTypeMode, m); });
            FilterFissureRegionCommand = new RelayCommand(p =>
            {
                if (p is FissureRegionFilterOption opt)
                    FissureRegionFilter = ToggleFilter(FissureRegionFilter, opt.Name);
                else if (p is string r)
                    FissureRegionFilter = ToggleFilter(FissureRegionFilter, r);
                SyncFissureRegionSelectionFlags();
            });
            FilterVarziaCommand = new RelayCommand(p => { if (p is string m) VarziaFilterMode = ToggleFilter(VarziaFilterMode, m); });
            FilterNightwaveOfferingsCommand = new RelayCommand(p => { if (p is string c) NightwaveOfferingFilter = ToggleFilter(NightwaveOfferingFilter, c); });

            OpenNewsLinkCommand = new RelayCommand(p =>
            {
                if (p is string url && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
                    catch { }
                }
            });

            ToggleWeeklyChecklistCommand = new RelayCommand(p =>
            {
                if (p is WeeklyChecklistItem item)
                {
                    item.IsCompleted = !item.IsCompleted;
                }
            });
        }

        private static string ToggleFilter(string current, string clicked) =>
            string.Equals(current, clicked, StringComparison.OrdinalIgnoreCase) ? "All" : clicked;

        private void SaveNotificationPreferences()
        {
            var enabled = WorldCycles.Where(w => w.NotifyEnabled).Select(w => w.Id).ToList();
            if (Baro.NotifyEnabled) enabled.Add("baro");
            StorageService.Instance.SaveCycleNotifications(enabled);
        }

        private void LoadEventsSeed()
        {
            if (_eventsSeedLoaded) return;
            _eventsSeedLoaded = true;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string? resName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("events_seed.json.gz", StringComparison.OrdinalIgnoreCase));
                if (resName == null) return;

                using Stream? rawStream = asm.GetManifestResourceStream(resName);
                if (rawStream == null) return;
                using var gz = new GZipStream(rawStream, CompressionMode.Decompress);
                using var doc = JsonDocument.Parse(gz);
                var root = doc.RootElement;

                if (root.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
                {
                    _ayaIconUrl = ImageCacheService.NormalizeImageUrl(WorldStateService.GetStr(meta, "ayaIcon"));
                    _regalAyaIconUrl = ImageCacheService.NormalizeImageUrl(WorldStateService.GetStr(meta, "regalAyaIcon"));
                    _nightwaveCredIconUrl = ImageCacheService.NormalizeImageUrl(WorldStateService.GetStr(meta, "nightwaveCredIcon"));
                    if (meta.TryGetProperty("archonIcons", out var ai) && ai.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in ai.EnumerateObject())
                        {
                            string? path = prop.Value.GetString();
                            if (!string.IsNullOrEmpty(path))
                                _archonIconPaths[prop.Name] = path;
                        }
                    }
                }

                if (root.TryGetProperty("simarisTargets", out var sim) && sim.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in sim.EnumerateObject())
                    {
                        string? tip = prop.Value.GetString();
                        if (!string.IsNullOrEmpty(tip))
                            _simarisGuides[prop.Name] = tip;
                    }
                }

                if (root.TryGetProperty("varziaAyaItems", out var vay) && vay.ValueKind == JsonValueKind.Array)
                {
                    _varziaPermanentAya.Clear();
                    foreach (var it in vay.EnumerateArray())
                    {
                        int ayaNum = it.TryGetProperty("aya", out var ayaEl) && ayaEl.ValueKind == JsonValueKind.Number ? ayaEl.GetInt32() : 0;
                        int regalNum = it.TryGetProperty("regalAya", out var regEl) && regEl.ValueKind == JsonValueKind.Number ? regEl.GetInt32() : 0;
                        bool isRelic = (it.TryGetProperty("isRelic", out var irEl) && irEl.ValueKind == JsonValueKind.True) ||
                                       WorldStateService.GetStr(it, "cat").Contains("Релик", StringComparison.OrdinalIgnoreCase);

                        if (ayaNum == 0 && regalNum == 0)
                        {
                            string legacyCost = WorldStateService.GetStr(it, "cost", "1 Айя");
                            var digits = new string(legacyCost.TakeWhile(char.IsDigit).ToArray());
                            if (int.TryParse(digits, out int d) && d > 0) ayaNum = d;
                            else ayaNum = 1;
                        }

                        string costLabel = regalNum > 0
                            ? $"{regalNum} К. Айя"
                            : $"{Math.Max(1, ayaNum)} Айя";

                        _varziaPermanentAya.Add(new VarziaItem
                        {
                            Name = WorldStateService.GetStr(it, "name"),
                            Category = WorldStateService.GetStr(it, "cat", "За Айю"),
                            Description = regalNum > 0
                                ? "Товар Возрождения Прайм у Варзии (Базар Мэру) за Королевскую Айю."
                                : "Постоянный обмен у Варзии (Базар Мэру) за обычную Айю.",
                            IconGlyph = isRelic ? "🏺" : "💠",
                            ThumbnailUrl = ImageCacheService.NormalizeImageUrl(WorldStateService.GetStr(it, "icon")),
                            CurrencyIconUrl = regalNum > 0 ? _regalAyaIconUrl : _ayaIconUrl,
                            CostLabelOverride = costLabel,
                            AyaCost = ayaNum,
                            RegalAyaCost = regalNum,
                            IsRelic = isRelic
                        });
                    }
                }

                if (root.TryGetProperty("varziaBundles", out var vb) && vb.ValueKind == JsonValueKind.Object)
                {
                    _varziaBundles.Clear();
                    foreach (var prop in vb.EnumerateObject())
                    {
                        if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                        string bName = WorldStateService.GetStr(prop.Value, "name");
                        string bCat = WorldStateService.GetStr(prop.Value, "cat", "Набор");
                        string bIcon = WorldStateService.GetStr(prop.Value, "icon");
                        if (!string.IsNullOrEmpty(bName))
                            _varziaBundles[prop.Name] = (bName, bCat, bIcon);
                    }
                }

                if (root.TryGetProperty("nightwaveRanks", out var nwRanks) && nwRanks.ValueKind == JsonValueKind.Array)
                {
                    Nightwave.RankRewards.Clear();
                    foreach (var r in nwRanks.EnumerateArray())
                    {
                        int rank = r.TryGetProperty("rank", out var rk) ? rk.GetInt32() : 1;
                        int cnt = r.TryGetProperty("count", out var ct) ? ct.GetInt32() : 1;
                        bool isPrest = r.TryGetProperty("isPrestige", out var ip) && ip.ValueKind == JsonValueKind.True;
                        if (isPrest || rank > 30) continue;
                        string rName = WorldStateService.GetStr(r, "name");
                        string icon = WorldStateService.GetStr(r, "icon");
                        Nightwave.RankRewards.Add(new NightwaveRewardRank
                        {
                            Rank = rank,
                            Count = Math.Max(1, cnt),
                            IsPrestige = isPrest,
                            RewardName = rName,
                            RewardType = isPrest ? "Престиж" : "Уровень",
                            ImageUrl = ImageCacheService.NormalizeImageUrl(icon),
                            IconGlyph = "🎁"
                        });
                    }
                }

                if (root.TryGetProperty("nightwaveShop", out var nwShop) && nwShop.ValueKind == JsonValueKind.Array)
                {
                    Nightwave.AllOfferings.Clear();
                    foreach (var s in nwShop.EnumerateArray())
                    {
                        string sName = WorldStateService.GetStr(s, "name");
                        if (string.IsNullOrEmpty(sName)) continue;
                        int cost = s.TryGetProperty("cost", out var cEl) ? cEl.GetInt32() : 25;
                        bool always = s.TryGetProperty("always", out var al) && al.ValueKind == JsonValueKind.True;
                        string cat = WorldStateService.GetStr(s, "cat", "Косметика");
                        string icon = WorldStateService.GetStr(s, "icon");
                        Nightwave.AllOfferings.Add(new NightwaveOffering
                        {
                            Name = sName,
                            Category = cat,
                            Description = always ? "Всегда в ротации Норы" : "Еженедельная ротация доверия Норы",
                            CredCost = cost,
                            IsAlwaysAvailable = always,
                            ImageUrl = ImageCacheService.NormalizeImageUrl(icon),
                            IconGlyph = cat == "Оружие" ? "⚔" : (cat == "Ауры и Моды" ? "💠" : "📦")
                        });
                    }
                }
            }
            catch { }
        }

        private void UpdateSimarisTooltip()
        {
            if (string.IsNullOrWhiteSpace(SimarisTarget) || SimarisTarget == "Сканируется...")
            {
                SimarisLocationTooltip = "Наведите после синхронизации с Worldstate, чтобы увидеть планету и лучшую миссию для сканирования.";
                return;
            }
            string rawLookup = !string.IsNullOrWhiteSpace(_simarisTargetRaw) ? _simarisTargetRaw.Trim() : SimarisTarget.Trim();
            if (_simarisGuides.TryGetValue(rawLookup, out string? exact) ||
                _simarisGuides.TryGetValue(SimarisTarget.Trim(), out exact))
            {
                SimarisLocationTooltip = $"Цель: {SimarisTarget}\nГде искать: {exact}";
                return;
            }
            var best = _simarisGuides
                .Where(kv => rawLookup.Contains(kv.Key, StringComparison.OrdinalIgnoreCase) ||
                             kv.Key.Contains(rawLookup, StringComparison.OrdinalIgnoreCase) ||
                             SimarisTarget.Contains(kv.Key, StringComparison.OrdinalIgnoreCase) ||
                             kv.Key.Contains(SimarisTarget, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(kv => kv.Key.Length)
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(best.Value))
                SimarisLocationTooltip = $"Цель: {SimarisTarget}\nГде искать: {best.Value}";
            else
                SimarisLocationTooltip = $"Цель: {SimarisTarget}\nИспользуйте Захват / Зачистку миссии нужной фракции в Солнечной системе и следите за звуковым маячком Цефалона Симариса.";
        }

        public void InitializeDefaults()
        {
            LoadEventsSeed();
            LoadUiIcons();
            DateTime now = DateTime.UtcNow;
            var notifySet = StorageService.Instance.LoadCycleNotifications();
            Baro.NotifyEnabled = notifySet.Contains("baro");

            WorldCycles.Clear();
            WorldCycles.Add(new WorldCycleInfo { Id = "cetus", LocationName = "Равнины Эйдолона (Цетус)", Subtitle = "Охота на Эйдолонов • Цикл 150м", State = "День", ExpiryUtc = now.AddMinutes(30), IsDayOrWarm = true, NotifyEnabled = notifySet.Contains("cetus"), NotifyTarget = "Наступление Ночи", IconText = "☀" });
            WorldCycles.Add(new WorldCycleInfo { Id = "earth", LocationName = "Земля (Леса Гринир)", Subtitle = "Растения Апотиков • Цикл 4ч", State = "День", ExpiryUtc = now.AddHours(1), IsDayOrWarm = true, NotifyEnabled = notifySet.Contains("earth"), NotifyTarget = "Смена Дня / Ночи", IconText = "🌿" });
            WorldCycles.Add(new WorldCycleInfo { Id = "vallis", LocationName = "Долина Сфер (Фортуна)", Subtitle = "Рыбалка и Разрывы Термии • 26м", State = "Холод", ExpiryUtc = now.AddMinutes(10), IsDayOrWarm = false, NotifyEnabled = notifySet.Contains("vallis"), NotifyTarget = "Наступление Тепла", IconText = "❄" });
            WorldCycles.Add(new WorldCycleInfo { Id = "cambion", LocationName = "Камбионский Дрейф (Деймос)", Subtitle = "Фасс / Воум • Цикл 150м", State = "Фасс (Fass)", ExpiryUtc = now.AddMinutes(40), IsDayOrWarm = true, NotifyEnabled = notifySet.Contains("cambion"), NotifyTarget = "Смена фазы Деймоса", IconText = "👁" });
            WorldCycles.Add(new WorldCycleInfo { Id = "zariman", LocationName = "Зариман 10-0", Subtitle = "Каскад Бездны и Перья • Цикл 5ч", State = "Корпус", ExpiryUtc = now.AddHours(2), IsDayOrWarm = true, NotifyEnabled = notifySet.Contains("zariman"), NotifyTarget = "Смена Фракции", IconText = "⚡" });

            DuviriMood.CurrentMood = "Печаль (Sorrow)";
            DuviriMood.RawStateKey = "sorrow";
            DuviriMood.ExpiryUtc = now.AddHours(1);
            DuviriMood.MoodDescription = GetDuviriMoodDescription("sorrow");

            var savedMoods = StorageService.Instance.LoadTrackedMoods();
            DuviriMoodOptions.Clear();
            DuviriMoodOptions.Add(new DuviriMoodTrackOption { Key = "sorrow", NameRu = "Печаль", BuffShort = "Холод / Замедление", IsTracked = savedMoods.Contains("sorrow"), OnChangedCallback = OnDuviriMoodOptionChanged });
            DuviriMoodOptions.Add(new DuviriMoodTrackOption { Key = "anger", NameRu = "Гнев", BuffShort = "Огонь / Поджоги", IsTracked = savedMoods.Contains("anger"), OnChangedCallback = OnDuviriMoodOptionChanged });
            DuviriMoodOptions.Add(new DuviriMoodTrackOption { Key = "joy", NameRu = "Радость", BuffShort = "Лечение / Реген", IsTracked = savedMoods.Contains("joy"), OnChangedCallback = OnDuviriMoodOptionChanged });
            DuviriMoodOptions.Add(new DuviriMoodTrackOption { Key = "envy", NameRu = "Зависть", BuffShort = "Токсин / Вампиризм", IsTracked = savedMoods.Contains("envy"), OnChangedCallback = OnDuviriMoodOptionChanged });
            DuviriMoodOptions.Add(new DuviriMoodTrackOption { Key = "fear", NameRu = "Страх", BuffShort = "Электро / Стан", IsTracked = savedMoods.Contains("fear"), OnChangedCallback = OnDuviriMoodOptionChanged });
            CheckDuviriMoodTracking();

            BuildDuviriRotationSchedule();
            BuildWeeklyChecklist(now);
            InitializeNightwaveDefaults(now);

            // Seed permanent Varzia Aya entries so Regular Aya is immediately browsable offline too
            if (Varzia.FeaturedItems.Count == 0 && _varziaPermanentAya.Count > 0)
            {
                Varzia.FeaturedItems.AddRange(_varziaPermanentAya);
                ApplyVarziaFilter();
            }

            // Default Archon Shard base crystal + Tauforged glow overlay before first sync
            var (defBaseUrl, defGlowUrl) = WorldStateService.GetArchonShardIconUrls("boreal");
            ArchonHunt.ShardBaseIconUrl = ImageCacheService.NormalizeImageUrl(defBaseUrl);
            ArchonHunt.ShardIconUrl = ImageCacheService.NormalizeImageUrl(
                _archonIconPaths.GetValueOrDefault("boreal", defGlowUrl));

            // Immediately hydrate last saved Worldstate snapshot if present on disk (0ms offline startup)
            string? cached = WorldStateService.Instance.LoadCachedWorldstateOnly();
            if (!string.IsNullOrEmpty(cached))
            {
                try
                {
                    using var doc = JsonDocument.Parse(cached);
                    ApplyParsedWorldstate(doc.RootElement, null, now);
                    SyncLabel = "Кэш (обновление...)";
                }
                catch { }
            }
            Tick(now, suppressNotifications: true);
            NotifyHudCountsChanged();
        }

        private void LoadUiIcons()
        {
            string? credPath = MainViewModel.GetItemImageUrl("GenericCreditIcon10.png")
                ?? ResolveAssetUpwards("items", "GenericCreditIcon10.png");
            CreditsIconBitmap ??= LoadLocalBitmapSafe(credPath);

            UiIconNavEvents = LoadLocalBitmapSafe(ResolveUiAssetPath("nav_events.png"));
            UiIconNavAlerts = LoadLocalBitmapSafe(ResolveUiAssetPath("nav_alerts.png"));
            UiIconNavInvasions = LoadLocalBitmapSafe(ResolveUiAssetPath("nav_invasions.png"));
            UiIconNavFissures = LoadLocalBitmapSafe(ResolveUiAssetPath("nav_fissures.png"));
            UiIconNavSortie = LoadLocalBitmapSafe(ResolveUiAssetPath("nav_sortie.png"));
            UiIconNavArchon = LoadLocalBitmapSafe(ResolveUiAssetPath("nav_archon.png"));
            UiIconNavTraders = LoadLocalBitmapSafe(ResolveUiAssetPath("nav_traders.png"));
            UiIconNightwave = LoadLocalBitmapSafe(ResolveUiAssetPath("nightwave_emblem.png"));
            UiIconArbitrationHexis = LoadLocalBitmapSafe(ResolveUiAssetPath("arbitration_hexis.png"));
            UiIconArbitrationBadge = LoadLocalBitmapSafe(ResolveUiAssetPath("arbitration_badge.png"));
            UiIconDuviriBanner = LoadLocalBitmapSafe(ResolveUiAssetPath("btn_duviri.png"), 380);
            UiIconFissureOrigin = LoadLocalBitmapSafe(ResolveUiAssetPath("fissure_origin.png"));
            UiIconFissureSteelPath = LoadLocalBitmapSafe(ResolveUiAssetPath("fissure_steelpath.png"));
            UiIconFissureProxima = LoadLocalBitmapSafe(ResolveUiAssetPath("fissure_proxima.png"));

            OnPropertyChanged(nameof(CreditsIconBitmap));
            OnPropertyChanged(nameof(UiIconNavEvents));
            OnPropertyChanged(nameof(UiIconNavAlerts));
            OnPropertyChanged(nameof(UiIconNavInvasions));
            OnPropertyChanged(nameof(UiIconNavFissures));
            OnPropertyChanged(nameof(UiIconNavSortie));
            OnPropertyChanged(nameof(UiIconNavArchon));
            OnPropertyChanged(nameof(UiIconNavTraders));
            OnPropertyChanged(nameof(UiIconNightwave));
            OnPropertyChanged(nameof(UiIconArbitrationHexis));
            OnPropertyChanged(nameof(UiIconArbitrationBadge));
            OnPropertyChanged(nameof(UiIconDuviriBanner));
            OnPropertyChanged(nameof(UiIconFissureOrigin));
            OnPropertyChanged(nameof(UiIconFissureSteelPath));
            OnPropertyChanged(nameof(UiIconFissureProxima));

            OnPropertyChanged(nameof(HasUiIconNavEvents));
            OnPropertyChanged(nameof(HasUiIconNavAlerts));
            OnPropertyChanged(nameof(HasUiIconNavInvasions));
            OnPropertyChanged(nameof(HasUiIconNavFissures));
            OnPropertyChanged(nameof(HasUiIconNavSortie));
            OnPropertyChanged(nameof(HasUiIconNavArchon));
            OnPropertyChanged(nameof(HasUiIconNavTraders));
            OnPropertyChanged(nameof(HasUiIconNightwave));
            OnPropertyChanged(nameof(HasUiIconArbitrationHexis));
            OnPropertyChanged(nameof(HasUiIconArbitrationBadge));
            OnPropertyChanged(nameof(HasUiIconDuviriBanner));
            OnPropertyChanged(nameof(HasUiIconFissureOrigin));
            OnPropertyChanged(nameof(HasUiIconFissureSteelPath));
            OnPropertyChanged(nameof(HasUiIconFissureProxima));
        }

        private static string? ResolveUiAssetPath(string fileName) => ResolveAssetUpwards("ui", fileName);

        private static string? ResolveAssetUpwards(string subFolder, string fileName)
        {
            string dataDir = Path.GetDirectoryName(StorageService.Instance.SaveDirectoryPath)
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            string p1 = Path.Combine(dataDir, "assets", subFolder, fileName);
            if (File.Exists(p1)) return p1;

            string? cur = Path.TrimEndingDirectorySeparator(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(cur); i++)
            {
                string cand = Path.Combine(cur, "data", "assets", subFolder, fileName);
                if (File.Exists(cand)) return cand;
                cur = Directory.GetParent(cur)?.FullName;
            }
            return null;
        }

        private static BitmapImage? LoadLocalBitmapSafe(string? path, int decodeWidth = 96)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = decodeWidth;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private void NotifyHudCountsChanged()
        {
            OnPropertyChanged(nameof(HudEventsCount));
            OnPropertyChanged(nameof(HudAlertsCount));
            OnPropertyChanged(nameof(HudInvasionsCount));
            OnPropertyChanged(nameof(HudFissuresCount));
            OnPropertyChanged(nameof(HudSortieCount));
            OnPropertyChanged(nameof(HudArchonCount));
            OnPropertyChanged(nameof(HudTradersCount));
            OnPropertyChanged(nameof(HasActiveAlertItems));
        }

        private void BuildDuviriRotationSchedule()
        {
            AllDuviriRotations.Clear();
            AllDuviriRotations.Add(new DuviriRotationWeek { WeekNumber = 1, DateRange = "Ротация 1", IsCurrentWeek = true, NormalWarframes = new() { "Excalibur", "Trinity", "Ember" }, SteelPathIncarnons = new() { "Брэйтон", "Лато", "Скана", "Парис", "Кунаи" } });
            AllDuviriRotations.Add(new DuviriRotationWeek { WeekNumber = 2, DateRange = "Ротация 2", NormalWarframes = new() { "Loki", "Mag", "Rhino" }, SteelPathIncarnons = new() { "Вепрь", "Гаммакор", "Ангстрем", "Горгона", "Анку" } });
            AllDuviriRotations.Add(new DuviriRotationWeek { WeekNumber = 3, DateRange = "Ротация 3", NormalWarframes = new() { "Gara", "Khora", "Revenant" }, SteelPathIncarnons = new() { "Бо", "Латрон", "Фурис", "Фуракс", "Стран" } });
            AllDuviriRotations.Add(new DuviriRotationWeek { WeekNumber = 4, DateRange = "Ротация 4", NormalWarframes = new() { "Ash", "Frost", "Nyx" }, SteelPathIncarnons = new() { "Лекс", "Магистр", "Болтор", "Бронко", "Керамический кинжал" } });
            AllDuviriRotations.Add(new DuviriRotationWeek { WeekNumber = 5, DateRange = "Ротация 5", NormalWarframes = new() { "Saryn", "Vauban", "Nova" }, SteelPathIncarnons = new() { "Торид", "Парные Токсоцисты", "Парные Ихоры", "Митра", "Атомос" } });
            AllDuviriRotations.Add(new DuviriRotationWeek { WeekNumber = 6, DateRange = "Ротация 6", NormalWarframes = new() { "Nekros", "Valkyr", "Oberon" }, SteelPathIncarnons = new() { "Ак и Брант", "Сома", "Васто", "Нами Соло", "Бёрстон" } });
            AllDuviriRotations.Add(new DuviriRotationWeek { WeekNumber = 7, DateRange = "Ротация 7", NormalWarframes = new() { "Ivara", "Inaros", "Titania" }, SteelPathIncarnons = new() { "Зайлок", "Сибирь", "Страх", "Отчаяние", "Ненависть" } });
            AllDuviriRotations.Add(new DuviriRotationWeek { WeekNumber = 8, DateRange = "Ротация 8", NormalWarframes = new() { "Nidus", "Octavia", "Harrow" }, SteelPathIncarnons = new() { "Дера", "Сибарис", "Цестра", "Сикарус", "Окина" } });
            AllDuviriRotations.Add(new DuviriRotationWeek { WeekNumber = 9, DateRange = "Ротация 9", NormalWarframes = new() { "Garuda", "Baruuk", "Hildryn" }, SteelPathIncarnons = new() { "Вектис", "Стаг", "Баллистика", "Дестреза", "Обекс" } });

            var savedBookmarks = StorageService.Instance.LoadBookmarkedIncarnonWeeks();
            foreach (var week in AllDuviriRotations)
                week.HasUserBookmark = savedBookmarks.Contains(week.WeekNumber);

            UpdateDuviriRelativeTags(1);
            RefreshDuviriCircuitIcons();
            ApplyIncarnonFilter();
        }

        public void RefreshDuviriCircuitIcons()
        {
            var catalog = _getCatalog();
            foreach (var week in AllDuviriRotations)
                SyncWeekCircuitTiles(week, catalog);
            if (catalog.Count > 0)
            {
                foreach (var tile in DuviriCurrentNormalFrames)
                {
                    string? resolvedUrl = ResolveCatalogArtUrl(tile.Name, false, catalog);
                    if (!string.IsNullOrEmpty(resolvedUrl))
                        tile.SetImageUrl(resolvedUrl);
                }
            }
        }

        private void UpdateDuviriRelativeTags(int currentWeekNumber)
        {
            int total = AllDuviriRotations.Count;
            if (total == 0) return;
            int prevNum = currentWeekNumber <= 1 ? total : currentWeekNumber - 1;
            int nextNum = currentWeekNumber >= total ? 1 : currentWeekNumber + 1;
            foreach (var w in AllDuviriRotations)
            {
                w.IsCurrentWeek = w.WeekNumber == currentWeekNumber;
                w.RelativeWeekTag = w.IsCurrentWeek
                    ? "★ ТЕКУЩАЯ"
                    : (w.WeekNumber == prevNum ? "◀ Прошлая" : (w.WeekNumber == nextNum ? "Следующая ▶" : string.Empty));
                w.DateRange = $"Неделя {w.WeekNumber}";
                w.NotifyMetaChanged();
            }
        }

        private static void SyncWeekCircuitTiles(DuviriRotationWeek week, IReadOnlyList<CollectionItemModel> catalog)
        {
            SyncTileList(week.WarframeTiles, week.NormalWarframes, false, catalog);
            SyncTileList(week.IncarnonTiles, week.SteelPathIncarnons, true, catalog);
        }

        private static void SyncTileList(
            ObservableCollection<DuviriCircuitTile> target,
            List<string> names,
            bool isIncarnon,
            IReadOnlyList<CollectionItemModel> catalog)
        {
            bool matchesExisting = target.Count == names.Count &&
                                   Enumerable.Range(0, names.Count).All(i => target[i].Name.Equals(names[i], StringComparison.Ordinal));
            if (!matchesExisting)
            {
                target.Clear();
                foreach (string name in names)
                {
                    target.Add(new DuviriCircuitTile
                    {
                        Name = name,
                        ShortLabel = name.Length > 10 ? name[..9] + "…" : name,
                        IsIncarnon = isIncarnon
                    });
                }
            }

            if (catalog.Count == 0) return;
            foreach (var tile in target)
            {
                string? resolvedUrl = ResolveCatalogArtUrl(tile.Name, isIncarnon, catalog);
                if (!string.IsNullOrEmpty(resolvedUrl))
                    tile.SetImageUrl(resolvedUrl);
            }
        }

        private static string? ResolveCatalogArtUrl(string itemName, bool isWeapon, IReadOnlyList<CollectionItemModel> catalog)
        {
            static string BaseName(string s)
            {
                int idx = s.IndexOf(" (", StringComparison.Ordinal);
                return idx > 0 ? s[..idx].Trim() : s.Trim();
            }

            string normalized = itemName.Trim().ToLowerInvariant() switch
            {
                "брейтон" => "Брэйтон",
                "кунай" => "Кунаи",
                _ => itemName.Trim()
            };

            IEnumerable<CollectionItemModel> pool = isWeapon
                ? catalog.Where(c => c.Category is "Основное" or "Вторичное" or "Ближний бой")
                : catalog.Where(c => c.Category == "Варфреймы");

            var candidates = pool.Where(c =>
                BaseName(c.Name).Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                c.NameEn.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                c.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 0 && normalized.Contains(' ', StringComparison.Ordinal))
            {
                candidates = pool.Where(c =>
                    c.Name.StartsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
                    c.NameEn.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            var best = candidates
                .OrderBy(c => c.IsPrime ? 1 : 0)
                .ThenBy(c => c.SubCategory is "Кува" or "Догмат" or "Кода 1999" or "Призма" or "Вандал" or "Врайт" ? 1 : 0)
                .ThenBy(c => c.Name.Length)
                .FirstOrDefault();

            if (best == null) return null;
            if (!string.IsNullOrEmpty(best.IconPath))
                return ImageCacheService.NormalizeImageUrl(best.IconPath);
            return best.ThumbnailUrl;
        }

        private static string GetWeekStartKey(DateTime utcNow)
        {
            int daysFromMon = ((int)utcNow.DayOfWeek + 6) % 7;
            return utcNow.Date.AddDays(-daysFromMon).ToString("yyyy-MM-dd");
        }

        private void BuildWeeklyChecklist(DateTime utcNow)
        {
            string key = GetWeekStartKey(utcNow);
            if (_currentWeekKey == key && WeeklyChecklist.Count > 0) return;
            _currentWeekKey = key;

            var doneSet = StorageService.Instance.LoadWeeklyChecklist(key);
            void SaveCb()
            {
                StorageService.Instance.SaveWeeklyChecklist(key, WeeklyChecklist.Where(i => i.IsCompleted).Select(i => i.Id));
                NotifyHudCountsChanged();
            }

            WeeklyChecklist.Clear();
            var entries = new[]
            {
                ("ayatan", "Охота за сокровищами Аятан (Еженед.)", "Базар Мэру (Марс) • 1 скульптура Аятан в неделю"),
                ("clem", "Помогите Клему", "Реле Strata / Дарво • Еженедельные чертежи Спектров"),
                ("archon", "Охота на Архонта", "Осколок Архонта + награда вылазки"),
                ("netracells", "Нетракамеры (5/5 зарядов)", "Санктум Анатомика: Мистификаторы & Осколки"),
                ("deep_archimedea", "Глубокая Архимедия (Лаборатории)", "3 миссии • Осколки Тау + Мистификаторы"),
                ("temp_archimedea", "Временная Архимедия (1999)", "Торговый Центр Хёльвании • Наклейки Печатного цеха + Осколки"),
                ("calendar_1999", "Календарь 1999 (Хекс)", "Большие призы, Форма Умбра / Адаптеры"),
                ("duviri_circuit", "Цепь Дувири (Обычная / СП)", "Инкарнон-адаптеры и чертежи фреймов"),
                ("palladino", "Палладино: Мод Разлома + Кува", "Железная Цитадель (Земля) • за 10 Осколков"),
                ("teshin_sp", "Почести Тешина (Стальной Путь)", "Еженедельная ротация за Стальную Эссенцию")
            };

            foreach (var (id, title, hint) in entries)
            {
                WeeklyChecklist.Add(new WeeklyChecklistItem
                {
                    Id = id,
                    Title = title,
                    RewardHint = hint,
                    IsCompleted = doneSet.Contains(id),
                    OnChanged = SaveCb
                });
            }
        }

        public Task FetchLiveWorldstateAsync(bool forceNotify = false)
        {
            Task? existing;
            lock (_fetchLock)
            {
                if (_activeFetchTask != null && !_activeFetchTask.IsCompleted)
                {
                    existing = _activeFetchTask;
                }
                else
                {
                    _isFetching = true;
                    // Run on thread pool OUTSIDE the lock so RunOnUiAsync can never AB-BA deadlock
                    existing = _activeFetchTask = Task.Run(() => FetchInternalAsync(forceNotify));
                }
            }
            return existing;
        }

        private async Task FetchInternalAsync(bool forceNotify)
        {
            lock (_fetchLock) { _lastAutoSyncUtc = DateTime.UtcNow; }

            await RunOnUiAsync(() =>
            {
                IsRefreshing = true;
                if (forceNotify) SyncLabel = "Обновление Worldstate...";
            }).ConfigureAwait(false);

            try
            {
                string? rawJson = await WorldStateService.Instance.FetchOrLoadCachedWorldstateJsonAsync().ConfigureAwait(false);
                DateTime utcNow = DateTime.UtcNow;
                bool liveApi = WorldStateService.Instance.LastFetchWasLive;
                var catalog = _getCatalog();

                JsonElement? rootNullable = null;
                if (!string.IsNullOrEmpty(rawJson))
                {
                    using var doc = JsonDocument.Parse(rawJson);
                    rootNullable = doc.RootElement.Clone();
                }

                var dictTask = WorldStateService.Instance.EnsureEnRuDictionaryLoadedAsync();
                Task<ArbitrationModel?> arbyTask = rootNullable.HasValue
                    ? WorldStateService.Instance.ResolveCurrentArbitrationAsync(rootNullable.Value, utcNow)
                    : Task.FromResult<ArbitrationModel?>(null);
                await Task.WhenAll(dictTask, arbyTask).ConfigureAwait(false);
                var arbyResolved = arbyTask.Result;

                bool hasLiveFissures = liveApi && rootNullable.HasValue &&
                    rootNullable.Value.TryGetProperty("fissures", out var fArrCheck) &&
                    fArrCheck.ValueKind == JsonValueKind.Array && fArrCheck.GetArrayLength() > 0;

                WorldStateService.DeOfficialWorldFallback? deFallback = null;
                if (!hasLiveFissures)
                {
                    deFallback = await WorldStateService.Instance.FetchOfficialDeFissuresAndThreatsAsync(utcNow, catalog).ConfigureAwait(false);
                }

                if (!rootNullable.HasValue && deFallback == null)
                {
                    await RunOnUiAsync(() => SyncLabel = "Оффлайн режим (нет связи с API)").ConfigureAwait(false);
                    return;
                }

                await RunOnUiAsync(() =>
                {
                    if (rootNullable.HasValue)
                        ApplyParsedWorldstate(rootNullable.Value, arbyResolved, utcNow);
                    else if (arbyResolved != null)
                        ParseArbitration(arbyResolved);

                    if (deFallback != null)
                        ApplyOfficialDeFallback(deFallback, liveApi);

                    string tsStr = rootNullable.HasValue ? WorldStateService.GetStr(rootNullable.Value, "timestamp") : string.Empty;
                    DateTime apiTs = WarframeApiService.ParseIsoOrFallback(tsStr, TimeSpan.Zero);
                    bool hasDeLive = deFallback != null && (deFallback.Fissures.Count > 0 || deFallback.SortieStages.Count > 0);
                    SyncLabel = liveApi
                        ? $"Live API • {apiTs.ToLocalTime():HH:mm:ss}"
                        : (hasDeLive
                            ? $"DE CDN + Кэш • {DateTime.Now:HH:mm:ss}"
                            : $"Кэш • {apiTs.ToLocalTime():HH:mm:ss}");
                    Tick(DateTime.UtcNow, suppressNotifications: true);

                    if (forceNotify)
                        _statusCallback($"✓ Worldstate синхронизирован ({DisplayFissures.Count} разрывов, {NewsItems.Count} новостей)");
                    else
                        _statusCallback($"● Синхронизировано с сервером Warframe ({DateTime.Now:HH:mm})");
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await RunOnUiAsync(() => SyncLabel = $"Ошибка API: {ex.Message}").ConfigureAwait(false);
            }
            finally
            {
                lock (_fetchLock)
                {
                    _isFetching = false;
                    _activeFetchTask = null;
                }
                await RunOnUiAsync(() => IsRefreshing = false).ConfigureAwait(false);
            }
        }

        private void ApplyOfficialDeFallback(WorldStateService.DeOfficialWorldFallback de, bool liveApi)
        {
            if (de.Fissures.Count > 0)
            {
                foreach (var f in de.Fissures)
                    f.TierIconUrl = MainViewModel.GetTierRelicIcon(f.Tier);
                AllFissures.Clear();
                AllFissures.AddRange(de.Fissures);
                RebuildFissureRegionOptions();
                ApplyFissuresFilter();
            }

            if (liveApi) return;

            if (de.FomorianPercent.HasValue)
            {
                Threats = new ThreatProgressModel
                {
                    FomorianPercent = de.FomorianPercent.Value,
                    RazorbackPercent = de.RazorbackPercent ?? Threats.RazorbackPercent
                };
                OnPropertyChanged(nameof(Threats));
            }

            if (de.SortieExpiryUtc.HasValue && de.SortieStages.Count > 0)
            {
                DailySortie.Boss = string.IsNullOrEmpty(de.SortieBoss) ? DailySortie.Boss : de.SortieBoss;
                DailySortie.Faction = string.IsNullOrEmpty(de.SortieFaction) ? DailySortie.Faction : de.SortieFaction;
                DailySortie.ExpiryUtc = de.SortieExpiryUtc.Value;
                DailySortie.Stages.Clear();
                foreach (var st in de.SortieStages) DailySortie.Stages.Add(st);
                DailySortie.RefreshAll();
            }

            if (de.ArchonExpiryUtc.HasValue && de.ArchonStages.Count > 0 && !string.IsNullOrEmpty(de.ArchonRawBoss))
            {
                string shardKey = de.ArchonRawBoss.Contains("Boreal", StringComparison.OrdinalIgnoreCase) ? "boreal"
                    : (de.ArchonRawBoss.Contains("Amar", StringComparison.OrdinalIgnoreCase) ? "amar"
                    : (de.ArchonRawBoss.Contains("Nira", StringComparison.OrdinalIgnoreCase) ? "nira" : "amar"));
                ArchonHunt.Boss = de.ArchonBossRu ?? ArchonHunt.Boss;
                ArchonHunt.ShardTitle = de.ArchonShardTitle ?? ArchonHunt.ShardTitle;
                ArchonHunt.ShardColor = de.ArchonShardColor ?? ArchonHunt.ShardColor;
                var (baseUrl, glowUrl) = WorldStateService.GetArchonShardIconUrls(de.ArchonRawBoss);
                ArchonHunt.ShardBaseIconUrl = ImageCacheService.NormalizeImageUrl(baseUrl);
                ArchonHunt.ShardIconUrl = ImageCacheService.NormalizeImageUrl(_archonIconPaths.GetValueOrDefault(shardKey, glowUrl));
                ArchonHunt.ExpiryUtc = de.ArchonExpiryUtc.Value;
                ArchonHunt.Stages.Clear();
                foreach (var st in de.ArchonStages) ArchonHunt.Stages.Add(st);
                ArchonHunt.RefreshAll();
            }

            if (de.DarvoDeal != null)
            {
                DarvoDeal.ItemName = de.DarvoDeal.ItemName;
                DarvoDeal.OriginalPrice = de.DarvoDeal.OriginalPrice;
                DarvoDeal.SalePrice = de.DarvoDeal.SalePrice;
                DarvoDeal.DiscountPercent = de.DarvoDeal.DiscountPercent;
                DarvoDeal.TotalCount = de.DarvoDeal.TotalCount;
                DarvoDeal.SoldCount = de.DarvoDeal.SoldCount;
                DarvoDeal.ExpiryUtc = de.DarvoDeal.ExpiryUtc;
                DarvoDeal.RefreshAll();
            }

            if (de.TraderExpiryUtc.HasValue && de.TraderActivationUtc.HasValue)
            {
                Baro.CharacterName = de.TraderCharacter ?? "Баро Ки'Тиир";
                if (!string.IsNullOrEmpty(de.TraderLocation)) Baro.Location = de.TraderLocation;
                Baro.ActivationUtc = de.TraderActivationUtc.Value;
                Baro.ExpiryUtc = de.TraderExpiryUtc.Value;
                Baro.IsActive = DateTime.UtcNow >= Baro.ActivationUtc && DateTime.UtcNow < Baro.ExpiryUtc;
                Baro.RefreshAll();
            }

            if (de.Alerts.Count > 0 || ActiveAlerts.Count > 0)
            {
                ActiveAlerts.Clear();
                foreach (var a in de.Alerts) ActiveAlerts.Add(a);
            }

            GlobalBoosters.Clear();
            foreach (var b in de.Boosters) GlobalBoosters.Add(b);
            OnPropertyChanged(nameof(HasGlobalBoosters));

            if (de.NightwaveExpiryUtc.HasValue)
            {
                Nightwave.ExpiryUtc = de.NightwaveExpiryUtc.Value;
                if (!string.IsNullOrEmpty(de.NightwaveTag))
                    Nightwave.SeasonTitle = FormatNightwaveSeasonTitle(de.NightwaveTag, de.NightwavePhase ?? 0);
                if (de.NightwaveChallenges.Count > 0)
                {
                    Nightwave.Challenges.Clear();
                    foreach (var c in de.NightwaveChallenges.OrderBy(x => x.Reputation).ThenBy(x => x.ExpiryUtc))
                        Nightwave.Challenges.Add(c);
                }
                Nightwave.RefreshAll();
            }

            if (de.DescendiaExpiryUtc.HasValue && de.DescendiaChallenges.Count > 0)
            {
                Descendia.ExpiryUtc = de.DescendiaExpiryUtc.Value;
                Descendia.Challenges.Clear();
                foreach (var ch in de.DescendiaChallenges) Descendia.Challenges.Add(ch);
                Descendia.RefreshAll();
            }

            NotifyHudCountsChanged();
        }

        private void ApplyParsedWorldstate(JsonElement root, ArbitrationModel? arbyResolved, DateTime utcNow)
        {
            var catalog = _getCatalog();
            ParseWorldCycles(root);
            ParseDuviri(root, catalog);
            ParseNews(root);
            ParseGlobalBoosters(root, utcNow);
            ParseDailySortie(root);
            if (arbyResolved != null) ParseArbitration(arbyResolved);
            ParseDarvoDeal(root, catalog);
            ParseArchonHunt(root);
            ParseArchimedeas(root);
            ParseDescendia(root);
            ParseHexCalendar(root, catalog);
            ParseSteelPath(root);
            ParseFissures(root);
            ParseAlerts(root, utcNow, catalog);
            ParseInvasions(root, catalog);
            ParseOperations(root);
            ParseThreatProgress(root);
            ParseVarzia(root, catalog);
            ParseBaro(root, catalog);
            ParseNightwave(root, utcNow);
            ParseMiscIndicators(root);
            NotifyHudCountsChanged();
        }

        private void ParseWorldCycles(JsonElement root)
        {
            if (root.TryGetProperty("cetusCycle", out var cetusEl))
            {
                bool isDay = cetusEl.TryGetProperty("isDay", out var id) && id.GetBoolean();
                DateTime exp = WarframeApiService.ParseIsoOrFallback(WorldStateService.GetStr(cetusEl, "expiry"), TimeSpan.FromMinutes(30));
                UpdateCycle("cetus", isDay ? "День" : "Ночь", isDay, exp, isDay ? "☀" : "🌙");
            }
            if (root.TryGetProperty("earthCycle", out var earthEl))
            {
                bool isDay = earthEl.TryGetProperty("isDay", out var id) && id.GetBoolean();
                DateTime exp = WarframeApiService.ParseIsoOrFallback(WorldStateService.GetStr(earthEl, "expiry"), TimeSpan.FromHours(2));
                UpdateCycle("earth", isDay ? "День" : "Ночь", isDay, exp, isDay ? "🌿" : "🌑");
            }
            if (root.TryGetProperty("vallisCycle", out var valEl))
            {
                bool isWarm = valEl.TryGetProperty("isWarm", out var iw) && iw.GetBoolean();
                DateTime exp = WarframeApiService.ParseIsoOrFallback(WorldStateService.GetStr(valEl, "expiry"), TimeSpan.FromMinutes(10));
                UpdateCycle("vallis", isWarm ? "Тепло" : "Холод", isWarm, exp, isWarm ? "🔥" : "❄");
            }
            if (root.TryGetProperty("cambionCycle", out var camEl))
            {
                string st = WorldStateService.GetStr(camEl, "state", "fass");
                bool isFass = st.Equals("fass", StringComparison.OrdinalIgnoreCase);
                DateTime exp = WarframeApiService.ParseIsoOrFallback(WorldStateService.GetStr(camEl, "expiry"), TimeSpan.FromMinutes(45));
                UpdateCycle("cambion", isFass ? "Фасс (Fass)" : "Воум (Vome)", isFass, exp, isFass ? "👁" : "✨");
            }
            if (root.TryGetProperty("zarimanCycle", out var zarEl))
            {
                string st = WorldStateService.GetStr(zarEl, "state", "corpus");
                bool isCorpus = st.Equals("corpus", StringComparison.OrdinalIgnoreCase);
                DateTime exp = WarframeApiService.ParseIsoOrFallback(WorldStateService.GetStr(zarEl, "expiry"), TimeSpan.FromHours(2));
                UpdateCycle("zariman", isCorpus ? "Корпус" : "Гринир", isCorpus, exp, isCorpus ? "⚡" : "🔨");
            }
        }

        private void UpdateCycle(string id, string state, bool isDayOrWarm, DateTime exp, string icon)
        {
            var c = WorldCycles.FirstOrDefault(x => x.Id == id);
            if (c == null) return;
            c.State = state;
            c.IsDayOrWarm = isDayOrWarm;
            c.ExpiryUtc = exp;
            c.IconText = icon;
        }

        private void ParseDuviri(JsonElement root, IReadOnlyList<CollectionItemModel> catalog)
        {
            if (!root.TryGetProperty("duviriCycle", out var dEl) || dEl.ValueKind != JsonValueKind.Object) return;
            string rawState = WorldStateService.GetStr(dEl, "state", "sorrow");
            DuviriMood.RawStateKey = rawState;
            DuviriMood.CurrentMood = GetDuviriMoodName(rawState);
            DuviriMood.MoodDescription = GetDuviriMoodDescription(rawState);
            DuviriMood.ExpiryUtc = WarframeApiService.ParseIsoOrFallback(WorldStateService.GetStr(dEl, "expiry"), TimeSpan.FromHours(1));
            CheckDuviriMoodTracking();

            if (dEl.TryGetProperty("choices", out var choicesArr) && choicesArr.ValueKind == JsonValueKind.Array)
            {
                var liveFrames = new List<string>();
                var liveIncarnons = new List<string>();
                foreach (var cat in choicesArr.EnumerateArray())
                {
                    string ck = WorldStateService.GetStr(cat, "category");
                    if (!cat.TryGetProperty("choices", out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                    foreach (var ch in arr.EnumerateArray())
                    {
                        string val = ch.GetString() ?? "";
                        if (string.IsNullOrEmpty(val)) continue;
                        if (ck == "normal")
                        {
                            string localizedFrame = WorldStateService.ResolveLocalizedCatalogName(null, val, catalog);
                            liveFrames.Add(string.IsNullOrWhiteSpace(localizedFrame) ? val : localizedFrame);
                        }
                        else if (ck == "hard")
                        {
                            liveIncarnons.Add(TranslateIncarnonToRu(val));
                        }
                    }
                }

                if (liveFrames.Count > 0)
                {
                    SyncTileList(DuviriCurrentNormalFrames, liveFrames, false, catalog);
                    OnPropertyChanged(nameof(HasDuviriCurrentNormalFrames));
                }

                DuviriRotationWeek? matched = null;
                if (liveIncarnons.Count > 0)
                {
                    matched = AllDuviriRotations.FirstOrDefault(w =>
                        w.SteelPathIncarnons.Any(inc => liveIncarnons.Contains(inc, StringComparer.OrdinalIgnoreCase)));
                }
                matched ??= AllDuviriRotations.FirstOrDefault();

                if (matched != null)
                {
                    // Never overwrite matched.NormalWarframes: Normal (11w) and Steel Path Incarnon (9w) rotate independently
                    if (liveIncarnons.Count >= 5 && !matched.SteelPathIncarnons.Any(inc => liveIncarnons.Contains(inc, StringComparer.OrdinalIgnoreCase)))
                        matched.SteelPathIncarnons = liveIncarnons;
                    UpdateDuviriRelativeTags(matched.WeekNumber);
                    RefreshDuviriCircuitIcons();
                    ApplyIncarnonFilter();
                }
            }
        }

        private void ParseNews(JsonElement root)
        {
            if (!root.TryGetProperty("news", out var newsArr) || newsArr.ValueKind != JsonValueKind.Array) return;
            var items = new List<NewsItemModel>();
            foreach (var n in newsArr.EnumerateArray())
            {
                string title = WorldStateService.GetStr(n, "message");
                if (n.TryGetProperty("translations", out var tr) && tr.ValueKind == JsonValueKind.Object)
                {
                    string ru = WorldStateService.GetStr(tr, "ru");
                    if (!string.IsNullOrWhiteSpace(ru)) title = ru;
                }
                if (string.IsNullOrWhiteSpace(title)) continue;

                bool isUpdate = n.TryGetProperty("update", out var up) && up.ValueKind == JsonValueKind.True;
                bool isPrime = n.TryGetProperty("primeAccess", out var pa) && pa.ValueKind == JsonValueKind.True;
                bool isStream = n.TryGetProperty("stream", out var st) && st.ValueKind == JsonValueKind.True;
                bool isPrio = n.TryGetProperty("priority", out var pr) && pr.ValueKind == JsonValueKind.True;

                string dateStr = WorldStateService.GetStr(n, "date");
                bool hasDate = WarframeApiService.TryParseIso(dateStr, out var dt) && dt.Year > 1990;
                DateTime publishedUtc = hasDate ? dt.ToUniversalTime() : DateTime.MinValue;
                string dateDisp = hasDate ? dt.ToLocalTime().ToString("dd.MM.yyyy") : "Закреплено";

                string cat = isUpdate ? "ОБНОВЛЕНИЕ" : (isPrime ? "PRIME ACCESS" : (isStream ? "СТРИМ" : "НОВОСТЬ"));
                string col = isUpdate ? "#10B981" : (isPrime ? "#F59E0B" : (isStream ? "#A855F7" : "#38BDF8"));

                items.Add(new NewsItemModel
                {
                    Title = title,
                    DateDisplay = dateDisp,
                    PublishedUtc = publishedUtc,
                    LinkUrl = WorldStateService.GetStr(n, "link"),
                    ImageUrl = WorldStateService.GetStr(n, "imageLink"),
                    CategoryLabel = cat,
                    CategoryColor = col,
                    IsPriority = isPrio || isUpdate
                });
            }

            items = items
                .OrderByDescending(x => x.PublishedUtc)
                .ThenByDescending(x => x.IsPriority)
                .ToList();
            NewsItems.Clear();
            foreach (var item in items.Take(16)) NewsItems.Add(item);
        }

        private void ParseGlobalBoosters(JsonElement root, DateTime utcNow)
        {
            GlobalBoosters.Clear();
            if (root.TryGetProperty("globalUpgrades", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in arr.EnumerateArray())
                {
                    DateTime exp = WarframeApiService.ParseIsoOrFallback(WorldStateService.GetStr(g, "expiry"), TimeSpan.FromHours(12));
                    if (exp <= utcNow) continue;
                    string upg = WorldStateService.GetStr(g, "upgrade");
                    string sym = WorldStateService.GetStr(g, "operationSymbol", "x");
                    double val = g.TryGetProperty("upgradeOperationValue", out var vEl) && vEl.ValueKind == JsonValueKind.Number ? vEl.GetDouble() : 2;
                    GlobalBoosters.Add(new GlobalBoosterModel
                    {
                        Title = "Глобальный бустер",
                        EffectText = WorldStateService.TranslateGlobalUpgrade(upg, sym, val),
                        ExpiryUtc = exp
                    });
                }
            }
            OnPropertyChanged(nameof(HasGlobalBoosters));
        }

        private void ParseDailySortie(JsonElement root)
        {
            if (!root.TryGetProperty("sortie", out var sEl) || sEl.ValueKind != JsonValueKind.Object) return;
            DailySortie.Boss = WorldStateService.Instance.TranslateEnToRu(WorldStateService.GetStr(sEl, "boss", "Вылазка"));
            DailySortie.Faction = WorldStateService.TranslateFaction(WorldStateService.GetStr(sEl, "faction"));
            DailySortie.ExpiryUtc = WarframeApiService.ParseIsoOrFallback(WorldStateService.GetStr(sEl, "expiry"), TimeSpan.FromHours(12));
            DailySortie.Stages.Clear();
            if (sEl.TryGetProperty("variants", out var vars) && vars.ValueKind == JsonValueKind.Array)
            {
                int idx = 1;
                foreach (var v in vars.EnumerateArray())
                {
                    var (n, p) = WorldStateService.SplitAndTranslateNode(WorldStateService.GetStr(v, "node"));
                    DailySortie.Stages.Add(new SortieStageModel
                    {
                        Index = idx++,
                        Node = string.IsNullOrEmpty(p) ? n : $"{n} ({p})",
                        MissionType = WorldStateService.TranslateMission(WorldStateService.GetStr(v, "missionType")),
                        Modifier = WorldStateService.TranslateModifier(WorldStateService.GetStr(v, "modifier")),
                        ModifierDesc = WorldStateService.Instance.TranslateEnToRu(WorldStateService.GetStr(v, "modifierDescription"))
                    });
                }
            }
            DailySortie.RefreshAll();
        }

        private void ParseArbitration(ArbitrationModel? arb)
        {
            if (arb == null)
            {
                Arbitration.Node = "Ожидание обновления";
                Arbitration.Planet = "";
                Arbitration.MissionType = "Каждый час в :00";
                Arbitration.Faction = "";
                Arbitration.SourceNote = "API";
                Arbitration.ExpiryUtc = DateTime.UtcNow.AddMinutes(30);
                Arbitration.RefreshAll();
                return;
            }
            Arbitration.Node = arb.Node;
            Arbitration.Planet = arb.Planet;
            Arbitration.MissionType = arb.MissionType;
            Arbitration.Faction = arb.Faction;
            Arbitration.SourceNote = arb.SourceNote;
            Arbitration.ExpiryUtc = arb.ExpiryUtc;
            Arbitration.RefreshAll();
        }

        private void ParseDarvoDeal(JsonElement root, IReadOnlyList<CollectionItemModel> catalog)
        {
            if (root.TryGetProperty("dailyDeals", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
            {
                var d = arr[0];
                string rawItem = WorldStateService.GetStr(d, "item");
                string uid = WorldStateService.GetStr(d, "uniqueName");
                DarvoDeal.ItemName = WorldStateService.ResolveLocalizedCatalogName(uid, rawItem, catalog);
                DarvoDeal.OriginalPrice = d.TryGetProperty("originalPrice", out var op) ? op.GetInt32() : 0;
                DarvoDeal.SalePrice = d.TryGetProperty("salePrice", out var sp) ? sp.GetInt32() : 0;
                DarvoDeal.DiscountPercent = d.TryGetProperty("discount", out var dp) ? dp.GetInt32() : 0;
                DarvoDeal.TotalCount = d.TryGetProperty("total", out var tp) ? tp.GetInt32() : 0;
                DarvoDeal.SoldCount = d.TryGetProperty("sold", out var sop) ? sop.GetInt32() : 0;
                DarvoDeal.ExpiryUtc = WarframeApiService.ParseIsoOrFallback(WorldStateService.GetStr(d, "expiry"), TimeSpan.FromHours(12));
                DarvoDeal.RefreshAll();
            }
            else
            {
                DarvoDeal.ItemName = string.Empty;
                DarvoDeal.OriginalPrice = 0;
                DarvoDeal.SalePrice = 0;
                DarvoDeal.DiscountPercent = 0;
                DarvoDeal.TotalCount = 0;
                DarvoDeal.SoldCount = 0;
                DarvoDeal.TimeRemaining = "00м 00с";
                DarvoDeal.RefreshAll();
            }
        }

        private void ParseArchonHunt(JsonElement root)
        {
            if (!root.TryGetProperty("archonHunt", out var aEl) || aEl.ValueKind != JsonValueKind.Object) return;
            string rawBoss = WorldStateService.GetStr(aEl, "boss");
            var (bossRu, shardRu, shardCol) = WorldStateService.TranslateArchon(rawBoss);
            string shardKey = rawBoss.Contains("Boreal", StringComparison.OrdinalIgnoreCase) ? "boreal"
                : (rawBoss.Contains("Amar", StringComparison.OrdinalIgnoreCase) ? "amar"
                : (rawBoss.Contains("Nira", StringComparison.OrdinalIgnoreCase) ? "nira" : "amar"));
            ArchonHunt.Boss = bossRu;
            ArchonHunt.ShardTitle = shardRu;
            ArchonHunt.ShardColor = shardCol;
            var (baseUrl, glowUrl) = WorldStateService.GetArchonShardIconUrls(rawBoss);
            ArchonHunt.ShardBaseIconUrl = ImageCacheService.NormalizeImageUrl(baseUrl);
            ArchonHunt.ShardIconUrl = ImageCacheService.NormalizeImageUrl(
                _archonIconPaths.GetValueOrDefault(shardKey, glowUrl));
            ArchonHunt.ExpiryUtc = WarframeApiService.ParseIsoOrFallback(WorldStateService.GetStr(aEl, "expiry"), TimeSpan.FromDays(3));
            ArchonHunt.Stages.Clear();
            if (aEl.TryGetProperty("missions", out var ms) && ms.ValueKind == JsonValueKind.Array)
            {
                int idx = 1;
                foreach (var m in ms.EnumerateArray())
                {
                    var (n, p) = WorldStateService.SplitAndTranslateNode(WorldStateService.GetStr(m, "node"));
                    ArchonHunt.Stages.Add(new SortieStageModel
                    {
                        Index = idx++,
                        Node = string.IsNullOrEmpty(p) ? n : $"{n} ({p})",
                        MissionType = WorldStateService.TranslateMission(WorldStateService.GetStr(m, "type"))
                    });
                }
            }
            ArchonHunt.RefreshAll();
        }

        private void ParseArchimedeas(JsonElement root)
        {
            Archimedeas.Clear();
            if (!root.TryGetProperty("archimedeas", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            foreach (var a in arr.EnumerateArray())
            {
                string typeRaw = WorldStateService.GetStr(a, "type");
                bool isHex = typeRaw.Replace(" ", "").Contains("HEX", StringComparison.OrdinalIgnoreCase);
                var model = new ArchimedeaModel
                {
                    Title = isHex ? "Временная Архимедия (Хольвания 1999)" : "Глубокая Архимедия (Лаборатории Энтрати)",
                    AccentColor = isHex ? "#10B981" : "#A855F7",
                    ExpiryUtc = WarframeApiService.ParseIsoOrFallback(WorldStateService.GetStr(a, "expiry"), TimeSpan.FromDays(3))
                };

                if (a.TryGetProperty("missions", out var mArr) && mArr.ValueKind == JsonValueKind.Array)
                {
                    int idx = 1;
                    foreach (var m in mArr.EnumerateArray())
                    {
                        string devName = "", devDesc = "";
                        if (m.TryGetProperty("deviation", out var dev) && dev.ValueKind == JsonValueKind.Object)
                        {
                            devName = WorldStateService.Instance.TranslateEnToRu(WorldStateService.GetStr(dev, "name"));
                            devDesc = WorldStateService.Instance.TranslateEnToRu(WorldStateService.GetStr(dev, "description"));
                        }
                        var riskList = new List<string>();
                        if (m.TryGetProperty("risks", out var rArr) && rArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var r in rArr.EnumerateArray())
                            {
                                string rn = WorldStateService.Instance.TranslateEnToRu(WorldStateService.GetStr(r, "name"));
                                if (!string.IsNullOrEmpty(rn)) riskList.Add(rn);
                            }
                        }

                        var missionModel = new ArchimedeaMissionModel
                        {
                            Index = idx++,
                            MissionType = WorldStateService.TranslateMission(WorldStateService.GetStr(m, "missionType")),
                            Faction = WorldStateService.TranslateFaction(WorldStateService.GetStr(m, "faction")),
                            DeviationName = devName,
                            DeviationDesc = devDesc,
                            RisksSummary = riskList.Count > 0 ? string.Join(" • ", riskList) : ""
                        };
                        foreach (var rk in riskList) missionModel.Risks.Add(rk);
                        model.Missions.Add(missionModel);
                    }
                }

                if (a.TryGetProperty("personalModifiers", out var pArr) && pArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var pm in pArr.EnumerateArray())
                    {
                        model.PersonalModifiers.Add(new ArchimedeaPersonalModModel
                        {
                            Name = WorldStateService.Instance.TranslateEnToRu(WorldStateService.GetStr(pm, "name")),
                            Description = WorldStateService.Instance.TranslateEnToRu(WorldStateService.GetStr(pm, "description"))
                        });
                    }
                }
                Archimedeas.Add(model);
            }
        }

        private void ParseDescendia(JsonElement root)
        {
            Descendia.Challenges.Clear();
            if (!root.TryGetProperty("descendia", out var dEl) || dEl.ValueKind != JsonValueKind.Object) return;
            Descendia.ExpiryUtc = WarframeApiService.ParseIsoOrFallback(WorldStateService.GetStr(dEl, "expiry"), TimeSpan.FromDays(3));
            if (dEl.TryGetProperty("challenges", out var chArr) && chArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var ch in chArr.EnumerateArray())
                {
                    int floorIdx = ch.TryGetProperty("index", out var ix) ? ix.GetInt32() : 0;
                    string rawTypeKey = WorldStateService.GetStr(ch, "typeKey");
                    string rawType = !string.IsNullOrEmpty(rawTypeKey) ? rawTypeKey : WorldStateService.GetStr(ch, "type");
                    string missionRu = WorldStateService.TranslateMission(rawType);
                    string rawChallenge = WorldStateService.GetStr(ch, "challenge");
                    string chName = rawChallenge.Equals("Devil", StringComparison.OrdinalIgnoreCase)
                        ? "Уриэль (Дьявол)"
                        : WorldStateService.Instance.TranslateEnToRu(rawChallenge);

                    var auraNames = new List<string>();
                    if (ch.TryGetProperty("auras", out var aurasArr) && aurasArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var au in aurasArr.EnumerateArray())
                        {
                            string an = WorldStateService.GetStr(au, "name").Replace("Co H ", "").Trim();
                            if (!string.IsNullOrEmpty(an)) auraNames.Add(an);
                        }
                    }

                    string tip = $"Этаж #{floorIdx} • {missionRu}\nИспытание: {chName}";
                    if (floorIdx == 7 || floorIdx == 14) tip += "\nКонтрольная точка (Протофрейм)";
                    else if (floorIdx == 21) tip += "\nФинальный босс Спуска";
                    if (auraNames.Count > 0) tip += $"\nМодификатор: {string.Join(", ", auraNames)}";

                    Descendia.Challenges.Add(new DescendiaChallengeItem
                    {
                        Index = floorIdx,
                        MissionType = missionRu,
                        ChallengeName = chName,
                        IsMiniBoss = rawTypeKey.Equals("DT_BOSS", StringComparison.OrdinalIgnoreCase),
                        DetailTooltip = tip
                    });
                }
            }
            Descendia.RefreshAll();
        }

        private void ParseHexCalendar(JsonElement root, IReadOnlyList<CollectionItemModel> catalog)
        {
            HexCalendar.Prizes.Clear();
            HexCalendar.Tasks.Clear();
            HexCalendar.Overrides.Clear();
            if (!root.TryGetProperty("calendar", out var cEl) || cEl.ValueKind != JsonValueKind.Object) return;

            string season = WorldStateService.GetStr(cEl, "season", "1999");
            string seasonRu = season switch
            {
                "Spring" => "Весна 1999",
                "Summer" => "Лето 1999",
                "Fall" or "Autumn" => "Осень 1999",
                "Winter" => "Зима 1999",
                _ => season
            };
            int yr = cEl.TryGetProperty("yearIteration", out var yEl) && yEl.ValueKind == JsonValueKind.Number ? yEl.GetInt32() : 0;
            HexCalendar.SeasonDisplay = yr > 0 ? $"{seasonRu} (Цикл #{yr})" : seasonRu;
            HexCalendar.ExpiryUtc = WarframeApiService.ParseIsoOrFallback(WorldStateService.GetStr(cEl, "expiry"), TimeSpan.FromDays(3));

            if (!cEl.TryGetProperty("days", out var days) || days.ValueKind != JsonValueKind.Array) return;
            foreach (var day in days.EnumerateArray())
            {
                string dStr = WorldStateService.GetStr(day, "date");
                string dLabel = WarframeApiService.TryParseIso(dStr, out var dt) ? dt.ToString("dd.MM") : "";
                if (!day.TryGetProperty("events", out var evArr) || evArr.ValueKind != JsonValueKind.Array) continue;

                foreach (var ev in evArr.EnumerateArray())
                {
                    string kind = WorldStateService.GetStr(ev, "type");
                    if (kind.Contains("Big Prize", StringComparison.OrdinalIgnoreCase))
                    {
                        string uid = WorldStateService.GetStr(ev, "uniqueName");
                        string rew = WorldStateService.ResolveLocalizedCatalogName(uid, WorldStateService.GetStr(ev, "reward"), catalog);
                        HexCalendar.Prizes.Add(new CalendarDayEntry { DateLabel = dLabel, EventKind = "ПРИЗ", Title = rew, BadgeColor = "#F59E0B" });
                    }
                    else if (kind == "To Do" && ev.TryGetProperty("challenge", out var ch))
                    {
                        HexCalendar.Tasks.Add(new CalendarDayEntry
                        {
                            DateLabel = dLabel,
                            EventKind = "ЗАДАНИЕ",
                            Title = WorldStateService.Instance.TranslateEnToRu(WorldStateService.GetStr(ch, "title")),
                            Detail = WorldStateService.Instance.TranslateEnToRu(WorldStateService.GetStr(ch, "description")),
                            BadgeColor = "#06B6D4"
                        });
                    }
                    else if (kind == "Override" && ev.TryGetProperty("upgrade", out var upg))
                    {
                        HexCalendar.Overrides.Add(new CalendarDayEntry
                        {
                            DateLabel = dLabel,
                            EventKind = "БАФФ",
                            Title = WorldStateService.Instance.TranslateEnToRu(WorldStateService.GetStr(upg, "title")),
                            Detail = WorldStateService.Instance.TranslateEnToRu(WorldStateService.GetStr(upg, "description")),
                            BadgeColor = "#10B981"
                        });
                    }
                }
            }
            HexCalendar.RefreshAll();
        }

        private void ParseSteelPath(JsonElement root)
        {
            if (!root.TryGetProperty("steelPath", out var spEl) || spEl.ValueKind != JsonValueKind.Object) return;
            string curName = "";
            int curCost = 0;
            if (spEl.TryGetProperty("currentReward", out var cr))
            {
                curName = WorldStateService.TranslateSteelPathReward(WorldStateService.GetStr(cr, "name"));
                curCost = cr.TryGetProperty("cost", out var cEl) ? cEl.GetInt32() : 0;
            }
            SteelPath.CurrentRewardName = curName;
            SteelPath.CurrentRewardCost = curCost;
            SteelPath.ExpiryUtc = WarframeApiService.ParseIsoOrFallback(WorldStateService.GetStr(spEl, "expiry"), TimeSpan.FromDays(3));

            SteelPath.Rotation.Clear();
            if (spEl.TryGetProperty("rotation", out var rotArr) && rotArr.ValueKind == JsonValueKind.Array)
            {
                int idx = 1;
                foreach (var r in rotArr.EnumerateArray())
                {
                    string raw = WorldStateService.GetStr(r, "name");
                    string nameRu = WorldStateService.TranslateSteelPathReward(raw);
                    int cost = r.TryGetProperty("cost", out var cEl) ? cEl.GetInt32() : 0;
                    SteelPath.Rotation.Add(new SteelPathRotationEntry
                    {
                        Index = idx++,
                        Name = nameRu,
                        Cost = cost,
                        IsCurrent = nameRu.Equals(curName, StringComparison.OrdinalIgnoreCase),
                        IsUmbraOrKey = raw.Contains("Umbra", StringComparison.OrdinalIgnoreCase) || raw.Contains("Kuva", StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            SteelPath.RefreshAll();
        }

        private void ParseFissures(JsonElement root)
        {
            if (!root.TryGetProperty("fissures", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            var list = new List<VoidFissureItem>();
            DateTime utcNow = DateTime.UtcNow;

            foreach (var elem in arr.EnumerateArray())
            {
                DateTime exp = WarframeApiService.ParseIsoOrFallback(WorldStateService.GetStr(elem, "expiry"), TimeSpan.FromMinutes(30));
                if (exp <= utcNow) continue;

                string rawNode = WorldStateService.GetStr(elem, "node");
                var (node, planet) = WorldStateService.SplitAndTranslateNode(rawNode);
                string mType = WorldStateService.TranslateMission(WorldStateService.GetStr(elem, "missionType"));
                string tier = WorldStateService.GetStr(elem, "tier", "Lith");
                string enemy = WorldStateService.TranslateFaction(WorldStateService.GetStr(elem, "enemy", "Grineer"));
                bool isHard = elem.TryGetProperty("isHard", out var hp) && hp.ValueKind == JsonValueKind.True;
                bool isStorm = elem.TryGetProperty("isStorm", out var sp) && sp.ValueKind == JsonValueKind.True;

                int order = tier switch { "Lith" => 1, "Meso" => 2, "Neo" => 3, "Axi" => 4, "Requiem" => 5, "Omnia" => 6, _ => 7 };
                string color = tier switch { "Lith" => "#10B981", "Meso" => "#06B6D4", "Neo" => "#EAB308", "Axi" => "#F97316", "Requiem" => "#DC2626", "Omnia" => "#38BDF8", _ => "#EAB308" };
                string region = WorldStateService.ResolveFissureRegion(planet, node, isStorm);

                list.Add(new VoidFissureItem
                {
                    Node = node,
                    Planet = planet,
                    SystemRegion = region,
                    MissionType = mType,
                    Tier = tier,
                    TierOrder = order,
                    TierColor = color,
                    EnemyFaction = enemy,
                    ExpiryUtc = exp,
                    IsSteelPath = isHard,
                    IsVoidStorm = isStorm,
                    TierIconUrl = MainViewModel.GetTierRelicIcon(tier)
                });
            }

            AllFissures.Clear();
            AllFissures.AddRange(list);
            RebuildFissureRegionOptions();
            ApplyFissuresFilter();
        }

        private void RebuildFissureRegionOptions()
        {
            var preferredOrder = new[]
            {
                "Звёздная карта", "Рэйлджек (Проксима)", "Зариман", "Санктум (Деймос)",
                "1999 (Хольвания)", "Луа", "Крепость Кувы", "Бездна"
            };
            var present = AllFissures
                .Select(f => f.SystemRegion)
                .Where(r => !string.IsNullOrEmpty(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(r =>
                {
                    int idx = Array.FindIndex(preferredOrder, p => p.Equals(r, StringComparison.OrdinalIgnoreCase));
                    return idx >= 0 ? idx : 99;
                })
                .ThenBy(r => r)
                .ToList();

            AvailableFissureRegions.Clear();
            foreach (string r in present)
            {
                AvailableFissureRegions.Add(new FissureRegionFilterOption
                {
                    Name = r,
                    IsSelected = string.Equals(_fissureRegionFilter, r, StringComparison.OrdinalIgnoreCase)
                });
            }
        }

        private void SyncFissureRegionSelectionFlags()
        {
            foreach (var opt in AvailableFissureRegions)
                opt.IsSelected = string.Equals(_fissureRegionFilter, opt.Name, StringComparison.OrdinalIgnoreCase);
        }

        private void ParseAlerts(JsonElement root, DateTime utcNow, IReadOnlyList<CollectionItemModel> catalog)
        {
            if (!root.TryGetProperty("alerts", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            ActiveAlerts.Clear();
            foreach (var elem in arr.EnumerateArray())
            {
                if (!elem.TryGetProperty("mission", out var m)) continue;
                DateTime exp = WarframeApiService.ParseIsoOrFallback(WorldStateService.GetStr(elem, "expiry"), TimeSpan.FromHours(2));
                if (exp <= utcNow) continue;

                var (node, planet) = WorldStateService.SplitAndTranslateNode(WorldStateService.GetStr(m, "node"));
                string rewardStr = "Кредиты + Награда";
                if (m.TryGetProperty("reward", out var rew))
                {
                    if (rew.TryGetProperty("items", out var ia) && ia.ValueKind == JsonValueKind.Array && ia.GetArrayLength() > 0)
                    {
                        string rawItem = ia[0].GetString() ?? rewardStr;
                        rewardStr = WorldStateService.ResolveLocalizedCatalogName(null, rawItem, catalog);
                    }
                    else if (rew.TryGetProperty("countedItems", out var ci) && ci.ValueKind == JsonValueKind.Array && ci.GetArrayLength() > 0)
                    {
                        var first = ci[0];
                        string rName = WorldStateService.ResolveLocalizedCatalogName(
                            WorldStateService.GetStr(first, "uniqueName"),
                            WorldStateService.GetStr(first, "type"),
                            catalog);
                        int cnt = first.TryGetProperty("count", out var cEl) ? cEl.GetInt32() : 1;
                        rewardStr = cnt > 1 ? $"{cnt:N0}× {rName}" : rName;
                    }
                    else if (rew.TryGetProperty("credits", out var cr) && cr.GetInt32() > 0)
                    {
                        rewardStr = $"{cr.GetInt32():N0} Кредитов";
                    }
                }

                ActiveAlerts.Add(new AlertItem
                {
                    Title = WorldStateService.Instance.TranslateEnToRu(WorldStateService.GetStr(m, "description", "Тревога")),
                    Node = node,
                    Planet = planet,
                    MissionType = WorldStateService.TranslateMission(WorldStateService.GetStr(m, "type")),
                    Faction = WorldStateService.TranslateFaction(WorldStateService.GetStr(m, "faction")),
                    Reward = rewardStr,
                    ExpiryUtc = exp
                });
            }
        }

        private void ParseInvasions(JsonElement root, IReadOnlyList<CollectionItemModel> catalog)
        {
            if (!root.TryGetProperty("invasions", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            ActiveInvasions.Clear();
            foreach (var elem in arr.EnumerateArray())
            {
                if (elem.TryGetProperty("completed", out var c) && c.ValueKind == JsonValueKind.True) continue;
                var (node, planet) = WorldStateService.SplitAndTranslateNode(WorldStateService.GetStr(elem, "node"));
                double pct = elem.TryGetProperty("completion", out var cp) ? Math.Clamp(cp.GetDouble(), 0, 100) : 50;

                var (attFac, attRew, attTh) = ExtractInvasionSide(elem, "attacker", "Grineer", catalog);
                var (defFac, defRew, defTh) = ExtractInvasionSide(elem, "defender", "Corpus", catalog);

                ActiveInvasions.Add(new InvasionItem
                {
                    Node = node,
                    Planet = planet,
                    AttackerFaction = WorldStateService.TranslateFaction(attFac),
                    DefenderFaction = WorldStateService.TranslateFaction(defFac),
                    AttackerReward = attRew,
                    DefenderReward = defRew,
                    AttackerRewardThumb = MainViewModel.GetRewardImageUrl(attRew, attTh),
                    DefenderRewardThumb = MainViewModel.GetRewardImageUrl(defRew, defTh),
                    AttackerRewardIcon = GetRewardIcon(attRew),
                    DefenderRewardIcon = GetRewardIcon(defRew),
                    AttackerProgressPercent = Math.Round(pct, 1),
                    ExpiryUtc = DateTime.UtcNow.AddHours(6)
                });
            }
        }

        private static (string Faction, string Reward, string? Thumb) ExtractInvasionSide(
            JsonElement elem,
            string sideKey,
            string defaultFac,
            IReadOnlyList<CollectionItemModel> catalog)
        {
            string fac = defaultFac;
            string rewStr = string.Empty;
            string? thumb = null;
            if (elem.TryGetProperty(sideKey, out var s) && s.ValueKind == JsonValueKind.Object)
            {
                fac = WorldStateService.GetStr(s, "faction", defaultFac);
                if (s.TryGetProperty("reward", out var r) && r.ValueKind == JsonValueKind.Object)
                {
                    thumb = WorldStateService.GetStr(r, "thumbnail");
                    if (r.TryGetProperty("countedItems", out var ci) && ci.ValueKind == JsonValueKind.Array && ci.GetArrayLength() > 0)
                    {
                        var i0 = ci[0];
                        string uid = WorldStateService.GetStr(i0, "uniqueName");
                        string t = WorldStateService.GetStr(i0, "type");
                        string localized = WorldStateService.ResolveLocalizedCatalogName(uid, t, catalog);
                        int count = i0.TryGetProperty("count", out var cnt) && cnt.ValueKind == JsonValueKind.Number ? cnt.GetInt32() : 1;
                        rewStr = count > 1 ? $"{count:N0}× {localized}" : localized;
                        if (string.IsNullOrEmpty(thumb)) thumb = WorldStateService.GetStr(i0, "thumbnail");
                    }
                    else if (r.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Array && it.GetArrayLength() > 0)
                    {
                        string raw = it[0].GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(raw))
                            rewStr = WorldStateService.ResolveLocalizedCatalogName(null, raw, catalog);
                    }
                    else if (r.TryGetProperty("credits", out var cr) && cr.ValueKind == JsonValueKind.Number && cr.GetInt32() > 0)
                    {
                        rewStr = $"{cr.GetInt32():N0} Кредитов";
                    }
                }
            }
            if (string.IsNullOrEmpty(rewStr))
                rewStr = fac.Contains("Infested", StringComparison.OrdinalIgnoreCase) ? "Без награды (Заражение)" : "Без награды";
            return (fac, rewStr, string.IsNullOrEmpty(thumb) ? null : thumb);
        }

        private void ParseOperations(JsonElement root)
        {
            Operations.Clear();
            if (root.TryGetProperty("events", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var ev in arr.EnumerateArray())
                {
                    string desc = WorldStateService.GetStr(ev, "description");
                    if (string.IsNullOrWhiteSpace(desc)) continue;
                    string tooltip = WorldStateService.GetStr(ev, "tooltip");
                    double health = ev.TryGetProperty("health", out var hEl) && hEl.ValueKind == JsonValueKind.Number ? hEl.GetDouble() : 0;
                    DateTime exp = WarframeApiService.ParseIsoOrFallback(WorldStateService.GetStr(ev, "expiry"), TimeSpan.FromDays(7));
                    Operations.Add(new OperationEvent
                    {
                        Name = WorldStateService.Instance.TranslateEnToRu(desc),
                        Description = string.IsNullOrEmpty(tooltip) ? "Глобальное событие / операция DE" : WorldStateService.Instance.TranslateEnToRu(tooltip),
                        ProgressPercent = Math.Round(health, 1),
                        ExpiryUtc = exp,
                        IsGlobalOperation = true,
                        HasCommunityProgress = health > 0
                    });
                }
            }
            OnPropertyChanged(nameof(HasActiveOperations));
        }

        private void ParseThreatProgress(JsonElement root)
        {
            if (!root.TryGetProperty("constructionProgress", out var cp) || cp.ValueKind != JsonValueKind.Object) return;
            double fom = double.TryParse(WorldStateService.GetStr(cp, "fomorianProgress", "0"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double fVal) ? fVal : 0;
            double raz = double.TryParse(WorldStateService.GetStr(cp, "razorbackProgress", "0"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double rVal) ? rVal : 0;
            Threats = new ThreatProgressModel { FomorianPercent = fom, RazorbackPercent = raz };
            OnPropertyChanged(nameof(Threats));
        }

        private void ParseVarzia(JsonElement root, IReadOnlyList<CollectionItemModel> catalog)
        {
            if (!root.TryGetProperty("vaultTrader", out var vt) || vt.ValueKind != JsonValueKind.Object) return;
            Varzia.ExpiryUtc = WarframeApiService.ParseIsoOrFallback(WorldStateService.GetStr(vt, "expiry"), TimeSpan.FromDays(20));

            var items = new List<VarziaItem>();
            var detectedPrimeFrames = new List<string>();
            if (vt.TryGetProperty("inventory", out var invArr) && invArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in invArr.EnumerateArray())
                {
                    string uid = WorldStateService.GetStr(it, "uniqueName");
                    string rawName = WorldStateService.GetStr(it, "item");
                    int cost = it.TryGetProperty("ducats", out var dEl) && dEl.ValueKind == JsonValueKind.Number ? dEl.GetInt32() : 0;
                    bool isRelic = rawName.Contains("Projection", StringComparison.OrdinalIgnoreCase) || uid.Contains("Projection", StringComparison.OrdinalIgnoreCase);

                    string normUid = uid.Replace("/Lotus/Types/StoreItems/", "/Lotus/", StringComparison.Ordinal)
                                        .Replace("/Lotus/StoreItems/", "/Lotus/", StringComparison.Ordinal);
                    var catMatch = catalog.FirstOrDefault(c => c.UniqueName.Equals(normUid, StringComparison.OrdinalIgnoreCase));
                    string tailKey = uid.Split('/').LastOrDefault() ?? string.Empty;
                    (string Name, string Cat, string Icon) bundle = (string.Empty, string.Empty, string.Empty);
                    bool hasBundle = !string.IsNullOrEmpty(tailKey) && _varziaBundles.TryGetValue(tailKey, out bundle);

                    string friendlyName = hasBundle
                        ? bundle.Name
                        : (catMatch?.Name ?? CleanVarziaName(WorldStateService.Instance.TranslateEnToRu(rawName)));
                    if (catMatch != null && catMatch.Category == "Варфреймы" && catMatch.IsPrime)
                        detectedPrimeFrames.Add(catMatch.Name);

                    string? thumb = catMatch != null && !string.IsNullOrEmpty(catMatch.IconPath)
                        ? ImageCacheService.NormalizeImageUrl(catMatch.IconPath)
                        : (!string.IsNullOrEmpty(catMatch?.ThumbnailUrl)
                            ? catMatch.ThumbnailUrl
                            : (hasBundle && !string.IsNullOrEmpty(bundle.Icon)
                                ? ImageCacheService.NormalizeImageUrl(bundle.Icon)
                                : (isRelic ? MainViewModel.GetTierRelicIcon("Lith") : null)));

                    string cat = isRelic
                        ? "Реликвия Бездны"
                        : (catMatch?.Category ?? (hasBundle ? bundle.Cat : (rawName.Contains("Pack", StringComparison.OrdinalIgnoreCase) ? "Набор" : "Косметика")));
                    items.Add(new VarziaItem
                    {
                        Name = friendlyName,
                        Category = cat,
                        Description = catMatch?.SourceInfo ?? "Товар Возрождения Прайм у Варзии",
                        IconGlyph = isRelic ? "🏺" : (catMatch?.IconGlyph ?? "👑"),
                        ThumbnailUrl = thumb,
                        CurrencyIconUrl = isRelic ? _ayaIconUrl : _regalAyaIconUrl,
                        AyaCost = isRelic ? 1 : 0,
                        RegalAyaCost = isRelic ? 0 : cost,
                        IsRelic = isRelic
                    });
                }
            }

            // Append permanent Regular Aya offerings from seed so "Обычная Айя" tab shows full Varzia store
            foreach (var perm in _varziaPermanentAya)
            {
                bool isGenericRelicPlaceholder = perm.IsRelic && items.Any(x => x.IsRelic);
                if (!isGenericRelicPlaceholder && !items.Any(x => x.Name.Equals(perm.Name, StringComparison.OrdinalIgnoreCase)))
                    items.Add(perm);
            }

            if (detectedPrimeFrames.Count >= 2)
                Varzia.CurrentRotationName = string.Join(" & ", detectedPrimeFrames.Distinct().Take(2));
            else if (items.Count > 0)
                Varzia.CurrentRotationName = "Актуальная ротация Возрождения Прайм";

            if (items.Count > 0)
            {
                Varzia.FeaturedItems.Clear();
                Varzia.FeaturedItems.AddRange(items.OrderByDescending(i => !i.IsRelic).ThenByDescending(i => i.RegalAyaCost).ThenBy(i => i.AyaCost));
                ApplyVarziaFilter();
            }
            Varzia.RefreshAll();
        }

        private static string CleanVarziaName(string raw) =>
            raw.Replace("M P V ", "Набор: ").Replace("Void Projection", "Реликвия:");

        private void ParseBaro(JsonElement root, IReadOnlyList<CollectionItemModel> catalog)
        {
            if (!root.TryGetProperty("voidTrader", out var bEl) || bEl.ValueKind != JsonValueKind.Object) return;
            Baro.CharacterName = WorldStateService.GetStr(bEl, "character", "Баро Ки'Тиир");
            var (loc, planet) = WorldStateService.SplitAndTranslateNode(WorldStateService.GetStr(bEl, "location", "Реле"));
            Baro.Location = string.IsNullOrEmpty(planet) ? loc : $"{loc} ({planet})";
            Baro.ActivationUtc = WarframeApiService.ParseIsoOrFallback(WorldStateService.GetStr(bEl, "activation"), TimeSpan.FromDays(5));
            Baro.ExpiryUtc = WarframeApiService.ParseIsoOrFallback(WorldStateService.GetStr(bEl, "expiry"), TimeSpan.FromDays(7));
            Baro.IsActive = DateTime.UtcNow >= Baro.ActivationUtc && DateTime.UtcNow < Baro.ExpiryUtc;

            AllBaroItems.Clear();
            if (bEl.TryGetProperty("inventory", out var inv) && inv.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in inv.EnumerateArray())
                {
                    string uid = WorldStateService.GetStr(it, "uniqueName");
                    string raw = WorldStateService.GetStr(it, "item");
                    int ducats = it.TryGetProperty("ducats", out var dEl) && dEl.ValueKind == JsonValueKind.Number ? dEl.GetInt32() : 0;
                    int credits = it.TryGetProperty("credits", out var cEl) && cEl.ValueKind == JsonValueKind.Number ? cEl.GetInt32() : 0;
                    string normUid = uid.Replace("/Lotus/Types/StoreItems/", "/Lotus/", StringComparison.Ordinal)
                                        .Replace("/Lotus/StoreItems/", "/Lotus/", StringComparison.Ordinal);
                    var catMatch = catalog.FirstOrDefault(c =>
                        (!string.IsNullOrEmpty(normUid) && c.UniqueName.Equals(normUid, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(raw) && c.NameEn.Equals(raw, StringComparison.OrdinalIgnoreCase)));
                    string? thumb = catMatch != null && !string.IsNullOrEmpty(catMatch.IconPath)
                        ? ImageCacheService.NormalizeImageUrl(catMatch.IconPath)
                        : catMatch?.ThumbnailUrl;
                    AllBaroItems.Add(new TraderInventoryItem
                    {
                        UniqueName = uid,
                        Name = catMatch?.Name ?? WorldStateService.ResolveLocalizedCatalogName(uid, raw, catalog),
                        Category = catMatch?.Category ?? string.Empty,
                        ThumbnailUrl = thumb,
                        Ducats = ducats,
                        Credits = credits
                    });
                }
            }
            ApplyBaroFilter();
            Baro.Inventory.Clear();
            foreach (var it in AllBaroItems) Baro.Inventory.Add(it);
            Baro.RefreshAll();
        }

        private void ParseNightwave(JsonElement root, DateTime utcNow)
        {
            if (!root.TryGetProperty("nightwave", out var nw) || nw.ValueKind != JsonValueKind.Object) return;
            Nightwave.ExpiryUtc = WarframeApiService.ParseIsoOrFallback(WorldStateService.GetStr(nw, "expiry"), TimeSpan.FromDays(30));
            string tag = WorldStateService.GetStr(nw, "tag");
            int phase = nw.TryGetProperty("phase", out var phEl) && phEl.ValueKind == JsonValueKind.Number ? phEl.GetInt32() : 0;
            if (!string.IsNullOrEmpty(tag))
                Nightwave.SeasonTitle = FormatNightwaveSeasonTitle(tag, phase);

            var chList = new List<NightwaveChallenge>();
            if (nw.TryGetProperty("activeChallenges", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var ch in arr.EnumerateArray())
                {
                    DateTime exp = WarframeApiService.ParseIsoOrFallback(WorldStateService.GetStr(ch, "expiry"), TimeSpan.FromDays(3));
                    if (exp <= utcNow) continue;

                    string title = WorldStateService.GetStr(ch, "title");
                    string desc = WorldStateService.GetStr(ch, "desc");
                    var (tRu, dRu) = TranslateNightwaveChallenge(title, desc);
                    chList.Add(new NightwaveChallenge
                    {
                        Title = tRu,
                        Description = dRu,
                        Reputation = ch.TryGetProperty("reputation", out var rep) ? rep.GetInt32() : 1000,
                        IsDaily = ch.TryGetProperty("isDaily", out var d) && d.ValueKind == JsonValueKind.True,
                        IsElite = ch.TryGetProperty("isElite", out var e) && e.ValueKind == JsonValueKind.True,
                        ExpiryUtc = exp
                    });
                }
            }

            if (chList.Count > 0)
            {
                Nightwave.Challenges.Clear();
                // Ascending by Reputation (1000 -> 4500 -> 7000), matching in-game order
                foreach (var c in chList.OrderBy(x => x.Reputation).ThenBy(x => x.ExpiryUtc))
                    Nightwave.Challenges.Add(c);
            }
            Nightwave.RefreshAll();
        }

        private void ParseMiscIndicators(JsonElement root)
        {
            if (root.TryGetProperty("simaris", out var sim) && sim.ValueKind == JsonValueKind.Object)
            {
                string tgt = WorldStateService.GetStr(sim, "target");
                if (!string.IsNullOrEmpty(tgt))
                {
                    _simarisTargetRaw = tgt;
                    SimarisTarget = WorldStateService.Instance.TranslateEnToRu(tgt);
                }
            }
            if (root.TryGetProperty("sentientOutposts", out var so) && so.ValueKind == JsonValueKind.Object)
            {
                bool act = so.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True;
                if (act && so.TryGetProperty("mission", out var m))
                {
                    var (n, p) = WorldStateService.SplitAndTranslateNode(WorldStateService.GetStr(m, "node"));
                    SentientOutpostText = string.IsNullOrEmpty(n) ? "Активно (Вуаль)" : $"{n} ({p})";
                }
                else
                {
                    SentientOutpostText = "Нет активной аномалии";
                }
            }
        }

        public void Tick(DateTime utcNow, bool suppressNotifications = false)
        {
            bool rolledOver = false;

            foreach (var cycle in WorldCycles)
            {
                if (cycle.ExpiryUtc <= utcNow)
                {
                    int guard = 0;
                    while (cycle.ExpiryUtc <= utcNow && ++guard < 500)
                        RollOverWorldCycle(cycle, utcNow);
                    if (cycle.ExpiryUtc <= utcNow)
                        cycle.ExpiryUtc = utcNow.AddMinutes(30);
                    rolledOver = true;
                    if (!suppressNotifications && cycle.NotifyEnabled)
                    {
                        _statusCallback($"🔔 Смена цикла: {cycle.LocationName} -> {cycle.State}!");
                        NotificationService.Instance.Notify(
                            $"Смена цикла: {cycle.LocationName}",
                            $"Активная фаза: {cycle.State}");
                    }
                }
                cycle.TimeRemaining = FormatTimeSpan(cycle.ExpiryUtc - utcNow);
            }

            TimeSpan moodDiff = DuviriMood.ExpiryUtc - utcNow;
            if (moodDiff <= TimeSpan.Zero)
            {
                int guard = 0;
                while (DuviriMood.ExpiryUtc <= utcNow && ++guard < 200)
                    RollOverDuviriMood(utcNow);
                if (DuviriMood.ExpiryUtc <= utcNow)
                    DuviriMood.ExpiryUtc = utcNow.AddHours(2);
                moodDiff = DuviriMood.ExpiryUtc - utcNow;
                rolledOver = true;
                if (!suppressNotifications && DuviriMood.IsTrackedActive)
                {
                    _statusCallback($"🔔 Настроение Дувири сменилось: {DuviriMood.CurrentMood}!");
                    NotificationService.Instance.Notify(
                        "Спираль Дувири",
                        $"Отслеживаемое настроение активно: {DuviriMood.CurrentMood}");
                }
            }
            DuviriMood.TimeRemaining = FormatTimeSpan(moodDiff);

            // Daily Reset (UTC 00:00) & Weekly Reset (Monday UTC 00:00)
            DateTime nextDailyReset = utcNow.Date.AddDays(1);
            DailyResetRemaining = FormatTimeSpan(nextDailyReset - utcNow);

            int daysToMon = (7 - (((int)utcNow.DayOfWeek + 6) % 7)) % 7;
            if (daysToMon == 0) daysToMon = 7;
            DateTime nextWeeklyReset = utcNow.Date.AddDays(daysToMon);
            WeeklyResetRemaining = FormatTimeSpan(nextWeeklyReset - utcNow);

            if (GetWeekStartKey(utcNow) != _currentWeekKey)
                BuildWeeklyChecklist(utcNow);

            bool isFetching;
            DateTime lastAutoSync;
            lock (_fetchLock)
            {
                isFetching = _isFetching;
                lastAutoSync = _lastAutoSyncUtc;
            }

            // Daily / Weekly activity countdowns
            if (((DailySortie.Stages.Count > 0 && DailySortie.ExpiryUtc <= utcNow) ||
                 (ArchonHunt.Stages.Count > 0 && ArchonHunt.ExpiryUtc <= utcNow)) &&
                !isFetching && (utcNow - lastAutoSync).TotalSeconds > 30)
            {
                rolledOver = true;
            }
            DailySortie.TimeRemaining = FormatTimeSpan(DailySortie.ExpiryUtc - utcNow);
            if (Arbitration.ExpiryUtc <= utcNow && !isFetching && (utcNow - lastAutoSync).TotalSeconds > 30)
                rolledOver = true;
            Arbitration.TimeRemaining = FormatTimeSpan(Arbitration.ExpiryUtc - utcNow);

            if (DarvoDeal.HasDeal) DarvoDeal.TimeRemaining = FormatTimeSpan(DarvoDeal.ExpiryUtc - utcNow);
            foreach (var gb in GlobalBoosters) gb.TimeRemaining = FormatTimeSpan(gb.ExpiryUtc - utcNow);

            ArchonHunt.TimeRemaining = FormatTimeSpan(ArchonHunt.ExpiryUtc - utcNow);
            foreach (var a in Archimedeas) a.TimeRemaining = FormatTimeSpan(a.ExpiryUtc - utcNow);
            Descendia.TimeRemaining = FormatTimeSpan(Descendia.ExpiryUtc - utcNow);
            HexCalendar.TimeRemaining = FormatTimeSpan(HexCalendar.ExpiryUtc - utcNow);
            SteelPath.TimeRemaining = FormatTimeSpan(SteelPath.ExpiryUtc - utcNow);
            OnPropertyChanged(nameof(ActiveEndgameTimeRemaining));

            bool wasBaroActive = Baro.IsActive;
            bool nowBaroActive = utcNow >= Baro.ActivationUtc && utcNow < Baro.ExpiryUtc;
            if (wasBaroActive != nowBaroActive)
            {
                Baro.IsActive = nowBaroActive;
                Baro.RefreshAll();
                OnPropertyChanged(nameof(HudTradersCount));
                if (nowBaroActive && !suppressNotifications && Baro.NotifyEnabled)
                {
                    _statusCallback($"🔔 Баро Ки'Тиир прибыл на реле {Baro.Location}!");
                    NotificationService.Instance.Notify(
                        "Баро Ки'Тиир прибыл",
                        $"Торговец Бездны открылся на реле: {Baro.Location}");
                }
            }
            Baro.TimeRemaining = FormatTimeSpan(Baro.ActiveTargetUtc - utcNow);
            Varzia.TimeRemaining = FormatTimeSpan(Varzia.ExpiryUtc - utcNow);

            bool fissuresChanged = false;
            for (int i = AllFissures.Count - 1; i >= 0; i--)
            {
                if (AllFissures[i].ExpiryUtc <= utcNow) { AllFissures.RemoveAt(i); fissuresChanged = true; }
            }
            if (fissuresChanged)
            {
                RebuildFissureRegionOptions();
                ApplyFissuresFilter();
            }
            else
            {
                foreach (var f in DisplayFissures) f.TimeRemaining = FormatTimeSpan(f.ExpiryUtc - utcNow);
            }

            bool alertsRemoved = false;
            for (int i = ActiveAlerts.Count - 1; i >= 0; i--)
            {
                if (ActiveAlerts[i].ExpiryUtc <= utcNow) { ActiveAlerts.RemoveAt(i); alertsRemoved = true; }
                else ActiveAlerts[i].TimeRemaining = FormatTimeSpan(ActiveAlerts[i].ExpiryUtc - utcNow);
            }
            if (alertsRemoved)
            {
                OnPropertyChanged(nameof(HudAlertsCount));
                OnPropertyChanged(nameof(HasActiveAlertItems));
            }
            foreach (var inv in ActiveInvasions) inv.TimeRemaining = FormatTimeSpan(inv.ExpiryUtc - utcNow);
            foreach (var op in Operations) op.TimeRemaining = FormatTimeSpan(op.ExpiryUtc - utcNow);

            Nightwave.TimeRemaining = FormatTimeSpan(Nightwave.ExpiryUtc - utcNow);
            for (int i = Nightwave.Challenges.Count - 1; i >= 0; i--)
            {
                if (Nightwave.Challenges[i].ExpiryUtc <= utcNow) Nightwave.Challenges.RemoveAt(i);
                else Nightwave.Challenges[i].TimeRemaining = FormatTimeSpan(Nightwave.Challenges[i].ExpiryUtc - utcNow);
            }

            if (rolledOver && !isFetching && (utcNow - lastAutoSync).TotalSeconds > 20)
            {
                _ = Task.Run(() => FetchLiveWorldstateAsync(false));
            }
        }

        private void CheckDuviriMoodTracking()
        {
            var opt = DuviriMoodOptions.FirstOrDefault(o => o.Key.Equals(DuviriMood.RawStateKey, StringComparison.OrdinalIgnoreCase));
            DuviriMood.IsTrackedActive = opt != null && opt.IsTracked;
        }

        private void OnDuviriMoodOptionChanged()
        {
            CheckDuviriMoodTracking();
            StorageService.Instance.SaveTrackedMoods(DuviriMoodOptions.Where(o => o.IsTracked).Select(o => o.Key));
            _statusCallback("🔔 Настройки отслеживания настроений Дувири сохранены");
        }

        public void ApplyIncarnonFilter()
        {
            FilteredDuviriRotations.Clear();
            string q = IncarnonSearchText.Trim();
            // Keep chronological week order 1..9 so Past -> Current -> Next weeks stay adjacent
            foreach (var w in AllDuviriRotations.OrderBy(r => r.WeekNumber))
            {
                if (string.IsNullOrEmpty(q) ||
                    w.SteelPathIncarnons.Any(i => i.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                    w.NormalWarframes.Any(f => f.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                    w.WeekNumber.ToString() == q)
                {
                    FilteredDuviriRotations.Add(w);
                }
            }
        }

        public void ApplyFissuresFilter()
        {
            DisplayFissures.Clear();
            var sorted = AllFissures.Where(f =>
            {
                if (SelectedFissureTier != "All" && !string.Equals(f.Tier, SelectedFissureTier, StringComparison.OrdinalIgnoreCase)) return false;
                if (FissureCategoryFilter == "Origin" && (f.IsSteelPath || f.IsVoidStorm)) return false;
                if (FissureCategoryFilter == "SteelPath" && !f.IsSteelPath) return false;
                if (FissureCategoryFilter == "Proxima" && !f.IsVoidStorm) return false;
                if (FissureSteelPathMode == "Normal" && f.IsSteelPath) return false;
                if (FissureSteelPathMode == "SteelPath" && !f.IsSteelPath) return false;
                if (FissureMissionTypeMode == "Standard" && f.IsVoidStorm) return false;
                if (FissureMissionTypeMode == "Railjack" && !f.IsVoidStorm) return false;
                if (FissureRegionFilter != "All" && !string.Equals(f.SystemRegion, FissureRegionFilter, StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }).OrderBy(f => f.TierOrder).ThenBy(f => f.ExpiryUtc);

            foreach (var item in sorted) DisplayFissures.Add(item);
            NotifyHudCountsChanged();
        }

        public void ApplyVarziaFilter()
        {
            Varzia.DisplayItems.Clear();
            foreach (var item in Varzia.FeaturedItems)
            {
                if (VarziaFilterMode == "Regal" && !item.HasRegalAyaCost) continue;
                if (VarziaFilterMode == "Regular" && !item.HasAyaCost) continue;
                if (VarziaFilterMode == "Relics" && !item.IsRelic) continue;
                Varzia.DisplayItems.Add(item);
            }
        }

        public void ApplyBaroFilter()
        {
            DisplayBaroItems.Clear();
            string q = BaroSearchText.Trim();
            foreach (var item in AllBaroItems)
            {
                if (string.IsNullOrEmpty(q) || item.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                    DisplayBaroItems.Add(item);
            }
        }

        public void ApplyNightwaveOfferingsFilter()
        {
            Nightwave.DisplayOfferings.Clear();
            string q = NightwaveShopSearchText.Trim();
            foreach (var o in Nightwave.AllOfferings)
            {
                bool catMatch = NightwaveOfferingFilter == "All" ||
                    (NightwaveOfferingFilter == "Aura" && (o.Category == "Ауры и Моды" || o.Category == "Ауры")) ||
                    (NightwaveOfferingFilter == "Weapon" && o.Category == "Оружие") ||
                    (NightwaveOfferingFilter == "Resource" && (o.Category == "Ресурсы и Кува" || o.Category == "Ресурсы")) ||
                    (NightwaveOfferingFilter == "Cosmetic" && o.Category == "Косметика");

                if (!catMatch) continue;
                if (!string.IsNullOrEmpty(q) && !o.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                Nightwave.DisplayOfferings.Add(o);
            }
        }

        private static void RollOverWorldCycle(WorldCycleInfo cycle, DateTime now)
        {
            DateTime anchor = cycle.ExpiryUtc > DateTime.MinValue.AddDays(1) ? cycle.ExpiryUtc : now;
            switch (cycle.Id)
            {
                case "cetus": cycle.IsDayOrWarm = !cycle.IsDayOrWarm; cycle.State = cycle.IsDayOrWarm ? "День" : "Ночь"; cycle.ExpiryUtc = anchor.AddMinutes(cycle.IsDayOrWarm ? 100 : 50); cycle.IconText = cycle.IsDayOrWarm ? "☀" : "🌙"; break;
                case "earth": cycle.IsDayOrWarm = !cycle.IsDayOrWarm; cycle.State = cycle.IsDayOrWarm ? "День" : "Ночь"; cycle.ExpiryUtc = anchor.AddHours(4); cycle.IconText = cycle.IsDayOrWarm ? "🌿" : "🌑"; break;
                case "vallis": cycle.IsDayOrWarm = !cycle.IsDayOrWarm; cycle.State = cycle.IsDayOrWarm ? "Тепло" : "Холод"; cycle.ExpiryUtc = cycle.IsDayOrWarm ? anchor.AddMinutes(6).AddSeconds(40) : anchor.AddMinutes(20); cycle.IconText = cycle.IsDayOrWarm ? "🔥" : "❄"; break;
                case "cambion": cycle.IsDayOrWarm = !cycle.IsDayOrWarm; cycle.State = cycle.IsDayOrWarm ? "Фасс (Fass)" : "Воум (Vome)"; cycle.ExpiryUtc = cycle.IsDayOrWarm ? anchor.AddMinutes(100) : anchor.AddMinutes(50); cycle.IconText = cycle.IsDayOrWarm ? "👁" : "✨"; break;
                case "zariman": cycle.IsDayOrWarm = !cycle.IsDayOrWarm; cycle.State = cycle.IsDayOrWarm ? "Корпус" : "Гринир"; cycle.ExpiryUtc = anchor.AddHours(2.5); cycle.IconText = cycle.IsDayOrWarm ? "⚡" : "🔨"; break;
            }
        }

        private void RollOverDuviriMood(DateTime now)
        {
            DateTime anchor = DuviriMood.ExpiryUtc > DateTime.MinValue.AddDays(1) ? DuviriMood.ExpiryUtc : now;
            string[] order = { "sorrow", "anger", "joy", "envy", "fear" };
            int nextIdx = (Array.IndexOf(order, DuviriMood.RawStateKey.ToLowerInvariant()) + 1) % order.Length;
            string next = order[nextIdx < 0 ? 0 : nextIdx];
            DuviriMood.RawStateKey = next;
            DuviriMood.ExpiryUtc = anchor.AddHours(2);
            DuviriMood.CurrentMood = GetDuviriMoodName(next);
            DuviriMood.MoodDescription = GetDuviriMoodDescription(next);
            CheckDuviriMoodTracking();
        }

        private static string GetDuviriMoodName(string k) => k.ToLowerInvariant() switch
        {
            "sorrow" => "Печаль (Sorrow)",
            "fear" => "Страх (Fear)",
            "joy" => "Радость (Joy)",
            "anger" => "Гнев (Anger)",
            "envy" => "Зависть (Envy)",
            _ => k
        };

        private static string GetDuviriMoodDescription(string k) => k.ToLowerInvariant() switch
        {
            "sorrow" => "Снежные метели и урон Холодом по противникам.",
            "anger" => "Огненные бури и усиление врагов уроном Огня.",
            "joy" => "Золотые бури и восстановление здоровья.",
            "envy" => "Токсичные ветры и вампиризм атак.",
            "fear" => "Грозовые вспышки и электро-оглушение.",
            _ => "Текущее настроение Спирали Дувири."
        };

        private void InitializeNightwaveDefaults(DateTime now)
        {
            LoadEventsSeed();
            Nightwave.SeasonTitle = "Ночная Волна";
            Nightwave.ExpiryUtc = now.AddDays(30);
            Nightwave.CredIconUrl = _nightwaveCredIconUrl ?? MainViewModel.GetItemImageUrl("GenericCreditIcon10.png");

            if (Nightwave.RankRewards.Count == 0)
            {
                Nightwave.RankRewards.Add(new NightwaveRewardRank { Rank = 1, Count = 150, RewardName = "Доверие Норы", RewardType = "Валюта", IconGlyph = "🪙" });
                Nightwave.RankRewards.Add(new NightwaveRewardRank { Rank = 29, RewardName = "Форма Умбра", RewardType = "Элитная", IconGlyph = "👑" });
            }

            if (Nightwave.AllOfferings.Count == 0)
            {
                Nightwave.AllOfferings.Add(new NightwaveOffering { Name = "Экстракт Нитаина (5 шт.)", Category = "Ресурсы и Кува", Description = "Всегда в ротации Норы", CredCost = 15, IsAlwaysAvailable = true, IconGlyph = "🧪" });
            }
            ApplyNightwaveOfferingsFilter();
        }

        private static string FormatNightwaveSeasonTitle(string tag, int phase)
        {
            string compact = tag.Replace(" ", "", StringComparison.Ordinal);
            if (compact.Contains("RadioLegionIntermission", StringComparison.OrdinalIgnoreCase))
            {
                var digits = new string(compact.Where(char.IsDigit).ToArray());
                return string.IsNullOrEmpty(digits)
                    ? $"Антракт Ночной Волны (Фаза {phase})"
                    : $"Микс Норы #{digits} (Фаза {phase})";
            }
            return $"{tag} (Фаза {phase})";
        }

        private static (string TitleRu, string DescRu) TranslateNightwaveChallenge(string title, string desc)
        {
            if (title.Contains("Detonator", StringComparison.OrdinalIgnoreCase)) return ("Взрывотехник", "Убейте 150 врагов уроном Взрывом");
            if (title.Contains("Artist", StringComparison.OrdinalIgnoreCase)) return ("Уличный Художник", "Разместите глиф в любой миссии");
            if (title.Contains("Whole Arsenal", StringComparison.OrdinalIgnoreCase)) return ("Полный Арсенал", "Убейте врагов каждым типом оружия");
            if (title.Contains("Mission Complete", StringComparison.OrdinalIgnoreCase)) return ("Миссия Выполнена", "Завершите 15 миссий");
            if (title.Contains("Eximus Eliminator", StringComparison.OrdinalIgnoreCase)) return ("Устранитель Эксимусов", "Убейте 30 врагов Эксимус");
            if (title.Contains("Not a Warning Shot", StringComparison.OrdinalIgnoreCase)) return ("Без предупреждения", "Убейте 500 противников");
            if (title.Contains("Crystal Clear", StringComparison.OrdinalIgnoreCase)) return ("Кристальная Ясность", "Завершите 3 волны Зеркальной Обороны");
            if (title.Contains("The Many Made Whole", StringComparison.OrdinalIgnoreCase)) return ("Единое Целое", "Обменяйте 10 Осколков Разлома у Палладина");
            return (title, desc.Replace("[PH] ", ""));
        }

        public static string TranslateIncarnonToRu(string name) => MainViewModel.TranslateIncarnonToRu(name);

        private static string GetRewardIcon(string r)
        {
            if (r.Contains("Катализатор") || r.Contains("Реактор") || r.Contains("Catalyst") || r.Contains("Reactor")) return "🥔";
            if (r.Contains("Форма") || r.Contains("Forma")) return "💠";
            if (r.Contains("Эксилус") || r.Contains("Exilus")) return "⚙️";
            if (r.Contains("Полетрон") || r.Contains("Fieldron") || r.Contains("Детонит") || r.Contains("Мутаген")) return "🧪";
            return "📦";
        }

        private static string FormatTimeSpan(TimeSpan ts)
        {
            if (ts <= TimeSpan.Zero) return "00м 00с";
            if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}д {ts.Hours}ч {ts.Minutes:D2}м";
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}ч {ts.Minutes:D2}м {ts.Seconds:D2}с";
            return $"{ts.Minutes:D2}м {ts.Seconds:D2}с";
        }

        private static Task RunOnUiAsync(Action action)
        {
            var disp = Application.Current?.Dispatcher;
            if (disp == null || disp.HasShutdownStarted)
            {
                action();
                return Task.CompletedTask;
            }
            if (disp.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }
            return disp.InvokeAsync(action).Task;
        }
    }

    public sealed class FissureRegionFilterOption : ViewModelBase
    {
        private bool _isSelected;
        public string Name { get; set; } = string.Empty;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }
}
