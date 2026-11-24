using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using WarfAssistant.Models;
using WarfAssistant.Services;

namespace WarfAssistant.ViewModels
{
    /// <summary>
    /// Modular Stage 5 Linked Builder ViewModel supporting Warframes + Exalted Weapons,
    /// Primary/Secondary/Melee Weapons, Operator Amps, Archwings + Archguns,
    /// Companions + Companion Weapons, Necramechs + Arquebex/Ironbride, and Railjack Plexus.
    /// </summary>
    public sealed class BuilderViewModel : ViewModelBase
    {
        private readonly BuilderDatabaseService _db = BuilderDatabaseService.Instance;
        private readonly Action<string> _statusCallback;

        public IReadOnlyList<string> Categories { get; } = new[]
        {
            "Варфрейм", "Оружие", "Усилитель", "Артефакты Тектолиста", "Арчвинг", "Компаньон", "Некрамех", "Рэйлджек"
        };

        public IReadOnlyList<HelminthOption> HelminthChoices => BuilderDatabaseService.HelminthOptions;
        public IReadOnlyList<ArchonShardOption> ShardChoices => BuilderDatabaseService.ShardOptions;
        public IReadOnlyList<AmpPartOption> AmpScaffoldChoices => BuilderDatabaseService.AmpScaffolds;
        public IReadOnlyList<AmpPartOption> AmpBraceChoices => BuilderDatabaseService.AmpBraces;
        public IReadOnlyList<EnemyFactionOption> FactionChoices => BuilderDatabaseService.FactionOptions;

        public ObservableCollection<BuilderPlatformItem> CategoryPlatforms { get; } = new();
        public ObservableCollection<BuilderPlatformItem> LinkedWeaponChoices { get; } = new();
        public ObservableCollection<BuilderPlatformItem> FilteredLinkedWeaponChoices { get; } = new();
        public ObservableCollection<BuilderModSlot> PrimarySlots { get; } = new();
        public ObservableCollection<BuilderModSlot> PrimaryMainSlots { get; } = new();
        public ObservableCollection<BuilderModSlot> SecondarySlots { get; } = new();
        public ObservableCollection<BuilderModSlot> SecondaryMainSlots { get; } = new();
        public ObservableCollection<BuilderModItem> ModPickerResults { get; } = new();
        public ObservableCollection<BuilderArcaneItem> PrimaryArcaneChoices { get; } = new();
        public ObservableCollection<BuilderArcaneItem> WeaponArcaneChoices { get; } = new();
        public ObservableCollection<ArchonShardSlot> ArchonShards { get; } = new();
        public ObservableCollection<SavedBuildEntry> SavedBuilds { get; } = new();

        public BuilderModSlot PrimaryTopSlot => PrimarySlots[0];
        public BuilderModSlot PrimaryExilusSlot => PrimarySlots[1];
        public BuilderModSlot SecondaryTopSlot => SecondarySlots[0];
        public BuilderModSlot SecondaryExilusSlot => SecondarySlots[1];

        private string _selectedCategory = "Варфрейм";
        public string SelectedCategory
        {
            get => _selectedCategory;
            set
            {
                if (SetProperty(ref _selectedCategory, value))
                {
                    OnCategoryChanged();
                }
            }
        }

        private bool _suppressComboSelectionReset;

        private BuilderPlatformItem? _selectedPrimaryItem;
        public BuilderPlatformItem? SelectedPrimaryItem
        {
            get => _selectedPrimaryItem;
            set
            {
                if (_suppressComboSelectionReset && value == null) return;
                if (SetProperty(ref _selectedPrimaryItem, value))
                {
                    OnPrimaryItemChanged();
                }
            }
        }

        private BuilderPlatformItem? _selectedSecondaryWeapon;
        public BuilderPlatformItem? SelectedSecondaryWeapon
        {
            get => _selectedSecondaryWeapon;
            set
            {
                if (_suppressComboSelectionReset && value == null) return;
                if (SetProperty(ref _selectedSecondaryWeapon, value))
                {
                    OnSecondaryWeaponChanged();
                }
            }
        }

        private HelminthOption _selectedHelminth = BuilderDatabaseService.HelminthOptions[0];
        public HelminthOption SelectedHelminth
        {
            get => _selectedHelminth;
            set { if (SetProperty(ref _selectedHelminth, value)) RecalculateAll(); }
        }

        private BuilderArcaneItem? _selectedPrimaryArcane1;
        public BuilderArcaneItem? SelectedPrimaryArcane1
        {
            get => _selectedPrimaryArcane1;
            set { if (SetProperty(ref _selectedPrimaryArcane1, value)) RecalculateAll(); }
        }

        private BuilderArcaneItem? _selectedPrimaryArcane2;
        public BuilderArcaneItem? SelectedPrimaryArcane2
        {
            get => _selectedPrimaryArcane2;
            set { if (SetProperty(ref _selectedPrimaryArcane2, value)) RecalculateAll(); }
        }

        private BuilderArcaneItem? _selectedWeaponArcane;
        public BuilderArcaneItem? SelectedWeaponArcane
        {
            get => _selectedWeaponArcane;
            set { if (SetProperty(ref _selectedWeaponArcane, value)) RecalculateAll(); }
        }

        private AmpPartOption _selectedAmpScaffold = BuilderDatabaseService.AmpScaffolds[3]; // Propa
        public AmpPartOption SelectedAmpScaffold
        {
            get => _selectedAmpScaffold;
            set { if (SetProperty(ref _selectedAmpScaffold, value)) RecalculateAll(); }
        }

        private AmpPartOption _selectedAmpBrace = BuilderDatabaseService.AmpBraces[2]; // Certus
        public AmpPartOption SelectedAmpBrace
        {
            get => _selectedAmpBrace;
            set { if (SetProperty(ref _selectedAmpBrace, value)) RecalculateAll(); }
        }

        private bool _useAmpScaffoldAltFire = true;
        public bool UseAmpScaffoldAltFire
        {
            get => _useAmpScaffoldAltFire;
            set { if (SetProperty(ref _useAmpScaffoldAltFire, value)) RecalculateAll(); }
        }

        public sealed record IncarnonProfileOption(int Id, string Label, double Dmg, double Cc, double Cd, double Sc, double Fr, double Ms);

        public IReadOnlyList<IncarnonProfileOption> IncarnonProfileChoices { get; } = new[]
        {
            new IncarnonProfileOption(0, "Базовая форма (без эволюций)", 0, 0, 0, 0, 0, 0),
            new IncarnonProfileOption(1, "Инкарнон: Крит (Урон +60, +14% Крит, +0.4× КУ)", 60, 14, 0.4, 0, 0, 0),
            new IncarnonProfileOption(2, "Инкарнон: Статус (Урон +60, +20% Статус, +30% Скор.)", 60, 0, 0, 20, 30, 0),
            new IncarnonProfileOption(3, "Инкарнон: Гибрид (Урон +70, +10% Крит, +12% Статус, +35% Мульт.)", 70, 10, 0.2, 12, 0, 35)
        };

        private int _selectedIncarnonProfileId;
        public IncarnonProfileOption SelectedIncarnonProfile
        {
            get => IncarnonProfileChoices.FirstOrDefault(x => x.Id == _selectedIncarnonProfileId) ?? IncarnonProfileChoices[0];
            set
            {
                int nextId = value?.Id ?? 0;
                if (SetProperty(ref _selectedIncarnonProfileId, nextId))
                {
                    OnPropertyChanged();
                    RecalculateAll();
                }
            }
        }

        private static readonly string[] IncarnonNameTokens =
        {
            "Braton","Брейтон","Брэйтон","Lato","Лато","Skana","Скана","Paris","Парис","Kunai","Кунай",
            "Boar","Вепрь","Gammacor","Гаммакор","Angstrum","Ангстрем","Gorgon","Горгона","Anku","Анку",
            "Latron","Латрон","Furis","Фурис","Furax","Фуракс","Strun","Стран","Magistar","Магистр",
            "Boltor","Болтор","Bronco","Бронко","Ceramic","Керамический","Torid","Торид",
            "Toxocyst","Токсоцист","Ichor","Ихор","Miter","Митра","Atomos","Атомос","Ack & Brunt","Ак и Брант",
            "Soma","Сома","Vasto","Васто","Nami Solo","Нами Соло","Burston","Бёрстон","Zylok","Зайлок",
            "Sibear","Сибирь","Dread","Страх","Despair","Отчаяние","Hate","Ненависть","Dera","Дера",
            "Sybaris","Сибарис","Cestra","Цестра","Sicarus","Сикарус","Okina","Окина",
            "Vectis","Вектис","Stug","Стаг","Ballistica","Баллистика","Destreza","Дестреза","Obex","Обекс",
            "Phenmor","Фенмор","Felarx","Феларкс","Laetum","Лаэтум","Innodem","Иннодем","Praedos","Предос",
            "Onos","Онос","Ruvox","Рувокс","Lex","Лекс","Bo Prime","Бо ("
        };

        public bool IsActiveWeaponIncarnonCapable => ActiveWeaponItem != null && !ActiveWeaponItem.IsExalted &&
            IncarnonNameTokens.Any(tok => ActiveWeaponItem.Name.Contains(tok, StringComparison.OrdinalIgnoreCase));

        private string _platformSearchText = string.Empty;
        public string PlatformSearchText
        {
            get => _platformSearchText;
            set
            {
                if (SetProperty(ref _platformSearchText, value))
                    RefreshPlatformChoices();
            }
        }

        private string _linkedWeaponSearchText = string.Empty;
        public string LinkedWeaponSearchText
        {
            get => _linkedWeaponSearchText;
            set
            {
                if (SetProperty(ref _linkedWeaponSearchText, value))
                    RefreshFilteredLinkedWeapons();
            }
        }

        private string _modPickerSearchText = string.Empty;
        public string ModPickerSearchText
        {
            get => _modPickerSearchText;
            set
            {
                if (SetProperty(ref _modPickerSearchText, value))
                    RefreshModPickerResults();
            }
        }

        private bool _isModPickerOpen;
        public bool IsModPickerOpen
        {
            get => _isModPickerOpen;
            set => SetProperty(ref _isModPickerOpen, value);
        }

        private BuilderModSlot? _editingSlot;
        private bool _editingIsPrimary;
        public string EditingSlotTitle => _editingSlot != null
            ? $"{(_editingIsPrimary ? SelectedPrimaryItem?.Name : ActiveWeaponItem?.Name)} — {_editingSlot.SlotLabel}"
            : "Выбор мода";

        private EnemyFactionOption _selectedFaction = BuilderDatabaseService.FactionOptions[0];
        public EnemyFactionOption SelectedFaction
        {
            get => _selectedFaction;
            set
            {
                if (value != null && SetProperty(ref _selectedFaction, value))
                    RecalculateSimulationOnly();
            }
        }

        private TargetPresetType _selectedTarget = TargetPresetType.EximusOverguard;
        public TargetPresetType SelectedTarget
        {
            get => _selectedTarget;
            set
            {
                if (SetProperty(ref _selectedTarget, value))
                {
                    OnPropertyChanged(nameof(IsTargetNormal));
                    OnPropertyChanged(nameof(IsTargetSteelPath));
                    OnPropertyChanged(nameof(IsTargetEximus));
                    OnPropertyChanged(nameof(IsTargetAcolyte));
                    OnPropertyChanged(nameof(IsTargetDemolyst));
                    OnPropertyChanged(nameof(IsTargetArchon));
                    OnPropertyChanged(nameof(IsTargetEidolon));
                    RecalculateSimulationOnly();
                }
            }
        }

        public bool IsTargetNormal => SelectedTarget == TargetPresetType.Normal;
        public bool IsTargetSteelPath => SelectedTarget == TargetPresetType.SteelPath;
        public bool IsTargetEximus => SelectedTarget == TargetPresetType.EximusOverguard;
        public bool IsTargetAcolyte => SelectedTarget == TargetPresetType.AcolyteBoss;
        public bool IsTargetDemolyst => SelectedTarget == TargetPresetType.DemolystBoss;
        public bool IsTargetArchon => SelectedTarget == TargetPresetType.ArchonBoss;
        public bool IsTargetEidolon => SelectedTarget == TargetPresetType.EidolonBoss;

        public bool IsCatWarframe => SelectedCategory == "Варфрейм";
        public bool IsCatWeapon => SelectedCategory == "Оружие";
        public bool IsCatAmp => SelectedCategory == "Усилитель";
        public bool IsCatArtifact => SelectedCategory == "Артефакты Тектолиста";
        public bool IsCatArchwing => SelectedCategory == "Арчвинг";
        public bool IsCatCompanion => SelectedCategory == "Компаньон";
        public bool IsCatMech => SelectedCategory == "Некрамех";
        public bool IsCatRailjack => SelectedCategory == "Рэйлджек";

        private WarframeCalculatedStats _warframeStats = new();
        public WarframeCalculatedStats WarframeStats
        {
            get => _warframeStats;
            private set => SetProperty(ref _warframeStats, value);
        }

        private WeaponCalculatedStats _weaponStats = new();
        public WeaponCalculatedStats WeaponStats
        {
            get => _weaponStats;
            private set => SetProperty(ref _weaponStats, value);
        }

        private DamageSimulationResult _simResult = new();
        public DamageSimulationResult SimResult
        {
            get => _simResult;
            private set => SetProperty(ref _simResult, value);
        }

        private string _customBuildName = string.Empty;
        public string CustomBuildName
        {
            get => _customBuildName;
            set => SetProperty(ref _customBuildName, value);
        }

        // Dynamic Mode Flags for XAML Visibility
        public bool IsPlatformMode => SelectedCategory != "Оружие" && SelectedCategory != "Усилитель" && SelectedCategory != "Артефакты Тектолиста";
        public bool IsWarframeCategory => SelectedCategory == "Варфрейм";
        public bool IsAmpCategory => SelectedCategory == "Усилитель";
        public bool IsModularAmp => SelectedCategory == "Усилитель" && SelectedPrimaryItem != null && !SelectedPrimaryItem.IsSirocco;
        public bool ShowPrimaryModGrid => SelectedCategory != "Усилитель";
        public bool ShowPrimaryAuraSlot => SelectedCategory == "Варфрейм" || (SelectedCategory == "Оружие" && SelectedPrimaryItem?.SubCategory.Contains("Ближ", StringComparison.OrdinalIgnoreCase) == true);
        public string PrimaryTopSlotLabel => SelectedCategory == "Оружие" ? "СТОЙКА" : "АУРА";
        public bool ShowPrimaryExilusSlot => SelectedCategory is "Варфрейм" or "Оружие";
        public bool HasLinkedWeapon => IsPlatformMode && SelectedSecondaryWeapon != null;
        public bool ShowSecondaryStanceSlot => HasLinkedWeapon && ActiveWeaponItem?.SubCategory.Contains("Ближ", StringComparison.OrdinalIgnoreCase) == true;
        public bool ShowSecondaryExilusSlot => HasLinkedWeapon && ActiveWeaponItem != null && !ActiveWeaponItem.IsExalted && ActiveWeaponItem.Category == "Оружие";
        public bool HasLinkedWeaponCandidates => LinkedWeaponChoices.Count > 1;
        public bool NoLinkedWeaponInPlatformMode => IsPlatformMode && SelectedSecondaryWeapon == null;
        public BuilderPlatformItem? ActiveWeaponItem => IsPlatformMode ? SelectedSecondaryWeapon : SelectedPrimaryItem;
        public string ActiveWeaponTitle => ActiveWeaponItem?.Name ?? "Нет привязанного оружия";
        public string ActiveWeaponSubTitle => ActiveWeaponItem?.SubCategory ?? "Выберите платформу или оружие для расчёта синергии";
        public bool HasPrimaryArcanes => SelectedCategory is "Варфрейм" or "Усилитель";
        public bool HasWeaponArcaneInPrimary => SelectedCategory is "Оружие" or "Артефакты Тектолиста";
        public bool HasWeaponArcaneInSecondary => HasLinkedWeapon;
        public string ActivePrimaryArcaneHeader => SelectedCategory == "Усилитель"
            ? "💎 МИСТИФИКАТОРЫ ОПЕРАТОРА И УСИЛИТЕЛЯ (2 СЛОТА)"
            : "💎 МИСТИФИКАТОРЫ ВАРФРЕЙМА (2 СЛОТА)";
        public string ActivePrimaryModGridTitle => SelectedCategory switch
        {
            "Артефакты Тектолиста" => "АНТИЧНЫЕ МОДЫ АРТЕФАКТА (5 СЛОТОВ)",
            "Оружие" => "МОДЫ ОРУЖИЯ (АРСЕНАЛ)",
            _ => "МОДЫ ПЛАТФОРМЫ (АРСЕНАЛ)"
        };
        public string PrimaryCapacityDisplay => IsPlatformMode ? WarframeStats.CapacityDisplay : WeaponStats.CapacityDisplay;
        public bool IsPrimaryOverCapacity => IsPlatformMode ? WarframeStats.IsOverCapacity : WeaponStats.IsOverCapacity;

        private ActiveBuilderRiven? _activeRiven;
        public ActiveBuilderRiven? ActiveRiven
        {
            get => _activeRiven;
            set
            {
                if (SetProperty(ref _activeRiven, value))
                {
                    OnPropertyChanged(nameof(HasActiveRiven));
                    RecalculateAll();
                }
            }
        }
        public bool HasActiveRiven => _activeRiven != null;

        // Commands
        public ICommand ClearActiveRivenCommand { get; }
        public ICommand SelectCategoryCommand { get; }
        public ICommand SelectTargetPresetCommand { get; }
        public ICommand OpenPrimarySlotPickerCommand { get; }
        public ICommand OpenSecondarySlotPickerCommand { get; }
        public ICommand EquipModCommand { get; }
        public ICommand CloseModPickerCommand { get; }
        public ICommand RemoveSlotModCommand { get; }
        public ICommand CycleSlotPolarityCommand { get; }
        public ICommand StepSlotRankUpCommand { get; }
        public ICommand StepSlotRankDownCommand { get; }
        public ICommand CycleShardOptionCommand { get; }
        public ICommand ToggleShardTauCommand { get; }
        public ICommand SaveBuildCommand { get; }
        public ICommand LoadSavedBuildCommand { get; }
        public ICommand DeleteSavedBuildCommand { get; }
        public ICommand ExportBuildCommand { get; }
        public ICommand ImportBuildCommand { get; }

        public BuilderViewModel(Action<string> statusCallback)
        {
            _statusCallback = statusCallback;

            for (int i = 1; i <= 5; i++)
            {
                var sh = new ArchonShardSlot(i, BuilderDatabaseService.ShardOptions[0]);
                sh.PropertyChanged += (_, _) => RecalculateAll();
                ArchonShards.Add(sh);
            }

            InitSlotGrid(PrimarySlots, includeAura: true);
            InitSlotGrid(SecondarySlots, includeAura: true);
            SyncMainSlotCollections();

            ClearActiveRivenCommand = new RelayCommand(() => ActiveRiven = null);
            SelectCategoryCommand = new RelayCommand(p => { if (p is string c) SelectedCategory = c; });
            SelectTargetPresetCommand = new RelayCommand(p =>
            {
                if (p is TargetPresetType t) SelectedTarget = t;
                else if (p is string s && Enum.TryParse<TargetPresetType>(s, out var parsed)) SelectedTarget = parsed;
            });

            OpenPrimarySlotPickerCommand = new RelayCommand(p => { if (p is BuilderModSlot s) OpenModPicker(s, true); });
            OpenSecondarySlotPickerCommand = new RelayCommand(p => { if (p is BuilderModSlot s) OpenModPicker(s, false); });
            EquipModCommand = new RelayCommand(p => { if (p is BuilderModItem m) EquipModToEditingSlot(m); });
            CloseModPickerCommand = new RelayCommand(() => CloseModPicker());
            RemoveSlotModCommand = new RelayCommand(p =>
            {
                if (p is BuilderModSlot s)
                {
                    s.EquippedMod = null;
                    RecalculateAll();
                }
            });
            CycleSlotPolarityCommand = new RelayCommand(p =>
            {
                if (p is BuilderModSlot s)
                {
                    s.SlotPolarity = (ModPolarity)(((int)s.SlotPolarity + 1) % 6);
                    RecalculateAll();
                }
            });
            StepSlotRankUpCommand = new RelayCommand(p => { if (p is BuilderModSlot s && s.HasMod) { s.Rank++; RecalculateAll(); } });
            StepSlotRankDownCommand = new RelayCommand(p => { if (p is BuilderModSlot s && s.HasMod) { s.Rank--; RecalculateAll(); } });
            CycleShardOptionCommand = new RelayCommand(p =>
            {
                if (p is ArchonShardSlot sh)
                {
                    int idx = BuilderDatabaseService.ShardOptions.ToList().FindIndex(o => o.Id == sh.Selected.Id);
                    sh.Selected = BuilderDatabaseService.ShardOptions[(idx + 1) % BuilderDatabaseService.ShardOptions.Count];
                }
            });
            ToggleShardTauCommand = new RelayCommand(p =>
            {
                if (p is ArchonShardSlot sh) sh.IsTauforged = !sh.IsTauforged;
            });
            SaveBuildCommand = new RelayCommand(SaveCurrentBuild);
            LoadSavedBuildCommand = new RelayCommand(p => { if (p is SavedBuildEntry b) LoadSavedBuild(b); });
            DeleteSavedBuildCommand = new RelayCommand(p =>
            {
                if (p is SavedBuildEntry b)
                {
                    SavedBuilds.Remove(b);
                    StorageService.Instance.SaveSavedBuilds(SavedBuilds);
                    _statusCallback($"🗑 Удалён сохранённый билд: {b.BuildName}");
                }
            });
            ExportBuildCommand = new RelayCommand(ExportBuildToClipboard);
            ImportBuildCommand = new RelayCommand(ImportBuildFromClipboard);
        }

        public void Initialize()
        {
            SavedBuilds.Clear();
            foreach (var b in StorageService.Instance.LoadSavedBuilds())
                SavedBuilds.Add(b);

            _selectedCategory = "Варфрейм";
            OnPropertyChanged(nameof(SelectedCategory));
            SyncMainSlotCollections();
            RefreshPlatformChoices();

            _selectedPrimaryItem = CategoryPlatforms.FirstOrDefault(p =>
                    p.Name.Contains("Миса Прайм", StringComparison.OrdinalIgnoreCase) ||
                    p.Name.Contains("Mesa Prime", StringComparison.OrdinalIgnoreCase))
                ?? CategoryPlatforms.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedPrimaryItem));
            OnPrimaryItemChanged();
        }

        private static void InitSlotGrid(ObservableCollection<BuilderModSlot> list, bool includeAura)
        {
            list.Clear();
            if (includeAura)
            {
                list.Add(new BuilderModSlot { SlotIndex = 0, IsAuraSlot = true });
                list.Add(new BuilderModSlot { SlotIndex = 0, IsExilusSlot = true });
            }
            for (int i = 1; i <= 8; i++)
                list.Add(new BuilderModSlot { SlotIndex = i });
        }

        private void SyncMainSlotCollections()
        {
            PrimaryMainSlots.Clear();
            int primaryMainCount = IsCatArtifact ? 5 : 8;
            for (int i = 2; i < Math.Min(PrimarySlots.Count, 2 + primaryMainCount); i++)
                PrimaryMainSlots.Add(PrimarySlots[i]);

            SecondaryMainSlots.Clear();
            for (int i = 2; i < SecondarySlots.Count; i++)
                SecondaryMainSlots.Add(SecondarySlots[i]);
        }

        private void OnCategoryChanged()
        {
            CloseModPicker();
            _platformSearchText = string.Empty;
            _linkedWeaponSearchText = string.Empty;
            OnPropertyChanged(nameof(PlatformSearchText));
            OnPropertyChanged(nameof(LinkedWeaponSearchText));
            SyncMainSlotCollections();

            RefreshPlatformChoices();
            _selectedPrimaryItem = CategoryPlatforms.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedPrimaryItem));
            OnPrimaryItemChanged();
        }

        private void RefreshPlatformChoices()
        {
            var all = _db.GetPlatformsForCategory(_selectedCategory);
            string q = _platformSearchText.Trim();
            var filtered = string.IsNullOrEmpty(q)
                ? all
                : all.Where(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || p.SubCategory.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

            var keep = _selectedPrimaryItem;
            _suppressComboSelectionReset = true;
            try
            {
                CategoryPlatforms.Clear();
                foreach (var p in filtered) CategoryPlatforms.Add(p);
                if (keep != null && CategoryPlatforms.Contains(keep))
                    OnPropertyChanged(nameof(SelectedPrimaryItem));
            }
            finally
            {
                _suppressComboSelectionReset = false;
            }
        }

        private void RefreshFilteredLinkedWeapons()
        {
            var preserve = _selectedSecondaryWeapon;
            string q = _linkedWeaponSearchText.Trim();
            _suppressComboSelectionReset = true;
            try
            {
                FilteredLinkedWeaponChoices.Clear();
                foreach (var c in LinkedWeaponChoices)
                {
                    if (string.IsNullOrEmpty(q) ||
                        c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        c.SubCategory.Contains(q, StringComparison.OrdinalIgnoreCase))
                    {
                        FilteredLinkedWeaponChoices.Add(c);
                    }
                }

                if (preserve != null && !FilteredLinkedWeaponChoices.Contains(preserve))
                    FilteredLinkedWeaponChoices.Insert(0, preserve);
                if (preserve != null)
                    OnPropertyChanged(nameof(SelectedSecondaryWeapon));
            }
            finally
            {
                _suppressComboSelectionReset = false;
            }
        }

        private void OnPrimaryItemChanged()
        {
            CloseModPicker();
            ClearSlots(PrimarySlots);
            _linkedWeaponSearchText = string.Empty;
            OnPropertyChanged(nameof(LinkedWeaponSearchText));

            _suppressComboSelectionReset = true;
            BuilderPlatformItem? resolvedSecondary = null;
            try
            {
                LinkedWeaponChoices.Clear();
                if (IsPlatformMode)
                {
                    foreach (var c in _db.GetLinkedCandidates(_selectedPrimaryItem, _selectedCategory))
                        LinkedWeaponChoices.Add(c);

                    resolvedSecondary = _db.ResolveLinkedSecondaryWeapon(_selectedPrimaryItem, _selectedCategory);
                }

                _selectedSecondaryWeapon = null;
                OnPropertyChanged(nameof(SelectedSecondaryWeapon));
                RefreshFilteredLinkedWeapons();
            }
            finally
            {
                _suppressComboSelectionReset = false;
            }

            _selectedSecondaryWeapon = resolvedSecondary;
            ClearSlots(SecondarySlots);
            OnPropertyChanged(nameof(SelectedSecondaryWeapon));
            RefreshArcaneDropdowns();
            NotifyModeBindings();
            RecalculateAll();
        }

        private void OnSecondaryWeaponChanged()
        {
            ClearSlots(SecondarySlots);
            RefreshArcaneDropdowns();
            NotifyModeBindings();
            RecalculateAll();
        }

        private void RefreshArcaneDropdowns()
        {
            PrimaryArcaneChoices.Clear();
            foreach (var a in _db.GetArcanesForSlot(_selectedPrimaryItem, _selectedCategory))
                PrimaryArcaneChoices.Add(a);
            _selectedPrimaryArcane1 = PrimaryArcaneChoices.FirstOrDefault();
            _selectedPrimaryArcane2 = PrimaryArcaneChoices.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedPrimaryArcane1));
            OnPropertyChanged(nameof(SelectedPrimaryArcane2));

            WeaponArcaneChoices.Clear();
            var wTarget = ActiveWeaponItem;
            foreach (var a in _db.GetArcanesForSlot(wTarget, wTarget?.Category == "Артефакты Тектолиста" ? "Артефакты Тектолиста" : (wTarget?.SubCategory ?? "Основное")))
                WeaponArcaneChoices.Add(a);
            _selectedWeaponArcane = WeaponArcaneChoices.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedWeaponArcane));
        }

        private void FillSlotsByNames(ObservableCollection<BuilderModSlot> slots, IEnumerable<string> modNames, string bucket)
        {
            ClearSlots(slots);
            int stdIdx = 2;
            foreach (var name in modNames)
            {
                var mod = _db.FindModByName(name, bucket);
                if (mod == null) continue;

                BuilderModSlot? target;
                if (mod.IsAuraOrStance)
                    target = slots[0];
                else if (mod.IsExilus && !slots[1].HasMod)
                    target = slots[1];
                else if (stdIdx < slots.Count)
                    target = slots[stdIdx++];
                else if (!slots[1].HasMod && (bucket != "Варфрейм" || mod.IsExilus))
                    target = slots[1];
                else
                    continue;

                target.SlotPolarity = mod.Polarity;
                target.EquippedMod = mod;
            }
        }

        private static void ClearSlots(ObservableCollection<BuilderModSlot> slots)
        {
            foreach (var s in slots)
            {
                s.EquippedMod = null;
                s.SlotPolarity = ModPolarity.None;
                s.IsSelected = false;
            }
        }

        private void OpenModPicker(BuilderModSlot slot, bool isPrimary)
        {
            foreach (var s in PrimarySlots) s.IsSelected = false;
            foreach (var s in SecondarySlots) s.IsSelected = false;

            _editingSlot = slot;
            _editingIsPrimary = isPrimary;
            slot.IsSelected = true;
            _modPickerSearchText = string.Empty;
            OnPropertyChanged(nameof(ModPickerSearchText));
            OnPropertyChanged(nameof(EditingSlotTitle));
            RefreshModPickerResults();
            IsModPickerOpen = true;
        }

        private void CloseModPicker()
        {
            foreach (var s in PrimarySlots) s.IsSelected = false;
            foreach (var s in SecondarySlots) s.IsSelected = false;
            _editingSlot = null;
            IsModPickerOpen = false;
        }

        private void RefreshModPickerResults()
        {
            ModPickerResults.Clear();
            if (_editingSlot == null) return;

            var targetItem = _editingIsPrimary ? _selectedPrimaryItem : ActiveWeaponItem;
            var compatMods = _db.GetCompatibleMods(targetItem, _editingSlot.IsAuraSlot, _editingSlot.IsExilusSlot, _modPickerSearchText);

            var installedIds = (_editingIsPrimary ? PrimarySlots : SecondarySlots)
                .Where(s => s != _editingSlot && s.HasMod)
                .Select(s => s.EquippedMod!.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var m in compatMods.Where(x => !installedIds.Contains(x.Id)))
                ModPickerResults.Add(m);
        }

        private void EquipModToEditingSlot(BuilderModItem mod)
        {
            if (_editingSlot == null || mod == null) return;
            _editingSlot.EquippedMod = mod;
            if (_editingSlot.SlotPolarity == ModPolarity.None && mod.Polarity != ModPolarity.None)
                _editingSlot.SlotPolarity = mod.Polarity;
            CloseModPicker();
            RecalculateAll();
        }

        public void RecalculateAll()
        {
            var inc = IsActiveWeaponIncarnonCapable ? SelectedIncarnonProfile : IncarnonProfileChoices[0];
            ActiveBuilderRiven? applicableLinkedRiven = (_activeRiven != null && ActiveWeaponItem != null &&
                ((!string.IsNullOrEmpty(_activeRiven.WeaponName) && ActiveWeaponItem.Name.Contains(_activeRiven.WeaponName, StringComparison.OrdinalIgnoreCase)) ||
                 (!string.IsNullOrEmpty(_activeRiven.WeaponNameEn) && ActiveWeaponItem.Name.Contains(_activeRiven.WeaponNameEn, StringComparison.OrdinalIgnoreCase))))
                ? _activeRiven
                : null;

            if (IsPlatformMode)
            {
                WarframeStats = BuilderSimulationEngine.ComputeWarframeStats(
                    _selectedPrimaryItem, PrimarySlots, _selectedHelminth, ArchonShards,
                    _selectedPrimaryArcane1, _selectedPrimaryArcane2);

                WeaponStats = ActiveWeaponItem != null
                    ? BuilderSimulationEngine.ComputeWeaponStats(
                        ActiveWeaponItem, SecondarySlots, _selectedWeaponArcane, WarframeStats,
                        _selectedAmpScaffold, _selectedAmpBrace, true, applicableLinkedRiven,
                        inc.Dmg, inc.Cc, inc.Cd, inc.Sc, inc.Fr, inc.Ms, _useAmpScaffoldAltFire)
                    : new WeaponCalculatedStats();
            }
            else if (IsAmpCategory)
            {
                // Operator Amp / Sirocco directly in Column 1 (PrimaryArcane1 -> weaponArcane, PrimaryArcane2 -> frame/operator buff)
                WarframeStats = BuilderSimulationEngine.ComputeWarframeStats(
                    _selectedPrimaryItem, PrimarySlots, null, Array.Empty<ArchonShardSlot>(),
                    null, _selectedPrimaryArcane2);

                WeaponStats = BuilderSimulationEngine.ComputeWeaponStats(
                    _selectedPrimaryItem, PrimarySlots, _selectedPrimaryArcane1, WarframeStats,
                    _selectedAmpScaffold, _selectedAmpBrace, true, _activeRiven,
                    0, 0, 0, 0, 0, 0, _useAmpScaffoldAltFire);
            }
            else
            {
                // Standalone Weapon or Tektolyst Artifact directly in Column 1 (1 Weapon/Artifact Arcane)
                WarframeStats = BuilderSimulationEngine.ComputeWarframeStats(
                    _selectedPrimaryItem, PrimarySlots, null, Array.Empty<ArchonShardSlot>(),
                    null, null);

                WeaponStats = BuilderSimulationEngine.ComputeWeaponStats(
                    _selectedPrimaryItem, PrimarySlots, _selectedWeaponArcane, null,
                    null, null, true, _activeRiven,
                    inc.Dmg, inc.Cc, inc.Cd, inc.Sc, inc.Fr, inc.Ms, _useAmpScaffoldAltFire);
            }

            OnPropertyChanged(nameof(PrimaryCapacityDisplay));
            OnPropertyChanged(nameof(IsPrimaryOverCapacity));
            RecalculateSimulationOnly();
        }

        public void InjectRivenForWeapon(ActiveBuilderRiven riven)
        {
            if (riven == null) return;
            _selectedCategory = "Оружие";
            OnPropertyChanged(nameof(SelectedCategory));
            SyncMainSlotCollections();

            _platformSearchText = string.Empty;
            _linkedWeaponSearchText = string.Empty;
            OnPropertyChanged(nameof(PlatformSearchText));
            OnPropertyChanged(nameof(LinkedWeaponSearchText));
            RefreshPlatformChoices();

            _suppressComboSelectionReset = true;
            try
            {
                LinkedWeaponChoices.Clear();
                _selectedSecondaryWeapon = null;
                RefreshFilteredLinkedWeapons();
            }
            finally
            {
                _suppressComboSelectionReset = false;
            }
            OnPropertyChanged(nameof(SelectedSecondaryWeapon));

            var match = CategoryPlatforms.FirstOrDefault(p =>
                (!string.IsNullOrEmpty(riven.WeaponName) && p.Name.Contains(riven.WeaponName, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(riven.WeaponNameEn) && p.Name.Contains(riven.WeaponNameEn, StringComparison.OrdinalIgnoreCase)));
            if (match != null)
            {
                _selectedPrimaryItem = match;
                OnPropertyChanged(nameof(SelectedPrimaryItem));
                string bucket = _db.GetModBucketForItem(match);
                string[] sampleMods = bucket switch
                {
                    "Вторичное" => new[] { "Удар осы", "Гальванизированный Двойной Ствол", "Пистолетный гамбит Прайм", "Дробитель Прайм" },
                    "Ближний бой" => new[] { "Избыточное состояние", "Приток крови", "Степень ранений", "Берсеркер" },
                    "Оружие арчвинга" => new[] { "Рубиновый ствол", "Двойные патроны", "Смертоносная прицельность", "Опустошающие патроны" },
                    _ when riven.RivenType == "shotgun" => new[] { "В упор Прайм", "Адский патронник", "Опустошение Прайм", "Порочный мороз" },
                    _ => new[] { "Зазубрины", "Раздельный патронник", "Точечный удар", "Чутье на уязвимости" }
                };
                FillSlotsByNames(PrimarySlots, sampleMods, bucket);
            }

            RefreshArcaneDropdowns();
            ActiveRiven = riven;
            NotifyModeBindings();
            RecalculateAll();
            _statusCallback($"🎲 Riven ({riven.WeaponName}) подключён к симулятору Билдера!");
        }

        private void RecalculateSimulationOnly()
        {
            SimResult = BuilderSimulationEngine.SimulateTarget(
                SelectedTarget, SelectedFaction.Type, WeaponStats, IsPlatformMode ? WarframeStats : null);
        }

        private void NotifyModeBindings()
        {
            OnPropertyChanged(nameof(IsPlatformMode));
            OnPropertyChanged(nameof(IsWarframeCategory));
            OnPropertyChanged(nameof(IsAmpCategory));
            OnPropertyChanged(nameof(IsModularAmp));
            OnPropertyChanged(nameof(ShowPrimaryModGrid));
            OnPropertyChanged(nameof(ShowPrimaryAuraSlot));
            OnPropertyChanged(nameof(PrimaryTopSlotLabel));
            OnPropertyChanged(nameof(ShowPrimaryExilusSlot));
            OnPropertyChanged(nameof(HasPrimaryArcanes));
            OnPropertyChanged(nameof(HasWeaponArcaneInPrimary));
            OnPropertyChanged(nameof(HasWeaponArcaneInSecondary));
            OnPropertyChanged(nameof(ActivePrimaryArcaneHeader));
            OnPropertyChanged(nameof(ActivePrimaryModGridTitle));
            OnPropertyChanged(nameof(IsCatWarframe));
            OnPropertyChanged(nameof(IsCatWeapon));
            OnPropertyChanged(nameof(IsCatAmp));
            OnPropertyChanged(nameof(IsCatArtifact));
            OnPropertyChanged(nameof(IsCatArchwing));
            OnPropertyChanged(nameof(IsCatCompanion));
            OnPropertyChanged(nameof(IsCatMech));
            OnPropertyChanged(nameof(IsCatRailjack));
            OnPropertyChanged(nameof(HasLinkedWeapon));
            OnPropertyChanged(nameof(ShowSecondaryStanceSlot));
            OnPropertyChanged(nameof(ShowSecondaryExilusSlot));
            OnPropertyChanged(nameof(HasLinkedWeaponCandidates));
            OnPropertyChanged(nameof(NoLinkedWeaponInPlatformMode));
            OnPropertyChanged(nameof(ActiveWeaponItem));
            OnPropertyChanged(nameof(ActiveWeaponTitle));
            OnPropertyChanged(nameof(ActiveWeaponSubTitle));
            OnPropertyChanged(nameof(IsActiveWeaponIncarnonCapable));
            OnPropertyChanged(nameof(PrimaryCapacityDisplay));
            OnPropertyChanged(nameof(IsPrimaryOverCapacity));
        }

        private void SaveCurrentBuild()
        {
            if (_selectedPrimaryItem == null) return;
            string name = string.IsNullOrWhiteSpace(CustomBuildName)
                ? $"{_selectedPrimaryItem.Name}{(HasLinkedWeapon ? " + " + ActiveWeaponTitle : "")}"
                : CustomBuildName.Trim();

            var entry = new SavedBuildEntry
            {
                BuildName = name,
                Category = SelectedCategory,
                PrimaryItemId = _selectedPrimaryItem.Id,
                SecondaryItemId = ActiveWeaponItem?.Id ?? string.Empty,
                PrimaryModIds = PrimarySlots.Select(s => s.EquippedMod?.Id ?? string.Empty).ToList(),
                PrimaryPolarities = PrimarySlots.Select(s => s.SlotPolarity.ToString()).ToList(),
                PrimaryRanks = PrimarySlots.Select(s => s.Rank).ToList(),
                SecondaryModIds = SecondarySlots.Select(s => s.EquippedMod?.Id ?? string.Empty).ToList(),
                SecondaryPolarities = SecondarySlots.Select(s => s.SlotPolarity.ToString()).ToList(),
                SecondaryRanks = SecondarySlots.Select(s => s.Rank).ToList(),
                HelminthId = SelectedHelminth.Id,
                ShardIds = ArchonShards.Select(s => s.Selected.Id).ToList(),
                ShardTauFlags = ArchonShards.Select(s => s.IsTauforged).ToList(),
                PrimaryArcane1 = SelectedPrimaryArcane1?.Id ?? string.Empty,
                PrimaryArcane2 = SelectedPrimaryArcane2?.Id ?? string.Empty,
                WeaponArcane = SelectedWeaponArcane?.Id ?? string.Empty,
                ScaffoldTier = SelectedAmpScaffold.Tier,
                BraceTier = SelectedAmpBrace.Tier,
                ActiveRiven = ActiveRiven,
                IncarnonPerkProfile = _selectedIncarnonProfileId,
                SavedAt = DateTime.Now.ToString("dd.MM HH:mm")
            };

            SavedBuilds.Insert(0, entry);
            StorageService.Instance.SaveSavedBuilds(SavedBuilds);
            CustomBuildName = string.Empty;
            _statusCallback($"💾 Билд сохранён: {name}");
        }

        private void LoadSavedBuild(SavedBuildEntry b)
        {
            _selectedCategory = b.Category;
            OnPropertyChanged(nameof(SelectedCategory));
            SyncMainSlotCollections();

            _platformSearchText = string.Empty;
            _linkedWeaponSearchText = string.Empty;
            OnPropertyChanged(nameof(PlatformSearchText));
            OnPropertyChanged(nameof(LinkedWeaponSearchText));
            RefreshPlatformChoices();

            _selectedPrimaryItem = _db.FindPlatformById(b.PrimaryItemId) ?? CategoryPlatforms.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedPrimaryItem));

            _suppressComboSelectionReset = true;
            try
            {
                LinkedWeaponChoices.Clear();
                if (IsPlatformMode)
                {
                    foreach (var c in _db.GetLinkedCandidates(_selectedPrimaryItem, _selectedCategory))
                        LinkedWeaponChoices.Add(c);
                    _selectedSecondaryWeapon = _db.FindPlatformById(b.SecondaryItemId) ?? _db.ResolveLinkedSecondaryWeapon(_selectedPrimaryItem, _selectedCategory);
                }
                else
                {
                    _selectedSecondaryWeapon = null;
                }
                RefreshFilteredLinkedWeapons();
            }
            finally
            {
                _suppressComboSelectionReset = false;
            }
            OnPropertyChanged(nameof(SelectedSecondaryWeapon));

            RefreshArcaneDropdowns();
            ApplySavedModIds(PrimarySlots, b.PrimaryModIds, b.PrimaryPolarities, b.PrimaryRanks);
            ApplySavedModIds(SecondarySlots, b.SecondaryModIds, b.SecondaryPolarities, b.SecondaryRanks);

            _selectedHelminth = BuilderDatabaseService.HelminthOptions.FirstOrDefault(h => h.Id == b.HelminthId) ?? BuilderDatabaseService.HelminthOptions[0];
            OnPropertyChanged(nameof(SelectedHelminth));

            if (b.ShardIds.Count > 0)
            {
                for (int i = 0; i < Math.Min(ArchonShards.Count, b.ShardIds.Count); i++)
                {
                    var opt = BuilderDatabaseService.ShardOptions.FirstOrDefault(o => o.Id == b.ShardIds[i]) ?? BuilderDatabaseService.ShardOptions[0];
                    ArchonShards[i].Selected = opt;
                    ArchonShards[i].IsTauforged = i < b.ShardTauFlags.Count && b.ShardTauFlags[i];
                }
            }

            _selectedAmpScaffold = BuilderDatabaseService.AmpScaffolds.FirstOrDefault(s => s.Tier == b.ScaffoldTier) ?? _selectedAmpScaffold;
            _selectedAmpBrace = BuilderDatabaseService.AmpBraces.FirstOrDefault(br => br.Tier == b.BraceTier) ?? _selectedAmpBrace;
            OnPropertyChanged(nameof(SelectedAmpScaffold));
            OnPropertyChanged(nameof(SelectedAmpBrace));

            _selectedPrimaryArcane1 = PrimaryArcaneChoices.FirstOrDefault(a => a.Id == b.PrimaryArcane1) ?? _selectedPrimaryArcane1;
            _selectedPrimaryArcane2 = PrimaryArcaneChoices.FirstOrDefault(a => a.Id == b.PrimaryArcane2) ?? _selectedPrimaryArcane2;
            _selectedWeaponArcane = WeaponArcaneChoices.FirstOrDefault(a => a.Id == b.WeaponArcane) ?? _selectedWeaponArcane;
            OnPropertyChanged(nameof(SelectedPrimaryArcane1));
            OnPropertyChanged(nameof(SelectedPrimaryArcane2));
            OnPropertyChanged(nameof(SelectedWeaponArcane));

            _selectedIncarnonProfileId = b.IncarnonPerkProfile;
            OnPropertyChanged(nameof(SelectedIncarnonProfile));
            _activeRiven = b.ActiveRiven;
            OnPropertyChanged(nameof(ActiveRiven));
            OnPropertyChanged(nameof(HasActiveRiven));

            NotifyModeBindings();
            RecalculateAll();
            _statusCallback($"📂 Загружен билд: {b.BuildName}");
        }

        private void ApplySavedModIds(
            ObservableCollection<BuilderModSlot> slots,
            List<string> modIds,
            List<string>? polarities = null,
            List<int>? ranks = null)
        {
            ClearSlots(slots);
            for (int i = 0; i < Math.Min(slots.Count, modIds.Count); i++)
            {
                if (polarities != null && i < polarities.Count && Enum.TryParse<ModPolarity>(polarities[i], out var pol))
                    slots[i].SlotPolarity = pol;

                var m = _db.FindModById(modIds[i]);
                if (m != null)
                {
                    if (slots[i].SlotPolarity == ModPolarity.None && (polarities == null || polarities.Count == 0))
                        slots[i].SlotPolarity = m.Polarity;
                    slots[i].EquippedMod = m;
                    if (ranks != null && i < ranks.Count)
                        slots[i].Rank = ranks[i];
                }
            }
        }

        private void ImportBuildFromClipboard()
        {
            try
            {
                if (!Clipboard.ContainsText())
                {
                    _statusCallback("⚠️ Буфер обмена не содержит текст билда.");
                    return;
                }
                string text = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text)) return;

                string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                bool applied = false;

                foreach (string rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (line.StartsWith("Моды платформы:", StringComparison.OrdinalIgnoreCase))
                    {
                        string listStr = line["Моды платформы:".Length..].Trim();
                        var names = listStr.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s));
                        FillSlotsByNames(PrimarySlots, names, _db.GetModBucketForItem(SelectedPrimaryItem));
                        applied = true;
                    }
                    else if (line.StartsWith("Моды оружия:", StringComparison.OrdinalIgnoreCase) && IsPlatformMode && ActiveWeaponItem != null)
                    {
                        string listStr = line["Моды оружия:".Length..].Trim();
                        var names = listStr.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s));
                        FillSlotsByNames(SecondarySlots, names, _db.GetModBucketForItem(ActiveWeaponItem));
                        applied = true;
                    }
                    else if (line.StartsWith("Моды:", StringComparison.OrdinalIgnoreCase))
                    {
                        string listStr = line["Моды:".Length..].Trim();
                        var names = listStr.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s));
                        FillSlotsByNames(PrimarySlots, names, _db.GetModBucketForItem(SelectedPrimaryItem));
                        applied = true;
                    }
                }

                if (!applied)
                {
                    var fallbackNames = text.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => s.Length >= 2)
                        .ToList();
                    if (fallbackNames.Count > 0)
                    {
                        var targetSlots = IsPlatformMode && ActiveWeaponItem != null && !fallbackNames.Any(n => _db.FindModByName(n, _db.GetModBucketForItem(SelectedPrimaryItem)) != null)
                            ? SecondarySlots : PrimarySlots;
                        var targetItem = targetSlots == SecondarySlots ? ActiveWeaponItem : SelectedPrimaryItem;
                        FillSlotsByNames(targetSlots, fallbackNames, _db.GetModBucketForItem(targetItem));
                        applied = true;
                    }
                }

                if (applied)
                {
                    RecalculateAll();
                    _statusCallback("📥 Моды билда импортированы из буфера обмена!");
                }
                else
                {
                    _statusCallback("⚠️ Не удалось распознать моды в тексте буфера обмена.");
                }
            }
            catch
            {
                _statusCallback("⚠️ Ошибка чтения буфера обмена при импорте.");
            }
        }

        private void ExportBuildToClipboard()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== WARF ASSISTANT BUILD: {SelectedPrimaryItem?.Name}{(HasLinkedWeapon ? " + " + ActiveWeaponTitle : "")} ===");
            if (IsPlatformMode)
            {
                sb.AppendLine($"Статы: Сила {WarframeStats.StrengthPercent:F0}% | Длит {WarframeStats.DurationPercent:F0}% | Эффект {WarframeStats.EfficiencyPercent:F0}% | Зона {WarframeStats.RangePercent:F0}% | EHP {WarframeStats.EffectiveHp:N0}");
                if (!string.IsNullOrEmpty(WarframeStats.SynergySummaryText))
                    sb.AppendLine($"Синергия: {WarframeStats.SynergySummaryText}");
                sb.AppendLine("Моды платформы: " + string.Join(", ", PrimarySlots.Where(s => s.HasMod).Select(s => s.DisplayName)));
                if (HasLinkedWeapon)
                    sb.AppendLine("Моды оружия: " + string.Join(", ", SecondarySlots.Where(s => s.HasMod).Select(s => s.DisplayName)));
            }
            else
            {
                sb.AppendLine("Моды: " + string.Join(", ", PrimarySlots.Where(s => s.HasMod).Select(s => s.DisplayName)));
            }
            sb.AppendLine($"Урон: Burst DPS = {SimResult.BurstDps:N0} | Эффективный DPS = {SimResult.EffectiveDps:N0} | TTK ({SimResult.TargetName}) = {SimResult.TimeToKillSeconds:F2} сек");

            try
            {
                Clipboard.SetDataObject(sb.ToString(), true);
                _statusCallback("📋 Билд и расчёт DPS скопированы в буфер обмена");
            }
            catch
            {
                _statusCallback("⚠️ Буфер обмена временно недоступен");
            }
        }
    }
}
