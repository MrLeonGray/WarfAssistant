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
    public sealed class BuilderPresetTemplate
    {
        public string Title { get; set; } = string.Empty;
        public string Category { get; set; } = "Варфрейм";
        public string PrimaryHint { get; set; } = string.Empty;
        public string SecondaryHint { get; set; } = string.Empty;
        public string HelminthId { get; set; } = "none";
        public string[] PrimaryMods { get; set; } = Array.Empty<string>();
        public string[] SecondaryMods { get; set; } = Array.Empty<string>();
        public string PrimaryArcane1 { get; set; } = string.Empty;
        public string PrimaryArcane2 { get; set; } = string.Empty;
        public string WeaponArcane { get; set; } = string.Empty;
        public string DefaultShardId { get; set; } = "crimson_str";
        public int ShardCount { get; set; }
    }

    /// <summary>
    /// Offline-first database for Warframe Builder: loads all 823+ official DE Platforms,
    /// 1,243 Mods/Avionics, and 168 Arcanes from compressed builder_seed.json.gz,
    /// along with curated Helminth infusions, Archon Shards, Amp Scaffolds/Braces, and Meta Presets.
    /// </summary>
    public sealed class BuilderDatabaseService
    {
        private static BuilderDatabaseService? _instance;
        public static BuilderDatabaseService Instance => _instance ??= new BuilderDatabaseService();

        private sealed class SeedDoc
        {
            [JsonPropertyName("version")] public string? Version { get; set; }
            [JsonPropertyName("platforms")] public List<BuilderPlatformItem>? Platforms { get; set; }
            [JsonPropertyName("mods")] public List<BuilderModItem>? Mods { get; set; }
            [JsonPropertyName("arcanes")] public List<BuilderArcaneItem>? Arcanes { get; set; }
        }

        public List<BuilderPlatformItem> Platforms { get; } = new();
        public List<BuilderModItem> Mods { get; } = new();
        public List<BuilderArcaneItem> Arcanes { get; } = new();

        private readonly Dictionary<string, BuilderPlatformItem> _platformsById = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BuilderModItem> _modsById = new(StringComparer.OrdinalIgnoreCase);
        private bool _loaded;

        private BuilderDatabaseService()
        {
            LoadSeed();
        }

        private void LoadSeed()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string? resName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("builder_seed.json.gz", StringComparison.OrdinalIgnoreCase));
                if (resName == null) return;

                using Stream? s = asm.GetManifestResourceStream(resName);
                if (s == null) return;

                using var gz = new GZipStream(s, CompressionMode.Decompress);
                var doc = JsonSerializer.Deserialize<SeedDoc>(gz);
                if (doc == null) return;

                if (doc.Platforms != null)
                {
                    Platforms.AddRange(doc.Platforms.OrderBy(p => p.Name));
                    foreach (var p in Platforms) _platformsById[p.Id] = p;

                    // Fix known seed misclassifications & synthesize missing platforms cleanly
                    var choirGlory = FindPlatformById("/Lotus/Powersuits/Choir/ChoirEruptWeapon");
                    if (choirGlory != null)
                    {
                        choirGlory.SubCategory = "Экзальт (Вторичное)";
                        if (choirGlory.TotalDamage < 50)
                        {
                            choirGlory.DamagePerShot = new List<double> { 0, 0, 0, 220 };
                            choirGlory.TotalDamage = 220;
                            choirGlory.CritChance = 15;
                            choirGlory.CritMultiplier = 2.0;
                            choirGlory.StatusChance = 20;
                            choirGlory.FireRate = 4.5;
                        }
                    }

                    if (FindPlatformById("/Lotus/Powersuits/Temple/ExaltedGuitar") == null)
                    {
                        var lizzie = new BuilderPlatformItem
                        {
                            Id = "/Lotus/Powersuits/Temple/ExaltedGuitar",
                            Name = "Лиззи (Exalted Guitar)",
                            Category = "Оружие",
                            SubCategory = "Экзальт (Основное)",
                            IsExalted = true,
                            DamagePerShot = new List<double> { 0, 0, 0, 120 },
                            TotalDamage = 120,
                            CritChance = 25,
                            CritMultiplier = 2.2,
                            StatusChance = 30,
                            FireRate = 8.0,
                            Multishot = 1.0,
                            MagazineSize = 100
                        };
                        Platforms.Add(lizzie);
                        _platformsById[lizzie.Id] = lizzie;
                    }

                    if (FindPlatformById("BEAST_CLAWS_COMPANION") == null)
                    {
                        var claws = new BuilderPlatformItem
                        {
                            Id = "BEAST_CLAWS_COMPANION",
                            Name = "Когти зверя (Кубрау / Кават / Вульпафила / Предазит)",
                            Category = "Компаньон",
                            SubCategory = "Оружие компаньона",
                            DamagePerShot = new List<double> { 30, 30, 240 },
                            TotalDamage = 300,
                            CritChance = 20,
                            CritMultiplier = 2.0,
                            StatusChance = 15,
                            FireRate = 1.5,
                            Multishot = 1.0
                        };
                        Platforms.Add(claws);
                        _platformsById[claws.Id] = claws;
                    }
                }
                if (doc.Mods != null)
                {
                    Mods.AddRange(doc.Mods.OrderByDescending(m => m.MaxRank).ThenBy(m => m.Name));
                    foreach (var m in Mods) _modsById[m.Id] = m;
                }
                if (doc.Arcanes != null)
                {
                    Arcanes.AddRange(doc.Arcanes.OrderBy(a => a.Name));
                }
            }
            catch { }
        }

        public BuilderPlatformItem? FindPlatformById(string? id) =>
            !string.IsNullOrWhiteSpace(id) && _platformsById.TryGetValue(id, out var item) ? item : null;

        public BuilderModItem? FindModById(string? id) =>
            !string.IsNullOrWhiteSpace(id) && _modsById.TryGetValue(id, out var mod) ? mod : null;

        public BuilderModItem? FindModByName(string name, string preferredCategory)
        {
            var exact = Mods.FirstOrDefault(m =>
                m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(preferredCategory) || m.Category.Equals(preferredCategory, StringComparison.OrdinalIgnoreCase)));
            if (exact != null) return exact;

            return Mods.FirstOrDefault(m =>
                m.Name.Contains(name, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(preferredCategory) || m.Category.Equals(preferredCategory, StringComparison.OrdinalIgnoreCase)));
        }

        public List<BuilderPlatformItem> GetPlatformsForCategory(string builderCategory)
        {
            return builderCategory switch
            {
                "Варфрейм" => Platforms.Where(p => p.Category == "Варфрейм").ToList(),
                "Оружие" => Platforms.Where(p => p.Category == "Оружие" && !p.IsExalted).ToList(),
                "Усилитель" => Platforms.Where(p => p.Category == "Усилитель").ToList(),
                "Артефакты Тектолиста" => Platforms.Where(p => p.Category == "Артефакты Тектолиста").ToList(),
                "Арчвинг" => Platforms.Where(p => p.Category == "Арчвинг" && p.SubCategory == "Арчвинг").ToList(),
                "Компаньон" => Platforms.Where(p => p.Category == "Компаньон" && p.SubCategory == "Компаньон").ToList(),
                "Некрамех" => Platforms.Where(p => p.Category == "Некрамех").ToList(),
                "Рэйлджек" => Platforms.Where(p => p.Id == "RAILJACK_PLEXUS").ToList(),
                _ => Platforms.Where(p => p.Category == builderCategory).ToList()
            };
        }

        public BuilderPlatformItem? ResolveLinkedSecondaryWeapon(BuilderPlatformItem? primary, string builderCategory)
        {
            if (primary == null) return null;

            // Warframes & Necramechs & Railjack Plexus prefer built-in Exalted weapons if present
            if (primary.ExaltedIds.Count > 0)
            {
                foreach (var exId in primary.ExaltedIds)
                {
                    var linked = FindPlatformById(exId);
                    if (linked != null && linked.TotalDamage > 0)
                        return linked;
                }
            }

            if (builderCategory == "Варфрейм")
            {
                // Frames without Exalted default to a popular normal weapon (e.g. Torid) so any Warframe+Weapon synergy is buildable!
                return Platforms.FirstOrDefault(p => p.Category == "Оружие" && !p.IsExalted &&
                           (p.Name.Equals("Торид", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Torid", StringComparison.OrdinalIgnoreCase)))
                    ?? Platforms.FirstOrDefault(p => p.Category == "Оружие" && !p.IsExalted);
            }

            if (builderCategory == "Арчвинг")
            {
                return Platforms.FirstOrDefault(p => p.Category == "Арчвинг" && p.SubCategory == "Тяжелое оружие" && p.Name.Contains("Маусолон", StringComparison.OrdinalIgnoreCase))
                    ?? Platforms.FirstOrDefault(p => p.Category == "Арчвинг" && p.SubCategory == "Тяжелое оружие");
            }

            if (builderCategory == "Компаньон")
            {
                if (primary.Name.Contains("Наутилус", StringComparison.OrdinalIgnoreCase))
                    return Platforms.FirstOrDefault(p => p.Name.Contains("Верглас Прайм", StringComparison.OrdinalIgnoreCase))
                        ?? Platforms.FirstOrDefault(p => p.Name.Contains("Верглас", StringComparison.OrdinalIgnoreCase));
                if (primary.Name.Contains("Гелиос", StringComparison.OrdinalIgnoreCase))
                    return Platforms.FirstOrDefault(p => p.Name.Contains("Деконструктор", StringComparison.OrdinalIgnoreCase));
                if (primary.Name.Contains("Носитель", StringComparison.OrdinalIgnoreCase))
                    return Platforms.FirstOrDefault(p => p.Name.Contains("Свипер", StringComparison.OrdinalIgnoreCase));
                if (IsBiologicalCompanion(primary))
                    return FindPlatformById("BEAST_CLAWS_COMPANION")
                        ?? Platforms.FirstOrDefault(p => p.Category == "Компаньон" && p.SubCategory == "Оружие компаньона");
                return Platforms.FirstOrDefault(p => p.Category == "Компаньон" && p.SubCategory == "Оружие компаньона");
            }

            return null;
        }

        private static bool IsBiologicalCompanion(BuilderPlatformItem? p) => p != null &&
            (p.Name.Contains("Кават", StringComparison.OrdinalIgnoreCase) ||
             p.Name.Contains("Кубрау", StringComparison.OrdinalIgnoreCase) ||
             p.Name.Contains("Вульпафил", StringComparison.OrdinalIgnoreCase) ||
             p.Name.Contains("Предазит", StringComparison.OrdinalIgnoreCase) ||
             p.Name.Contains("Гельминтный", StringComparison.OrdinalIgnoreCase) ||
             p.Id.Contains("Kubrow", StringComparison.OrdinalIgnoreCase) ||
             p.Id.Contains("Kavat", StringComparison.OrdinalIgnoreCase) ||
             p.Id.Contains("InfestedPet", StringComparison.OrdinalIgnoreCase));

        public List<BuilderPlatformItem> GetLinkedCandidates(BuilderPlatformItem? primary, string builderCategory)
        {
            if (builderCategory == "Варфрейм")
            {
                var list = new List<BuilderPlatformItem>();
                if (primary != null && primary.ExaltedIds.Count > 0)
                {
                    foreach (var exId in primary.ExaltedIds)
                    {
                        var ex = FindPlatformById(exId);
                        if (ex != null && ex.TotalDamage > 0) list.Add(ex);
                    }
                }
                // Any Primary / Secondary / Melee weapon can also be linked to receive Aura/Arcanes/Abilities/Shards!
                list.AddRange(Platforms.Where(p => p.Category == "Оружие" && !p.IsExalted));
                return list;
            }

            return builderCategory switch
            {
                "Арчвинг" => Platforms.Where(p => p.Category == "Арчвинг" && p.SubCategory is "Тяжелое оружие" or "Арч-ближ. бой").ToList(),
                "Компаньон" => Platforms.Where(p => p.Category == "Компаньон" && p.SubCategory == "Оружие компаньона").ToList(),
                "Рэйлджек" => Platforms.Where(p => p.Category == "Рэйлджек" && p.SubCategory == "Турель Рэйлджека").ToList(),
                _ when primary != null && primary.ExaltedIds.Count > 0 =>
                    primary.ExaltedIds.Select(FindPlatformById).Where(p => p != null && p.TotalDamage > 0).Cast<BuilderPlatformItem>().ToList(),
                _ => new List<BuilderPlatformItem>()
            };
        }

        public string GetModBucketForItem(BuilderPlatformItem? item)
        {
            if (item == null) return "Варфрейм";
            if (item.Category == "Варфрейм") return "Варфрейм";
            if (item.Category == "Артефакты Тектолиста") return "Артефакты Тектолиста";
            if (item.Category == "Некрамех") return "Некрамех";
            if (item.Category == "Рэйлджек") return "Рэйлджек";
            if (item.SubCategory == "Арчвинг") return "Арчвинг";
            if (item.SubCategory == "Тяжелое оружие" || item.SubCategory == "Экзальт (Некрамех)") return "Оружие арчвинга";
            if (item.SubCategory == "Арч-ближ. бой" || item.SubCategory == "Экзальт (Некрамех Ближний)") return "Арч-ближ. бой";
            if (item.SubCategory == "Компаньон") return "Компаньон";
            if (item.SubCategory == "Оружие компаньона")
            {
                if (item.Id.Contains("Beast", StringComparison.OrdinalIgnoreCase) || item.Name.Contains("Когт", StringComparison.OrdinalIgnoreCase))
                    return "Компаньон";
                if (item.Id.Contains("Zanuka", StringComparison.OrdinalIgnoreCase) || item.Id.Contains("Glaive", StringComparison.OrdinalIgnoreCase) || item.Name.Contains("Деконструктор", StringComparison.OrdinalIgnoreCase))
                    return "Ближний бой";
                if (item.Id.Contains("Pistol", StringComparison.OrdinalIgnoreCase) || item.Name.Contains("пистолет", StringComparison.OrdinalIgnoreCase))
                    return "Вторичное";
                return "Основное";
            }
            if (item.SubCategory.Contains("Вторич") || item.Category == "Усилитель") return "Вторичное";
            if (item.SubCategory.Contains("Ближн")) return "Ближний бой";
            return "Основное";
        }

        public List<BuilderModItem> GetCompatibleMods(BuilderPlatformItem? targetItem, bool auraOrStanceOnly, bool exilusOnly, string searchText)
        {
            string bucket = GetModBucketForItem(targetItem);
            var query = Mods.Where(m => m.Category == bucket);

            if (auraOrStanceOnly)
            {
                query = bucket == "Варфрейм" || bucket == "Рэйлджек"
                    ? query.Where(m => m.ModType == "AURA" || m.GetStat("isAura") > 0)
                    : query.Where(m => m.ModType == "STANCE");
            }
            else
            {
                query = query.Where(m => m.ModType != "AURA" && m.ModType != "STANCE" && m.GetStat("isAura") == 0);
                if (exilusOnly)
                    query = query.Where(m => m.IsExilus || m.GetStat("sp") != 0);
            }

            if (targetItem != null && !auraOrStanceOnly)
            {
                if (targetItem.Id == "BEAST_CLAWS_COMPANION" || (targetItem.SubCategory == "Оружие компаньона" && bucket == "Компаньон"))
                {
                    query = query.Where(m => string.IsNullOrEmpty(m.CompatName) ||
                        m.CompatName.Contains("Когт", StringComparison.OrdinalIgnoreCase) ||
                        m.CompatName.Equals("КОМПАНЬОН", StringComparison.OrdinalIgnoreCase) ||
                        m.CompatName.Equals("ЛЮБОЕ", StringComparison.OrdinalIgnoreCase));
                }
                else if (targetItem.SubCategory == "Компаньон")
                {
                    bool bio = IsBiologicalCompanion(targetItem);
                    bool hound = targetItem.Name.Contains("Гонч", StringComparison.OrdinalIgnoreCase) || targetItem.Id.Contains("Zanuka", StringComparison.OrdinalIgnoreCase);
                    query = query.Where(m =>
                    {
                        if (string.IsNullOrEmpty(m.CompatName) || m.CompatName.Equals("КОМПАНЬОН", StringComparison.OrdinalIgnoreCase) || m.CompatName.Equals("ЛЮБОЕ", StringComparison.OrdinalIgnoreCase))
                            return true;
                        if (m.CompatName.Contains("Когт", StringComparison.OrdinalIgnoreCase)) return false;
                        if (bio) return !m.CompatName.Contains("РОБОТОТЕХНИКА", StringComparison.OrdinalIgnoreCase) && !m.CompatName.Contains("Гонч", StringComparison.OrdinalIgnoreCase);
                        if (hound) return !m.CompatName.Contains("ЗВЕРЬ", StringComparison.OrdinalIgnoreCase);
                        return !m.CompatName.Contains("ЗВЕРЬ", StringComparison.OrdinalIgnoreCase) && !m.CompatName.Contains("Гонч", StringComparison.OrdinalIgnoreCase);
                    });
                }
            }

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                string q = searchText.Trim();
                query = query.Where(m =>
                    m.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    m.Description.Contains(q, StringComparison.OrdinalIgnoreCase));
            }

            return query.Take(120).ToList();
        }

        public List<BuilderArcaneItem> GetArcanesForSlot(BuilderPlatformItem? item, string builderCategory)
        {
            var list = new List<BuilderArcaneItem>
            {
                new BuilderArcaneItem { Id = "", Name = "— Без мистификатора —", Category = builderCategory, Description = "Нет активного эффекта" }
            };

            string targetCat = builderCategory switch
            {
                "Усилитель" or "Артефакты Тектолиста" => "Усилитель",
                _ when item?.SubCategory.Contains("Вторич") == true => "Вторичное",
                _ when item?.SubCategory.Contains("Ближн") == true => "Ближний бой",
                _ when item?.SubCategory.Contains("Основн") == true => "Основное",
                _ => "Варфрейм"
            };

            list.AddRange(Arcanes.Where(a => a.Category == targetCat));
            if (targetCat == "Усилитель")
                list.AddRange(Arcanes.Where(a => a.Category == "Оператор"));

            return list;
        }

        public static readonly List<HelminthOption> HelminthOptions = new()
        {
            new HelminthOption { Id = "none", Name = "Нет (Базовые способности)", Description = "Стандартный набор способностей без замены Гельминтом" },
            new HelminthOption { Id = "roar", Name = "Рёв Рино (Roar)", Description = "+30% × Сила к общему урону (фракционный множитель)", WeaponDamageScalePerStr = 0.30, IsMultiplicativeRoar = true },
            new HelminthOption { Id = "eclipse", Name = "Затмение Мираж (Eclipse)", Description = "+30% × Сила к урону оружия (включая Регуляторы/Экзальты)", WeaponDamageScalePerStr = 0.30 },
            new HelminthOption { Id = "nourish", Name = "Подпитка Гренделя (Nourish)", Description = "+45% × Сила Вирусного урона + реген энергии", NourishViralPerStr = 0.45 },
            new HelminthOption { Id = "wrathful", Name = "Гневный шаг (Wrathful Advance)", Description = "+100% × Сила плоского шанса крита Ближнего боя", FlatMeleeCritPerStr = 1.0 },
            new HelminthOption { Id = "pillage", Name = "Разрушающий грабёж (Pillage)", Description = "Снятие 25% × Сила брони и щитов врагов + срез статусов" }
        };

        public static readonly List<ArchonShardOption> ShardOptions = new()
        {
            new ArchonShardOption { Id = "none", Label = "Пусто", ColorHex = "#475569" },
            new ArchonShardOption { Id = "crimson_str", Label = "Багр. (+10% Сила)", ColorHex = "#EF4444", IconPath = "/Lotus/Interface/Graphics/Helminth/ArchonShards/ArchonShardAmarMythicGlow.png", StrengthBonus = 10 },
            new ArchonShardOption { Id = "crimson_dur", Label = "Багр. (+10% Длит.)", ColorHex = "#EF4444", IconPath = "/Lotus/Interface/Graphics/Helminth/ArchonShards/ArchonShardAmarMythicGlow.png", DurationBonus = 10 },
            new ArchonShardOption { Id = "crimson_sec_cc", Label = "Багр. (+25% Крит Втор.)", ColorHex = "#F87171", IconPath = "/Lotus/Interface/Graphics/Helminth/ArchonShards/ArchonShardAmarMythicGlow.png", SecondaryCritBonus = 25 },
            new ArchonShardOption { Id = "violet_melee_cd", Label = "Фиол. (+25% Крит. урон Бл.)", ColorHex = "#A855F7", IconPath = "/Lotus/Interface/Graphics/Helminth/ArchonShards/ArchonShardPurpleGlow.png", MeleeCritDmgBonus = 25 },
            new ArchonShardOption { Id = "azure_en", Label = "Лазур. (+50 Энергия)", ColorHex = "#38BDF8", IconPath = "/Lotus/Interface/Graphics/Helminth/ArchonShards/ArchonShardBorealMythicGlow.png", EnergyFlat = 50 },
            new ArchonShardOption { Id = "azure_ar", Label = "Лазур. (+150 Броня)", ColorHex = "#0EA5E9", IconPath = "/Lotus/Interface/Graphics/Helminth/ArchonShards/ArchonShardBorealMythicGlow.png", ArmorFlat = 150 },
            new ArchonShardOption { Id = "emerald_corr", Label = "Изумр. (+2 стака Коррозии)", ColorHex = "#10B981", IconPath = "/Lotus/Interface/Graphics/Helminth/ArchonShards/ArchonShardMythicGreenGlow.png", ExtraCorrosiveStacks = 2 },
            new ArchonShardOption { Id = "amber_cast", Label = "Янтар. (+25% Скор. каста)", ColorHex = "#F59E0B", IconPath = "/Lotus/Interface/Graphics/Helminth/ArchonShards/ArchonShardNiraMythicGlow.png" }
        };

        public static readonly List<EnemyFactionOption> FactionOptions = new()
        {
            new EnemyFactionOption { Type = EnemyFactionType.Grineer, Name = "Гринир (Grineer)", WeaknessHint = "Уязвимость: Коррозия +50%, Удар +50%" },
            new EnemyFactionOption { Type = EnemyFactionType.Corpus, Name = "Корпус (Corpus)", WeaknessHint = "Щиты • Уязвимость: Магнит +50%, Пронзание +50% (Токсин бьёт в HP)" },
            new EnemyFactionOption { Type = EnemyFactionType.Infested, Name = "Заражённые (Infested)", WeaknessHint = "Плоть • Уязвимость: Разрез +50%, Огонь +50%" },
            new EnemyFactionOption { Type = EnemyFactionType.Murmur, Name = "Шёпот (Murmur)", WeaknessHint = "Уязвимость: Радиация +50%, Электро +50% (-50% Вирус)" },
            new EnemyFactionOption { Type = EnemyFactionType.Sentient, Name = "Владеющие / Нармер", WeaknessHint = "Уязвимость: Холод +50%, Радиация +50% (-50% Коррозия)" }
        };

        public static readonly List<AmpPartOption> AmpScaffolds = new()
        {
            new AmpPartOption { Tier = 1, Name = "1: Пенча (Луч заряда)", AltFireDamage = 9000, AltCritChance = 14, AltCritMult = 2.0 },
            new AmpPartOption { Tier = 2, Name = "2: Шраксан (Дробовик)", AltFireDamage = 7000, AltCritChance = 20, AltCritMult = 2.0 },
            new AmpPartOption { Tier = 4, Name = "4: Фад (Рикошет-диски)", AltFireDamage = 4000, AltCritChance = 34, AltCritMult = 2.6 },
            new AmpPartOption { Tier = 7, Name = "7: Пропа (Взрыв — Мета Эйдолоны)", AltFireDamage = 9000, AltCritChance = 30, AltCritMult = 2.0 }
        };

        public static readonly List<AmpPartOption> AmpBraces = new()
        {
            new AmpPartOption { Tier = 3, Name = "3: Лорин (+12% Крит, +12% Статус)", BonusCritChance = 12, BonusStatusChance = 12 },
            new AmpPartOption { Tier = 4, Name = "4: Анспата (Реген энергии)", BonusCritChance = 0, BonusStatusChance = 0 },
            new AmpPartOption { Tier = 7, Name = "7: Цертус (+20% Шанс крита — Мета)", BonusCritChance = 20, BonusStatusChance = 0 }
        };

        public static readonly List<BuilderPresetTemplate> BuiltinPresets = new()
        {
            new BuilderPresetTemplate
            {
                Title = "Mesa Prime + Регуляторы",
                Category = "Варфрейм",
                PrimaryHint = "Mesa Prime",
                SecondaryHint = "Регуляторы Прайм",
                HelminthId = "roar",
                PrimaryMods = new[] { "Растущая мощь", "Вальс Мисы", "Усиление Умбра", "Кратковременное усиление", "Слепая ярость", "Непрерывность Прайм", "Обтекаемость", "Мимолетное мастерство", "Адаптация", "Поток Прайм" },
                SecondaryMods = new[] { "Удар осы", "Гальванизированный Двойной Ствол", "Пистолетный гамбит Прайм", "Дробитель Прайм", "Смертоносный ливень", "Конвульсия Прайм", "Чумной пистолет", "Горячий Заряд Прайм" },
                PrimaryArcane1 = "Мистическая скорость",
                PrimaryArcane2 = "Мистический мститель",
                DefaultShardId = "crimson_str",
                ShardCount = 3
            },
            new BuilderPresetTemplate
            {
                Title = "Войдриг + Аркебукс",
                Category = "Некрамех",
                PrimaryHint = "Войдриг",
                SecondaryHint = "Аркебекс",
                PrimaryMods = new[] { "Усиление Некрамеха", "Обтекаемость Некрамеха", "Непрерывность Некрамеха", "Живучесть Некрамеха", "Стальное Волокно Некрамеха", "Поток Некрамеха", "Ремонт Некрамеха", "Гнев Некрамеха" },
                SecondaryMods = new[] { "Рубиновый ствол", "Двойные патроны", "Смертоносная прицельность", "Опустошающие патроны", "Электрическая палата", "Ядовитые патроны", "Горение", "Автоматический спуск" }
            },
            new BuilderPresetTemplate
            {
                Title = "Торид Инкарнон",
                Category = "Оружие",
                PrimaryHint = "Торид",
                PrimaryMods = new[] { "Зазубрины", "Раздельный патронник", "Точечный удар", "Чутье на уязвимости", "Крио Патроны Прайм", "Пагубная сила", "Кромсание Прайм", "Злодейская акселерация" },
                WeaponArcane = "Основное Безжалостное"
            },
            new BuilderPresetTemplate
            {
                Title = "Усилитель 1-7-7 (Эйдолоны)",
                Category = "Усилитель",
                PrimaryHint = "Раплак",
                PrimaryArcane1 = "Вечный натиск",
                PrimaryArcane2 = "Вечный искоренитель"
            },
            new BuilderPresetTemplate
            {
                Title = "Амеша + Мосолон",
                Category = "Арчвинг",
                PrimaryHint = "Амеша",
                SecondaryHint = "Мосолон",
                PrimaryMods = new[] { "Усиление Арчвинга", "Гиперускоритель", "Система Реакторов", "Энергетический усилитель", "Увеличенный Щит", "Поглотитель Энергии" },
                SecondaryMods = new[] { "Рубиновый ствол", "Двойные патроны", "Смертоносная прицельность", "Опустошающие патроны", "Электрическая палата", "Ядовитые патроны" }
            },
            new BuilderPresetTemplate
            {
                Title = "Наутилус Прайм + Верглас",
                Category = "Компаньон",
                PrimaryHint = "Наутилус Прайм",
                SecondaryHint = "Верглас",
                PrimaryMods = new[] { "Усиленная связь", "Заразная связь", "Разнообразная связь", "Вакуум", "Ускоренное Отклонение" },
                SecondaryMods = new[] { "Зазубрины", "Раздельный патронник", "Точечный удар", "Чутье на уязвимости", "Крио Патроны Прайм", "Злодейская акселерация" }
            },
            new BuilderPresetTemplate
            {
                Title = "Плексус Рэйлджека MK III",
                Category = "Рэйлджек",
                PrimaryHint = "Плексус",
                SecondaryHint = "Фотор",
                PrimaryMods = new[] { "Матрица Натиска", "Гиператака", "Хищник", "Сжатие Диапазона", "Коническое Сопло", "Самонаводящийся Залп", "Воронка Бездны", "Боевые Станции" }
            }
        };
    }
}
