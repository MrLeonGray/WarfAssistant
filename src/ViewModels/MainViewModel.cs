using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using WarfAssistant.Models;
using WarfAssistant.Services;

namespace WarfAssistant.ViewModels
{
    public partial class MainViewModel : ViewModelBase
    {
        private string _activeTab = "Events"; // Default: Events
        private string _statusMessage = "Запуск ассистента • Синхронизация живого Worldstate...";

        private readonly DispatcherTimer _liveClockTimer;

        public EventsViewModel Events { get; }

        public string ActiveTab
        {
            get => _activeTab;
            set => SetProperty(ref _activeTab, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public StorageService Storage => StorageService.Instance;

        // Modular Feature ViewModels
        public CollectionViewModel Collection { get; }
        public CraftingViewModel Crafting { get; }
        public BuilderViewModel Builder { get; }
        public MarketRivenViewModel MarketRiven { get; }

        public List<CollectionItemModel> AllCollectionItems => Collection.AllItems;
        public ObservableRangeCollection<CollectionItemModel> DisplayCollectionItems => Collection.DisplayItems;
        public ObservableRangeCollection<CollectionItemModel> CollectionItems => Collection.DisplayItems;
        public MasterySummaryInfo MasterySummary => Collection.MasterySummary;

        private string _diskCacheSummary = "Вычисление...";
        public string DiskCacheSummary
        {
            get => _diskCacheSummary;
            set => SetProperty(ref _diskCacheSummary, value);
        }

        private string _playerName = "Тэнно";
        public string PlayerName
        {
            get => _playerName;
            set
            {
                string clean = string.IsNullOrWhiteSpace(value) ? "Тэнно" : value.Trim();
                if (SetProperty(ref _playerName, clean))
                    Storage.SavePlayerName(clean);
            }
        }

        private string _lastBackupDisplay = "ещё не создавался";
        public string LastBackupDisplay
        {
            get => _lastBackupDisplay;
            set => SetProperty(ref _lastBackupDisplay, value);
        }

        private bool _desktopNotificationsEnabled;
        public bool DesktopNotificationsEnabled
        {
            get => _desktopNotificationsEnabled;
            set
            {
                if (SetProperty(ref _desktopNotificationsEnabled, value))
                    NotificationService.Instance.SetPreferences(value, _soundNotificationsEnabled);
            }
        }

        private bool _soundNotificationsEnabled;
        public bool SoundNotificationsEnabled
        {
            get => _soundNotificationsEnabled;
            set
            {
                if (SetProperty(ref _soundNotificationsEnabled, value))
                    NotificationService.Instance.SetPreferences(_desktopNotificationsEnabled, value);
            }
        }

        // Commands
        public ICommand SwitchTabCommand { get; }
        public ICommand OpenSaveFolderCommand { get; }
        public ICommand CreateBackupCommand { get; }
        public ICommand ExportProfileCommand { get; }
        public ICommand ImportProfileCommand { get; }
        public ICommand ClearImageCacheCommand { get; }
        public ICommand ClearDataCacheCommand { get; }
        public ICommand RefreshCacheStatsCommand { get; }
        public ICommand RefreshCatalogApiCommand { get; }

        public MainViewModel()
        {
            _playerName = Storage.LoadPlayerName();
            _lastBackupDisplay = Storage.LoadLastBackupDisplay();
            var (deskNotif, soundNotif) = Storage.LoadNotificationSettings();
            _desktopNotificationsEnabled = deskNotif;
            _soundNotificationsEnabled = soundNotif;

            SwitchTabCommand = new RelayCommand(param =>
            {
                if (param is string tab)
                {
                    ActiveTab = tab;
                    if (tab == "Settings") RefreshCacheStats();
                    else if (tab == "MarketRiven" && MarketRiven != null && MarketRiven.SellOrders.Count == 0 && MarketRiven.SelectedMarketItem != null && !MarketRiven.IsLoadingMarket)
                        _ = MarketRiven.LoadMarketOrdersAsync(MarketRiven.SelectedMarketItem);
                    StatusMessage = $"Вкладка: {GetTabDisplayName(tab)}";
                }
            });

            OpenSaveFolderCommand = new RelayCommand(() =>
            {
                bool success = Storage.OpenSaveFolder();
                StatusMessage = success
                    ? "Папка с сохранениями открыта в проводнике"
                    : "Не удалось открыть папку сохранений";
            });

            CreateBackupCommand = new RelayCommand(() =>
            {
                if (Storage.CreateBackup(out string? backupPath, out string? error) && backupPath != null)
                {
                    LastBackupDisplay = Storage.LoadLastBackupDisplay();
                    StatusMessage = $"📦 Резервная копия создана: {Path.GetFileName(backupPath)}";
                }
                else
                {
                    StatusMessage = $"⚠️ Ошибка бэкапа: {error}";
                }
            });

            ExportProfileCommand = new RelayCommand(() =>
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Экспорт профиля WarfAssistant",
                    Filter = "JSON профиль (*.json)|*.json|Все файлы (*.*)|*.*",
                    FileName = $"WarfAssistant_Profile_{DateTime.Now:yyyyMMdd}.json",
                    InitialDirectory = Storage.SaveDirectoryPath
                };
                if (dlg.ShowDialog() == true)
                {
                    StatusMessage = Storage.ExportProfileToFile(dlg.FileName, out string? err)
                        ? $"📤 Профиль экспортирован: {Path.GetFileName(dlg.FileName)}"
                        : $"⚠️ Ошибка экспорта: {err}";
                }
            });

            ImportProfileCommand = new RelayCommand(() =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Импорт профиля WarfAssistant",
                    Filter = "JSON профиль (*.json)|*.json|Все файлы (*.*)|*.*",
                    InitialDirectory = Storage.SaveDirectoryPath
                };
                if (dlg.ShowDialog() == true)
                {
                    if (Storage.ImportProfileFromFile(dlg.FileName, out string? err))
                    {
                        ReloadAllUserState();
                        StatusMessage = $"📥 Профиль импортирован ({Path.GetFileName(dlg.FileName)}), создан бэкап предыдущего состояния";
                    }
                    else
                    {
                        StatusMessage = $"⚠️ Ошибка импорта: {err}";
                    }
                }
            });

            ClearImageCacheCommand = new RelayCommand(() =>
            {
                int removed = ImageCacheService.Instance.ClearDiskCache();
                RefreshCacheStats();
                StatusMessage = $"🗑 Кэш картинок очищен ({removed} файлов)";
            });

            ClearDataCacheCommand = new RelayCommand(() =>
            {
                int removed = ClearJsonDiskCaches();
                RefreshCacheStats();
                StatusMessage = $"🧹 Удалено {removed} файлов JSON/текст-кэша (офлайн-сиды сохранены)";
            });

            RefreshCacheStatsCommand = new RelayCommand(RefreshCacheStats);
            RefreshCatalogApiCommand = new RelayCommand(() => Collection?.SyncCatalogCommand.Execute(null));

            Collection = new CollectionViewModel(
                item => Crafting?.AddOrToggleCollectionItem(item, toggleIfPresent: true),
                msg => StatusMessage = msg);
            Events = new EventsViewModel(() => AllCollectionItems, msg => StatusMessage = msg);
            Crafting = new CraftingViewModel(
                () => AllCollectionItems,
                () => Events.AllFissures,
                msg => StatusMessage = msg,
                colItem => Collection.MarkOwnedFromCrafting(colItem));
            Builder = new BuilderViewModel(msg => StatusMessage = msg);
            MarketRiven = new MarketRivenViewModel(riven =>
            {
                Builder.InjectRivenForWeapon(riven);
                ActiveTab = "Builder";
            }, msg => StatusMessage = msg);
            Events.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(EventsViewModel.SyncLabel))
                {
                    Crafting.RefreshLiveFissureSynergy();
                }
            };

            InitializeInitialData();

            // Setup real 1-second live clock timer
            _liveClockTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _liveClockTimer.Tick += (_, _) => Events.Tick(DateTime.UtcNow);
            _liveClockTimer.Start();

            // Fetch live Worldstate API data in background
            _ = Task.Run(() => Events.FetchLiveWorldstateAsync());
        }

        private string GetTabDisplayName(string tab) => tab switch
        {
            "Events" => "Эвенты, Таймеры и Мир",
            "Crafting" => "Помощник по крафту и фарму",
            "Builder" => "Связанный билдер и симулятор",
            "Collection" => "Отслеживатель коллекций",
            "MarketRiven" => "Маркет и Моды разлома",
            "Settings" => "Настройки и кэш",
            _ => tab
        };

        private void RefreshCacheStats()
        {
            _ = Task.Run(() =>
            {
                var (files, mb) = ImageCacheService.Instance.GetDiskCacheStats();
                string summary = $"{files:N0} файлов ({mb:F2} МБ) в data/cache/images";
                var disp = System.Windows.Application.Current?.Dispatcher;
                if (disp != null && !disp.HasShutdownStarted)
                    _ = disp.BeginInvoke(new Action(() => DiskCacheSummary = summary));
                else
                    DiskCacheSummary = summary;
            });
        }

        private void ReloadAllUserState()
        {
            _playerName = Storage.LoadPlayerName();
            OnPropertyChanged(nameof(PlayerName));
            LastBackupDisplay = Storage.LoadLastBackupDisplay();
            var (deskNotif, soundNotif) = Storage.LoadNotificationSettings();
            _desktopNotificationsEnabled = deskNotif;
            _soundNotificationsEnabled = soundNotif;
            OnPropertyChanged(nameof(DesktopNotificationsEnabled));
            OnPropertyChanged(nameof(SoundNotificationsEnabled));
            NotificationService.Instance.SetPreferences(deskNotif, soundNotif);
            Crafting.Initialize();
            Collection.ReloadFromStorage();
            Events.InitializeDefaults();
            Builder.Initialize();
            MarketRiven.Initialize();
        }

        private int ClearJsonDiskCaches()
        {
            int removed = 0;
            try
            {
                string dataDir = Path.GetDirectoryName(Storage.SaveDirectoryPath)
                    ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                string cacheDir = Path.Combine(dataDir, "cache");
                if (!Directory.Exists(cacheDir)) return 0;

                foreach (string f in Directory.GetFiles(cacheDir))
                {
                    try { File.Delete(f); removed++; } catch { }
                }
            }
            catch { }
            return removed;
        }

        private void InitializeInitialData()
        {
            // 1. Worldstate, Timers, Duviri, Nightwave defaults
            Events.InitializeDefaults();

            // 2. Collection & Codex Catalog (embedded seed + async DE API sync)
            Collection.Initialize(() => StorageService.Instance.LoadCraftQueue());
            Events.RefreshDuviriCircuitIcons();

            // 3. Modular Crafting Queue, DE Recipes & Farm Strategy Optimizer
            Crafting.Initialize();

            // 4. Modular Linked Builder & Combat Simulator
            Builder.Initialize();

            // 5. Stage 6 Market & Riven Helper
            MarketRiven.Initialize();
            RefreshCacheStats();
        }


        private static HashSet<string>? _assetFileNames;
        private static string? _assetsDirectory;

        private static void EnsureAssetDirectoryScanned()
        {
            if (_assetFileNames != null) return;
            _assetFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string dir1 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "assets", "items");
            string dir2 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "data", "assets", "items");

            string chosen = Directory.Exists(dir1) ? dir1 : (Directory.Exists(dir2) ? Path.GetFullPath(dir2) : dir1);
            _assetsDirectory = chosen;

            if (Directory.Exists(chosen))
            {
                foreach (var f in Directory.GetFiles(chosen))
                {
                    _assetFileNames.Add(Path.GetFileName(f));
                }
            }
        }

        public static string? GetItemImageUrl(string? filename, bool allowRemote = false)
        {
            if (string.IsNullOrEmpty(filename)) return null;
            try
            {
                EnsureAssetDirectoryScanned();
                if (_assetsDirectory == null || _assetFileNames == null) return null;

                if (_assetFileNames.Contains(filename))
                    return Path.Combine(_assetsDirectory, filename);

                string noHyphen = filename.Replace("-", "");
                if (_assetFileNames.Contains(noHyphen))
                    return Path.Combine(_assetsDirectory, noHyphen);

                string lower = filename.ToLowerInvariant();
                if (_assetFileNames.Contains(lower))
                    return Path.Combine(_assetsDirectory, lower);

                return allowRemote ? $"https://cdn.warframestat.us/img/{filename}" : null;
            }
            catch
            {
                return allowRemote ? $"https://cdn.warframestat.us/img/{filename}" : null;
            }
        }

        public static string? GetRewardImageUrl(string rewardName, string? apiThumb)
        {
            if (!string.IsNullOrEmpty(apiThumb)) return apiThumb;
            if (string.IsNullOrEmpty(rewardName)) return null;

            if (rewardName.Contains("Катализатор") || rewardName.Contains("Catalyst")) return GetItemImageUrl("ComponentCatalyst.png");
            if (rewardName.Contains("Реактор") || rewardName.Contains("Reactor")) return GetItemImageUrl("ComponentReactor.png");
            if (rewardName.Contains("Форма") || rewardName.Contains("Forma")) return GetItemImageUrl("Forma.png");
            if (rewardName.Contains("Эксилус") || rewardName.Contains("Exilus")) return GetItemImageUrl("UtilityModule.png");
            if (rewardName.Contains("Полетрон") || rewardName.Contains("Fieldron")) return GetItemImageUrl("CorpusComponent.png");
            if (rewardName.Contains("Мутаген") || rewardName.Contains("Mutagen")) return GetItemImageUrl("InfestedComponent.png");
            if (rewardName.Contains("Детонит") || rewardName.Contains("Detonite")) return GetItemImageUrl("GrineerComponent.png");
            if (rewardName.Contains("Координат") || rewardName.Contains("Nav")) return GetItemImageUrl("MutalistNavCoord.png");

            return null;
        }

        public static string? GetTierRelicIcon(string tier)
        {
            return tier switch
            {
                "Lith" => GetItemImageUrl("RelicLithD.png"),
                "Meso" => GetItemImageUrl("RelicMesoD.png"),
                "Neo" => GetItemImageUrl("RelicNeoD.png"),
                "Axi" => GetItemImageUrl("RelicAxiD.png"),
                "Requiem" => GetItemImageUrl("RelicImmortalD.png"),
                "Omnia" => GetItemImageUrl("RelicAxiD.png"),
                _ => GetItemImageUrl("RelicAxiD.png")
            };
        }

        public static string TranslateIncarnonToRu(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            string compact = name.Replace(" ", "").Replace("-", "").Trim();
            return compact.ToLowerInvariant() switch
            {
                "braton" => "Брейтон",
                "lato" => "Лато",
                "skana" => "Скана",
                "paris" => "Парис",
                "kunai" => "Кунай",
                "boar" => "Вепрь",
                "gammacor" => "Гаммакор",
                "angstrum" => "Ангстрем",
                "gorgon" => "Горгона",
                "anku" => "Анку",
                "bo" => "Бо",
                "latron" => "Латрон",
                "furis" => "Фурис",
                "furax" => "Фуракс",
                "strun" => "Стран",
                "lex" => "Лекс",
                "magistar" => "Магистр",
                "boltor" => "Болтор",
                "bronco" => "Бронко",
                "ceramicdagger" => "Керамический кинжал",
                "torid" => "Торид",
                "dualtoxocyst" => "Парные Токсоцисты",
                "dualichor" => "Парные Ихоры",
                "miter" => "Митра",
                "atomos" => "Атомос",
                "ack&brunt" or "ackandbrunt" => "Ак и Брант",
                "soma" => "Сома",
                "vasto" => "Васто",
                "namisolo" => "Нами Соло",
                "burston" => "Бёрстон",
                "zylok" => "Зайлок",
                "sibear" => "Сибирь",
                "dread" => "Страх",
                "despair" => "Отчаяние",
                "hate" => "Ненависть",
                "dera" => "Дера",
                "sybaris" => "Сибарис",
                "cestra" => "Цестра",
                "sicarus" => "Сикарус",
                "okina" => "Окина",
                "vectis" => "Вектис",
                "stug" => "Стаг",
                "ballistica" => "Баллистика",
                "destreza" => "Дестреза",
                "obex" => "Обекс",
                _ => name
            };
        }

    }
}
