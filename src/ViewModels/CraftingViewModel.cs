using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using WarfAssistant.Models;
using WarfAssistant.Services;

namespace WarfAssistant.ViewModels
{
    /// <summary>
    /// Modular ViewModel for Crafting Planner, Stock-Aware Multi-Level DE Recipe Resolution,
    /// Interactive Resource Inventory Tracking with DE Icons, and Set-Cover Farming Optimizer.
    /// </summary>
    public sealed class CraftingViewModel : ViewModelBase
    {
        private readonly Func<IReadOnlyList<CollectionItemModel>> _getCatalog;
        private readonly Func<IReadOnlyList<VoidFissureItem>> _getLiveFissures;
        private readonly Action<string> _statusCallback;
        private readonly Action<CollectionItemModel>? _onItemCraftedCallback;
        private readonly RecipeDatabaseService _recipeDb = RecipeDatabaseService.Instance;

        private Dictionary<string, int> _playerResourceInventory = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _activeOptionalUtilities = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _expandableUidsByQueueKey = new(StringComparer.OrdinalIgnoreCase);

        public ObservableCollection<CraftQueueItem> CraftQueue { get; } = new();
        public ObservableCollection<CraftResource> AggregatedResources { get; } = new();
        public ObservableCollection<CraftResource> DisplayAggregatedResources { get; } = new();
        public ObservableCollection<FarmStrategyStep> OptimalRouteSteps { get; } = new();
        public ObservableCollection<FarmNodeRecommendation> FarmRecommendations { get; } = new();
        public ObservableCollection<CollectionItemModel> AvailableBlueprintsForAdd { get; } = new();

        private string _craftBlueprintSearchText = string.Empty;
        public string CraftBlueprintSearchText
        {
            get => _craftBlueprintSearchText;
            set
            {
                if (SetProperty(ref _craftBlueprintSearchText, value))
                {
                    OnPropertyChanged(nameof(IsSearchTextEmpty));
                    FilterAvailableBlueprints();
                }
            }
        }

        private bool _isBlueprintSearchDropdownOpen;
        public bool IsBlueprintSearchDropdownOpen
        {
            get => _isBlueprintSearchDropdownOpen;
            set => SetProperty(ref _isBlueprintSearchDropdownOpen, value);
        }

        private bool _onlyShowDeficitResources;
        public bool OnlyShowDeficitResources
        {
            get => _onlyShowDeficitResources;
            set
            {
                if (SetProperty(ref _onlyShowDeficitResources, value))
                {
                    UpdateDisplayAggregatedResources();
                }
            }
        }

        private bool _prioritizeSteelPath = true;
        public bool PrioritizeSteelPath
        {
            get => _prioritizeSteelPath;
            set
            {
                if (SetProperty(ref _prioritizeSteelPath, value))
                {
                    RecalculateFarmingOnly();
                }
            }
        }

        public int TotalQueueItemsCount => CraftQueue.Count;
        public bool IsQueueEmpty => CraftQueue.Count == 0;
        public bool HasNoRecommendations => FarmRecommendations.Count == 0;
        public bool IsSearchTextEmpty => string.IsNullOrEmpty(CraftBlueprintSearchText);
        public int TotalQueueUnitsCount => CraftQueue.Sum(c => c.Quantity);
        public int TotalCreditsNeeded => CraftQueue.Sum(c => c.TotalCreditsCost);
        public string TotalCreditsDisplay => $"💳 {TotalCreditsNeeded:N0} Кредитов";
        public int TotalDeficitResourcesCount => AggregatedResources.Count(r => r.DeficitAmount > 0 && !r.Name.Equals("Кредиты", StringComparison.OrdinalIgnoreCase));
        public bool HasDeficitResources => AggregatedResources.Any(r => r.DeficitAmount > 0);
        public string DeficitSummaryText => IsQueueEmpty
            ? "Очередь пуста"
            : (TotalDeficitResourcesCount > 0
                ? $"Дефицит: {TotalDeficitResourcesCount} рес."
                : (HasDeficitResources ? "Не хватает кредитов" : "Все ресурсы собраны!"));
        public int MatchedNodesCount => FarmRecommendations.Count;
        public string MatchedNodesSummaryText => IsQueueEmpty
            ? "Нет плана"
            : (OptimalRouteSteps.Count > 0
                ? $"Маршрут: {OptimalRouteSteps.Count} миссии"
                : (MatchedNodesCount > 0 ? $"Узлов: {MatchedNodesCount}" : "Дефицит отсутствует"));
        public bool HasOptimalRoute => OptimalRouteSteps.Count > 0;
        public string OptimalRouteSummaryText => HasOptimalRoute
            ? $"Минимальный маршрут за {OptimalRouteSteps.Count} шага(ов) закрывает дефицит выбранных чертежей:"
            : string.Empty;
        public string StrategyQueueContextText => CraftQueue.Count > 0
            ? "Для очереди: " + string.Join(", ", CraftQueue.Take(3).Select(c => c.Quantity > 1 ? $"{c.Name} ×{c.Quantity}" : c.Name)) + (CraftQueue.Count > 3 ? $" и ещё {CraftQueue.Count - 3}" : "")
            : "Добавьте чертежи слева";
        public string EmptyStrategyHintText => IsQueueEmpty
            ? "Очередь крафта пуста — выберите чертёж в поиске сверху, кнопку быстрого добавления или [🛠 В крафт] в Коллекции, чтобы рассчитать список ресурсов и стратегию фарма."
            : "Все необходимые ресурсы и промежуточные компоненты (Формы / Додзё) для выбранных чертежей уже есть на складе!";

        public ICommand AddPresetBlueprintCommand { get; }
        public ICommand SelectBlueprintForAddCommand { get; }
        public ICommand RemoveCraftQueueItemCommand { get; }
        public ICommand IncrementQueueQtyCommand { get; }
        public ICommand DecrementQueueQtyCommand { get; }
        public ICommand MarkCraftedCommand { get; }
        public ICommand ClearCraftQueueCommand { get; }
        public ICommand ClearBlueprintSearchCommand { get; }
        public ICommand ToggleDeficitFilterCommand { get; }
        public ICommand TogglePreOwnedStepCommand { get; }
        public ICommand StepResourceUpCommand { get; }
        public ICommand StepResourceDownCommand { get; }
        public ICommand CopyLfgCommand { get; }

        public CraftingViewModel(
            Func<IReadOnlyList<CollectionItemModel>> getCatalog,
            Func<IReadOnlyList<VoidFissureItem>> getLiveFissures,
            Action<string> statusCallback,
            Action<CollectionItemModel>? onItemCraftedCallback = null)
        {
            _getCatalog = getCatalog;
            _getLiveFissures = getLiveFissures;
            _statusCallback = statusCallback;
            _onItemCraftedCallback = onItemCraftedCallback;

            AddPresetBlueprintCommand = new RelayCommand(p => { if (p is string name) AddPresetBlueprintToQueue(name); });
            SelectBlueprintForAddCommand = new RelayCommand(p =>
            {
                if (p is CollectionItemModel colItem)
                {
                    AddOrToggleCollectionItem(colItem, toggleIfPresent: false);
                    CraftBlueprintSearchText = string.Empty;
                    IsBlueprintSearchDropdownOpen = false;
                }
            });
            RemoveCraftQueueItemCommand = new RelayCommand(p => { if (p is CraftQueueItem q) RemoveCraftQueueItem(q); });
            IncrementQueueQtyCommand = new RelayCommand(p => { if (p is CraftQueueItem q) ChangeQueueQuantity(q, 1); });
            DecrementQueueQtyCommand = new RelayCommand(p => { if (p is CraftQueueItem q) ChangeQueueQuantity(q, -1); });
            MarkCraftedCommand = new RelayCommand(p => { if (p is CraftQueueItem q) MarkItemCrafted(q); });
            ClearCraftQueueCommand = new RelayCommand(ClearAllCraftQueue);
            ClearBlueprintSearchCommand = new RelayCommand(() =>
            {
                CraftBlueprintSearchText = string.Empty;
                IsBlueprintSearchDropdownOpen = false;
            });
            ToggleDeficitFilterCommand = new RelayCommand(() => OnlyShowDeficitResources = !OnlyShowDeficitResources);
            TogglePreOwnedStepCommand = new RelayCommand(p => { if (p is CraftStepInfo s) TogglePreOwnedStep(s); });
            StepResourceUpCommand = new RelayCommand(p => { if (p is CraftResource r) StepInventoryAmount(r, +1); });
            StepResourceDownCommand = new RelayCommand(p => { if (p is CraftResource r) StepInventoryAmount(r, -1); });
            CopyLfgCommand = new RelayCommand(p => { if (p is FarmNodeRecommendation rec) CopyLfgToClipboard(rec); });
        }

        public void Initialize()
        {
            var rawInv = StorageService.Instance.LoadResourceInventory();
            _playerResourceInventory = RecipeDatabaseService.MigrateInventoryKeys(rawInv);

            if (_playerResourceInventory.Count == 0)
            {
                _playerResourceInventory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Элемент питания Орокин"] = 6,
                    ["Теллуриум"] = 2,
                    ["Пластиды"] = 1800,
                    ["Полимеры"] = 3200,
                    ["Феррит"] = 25000,
                    ["Рубедо"] = 3500,
                    ["Морфиды"] = 12,
                    ["Нейроды"] = 4,
                    ["Нейронные датчики"] = 3,
                    ["Модуль контроля"] = 35,
                    ["Галлий"] = 8,
                    ["Наноспоры"] = 28000,
                    ["Кредиты"] = 220000,
                    ["Сплавы"] = 14000,
                    ["Схемы"] = 6500,
                    ["Вторсырьё"] = 9000,
                    ["Кристалл аргона"] = 0,
                    ["Форма"] = 1,
                    ["Оксиум"] = 350,
                    ["Образец Мутагена"] = 120
                };
            }
            StorageService.Instance.SaveResourceInventory(_playerResourceInventory);

            CraftQueue.Clear();
            var catalog = _getCatalog();
            var savedEntries = StorageService.Instance.LoadCraftQueueEntries();

            if (savedEntries.Count > 0)
            {
                foreach (var entry in savedEntries)
                {
                    var match = catalog.FirstOrDefault(i =>
                        (!string.IsNullOrEmpty(entry.UniqueName) && i.UniqueName.Equals(entry.UniqueName, StringComparison.OrdinalIgnoreCase)) ||
                        i.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase) ||
                        i.NameEn.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));

                    if (match != null)
                    {
                        match.IsInCraftQueue = true;
                        CraftQueue.Add(BuildQueueItem(match.Name, match.UniqueName, match.Category, ResolveItemIcon(match), entry.Quantity, null));
                    }
                    else
                    {
                        var extra = _recipeDb.SearchableExtras.FirstOrDefault(e =>
                            (!string.IsNullOrEmpty(entry.UniqueName) && e.UniqueName.Equals(entry.UniqueName, StringComparison.OrdinalIgnoreCase)) ||
                            e.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
                        string? thumb = extra != null ? ResolveItemIcon(extra) : _recipeDb.ResolveResourceIconUrl(entry.UniqueName, entry.Name);
                        CraftQueue.Add(BuildQueueItem(entry.Name, entry.UniqueName, extra?.Category ?? "Кузница", thumb, entry.Quantity, null));
                    }
                }
            }

            RecalculateCraftingAndFarming();

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                bool loaded = await _recipeDb.EnsureRelicDropsLoadedAsync().ConfigureAwait(false);
                if (!loaded && MarketRivenService.Instance.MarketItems.Count == 0) return;

                var disp = Application.Current?.Dispatcher;
                if (disp != null && !disp.HasShutdownStarted)
                    _ = disp.BeginInvoke(new Action(RefreshQueueHintsAndPrices));
            });
        }

        private void RefreshQueueHintsAndPrices()
        {
            foreach (var q in CraftQueue)
            {
                string key = string.IsNullOrEmpty(q.UniqueName) ? q.Name : q.UniqueName;
                if (q.IsPrime && _expandableUidsByQueueKey.TryGetValue(key, out var subUids))
                    q.RelicHintText = _recipeDb.BuildRelicHintForItem(q.UniqueName, q.BlueprintUniqueName, subUids, true, q.IsVaulted);

                if (string.IsNullOrEmpty(q.MarketPriceHint) && (q.IsPrime || q.Category is "Основное" or "Вторичное" or "Ближний бой" or "Варфреймы"))
                    _ = PopulateQueueItemPriceAsync(q);
            }
        }

        private async System.Threading.Tasks.Task PopulateQueueItemPriceAsync(CraftQueueItem q)
        {
            try
            {
                if (MarketRivenService.Instance.MarketItems.Count == 0) return;
                string searchBase = q.Name;
                int paren = searchBase.IndexOf(" (", StringComparison.Ordinal);
                if (paren > 0) searchBase = searchBase[..paren].Trim();

                var item = MarketRivenService.Instance.MarketItems.FirstOrDefault(m =>
                    m.NameRu.Equals(searchBase, StringComparison.OrdinalIgnoreCase) ||
                    m.NameRu.Equals(searchBase + " (Комплект)", StringComparison.OrdinalIgnoreCase) ||
                    m.NameEn.Equals(searchBase, StringComparison.OrdinalIgnoreCase) ||
                    m.NameEn.Equals(searchBase + " Set", StringComparison.OrdinalIgnoreCase));

                if (item == null) return;
                var analysis = await MarketRivenService.Instance.FetchLiveMarketItemAsync(item).ConfigureAwait(false);
                if (analysis == null || analysis.MinSellPrice <= 0) return;

                var disp = Application.Current?.Dispatcher;
                if (disp != null && !disp.HasShutdownStarted)
                {
                    _ = disp.BeginInvoke(new Action(() =>
                    {
                        q.MarketPriceHint = analysis.Avg48hPrice > 0
                            ? $"🪙 WFM: ~{analysis.MinSellPrice} пл. (ср. {analysis.Avg48hPrice:F0} пл.)"
                            : $"🪙 WFM: ~{analysis.MinSellPrice} пл.";
                    }));
                }
            }
            catch { }
        }

        private void TogglePreOwnedStep(CraftStepInfo step)
        {
            if (step == null || !step.CanTogglePreOwned) return;
            string key = RecipeDatabaseService.NormalizeResourceName(step.CanonicalStockKey);
            if (string.IsNullOrEmpty(key)) return;

            int cur = GetInventoryAmount(key);
            int next = cur >= step.Quantity ? 0 : (step.Quantity == 1 ? 1 : Math.Min(step.Quantity, cur + 1));
            _playerResourceInventory[key] = next;
            StorageService.Instance.SaveResourceInventory(_playerResourceInventory);
            RecalculateCraftingAndFarming();
            _statusCallback(next > 0
                ? $"✓ Деталь отмечена на складе: {key} ×{next} (ингредиенты исключены)"
                : $"↺ Сброшен запас готовой детали: {key}");
        }

        private string? ResolveItemIcon(CollectionItemModel item)
        {
            if (!string.IsNullOrEmpty(item.IconPath))
                return ImageCacheService.NormalizeImageUrl(item.IconPath);
            if (!string.IsNullOrEmpty(item.ThumbnailUrl))
                return item.ThumbnailUrl;
            return _recipeDb.ResolveResourceIconUrl(item.UniqueName, item.Name);
        }

        public void AddOrToggleCollectionItem(CollectionItemModel item, bool toggleIfPresent = true)
        {
            if (item == null) return;
            var existing = CraftQueue.FirstOrDefault(c =>
                (!string.IsNullOrEmpty(item.UniqueName) && c.UniqueName.Equals(item.UniqueName, StringComparison.OrdinalIgnoreCase)) ||
                c.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                if (toggleIfPresent)
                {
                    CraftQueue.Remove(existing);
                    item.IsInCraftQueue = false;
                    RecalculateCraftingAndFarming();
                    _statusCallback($"🛠 Удалено из очереди крафта: {item.Name}");
                }
                else
                {
                    ChangeQueueQuantity(existing, 1);
                }
                return;
            }

            item.IsInCraftQueue = true;
            var qItem = BuildQueueItem(item.Name, item.UniqueName, item.Category, ResolveItemIcon(item), 1, null);
            CraftQueue.Add(qItem);
            RecalculateCraftingAndFarming();
            _statusCallback($"🛠 Добавлено в очередь: {item.Name}");
        }

        public void AddPresetBlueprintToQueue(string presetName, bool saveImmediately = true)
        {
            var existing = CraftQueue.FirstOrDefault(c => c.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                ChangeQueueQuantity(existing, 1);
                return;
            }

            var catalog = _getCatalog();
            var colMatch = catalog.FirstOrDefault(c =>
                c.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase) ||
                c.NameEn.Equals(presetName, StringComparison.OrdinalIgnoreCase));
            var extraMatch = colMatch == null
                ? _recipeDb.SearchableExtras.FirstOrDefault(e => e.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase))
                : null;

            string cat = colMatch?.Category ?? extraMatch?.Category ?? "Улучшения";
            string uid = colMatch?.UniqueName ?? extraMatch?.UniqueName ?? RecipeDatabaseService.GetPresetLookupKey(presetName);
            string? thumb = colMatch != null ? ResolveItemIcon(colMatch) : (extraMatch != null ? ResolveItemIcon(extraMatch) : _recipeDb.ResolveResourceIconUrl(uid, presetName));
            if (colMatch != null) colMatch.IsInCraftQueue = true;

            CraftQueue.Add(BuildQueueItem(presetName, uid, cat, thumb, 1, null));
            if (saveImmediately)
            {
                RecalculateCraftingAndFarming();
                _statusCallback($"🛠 Добавлен чертёж: {presetName}");
            }
        }

        private CraftQueueItem BuildQueueItem(
            string name,
            string? uniqueName,
            string category,
            string? thumbUrl,
            int quantity,
            Dictionary<string, int>? remainingStock,
            HashSet<string>? utilityAccumulator = null)
        {
            var resolved = _recipeDb.ResolveRecipe(name, uniqueName, category, quantity, remainingStock);
            if (utilityAccumulator != null)
            {
                foreach (string u in resolved.OptionalUtilityNames)
                    utilityAccumulator.Add(u);
            }

            string resultUid = !string.IsNullOrEmpty(resolved.ResultUniqueName) ? resolved.ResultUniqueName : (uniqueName ?? string.Empty);
            var colMatch = _getCatalog().FirstOrDefault(c =>
                (!string.IsNullOrEmpty(resultUid) && c.UniqueName.Equals(resultUid, StringComparison.OrdinalIgnoreCase)) ||
                c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                c.NameEn.Equals(name, StringComparison.OrdinalIgnoreCase));

            bool isPrime = colMatch?.IsPrime ?? name.Contains("Prime", StringComparison.OrdinalIgnoreCase) || name.Contains("Прайм", StringComparison.OrdinalIgnoreCase);
            bool isVaulted = colMatch?.IsVaulted ?? false;

            string mapKey = string.IsNullOrEmpty(resultUid) ? name : resultUid;
            _expandableUidsByQueueKey[mapKey] = resolved.ExpandableIngredientUids;

            string relicHint = _recipeDb.BuildRelicHintForItem(
                resultUid, resolved.BlueprintUniqueName, resolved.ExpandableIngredientUids, isPrime, isVaulted);

            var item = new CraftQueueItem
            {
                UniqueName = resultUid,
                BlueprintUniqueName = resolved.BlueprintUniqueName,
                Name = name,
                Category = category,
                Quantity = quantity,
                CraftTimeHours = resolved.CraftHours,
                TotalCreditsCost = resolved.TotalCreditsCost,
                IconGlyph = resolved.IconGlyph,
                ThumbnailUrl = thumbUrl ?? _recipeDb.ResolveResourceIconUrl(resolved.ResultUniqueName, name),
                HasRealRecipe = resolved.HasRealRecipe,
                IsPrime = isPrime,
                IsVaulted = isVaulted,
                RelicHintText = relicHint,
                IntermediateSteps = resolved.Steps,
                Resources = resolved.RawResources
            };
            return item;
        }

        private void ChangeQueueQuantity(CraftQueueItem qItem, int delta)
        {
            int next = Math.Clamp(qItem.Quantity + delta, 1, 99);
            if (next == qItem.Quantity) return;

            int idx = CraftQueue.IndexOf(qItem);
            if (idx < 0) return;

            CraftQueue[idx] = BuildQueueItem(qItem.Name, qItem.UniqueName, qItem.Category, qItem.ThumbnailUrl, next, null);
            RecalculateCraftingAndFarming();
            _statusCallback($"🛠 Количество обновлено: {qItem.Name} ×{next}");
        }

        private void RemoveCraftQueueItem(CraftQueueItem queueItem)
        {
            if (queueItem == null) return;
            CraftQueue.Remove(queueItem);

            var colItem = _getCatalog().FirstOrDefault(c =>
                (!string.IsNullOrEmpty(queueItem.UniqueName) && c.UniqueName.Equals(queueItem.UniqueName, StringComparison.OrdinalIgnoreCase)) ||
                c.Name.Equals(queueItem.Name, StringComparison.OrdinalIgnoreCase));
            if (colItem != null) colItem.IsInCraftQueue = false;

            RecalculateCraftingAndFarming();
            _statusCallback($"🛠 Удалено из очереди: {queueItem.Name}");
        }

        private void MarkItemCrafted(CraftQueueItem queueItem)
        {
            if (queueItem == null) return;

            foreach (var res in queueItem.Resources)
            {
                int current = GetInventoryAmount(res.Name);
                _playerResourceInventory[res.Name] = Math.Max(0, current - res.RequiredAmount);
            }
            if (queueItem.TotalCreditsCost > 0)
            {
                int curCredits = GetInventoryAmount("Кредиты");
                _playerResourceInventory["Кредиты"] = Math.Max(0, curCredits - queueItem.TotalCreditsCost);
            }
            StorageService.Instance.SaveResourceInventory(_playerResourceInventory);

            var colItem = _getCatalog().FirstOrDefault(c =>
                (!string.IsNullOrEmpty(queueItem.UniqueName) && c.UniqueName.Equals(queueItem.UniqueName, StringComparison.OrdinalIgnoreCase)) ||
                c.Name.Equals(queueItem.Name, StringComparison.OrdinalIgnoreCase));
            if (colItem != null)
            {
                if (_onItemCraftedCallback != null)
                    _onItemCraftedCallback(colItem);
                else
                {
                    colItem.IsOwned = true;
                    colItem.IsInCraftQueue = false;
                }
            }

            CraftQueue.Remove(queueItem);
            RecalculateCraftingAndFarming();
            _statusCallback($"✓ Скрафчено: {queueItem.Name} ×{queueItem.Quantity} — ресурсы списаны, предмет отмечен в коллекции!");
        }

        private void ClearAllCraftQueue()
        {
            CraftQueue.Clear();
            foreach (var item in _getCatalog())
                item.IsInCraftQueue = false;
            RecalculateCraftingAndFarming();
            _statusCallback("🗑 Очередь крафта очищена");
        }

        private void FilterAvailableBlueprints()
        {
            AvailableBlueprintsForAdd.Clear();
            if (string.IsNullOrWhiteSpace(CraftBlueprintSearchText))
            {
                IsBlueprintSearchDropdownOpen = false;
                return;
            }

            string q = CraftBlueprintSearchText.Trim();
            var catalog = _getCatalog().Where(i => i.CanBeCrafted);
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var combined = new List<CollectionItemModel>();

            foreach (var item in catalog.Concat(_recipeDb.SearchableExtras))
            {
                if (!seenNames.Add(item.UniqueName + "|" + item.Name)) continue;
                if (item.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    item.NameEn.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    item.Category.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    combined.Add(item);
                }
            }

            var ordered = combined
                .OrderByDescending(i => i.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) || i.NameEn.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                .ThenBy(i => i.Name.Length)
                .Take(28);

            foreach (var m in ordered)
                AvailableBlueprintsForAdd.Add(m);

            IsBlueprintSearchDropdownOpen = AvailableBlueprintsForAdd.Count > 0;
        }

        public void SetInventoryAmount(string resourceName, int newAmount)
        {
            string norm = RecipeDatabaseService.NormalizeResourceName(resourceName);
            if (string.IsNullOrEmpty(norm)) return;

            int clamped = Math.Max(0, newAmount);
            if (GetInventoryAmount(norm) == clamped) return;

            _playerResourceInventory[norm] = clamped;
            StorageService.Instance.SaveResourceInventory(_playerResourceInventory);

            bool affectsExpansion =
                _activeOptionalUtilities.Contains(norm) ||
                _recipeDb.IsTrackedUtilityResource(norm) ||
                CraftQueue.Any(q => q.IntermediateSteps.Any(s =>
                    !s.IsRoot && s.CanonicalStockKey.Equals(norm, StringComparison.OrdinalIgnoreCase)));

            if (affectsExpansion)
            {
                RecalculateCraftingAndFarming(keepVisibleResourceName: norm);
            }
            else
            {
                SyncExistingResourceAmounts(keepVisibleResourceName: norm);
                RecalculateFarmingOnly();
                NotifySummaryProperties();
            }
        }

        private void StepInventoryAmount(CraftResource res, int sign)
        {
            int step = (res.Name.Equals("Кредиты", StringComparison.OrdinalIgnoreCase)) ? 10000
                : (res.RequiredAmount >= 200 ? 100 : 1);
            int next = Math.Max(0, GetInventoryAmount(res.Name) + (sign * step));
            SetInventoryAmount(res.Name, next);
        }

        public int GetInventoryAmount(string resourceName)
        {
            string norm = RecipeDatabaseService.NormalizeResourceName(resourceName);
            return _playerResourceInventory.GetValueOrDefault(norm, 0);
        }

        public void RefreshLiveFissureSynergy()
        {
            RecalculateFarmingOnly();
        }

        public IEnumerable<string> GetQueuedNames() => CraftQueue.Select(c => c.Name);

        public void RecalculateCraftingAndFarming(string? keepVisibleResourceName = null)
        {
            // Stock-aware re-resolution: owned Formas / Dojo items / pre-crafted parts automatically skip sub-materials
            var remainingStock = new Dictionary<string, int>(_playerResourceInventory, StringComparer.OrdinalIgnoreCase);
            var utilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < CraftQueue.Count; i++)
            {
                var old = CraftQueue[i];
                var rebuilt = BuildQueueItem(old.Name, old.UniqueName, old.Category, old.ThumbnailUrl, old.Quantity, remainingStock, utilities);
                rebuilt.MarketPriceHint = old.MarketPriceHint;
                CraftQueue[i] = rebuilt;
            }
            _activeOptionalUtilities = utilities;

            // 1. Aggregate all required resources across queue
            var totals = new Dictionary<string, (string UniqueName, int Req, string Icon, string? IconUrl, string Hint, bool IsOptUtil)>(StringComparer.OrdinalIgnoreCase);
            int totalCredits = 0;
            foreach (var item in CraftQueue)
            {
                totalCredits += item.TotalCreditsCost;
                foreach (var res in item.Resources)
                {
                    if (!totals.TryGetValue(res.Name, out var ex))
                        totals[res.Name] = (res.UniqueName, res.RequiredAmount, res.IconText, res.IconUrl, res.SourceHint, false);
                    else
                        totals[res.Name] = (ex.UniqueName, ex.Req + res.RequiredAmount, ex.Icon, ex.IconUrl ?? res.IconUrl, ex.Hint, ex.IsOptUtil);
                }
            }

            // Ensure editable utility rows (Forma / Fieldron / Detonite / Mutagen Mass) appear in warehouse without double deficit
            foreach (string utilName in _activeOptionalUtilities)
            {
                if (!totals.ContainsKey(utilName))
                {
                    totals[utilName] = (utilName, 0, RecipeDatabaseService.GetResourceIcon(utilName),
                        _recipeDb.ResolveResourceIconUrl(null, utilName),
                        "Готовый предмет: укажите запас, чтобы исключить разбор на компоненты", true);
                }
            }

            if (totalCredits > 0)
            {
                totals["Кредиты"] = ("CREDITS", totalCredits, "💳",
                    _recipeDb.ResolveResourceIconUrl("CREDITS", "Кредиты"),
                    "Нептун: Индекс / Бонусы миссий", false);
            }

            // 2. Update AggregatedResources in place (never Clear() unconditionally so TextBox focus survives live edits)
            for (int i = AggregatedResources.Count - 1; i >= 0; i--)
            {
                if (!totals.ContainsKey(AggregatedResources[i].Name))
                    AggregatedResources.RemoveAt(i);
            }

            var existingByName = AggregatedResources.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in totals.OrderByDescending(t => Math.Max(0, t.Value.Req - GetInventoryAmount(t.Key)) > 0 ? 1 : 0)
                                       .ThenByDescending(t => Math.Max(0, t.Value.Req - GetInventoryAmount(t.Key))))
            {
                int owned = GetInventoryAmount(kvp.Key);
                if (existingByName.TryGetValue(kvp.Key, out var existingRow))
                {
                    existingRow.IsOptionalUtility = kvp.Value.IsOptUtil;
                    existingRow.RequiredAmount = kvp.Value.Req;
                    existingRow.SourceHint = kvp.Value.Hint;
                    existingRow.SetAmountSilently(owned);
                }
                else
                {
                    var agg = new CraftResource
                    {
                        UniqueName = kvp.Value.UniqueName,
                        Name = kvp.Key,
                        RequiredAmount = kvp.Value.Req,
                        IconText = kvp.Value.Icon,
                        IconUrl = kvp.Value.IconUrl ?? _recipeDb.ResolveResourceIconUrl(kvp.Value.UniqueName, kvp.Key),
                        SourceHint = kvp.Value.Hint,
                        IsOptionalUtility = kvp.Value.IsOptUtil,
                        OnAmountEditedCallback = SetInventoryAmount
                    };
                    agg.SetAmountSilently(owned);
                    AggregatedResources.Add(agg);
                }
            }

            // Sync inner resource rows of each queue card with real current stock
            foreach (var q in CraftQueue)
            {
                foreach (var r in q.Resources)
                    r.SetAmountSilently(GetInventoryAmount(r.Name));
                q.NotifyResourcesUpdated();
            }

            UpdateDisplayAggregatedResources(keepVisibleResourceName);
            RecalculateFarmingOnly();
            RefreshQueueHintsAndPrices();

            StorageService.Instance.SaveCraftQueueEntries(CraftQueue.Select(c => new CraftQueueEntry
            {
                Name = c.Name,
                UniqueName = c.UniqueName,
                Quantity = c.Quantity
            }));

            NotifySummaryProperties();
        }

        private void SyncExistingResourceAmounts(string? keepVisibleResourceName = null)
        {
            foreach (var agg in AggregatedResources)
                agg.SetAmountSilently(GetInventoryAmount(agg.Name));
            foreach (var q in CraftQueue)
            {
                foreach (var r in q.Resources)
                    r.SetAmountSilently(GetInventoryAmount(r.Name));
                q.NotifyResourcesUpdated();
            }
            if (OnlyShowDeficitResources)
                UpdateDisplayAggregatedResources(keepVisibleResourceName);
        }

        private void UpdateDisplayAggregatedResources(string? keepVisibleResourceName = null)
        {
            var desired = (OnlyShowDeficitResources
                ? AggregatedResources.Where(r =>
                    r.DeficitAmount > 0 ||
                    (!string.IsNullOrEmpty(keepVisibleResourceName) && r.Name.Equals(keepVisibleResourceName, StringComparison.OrdinalIgnoreCase)))
                : AggregatedResources.AsEnumerable()).ToList();

            var desiredSet = new HashSet<CraftResource>(desired);
            for (int i = DisplayAggregatedResources.Count - 1; i >= 0; i--)
            {
                if (!desiredSet.Contains(DisplayAggregatedResources[i]))
                    DisplayAggregatedResources.RemoveAt(i);
            }

            foreach (var item in desired)
            {
                if (!DisplayAggregatedResources.Contains(item))
                    DisplayAggregatedResources.Add(item);
            }
        }

        private void RecalculateFarmingOnly()
        {
            var deficits = AggregatedResources
                .Where(r => r.DeficitAmount > 0)
                .ToDictionary(r => r.Name, r => r.DeficitAmount, StringComparer.OrdinalIgnoreCase);

            var liveFissures = _getLiveFissures();
            var (route, ranked) = CraftQueue.Count > 0 && deficits.Count > 0
                ? _recipeDb.BuildOptimalFarmingPlan(deficits, liveFissures, _prioritizeSteelPath)
                : (new List<FarmStrategyStep>(), new List<FarmNodeRecommendation>());

            OptimalRouteSteps.Clear();
            foreach (var step in route) OptimalRouteSteps.Add(step);

            FarmRecommendations.Clear();
            foreach (var rec in ranked) FarmRecommendations.Add(rec);

            OnPropertyChanged(nameof(MatchedNodesCount));
            OnPropertyChanged(nameof(MatchedNodesSummaryText));
            OnPropertyChanged(nameof(HasOptimalRoute));
            OnPropertyChanged(nameof(HasNoRecommendations));
            OnPropertyChanged(nameof(OptimalRouteSummaryText));
            OnPropertyChanged(nameof(StrategyQueueContextText));
            OnPropertyChanged(nameof(EmptyStrategyHintText));
        }

        private void NotifySummaryProperties()
        {
            OnPropertyChanged(nameof(TotalQueueItemsCount));
            OnPropertyChanged(nameof(IsQueueEmpty));
            OnPropertyChanged(nameof(TotalQueueUnitsCount));
            OnPropertyChanged(nameof(TotalCreditsNeeded));
            OnPropertyChanged(nameof(TotalCreditsDisplay));
            OnPropertyChanged(nameof(TotalDeficitResourcesCount));
            OnPropertyChanged(nameof(HasDeficitResources));
            OnPropertyChanged(nameof(DeficitSummaryText));
            OnPropertyChanged(nameof(MatchedNodesCount));
            OnPropertyChanged(nameof(MatchedNodesSummaryText));
            OnPropertyChanged(nameof(HasOptimalRoute));
            OnPropertyChanged(nameof(HasNoRecommendations));
            OnPropertyChanged(nameof(OptimalRouteSummaryText));
            OnPropertyChanged(nameof(StrategyQueueContextText));
            OnPropertyChanged(nameof(EmptyStrategyHintText));
        }

        private void CopyLfgToClipboard(FarmNodeRecommendation rec)
        {
            if (rec == null) return;
            try
            {
                Clipboard.SetDataObject(rec.LfgCopyText, true);
                _statusCallback($"📋 Скопировано в буфер: \"{rec.LfgCopyText}\"");
            }
            catch
            {
                _statusCallback("⚠️ Буфер обмена временно недоступен");
            }
        }
    }
}
