using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using WarfAssistant.Models;
using WarfAssistant.Services;

namespace WarfAssistant.ViewModels
{
    /// <summary>
    /// High-performance modular ViewModel for Collection & Collectibles tracking:
    /// Warframes, Weapons, Modular, Archwing, Companions, Cosmetics, and Orbiter Decorations.
    /// </summary>
    public sealed class CollectionViewModel : ViewModelBase
    {
        private static readonly string[] RankTitles =
        {
            "Неинициализированный (Unranked)",
            "Инициат (Initiate)",
            "Серебряный Инициат (Silver Initiate)",
            "Золотой Инициат (Gold Initiate)",
            "Новичок (Novice)",
            "Серебряный Новичок (Silver Novice)",
            "Золотой Новичок (Gold Novice)",
            "Ученик (Disciple)",
            "Серебряный Ученик (Silver Disciple)",
            "Золотой Ученик (Gold Disciple)",
            "Искатель (Seeker)",
            "Серебряный Искатель (Silver Seeker)",
            "Золотой Искатель (Gold Seeker)",
            "Охотник (Hunter)",
            "Серебряный Охотник (Silver Hunter)",
            "Золотой Охотник (Gold Hunter)",
            "Орёл (Eagle)",
            "Серебряный Орёл (Silver Eagle)",
            "Золотой Орёл (Gold Eagle)",
            "Тигр (Tiger)",
            "Серебряный Тигр (Silver Tiger)",
            "Золотой Тигр (Gold Tiger)",
            "Дракон (Dragon)",
            "Серебряный Дракон (Silver Dragon)",
            "Золотой Дракон (Gold Dragon)",
            "Мудрец (Sage)",
            "Серебряный Мудрец (Silver Sage)",
            "Золотой Мудрец (Gold Sage)",
            "Мастер (Master)",
            "Средний Мастер (Middle Master)",
            "Истинный Мастер (True Master)",
            "Легендарный 1 (Legendary 1)",
            "Легендарный 2 (Legendary 2)",
            "Легендарный 3 (Legendary 3)",
            "Легендарный 4 (Legendary 4)",
            "Легендарный 5 (Legendary 5)",
            "Легендарный 6 (Legendary 6)"
        };

        private readonly DispatcherTimer _searchDebounceTimer;
        private readonly Action<CollectionItemModel> _toggleCraftCallback;
        private readonly Action<string> _statusCallback;
        private Func<IEnumerable<string>>? _getQueuedNames;

        public List<CollectionItemModel> AllItems { get; } = new();
        public ObservableRangeCollection<CollectionItemModel> DisplayItems { get; } = new();
        public ObservableCollection<SubTabItem> SubTabs { get; } = new();
        public MasterySummaryInfo MasterySummary { get; } = new();

        private string _searchText = string.Empty;
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                {
                    _searchDebounceTimer.Stop();
                    _searchDebounceTimer.Start();
                }
            }
        }

        private string _selectedCategory = "Варфреймы";
        public string SelectedCategory
        {
            get => _selectedCategory;
            set
            {
                if (SetProperty(ref _selectedCategory, value))
                {
                    OnPropertyChanged(nameof(IsWeaponOrFrameCategory));
                    BuildSubTabsForCategory(value);
                    ApplyFilter();
                }
            }
        }

        private string _selectedSubCategory = "Все";
        public string SelectedSubCategory
        {
            get => _selectedSubCategory;
            set
            {
                if (SetProperty(ref _selectedSubCategory, value))
                {
                    ApplyFilter();
                }
            }
        }

        private bool _isFiltersExpanded;
        public bool IsFiltersExpanded
        {
            get => _isFiltersExpanded;
            set => SetProperty(ref _isFiltersExpanded, value);
        }

        // Bidirectional & Source Filters
        private string _ownedFilter = "Все"; // "Все", "В наличии", "Не в наличии"
        public string OwnedFilter
        {
            get => _ownedFilter;
            set
            {
                if (SetProperty(ref _ownedFilter, value))
                {
                    OnPropertyChanged(nameof(ActiveFiltersCount));
                    OnPropertyChanged(nameof(FiltersButtonLabel));
                    ApplyFilter();
                }
            }
        }

        private string _masteredFilter = "Все"; // "Все", "В ранге", "Не в ранге"
        public string MasteredFilter
        {
            get => _masteredFilter;
            set
            {
                if (SetProperty(ref _masteredFilter, value))
                {
                    OnPropertyChanged(nameof(ActiveFiltersCount));
                    OnPropertyChanged(nameof(FiltersButtonLabel));
                    ApplyFilter();
                }
            }
        }

        private string _vaultedFilter = "Все"; // "Все", "В хранилище", "Доступно"
        public string VaultedFilter
        {
            get => _vaultedFilter;
            set
            {
                if (SetProperty(ref _vaultedFilter, value))
                {
                    OnPropertyChanged(nameof(ActiveFiltersCount));
                    OnPropertyChanged(nameof(FiltersButtonLabel));
                    ApplyFilter();
                }
            }
        }

        private string _variantFilter = "Все"; // "Все", "Prime", "Не-Prime", "Антагонист"
        public string VariantFilter
        {
            get => _variantFilter;
            set
            {
                if (SetProperty(ref _variantFilter, value))
                {
                    OnPropertyChanged(nameof(ActiveFiltersCount));
                    OnPropertyChanged(nameof(FiltersButtonLabel));
                    ApplyFilter();
                }
            }
        }

        private string _sourceFilter = "Все"; // "Все", "Квест/Дроп", "Игр. валюта", "Кузница", "Платина", "Корол. Айя", "За реал"
        public string SourceFilter
        {
            get => _sourceFilter;
            set
            {
                if (SetProperty(ref _sourceFilter, value))
                {
                    OnPropertyChanged(nameof(ActiveFiltersCount));
                    OnPropertyChanged(nameof(FiltersButtonLabel));
                    ApplyFilter();
                }
            }
        }

        private bool _hideFoundersExclusive;
        public bool HideFoundersExclusive
        {
            get => _hideFoundersExclusive;
            set
            {
                if (SetProperty(ref _hideFoundersExclusive, value))
                {
                    StorageService.Instance.SaveExcludeFounders(value);
                    OnPropertyChanged(nameof(HideFoundersLabel));
                    OnPropertyChanged(nameof(ActiveFiltersCount));
                    OnPropertyChanged(nameof(FiltersButtonLabel));
                    UpdateMasteryCalculations();
                    ApplyFilter();
                }
            }
        }

        private bool _isSyncing;
        public bool IsSyncing
        {
            get => _isSyncing;
            set => SetProperty(ref _isSyncing, value);
        }

        private string _syncStatusText = "Локальная база";
        public string SyncStatusText
        {
            get => _syncStatusText;
            set => SetProperty(ref _syncStatusText, value);
        }

        public bool IsWeaponOrFrameCategory =>
            SelectedCategory != "Косметика" && SelectedCategory != "Украшения Убежища";

        public int ActiveFiltersCount
        {
            get
            {
                int c = 0;
                if (OwnedFilter != "Все") c++;
                if (MasteredFilter != "Все") c++;
                if (VaultedFilter != "Все") c++;
                if (VariantFilter != "Все") c++;
                if (SourceFilter != "Все") c++;
                if (HideFoundersExclusive) c++;
                return c;
            }
        }

        public string FiltersButtonLabel =>
            ActiveFiltersCount > 0 ? $"⚙ Фильтры ({ActiveFiltersCount})" : "⚙ Фильтры";

        public string HideFoundersLabel => "🛡 Не учитывать Основателей (3)";

        public string FilteredCountSummary =>
            $"Показано: {DisplayItems.Count:N0} из {AllItems.Count:N0}";

        // Commands
        public ICommand ToggleOwnedCommand { get; }
        public ICommand ToggleMasteredCommand { get; }
        public ICommand ToggleSubsumedCommand { get; }
        public ICommand ToggleIncarnonCommand { get; }
        public ICommand ToggleCraftQueueCommand { get; }
        public ICommand SelectCategoryCommand { get; }
        public ICommand SelectSubTabCommand { get; }
        public ICommand ToggleFiltersDrawerCommand { get; }
        public ICommand SetOwnedFilterCommand { get; }
        public ICommand SetMasteredFilterCommand { get; }
        public ICommand SetVaultedFilterCommand { get; }
        public ICommand SetVariantFilterCommand { get; }
        public ICommand SetSourceFilterCommand { get; }
        public ICommand ToggleHideFoundersCommand { get; }
        public ICommand ResetFiltersCommand { get; }
        public ICommand ClearSearchCommand { get; }
        public ICommand SyncCatalogCommand { get; }
        public ICommand SaveCollectionCommand { get; }

        public CollectionViewModel(
            Action<CollectionItemModel> toggleCraftCallback,
            Action<string> statusCallback)
        {
            _toggleCraftCallback = toggleCraftCallback;
            _statusCallback = statusCallback;
            _hideFoundersExclusive = StorageService.Instance.LoadExcludeFounders();

            _searchDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(120)
            };
            _searchDebounceTimer.Tick += (_, _) =>
            {
                _searchDebounceTimer.Stop();
                ApplyFilter();
            };

            ToggleOwnedCommand = new RelayCommand(p =>
            {
                if (p is CollectionItemModel item)
                {
                    item.IsOwned = !item.IsOwned;
                    UpdateMasteryCalculations();
                    SaveToDisk();
                    _statusCallback(item.IsOwned ? $"✓ В инвентаре: {item.Name}" : $"Убрано из инвентаря: {item.Name}");
                }
            });

            ToggleMasteredCommand = new RelayCommand(p =>
            {
                if (p is CollectionItemModel item && item.IsMasterable)
                {
                    item.IsMastered = !item.IsMastered;
                    UpdateMasteryCalculations();
                    SaveToDisk();
                    _statusCallback(item.IsMastered ? $"★ Освоено в ранг: {item.Name} (+{item.MasteryXp:N0} XP)" : $"Снята отметка ранга: {item.Name}");
                }
            });

            ToggleSubsumedCommand = new RelayCommand(p =>
            {
                if (p is CollectionItemModel item && item.CanBeSubsumed)
                {
                    item.IsSubsumed = !item.IsSubsumed;
                    SaveToDisk();
                    _statusCallback(item.IsSubsumed ? $"🌸 {item.Name} скормлен Гельминту" : $"🌸 Отметка Гельминта снята: {item.Name}");
                }
            });

            ToggleIncarnonCommand = new RelayCommand(p =>
            {
                if (p is CollectionItemModel item && item.CanHaveIncarnon)
                {
                    item.HasIncarnon = !item.HasIncarnon;
                    SaveToDisk();
                    _statusCallback(item.HasIncarnon ? $"⚡ Инкарнон установлен: {item.Name}" : $"⚡ Инкарнон снят: {item.Name}");
                }
            });

            ToggleCraftQueueCommand = new RelayCommand(p =>
            {
                if (p is CollectionItemModel item)
                {
                    _toggleCraftCallback(item);
                }
            });

            SelectCategoryCommand = new RelayCommand(p =>
            {
                if (p is string cat)
                    SelectedCategory = ToggleFilterValue(SelectedCategory, cat);
            });

            SelectSubTabCommand = new RelayCommand(p =>
            {
                if (p is SubTabItem tab)
                {
                    if (tab.IsSelected)
                    {
                        tab.IsSelected = false;
                        SelectedSubCategory = "Все";
                    }
                    else
                    {
                        foreach (var t in SubTabs) t.IsSelected = (t == tab);
                        SelectedSubCategory = tab.Title;
                    }
                }
                else if (p is string title)
                {
                    var m = SubTabs.FirstOrDefault(t => t.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
                    if (m != null)
                    {
                        bool turnOff = m.IsSelected;
                        foreach (var t in SubTabs) t.IsSelected = !turnOff && (t == m);
                        SelectedSubCategory = turnOff ? "Все" : m.Title;
                    }
                }
            });

            ToggleFiltersDrawerCommand = new RelayCommand(() => IsFiltersExpanded = !IsFiltersExpanded);
            SetOwnedFilterCommand = new RelayCommand(p => { if (p is string s) OwnedFilter = ToggleFilterValue(OwnedFilter, s); });
            SetMasteredFilterCommand = new RelayCommand(p => { if (p is string s) MasteredFilter = ToggleFilterValue(MasteredFilter, s); });
            SetVaultedFilterCommand = new RelayCommand(p => { if (p is string s) VaultedFilter = ToggleFilterValue(VaultedFilter, s); });
            SetVariantFilterCommand = new RelayCommand(p => { if (p is string s) VariantFilter = ToggleFilterValue(VariantFilter, s); });
            SetSourceFilterCommand = new RelayCommand(p => { if (p is string s) SourceFilter = ToggleFilterValue(SourceFilter, s); });
            ToggleHideFoundersCommand = new RelayCommand(() => HideFoundersExclusive = !HideFoundersExclusive);

            ResetFiltersCommand = new RelayCommand(() =>
            {
                _ownedFilter = "Все";
                _masteredFilter = "Все";
                _vaultedFilter = "Все";
                _variantFilter = "Все";
                _sourceFilter = "Все";
                _selectedSubCategory = "Все";
                foreach (var t in SubTabs) t.IsSelected = false;
                _searchText = string.Empty;
                OnPropertyChanged(nameof(OwnedFilter));
                OnPropertyChanged(nameof(MasteredFilter));
                OnPropertyChanged(nameof(VaultedFilter));
                OnPropertyChanged(nameof(VariantFilter));
                OnPropertyChanged(nameof(SourceFilter));
                OnPropertyChanged(nameof(SelectedSubCategory));
                OnPropertyChanged(nameof(SearchText));
                OnPropertyChanged(nameof(ActiveFiltersCount));
                OnPropertyChanged(nameof(FiltersButtonLabel));
                ApplyFilter();
            });

            ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);

            SyncCatalogCommand = new RelayCommand(() => _ = SyncCatalogAsync(force: true));

            SaveCollectionCommand = new RelayCommand(() =>
            {
                SaveToDisk();
                _statusCallback($"💾 Коллекция сохранена ({MasterySummary.OwnedCount} в инвентаре, {MasterySummary.MasteredCount} в ранге)");
            });
        }

        private static string ToggleFilterValue(string current, string clicked) =>
            string.Equals(current, clicked, StringComparison.OrdinalIgnoreCase) ? "Все" : clicked;

        public void Initialize(Func<IEnumerable<string>>? getQueuedNames = null)
        {
            _getQueuedNames = getQueuedNames;
            var items = CatalogSyncService.Instance.LoadCatalogFast();
            ApplyLoadedCatalog(items);
            SyncStatusText = CatalogSyncService.Instance.LastSyncSummary;
            BuildSubTabsForCategory(_selectedCategory);
            ApplyFilter();

            // Passive non-blocking check for DE PublicExport updates in background
            _ = Task.Run(async () =>
            {
                var (updated, liveItems, msg) = await CatalogSyncService.Instance.SyncFromApiAsync(forceRefresh: false).ConfigureAwait(false);
                var disp = Application.Current?.Dispatcher;
                if (disp == null || disp.HasShutdownStarted) return;

                _ = disp.BeginInvoke(new Action(() =>
                {
                    if (updated && liveItems != null)
                    {
                        ApplyLoadedCatalog(liveItems);
                        ApplyFilter();
                        _statusCallback(msg);
                    }
                    SyncStatusText = CatalogSyncService.Instance.LastSyncSummary;
                }));
            });
        }

        public async Task SyncCatalogAsync(bool force)
        {
            if (IsSyncing) return;
            IsSyncing = true;
            SyncStatusText = "Синхронизация DE API...";
            _statusCallback("↻ Загрузка манифестов из DE PublicExport + browse.wf...");
            try
            {
                var (updated, items, msg) = await CatalogSyncService.Instance.SyncFromApiAsync(forceRefresh: force).ConfigureAwait(false);
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (updated && items != null)
                    {
                        ApplyLoadedCatalog(items);
                        ApplyFilter();
                    }
                    SyncStatusText = CatalogSyncService.Instance.LastSyncSummary;
                    _statusCallback(msg);
                });
            }
            finally
            {
                Application.Current?.Dispatcher.Invoke(() => IsSyncing = false);
            }
        }

        private void ApplyLoadedCatalog(List<CollectionItemModel> items)
        {
            var saved = StorageService.Instance.LoadCollectionStateV2();
            var queuedSet = _getQueuedNames != null
                ? new HashSet<string>(_getQueuedNames(), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AllItems.Clear();
            foreach (var item in items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                ApplySavedFlags(item, saved, queuedSet);
                AllItems.Add(item);
            }

            if (!saved.UsesUniqueNames && AllItems.Count > 0 &&
                (saved.Owned.Count > 0 || saved.Mastered.Count > 0 || saved.Subsumed.Count > 0 || saved.Incarnon.Count > 0))
            {
                SaveToDisk();
            }

            UpdateMasteryCalculations();
            OnPropertyChanged(nameof(FilteredCountSummary));
        }

        private static void ApplySavedFlags(CollectionItemModel item, CollectionSavedState saved, HashSet<string> queuedSet)
        {
            if (saved.UsesUniqueNames)
            {
                string key = !string.IsNullOrEmpty(item.UniqueName) ? item.UniqueName : item.Name;
                item.IsOwned = saved.Owned.Contains(key);
                item.IsMastered = item.IsMasterable && saved.Mastered.Contains(key);
                item.IsSubsumed = item.CanBeSubsumed && saved.Subsumed.Contains(key);
                item.HasIncarnon = item.CanHaveIncarnon && saved.Incarnon.Contains(key);
            }
            else
            {
                item.IsOwned = MatchesSavedSet(item, saved.Owned);
                item.IsMastered = item.IsMasterable && MatchesSavedSet(item, saved.Mastered);
                item.IsSubsumed = item.CanBeSubsumed && MatchesSavedSet(item, saved.Subsumed);
                item.HasIncarnon = item.CanHaveIncarnon && MatchesSavedSet(item, saved.Incarnon);
            }
            item.IsInCraftQueue = MatchesSavedSet(item, queuedSet);
        }

        public void MarkOwnedFromCrafting(CollectionItemModel item)
        {
            if (item == null) return;
            item.IsOwned = true;
            item.IsInCraftQueue = false;
            UpdateMasteryCalculations();
            SaveToDisk();
        }

        public void ReloadFromStorage()
        {
            _hideFoundersExclusive = StorageService.Instance.LoadExcludeFounders();
            OnPropertyChanged(nameof(HideFoundersExclusive));
            OnPropertyChanged(nameof(ActiveFiltersCount));
            OnPropertyChanged(nameof(FiltersButtonLabel));

            var saved = StorageService.Instance.LoadCollectionStateV2();
            var queuedSet = _getQueuedNames != null
                ? new HashSet<string>(_getQueuedNames(), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in AllItems)
                ApplySavedFlags(item, saved, queuedSet);

            UpdateMasteryCalculations();
            ApplyFilter();
        }

        private static bool MatchesSavedSet(CollectionItemModel item, HashSet<string> set)
        {
            if (set.Count == 0) return false;
            if (set.Contains(item.Name)) return true;
            if (!string.IsNullOrEmpty(item.NameEn) && set.Contains(item.NameEn)) return true;
            if (!string.IsNullOrEmpty(item.UniqueName) && set.Contains(item.UniqueName)) return true;

            // Handle legacy Cyrillic-only names like "Войдриг (Voidrig)" or bare RU part before " ("
            int paren = item.Name.IndexOf(" (", StringComparison.Ordinal);
            if (paren > 0 && set.Contains(item.Name[..paren].Trim()))
                return true;

            return false;
        }

        public void SyncCraftQueueFlags(IEnumerable<string> queuedNames)
        {
            var set = new HashSet<string>(queuedNames, StringComparer.OrdinalIgnoreCase);
            foreach (var item in AllItems)
            {
                item.IsInCraftQueue = MatchesSavedSet(item, set);
            }
        }

        public void SaveToDisk()
        {
            static string KeyOf(CollectionItemModel i) => !string.IsNullOrEmpty(i.UniqueName) ? i.UniqueName : i.Name;
            var owned = AllItems.Where(i => i.IsOwned).Select(KeyOf).ToList();
            var mastered = AllItems.Where(i => i.IsMastered).Select(KeyOf).ToList();
            var subsumed = AllItems.Where(i => i.IsSubsumed).Select(KeyOf).ToList();
            var incarnon = AllItems.Where(i => i.HasIncarnon).Select(KeyOf).ToList();
            StorageService.Instance.SaveCollectionStateV2(owned, mastered, subsumed, incarnon);
        }

        public void UpdateMasteryCalculations()
        {
            int totalXp = 0;
            int totalMasterable = 0;
            int masteredCount = 0;
            int ownedMasterable = 0;

            bool countFounders = !HideFoundersExclusive;
            foreach (var item in AllItems)
            {
                if (item.IsMasterable && item.MasteryXp > 0 && (!item.IsFounder || countFounders))
                {
                    totalMasterable++;
                    if (item.IsOwned) ownedMasterable++;
                }
                if (item.IsMastered && item.IsMasterable && (!item.IsFounder || countFounders))
                {
                    totalXp += item.MasteryXp;
                    masteredCount++;
                }
            }

            int currentRank = 0;
            for (int r = RankTitles.Length - 1; r >= 0; r--)
            {
                if (totalXp >= GetMasteryXpRequiredForRank(r))
                {
                    currentRank = r;
                    break;
                }
            }

            MasterySummary.TotalMasteryXp = totalXp;
            MasterySummary.CurrentRankNumber = currentRank;
            MasterySummary.RankTitle = currentRank < RankTitles.Length ? RankTitles[currentRank] : $"Ранг {currentRank}";
            MasterySummary.TotalItemsCount = totalMasterable;
            MasterySummary.OwnedCount = ownedMasterable;
            MasterySummary.MasteredCount = masteredCount;

            if (currentRank >= RankTitles.Length - 1)
            {
                MasterySummary.ProgressPercent = 100.0;
                MasterySummary.ProgressText = $"{totalXp:N0} XP • Освоено {masteredCount}/{totalMasterable}";
            }
            else
            {
                int currThreshold = GetMasteryXpRequiredForRank(currentRank);
                int nextThreshold = GetMasteryXpRequiredForRank(currentRank + 1);
                int needed = nextThreshold - currThreshold;
                int inRank = totalXp - currThreshold;
                MasterySummary.ProgressPercent = needed > 0 ? Math.Clamp((double)inRank / needed * 100.0, 0, 100) : 100;
                MasterySummary.ProgressText = $"{inRank:N0} / {needed:N0} XP до Ранга {currentRank + 1} • Освоено {masteredCount}/{totalMasterable}";
            }
        }

        private static int GetMasteryXpRequiredForRank(int rank)
        {
            if (rank <= 0) return 0;
            if (rank <= 30) return 2500 * rank * rank;
            return 2250000 + (rank - 30) * 147500;
        }

        private void BuildSubTabsForCategory(string category)
        {
            SubTabs.Clear();
            string[] tabs = category switch
            {
                "Варфреймы" => new[] { "Прайм", "Стандартные", "Некромехи" },
                "Основное" or "Вторичное" => new[] { "Прайм", "Кува", "Догмат", "Кода 1999", "Инкарнон", "Вандал / Призма / Врайт", "Стандартное" },
                "Ближний бой" => new[] { "Прайм", "Кода 1999", "Инкарнон", "Вандал / Призма / Врайт", "Дувири", "Стандартное" },
                "Модульное" => new[] { "Зо", "Китган", "Усилитель", "К-Драйв" },
                "Арчвинг" => new[] { "Арчвинги", "Тяжелое оружие", "Ближний бой" },
                "Компаньоны" => new[] { "Стражи", "Оружие стража", "Кубрау", "Каваты", "Вульпафилы & Предазиты", "МОА & Гончие" },
                "Косметика" => new[] { "Эфемера", "Сигна", "Броня", "Сандана", "Шлем", "Скин", "Анимации", "Сигилы", "Прочее" },
                "Украшения Убежища" => new[] { "Фигурки", "Флоффы", "Артикулы & Трофеи", "Дисплеи", "Декор Орбитера" },
                _ => new[] { "Варфреймы", "Оружие", "Модульное", "Арчвинг", "Компаньоны", "Косметика", "Украшения Убежища" }
            };

            foreach (string t in tabs)
            {
                SubTabs.Add(new SubTabItem { Title = t, IsSelected = false });
            }
            _selectedSubCategory = "Все";
            OnPropertyChanged(nameof(SelectedSubCategory));
        }

        public void ApplyFilter()
        {
            IEnumerable<CollectionItemModel> q = AllItems;

            // 1. Founder exclusion toggle
            if (HideFoundersExclusive)
            {
                q = q.Where(i => !i.IsFounder);
            }

            // 2. Top Category
            if (!string.IsNullOrEmpty(SelectedCategory) && SelectedCategory != "Все")
            {
                if (SelectedCategory == "Варфреймы")
                    q = q.Where(i => i.Category == "Варфреймы" || i.Category == "Некромехи");
                else
                    q = q.Where(i => i.Category.Equals(SelectedCategory, StringComparison.OrdinalIgnoreCase));
            }

            // 3. Sub-tab filter
            if (!string.IsNullOrEmpty(SelectedSubCategory) && SelectedSubCategory != "Все")
            {
                string s = SelectedSubCategory;
                q = s switch
                {
                    "Прайм" => q.Where(i => i.IsPrime),
                    "Стандартные" or "Стандартное" => q.Where(i => !i.IsPrime && i.SubCategory is "Стандартный" or "Квестовый"),
                    "Некромехи" => q.Where(i => i.Category == "Некромехи"),
                    "Кува" => q.Where(i => i.SubCategory == "Кува"),
                    "Догмат" => q.Where(i => i.SubCategory == "Догмат"),
                    "Кода 1999" => q.Where(i => i.SubCategory == "Кода 1999"),
                    "Инкарнон" => q.Where(i => i.CanHaveIncarnon),
                    "Вандал / Призма / Врайт" => q.Where(i => i.SubCategory is "Вандал" or "Призма" or "Врайт"),
                    "Дувири" => q.Where(i => i.SubCategory == "Дувири"),
                    "Арчвинги" => q.Where(i => i.SlotTag == "Арчвинг"),
                    "Тяжелое оружие" => q.Where(i => i.SlotTag == "Тяжелое оружие"),
                    "Ближний бой" => SelectedCategory == "Арчвинг"
                        ? q.Where(i => i.SlotTag == "Ближний бой")
                        : q.Where(i => i.Category == "Ближний бой"),
                    "Стражи" => q.Where(i => i.SlotTag == "Страж"),
                    "Оружие стража" => q.Where(i => i.SlotTag == "Оружие стража"),
                    "Кубрау" => q.Where(i => i.SlotTag == "Кубрау"),
                    "Каваты" => q.Where(i => i.SlotTag == "Кават"),
                    "Вульпафилы" or "Вульпафилы & Предазиты" => q.Where(i => i.SlotTag is "Вульпафила" or "Предазит"),
                    "МОА & Гончие" => q.Where(i => i.SlotTag is "МОА" or "Гончая"),
                    "Оружие" => q.Where(i => i.Category is "Основное" or "Вторичное" or "Ближний бой"),
                    _ => q.Where(i => i.SubCategory.Equals(s, StringComparison.OrdinalIgnoreCase) ||
                                      i.Category.Equals(s, StringComparison.OrdinalIgnoreCase))
                };
            }

            // 4. Bidirectional Ownership Filter
            if (OwnedFilter == "В наличии") q = q.Where(i => i.IsOwned);
            else if (OwnedFilter == "Не в наличии") q = q.Where(i => !i.IsOwned);

            // 5. Bidirectional Mastery Filter
            if (MasteredFilter == "В ранге") q = q.Where(i => i.IsMastered);
            else if (MasteredFilter == "Не в ранге") q = q.Where(i => i.IsMasterable && !i.IsMastered);

            // 6. Bidirectional Vaulted Filter
            if (VaultedFilter == "В хранилище") q = q.Where(i => i.IsVaulted);
            else if (VaultedFilter == "Доступно") q = q.Where(i => !i.IsVaulted);

            // 7. Variant Filter (inside drawer)
            if (VariantFilter == "Prime") q = q.Where(i => i.IsPrime);
            else if (VariantFilter == "Не-Prime") q = q.Where(i => !i.IsPrime);
            else if (VariantFilter == "Антагонист") q = q.Where(i => i.SubCategory is "Кува" or "Догмат" or "Кода 1999");

            // 8. Source Filter (essential for Cosmetics & Orbiter Figurines!)
            if (SourceFilter != "Все")
            {
                string srcKey = SourceFilter switch
                {
                    "Квест/Дроп" => "QuestOrDrop",
                    "Игр. валюта" => "InGameCurrency",
                    "Кузница" => "Foundry",
                    "Платина" => "Platinum",
                    "Корол. Айя" => "RegalAya",
                    "За реал" => "RealMoney",
                    _ => ""
                };
                if (!string.IsNullOrEmpty(srcKey))
                    q = q.Where(i => i.SourceKind == srcKey);
            }

            // 9. Text Search (matches RU name, EN name, SubCategory, Source, UsedInCrafting)
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                string term = SearchText.Trim();
                q = q.Where(i =>
                    i.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    i.NameEn.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    i.SubCategory.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    i.SourceInfo.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    i.Category.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    (i.UsedInCraftingFor.Length > 0 && i.UsedInCraftingFor.Contains(term, StringComparison.OrdinalIgnoreCase)));
            }

            DisplayItems.ReplaceRange(q);
            OnPropertyChanged(nameof(FilteredCountSummary));
        }
    }
}
