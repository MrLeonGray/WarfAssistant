using System;
using System.Collections.Generic;
using System.Linq;
using WarfAssistant.Models;

namespace WarfAssistant.Services
{
    /// <summary>
    /// Computes authentic Warframe stats, Elemental combinations (slot order 1..8),
    /// Exalted/Helminth/Arcane/Shard cross-synergies, and Combat Target DPS/TTK math.
    /// </summary>
    public static class BuilderSimulationEngine
    {
        public static WarframeCalculatedStats ComputeWarframeStats(
            BuilderPlatformItem? platform,
            IReadOnlyList<BuilderModSlot> slots,
            HelminthOption? helminth,
            IReadOnlyList<ArchonShardSlot> shards,
            BuilderArcaneItem? arcane1,
            BuilderArcaneItem? arcane2,
            bool hasOrokinReactor = true,
            int formaCount = 0)
        {
            var res = new WarframeCalculatedStats
            {
                MaxCapacity = (platform?.Category == "Некрамех" ? 40 : 30) * (hasOrokinReactor ? 2 : 1)
            };

            if (platform == null) return res;

            // Count Umbra set pieces for Warframe Umbra set scaling
            int umbraCount = slots.Count(s => s.HasMod && s.EquippedMod!.GetSetTag().Equals("Umbra", StringComparison.OrdinalIgnoreCase));
            res.UmbraSetCount = umbraCount;
            double umbraFactor = umbraCount switch
            {
                3 => 1.75, // 77% / 44% == 1.75
                2 => 1.25, // 55% / 44% == 1.25
                _ => 1.0
            };

            double strBonus = 0, durBonus = 0, effBonus = 0, rngBonus = 0;
            double hpBonus = 0, shBonus = 0, arBonus = 0, enBonus = 0;
            int usedDrain = 0;

                foreach (var s in slots)
                {
                    usedDrain += s.EffectiveDrain;
                    if (!s.HasMod) continue;
                    var m = s.EquippedMod!;
                    double scale = s.RankScale;
                    if (m.GetSetTag().Equals("Umbra", StringComparison.OrdinalIgnoreCase))
                        scale *= umbraFactor;

                    strBonus += m.GetStat("str") * scale;
                    durBonus += m.GetStat("dur") * scale;
                    effBonus += m.GetStat("eff") * scale;
                    rngBonus += m.GetStat("rng") * scale;
                    hpBonus += m.GetStat("hp") * scale;
                    shBonus += m.GetStat("sh") * scale;
                    arBonus += m.GetStat("ar") * scale;
                    enBonus += m.GetStat("en") * scale;
                }

                // Frame Aura & Passive weapon buffs applied cleanly to any linked weapon
                var aura = slots.FirstOrDefault(s => s.IsAuraSlot && s.HasMod)?.EquippedMod;
                if (aura != null)
                {
                    if (aura.Name.Contains("Коррозийный", StringComparison.OrdinalIgnoreCase) || aura.Name.Contains("Corrosive Projection", StringComparison.OrdinalIgnoreCase))
                        res.AuraArmorReductionPercent = 18.0;
                    else if (aura.Name.Contains("Щит", StringComparison.OrdinalIgnoreCase) || aura.Name.Contains("Shield Disruption", StringComparison.OrdinalIgnoreCase))
                        res.AuraShieldReductionPercent = 18.0;
                    else if (aura.Name.Contains("Растущая мощь", StringComparison.OrdinalIgnoreCase) || aura.Name.Contains("Growing Power", StringComparison.OrdinalIgnoreCase))
                        strBonus += 25.0;
                    else if (aura.Name.Contains("Стальной чардж", StringComparison.OrdinalIgnoreCase) || aura.Name.Contains("Steel Charge", StringComparison.OrdinalIgnoreCase))
                        res.AuraMeleeDamagePercent = 60.0;
                    else if (aura.Name.Contains("Винтов", StringComparison.OrdinalIgnoreCase) || aura.Name.Contains("Rifle Amp", StringComparison.OrdinalIgnoreCase))
                        res.AuraPrimaryDamagePercent = 27.0;
                    else if (aura.Name.Contains("Пистолет", StringComparison.OrdinalIgnoreCase) || aura.Name.Contains("Pistol Amp", StringComparison.OrdinalIgnoreCase))
                        res.AuraSecondaryDamagePercent = 27.0;
                    else if (aura.Name.Contains("Дробовик", StringComparison.OrdinalIgnoreCase))
                        res.AuraPrimaryDamagePercent = 18.0;
                }

                // Shards
                double shardEn = 0, shardAr = 0;
                foreach (var sh in shards)
                {
                    double m = sh.Multiplier;
                    strBonus += sh.Selected.StrengthBonus * m;
                    durBonus += sh.Selected.DurationBonus * m;
                    shardEn += sh.Selected.EnergyFlat * m;
                    shardAr += sh.Selected.ArmorFlat * m;
                    res.ExtraCorrosiveStacks += sh.Selected.ExtraCorrosiveStacks;
                    res.SecondaryCritShardBonusPercent += sh.Selected.SecondaryCritBonus * m;
                    res.MeleeCritDmgShardBonusPercent += sh.Selected.MeleeCritDmgBonus * m;
                }

                // Arcanes on Primary (Warframe)
                double arcaneArmor = 0;
                foreach (var arc in new[] { arcane1, arcane2 })
                {
                    if (arc == null || string.IsNullOrEmpty(arc.Id)) continue;
                    strBonus += arc.GetStat("str");
                    effBonus += arc.GetStat("eff");
                    shBonus += arc.GetStat("sh");
                    arcaneArmor += arc.GetStat("flatArmor");
                    res.FlatCritBuffPercent += arc.GetStat("flatCc");
                    double frVal = arc.GetStat("fr");
                    if (frVal > 0)
                    {
                        if (arc.Description.Contains("вторич", StringComparison.OrdinalIgnoreCase) || arc.Name.Contains("скорость", StringComparison.OrdinalIgnoreCase))
                            res.SecondaryFireRateBuffPercent += frVal;
                        else
                            res.PrimaryFireRateBuffPercent += frVal;
                    }
                    res.MeleeDamageBuffPercent += arc.GetStat("mDmg");
                    if (arc.GetStat("dmg") > 0)
                        res.GeneralWeaponDamageBuffPercent += arc.GetStat("dmg");
                }

                res.StrengthPercent = Math.Max(10.0, 100.0 + strBonus);
                res.DurationPercent = Math.Max(10.0, 100.0 + durBonus);
                res.EfficiencyPercent = Math.Clamp(100.0 + effBonus, 25.0, 175.0);
                res.RangePercent = Math.Max(10.0, 100.0 + rngBonus);

                // Rank 30 Frame/Mech HP/Shield/Energy scaling
                double baseHp30 = platform.Category == "Рэйлджек" ? platform.Health : platform.Health * 1.35;
                double baseSh30 = platform.Category == "Рэйлджек" ? platform.Shield : platform.Shield * 1.35;
                double baseEn30 = platform.Energy * 1.25;

                res.Health = (int)Math.Round(baseHp30 + platform.Health * (hpBonus / 100.0));
                res.Shield = (int)Math.Round(baseSh30 + platform.Shield * (shBonus / 100.0));
                res.Armor = (int)Math.Round(platform.Armor * (1.0 + arBonus / 100.0) + shardAr + arcaneArmor);
                res.Energy = (int)Math.Round(baseEn30 + platform.Energy * (enBonus / 100.0) + shardEn);

                double hpEhp = res.Health * (1.0 + res.Armor / 300.0);
                double shEhp = res.Shield * 2.0;
                res.EffectiveHp = Math.Round(hpEhp + shEhp);

                res.UsedCapacity = usedDrain;

                double strFactor = res.StrengthPercent / 100.0;
                bool isRegulators = platform.Name.Contains("Mesa", StringComparison.OrdinalIgnoreCase) ||
                                    platform.Name.Contains("Миса", StringComparison.OrdinalIgnoreCase);
                res.ExaltedDamageBonusPercent = Math.Round((isRegulators ? 150.0 : 100.0) * strFactor, 1);

                if (helminth != null && helminth.Id != "none")
                {
                    if (helminth.IsMultiplicativeRoar)
                        res.RoarBonusPercent = Math.Round(helminth.WeaponDamageScalePerStr * 100.0 * strFactor, 1);
                    else if (helminth.WeaponDamageScalePerStr > 0)
                        res.EclipseWeaponBonusPercent = Math.Round(helminth.WeaponDamageScalePerStr * 100.0 * strFactor, 1);

                    if (helminth.NourishViralPerStr > 0)
                        res.NourishViralPercent = Math.Round(helminth.NourishViralPerStr * 100.0 * strFactor, 1);

                    if (helminth.FlatMeleeCritPerStr > 0)
                        res.FlatMeleeCritPercent = Math.Round(helminth.FlatMeleeCritPerStr * 100.0 * strFactor, 1);
                }

                var parts = new List<string> { $"Сила {res.StrengthPercent:F0}%" };
                if (res.AuraArmorReductionPercent > 0) parts.Add($"Аура: -{res.AuraArmorReductionPercent:F0}% брони");
                if (res.AuraShieldReductionPercent > 0) parts.Add($"Аура: -{res.AuraShieldReductionPercent:F0}% щитов");
                if (res.RoarBonusPercent > 0) parts.Add($"Рёв +{res.RoarBonusPercent:F1}%");
                if (res.EclipseWeaponBonusPercent > 0) parts.Add($"Затмение +{res.EclipseWeaponBonusPercent:F1}%");
                if (res.NourishViralPercent > 0) parts.Add($"Подпитка +{res.NourishViralPercent:F0}% Вирус");
                if (res.SecondaryFireRateBuffPercent > 0 || res.PrimaryFireRateBuffPercent > 0) parts.Add($"Мистик скоростр.");
                if (res.FlatCritBuffPercent > 0) parts.Add($"Мститель +{res.FlatCritBuffPercent:F0}% крит");
                res.SynergySummaryText = string.Join(" • ", parts);

                return res;
        }

        public static WeaponCalculatedStats ComputeWeaponStats(
            BuilderPlatformItem? weapon,
            IReadOnlyList<BuilderModSlot> slots,
            BuilderArcaneItem? weaponArcane,
            WarframeCalculatedStats? linkedFrameStats,
            AmpPartOption? ampScaffold = null,
            AmpPartOption? ampBrace = null,
            bool hasCatalyst = true,
            ActiveBuilderRiven? activeRiven = null,
            double incarnonDmgBonus = 0,
            double incarnonCcBonus = 0,
            double incarnonCdBonus = 0,
            double incarnonScBonus = 0,
            double incarnonFrBonus = 0,
            double incarnonMsBonus = 0,
            bool useAmpScaffoldAltFire = true)
        {
            var stats = new WeaponCalculatedStats
            {
                MaxCapacity = (weapon?.Name.Contains("Кува", StringComparison.OrdinalIgnoreCase) == true ||
                               weapon?.Name.Contains("Догмат", StringComparison.OrdinalIgnoreCase) == true ||
                               weapon?.Name.Contains("Кода", StringComparison.OrdinalIgnoreCase) == true)
                    ? (hasCatalyst ? 80 : 40)
                    : (hasCatalyst ? 60 : 30)
            };
            if (weapon == null) return stats;

            double dmgMod = 0, msBonus = 0, ccBonus = 0, cdBonus = 0, scBonus = 0, frBonus = 0, relBonus = 0, magBonus = 0;
            double impMod = 0, punMod = 0, slaMod = 0;
            var baneBonuses = new Dictionary<EnemyFactionType, double>();
            var elementalQueue = new List<(string Elem, double Pct)>();
            int usedDrain = 0;

            bool isMelee = weapon.SubCategory.Contains("Ближ", StringComparison.OrdinalIgnoreCase);
            bool isSecondary = weapon.SubCategory.Contains("Вторич", StringComparison.OrdinalIgnoreCase) ||
                               weapon.Category == "Усилитель";
            bool isPrimary = !isMelee && !isSecondary && weapon.Category != "Артефакты Тектолиста";
            bool isAmp = weapon.Category == "Усилитель" && !weapon.IsSirocco;

            foreach (var s in slots)
            {
                usedDrain += s.EffectiveDrain;
                if (!s.HasMod) continue;
                var m = s.EquippedMod!;
                double sc = s.RankScale;

                dmgMod += (m.GetStat("dmg") + m.GetStat("mDmg")) * sc;
                msBonus += m.GetStat("ms") * sc;
                ccBonus += m.GetStat("cc") * sc;
                cdBonus += m.GetStat("cd") * sc;
                scBonus += m.GetStat("sc") * sc;
                frBonus += m.GetStat("fr") * sc;
                relBonus += m.GetStat("rel") * sc;
                magBonus += m.GetStat("mag") * sc;
                impMod += m.GetStat("imp") * sc;
                punMod += m.GetStat("pun") * sc;
                slaMod += m.GetStat("sla") * sc;

                double fVal = m.GetStat("fac") * sc;
                if (fVal > 0)
                {
                    if (m.Name.Contains("Гринир", StringComparison.OrdinalIgnoreCase) || m.Id.Contains("Grineer", StringComparison.OrdinalIgnoreCase))
                        baneBonuses[EnemyFactionType.Grineer] = Math.Max(baneBonuses.GetValueOrDefault(EnemyFactionType.Grineer), fVal);
                    else if (m.Name.Contains("Корпус", StringComparison.OrdinalIgnoreCase) || m.Id.Contains("Corpus", StringComparison.OrdinalIgnoreCase))
                        baneBonuses[EnemyFactionType.Corpus] = Math.Max(baneBonuses.GetValueOrDefault(EnemyFactionType.Corpus), fVal);
                    else if (m.Name.Contains("Зараж", StringComparison.OrdinalIgnoreCase) || m.Id.Contains("Infest", StringComparison.OrdinalIgnoreCase))
                        baneBonuses[EnemyFactionType.Infested] = Math.Max(baneBonuses.GetValueOrDefault(EnemyFactionType.Infested), fVal);
                    else if (m.Name.Contains("Шёпот", StringComparison.OrdinalIgnoreCase) || m.Id.Contains("Murmur", StringComparison.OrdinalIgnoreCase))
                        baneBonuses[EnemyFactionType.Murmur] = Math.Max(baneBonuses.GetValueOrDefault(EnemyFactionType.Murmur), fVal);
                    else if (m.Name.Contains("Владеющ", StringComparison.OrdinalIgnoreCase) || m.Id.Contains("Sentient", StringComparison.OrdinalIgnoreCase))
                        baneBonuses[EnemyFactionType.Sentient] = Math.Max(baneBonuses.GetValueOrDefault(EnemyFactionType.Sentient), fVal);
                    else
                    {
                        foreach (EnemyFactionType f in Enum.GetValues<EnemyFactionType>())
                            baneBonuses[f] = Math.Max(baneBonuses.GetValueOrDefault(f), fVal);
                    }
                }

                void AddEl(string key, string label)
                {
                    double val = m.GetStat(key) * sc;
                    if (val > 0) elementalQueue.Add((label, val));
                }
                AddEl("heat", "Огонь");
                AddEl("cold", "Холод");
                AddEl("elec", "Электро");
                AddEl("tox", "Токсин");
                AddEl("rad", "Радиация");
                AddEl("vir", "Вирус");
                AddEl("magD", "Магнит");
                AddEl("corr", "Коррозия");
            }

            // Apply linked Warframe / Aura / Exalted / Helminth / Arcane / Shard synergies (both Exalted AND Normal Weapons!)
            if (linkedFrameStats != null)
            {
                if (weapon.IsExalted)
                    dmgMod += linkedFrameStats.ExaltedDamageBonusPercent;
                if (isPrimary)
                {
                    dmgMod += linkedFrameStats.AuraPrimaryDamagePercent + linkedFrameStats.GeneralWeaponDamageBuffPercent;
                    frBonus += linkedFrameStats.PrimaryFireRateBuffPercent;
                }
                if (isSecondary)
                {
                    dmgMod += linkedFrameStats.AuraSecondaryDamagePercent + linkedFrameStats.GeneralWeaponDamageBuffPercent;
                    frBonus += linkedFrameStats.SecondaryFireRateBuffPercent;
                    ccBonus += linkedFrameStats.SecondaryCritShardBonusPercent;
                }
                if (isMelee)
                {
                    dmgMod += linkedFrameStats.AuraMeleeDamagePercent + linkedFrameStats.MeleeDamageBuffPercent + linkedFrameStats.GeneralWeaponDamageBuffPercent;
                    cdBonus += linkedFrameStats.MeleeCritDmgShardBonusPercent;
                }
                if (linkedFrameStats.NourishViralPercent > 0)
                    elementalQueue.Add(("Вирус", linkedFrameStats.NourishViralPercent));
            }

            if (weaponArcane != null && !string.IsNullOrEmpty(weaponArcane.Id))
            {
                dmgMod += weaponArcane.GetStat("dmg") + weaponArcane.GetStat("mDmg");
                ccBonus += weaponArcane.GetStat("cc");
                cdBonus += weaponArcane.GetStat("cd");
                frBonus += weaponArcane.GetStat("fr");
                if (weaponArcane.GetStat("ogMult") > 1.0)
                    stats.OverguardMultiplier = weaponArcane.GetStat("ogMult");
            }

            if (activeRiven != null && activeRiven.Stats.Count > 0)
            {
                double r(string k) => activeRiven.Stats.GetValueOrDefault(k, 0);
                dmgMod += r("dmg");
                msBonus += r("ms");
                ccBonus += r("cc");
                cdBonus += r("cd");
                scBonus += r("sc");
                frBonus += r("fr");
                relBonus += r("rel");
                magBonus += r("mag");
                if (r("fac") > 0)
                {
                    foreach (EnemyFactionType f in Enum.GetValues<EnemyFactionType>())
                        baneBonuses[f] = Math.Max(baneBonuses.GetValueOrDefault(f), r("fac"));
                }
                impMod += r("imp");
                punMod += r("pun");
                slaMod += r("sla");
                if (r("heat") > 0) elementalQueue.Add(("Огонь", r("heat")));
                if (r("cold") > 0) elementalQueue.Add(("Холод", r("cold")));
                if (r("elec") > 0) elementalQueue.Add(("Электро", r("elec")));
                if (r("tox") > 0) elementalQueue.Add(("Токсин", r("tox")));
            }

            // Base Damage breakdown from DE indices [0..14]
            double[] baseParts = new double[15];
            for (int i = 0; i < Math.Min(15, weapon.DamagePerShot.Count); i++)
                baseParts[i] = weapon.DamagePerShot[i];
            double baseSum = baseParts.Sum();
            if (isAmp && ampScaffold != null && useAmpScaffoldAltFire && ampScaffold.AltFireDamage > 0)
            {
                Array.Clear(baseParts, 0, baseParts.Length);
                baseParts[13] = ampScaffold.AltFireDamage; // Void damage
                baseSum = ampScaffold.AltFireDamage;
            }
            else if (baseSum <= 0 && weapon.TotalDamage > 0)
            {
                baseParts[0] = weapon.TotalDamage * 0.4;
                baseParts[1] = weapon.TotalDamage * 0.3;
                baseParts[2] = weapon.TotalDamage * 0.3;
                baseSum = weapon.TotalDamage;
            }
            if (incarnonDmgBonus > 0)
            {
                double scaleInc = baseSum > 0 ? (baseSum + incarnonDmgBonus) / baseSum : 1.0;
                for (int i = 0; i < baseParts.Length; i++) baseParts[i] *= scaleInc;
                baseSum += incarnonDmgBonus;
            }

            stats.BaseDamage = Math.Round(baseSum, 1);

            double baseMult = Math.Max(0.1, 1.0 + dmgMod / 100.0);
            if (linkedFrameStats != null && linkedFrameStats.EclipseWeaponBonusPercent > 0)
                baseMult *= (1.0 + linkedFrameStats.EclipseWeaponBonusPercent / 100.0);

            // Physical totals with physical mods (% applies only to respective physical base component!)
            double impDmg = baseParts[0] * baseMult * (1.0 + impMod / 100.0);
            double punDmg = baseParts[1] * baseMult * (1.0 + punMod / 100.0);
            double slaDmg = baseParts[2] * baseMult * (1.0 + slaMod / 100.0);

            // Combine single elements in Warframe slot order 1..8
            var elemBuckets = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var singleOrder = new List<(string Elem, double Amount)>();

            // Pre-combined standalone elements (e.g. Nourish Viral / Radiation mods)
            foreach (var (el, pct) in elementalQueue)
            {
                double amt = baseSum * baseMult * (pct / 100.0);
                if (el is "Радиация" or "Вирус" or "Магнит" or "Коррозия" or "Взрыв" or "Газ")
                {
                    elemBuckets[el] = elemBuckets.GetValueOrDefault(el) + amt;
                }
                else
                {
                    var existing = singleOrder.FindIndex(x => x.Elem == el);
                    if (existing >= 0) singleOrder[existing] = (el, singleOrder[existing].Amount + amt);
                    else singleOrder.Add((el, amt));
                }
            }

            // Append innate weapon elemental components last (authentic Warframe rule!)
            string[] innateNames = { "", "", "", "Огонь", "Холод", "Электро", "Токсин", "Взрыв", "Радиация", "Газ", "Магнит", "Вирус", "Коррозия", "Бездна", "Тау" };
            for (int idx = 3; idx < 15; idx++)
            {
                if (baseParts[idx] <= 0.001) continue;
                double amt = baseParts[idx] * baseMult;
                string en = innateNames[idx];
                if (idx >= 7) // pre-combined innate (Radiation, Magnetic, Viral, Void, etc.)
                {
                    elemBuckets[en] = elemBuckets.GetValueOrDefault(en) + amt;
                }
                else
                {
                    var existing = singleOrder.FindIndex(x => x.Elem == en);
                    if (existing >= 0) singleOrder[existing] = (en, singleOrder[existing].Amount + amt);
                    else singleOrder.Add((en, amt));
                }
            }

            // Pairwise combine single elements into secondary elements
            while (singleOrder.Count >= 2)
            {
                var a = singleOrder[0];
                var b = singleOrder[1];
                singleOrder.RemoveRange(0, 2);
                string combo = CombinePair(a.Elem, b.Elem);
                elemBuckets[combo] = elemBuckets.GetValueOrDefault(combo) + a.Amount + b.Amount;
            }
            if (singleOrder.Count == 1)
            {
                var rem = singleOrder[0];
                elemBuckets[rem.Elem] = elemBuckets.GetValueOrDefault(rem.Elem) + rem.Amount;
            }

            double totalPelletDmg = impDmg + punDmg + slaDmg + elemBuckets.Values.Sum();
            stats.ModdedDamagePerPellet = Math.Round(totalPelletDmg, 1);

            stats.EffectiveMultishot = Math.Max(1.0, (weapon.Multishot > 0 ? weapon.Multishot : 1.0) * (1.0 + (msBonus + incarnonMsBonus) / 100.0));
            double baseRate = (isAmp && ampScaffold != null && useAmpScaffoldAltFire) ? 1.5 : weapon.FireRate;
            stats.EffectiveFireRate = Math.Max(0.1, baseRate * Math.Max(0.2, 1.0 + (frBonus + incarnonFrBonus) / 100.0));
            if (weapon.Name.Contains("Регулятор", StringComparison.OrdinalIgnoreCase))
                stats.EffectiveFireRate = Math.Min(35.0, stats.EffectiveFireRate); // Peacemaker ramp realism

            double extraFlatCc = (linkedFrameStats?.FlatCritBuffPercent ?? 0.0) +
                                 (isMelee ? (linkedFrameStats?.FlatMeleeCritPercent ?? 0.0) : 0.0);

            double baseCcRaw = (isAmp && ampScaffold != null && useAmpScaffoldAltFire) ? ampScaffold.AltCritChance : weapon.CritChance;
            double baseCdRaw = (isAmp && ampScaffold != null && useAmpScaffoldAltFire) ? ampScaffold.AltCritMult : weapon.CritMultiplier;
            double baseCcWithBrace = baseCcRaw + incarnonCcBonus + (isAmp && ampBrace != null ? ampBrace.BonusCritChance : 0.0);
            stats.CritChancePercent = Math.Max(0.0, baseCcWithBrace * (1.0 + ccBonus / 100.0) + extraFlatCc);
            stats.CritMultiplier = Math.Max(1.0, (baseCdRaw + incarnonCdBonus) * (1.0 + cdBonus / 100.0));

            double critRatio = stats.CritChancePercent / 100.0;
            stats.AverageCritMultiplier = 1.0 + critRatio * (stats.CritMultiplier - 1.0);

            double baseSc = weapon.StatusChance + incarnonScBonus + (isAmp && ampBrace != null ? ampBrace.BonusStatusChance : 0.0);
            stats.StatusChancePercent = Math.Max(0.0, baseSc * (1.0 + scBonus / 100.0));
            stats.StatusPerSecond = Math.Round((stats.StatusChancePercent / 100.0) * stats.EffectiveMultishot * stats.EffectiveFireRate, 1);

            stats.MagazineSize = weapon.MagazineSize > 0 ? Math.Max(1, (int)Math.Round(weapon.MagazineSize * (1.0 + magBonus / 100.0))) : 0;
            stats.ReloadSeconds = weapon.ReloadTime > 0 ? Math.Round(weapon.ReloadTime / Math.Max(0.25, 1.0 + relBonus / 100.0), 2) : 0;

            double roarMult = (linkedFrameStats != null && linkedFrameStats.RoarBonusPercent > 0)
                ? (1.0 + linkedFrameStats.RoarBonusPercent / 100.0)
                : 1.0;
            stats.RoarFactionMultiplier = roarMult;
            foreach (var kvp in baneBonuses)
                stats.FactionBaneBonuses[kvp.Key] = kvp.Value;

            double avgBane = baneBonuses.Count > 0 ? (1.0 + baneBonuses.Values.Max() / 100.0) : 1.0;
            stats.FactionMultiplier = Math.Round(roarMult * avgBane, 2);

            stats.TotalDamagePerShot = Math.Round(totalPelletDmg * stats.EffectiveMultishot * stats.AverageCritMultiplier * roarMult, 0);
            stats.RawBurstDps = Math.Round(stats.TotalDamagePerShot * stats.EffectiveFireRate, 0);

            if (stats.MagazineSize > 0 && stats.ReloadSeconds > 0 && !isMelee && !weapon.IsExalted)
            {
                double fireTime = stats.MagazineSize / stats.EffectiveFireRate;
                stats.SustainedDps = Math.Round(stats.RawBurstDps * (fireTime / (fireTime + stats.ReloadSeconds)), 0);
            }
            else
            {
                stats.SustainedDps = stats.RawBurstDps;
            }

            stats.UsedCapacity = usedDrain;

            double displayScale = stats.EffectiveMultishot * stats.AverageCritMultiplier * roarMult;
            var slices = new List<ElementDamageSlice>();
            void PushSlice(string nm, double rawAmt, string col, string icon)
            {
                if (rawAmt <= 0.1) return;
                double val = Math.Round(rawAmt * displayScale, 0);
                slices.Add(new ElementDamageSlice
                {
                    Name = nm,
                    Amount = val,
                    SharePercent = totalPelletDmg > 0 ? Math.Round(rawAmt / totalPelletDmg * 100.0, 1) : 0,
                    ColorHex = col,
                    Icon = icon
                });
            }

            foreach (var kvp in elemBuckets.OrderByDescending(x => x.Value))
            {
                var (col, icon) = kvp.Key switch
                {
                    "Коррозия" => ("#10B981", "🧪"),
                    "Вирус" => ("#A855F7", "🦠"),
                    "Радиация" => ("#FACC15", "☢️"),
                    "Огонь" => ("#EF4444", "🔥"),
                    "Холод" => ("#38BDF8", "❄️"),
                    "Электро" => ("#60A5FA", "⚡"),
                    "Токсин" => ("#4ADE80", "☠️"),
                    "Магнит" => ("#818CF8", "🧲"),
                    "Взрыв" => ("#FB923C", "💥"),
                    "Газ" => ("#34D399", "💨"),
                    "Бездна" => ("#22D3EE", "🌀"),
                    _ => ("#94A3B8", "💎")
                };
                PushSlice(kvp.Key, kvp.Value, col, icon);
            }
            if (impDmg > 0.1) PushSlice("Удар", impDmg, "#94A3B8", "●");
            if (punDmg > 0.1) PushSlice("Пронзание", punDmg, "#CBD5E1", "◆");
            if (slaDmg > 0.1) PushSlice("Разрез", slaDmg, "#F87171", "⚔️");

            stats.Elements = slices;
            stats.HasCorrosive = elemBuckets.ContainsKey("Коррозия");
            stats.HasViral = elemBuckets.ContainsKey("Вирус");
            stats.HasHeat = elemBuckets.ContainsKey("Огонь");
            stats.HasRadiation = elemBuckets.ContainsKey("Радиация");
            stats.HasMagnetic = elemBuckets.ContainsKey("Магнит");
            stats.HasToxin = elemBuckets.ContainsKey("Токсин");

            return stats;
        }

        private static string CombinePair(string a, string b)
        {
            var pair = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { a, b };
            if (pair.Contains("Токсин") && pair.Contains("Холод")) return "Вирус";
            if (pair.Contains("Токсин") && pair.Contains("Электро")) return "Коррозия";
            if (pair.Contains("Токсин") && pair.Contains("Огонь")) return "Газ";
            if (pair.Contains("Огонь") && pair.Contains("Электро")) return "Радиация";
            if (pair.Contains("Огонь") && pair.Contains("Холод")) return "Взрыв";
            if (pair.Contains("Холод") && pair.Contains("Электро")) return "Магнит";
            return a;
        }

        private static double GetDamage30ElementFactor(string elemName, EnemyFactionType faction)
        {
            return faction switch
            {
                EnemyFactionType.Grineer => elemName switch { "Коррозия" or "Удар" => 1.5, _ => 1.0 },
                EnemyFactionType.Corpus => elemName switch { "Магнит" or "Пронзание" => 1.5, _ => 1.0 },
                EnemyFactionType.Infested => elemName switch { "Огонь" or "Разрез" => 1.5, _ => 1.0 },
                EnemyFactionType.Murmur => elemName switch { "Радиация" or "Электро" => 1.5, "Вирус" => 0.5, _ => 1.0 },
                EnemyFactionType.Sentient => elemName switch { "Холод" or "Радиация" => 1.5, "Коррозия" => 0.5, _ => 1.0 },
                _ => 1.0
            };
        }

        public static DamageSimulationResult SimulateTarget(
            TargetPresetType target,
            EnemyFactionType faction,
            WeaponCalculatedStats wStats,
            WarframeCalculatedStats? wfStats)
        {
            var res = new DamageSimulationResult();
            double baseBurst = Math.Max(1.0, wStats.RawBurstDps);

            // Apply Faction-specific Bane Mod (excluding already applied Roar) + Damage 3.0 Elemental Weakness weighting
            double baneBonusPct = wStats.FactionBaneBonuses.GetValueOrDefault(faction, 0);
            double baneMult = 1.0 + baneBonusPct / 100.0;

            double weightedElemFactor = 1.0;
            if (wStats.Elements.Count > 0)
            {
                double totalShare = wStats.Elements.Sum(e => e.SharePercent);
                if (totalShare > 0.1)
                {
                    weightedElemFactor = wStats.Elements.Sum(e => (e.SharePercent / totalShare) * GetDamage30ElementFactor(e.Name, faction));
                }
            }
            res.FactionWeaknessMultiplier = Math.Round(weightedElemFactor * baneMult, 2);

            double factionAdjustedBurst = baseBurst * baneMult * weightedElemFactor;

            // Aura shield & armor reductions from linked Warframe
            double auraArmorMult = 1.0 - Math.Clamp((wfStats?.AuraArmorReductionPercent ?? 0) / 100.0, 0, 0.9);
            double auraShieldMult = 1.0 - Math.Clamp((wfStats?.AuraShieldReductionPercent ?? 0) / 100.0, 0, 0.9);

            // Jade Shadows Status Armor Strip
            double statusArmorMult = 1.0;
            bool statusImmune = target is TargetPresetType.EidolonBoss or TargetPresetType.ArchonBoss;
            if (!statusImmune && wStats.HasCorrosive && wStats.StatusPerSecond > 0.4)
            {
                int maxStacks = 10 + (wfStats?.ExtraCorrosiveStacks ?? 0);
                statusArmorMult *= (maxStacks >= 14 ? 0.0 : 0.20);
            }
            if (!statusImmune && wStats.HasHeat && wStats.StatusPerSecond > 0.4)
                statusArmorMult *= 0.50;

            double viralAmp = (!statusImmune && wStats.HasViral && wStats.StatusPerSecond > 0.3) ? 2.75 : 1.0;

            res.FactionName = faction switch
            {
                EnemyFactionType.Grineer => "Гринир",
                EnemyFactionType.Corpus => "Корпус",
                EnemyFactionType.Infested => "Заражённые",
                EnemyFactionType.Murmur => "Шёпот",
                EnemyFactionType.Sentient => "Владеющие / Нармер",
                _ => "Гринир"
            };

            // Configure Base HP / Shields / Armor / Overguard dynamically from Faction + Target Type
            (double hp, double sh, double ar, double og, int lvl, string tName, string hDesc) = (target, faction) switch
            {
                (TargetPresetType.Normal, EnemyFactionType.Grineer) => (145000, 0, 1650, 0, 100, "Тяжёлый Пулемётчик Гринир", "Клонированная плоть + Броня 1650"),
                (TargetPresetType.Normal, EnemyFactionType.Corpus) => (52000, 140000, 0, 0, 100, "Техник / Солдат Корпуса", "Высокие Щиты Корпуса"),
                (TargetPresetType.Normal, EnemyFactionType.Infested) => (210000, 0, 0, 0, 100, "Древний Разрушитель (Заражённые)", "Огромный запас плоти без брони"),
                (TargetPresetType.Normal, EnemyFactionType.Murmur) => (185000, 0, 950, 0, 100, "Саксик Шёпота (Murmur)", "Каменная плоть + Броня (Уязв. Радиация)"),
                (TargetPresetType.Normal, EnemyFactionType.Sentient) => (170000, 35000, 1100, 0, 100, "Батталист Владеющих", "Щиты + Броня сплавов + Адаптация"),

                (TargetPresetType.SteelPath, EnemyFactionType.Corpus) => (380000, 950000, 0, 0, 225, "Корпус Супра-Техник (Стальной Путь)", "+250% Щиты Корпуса (+Магнит / Токсин)"),
                (TargetPresetType.SteelPath, EnemyFactionType.Infested) => (1350000, 0, 0, 0, 225, "Древний Заражённый (Стальной Путь)", "+250% HP (Огонь / Разрез +50%)"),
                (TargetPresetType.SteelPath, _) => (920000, faction == EnemyFactionType.Sentient ? 220000 : 0, 2700, 0, 225, $"Тяжёлый юнит {res.FactionName} (СП)", "+250% HP + Кап брони 2700"),

                (TargetPresetType.EximusOverguard, _) => (850000, faction == EnemyFactionType.Corpus ? 650000 : 0, faction == EnemyFactionType.Corpus ? 0 : 2700, 850000, 225, $"Эксимус {res.FactionName} (СП Overguard)", "Сверхзащита (Overguard) + Защита SP"),
                (TargetPresetType.AcolyteBoss, _) => (1600000, 450000, 2200, 0, 200, "Аколит Стального Пути (Насилие / Мания)", "Босс СП: Щиты + Броня + 4-стаковый кап статусов"),
                (TargetPresetType.DemolystBoss, _) => (2100000, faction == EnemyFactionType.Corpus ? 820000 : 0, 2700, 400000, 250, $"Демолист-Разрушитель {res.FactionName}", "Босс Сбоя: Сверхзащита + высокая броня"),
                (TargetPresetType.ArchonBoss, _) => (6500000, 0, 1500, 0, 150, "Архонт (Бореаль / Амар / Нира)", "Босс Недели: Иммунитет к части статусов + Броня"),
                (TargetPresetType.EidolonBoss, _) => (420000, 380000, 850, 0, 150, "Эйдолон Гидролист (Щиты + Синовия)", "Щиты (Усилитель Оператора) + Синовия (Радиация +50%)"),
                _ => (920000, 0, 2700, 0, 225, "Тяжёлая цель СП", "Броня + HP")
            };

            res.TargetName = tName;
            res.TargetLevel = lvl;
            res.HealthType = hDesc;
            res.Health = hp;
            res.Shield = Math.Round(sh * auraShieldMult, 0);
            res.Armor = Math.Round(ar * auraArmorMult, 0);
            res.Overguard = og;

            double effArmor = Math.Min(2700.0, res.Armor * statusArmorMult);
            // Update 36 Jade Shadows Enemy Armor DR curve: 90% * sqrt(Armor / 2700), capped at 90%
            double armorDr = effArmor > 0 ? Math.Min(0.90, 0.90 * Math.Sqrt(effArmor / 2700.0)) : 0.0;
            res.DamageReductionPercent = Math.Round(armorDr * 100.0, 1);

            double shieldMult = wStats.HasMagnetic
                ? (!statusImmune && wStats.StatusPerSecond > 0.3 ? 2.00 : 1.50)
                : 1.0;
            double shieldDps = factionAdjustedBurst * shieldMult;

            // Status DoT contribution (Slash ignores armor; Heat / Toxin DoTs scale cleanly)
            double dotDps = 0;
            if (!statusImmune && wStats.StatusPerSecond > 0.2)
            {
                double slashShare = wStats.Elements.FirstOrDefault(e => e.Name == "Разрез")?.SharePercent ?? 0;
                double heatShare = wStats.Elements.FirstOrDefault(e => e.Name == "Огонь")?.SharePercent ?? 0;
                double toxShare = wStats.Elements.FirstOrDefault(e => e.Name == "Токсин")?.SharePercent ?? 0;
                double dotTriggerRate = Math.Min(15.0, wStats.StatusPerSecond);
                dotDps += baseBurst * (slashShare / 100.0) * 0.35 * dotTriggerRate * viralAmp; // True damage bypasses armor!
                dotDps += baseBurst * (heatShare / 100.0) * 0.50 * dotTriggerRate * (1.0 - armorDr) * viralAmp;
                dotDps += baseBurst * (toxShare / 100.0) * 0.50 * dotTriggerRate * (faction == EnemyFactionType.Corpus ? 1.0 : (1.0 - armorDr)) * viralAmp;
            }

            double effectiveHpDps = Math.Max(1.0, factionAdjustedBurst * (1.0 - armorDr) * viralAmp + dotDps);

            // Corpus Toxin bypasses Shields straight to Health!
            bool toxinBypass = faction == EnemyFactionType.Corpus && wStats.HasToxin && res.Shield > 0 && target != TargetPresetType.EidolonBoss;

            res.BurstDps = Math.Round(factionAdjustedBurst, 0);
            res.ShieldDps = Math.Round(shieldDps, 0);
            res.EffectiveDps = Math.Round(effectiveHpDps, 0);
            double effectiveOgMult = Math.Max(wStats.OverguardMultiplier, wStats.HasMagnetic ? (!statusImmune && wStats.StatusPerSecond > 0.3 ? 2.25 : 1.50) : 1.0);
            res.OverguardDps = res.Overguard > 0 ? Math.Round(factionAdjustedBurst * effectiveOgMult, 0) : 0;

            double totalTtk = 0;
            if (res.Overguard > 0) totalTtk += res.Overguard / Math.Max(1.0, res.OverguardDps);
            if (res.Shield > 0 && !toxinBypass) totalTtk += res.Shield / Math.Max(1.0, shieldDps);
            totalTtk += res.Health / Math.Max(1.0, effectiveHpDps);
            res.TimeToKillSeconds = Math.Round(totalTtk, 2);

            res.BreakdownNote =
                $"Фракция: {res.FactionName} (Стихии/Фракц. множ. ×{res.FactionWeaknessMultiplier:F2}" +
                (baneBonusPct > 0 ? $", мод Погибели +{baneBonusPct:F0}%" : "") + "). " +
                (toxinBypass ? "Токсин бьёт в обход Щитов! " : (res.Shield > 0 ? $"Щиты: {res.Shield:N0}{(wStats.HasMagnetic ? $" (Магнит ×{shieldMult:F1})" : "")}. " : "")) +
                $"Эфф. броня (Jade Shadows): {effArmor:N0} ({res.DamageReductionPercent:F1}% DR)" +
                (viralAmp > 1 ? " + Вирус ×2.75" : "") +
                (dotDps > 100 ? $" + DoT {dotDps:N0}" : "") +
                (res.Overguard > 0 && effectiveOgMult > 1 ? $" + Overguard ×{effectiveOgMult:F2}" : "") + ".";

            return res;
        }
    }
}
