using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using WarfAssistant.Models;

namespace WarfAssistant.Services
{
    public sealed class ResolvedCraftRecipe
    {
        public bool HasRealRecipe { get; set; }
        public string ResultUniqueName { get; set; } = string.Empty;
        public string BlueprintUniqueName { get; set; } = string.Empty;
        public int CraftHours { get; set; }
        public int TotalCreditsCost { get; set; }
        public string IconGlyph { get; set; } = "🔨";
        public List<CraftStepInfo> Steps { get; set; } = new();
        public List<CraftResource> RawResources { get; set; } = new();
        public HashSet<string> OptionalUtilityNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> ExpandableIngredientUids { get; } = new();
    }

    public sealed class MetaFarmNode
    {
        public string NodeName { get; set; } = string.Empty;
        public string Planet { get; set; } = string.Empty;
        public string MissionType { get; set; } = string.Empty;
        public string EnemyFaction { get; set; } = string.Empty;
        public string[] DropResources { get; set; } = Array.Empty<string>();
        public double Rating { get; set; } = 4.8;
        public string[] Tags { get; set; } = Array.Empty<string>();
        public string RecommendedSetup { get; set; } = string.Empty;
        public string FarmingTip { get; set; } = string.Empty;
        public bool SteelPath { get; set; } = true;
    }

    /// <summary>
    /// Offline-first recipe graph &amp; optimal farming route database backed by compressed
    /// official DE + Dojo recipes (recipes_seed.json.gz) and weighted Set Cover optimization.
    /// </summary>
    public sealed class RecipeDatabaseService
    {
        private static RecipeDatabaseService? _instance;
        public static RecipeDatabaseService Instance => _instance ??= new RecipeDatabaseService();

        private sealed class SeedDoc
        {
            [JsonPropertyName("version")] public string? Version { get; set; }
            [JsonPropertyName("recipes")] public List<RecipeDto>? Recipes { get; set; }
            [JsonPropertyName("canonicalByResult")] public Dictionary<string, string>? CanonicalByResult { get; set; }
            [JsonPropertyName("names")] public Dictionary<string, string>? Names { get; set; }
            [JsonPropertyName("icons")] public Dictionary<string, string>? Icons { get; set; }
            [JsonPropertyName("craftableExtras")] public List<CraftableExtraDto>? CraftableExtras { get; set; }
        }

        private sealed class CraftableExtraDto
        {
            [JsonPropertyName("u")] public string U { get; set; } = string.Empty;
            [JsonPropertyName("n")] public string N { get; set; } = string.Empty;
            [JsonPropertyName("c")] public string C { get; set; } = "Кузница";
            [JsonPropertyName("ic")] public string? Ic { get; set; }
        }

        private sealed class RecipeDto
        {
            [JsonPropertyName("b")] public string B { get; set; } = string.Empty;
            [JsonPropertyName("r")] public string R { get; set; } = string.Empty;
            [JsonPropertyName("c")] public int C { get; set; }
            [JsonPropertyName("t")] public int T { get; set; }
            [JsonPropertyName("n")] public int N { get; set; } = 1;
            [JsonPropertyName("i")] public List<IngredientDto>? I { get; set; }
        }

        private sealed class IngredientDto
        {
            [JsonPropertyName("u")] public string U { get; set; } = string.Empty;
            [JsonPropertyName("q")] public int Q { get; set; }
        }

        // Old App/userdata resource aliases -> canonical DE Russian resource names
        private static readonly Dictionary<string, string> ResourceAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Орокинские Ячейки"] = "Элемент питания Орокин",
            ["Ячейки Орокин"] = "Элемент питания Орокин",
            ["Аргоновые Кристаллы"] = "Кристалл аргона",
            ["Кристаллы Аргона"] = "Кристалл аргона",
            ["Модули Контроля"] = "Модуль контроля",
            ["Вторсырье"] = "Вторсырьё",
            ["Теллур"] = "Теллуриум",
            ["Мутагенная Выборка"] = "Образец Мутагена",
            ["Нейронные Датчики"] = "Нейронные датчики",
            ["Плазма Луа"] = "Траксовая плазма Луа",
            ["Плазма Тракс"] = "Траксовая плазма"
        };

        // Preset display names -> exact blueprint or resultType UniqueName
        private static readonly Dictionary<string, (string LookupId, bool IsBlueprint)> PresetMappings = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Форма"] = ("/Lotus/Types/Recipes/Components/FormaBlueprint", true),
            ["Форма: Умбра"] = ("/Lotus/Types/Recipes/Components/UmbraFormaBlueprint", true),
            ["Форма Умбра"] = ("/Lotus/Types/Recipes/Components/UmbraFormaBlueprint", true),
            ["Форма: Омни"] = ("/Lotus/Types/Recipes/Components/FormaAuraBlueprint", true),
            ["Катализатор Орокин"] = ("/Lotus/Types/Recipes/Components/OrokinCatalystBlueprint", true),
            ["Реактор Орокин"] = ("/Lotus/Types/Recipes/Components/OrokinReactorBlueprint", true),
            ["Эксилус Адаптер"] = ("/Lotus/Types/Recipes/Components/UtilityUnlockerBlueprint", true),
            ["Эксилус-адаптер для варфрейма"] = ("/Lotus/Types/Recipes/Components/UtilityUnlockerBlueprint", true),
            ["Эксилус для оружия"] = ("/Lotus/Types/Recipes/Components/WeaponUtilityUnlockerBlueprint", true),
            ["Восстановитель Энергии (x100)"] = ("/Lotus/Weapons/ClanTech/Energy/LargeHundredTeamEnergyBlueprint", true),
            ["Дешифратор (x100)"] = ("/Lotus/Types/StoreItems/Consumables/HundredCipherBlueprint", true),
            ["Мутагенная Масса"] = ("/Lotus/Types/Items/Research/BioComponentBlueprint", true),
            ["Филдрон"] = ("/Lotus/Types/Items/Research/EnergyComponentBlueprint", true),
            ["Инжектор Детонита"] = ("/Lotus/Types/Items/Research/ChemComponentBlueprint", true)
        };

        // Utility items that shouldn't be auto-expanded when nested unless user wants raw base expansion
        private static readonly HashSet<string> UtilityConsumableResults = new(StringComparer.OrdinalIgnoreCase)
        {
            "/Lotus/Types/Items/MiscItems/Forma",
            "/Lotus/Types/Items/MiscItems/FormaAura",
            "/Lotus/Types/Items/MiscItems/FormaStance",
            "/Lotus/Types/Items/MiscItems/FormaUmbra",
            "/Lotus/Types/Items/Research/BioComponent",
            "/Lotus/Types/Items/Research/ChemComponent",
            "/Lotus/Types/Items/Research/EnergyComponent",
            "/Lotus/Types/Items/MiscItems/OrokinCatalyst",
            "/Lotus/Types/Items/MiscItems/OrokinReactor"
        };

        private static readonly HashSet<string> RareResourceSet = new(StringComparer.OrdinalIgnoreCase)
        {
            "Элемент питания Орокин", "Теллуриум", "Кристалл аргона", "Нейроды",
            "Нейронные датчики", "Оксиум", "Экстракт Нитаина", "Гексенон",
            "Струбцина пафоса", "Лампа Энтрати", "Стелла", "Соматические волокна"
        };

        private static readonly Dictionary<string, string> ResourceSourceHints = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Экстракт Нитаина"] = "Ночная Волна (15 Доверия = 5 шт.)",
            ["Кристалл аргона"] = "Бездна (распадается через 24ч)",
            ["Элемент питания Орокин"] = "Церера (Gabii) / Сатурн (Helene)",
            ["Теллуриум"] = "Уран (Ophelia) / Арчвинг",
            ["Нейроды"] = "Земля / Деймос / Луа / Эрида",
            ["Нейронные датчики"] = "Юпитер (Io / Elara / Themisto)",
            ["Оксиум"] = "Юпитер (Io — Дроны Оксиума)",
            ["Криотик"] = "Плутон (Hieracon) / Земля (Everest)",
            ["Гексенон"] = "Юпитер (Elara / Газовый город)",
            ["Мутагенная Масса"] = "Вторжения (х1-3) или Додзё",
            ["Филдрон"] = "Вторжения (х1-3) или Додзё",
            ["Инжектор Детонита"] = "Вторжения (х1-3) или Додзё",
            ["Струбцина пафоса"] = "Дувири (бой с Оровирмом)",
            ["Лампа Энтрати"] = "Зариман / Деймос (Альбрехт)",
            ["Стелла"] = "Деймос: Санктум Анатомика",
            ["Траксовая плазма"] = "Зариман / Луа (Сопряжение)",
            ["Форма"] = "Реликвии Бездны (Кузница 23ч)",
            ["Кува"] = "Крепость Кувы / Зариман / Арбитраж"
        };

        private readonly Dictionary<string, RecipeDto> _byBlueprint = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RecipeDto> _byResult = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _resultByDisplayName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _iconsByUnique = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _iconsByName = new(StringComparer.OrdinalIgnoreCase);
        public List<CollectionItemModel> SearchableExtras { get; } = new();
        private bool _loaded;

        private RecipeDatabaseService()
        {
            LoadSeed();
        }

        public static string NormalizeResourceName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;
            string trimmed = rawName.Trim();
            return ResourceAliases.TryGetValue(trimmed, out var canonical) ? canonical : trimmed;
        }

        public bool IsTrackedUtilityResource(string displayName)
        {
            string norm = NormalizeResourceName(displayName);
            return UtilityConsumableResults.Any(uid =>
                NormalizeResourceName(_names.GetValueOrDefault(uid, TailName(uid)))
                    .Equals(norm, StringComparison.OrdinalIgnoreCase));
        }

        public string? ResolveResourceIconUrl(string? uniqueName, string? displayName)
        {
            if (!string.IsNullOrWhiteSpace(uniqueName) && _iconsByUnique.TryGetValue(uniqueName, out var p1))
                return ImageCacheService.NormalizeImageUrl(p1);
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                string norm = NormalizeResourceName(displayName);
                if (_iconsByName.TryGetValue(norm, out var p2))
                    return ImageCacheService.NormalizeImageUrl(p2);
            }
            return null;
        }

        public static Dictionary<string, int> MigrateInventoryKeys(Dictionary<string, int> input)
        {
            var normalized = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in input)
            {
                string key = NormalizeResourceName(kvp.Key);
                if (string.IsNullOrEmpty(key)) continue;
                normalized[key] = normalized.TryGetValue(key, out int prev)
                    ? Math.Max(prev, kvp.Value)
                    : Math.Max(0, kvp.Value);
            }
            return normalized;
        }

        private void LoadSeed()
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string? resName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("recipes_seed.json.gz", StringComparison.OrdinalIgnoreCase));
                if (resName == null) return;

                using Stream? rawStream = asm.GetManifestResourceStream(resName);
                if (rawStream == null) return;

                using var gz = new GZipStream(rawStream, CompressionMode.Decompress);
                var doc = JsonSerializer.Deserialize<SeedDoc>(gz);
                if (doc == null) return;

                if (doc.Names != null)
                {
                    foreach (var kvp in doc.Names)
                        _names[kvp.Key] = kvp.Value;
                }

                if (doc.Icons != null)
                {
                    foreach (var kvp in doc.Icons)
                    {
                        if (string.IsNullOrEmpty(kvp.Value)) continue;
                        _iconsByUnique[kvp.Key] = kvp.Value;
                        if (kvp.Key == "CREDITS")
                            _iconsByName["Кредиты"] = kvp.Value;
                        else if (_names.TryGetValue(kvp.Key, out var n) && !string.IsNullOrWhiteSpace(n))
                            _iconsByName.TryAdd(NormalizeResourceName(n), kvp.Value);
                    }
                }

                if (doc.Recipes != null)
                {
                    foreach (var r in doc.Recipes)
                        _byBlueprint[r.B] = r;
                }

                if (doc.CanonicalByResult != null)
                {
                    foreach (var kvp in doc.CanonicalByResult)
                    {
                        if (_byBlueprint.TryGetValue(kvp.Value, out var rec))
                        {
                            _byResult[kvp.Key] = rec;
                            if (_names.TryGetValue(kvp.Key, out var dispName) && !string.IsNullOrWhiteSpace(dispName))
                                _resultByDisplayName.TryAdd(dispName, kvp.Key);
                        }
                    }
                }

                if (doc.CraftableExtras != null)
                {
                    foreach (var ex in doc.CraftableExtras)
                    {
                        if (string.IsNullOrWhiteSpace(ex.N)) continue;
                        if (!string.IsNullOrEmpty(ex.Ic))
                        {
                            _iconsByUnique.TryAdd(ex.U, ex.Ic);
                            _iconsByName.TryAdd(NormalizeResourceName(ex.N), ex.Ic);
                        }
                        SearchableExtras.Add(new CollectionItemModel
                        {
                            UniqueName = ex.U,
                            Name = ex.N,
                            Category = ex.C,
                            SubCategory = ex.C,
                            IconPath = ex.Ic,
                            ThumbnailUrl = ImageCacheService.NormalizeImageUrl(ex.Ic),
                            ImageCacheKey = ImageCacheService.ComputeCacheKey(ex.Ic, null, null),
                            IconGlyph = ex.N.Contains("Форма", StringComparison.OrdinalIgnoreCase) ? "💠"
                                : (ex.N.Contains("Линз", StringComparison.OrdinalIgnoreCase) ? "🔭" : "🔨"),
                            CanBeCrafted = true,
                            IsMasterable = false
                        });
                    }
                }
            }
            catch
            {
                // Keep empty dictionaries gracefully if resource load fails
            }
        }

        public static string GetPresetLookupKey(string presetName) =>
            PresetMappings.TryGetValue(presetName, out var m) ? m.LookupId : string.Empty;

        public ResolvedCraftRecipe ResolveRecipe(
            string itemName,
            string? uniqueName,
            string category,
            int quantity = 1,
            Dictionary<string, int>? remainingWarehouseStock = null)
        {
            int mult = Math.Clamp(quantity, 1, 99);
            var resolved = new ResolvedCraftRecipe
            {
                IconGlyph = GetGlyphForCategory(category)
            };

            RecipeDto? root = FindRootRecipe(itemName, uniqueName);
            if (root != null)
            {
                resolved.HasRealRecipe = true;
                resolved.ResultUniqueName = root.R;
                resolved.BlueprintUniqueName = root.B;
                resolved.CraftHours = Math.Max(0, (int)Math.Round(root.T / 3600.0));

                var rawTotals = new Dictionary<string, (string UniqueName, int Amount)>(StringComparer.OrdinalIgnoreCase);
                var stepList = new List<CraftStepInfo>();
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                ExpandRecursive(
                    root, mult, true, remainingWarehouseStock, visited,
                    rawTotals, resolved.OptionalUtilityNames, resolved.ExpandableIngredientUids,
                    stepList, out int totalCredits);

                resolved.TotalCreditsCost = totalCredits;
                resolved.Steps = MergeIdenticalSteps(stepList);
                resolved.RawResources = rawTotals.Values
                    .OrderByDescending(v => RareResourceSet.Contains(NormalizeResourceName(_names.GetValueOrDefault(v.UniqueName, v.UniqueName))) ? 1 : 0)
                    .ThenByDescending(v => v.Amount)
                    .Select(v =>
                    {
                        string display = NormalizeResourceName(_names.GetValueOrDefault(v.UniqueName, TailName(v.UniqueName)));
                        return new CraftResource
                        {
                            UniqueName = v.UniqueName,
                            Name = display,
                            RequiredAmount = v.Amount,
                            IconText = GetResourceIcon(display),
                            IconUrl = ResolveResourceIconUrl(v.UniqueName, display),
                            SourceHint = ResourceSourceHints.GetValueOrDefault(display, string.Empty)
                        };
                    })
                    .ToList();
                return resolved;
            }

            // Fallback when item is quest-only / store-credits-only / missing DE recipe
            resolved.HasRealRecipe = false;
            resolved.CraftHours = category.Contains("Варфрейм") ? 72 : 12;
            resolved.RawResources = new List<CraftResource>();
            return resolved;
        }

        private RecipeDto? FindRootRecipe(string itemName, string? uniqueName)
        {
            if (PresetMappings.TryGetValue(itemName, out var preset))
            {
                if (preset.IsBlueprint && _byBlueprint.TryGetValue(preset.LookupId, out var bpRec))
                    return bpRec;
                if (!preset.IsBlueprint && _byResult.TryGetValue(preset.LookupId, out var rtRec))
                    return rtRec;
            }

            if (!string.IsNullOrWhiteSpace(uniqueName))
            {
                if (_byResult.TryGetValue(uniqueName, out var exact))
                    return exact;
                if (_byBlueprint.TryGetValue(uniqueName, out var exactBp))
                    return exactBp;
            }

            string clean = StripEnglishSuffix(itemName);
            if (_resultByDisplayName.TryGetValue(itemName, out var resId) && _byResult.TryGetValue(resId, out var r1))
                return r1;
            if (_resultByDisplayName.TryGetValue(clean, out var resId2) && _byResult.TryGetValue(resId2, out var r2))
                return r2;

            return null;
        }

        private void ExpandRecursive(
            RecipeDto recipe,
            int count,
            bool isRoot,
            Dictionary<string, int>? remainingWarehouseStock,
            HashSet<string> stack,
            Dictionary<string, (string UniqueName, int Amount)> rawTotals,
            HashSet<string> optionalUtilities,
            List<string> expandableUids,
            List<CraftStepInfo> steps,
            out int totalCredits)
        {
            totalCredits = recipe.C * count;
            string canonicalName = NormalizeResourceName(_names.GetValueOrDefault(recipe.R, TailName(recipe.R)));
            string dispName = recipe.N > 1 ? $"{canonicalName} (x{recipe.N})" : canonicalName;
            int yieldedUnits = count * Math.Max(1, recipe.N);

            var currentStep = new CraftStepInfo
            {
                Name = dispName,
                CanonicalStockKey = isRoot ? string.Empty : canonicalName,
                Quantity = yieldedUnits,
                BatchYield = Math.Max(1, recipe.N),
                CraftHours = Math.Max(0, (int)Math.Round(recipe.T / 3600.0)),
                CreditsCost = recipe.C * count,
                IsRoot = isRoot
            };
            steps.Add(currentStep);

            if (recipe.I == null || recipe.I.Count == 0 || stack.Count >= 6)
                return;

            stack.Add(recipe.R);
            foreach (var ing in recipe.I)
            {
                int needed = ing.Q * count;
                if (needed <= 0) continue;

                expandableUids.Add(ing.U);

                bool hasSub = _byResult.TryGetValue(ing.U, out var subRec);
                bool canRecurse = hasSub &&
                                  subRec != null &&
                                  !string.Equals(ing.U, recipe.R, StringComparison.OrdinalIgnoreCase) &&
                                  !stack.Contains(ing.U);

                if (canRecurse && subRec != null && UtilityConsumableResults.Contains(ing.U))
                {
                    string normKey = NormalizeResourceName(_names.GetValueOrDefault(ing.U, TailName(ing.U)));
                    optionalUtilities.Add(normKey);

                    int avail = remainingWarehouseStock != null ? remainingWarehouseStock.GetValueOrDefault(normKey, 0) : 0;
                    int used = Math.Min(needed, avail);
                    if (remainingWarehouseStock != null && used > 0)
                    {
                        remainingWarehouseStock[normKey] = avail - used;
                        if (rawTotals.TryGetValue(normKey, out var existing))
                            rawTotals[normKey] = (existing.UniqueName, existing.Amount + used);
                        else
                            rawTotals[normKey] = (ing.U, used);
                    }

                    int missing = needed - used;
                    if (missing > 0)
                    {
                        int batchYield = Math.Max(1, subRec.N);
                        int craftsNeeded = (int)Math.Ceiling((double)missing / batchYield);
                        ExpandRecursive(subRec, craftsNeeded, false, remainingWarehouseStock, stack, rawTotals, optionalUtilities, expandableUids, steps, out int subCred);
                        totalCredits += subCred;
                    }
                    else
                    {
                        steps.Add(new CraftStepInfo
                        {
                            Name = normKey,
                            CanonicalStockKey = normKey,
                            Quantity = needed,
                            BatchYield = Math.Max(1, subRec.N),
                            PreOwnedCount = used,
                            CraftHours = 0,
                            CreditsCost = 0,
                            IsRoot = false
                        });
                    }
                }
                else if (canRecurse && subRec != null)
                {
                    string normSubKey = NormalizeResourceName(_names.GetValueOrDefault(ing.U, TailName(ing.U)));
                    int avail = remainingWarehouseStock != null ? remainingWarehouseStock.GetValueOrDefault(normSubKey, 0) : 0;
                    int used = Math.Min(needed, avail);
                    if (remainingWarehouseStock != null && used > 0)
                    {
                        remainingWarehouseStock[normSubKey] = avail - used;
                        if (rawTotals.TryGetValue(normSubKey, out var existing))
                            rawTotals[normSubKey] = (existing.UniqueName, existing.Amount + used);
                        else
                            rawTotals[normSubKey] = (ing.U, used);

                        steps.Add(new CraftStepInfo
                        {
                            Name = normSubKey,
                            CanonicalStockKey = normSubKey,
                            Quantity = used,
                            BatchYield = Math.Max(1, subRec.N),
                            PreOwnedCount = used,
                            CraftHours = 0,
                            CreditsCost = 0,
                            IsRoot = false
                        });
                    }

                    int missing = needed - used;
                    if (missing > 0)
                    {
                        int batchYield = Math.Max(1, subRec.N);
                        int craftsNeeded = (int)Math.Ceiling((double)missing / batchYield);
                        ExpandRecursive(subRec, craftsNeeded, false, remainingWarehouseStock, stack, rawTotals, optionalUtilities, expandableUids, steps, out int subCred);
                        totalCredits += subCred;
                    }
                }
                else
                {
                    string normKey = NormalizeResourceName(_names.GetValueOrDefault(ing.U, TailName(ing.U)));
                    if (rawTotals.TryGetValue(normKey, out var existing))
                        rawTotals[normKey] = (existing.UniqueName, existing.Amount + needed);
                    else
                        rawTotals[normKey] = (ing.U, needed);
                }
            }
            stack.Remove(recipe.R);
        }

        private static List<CraftStepInfo> MergeIdenticalSteps(List<CraftStepInfo> steps)
        {
            var grouped = new List<CraftStepInfo>();
            var map = new Dictionary<string, CraftStepInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in steps)
            {
                string key = $"{s.IsRoot}|{s.Name}";
                if (map.TryGetValue(key, out var ex))
                {
                    ex.Quantity += s.Quantity;
                    ex.PreOwnedCount += s.PreOwnedCount;
                    ex.CreditsCost += s.CreditsCost;
                }
                else
                {
                    var clone = new CraftStepInfo
                    {
                        Name = s.Name,
                        CanonicalStockKey = s.CanonicalStockKey,
                        Quantity = s.Quantity,
                        BatchYield = s.BatchYield,
                        PreOwnedCount = s.PreOwnedCount,
                        CraftHours = s.CraftHours,
                        CreditsCost = s.CreditsCost,
                        IsRoot = s.IsRoot
                    };
                    map[key] = clone;
                    grouped.Add(clone);
                }
            }
            return grouped;
        }

        /// <summary>
        /// Runs weighted greedy Set Cover across meta farming nodes for actual queued deficits only.
        /// Returns empty lists when nothing is missing.
        /// </summary>
        public (List<FarmStrategyStep> OptimalRoute, List<FarmNodeRecommendation> RankedNodes) BuildOptimalFarmingPlan(
            Dictionary<string, int> deficitResources,
            IReadOnlyList<VoidFissureItem> liveFissures,
            bool prioritizeSteelPath)
        {
            var farmableDeficits = new HashSet<string>(
                deficitResources.Where(k => k.Value > 0).Select(k => NormalizeResourceName(k.Key)),
                StringComparer.OrdinalIgnoreCase);

            var allNodes = GetMetaFarmingNodes();
            var route = new List<FarmStrategyStep>();
            var optimalOrderByNode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            if (farmableDeficits.Count == 0)
                return (route, new List<FarmNodeRecommendation>());

            var uncovered = new HashSet<string>(farmableDeficits, StringComparer.OrdinalIgnoreCase);
            var usedNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int step = 1; step <= 8 && uncovered.Count > 0; step++)
            {
                MetaFarmNode? best = null;
                List<string>? bestCovers = null;
                double bestScore = -1;

                foreach (var node in allNodes)
                {
                    if (usedNodes.Contains(node.NodeName)) continue;
                    var covers = node.DropResources.Where(r => uncovered.Contains(r)).ToList();
                    if (covers.Count == 0) continue;

                    double rareBonus = covers.Count(r => RareResourceSet.Contains(r)) * 35.0;
                    bool hasFissure = FindMatchingFissure(node, liveFissures) != null;
                    double spBonus = (prioritizeSteelPath && node.SteelPath) ? 18.0 : 0.0;
                    double score = (covers.Count * 100.0) + rareBonus + (hasFissure ? 25.0 : 0.0) + spBonus + (node.Rating * 4.0);

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = node;
                        bestCovers = covers;
                    }
                }

                if (best == null || bestCovers == null) break;

                usedNodes.Add(best.NodeName);
                optimalOrderByNode[best.NodeName] = step;
                foreach (var c in bestCovers) uncovered.Remove(c);

                var fissure = FindMatchingFissure(best, liveFissures);
                route.Add(new FarmStrategyStep
                {
                    StepNumber = step,
                    Node = best.NodeName,
                    Planet = best.Planet,
                    MissionType = best.MissionType,
                    CoversText = string.Join(" + ", bestCovers),
                    SetupHint = best.RecommendedSetup,
                    FissureNote = fissure != null ? $"🌀 Разлом {fissure.Tier} ({fissure.MissionType}) • {fissure.TimeRemaining}" : null
                });
            }

            var ranked = allNodes
                .Select(n => new
                {
                    Node = n,
                    Matched = n.DropResources.Where(r => farmableDeficits.Contains(r)).ToList(),
                    Fissure = FindMatchingFissure(n, liveFissures),
                    Order = optimalOrderByNode.GetValueOrDefault(n.NodeName, 0)
                })
                .Where(x => x.Matched.Count > 0)
                .OrderByDescending(x => x.Order > 0 ? 1 : 0)
                .ThenBy(x => x.Order == 0 ? int.MaxValue : x.Order)
                .ThenByDescending(x => x.Matched.Count)
                .ThenByDescending(x => (prioritizeSteelPath && x.Node.SteelPath) ? 1 : 0)
                .ThenByDescending(x => x.Fissure != null ? 1 : 0)
                .ThenByDescending(x => x.Node.Rating)
                .Take(10)
                .Select(x =>
                {
                    var tags = x.Node.Tags.ToList();
                    if (prioritizeSteelPath && x.Node.SteelPath)
                        tags.Insert(0, "💀 СП: +100% шанс дропа ресурсов");
                    return new FarmNodeRecommendation
                    {
                        RouteOrder = x.Order,
                        IsOptimalPick = x.Order > 0,
                        Node = x.Node.NodeName,
                        Planet = x.Node.Planet,
                        MissionType = x.Node.MissionType,
                        MatchedResources = x.Matched,
                        CommunityRating = x.Node.Rating,
                        Tags = tags,
                        RecommendedSetup = x.Node.RecommendedSetup,
                        FarmingTip = x.Node.FarmingTip,
                        SteelPathViable = x.Node.SteelPath,
                        ActiveFissureBonus = x.Fissure != null
                            ? $"🌀 Активен Разлом {x.Fissure.Tier} ({x.Fissure.Node}) — двойной профит • {x.Fissure.TimeRemaining}"
                            : string.Empty
                    };
                })
                .ToList();

            return (route, ranked);
        }

        private static VoidFissureItem? FindMatchingFissure(MetaFarmNode node, IReadOnlyList<VoidFissureItem> liveFissures)
        {
            if (liveFissures == null || liveFissures.Count == 0 || string.IsNullOrWhiteSpace(node.NodeName)) return null;
            foreach (var f in liveFissures)
            {
                if (f.Node.Equals(node.NodeName, StringComparison.OrdinalIgnoreCase) ||
                    (node.NodeName.Length >= 4 && f.Node.Contains(node.NodeName, StringComparison.OrdinalIgnoreCase)))
                    return f;
            }
            return null;
        }

        public static List<MetaFarmNode> GetMetaFarmingNodes() => MetaNodes;

        private static readonly List<MetaFarmNode> MetaNodes = new()
        {
            new MetaFarmNode
            {
                NodeName = "Ophelia",
                Planet = "Уран (Uranus)",
                MissionType = "Выживание",
                EnemyFaction = "Гринир (Подводная лаборатория)",
                DropResources = new[] { "Теллуриум", "Полимеры", "Пластиды", "Галлий", "Образец Филдрона" },
                Rating = 4.95,
                Tags = new[] { "★ ТОП ДЛЯ ТЕЛЛУРА", "Полимеры + Пластиды", "Стальной путь" },
                RecommendedSetup = "Nekros (Desecrate) + Khora (Pilfering Strangledome) + Speed Nova",
                FarmingTip = "Враги с ранцами считаются арчвинг-юнитами — единственный планетный фарм Теллуриума.",
                SteelPath = true
            },
            new MetaFarmNode
            {
                NodeName = "Gabii",
                Planet = "Церера (Ceres)",
                MissionType = "Выживание (Тёмный сектор)",
                EnemyFaction = "Заражённые",
                DropResources = new[] { "Элемент питания Орокин", "Сплавы", "Схемы", "Ампула Детонита" },
                Rating = 4.92,
                Tags = new[] { "★ ТОП ЯЧЕЙКИ ОРОКИН", "+35% шанс ресурсов", "Тёмный сектор" },
                RecommendedSetup = "Nekros + Смита/Чеса + оружие с разрезом",
                FarmingTip = "За 20 минут на Стальном пути выпадает 8-15 Элементов питания Орокин.",
                SteelPath = true
            },
            new MetaFarmNode
            {
                NodeName = "Helene",
                Planet = "Сатурн (Saturn)",
                MissionType = "Оборона",
                EnemyFaction = "Гринир",
                DropResources = new[] { "Элемент питания Орокин", "Пластиды", "Наноспоры", "Ампула Детонита" },
                Rating = 4.88,
                Tags = new[] { "Ячейки Орокин + Пластиды", "Прокачка рангов", "Быстрые волны" },
                RecommendedSetup = "Speed Nova + Saryn / Mirage / Volt",
                FarmingTip = "Компактная карта: удобно фармить Элементы Орокин и Пластиды параллельно с прокачкой.",
                SteelPath = true
            },
            new MetaFarmNode
            {
                NodeName = "Elara",
                Planet = "Юпитер (Jupiter)",
                MissionType = "Выживание",
                EnemyFaction = "Корпус",
                DropResources = new[] { "Нейронные датчики", "Гексенон", "Вторсырьё", "Сплавы", "Образец Филдрона" },
                Rating = 4.90,
                Tags = new[] { "★ ТОП НЕЙРОДАТЧИКИ", "Фарм Гексенона", "Много Вторсырья" },
                RecommendedSetup = "Khora (Купол) + Nekros + Wisp",
                FarmingTip = "Газовый город Юпитера: даёт одновременно Нейронные датчики, Вторсырьё и Гексенон с Амальгам.",
                SteelPath = true
            },
            new MetaFarmNode
            {
                NodeName = "Io",
                Planet = "Юпитер (Jupiter)",
                MissionType = "Оборона",
                EnemyFaction = "Корпус",
                DropResources = new[] { "Оксиум", "Нейронные датчики", "Вторсырьё", "Сплавы" },
                Rating = 4.88,
                Tags = new[] { "★ ЛУЧШИЙ ФАРМ ОКСИУМА", "Дроны Оксиума с 3 волны", "Нейронные датчики" },
                RecommendedSetup = "Khora (Pilfering Strangledome) + Speed Nova",
                FarmingTip = "Уничтожайте Дронов Оксиума до их камикадзе-взрыва — иначе Оксиум не выпадет.",
                SteelPath = true
            },
            new MetaFarmNode
            {
                NodeName = "Mot",
                Planet = "Бездна (Void)",
                MissionType = "Выживание (Т4)",
                EnemyFaction = "Порабощённые",
                DropResources = new[] { "Кристалл аргона", "Модуль контроля", "Феррит", "Рубедо" },
                Rating = 4.92,
                Tags = new[] { "★ ТОП ДЛЯ АРГОНА", "Модули контроля + Феррит", "Стальной путь" },
                RecommendedSetup = "Revenant / Dante + Nekros / Khora",
                FarmingTip = "Кристалл аргона распадается на 50% каждые 24ч: запускайте крафт в тот же день.",
                SteelPath = true
            },
            new MetaFarmNode
            {
                NodeName = "Hepit",
                Planet = "Бездна (Void)",
                MissionType = "Захват",
                EnemyFaction = "Порабощённые",
                DropResources = new[] { "Кристалл аргона", "Модуль контроля", "Феррит", "Рубедо" },
                Rating = 4.78,
                Tags = new[] { "Спидран 45 сек", "Ящики Аргона", "Реликвии Лито" },
                RecommendedSetup = "Xaku (Взлом контейнеров 4-й ультой) / Limbo",
                FarmingTip = "Пробегайте захват с Xaku/Limbo — ломайте все Пег-контейнеры для быстрого Аргона.",
                SteelPath = false
            },
            new MetaFarmNode
            {
                NodeName = "Terrorem",
                Planet = "Деймос (Deimos)",
                MissionType = "Выживание",
                EnemyFaction = "Заражённые",
                DropResources = new[] { "Образец Мутагена", "Нейроды", "Элемент питания Орокин", "Наноспоры" },
                Rating = 4.90,
                Tags = new[] { "★ ТОП МУТАГЕН И НЕЙРОДЫ", "+Ячейки Орокин", "Наноспоры" },
                RecommendedSetup = "Nekros + Khora + оружие с разрезом",
                FarmingTip = "На Деймосе Образец Мутагена имеет редкий-повышенный дроп (намного выше, чем на Эриде).",
                SteelPath = true
            },
            new MetaFarmNode
            {
                NodeName = "Wahiba",
                Planet = "Марс (Mars)",
                MissionType = "Выживание (Тёмный сектор)",
                EnemyFaction = "Заражённые",
                DropResources = new[] { "Морфиды", "Галлий", "Вторсырьё", "Феррит" },
                Rating = 4.86,
                Tags = new[] { "★ ТОП МОРФИДЫ И ГАЛЛИЙ", "+25% шанс дропа", "Феррит + Вторсырьё" },
                RecommendedSetup = "Nekros / Khora",
                FarmingTip = "Тёмный сектор Марса быстро закрывает потребности в Морфидах, Галлии и Вторсырье.",
                SteelPath = true
            },
            new MetaFarmNode
            {
                NodeName = "Assur",
                Planet = "Уран (Uranus)",
                MissionType = "Выживание (Тёмный сектор)",
                EnemyFaction = "Заражённые",
                DropResources = new[] { "Полимеры", "Пластиды", "Галлий", "Образец Мутагена" },
                Rating = 4.84,
                Tags = new[] { "★ ТОП ПОЛИМЕРЫ И ПЛАСТИДЫ", "+30% ресурсы", "Без арчвинга" },
                RecommendedSetup = "Khora + Nekros + Speed Nova",
                FarmingTip = "Если Теллуриум не нужен, Assur даёт больше Полимеров и Пластидов за минуту, чем Ophelia.",
                SteelPath = true
            },
            new MetaFarmNode
            {
                NodeName = "Cameria",
                Planet = "Юпитер (Jupiter)",
                MissionType = "Выживание (Тёмный сектор)",
                EnemyFaction = "Заражённые",
                DropResources = new[] { "Вторсырьё", "Нейронные датчики", "Сплавы", "Гексенон" },
                Rating = 4.82,
                Tags = new[] { "+30% шанс ресурсов", "Вторсырьё + Датчики" },
                RecommendedSetup = "Nekros + Оружие с разрезом",
                FarmingTip = "Удобный Тёмный сектор Юпитера против Заражённых с бонусом к Нейродатчикам и Вторсырью.",
                SteelPath = true
            },
            new MetaFarmNode
            {
                NodeName = "Coba",
                Planet = "Земля (Earth)",
                MissionType = "Оборона (Тёмный сектор)",
                EnemyFaction = "Заражённые",
                DropResources = new[] { "Нейроды", "Рубедо", "Феррит", "Ампула Детонита" },
                Rating = 4.74,
                Tags = new[] { "★ БЫСТРЫЙ ФАРМ НЕЙРОДОВ", "Рубедо + Феррит" },
                RecommendedSetup = "Saryn / Volt / Nekros",
                FarmingTip = "Отличная точка старта для фарма Нейродов, Рубедо и Феррита.",
                SteelPath = true
            },
            new MetaFarmNode
            {
                NodeName = "Hieracon",
                Planet = "Плутон (Pluto)",
                MissionType = "Раскопки (Тёмный сектор)",
                EnemyFaction = "Заражённые",
                DropResources = new[] { "Криотик", "Эндо", "Морфиды", "Рубедо", "Пластиды", "Образец Филдрона" },
                Rating = 4.91,
                Tags = new[] { "★ ЛУЧШИЙ ФАРМ КРИОТИКА", "100 Криотика за экскаватор", "Эндо + Реликвии" },
                RecommendedSetup = "Frost / Limbo / Khora + Nekros",
                FarmingTip = "Запускайте 2-3 экскаватора одновременно — это самый быстрый способ нафармить тысячи Криотика.",
                SteelPath = true
            },
            new MetaFarmNode
            {
                NodeName = "Malva",
                Planet = "Венера (Venus)",
                MissionType = "Выживание (Тёмный сектор)",
                EnemyFaction = "Заражённые",
                DropResources = new[] { "Полимеры", "Сплавы", "Схемы", "Образец Филдрона" },
                Rating = 4.78,
                Tags = new[] { "★ ТОП СХЕМЫ + ПОЛИМЕРЫ", "+20% к ресурсам", "Старт игры" },
                RecommendedSetup = "Nekros / Khora",
                FarmingTip = "Покрывает связку Схемы + Полимеры + Сплавы буквально за один заход.",
                SteelPath = true
            },
            new MetaFarmNode
            {
                NodeName = "Ночная Волна (Магазин Норы)",
                Planet = "Магазин Ночной Волны",
                MissionType = "Обмен Доверия Норы",
                EnemyFaction = "Сезонная активность",
                DropResources = new[] { "Экстракт Нитаина", "Катализатор Орокин", "Реактор Орокин" },
                Rating = 4.97,
                Tags = new[] { "★ ОСНОВНОЙ ИСТОЧНИК НИТАИНА", "15 Доверия = 5x Нитаина" },
                RecommendedSetup = "Закрывайте 3-4 еженедельных акта Ночной Волны",
                FarmingTip = "Экстракт Нитаина НЕ падает с обычных миссий звёздной карты — берите наборы по 5 шт. у Норы.",
                SteelPath = false
            },
            new MetaFarmNode
            {
                NodeName = "Вторжения (Invasions)",
                Planet = "Активные Вторжения (Вкладка Эвенты)",
                MissionType = "3 миссии за сторону",
                EnemyFaction = "Гринир / Корпус / Заражённые",
                DropResources = new[] { "Филдрон", "Инжектор Детонита", "Мутагенная Масса" },
                Rating = 4.94,
                Tags = new[] { "★ БЕЗ ЧЕРТЕЖЕЙ ДОДЗЁ", "1-3 готовых компонента за вторжение" },
                RecommendedSetup = "Titania / Volt / Gauss (быстрый спидран 3 миссий)",
                FarmingTip = "Фармите готовые Филдроны, Детониты и Мутагенные Массы во Вторжениях — это экономит миллионы кредитов и десятки часов Кузницы.",
                SteelPath = false
            },
            new MetaFarmNode
            {
                NodeName = "The Index (Индекс)",
                Planet = "Нептун (Neptune)",
                MissionType = "Финансовая арена Корпуса",
                EnemyFaction = "Брокеры Корпуса",
                DropResources = new[] { "Кредиты" },
                Rating = 4.98,
                Tags = new[] { "★ ТОП ФАРМ КРЕДИТОВ", "250k / 500k с бустером за 4 мин" },
                RecommendedSetup = "Revenant / Rhino + Mesa / Laetum",
                FarmingTip = "Сдавайте очки пачками по 15 штук (+8 бонус) или ходите в Profit-Taker с Хромой.",
                SteelPath = false
            },
            new MetaFarmNode
            {
                NodeName = "Vodyanoi",
                Planet = "Седна (Sedna)",
                MissionType = "Арена Rathuum (Ур. 85)",
                EnemyFaction = "Палачи Гринир",
                DropResources = new[] { "Эндо", "Сплавы", "Рубедо", "Ампула Детонита" },
                Rating = 4.92,
                Tags = new[] { "★ ТОП ФАРМ ЭНДО", "До 3,000 Эндо за 2 мин" },
                RecommendedSetup = "Khora + Nidus (Larva) + 2x Nekros (Ripkas Amalgam)",
                FarmingTip = "Высокий спавн Эндо в пати с Некросами и модом Амальгам Рипкас.",
                SteelPath = true
            },
            new MetaFarmNode
            {
                NodeName = "Taveuni",
                Planet = "Крепость Кувы (Kuva Fortress)",
                MissionType = "Выживание",
                EnemyFaction = "Гринир Кувы",
                DropResources = new[] { "Кува", "Нейроды", "Ампула Детонита" },
                Rating = 4.86,
                Tags = new[] { "★ БЕСКОНЕЧНАЯ КУВА", "Нейроды бонусом" },
                RecommendedSetup = "Khora + Nekros + Wisp",
                FarmingTip = "Хорошо совмещается с Арбитражом/Реликвиями Бездны или обменом у Йонты на Заримане.",
                SteelPath = true
            },
            new MetaFarmNode
            {
                NodeName = "Каскад / Поток Заримана",
                Planet = "Зариман 10-0 (Zariman)",
                MissionType = "Каскад / Армагеддон / Мобильная",
                EnemyFaction = "Гринир / Корпус / Тракс",
                DropResources = new[] { "Лампа Энтрати", "Траксовая плазма", "Эндо", "Феррит" },
                Rating = 4.91,
                Tags = new[] { "★ ТОП ЛАМПЫ ЭНТРАТИ", "Траксовая плазма", "Мистики Молота/Изобилия" },
                RecommendedSetup = "Xaku / Carrier (Looter) + оператор 177/747",
                FarmingTip = "Сундуки на Заримане (особенно в Зачистке Галсион/Террор) и Экстракторы приносят десятки Ламп Энтрати.",
                SteelPath = true
            },
            new MetaFarmNode
            {
                NodeName = "Sanctum Anatomica",
                Planet = "Деймос (Лаборатории Альбрехта)",
                MissionType = "Зачистка / Выживание / Нетрацеллы",
                EnemyFaction = "Шёпот (Мурмур)",
                DropResources = new[] { "Стелла", "Некракатушка", "Лампа Энтрати", "Элемент питания Орокин" },
                Rating = 4.89,
                Tags = new[] { "★ СТЕЛЛА И НЕКРАКАТУШКА", "Нетрацеллы", "Архонт-осколки" },
                RecommendedSetup = "Dante / Xaku (взрыв саркофагов Шёпота) + Torid",
                FarmingTip = "Разбивайте саркофаги Шёпота с помощью Xaku 4-й способностью: выпадает много Стеллы.",
                SteelPath = true
            },
            new MetaFarmNode
            {
                NodeName = "Circulus / Yuvarium",
                Planet = "Луа (Lua)",
                MissionType = "Выживание Сопряжения",
                EnemyFaction = "Тракс / Заражённые / Порабощённые",
                DropResources = new[] { "Траксовая плазма Луа", "Соматические волокна", "Нейроды", "Феррит" },
                Rating = 4.90,
                Tags = new[] { "★ ПЛАЗМА ЛУА", "Воруна + Мистики Луа" },
                RecommendedSetup = "Nekros + Dante + Оператор 747",
                FarmingTip = "Плазма Луа падает стабильно каждые 5 минут и за добивание врагов Тракс оператором.",
                SteelPath = true
            },
            new MetaFarmNode
            {
                NodeName = "Опыт Дувири / Цепь",
                Planet = "Дувири (Duviri)",
                MissionType = "Опыт Дувири / Схватка с Оровирмом",
                EnemyFaction = "Даксы",
                DropResources = new[] { "Струбцина пафоса", "Руническая сущность", "Яо-куст", "Лист сагрума", "Дракрут", "Эвен-цветок", "Ковник", "Перламутровая галька" },
                Rating = 4.87,
                Tags = new[] { "★ ТОП ДЛЯ ИНКАРНОНОВ", "10-15 Струбцин за Оровирма" },
                RecommendedSetup = "Идите в 'Опыт Дувири', собирайте кусты по пути к 6 декретам + Оровирм",
                FarmingTip = "Прогресс ресурсов Дувири сохраняется при получении следующего Декрета.",
                SteelPath = true
            },
            new MetaFarmNode
            {
                NodeName = "Хёльвания 1999 (Заказы Гекса)",
                Planet = "Хёльвания 1999",
                MissionType = "Адская зачистка / Наследие / Заказы",
                EnemyFaction = "Скальдра и Техрот",
                DropResources = new[] { "Техротный хитин", "Образцы Эфервона", "Экспериментальное дуга-реле", "Материнская плата Техрота", "Кредиты" },
                Rating = 4.88,
                Tags = new[] { "★ РЕСУРСЫ 1999", "Реликвии + Эндо", "Тайники Сейфов" },
                RecommendedSetup = "Saryn / Mesa / Dante + Ключи Сейфов Биокода",
                FarmingTip = "Сейфы Биокода Скальдры и сундуки Техрота дают повышенный дроп ресурсов 1999 и мистики.",
                SteelPath = true
            },
            new MetaFarmNode
            {
                NodeName = "Равнины Эйдолона",
                Planet = "Земля (Цетус)",
                MissionType = "Свободный мир / Охота на Эйдолонов",
                EnemyFaction = "Гринир Бивня",
                DropResources = new[] { "Огонек Цетуса", "Огонёк Цетуса", "Ирадит", "Грокдрул", "Нистлпод", "Маприко", "Дыхание Эйдолона" },
                Rating = 4.82,
                Tags = new[] { "★ ЦЕТУС РЕСУРСЫ И ОГОНЬКИ", "Ночной спавн у озёр" },
                RecommendedSetup = "Itzal (Cosmic Crush) + Nova / Volt (Быстрый сбор)",
                FarmingTip = "Огоньки Цетуса легче всего фармить ночью по периметру озёр или менять за стенд Перьев.",
                SteelPath = true
            },
            new MetaFarmNode
            {
                NodeName = "Долина Сфер",
                Planet = "Венера (Фортуна)",
                MissionType = "Термальные разломы / Заказы / Майнинг",
                EnemyFaction = "Корпус Терра",
                DropResources = new[] { "Термошлак", "Спора горгарикуса", "Клубень тепы", "Митокардия", "Тороид Калда", "Тороид Вега", "Тороид Сола" },
                Rating = 4.81,
                Tags = new[] { "★ РЕСУРСЫ ФОРТУНЫ И БАРУКА", "Терморазломы" },
                RecommendedSetup = "Nekros (Тороиды в пещерах) + Mesa + Sunpoint Drill",
                FarmingTip = "Сфера Извлечения Прибыли и Храм Прибыли на высокой тревоге дают максимум Тороидов.",
                SteelPath = true
            },
            new MetaFarmNode
            {
                NodeName = "Камбионский Дрейф",
                Planet = "Деймос (Некралиск)",
                MissionType = "Заказы Энтрати / Столбы Реквиема",
                EnemyFaction = "Серые Заражённые",
                DropResources = new[] { "Ганглий", "Пустулит", "Люцент-тераглоб", "Тауманика", "Неопознанное ядро", "Пластиды" },
                Rating = 4.83,
                Tags = new[] { "★ ОБЕЛИСКИ РЕКВИЕМА", "Тауманика и ресурсы некрамехов" },
                RecommendedSetup = "Активируйте обелиски Реквиема усилителем Оператора",
                FarmingTip = "Убийство мобов рядом с обелисками Реквиема оператором мгновенно роняет редкие камни Деймоса (Тауманику/Хецифрон).",
                SteelPath = false
            },
            new MetaFarmNode
            {
                NodeName = "Проксима Вуали (Рэйлджек)",
                Planet = "Проксима Вуали / Сатурна",
                MissionType = "Стычка Рэйлджека",
                EnemyFaction = "Гринир / Владеющие Разумом",
                DropResources = new[] { "Титаниум", "Астерит", "Изос", "Карбиды", "Кубические диоды", "Кесслеры", "Фризнелы", "Коперники", "Пастрелы", "Окраксные конденсаторы", "Бракоид", "Стержни Гэллоса", "Коммы", "Трахеоны", "Кредиты", "Эндо" },
                Rating = 4.84,
                Tags = new[] { "★ ВСЕ РЕСУРСЫ РЭЙЛДЖЕКА", "Много Кредитов и Эндо" },
                RecommendedSetup = "Seeker Volley + Lavos / Protea (бесконечная энергия Рэйлджека)",
                FarmingTip = "Разрушайте астероиды после зачистки истребителей — за миссию выпадают тысячи Титаниума и Детали на Эндо.",
                SteelPath = false
            }
        };

        private static string TailName(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return string.Empty;
            int idx = uid.LastIndexOf('/');
            return idx >= 0 ? uid[(idx + 1)..] : uid;
        }

        private static string StripEnglishSuffix(string s)
        {
            int idx = s.IndexOf(" (", StringComparison.Ordinal);
            return idx > 0 ? s[..idx].Trim() : s.Trim();
        }

        public static string GetGlyphForCategory(string category) => category switch
        {
            "Варфреймы" => "🛡️",
            "Основное" => "🔫",
            "Вторичное" => "🎯",
            "Ближний бой" => "⚔️",
            "Компаньоны" => "🤖",
            "Арчвинг" => "🚀",
            "Усилители" => "👁️",
            _ => "🔨"
        };

        private Dictionary<string, List<string>> _relicDropsByUid = new(StringComparer.OrdinalIgnoreCase);
        private bool _relicCacheLoadAttempted;
        private readonly object _relicLock = new();

        public async System.Threading.Tasks.Task<bool> EnsureRelicDropsLoadedAsync()
        {
            lock (_relicLock)
            {
                if (_relicDropsByUid.Count > 0) return true;
                if (_relicCacheLoadAttempted) return _relicDropsByUid.Count > 0;
                _relicCacheLoadAttempted = true;
            }

            try
            {
                string dataDir = Path.GetDirectoryName(StorageService.Instance.SaveDirectoryPath)
                    ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                string cachePath = Path.Combine(dataDir, "cache", "relic_drops_v1.json");

                if (File.Exists(cachePath) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath)).TotalDays < 7)
                {
                    string cachedJson = await File.ReadAllTextAsync(cachePath).ConfigureAwait(false);
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(cachedJson);
                    if (loaded != null && loaded.Count > 10)
                    {
                        lock (_relicLock)
                        {
                            _relicDropsByUid = new Dictionary<string, List<string>>(loaded, StringComparer.OrdinalIgnoreCase);
                        }
                        return true;
                    }
                }

                var relicsTask = WarframeApiService.GetStringAsync("https://browse.wf/warframe-public-export-plus/ExportRelics.json");
                var rewardsTask = WarframeApiService.GetStringAsync("https://browse.wf/warframe-public-export-plus/ExportRewards.json");
                await System.Threading.Tasks.Task.WhenAll(relicsTask, rewardsTask).ConfigureAwait(false);
                if (string.IsNullOrEmpty(relicsTask.Result) || string.IsNullOrEmpty(rewardsTask.Result))
                {
                    if (File.Exists(cachePath))
                    {
                        var stale = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(await File.ReadAllTextAsync(cachePath).ConfigureAwait(false));
                        if (stale != null) lock (_relicLock) _relicDropsByUid = new Dictionary<string, List<string>>(stale, StringComparer.OrdinalIgnoreCase);
                    }
                    return _relicDropsByUid.Count > 0;
                }

                using var relDoc = JsonDocument.Parse(relicsTask.Result);
                using var rewDoc = JsonDocument.Parse(rewardsTask.Result);

                var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var relProp in relDoc.RootElement.EnumerateObject())
                {
                    var rel = relProp.Value;
                    string eraEn = rel.TryGetProperty("era", out var er) ? er.GetString() ?? "" : "";
                    string cat = rel.TryGetProperty("category", out var ct) ? ct.GetString() ?? "" : "";
                    string manifest = rel.TryGetProperty("rewardManifest", out var rm) ? rm.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(manifest) || string.IsNullOrEmpty(cat)) continue;

                    bool isVaulted = rel.TryGetProperty("vaultedAt", out _);
                    string eraRu = eraEn switch
                    {
                        "Lith" => "Лит",
                        "Meso" => "Мезо",
                        "Neo" => "Нео",
                        "Axi" => "Акси",
                        "Requiem" => "Реквием",
                        "Vanguard" => "Авангард",
                        _ => eraEn
                    };
                    string relName = isVaulted ? $"{eraRu} {cat} (Хран.)" : $"{eraRu} {cat}";

                    if (!rewDoc.RootElement.TryGetProperty(manifest, out var rewArr) || rewArr.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var drop in rewArr.EnumerateArray())
                    {
                        string rawType = drop.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(rawType)) continue;
                        string normUid = rawType
                            .Replace("/Lotus/StoreItems/", "/Lotus/", StringComparison.Ordinal)
                            .Replace("/Lotus/Types/StoreItems/", "/Lotus/", StringComparison.Ordinal);

                        string rarity = drop.TryGetProperty("rarity", out var rr) ? rr.GetString() ?? "" : "";
                        string rarTag = rarity switch
                        {
                            "RARE" => "Золото",
                            "UNCOMMON" => "Серебро",
                            _ => "Бронза"
                        };
                        string entry = $"{relName} [{rarTag}]";

                        void AddKey(string k)
                        {
                            if (string.IsNullOrEmpty(k)) return;
                            if (!map.TryGetValue(k, out var list))
                            {
                                list = new List<string>();
                                map[k] = list;
                            }
                            if (!list.Contains(entry) && list.Count < 8)
                                list.Add(entry);
                        }

                        AddKey(normUid);
                        if (_byBlueprint.TryGetValue(normUid, out var bpRec) && !string.IsNullOrEmpty(bpRec.R))
                            AddKey(bpRec.R);
                    }
                }

                if (map.Count > 0)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                        string tmp = cachePath + ".tmp";
                        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(map)).ConfigureAwait(false);
                        File.Move(tmp, cachePath, overwrite: true);
                    }
                    catch { }
                    lock (_relicLock) { _relicDropsByUid = map; }
                }
            }
            catch { }

            return _relicDropsByUid.Count > 0;
        }

        public string BuildRelicHintForItem(
            string rootUniqueName,
            string blueprintUniqueName,
            IEnumerable<string> ingredientUids,
            bool isPrime,
            bool isVaulted)
        {
            if (!isPrime) return string.Empty;

            var found = new List<string>();
            lock (_relicLock)
            {
                if (_relicDropsByUid.Count > 0)
                {
                    if (!string.IsNullOrEmpty(blueprintUniqueName) && _relicDropsByUid.TryGetValue(blueprintUniqueName, out var bpRelics))
                        found.AddRange(bpRelics.Take(2));

                    if (!string.IsNullOrEmpty(rootUniqueName) && _relicDropsByUid.TryGetValue(rootUniqueName, out var rRelics))
                        foreach (var r in rRelics.Take(2)) if (!found.Contains(r)) found.Add(r);

                    foreach (string uid in ingredientUids)
                    {
                        if (found.Count >= 5) break;
                        if (_relicDropsByUid.TryGetValue(uid, out var subRels))
                        {
                            string tail = NormalizeResourceName(_names.GetValueOrDefault(uid, TailName(uid)));
                            var activeFirst = subRels.OrderBy(s => s.Contains("(Хран.)") ? 1 : 0).FirstOrDefault();
                            if (!string.IsNullOrEmpty(activeFirst))
                            {
                                string shortRel = $"{tail}: {activeFirst}";
                                if (!found.Contains(shortRel)) found.Add(shortRel);
                            }
                        }
                    }
                }
            }

            if (found.Count > 0)
            {
                string prefix = isVaulted ? "🔒 В Хранилище • Реликвии: " : "🏺 Реликвии: ";
                return prefix + string.Join(" | ", found.Take(4));
            }

            return isVaulted
                ? "🔒 В Хранилище Прайм (Реликвии недоступны в миссиях / Возрождение Варзии)"
                : "🏺 Выбивается из актуальных Реликвий Бездны (Лит / Мезо / Нео / Акси)";
        }

        public static string GetResourceIcon(string resourceName)
        {
            string n = resourceName.ToLowerInvariant();
            if (n.Contains("кредит")) return "💳";
            if (n.Contains("форма")) return "💠";
            if (n.Contains("орокин") || n.Contains("реактор") || n.Contains("катализатор")) return "⚡";
            if (n.Contains("теллур")) return "☄️";
            if (n.Contains("аргон")) return "🔮";
            if (n.Contains("нитаин")) return "🧪";
            if (n.Contains("кува")) return "🩸";
            if (n.Contains("криотик")) return "❄️";
            if (n.Contains("оксиум")) return "🪶";
            if (n.Contains("нейрод") || n.Contains("нейрон")) return "🧠";
            if (n.Contains("эндо")) return "✨";
            if (n.Contains("струбцин")) return "🔧";
            if (n.Contains("лампа") || n.Contains("стелла")) return "🏺";
            if (n.Contains("плазма")) return "⚛️";
            if (n.Contains("мутаген") || n.Contains("наноспор")) return "🦠";
            if (n.Contains("филдрон") || n.Contains("детонит") || n.Contains("схем")) return "🔋";
            return "💎";
        }
    }
}
