using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace WarfAssistant.Models
{
    public enum EnemyFactionType
    {
        Grineer,
        Corpus,
        Infested,
        Murmur,
        Sentient
    }

    public sealed class EnemyFactionOption
    {
        public EnemyFactionType Type { get; set; }
        public string Name { get; set; } = string.Empty;
        public string WeaknessHint { get; set; } = string.Empty;
        public override string ToString() => Name;
    }

    public enum ModPolarity
    {
        None,
        Madurai,   // V (AP_ATTACK)
        Vazarin,   // D (AP_DEFENSE)
        Naramon,   // — (AP_TACTIC)
        Zenurik,   // = (AP_POWER)
        Umbra      // U (AP_UMBRA)
    }

    public sealed class BuilderPlatformItem : INotifyPropertyChanged
    {
        private BitmapImage? _iconBitmap;
        public event PropertyChangedEventHandler? PropertyChanged;

        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("cat")] public string Category { get; set; } = string.Empty;
        [JsonPropertyName("sub")] public string SubCategory { get; set; } = string.Empty;
        [JsonPropertyName("icon")] public string? IconPath { get; set; }
        [JsonPropertyName("exaltedFlag")] public bool IsExalted { get; set; }

        // Suit / Frame / Mech / Companion / Railjack base stats
        [JsonPropertyName("hp")] public int Health { get; set; }
        [JsonPropertyName("sh")] public int Shield { get; set; }
        [JsonPropertyName("ar")] public int Armor { get; set; }
        [JsonPropertyName("en")] public int Energy { get; set; }
        [JsonPropertyName("sp")] public double SprintSpeed { get; set; } = 1.0;
        [JsonPropertyName("exalted")] public List<string> ExaltedIds { get; set; } = new();
        [JsonPropertyName("abs")] public List<string> Abilities { get; set; } = new();

        // Weapon / Exalted / Archgun / Amp / Turret base stats
        [JsonPropertyName("dmg")] public List<double> DamagePerShot { get; set; } = new();
        [JsonPropertyName("tot")] public double TotalDamage { get; set; }
        [JsonPropertyName("cc")] public double CritChance { get; set; }
        [JsonPropertyName("cd")] public double CritMultiplier { get; set; } = 2.0;
        [JsonPropertyName("sc")] public double StatusChance { get; set; }
        [JsonPropertyName("fr")] public double FireRate { get; set; } = 1.0;
        [JsonPropertyName("ms")] public double Multishot { get; set; } = 1.0;
        [JsonPropertyName("mag")] public int MagazineSize { get; set; }
        [JsonPropertyName("rel")] public double ReloadTime { get; set; } = 1.5;
        [JsonPropertyName("disp")] public double RivenDisposition { get; set; } = 1.0;

        [JsonIgnore] public bool HasLoadedIcon => _iconBitmap != null;
        [JsonIgnore]
        public BitmapImage? IconBitmap => IconLoaderHelper.GetOrRequest(IconPath, ref _iconBitmap, bmp =>
        {
            _iconBitmap = bmp;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconBitmap)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasLoadedIcon)));
        }, decodeWidth: 88);

        [JsonIgnore] public bool IsSirocco => Id.Contains("Sirocco", StringComparison.OrdinalIgnoreCase) || Name.Contains("Сирокко", StringComparison.OrdinalIgnoreCase);
        [JsonIgnore] public bool IsArtifact => Category == "Артефакты Тектолиста";
        public string DisplayHeader => $"{Name} [{SubCategory}]";
        public override string ToString() => Name;
        public string DispositionDots
        {
            get
            {
                int filled = RivenDisposition switch
                {
                    >= 1.31 => 5,
                    >= 1.11 => 4,
                    >= 0.90 => 3,
                    >= 0.70 => 2,
                    _ => 1
                };
                return new string('●', filled) + new string('○', 5 - filled) + $" ({RivenDisposition:F2})";
            }
        }
    }

    public sealed class BuilderModItem : INotifyPropertyChanged
    {
        private BitmapImage? _iconBitmap;
        public event PropertyChangedEventHandler? PropertyChanged;

        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("cat")] public string Category { get; set; } = string.Empty;
        [JsonPropertyName("compat")] public string CompatName { get; set; } = string.Empty;
        [JsonPropertyName("mtype")] public string ModType { get; set; } = string.Empty;
        [JsonPropertyName("pol")] public string PolarityRaw { get; set; } = "AP_ANY";
        [JsonPropertyName("rar")] public string Rarity { get; set; } = "UNCOMMON";
        [JsonPropertyName("bDrain")] public int BaseDrain { get; set; } = 2;
        [JsonPropertyName("maxRank")] public int MaxRank { get; set; } = 5;
        [JsonPropertyName("exilus")] public bool IsExilus { get; set; }
        [JsonPropertyName("icon")] public string? IconPath { get; set; }
        [JsonPropertyName("desc")] public string Description { get; set; } = string.Empty;
        [JsonPropertyName("st")] public Dictionary<string, JsonElement> Stats { get; set; } = new();

        [JsonIgnore] public bool HasLoadedIcon => _iconBitmap != null;
        [JsonIgnore]
        public BitmapImage? IconBitmap => IconLoaderHelper.GetOrRequest(IconPath, ref _iconBitmap, bmp =>
        {
            _iconBitmap = bmp;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconBitmap)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasLoadedIcon)));
        }, decodeWidth: 72);

        [JsonIgnore] public ModPolarity Polarity => ParsePolarity(PolarityRaw);
        [JsonIgnore] public string PolarityGlyph => GetPolarityGlyph(Polarity);
        [JsonIgnore] public bool IsAuraOrStance => ModType is "AURA" or "STANCE" || (Stats.TryGetValue("isAura", out var a) && a.GetDouble() > 0);
        [JsonIgnore] public int MaxDrain => BaseDrain + MaxRank;
        [JsonIgnore] public string RarityColor => Rarity switch
        {
            "LEGENDARY" => "#E2E8F0",
            "RARE" => "#FBBF24",
            "UNCOMMON" => "#38BDF8",
            _ => "#CD7F32"
        };

        public double GetStat(string key)
        {
            if (Stats.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.Number)
                return el.GetDouble();
            return 0.0;
        }

        public string GetSetTag()
        {
            if (Stats.TryGetValue("set", out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString() ?? string.Empty;
            return string.Empty;
        }

        public static ModPolarity ParsePolarity(string? raw) => raw switch
        {
            "AP_ATTACK" => ModPolarity.Madurai,
            "AP_DEFENSE" => ModPolarity.Vazarin,
            "AP_TACTIC" => ModPolarity.Naramon,
            "AP_POWER" => ModPolarity.Zenurik,
            "AP_UMBRA" => ModPolarity.Umbra,
            _ => ModPolarity.None
        };

        public static string GetPolarityGlyph(ModPolarity pol) => pol switch
        {
            ModPolarity.Madurai => "V",
            ModPolarity.Vazarin => "D",
            ModPolarity.Naramon => "—",
            ModPolarity.Zenurik => "=",
            ModPolarity.Umbra => "U",
            _ => "•"
        };
    }

    public sealed class BuilderArcaneItem : INotifyPropertyChanged
    {
        private BitmapImage? _iconBitmap;
        public event PropertyChangedEventHandler? PropertyChanged;

        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("cat")] public string Category { get; set; } = string.Empty;
        [JsonPropertyName("rar")] public string Rarity { get; set; } = "RARE";
        [JsonPropertyName("icon")] public string? IconPath { get; set; }
        [JsonPropertyName("desc")] public string Description { get; set; } = string.Empty;
        [JsonPropertyName("st")] public Dictionary<string, double> Stats { get; set; } = new();

        [JsonIgnore] public bool HasLoadedIcon => _iconBitmap != null;
        [JsonIgnore]
        public BitmapImage? IconBitmap => IconLoaderHelper.GetOrRequest(IconPath, ref _iconBitmap, bmp =>
        {
            _iconBitmap = bmp;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconBitmap)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasLoadedIcon)));
        }, decodeWidth: 64);

        public double GetStat(string key) => Stats.TryGetValue(key, out var v) ? v : 0.0;
        public override string ToString() => Name;
    }

    public sealed class BuilderModSlot : INotifyPropertyChanged
    {
        private BuilderModItem? _equippedMod;
        private ModPolarity _slotPolarity = ModPolarity.None;
        private int _rank;
        private bool _isSelected;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));

        public int SlotIndex { get; set; }
        public bool IsAuraSlot { get; set; }
        public bool IsExilusSlot { get; set; }
        public string SlotLabel => IsAuraSlot ? "АУРА / СТОЙКА" : (IsExilusSlot ? "ЭКСИЛУС" : $"СЛОТ #{SlotIndex}");

        public ModPolarity SlotPolarity
        {
            get => _slotPolarity;
            set
            {
                if (_slotPolarity != value)
                {
                    _slotPolarity = value;
                    NotifyAll();
                }
            }
        }

        private void OnEquippedModPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(BuilderModItem.IconBitmap) or nameof(BuilderModItem.HasLoadedIcon) or "")
            {
                OnPropertyChanged(nameof(ModIconBitmap));
                OnPropertyChanged(nameof(HasModIcon));
            }
        }

        public BuilderModItem? EquippedMod
        {
            get => _equippedMod;
            set
            {
                if (_equippedMod != null)
                    _equippedMod.PropertyChanged -= OnEquippedModPropertyChanged;
                _equippedMod = value;
                if (_equippedMod != null)
                    _equippedMod.PropertyChanged += OnEquippedModPropertyChanged;
                _rank = value?.MaxRank ?? 0;
                NotifyAll();
            }
        }

        public int Rank
        {
            get => _rank;
            set
            {
                int max = _equippedMod?.MaxRank ?? 0;
                int clamped = Math.Clamp(value, 0, max);
                if (_rank != clamped)
                {
                    _rank = clamped;
                    NotifyAll();
                }
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); OnPropertyChanged(nameof(BorderColor)); }
        }

        public bool HasMod => EquippedMod != null;
        public bool HasModIcon => EquippedMod?.IconPath != null;
        public BitmapImage? ModIconBitmap => EquippedMod?.IconBitmap;
        public string DisplayName => EquippedMod?.Name ?? (IsAuraSlot ? "+ Аура / Стойка" : (IsExilusSlot ? "+ Эксилус" : "+ Пустой слот"));
        public string DisplayStatShort => EquippedMod != null ? EquippedMod.Description : "Нажмите, чтобы установить мод";
        public string SlotPolarityGlyph => BuilderModItem.GetPolarityGlyph(SlotPolarity);
        public string ModPolarityGlyph => EquippedMod?.PolarityGlyph ?? "";
        public string TitleColor => EquippedMod != null ? EquippedMod.RarityColor : "#7F93AB";
        public string BorderColor => IsSelected ? "#00E5FF" : (HasMod ? EquippedMod!.RarityColor : "#202E42");

        public double RankScale => EquippedMod == null || EquippedMod.MaxRank == 0
            ? 1.0
            : (double)(Rank + 1) / (EquippedMod.MaxRank + 1);

        public int EffectiveDrain
        {
            get
            {
                if (EquippedMod == null) return 0;
                int raw = EquippedMod.BaseDrain + Rank;
                if (EquippedMod.IsAuraOrStance)
                {
                    int auraGain = raw;
                    if (SlotPolarity != ModPolarity.None && SlotPolarity == EquippedMod.Polarity) return -(auraGain * 2);
                    if (SlotPolarity != ModPolarity.None && SlotPolarity != EquippedMod.Polarity) return -(int)Math.Round(auraGain * 0.8);
                    return -auraGain;
                }
                if (SlotPolarity != ModPolarity.None && EquippedMod.Polarity == SlotPolarity)
                    return (int)Math.Ceiling(raw / 2.0);
                if (SlotPolarity != ModPolarity.None && EquippedMod.Polarity != ModPolarity.None)
                    return (int)Math.Round(raw * 1.25, MidpointRounding.AwayFromZero);
                return raw;
            }
        }

        public string DrainBadge => EquippedMod == null
            ? SlotPolarityGlyph
            : (EffectiveDrain < 0 ? $"+{-EffectiveDrain} {ModPolarityGlyph}" : $"{EffectiveDrain} {ModPolarityGlyph}");

        public string DrainColor => EquippedMod == null
            ? "#64748B"
            : (EffectiveDrain < 0 || (SlotPolarity != ModPolarity.None && EquippedMod.Polarity == SlotPolarity) ? "#10B981" :
               (SlotPolarity != ModPolarity.None && EquippedMod.Polarity != ModPolarity.None ? "#EF4444" : "#E2E8F0"));

        public string RankText => EquippedMod == null ? "" : $"{Rank}/{EquippedMod.MaxRank}";

        private void NotifyAll()
        {
            OnPropertyChanged(nameof(SlotPolarity));
            OnPropertyChanged(nameof(EquippedMod));
            OnPropertyChanged(nameof(Rank));
            OnPropertyChanged(nameof(HasMod));
            OnPropertyChanged(nameof(HasModIcon));
            OnPropertyChanged(nameof(ModIconBitmap));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(DisplayStatShort));
            OnPropertyChanged(nameof(SlotPolarityGlyph));
            OnPropertyChanged(nameof(ModPolarityGlyph));
            OnPropertyChanged(nameof(TitleColor));
            OnPropertyChanged(nameof(BorderColor));
            OnPropertyChanged(nameof(EffectiveDrain));
            OnPropertyChanged(nameof(DrainBadge));
            OnPropertyChanged(nameof(DrainColor));
            OnPropertyChanged(nameof(RankText));
        }
    }

    public sealed class HelminthOption
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public double WeaponDamageScalePerStr { get; set; }
        public bool IsMultiplicativeRoar { get; set; }
        public double NourishViralPerStr { get; set; }
        public double FlatMeleeCritPerStr { get; set; }
        public override string ToString() => Name;
    }

    public sealed class ArchonShardOption : INotifyPropertyChanged
    {
        private BitmapImage? _iconBitmap;
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string ColorHex { get; set; } = "#94A3B8";
        public string? IconPath { get; set; }
        public double StrengthBonus { get; set; }
        public double DurationBonus { get; set; }
        public double EnergyFlat { get; set; }
        public double ArmorFlat { get; set; }
        public double SecondaryCritBonus { get; set; }
        public double MeleeCritDmgBonus { get; set; }
        public int ExtraCorrosiveStacks { get; set; }

        public bool HasIcon => !string.IsNullOrEmpty(IconPath);
        public bool HasLoadedIcon => _iconBitmap != null;
        public BitmapImage? IconBitmap => IconLoaderHelper.GetOrRequest(IconPath, ref _iconBitmap, bmp =>
        {
            _iconBitmap = bmp;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconBitmap)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasLoadedIcon)));
        }, decodeWidth: 48);
    }

    public sealed class ArchonShardSlot : INotifyPropertyChanged
    {
        private ArchonShardOption _selected;
        private bool _isTauforged;

        public event PropertyChangedEventHandler? PropertyChanged;
        public int Index { get; set; }

        public ArchonShardSlot(int index, ArchonShardOption initial)
        {
            Index = index;
            _selected = initial;
        }

        public ArchonShardOption Selected
        {
            get => _selected;
            set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)); }
        }

        public bool IsTauforged
        {
            get => _isTauforged;
            set { _isTauforged = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)); }
        }

        public double Multiplier => IsTauforged ? 1.5 : 1.0;
        public string SummaryText => Selected.Id == "none"
            ? $"#{Index}: Пусто"
            : $"#{Index}: {(IsTauforged ? "Тау-" : "")}{Selected.Label}";
    }

    public sealed class AmpPartOption
    {
        public int Tier { get; set; }
        public string Name { get; set; } = string.Empty;
        public double BonusCritChance { get; set; }
        public double BonusStatusChance { get; set; }
        public double AltFireDamage { get; set; }
        public double AltCritChance { get; set; }
        public double AltCritMult { get; set; } = 2.0;
        public string Note { get; set; } = string.Empty;
        public override string ToString() => Name;
    }

    public sealed class WarframeCalculatedStats
    {
        public double StrengthPercent { get; set; } = 100;
        public double DurationPercent { get; set; } = 100;
        public double EfficiencyPercent { get; set; } = 100;
        public double RangePercent { get; set; } = 100;
        public int Health { get; set; }
        public int Shield { get; set; }
        public int Armor { get; set; }
        public int Energy { get; set; }
        public double EffectiveHp { get; set; }
        public int UmbraSetCount { get; set; }
        public int UsedCapacity { get; set; }
        public int MaxCapacity { get; set; } = 60;
        public string CapacityDisplay => $"{MaxCapacity - UsedCapacity} / {MaxCapacity}";
        public bool IsOverCapacity => UsedCapacity > MaxCapacity;

        public double ExaltedDamageBonusPercent { get; set; }
        public double RoarBonusPercent { get; set; }
        public double EclipseWeaponBonusPercent { get; set; }
        public double NourishViralPercent { get; set; }
        public double FlatMeleeCritPercent { get; set; }
        public double PrimaryFireRateBuffPercent { get; set; }
        public double SecondaryFireRateBuffPercent { get; set; }
        public double GeneralWeaponDamageBuffPercent { get; set; }
        public double MeleeDamageBuffPercent { get; set; }
        public double FlatCritBuffPercent { get; set; }
        public double SecondaryCritShardBonusPercent { get; set; }
        public double MeleeCritDmgShardBonusPercent { get; set; }
        public double AuraPrimaryDamagePercent { get; set; }
        public double AuraSecondaryDamagePercent { get; set; }
        public double AuraMeleeDamagePercent { get; set; }
        public double AuraArmorReductionPercent { get; set; }
        public double AuraShieldReductionPercent { get; set; }
        public int ExtraCorrosiveStacks { get; set; }
        public string SynergySummaryText { get; set; } = string.Empty;
    }

    public sealed class ElementDamageSlice
    {
        public string Name { get; set; } = string.Empty;
        public double Amount { get; set; }
        public double SharePercent { get; set; }
        public string ColorHex { get; set; } = "#94A3B8";
        public string Icon { get; set; } = "⚡";
    }

    public sealed class WeaponCalculatedStats
    {
        public double BaseDamage { get; set; }
        public double ModdedDamagePerPellet { get; set; }
        public double TotalDamagePerShot { get; set; }
        public double EffectiveMultishot { get; set; } = 1.0;
        public double EffectiveFireRate { get; set; } = 1.0;
        public double CritChancePercent { get; set; }
        public double CritMultiplier { get; set; } = 2.0;
        public double AverageCritMultiplier { get; set; } = 1.0;
        public double StatusChancePercent { get; set; }
        public double StatusPerSecond { get; set; }
        public int MagazineSize { get; set; }
        public double ReloadSeconds { get; set; } = 1.5;
        public double RawBurstDps { get; set; }
        public double SustainedDps { get; set; }
        public int UsedCapacity { get; set; }
        public int MaxCapacity { get; set; } = 60;
        public string CapacityDisplay => $"{MaxCapacity - UsedCapacity} / {MaxCapacity}";
        public bool IsOverCapacity => UsedCapacity > MaxCapacity;
        public string CritTierLabel => CritChancePercent switch
        {
            >= 300 => "Красный Т3+ Крит",
            >= 200 => "Красный Крит",
            >= 100 => "Оранжевый Крит",
            >= 50 => "Жёлтый Крит",
            _ => "Базовый Крит"
        };
        public string CritTierColor => CritChancePercent switch
        {
            >= 200 => "#EF4444",
            >= 100 => "#F97316",
            >= 50 => "#FACC15",
            _ => "#E2E8F0"
        };
        public double FactionMultiplier { get; set; } = 1.0;
        public double RoarFactionMultiplier { get; set; } = 1.0;
        public Dictionary<EnemyFactionType, double> FactionBaneBonuses { get; } = new();
        public double OverguardMultiplier { get; set; } = 1.0;
        public bool HasCorrosive { get; set; }
        public bool HasViral { get; set; }
        public bool HasHeat { get; set; }
        public bool HasRadiation { get; set; }
        public bool HasMagnetic { get; set; }
        public bool HasToxin { get; set; }
        public List<ElementDamageSlice> Elements { get; set; } = new();
    }

    public sealed class SavedBuildEntry
    {
        public string BuildName { get; set; } = string.Empty;
        public string Category { get; set; } = "Варфрейм";
        public string PrimaryItemId { get; set; } = string.Empty;
        public string SecondaryItemId { get; set; } = string.Empty;
        public List<string> PrimaryModIds { get; set; } = new();
        public List<string> PrimaryPolarities { get; set; } = new();
        public List<int> PrimaryRanks { get; set; } = new();
        public List<string> SecondaryModIds { get; set; } = new();
        public List<string> SecondaryPolarities { get; set; } = new();
        public List<int> SecondaryRanks { get; set; } = new();
        public string HelminthId { get; set; } = "none";
        public List<string> ShardIds { get; set; } = new();
        public List<bool> ShardTauFlags { get; set; } = new();
        public string PrimaryArcane1 { get; set; } = string.Empty;
        public string PrimaryArcane2 { get; set; } = string.Empty;
        public string WeaponArcane { get; set; } = string.Empty;
        public int ScaffoldTier { get; set; } = 7;
        public int BraceTier { get; set; } = 7;
        public ActiveBuilderRiven? ActiveRiven { get; set; }
        public int IncarnonPerkProfile { get; set; }
        public string SavedAt { get; set; } = string.Empty;
    }
}
