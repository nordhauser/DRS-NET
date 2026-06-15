using System;
using System.Linq;
using System.Collections.Generic;
using DungeonRunners.Engine;
using DungeonRunners.Data;
using DungeonRunners.Networking;
using DungeonRunners.Core;

namespace DungeonRunners.Combat
{
    /// <summary>
    /// Binary-verified damage computation from DR.exe disassembly.
    /// ALL combat values now loaded from GC files via GCDatabase.
    /// 
    /// VERIFIED FROM BINARY (Session 18 re-analysis):
    /// - Fixed32 8.8 arithmetic (shift patterns at multiple call sites)
    /// - RNG #1 at 0x59804B: Generate() % 25700              (hit roll)
    /// - RNG #2 at 0x598133: (Generate() >> 8 & 0xFF) % 100 + 1  (block roll)
    /// - RNG #3 at 0x599011: Generate() % range + min         (damage roll, HIT only)
    /// - Weapon::playAttackSound at 0x598670 consumes global Random::generator, not the weapon apply RNG
    ///
    /// MeleeWeapon::update (0x591980) per-cycle timeline (30 ticks):
    ///   Tick 29 (total-1): call 0x591B10 → Timer(6) → sends UseTarget TCP packet
    ///   Tick 10 (soundTick): call 0x598670 → Weapon::playAttackSound → global sound RNG consumed
    ///   Tick 15 (hitTick): vtable[0x128] → doHit → applyDamage → RNG #1-#3
    ///   Tick 0:            vtable[0xD8]  → attack cycle complete
    ///
    /// TOTAL ENTITY-MANAGER RNG PER SWING: 2 (miss/block) or 3 (hit), plus the MeleeWeapon::use animation RNG.
    /// Weapon::playAttackSound at 0x598670 calls Random::generator then tests (al & 0x03).
    ///
    /// DAMAGE FORMULA (Weapon::applyDamage -> Weapon::computeDamageRange):
    ///   damageLevel = raw ApplyDamageContext+0x18 ushort copied from native weapon object field +0x88
    ///   damageBonus/damageMod = native Unit cached base + weapon-class + damage-type fields
    ///   scaledDamage = (damageLevel + damageBonus) * weaponDamageMultiplier * damageMod
    ///   Apply DamageVolatility for min/max range; RNG picks value in [min, max]
    /// </summary>
    public static class DamageComputer
    {
        // ═══════════════════════════════════════════════════════════════
        // FIXED32 MATH — Binary verified from multiple call sites
        // Format: upper 24 bits integer, lower 8 bits fractional
        // ═══════════════════════════════════════════════════════════════

        public static int FromInt(int n) => n << 8;
        public static int FromFloat(float f) => (int)(f * 256f);
        public static int ToInt(int f) => f >> 8;
        public static float ToFloat(int f) => f / 256f;
        public static int FixedMul(int a, int b) => (int)(((long)a * (long)b) >> 8);

        public static readonly NativeWeaponDamageAddSlot[] NativeWeaponDamageAddSlots =
        {
            new NativeWeaponDamageAddSlot
            {
                Element = "Divine",
                DamageTypeId = 7,
                WeaponAddStats = new[] { "DIVINE_DAMAGE_WEAPON_ADD" },
                DamageBonusStats = new[] { "DIVINE_DAMAGE_BONUS", "DIVINEDAMAGEBONUS" },
                DamageModStats = new[] { "DIVINE_DAMAGE_MOD", "DIVINEDAMAGEMOD", "DIVINE_DAMAGE_PCT", "DIVINEDAMAGEPCT" }
            },
            new NativeWeaponDamageAddSlot
            {
                Element = "Fire",
                DamageTypeId = 3,
                WeaponAddStats = new[] { "FIRE_DAMAGE_WEAPON_ADD" },
                DamageBonusStats = new[] { "FIRE_DAMAGE_BONUS", "FIREDAMAGEBONUS" },
                DamageModStats = new[] { "FIRE_DAMAGE_MOD", "FIREDAMAGEMOD", "FIRE_DAMAGE_PCT", "FIREDAMAGEPCT" }
            },
            new NativeWeaponDamageAddSlot
            {
                Element = "Ice",
                DamageTypeId = 4,
                WeaponAddStats = new[] { "ICE_DAMAGE_WEAPON_ADD", "COLD_DAMAGE_WEAPON_ADD" },
                DamageBonusStats = new[] { "ICE_DAMAGE_BONUS", "ICEDAMAGEBONUS", "COLD_DAMAGE_BONUS", "COLDDAMAGEBONUS" },
                DamageModStats = new[] { "ICE_DAMAGE_MOD", "ICEDAMAGEMOD", "ICE_DAMAGE_PCT", "ICEDAMAGEPCT", "COLD_DAMAGE_MOD", "COLDDAMAGEMOD" }
            },
            new NativeWeaponDamageAddSlot
            {
                Element = "Poison",
                DamageTypeId = 5,
                WeaponAddStats = new[] { "POISON_DAMAGE_WEAPON_ADD" },
                DamageBonusStats = new[] { "POISON_DAMAGE_BONUS", "POISONDAMAGEBONUS" },
                DamageModStats = new[] { "POISON_DAMAGE_MOD", "POISONDAMAGEMOD", "POISON_DAMAGE_PCT", "POISONDAMAGEPCT" }
            },
            new NativeWeaponDamageAddSlot
            {
                Element = "Shadow",
                DamageTypeId = 6,
                WeaponAddStats = new[] { "SHADOW_DAMAGE_WEAPON_ADD" },
                DamageBonusStats = new[] { "SHADOW_DAMAGE_BONUS", "SHADOWDAMAGEBONUS" },
                DamageModStats = new[] { "SHADOW_DAMAGE_MOD", "SHADOWDAMAGEMOD", "SHADOW_DAMAGE_PCT", "SHADOWDAMAGEPCT" }
            }
        };

        /// <summary>Binary: rounds 0.5+ up, then clears fractional bits</summary>
        public static int RoundFixed32(int f)
        {
            if ((f & 0xFF) > 0x7E) f += 0x100;
            return f & ~0xFF; // clear lower 8 bits
        }

        public static int RollDamageRange(int minDmg, int maxDmg, uint raw)
        {
            int range = Math.Max(0, maxDmg - minDmg);
            return range > 0 ? (int)(raw % ((uint)range + 1u)) + minDmg : minDmg;
        }

        public static int RollSpellDamageRange(int minDmg, int maxDmg, uint raw)
        {
            int minHp = minDmg >> 8;
            int maxHp = maxDmg >> 8;
            if (minHp < 1) minHp = 1;
            if (maxHp < minHp) maxHp = minHp;

            int damageHp = minHp;
            int rangeHp = maxHp - minHp;
            if (rangeHp > 0)
                damageHp = (int)(raw % (uint)rangeHp) + minHp;

            return Math.Max(0x100, damageHp << 8);
        }

        /// <summary>
        /// Shared native Weapon::applyDamage path for player-owned and monster-owned weapon hits.
        /// Ghidra/PDB/i64 anchors: Weapon::applyDamage 0x00597E50, computeDamageRange 0x00598ED0,
        /// computeDamage 0x00598FD0. Native RNG order is hit, block, then damage only for a landed
        /// non-blocked hit; the Unit+0x88+0x44 access path aliases EntityManager+0x44, and sound RNG
        /// is intentionally outside this stream.
        /// </summary>
        public static NativeWeaponDamageResult ResolveNativeWeaponDamage(NativeWeaponDamageInput input)
        {
            var result = new NativeWeaponDamageResult();
            if (input == null)
                return result;

            result.AttackRating = input.AttackRating;
            result.DefenseRating = input.DefenseRating;
            result.AttackerLevel = input.AttackerLevel;
            result.DefenderLevel = input.DefenderLevel;
            result.BlockChance = input.BlockChance;
            result.DamageLevel = input.DamageLevel;
            result.DamageBonus = input.DamageBonus;
            result.DamageMod = input.DamageMod;
            result.WeaponClassId = input.WeaponClassId;
            result.DamageTypeId = input.DamageTypeId;
            result.WeaponDamageF32 = input.WeaponDamageF32;
            result.WeaponVolatilityF32 = input.WeaponVolatilityF32;
            result.CritThreshold = input.CritThreshold;
            result.CritDamagePercent = input.CritDamagePercent;
            result.AttackerState = input.AttackerState;

            if (input.Rng == null)
            {
                result.Type = AttackResultType.Miss;
                result.ResultName = "NO-RNG";
                return result;
            }

            result.HitThreshold = ResolveNativeHitThreshold(
                input.AttackRating,
                input.DefenseRating,
                input.AttackerLevel,
                input.DefenderLevel);

            result.HitRaw = NativeRngLedger.Generate(input.Rng, "unitOwnedCombat", $"{input.Source ?? "Weapon::applyDamage"}:hit", "Weapon::applyDamage");
            result.HitRoll = (int)(result.HitRaw % 25700u);

            result.BlockRaw = NativeRngLedger.Generate(input.Rng, "unitOwnedCombat", $"{input.Source ?? "Weapon::applyDamage"}:block", "Weapon::applyDamage");
            result.BlockRoll = (int)(((result.BlockRaw >> 8) & 0xFF) % 100) + 1;

            result.IsHit = result.HitRoll < result.HitThreshold;
            result.IsBlocked = result.IsHit && result.BlockRoll < input.BlockChance;

            if (!result.IsHit)
            {
                result.Type = AttackResultType.Miss;
                result.ResultName = "MISS";
                result.RoomRngAfter = input.Rng.CallsSinceReseed;
                return result;
            }

            if (result.IsBlocked)
            {
                result.Type = AttackResultType.Block;
                result.ResultName = "BLOCK";
                result.RoomRngAfter = input.Rng.CallsSinceReseed;
                return result;
            }

            ComputeNativeWeaponDamageRange(
                input.DamageLevel,
                input.DamageBonus,
                input.DamageMod,
                input.WeaponDamageF32,
                input.WeaponVolatilityF32,
                out int minDamage,
                out int maxDamage);
            result.MinDamageF32 = minDamage;
            result.MaxDamageF32 = maxDamage;

            result.DamageRaw = NativeRngLedger.Generate(input.Rng, "unitOwnedCombat", $"{input.Source ?? "Weapon::applyDamage"}:damage", "Weapon::computeDamage");
            int damage = RollDamageRange(minDamage, maxDamage, result.DamageRaw);

            if (input.CritThreshold > 0 && result.HitRoll < input.CritThreshold)
            {
                result.IsCritical = true;
                int critPercent = input.CritDamagePercent > 0 ? input.CritDamagePercent : 200;
                damage = Math.Max(0x100, (damage * critPercent) / 100);
            }

            result.DamageF32 = damage;
            result.DamageWire = (uint)Math.Max(1, damage);
            result.Type = result.IsCritical ? AttackResultType.Critical : AttackResultType.Hit;
            result.ResultName = result.IsCritical ? "CRIT" : "HIT";
            if (input.IncludeWeaponDamageAdds && input.AttackerState != null)
                result.DamageAdds.AddRange(ResolveNativeWeaponDamageAdds(input.AttackerState, input.WeaponDamageF32));
            result.TotalDamageWire = result.DamageWire;
            result.TotalDamageF32 = result.DamageF32;
            foreach (NativeWeaponDamageEvent add in result.DamageAdds)
            {
                result.TotalDamageWire = ClampWireAdd(result.TotalDamageWire, add.DamageWire);
                result.TotalDamageF32 = ClampIntAdd(result.TotalDamageF32, add.DamageF32);
            }
            result.RoomRngAfter = input.Rng.CallsSinceReseed;
            return result;
        }

        public static List<NativeWeaponDamageEvent> ResolveNativeWeaponDamageAdds(PlayerState state, int weaponDamageF32)
        {
            var events = new List<NativeWeaponDamageEvent>();
            if (state == null)
                return events;

            foreach (NativeWeaponDamageAddSlot slot in NativeWeaponDamageAddSlots)
            {
                int weaponAdd = GetEquipmentStat(state, slot.WeaponAddStats);
                int damageBonus = GetEquipmentStat(state, slot.DamageBonusStats);
                int damageMod = GetEquipmentStat(state, slot.DamageModStats);
                int damageF32 = ComputeNativeWeaponDamageAddF32(weaponAdd, damageBonus, damageMod, weaponDamageF32);
                if (damageF32 <= 0)
                    continue;

                events.Add(new NativeWeaponDamageEvent
                {
                    Element = slot.Element,
                    DamageTypeId = slot.DamageTypeId,
                    DamageF32 = damageF32,
                    DamageWire = (uint)damageF32,
                    WeaponAdd = weaponAdd,
                    DamageBonus = damageBonus,
                    DamageMod = damageMod,
                    WeaponDamageF32 = weaponDamageF32
                });
            }

            return events;
        }

        public static int ComputeNativeWeaponDamageAddF32(int weaponAdd, int damageBonus, int damageMod, int weaponDamageF32)
        {
            long baseF32 = (long)weaponAdd * 0x100L;
            if (baseF32 <= 0)
                return 0;

            long value = baseF32;
            value += (long)damageBonus * 0x100L;
            value += (baseF32 * ((long)damageMod << 8)) / 0x6400L;
            if (weaponDamageF32 > 0)
                value = (value * weaponDamageF32) >> 8;
            if (value <= 0)
                return 0;

            value = (value >> 8) << 8;
            if (value > int.MaxValue)
                return int.MaxValue & ~0xFF;
            return (int)value;
        }

        private static uint ClampWireAdd(uint left, uint right)
        {
            ulong sum = (ulong)left + right;
            return sum >= uint.MaxValue ? uint.MaxValue : (uint)sum;
        }

        private static int ClampIntAdd(int left, int right)
        {
            long sum = (long)left + right;
            if (sum > int.MaxValue) return int.MaxValue;
            if (sum < int.MinValue) return int.MinValue;
            return (int)sum;
        }

        public static int ResolveNativeMeleeDamageBonus(PlayerState state)
        {
            int strength = Math.Max(0, state?.Strength ?? 10);
            var gc = GCDatabase.Instance;
            float meleeDmgPerStr = gc.GetKnob("MeleeDamagePerStrength", 2.3364f);
            int bonus = Mathf.FloorToInt(meleeDmgPerStr * strength);
            return Math.Max(0, Math.Min(0xFFFF, bonus));
        }

        public static bool IsNativeRangedWeapon(PlayerState state)
        {
            if (state == null) return false;

            string weaponClass = state.WeaponClass ?? string.Empty;
            string weaponCategory = state.WeaponCategory ?? string.Empty;

            if (ContainsIgnoreCase(weaponClass, "RANGED") ||
                ContainsIgnoreCase(weaponClass, "BOW") ||
                ContainsIgnoreCase(weaponClass, "CROSSBOW") ||
                ContainsIgnoreCase(weaponClass, "GUN") ||
                ContainsIgnoreCase(weaponClass, "CANNON"))
                return true;

            if (ContainsIgnoreCase(weaponCategory, "RANGED") ||
                ContainsIgnoreCase(weaponCategory, "BOW") ||
                ContainsIgnoreCase(weaponCategory, "CROSSBOW") ||
                ContainsIgnoreCase(weaponCategory, "GUN") ||
                ContainsIgnoreCase(weaponCategory, "CANNON"))
                return true;

            if (state.WeaponUsesProjectile || state.WeaponProjectileSpeed > 0f || state.WeaponProjectileSize > 0f)
                return true;

            return state.WeaponRange > 16;
        }

        public static bool IsNativeProjectileWeapon(PlayerState state)
        {
            if (state == null) return false;
            if (state.WeaponUsesProjectile) return true;
            if (!IsNativeRangedWeapon(state)) return false;
            return state.WeaponProjectileSpeed > 0f || state.WeaponProjectileSize > 0f;
        }

        public static string ResolveNativeWeaponStatSource(PlayerState state)
        {
            return IsNativeRangedWeapon(state) ? "UnitCache/RangedDamagePerAgility" : "UnitCache/MeleeDamagePerStrength";
        }

        public static float ResolveNativeWeaponAttackSpeedPct(PlayerState state)
        {
            if (state == null) return 0f;
            return IsNativeRangedWeapon(state) ? state.RangeAttackSpeedModPercent : state.MeleeAttackSpeedModPercent;
        }

        public static int ApplyNativeAttackSpeedPctToTicks(int ticks, float pct)
        {
            if (ticks <= 0 || Math.Abs(pct) < 0.0001f) return Math.Max(0, ticks);
            double scale = 1.0d + (pct / 100.0d);
            if (scale < 0.05d) scale = 0.05d;
            int adjusted = (int)Math.Floor((ticks / scale) + 0.5d);
            return Math.Max(1, adjusted);
        }

        public static int ResolveNativeBasicAttackCooldownTicks(PlayerState state)
        {
            float cooldown = state != null ? state.WeaponCooldown : 0f;
            if (cooldown <= 0f) return 0;
            return Math.Max(0, (int)Math.Floor((cooldown * 30f) + 0.0001f));
        }

        private static int RoundPositiveToInt(float value)
        {
            if (value <= 0f) return 0;
            return (int)Math.Floor(value + 0.5f);
        }

        public static int ResolveNativeWeaponDamageBonus(PlayerState state)
        {
            if (state == null) return 0;
            int weaponClassId = ResolveNativeWeaponClassId(state);
            int damageTypeId = ResolveNativeDamageTypeId(state);
            int bonus = ResolveNativeUnitBaseDamageBonus(state);
            bonus += ResolveNativeUnitWeaponClassDamageBonus(state, weaponClassId);
            bonus += ResolveNativeUnitDamageTypeBonus(state, damageTypeId);
            return ClampNativeUShort(bonus);
        }

        private static bool ContainsIgnoreCase(string value, string needle)
        {
            return !string.IsNullOrEmpty(value) &&
                   !string.IsNullOrEmpty(needle) &&
                   value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static int ResolveNativeWeaponDamageLevel(PlayerState state)
        {
            if (state == null) return 1;
            if (state.NativeWeaponBaseDamageTracksPlayerLevel)
            {
                int trackedLevel = Math.Max(1, ClampNativeUShort(state.Level));
                state.NativeWeaponBaseDamage = trackedLevel;
                state.NativeWeaponDamageLevel = trackedLevel;
                return trackedLevel;
            }
            if (state.NativeWeaponBaseDamage > 0)
                return Math.Max(1, ClampNativeUShort(state.NativeWeaponBaseDamage));
            int level = state.NativeWeaponDamageLevel > 0
                ? state.NativeWeaponDamageLevel
                : Math.Max(1, state.WeaponLevel);
            return Math.Max(1, ClampNativeUShort(level));
        }

        public static int ResolveNativeWeaponRuntimeBaseDamageLevel(
            int playerLevel,
            int storedLevel,
            int fallbackItemLevel,
            out bool tracksPlayerLevel,
            out string source)
        {
            if (fallbackItemLevel > 0)
            {
                tracksPlayerLevel = false;
                source = storedLevel >= 0 ? "materialized-item-level" : "authored-item-level";
                return ScaleNativeWeaponDamageLevel(Math.Max(1, ClampNativeUShort(fallbackItemLevel)));
            }

            if (storedLevel >= 0)
            {
                tracksPlayerLevel = false;
                source = "materialized-item-level";
                return ScaleNativeWeaponDamageLevel(Math.Max(1, ClampNativeUShort(storedLevel)));
            }

            tracksPlayerLevel = false;
            source = "native-default-item-level";
            return ScaleNativeWeaponDamageLevel(1);
        }

        private static int ScaleNativeWeaponDamageLevel(int itemLevel)
        {
            int lvl = Math.Max(1, itemLevel);
            int wdplF32 = FromFloat(GCDatabase.Instance.GetKnob("WeaponDamagePerLevel", 10f));
            int dpsF32 = FromFloat(GCDatabase.Instance.GetKnob("DPSModifier", 1f));
            int raw = FixedMul(lvl << 8, wdplF32);
            int scaled = (int)(((long)raw * dpsF32) >> 16);
            return Math.Max(1, ClampNativeUShort(scaled));
        }

        public static void ApplyNativeWeaponRuntimeBaseDamage(
            PlayerState state,
            int playerLevel,
            int storedLevel,
            int fallbackItemLevel,
            string sourceTag)
        {
            if (state == null)
                return;

            int baseDamage = ResolveNativeWeaponRuntimeBaseDamageLevel(
                playerLevel,
                storedLevel,
                fallbackItemLevel,
                out bool tracksPlayerLevel,
                out string source);

            if (string.Equals(source, "native-default-item-level", StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogError($"[DAMAGE-LEVEL] sourceTag={sourceTag ?? "unknown"} source={source} playerLevel={playerLevel} storedLevel={storedLevel} fallbackItemLevel={fallbackItemLevel} resolved={baseDamage} native=Weapon::ComputeAttributes@0x00599240");
            }

            state.NativeWeaponBaseDamage = baseDamage;
            state.NativeWeaponBaseDamageTracksPlayerLevel = tracksPlayerLevel;
            state.NativeWeaponBaseDamageSource = string.IsNullOrEmpty(sourceTag)
                ? source
                : $"{sourceTag}:{source}";
            state.NativeWeaponDamageLevel = baseDamage;
            Debug.LogError($"[DAMAGE-LEVEL] sourceTag={sourceTag ?? "unknown"} source={source} playerLevel={playerLevel} storedLevel={storedLevel} fallbackItemLevel={fallbackItemLevel} resolved={baseDamage} tracksPlayerLevel={tracksPlayerLevel}");
        }

        public static int ResolveNativeLevelDamageBonus(PlayerState state)
        {
            return ResolveNativeWeaponDamageLevel(state);
        }

        public static int ResolveNativeDamageMod(PlayerState state)
        {
            int weaponClassId = ResolveNativeWeaponClassId(state);
            int damageTypeId = ResolveNativeDamageTypeId(state);
            int damagePct = ResolveNativeUnitBaseDamageModPct(state);
            damagePct += ResolveNativeUnitWeaponClassDamageModPct(state, weaponClassId);
            damagePct += ResolveNativeUnitDamageTypeModPct(state, damageTypeId);
            int damageModPct = 100 + damagePct;
            if (damageModPct < 0) damageModPct = 0;
            return ClampNativeUShort(damageModPct);
        }

        public static void LogNativeDamageSlots(PlayerState state, NativeWeaponDamageInput input, Monster monster, string source)
        {
            int weaponClassId = input != null ? input.WeaponClassId : ResolveNativeWeaponClassId(state);
            int damageTypeId = input != null ? input.DamageTypeId : ResolveNativeDamageTypeId(state);
            int baseBonus = ResolveNativeUnitBaseDamageBonus(state);
            int classBonus = ResolveNativeUnitWeaponClassDamageBonus(state, weaponClassId);
            int typeBonus = ResolveNativeUnitDamageTypeBonus(state, damageTypeId);
            int categoryBonus = ResolveNativeUnitWeaponCategoryDamageBonus(state);
            int baseModPct = ResolveNativeUnitBaseDamageModPct(state);
            int classModPct = ResolveNativeUnitWeaponClassDamageModPct(state, weaponClassId);
            int typeModPct = ResolveNativeUnitDamageTypeModPct(state, damageTypeId);
            int categoryModPct = ResolveNativeUnitWeaponCategoryDamageModPct(state);
            int dpsModifierF32 = FromFloat(GCDatabase.Instance.GetKnob("DPSModifier", 1f));
            string target = monster != null ? $"{monster.Name}#{monster.EntityId}" : "<none>";
            int rngBefore = input?.Rng != null ? input.Rng.CallsSinceReseed : -1;

            Debug.LogError(
                $"[DAMAGE-NATIVE-SLOTS] source={source ?? input?.Source ?? "unknown"} class={state?.ClassName ?? "<none>"} level={state?.Level ?? 0} target={target} " +
                $"weaponClass={state?.WeaponClass ?? "<none>"} weaponCategory={state?.WeaponCategory ?? "<none>"} damageType={state?.WeaponDamageType ?? "<none>"} weaponClassId={weaponClassId} damageTypeId={damageTypeId} " +
                $"nativeBaseDamage={state?.NativeWeaponBaseDamage ?? 0} nativeBaseSource={state?.NativeWeaponBaseDamageSource ?? "<none>"} damageLevel={input?.DamageLevel ?? ResolveNativeWeaponDamageLevel(state)} " +
                $"bonusBase={baseBonus} bonusClass={classBonus} bonusType={typeBonus} bonusCategoryAudit={categoryBonus} bonusTotal={input?.DamageBonus ?? ResolveNativeWeaponDamageBonus(state)} " +
                $"modBasePct={baseModPct} modClassPct={classModPct} modTypePct={typeModPct} modCategoryAudit={categoryModPct} dpsModifierF32={dpsModifierF32} damageMod={input?.DamageMod ?? ResolveNativeDamageMod(state)} " +
                $"weaponDamageF32={input?.WeaponDamageF32 ?? GetWeaponBaseDamageF32(state)} volatilityF32={input?.WeaponVolatilityF32 ?? GetWeaponVolatilityF32(state)} critThreshold={input?.CritThreshold ?? ResolveNativeCriticalThreshold(state, monster)} critPct={input?.CritDamagePercent ?? ResolveNativeCriticalDamagePercent(state)} rngBefore={rngBefore}");
        }

        private static float ResolveNativeClassDamageStatMod(PlayerState state, string descKey, float fallback)
        {
            string className = state?.ClassName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(className))
            {
                Debug.LogError($"[DAMAGE-CLASS-MOD] reason=missing-class-name descKey={descKey} value={fallback} source=native-default native=HeroDesc-class-stat-default");
                return fallback;
            }

            string resolvedClassBase = null;
            var node = ResolveClassBaseNode(className, out resolvedClassBase);
            if (node == null)
            {
                Debug.LogError($"[DAMAGE-CLASS-MOD] reason=missing-class-node class={className} descKey={descKey} value={fallback} source=native-default native=HeroDesc-class-stat-default");
                return fallback;
            }
            var desc = node?.GetChild("Description") ?? node;
            if (desc == null)
            {
                Debug.LogError($"[DAMAGE-CLASS-MOD] reason=missing-class-desc classBase={resolvedClassBase} descKey={descKey} value={fallback} source=native-default native=HeroDesc-class-stat-default");
                return fallback;
            }
            if (!desc.HasProperty(descKey))
            {
                Debug.LogError($"[DAMAGE-CLASS-MOD] reason=missing-property classBase={resolvedClassBase} descKey={descKey} value={fallback} source=native-default native=HeroDesc-class-stat-default");
                return fallback;
            }
            float value = desc.GetFloat(descKey, fallback);
            Debug.LogError($"[DAMAGE-CLASS-MOD] classBase={resolvedClassBase} className={className} descKey={descKey} value={value} source=gc");
            return value;
        }

        private static GCNode ResolveClassBaseNode(string className, out string resolvedClassBase)
        {
            resolvedClassBase = null;
            if (string.IsNullOrWhiteSpace(className) || GCDatabase.Instance == null)
                return null;

            string classBase = className.Trim();
            if (!classBase.EndsWith("Base", StringComparison.OrdinalIgnoreCase))
                classBase += "Base";

            var candidates = new List<string> { classBase, $"avatar.classes.{classBase}" };
            if (classBase.Equals("MageBase", StringComparison.OrdinalIgnoreCase) ||
                classBase.Equals("WarlockBase", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add("WarlockBase");
                candidates.Add("avatar.classes.WarlockBase");
            }

            foreach (string candidate in candidates)
            {
                var node = GCDatabase.Instance.ResolveWithInheritance(candidate);
                if (node == null)
                    continue;
                resolvedClassBase = candidate;
                return node;
            }

            return null;
        }

        public static int ResolveNativeWeaponClassId(PlayerState state)
        {
            if (state != null && state.NativeWeaponClassId > 0 && state.WeaponStatsResolved)
                return state.NativeWeaponClassId;
            return ResolveNativeWeaponClassId(state?.WeaponClass);
        }

        public static int ResolveNativeWeaponClassId(string weaponClass)
        {
            string value = CanonicalStatName(weaponClass);
            if (TryResolveNativeWeaponClassId(value, out int weaponClassId))
                return weaponClassId;
            return LogUnknownWeaponClass(value);
        }

        public static bool TryResolveNativeWeaponClassId(string weaponClass, out int weaponClassId)
        {
            string value = CanonicalStatName(weaponClass);
            weaponClassId = 0;
            if (string.IsNullOrEmpty(value)) return false;
            return value switch
            {
                "HTH" => AssignNativeWeaponClass(1, out weaponClassId),
                "2HRANGED" => AssignNativeWeaponClass(3, out weaponClassId),
                "1HMELEE" or "1HSTAFF" or "1HMACE" or "1HSWORD" or "1HAXE" => AssignNativeWeaponClass(5, out weaponClassId),
                "2HMELEE" or "2HMACE" or "2HSWORD" or "2HAXE" => AssignNativeWeaponClass(6, out weaponClassId),
                "POLEARM" => AssignNativeWeaponClass(8, out weaponClassId),
                "1HRANGED" or "1HCROSSBOW" or "1HBOW" or "1HGUN" => AssignNativeWeaponClass(9, out weaponClassId),
                "2HCANNON" => AssignNativeWeaponClass(13, out weaponClassId),
                "2HCROSSBOW" or "2HBOW" or "2HGUN" => AssignNativeWeaponClass(3, out weaponClassId),
                _ => false
            };
        }

        private static bool AssignNativeWeaponClass(int value, out int weaponClassId)
        {
            weaponClassId = value;
            return true;
        }

        private static int LogUnknownWeaponClass(string value)
        {
            RuntimeEvidenceManager.LogFallbackHit("damage-weapon-class", "unknown", $"weaponClass={value ?? "<null>"} native=blocked compatibility=none", 64);
            return 0;
        }

        public static int ResolveNativeDamageTypeId(PlayerState state)
        {
            if (state != null && state.NativeDamageTypeId >= 0 && state.WeaponStatsResolved)
                return state.NativeDamageTypeId;
            return ResolveNativeDamageTypeId(state?.WeaponDamageType);
        }

        public static int ResolveNativeDamageTypeId(string damageType)
        {
            string value = CanonicalStatName(damageType);
            if (TryResolveNativeDamageTypeId(value, out int damageTypeId))
                return damageTypeId;
            return LogUnknownDamageType(value);
        }

        public static bool TryResolveNativeDamageTypeId(string damageType, out int damageTypeId)
        {
            string value = CanonicalStatName(damageType);
            damageTypeId = -1;
            if (string.IsNullOrEmpty(value)) return false;
            return value switch
            {
                "CRUSHING" => AssignNativeDamageType(0, out damageTypeId),
                "PIERCING" => AssignNativeDamageType(1, out damageTypeId),
                "SLASHING" => AssignNativeDamageType(2, out damageTypeId),
                "FIRE" => AssignNativeDamageType(3, out damageTypeId),
                "ICE" => AssignNativeDamageType(4, out damageTypeId),
                "POISON" => AssignNativeDamageType(5, out damageTypeId),
                "SHADOW" => AssignNativeDamageType(6, out damageTypeId),
                "DIVINE" => AssignNativeDamageType(7, out damageTypeId),
                _ => false
            };
        }

        private static bool AssignNativeDamageType(int value, out int damageTypeId)
        {
            damageTypeId = value;
            return true;
        }

        private static int LogUnknownDamageType(string value)
        {
            RuntimeEvidenceManager.LogFallbackHit("damage-type", "unknown", $"damageType={value ?? "<null>"} native=blocked compatibility=none", 64);
            return -1;
        }

        public static int ResolveNativeDamageTypeId(DamageElement element)
        {
            return element switch
            {
                DamageElement.PHYSICAL => 0,
                DamageElement.FIRE => 3,
                DamageElement.ICE => 4,
                DamageElement.POISON => 5,
                DamageElement.SHADOW => 6,
                DamageElement.DIVINE => 7,
                _ => LogUnknownDamageElement(element)
            };
        }

        private static int LogUnknownDamageElement(DamageElement element)
        {
            RuntimeEvidenceManager.LogFallbackHit("damage-type", "unknown-damage-element", $"element={element} native=blocked compatibility=none", 64);
            return -1;
        }

        public static int ResolveNativeWeaponComputedDamageLevel(int weaponLevel)
        {
            return Math.Max(1, ClampNativeUShort(weaponLevel));
        }

        private static int ResolveNativeUnitBaseDamageBonus(PlayerState state)
        {
            return GetEquipmentStat(state, "DAMAGE_BONUS", "DAMAGEBONUS");
        }

        private static int ResolveNativeUnitWeaponClassDamageBonus(PlayerState state, int weaponClassId)
        {
            switch (weaponClassId)
            {
                case 1:
                    return ResolveNativeUnitMeleeCommonDamageBonus(state);
                case 3:
                case 9:
                case 13:
                    return ResolveNativeUnitRangedDamageBonus(state);
                case 5:
                    return ResolveNativeUnitMelee1HDamageBonus(state) +
                           ResolveNativeUnitMeleeCommonDamageBonus(state);
                case 6:
                case 8:
                    return ResolveNativeUnitMelee2HDamageBonus(state) +
                           ResolveNativeUnitMeleeCommonDamageBonus(state);
                default:
                    RuntimeEvidenceManager.LogFallbackHit("damage-weapon-class", "bonus-unresolved", $"weaponClass={state?.WeaponClass ?? "<null>"} weaponClassId={weaponClassId}", 64);
                    return 0;
            }
        }

        private static int ResolveNativeUnitRangedDamageBonus(PlayerState state)
        {
            int agility = Math.Max(0, state?.Agility ?? 10);
            float rangedDmgPerAgi = GCDatabase.Instance.GetKnob("RangedDamagePerAgility", 2.124f);
            float rangedDmgMod = ResolveNativeClassDamageStatMod(state, "RangedDamagePerAgilityMod", 1f);
            int bonus = Mathf.FloorToInt(rangedDmgPerAgi * agility * rangedDmgMod);
            bonus += GetEquipmentStat(state, "RANGE_DAMAGE_BONUS", "RANGED_DAMAGE_BONUS", "RANGEDAMAGEBONUS", "RANGEDDAMAGEBONUS");
            return bonus;
        }

        private static int ResolveNativeUnitMeleeCommonDamageBonus(PlayerState state)
        {
            int strength = Math.Max(0, state?.Strength ?? 10);
            float meleeDmgPerStr = GCDatabase.Instance.GetKnob("MeleeDamagePerStrength", 2.3364f);
            float meleeDmgMod = ResolveNativeClassDamageStatMod(state, "MeleeDamagePerStrengthMod", 1f);
            int bonus = Mathf.FloorToInt(meleeDmgPerStr * strength * meleeDmgMod);
            bonus += GetEquipmentStat(state, "MELEE_DAMAGE_BONUS", "MELEEDAMAGEBONUS");
            return bonus;
        }

        private static int ResolveNativeUnitMelee1HDamageBonus(PlayerState state)
        {
            return GetEquipmentStat(state, "MELEE1H_DAMAGE_BONUS", "1HMELEE_DAMAGE_BONUS", "MELEE_1H_DAMAGE_BONUS", "MELEE1HDAMAGEBONUS");
        }

        private static int ResolveNativeUnitMelee2HDamageBonus(PlayerState state)
        {
            return GetEquipmentStat(state, "MELEE2H_DAMAGE_BONUS", "2HMELEE_DAMAGE_BONUS", "MELEE_2H_DAMAGE_BONUS", "MELEE2HDAMAGEBONUS");
        }

        private static int ResolveNativeUnitDamageTypeBonus(PlayerState state, int damageTypeId)
        {
            return damageTypeId switch
            {
                0 => GetEquipmentStat(state, "CRUSHING_DAMAGE_BONUS", "CRUSHINGDAMAGEBONUS"),
                1 => GetEquipmentStat(state, "PIERCING_DAMAGE_BONUS", "PIERCINGDAMAGEBONUS"),
                2 => GetEquipmentStat(state, "SLASHING_DAMAGE_BONUS", "SLASHINGDAMAGEBONUS"),
                3 => GetEquipmentStat(state, "FIRE_DAMAGE_BONUS", "FIREDAMAGEBONUS"),
                4 => GetEquipmentStat(state, "ICE_DAMAGE_BONUS", "ICEDAMAGEBONUS", "COLD_DAMAGE_BONUS", "COLDDAMAGEBONUS"),
                5 => GetEquipmentStat(state, "POISON_DAMAGE_BONUS", "POISONDAMAGEBONUS"),
                6 => GetEquipmentStat(state, "SHADOW_DAMAGE_BONUS", "SHADOWDAMAGEBONUS"),
                7 => GetEquipmentStat(state, "DIVINE_DAMAGE_BONUS", "DIVINEDAMAGEBONUS"),
                _ => 0
            };
        }

        private static int ResolveNativeUnitWeaponCategoryDamageBonus(PlayerState state)
        {
            string category = CanonicalStatName(state?.WeaponCategory);
            if (string.IsNullOrEmpty(category)) return 0;
            return GetEquipmentStat(state, category + "_DAMAGE_BONUS", category + "DAMAGEBONUS");
        }

        private static int ResolveNativeUnitBaseDamageModPct(PlayerState state)
        {
            return GetEquipmentStat(state, "DAMAGE_MOD", "DAMAGEMOD", "DAMAGE_PCT", "DAMAGEPCT");
        }

        private static int ResolveNativeUnitWeaponClassDamageModPct(PlayerState state, int weaponClassId)
        {
            switch (weaponClassId)
            {
                case 1:
                    return ResolveNativeUnitMeleeCommonDamageModPct(state);
                case 3:
                case 9:
                case 13:
                    return ResolveNativeUnitRangedDamageModPct(state);
                case 5:
                    return ResolveNativeUnitMelee1HDamageModPct(state) +
                           ResolveNativeUnitMeleeCommonDamageModPct(state);
                case 6:
                case 8:
                    return ResolveNativeUnitMelee2HDamageModPct(state) +
                           ResolveNativeUnitMeleeCommonDamageModPct(state);
                default:
                    RuntimeEvidenceManager.LogFallbackHit("damage-weapon-class", "mod-unresolved", $"weaponClass={state?.WeaponClass ?? "<null>"} weaponClassId={weaponClassId}", 64);
                    return 0;
            }
        }

        private static int ResolveNativeUnitRangedDamageModPct(PlayerState state)
        {
            return GetEquipmentStat(state, "RANGE_DAMAGE_MOD", "RANGED_DAMAGE_MOD", "RANGEDAMAGEMOD", "RANGEDDAMAGEMOD");
        }

        private static int ResolveNativeUnitMeleeCommonDamageModPct(PlayerState state)
        {
            return GetEquipmentStat(state, "MELEE_DAMAGE_MOD", "MELEEDAMAGEMOD");
        }

        private static int ResolveNativeUnitMelee1HDamageModPct(PlayerState state)
        {
            return GetEquipmentStat(state, "MELEE1H_DAMAGE_MOD", "1HMELEE_DAMAGE_MOD", "MELEE_1H_DAMAGE_MOD", "MELEE1HDAMAGEMOD");
        }

        private static int ResolveNativeUnitMelee2HDamageModPct(PlayerState state)
        {
            return GetEquipmentStat(state, "MELEE2H_DAMAGE_MOD", "2HMELEE_DAMAGE_MOD", "MELEE_2H_DAMAGE_MOD", "MELEE2HDAMAGEMOD");
        }

        private static int ResolveNativeUnitDamageTypeModPct(PlayerState state, int damageTypeId)
        {
            return damageTypeId switch
            {
                0 => GetEquipmentStat(state, "CRUSHING_DAMAGE_MOD", "CRUSHINGDAMAGEMOD", "CRUSHING_DAMAGE_PCT", "CRUSHINGDAMAGEPCT"),
                1 => GetEquipmentStat(state, "PIERCING_DAMAGE_MOD", "PIERCINGDAMAGEMOD", "PIERCING_DAMAGE_PCT", "PIERCINGDAMAGEPCT"),
                2 => GetEquipmentStat(state, "SLASHING_DAMAGE_MOD", "SLASHINGDAMAGEMOD", "SLASHING_DAMAGE_PCT", "SLASHINGDAMAGEPCT"),
                3 => GetEquipmentStat(state, "FIRE_DAMAGE_MOD", "FIREDAMAGEMOD", "FIRE_DAMAGE_PCT", "FIREDAMAGEPCT"),
                4 => GetEquipmentStat(state, "ICE_DAMAGE_MOD", "ICEDAMAGEMOD", "ICE_DAMAGE_PCT", "ICEDAMAGEPCT", "COLD_DAMAGE_MOD", "COLDDAMAGEMOD"),
                5 => GetEquipmentStat(state, "POISON_DAMAGE_MOD", "POISONDAMAGEMOD", "POISON_DAMAGE_PCT", "POISONDAMAGEPCT"),
                6 => GetEquipmentStat(state, "SHADOW_DAMAGE_MOD", "SHADOWDAMAGEMOD", "SHADOW_DAMAGE_PCT", "SHADOWDAMAGEPCT"),
                7 => GetEquipmentStat(state, "DIVINE_DAMAGE_MOD", "DIVINEDAMAGEMOD", "DIVINE_DAMAGE_PCT", "DIVINEDAMAGEPCT"),
                _ => 0
            };
        }

        private static int ResolveNativeUnitWeaponCategoryDamageModPct(PlayerState state)
        {
            string category = CanonicalStatName(state?.WeaponCategory);
            if (string.IsNullOrEmpty(category)) return 0;
            return GetEquipmentStat(state, category + "_DAMAGE_MOD", category + "DAMAGEMOD", category + "_DAMAGE_PCT", category + "DAMAGEPCT");
        }

        private static int ClampNativeUShort(int value)
        {
            return Math.Max(0, Math.Min(0xFFFF, value));
        }

        private static int GetEquipmentStat(PlayerState state, params string[] names)
        {
            if (state?.EquipmentStats == null || state.EquipmentStats.Count == 0 || names == null) return 0;
            int total = 0;
            foreach (var kvp in state.EquipmentStats)
            {
                string key = CanonicalStatName(kvp.Key);
                for (int i = 0; i < names.Length; i++)
                {
                    if (key == CanonicalStatName(names[i]))
                    {
                        total += kvp.Value;
                        break;
                    }
                }
            }
            return total;
        }

        private static string CanonicalStatName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return value.Replace("_", string.Empty).Replace(" ", string.Empty).Trim().ToUpperInvariant();
        }

        public static int ResolveNativeCriticalThreshold(PlayerState state, Monster defender)
        {
            int baseCrit = Mathf.FloorToInt(GCDatabase.Instance.GetKnob("HeroCriticalChance", 3f));
            int threshold = baseCrit << 8;
            int attackerLevel = Math.Max(0, state?.Level ?? 1);
            int defenderLevel = Math.Max(0, defender?.Level ?? attackerLevel);
            int levelDelta = attackerLevel - defenderLevel;
            if (levelDelta > 0)
                threshold += levelDelta * 0x500;
            if (threshold > 0x5A00) threshold = 0x5A00;
            if (threshold < 0) threshold = 0;
            return threshold;
        }

        public static int ResolveNativeCriticalDamagePercent(PlayerState state)
        {
            return 200;
        }

        public static int ResolveNativeMonsterCriticalThreshold(Monster attacker, PlayerState defender)
        {
            int baseCrit = MonsterUnitStatsBuilder.ComputeBaseCriticalChance(attacker?.CritChance ?? 0f);
            if (baseCrit <= 0) return 0;
            int threshold = baseCrit << 8;
            int attackerLevel = attacker != null ? attacker.Level : 1;
            int defenderLevel = defender != null ? defender.Level : attackerLevel;
            int levelDelta = attackerLevel - defenderLevel;
            if (levelDelta > 0)
                threshold += levelDelta * 0x500;
            if (threshold > 0x5A00) threshold = 0x5A00;
            if (threshold < 0) threshold = 0;
            return threshold;
        }

        public static int ResolveNativeAvatarAttackRating(PlayerState state)
        {
            if (state == null) return 0;
            float attackPerAgility = GCDatabase.Instance.GetKnob("AttackRatingPerAgility", 14f);
            int baseRating = Math.Max(0, Mathf.RoundToInt(state.Agility * attackPerAgility));
            int modPercent = IsNativeRangedWeapon(state) ? 0 : Math.Max(-100, state.MeleeAttackRatingModPercent);
            return Math.Max(0, (baseRating * (100 + modPercent)) / 100);
        }

        public static int ResolveNativeMonsterDefenseRating(Monster monster)
        {
            return ResolveNativeMonsterCurveRating("MonsterDefenseRating", monster);
        }

        public static int ResolveNativeMonsterAttackRating(Monster monster)
        {
            return ResolveNativeMonsterCurveRating("MonsterAttackRating", monster, true);
        }

        private static int ResolveNativeMonsterCurveRating(string curveName, Monster monster, bool attack = false)
        {
            if (monster == null) return 0;
            float authored = attack ? monster.AttackRating : monster.DefenseRating;
            int authoredF32 = NativeFixed32FromAuthoredDecimal(Mathf.Max(0f, authored));
            if (authoredF32 <= 0) return 0;
            int tableF32 = GCDatabase.Instance.RequireCurveValueFixed32(curveName, Mathf.Clamp(monster.Level, 1, 110));
            int rating = (int)(((long)authoredF32 * tableF32) >> 16);
            return (ushort)rating;
        }

        public static int ResolveNativeHitThreshold(int attackRating, int defenseRating, int attackerLevel, int defenderLevel)
        {
            int attack = Math.Max(0, attackRating);
            int defense = Math.Max(0, defenseRating);
            int chancePercent = attack + defense == 0 ? 0 : (attack * 100) / (attack + defense);
            int threshold = chancePercent << 8;
            int levelDelta = Math.Max(0, Math.Min(110, defenderLevel)) - Math.Max(0, Math.Min(110, attackerLevel));
            threshold -= levelDelta * 0x500;
            if (threshold < 0x0A00) threshold = 0x0A00;
            return threshold;
        }

        public static void ComputeNativeWeaponDamageRange(int damageLevel, int damageBonus, int damageMod, int weaponDamageF32, int volatilityF32, out int minDamage, out int maxDamage)
        {
            if (damageLevel < 0) damageLevel = 0;
            if (damageBonus < 0) damageBonus = 0;
            if (damageMod < 0) damageMod = 0;
            if (weaponDamageF32 <= 0) weaponDamageF32 = 0x100;
            if (volatilityF32 < 0) volatilityF32 = 0;

            int normalized = FixedMul((damageLevel + damageBonus) << 8, weaponDamageF32);
            normalized = (int)(((long)normalized * ((long)damageMod << 8)) / 0x6400L);
            if (normalized < 0x100) normalized = 0x100;

            int spread = FixedMul(normalized, volatilityF32);
            minDamage = RoundFixed32(normalized - spread);
            maxDamage = RoundFixed32(normalized + spread);
            if (minDamage < 0x100) minDamage = 0x100;
            if (maxDamage < 0x100) maxDamage = 0x100;
            minDamage = (minDamage >> 8) << 8;
            maxDamage = (maxDamage >> 8) << 8;
            if (maxDamage < minDamage) maxDamage = minDamage;
        }

        /// <summary>
        /// Get weapon damage multiplier from PlayerState.WeaponDamage (loaded from weapons.json).
        /// This is a MULTIPLIER (e.g. 1.0), not absolute damage.
        /// The actual damage comes from: (WeaponDamagePerLevel * level + MeleeDamagePerStrength * str) * this
        /// </summary>
        public static int GetWeaponBaseDamageF32(PlayerState state)
        {
            // WeaponDamage loaded from weapons.json at spawn/pickup (e.g. 1.0 for base sword)
            int f32 = NativeFixed32FromAuthoredDecimal(state.WeaponDamage);
            Debug.LogError($"[DMG] Weapon multiplier: {state.WeaponDamage} (F32: 0x{f32:X})");
            return f32;
        }

        /// <summary>
        /// Get weapon volatility from GC data.
        /// MeleeUnitWeapon.gc: DamageVolatility = 0.5
        /// Base1HMelee.gc: inherits from MeleeWeapon (DamageVolatility not set → use default 0.5)
        /// </summary>
        public static int GetWeaponVolatilityF32(PlayerState state)
        {
            float vol = state != null ? state.WeaponDamageVolatility : 0.5f;
            if (state == null || !state.WeaponStatsResolved)
            {
                RuntimeEvidenceManager.LogFallbackHit(
                    "damage-volatility",
                    "unresolved-player-weapon",
                    $"class={state?.WeaponClass ?? "<null>"} volatility={vol:F3}",
                    64);
            }
            vol = Mathf.Clamp(vol, 0f, 0.95f);
            return NativeFixed32FromAuthoredDecimal(vol);
        }

        public static int NativeFixed32FromAuthoredDecimal(float value)
        {
            return Mathf.CeilToInt(value * 256f);
        }

        // ═══════════════════════════════════════════════════════════════
        // SPELL DAMAGE — Uses SkillDamagePerLevel + SkillDamagePerIntellect
        //
        // Formula from GC files (mirrors melee but different knobs):
        //   baseDamage = (SkillDamagePerLevel(15) × level) + (SkillDamagePerIntellect(1.5) × Intellect)
        //   scaled = baseDamage × spell.DamageMod
        //   spread = scaled × spell.DamageVolatility
        //   damage = RNG in [scaled - spread, scaled + spread]
        //
        // Spells fire INSTANTLY on UseTarget (no weapon cycle tick delay).
        // Client sends 0x50 with flags=101 for spells vs flags=10 for melee.
        // ═══════════════════════════════════════════════════════════════

        // GC Avatar base knobs for spell damage — wired to server.cfg
        public static float GC_SKILL_DAMAGE_PER_LEVEL => GCDatabase.Instance.GetKnob("SkillDamagePerLevel", 15f);
        public static float GC_SKILL_DAMAGE_PER_INTELLECT => GCDatabase.Instance.GetKnob("SkillDamagePerIntellect", 1.5f);

        /// <summary>
        /// Compute spell damage range using GC formula.
        /// Same Fixed32 math as melee but with spell-specific knobs.
        /// </summary>
        public static void ComputeSpellDamageRange(
            int attackerLevel,
            int attackerIntellect,
            float spellDamageMod,
            float spellDamageVolatility,
            out int minDamage, out int maxDamage)
        {
            // Formula: baseDamage = (SkillDmgPerLevel × level) + (SkillDmgPerIntellect × Intellect)
            float levelComponent = GC_SKILL_DAMAGE_PER_LEVEL * attackerLevel;
            float intellectComponent = GC_SKILL_DAMAGE_PER_INTELLECT * attackerIntellect;
            float baseDamage = levelComponent + intellectComponent;

            int baseDmgF32 = FromFloat(baseDamage);
            int spellModF32 = FromFloat(spellDamageMod);
            int normalized = FixedMul(baseDmgF32, spellModF32);

            if (normalized < 0x100) normalized = 0x100;

            int volF32 = FromFloat(spellDamageVolatility);
            int spread = FixedMul(normalized, volF32);

            minDamage = normalized - spread;
            maxDamage = normalized + spread;

            minDamage = RoundFixed32(minDamage);
            maxDamage = RoundFixed32(maxDamage);
            if (minDamage < 0x100) minDamage = 0x100;
            if (maxDamage < 0x100) maxDamage = 0x100;

            // Floor to integer part before range calc (same as melee at 0x598FD0)
            minDamage = (minDamage >> 8) << 8;
            maxDamage = (maxDamage >> 8) << 8;

            Debug.LogError($"[SPELL-DMG] DamageRange: level={attackerLevel} int={attackerIntellect} base={baseDamage:F1} (lvl={levelComponent:F1}+int={intellectComponent:F1}) spellMod={spellDamageMod:F2} vol={spellDamageVolatility:F2} → [{minDamage / 256},{maxDamage / 256}]");
        }

        // ═══════════════════════════════════════════════════════════════
        // RANGED DAMAGE — Uses RangedDamagePerAgility (Ranger skills)
        //
        // Formula from GC:
        //   baseDamage = (WeaponDamagePerLevel(10) × level) + (RangedDamagePerAgility(2.124) × Agility)
        //   scaled = baseDamage × spell.DamageMod
        // ═══════════════════════════════════════════════════════════════
        public static float GC_RANGED_DAMAGE_PER_AGILITY => GCDatabase.Instance.GetKnob("RangedDamagePerAgility", 2.124f);

        public static void ComputeRangedDamageRange(
            int attackerLevel,
            int attackerAgility,
            float weaponDamageMultiplier,
            float spellDamageMod,
            float spellDamageVolatility,
            out int minDamage, out int maxDamage)
        {
            // Ranger skills use weapon base + agility scaling, then spell modifier on top
            float levelComponent = GCDatabase.Instance.GetKnob("WeaponDamagePerLevel", 10f) * attackerLevel; // WeaponDamagePerLevel
            float agilityComponent = GC_RANGED_DAMAGE_PER_AGILITY * attackerAgility;
            float baseDamage = levelComponent + agilityComponent;

            int baseDmgF32 = FromFloat(baseDamage);
            int weaponF32 = FromFloat(weaponDamageMultiplier);
            int weaponScaled = FixedMul(baseDmgF32, weaponF32);

            // Then apply spell's DamageMod on top of weapon damage
            int spellModF32 = FromFloat(spellDamageMod);
            int normalized = FixedMul(weaponScaled, spellModF32);
            if (normalized < 0x100) normalized = 0x100;

            int volF32 = FromFloat(spellDamageVolatility);
            int spread = FixedMul(normalized, volF32);

            minDamage = normalized - spread;
            maxDamage = normalized + spread;
            minDamage = RoundFixed32(minDamage);
            maxDamage = RoundFixed32(maxDamage);
            if (minDamage < 0x100) minDamage = 0x100;
            if (maxDamage < 0x100) maxDamage = 0x100;
            minDamage = (minDamage >> 8) << 8;
            maxDamage = (maxDamage >> 8) << 8;

            Debug.LogError($"[RANGED-DMG] DamageRange: level={attackerLevel} agi={attackerAgility} base={baseDamage:F1} wpn={weaponDamageMultiplier:F2} spellMod={spellDamageMod:F2} vol={spellDamageVolatility:F2} → [{minDamage / 256},{maxDamage / 256}]");
        }

        // ═══════════════════════════════════════════════════════════════
        // WEAPON SKILL DAMAGE — For Butcher/Cleave (SpellWeaponDamageEffect)
        //
        // Uses normal melee damage, then applies skill-level modifier:
        //   weaponDamage = (WeaponDamagePerLevel × level + MeleeDamagePerStrength × STR) × weaponMult
        //   skillMod = (DamageModMin + skillLevel × DamageModInc) / 100
        //   finalDamage = weaponDamage × skillMod
        // ═══════════════════════════════════════════════════════════════

        public static void ComputeWeaponSkillDamageRange(
            int attackerLevel,
            int attackerStrength,
            float weaponDamageMultiplier,
            float weaponVolatility,
            int skillLevel,
            int damageModMin, int damageModMax, int damageModInc,
            out int minDamage, out int maxDamage)
        {
            // Base melee damage (same as WeaponCycleTracker)
            float levelComponent = GCDatabase.Instance.GetKnob("WeaponDamagePerLevel", 10f) * attackerLevel;
            float strengthComponent = GCDatabase.Instance.GetKnob("MeleeDamagePerStrength", 2.3364f) * attackerStrength;
            float baseDamage = levelComponent + strengthComponent;

            int baseDmgF32 = FromFloat(baseDamage);
            int weaponF32 = FromFloat(weaponDamageMultiplier);
            int normalized = FixedMul(baseDmgF32, weaponF32);
            if (normalized < 0x100) normalized = 0x100;

            // Apply skill-level modifier
            int rawMod = damageModMin + (skillLevel * damageModInc);
            if (damageModMax > 0 && rawMod > damageModMax) rawMod = damageModMax;
            float skillModifier = (100f + rawMod) / 100f; // DamageModMin=-60 at level 1 = 40% damage
            int skillModF32 = FromFloat(skillModifier);
            normalized = FixedMul(normalized, skillModF32);
            if (normalized < 0x100) normalized = 0x100;

            int volF32 = FromFloat(weaponVolatility);
            int spread = FixedMul(normalized, volF32);

            minDamage = normalized - spread;
            maxDamage = normalized + spread;
            minDamage = RoundFixed32(minDamage);
            maxDamage = RoundFixed32(maxDamage);
            if (minDamage < 0x100) minDamage = 0x100;
            if (maxDamage < 0x100) maxDamage = 0x100;
            minDamage = (minDamage >> 8) << 8;
            maxDamage = (maxDamage >> 8) << 8;

            Debug.LogError($"[WPNSKILL-DMG] DamageRange: level={attackerLevel} str={attackerStrength} wpn={weaponDamageMultiplier:F2} skillLvl={skillLevel} mod={rawMod}({skillModifier:F2}x) → [{minDamage / 256},{maxDamage / 256}]");
        }

        /// <summary>
        /// Process one spell attack with binary-proven RNG consumption pattern.
        ///
        /// PRIMARY target (isChainTarget=false): 1-3 Generate() calls
        ///   Optional #1: SpellEffect::CheckChance RNG only when Chance < 0x6400 (VA 0x545FF0)
        ///   #2: Damage roll — rng % range + min (virtualA0 +0xC8, VA 0x54FDE8)
        ///   #3: Crit roll — rng % 25700 vs threshold (virtualA0 +0x270, VA 0x54FF90)
        ///       ONLY if spell.CriticalChance > 0; skipped otherwise
        ///
        /// CHAIN target (isChainTarget=true): 2 Generate() calls
        ///   Chain dispatch (SpellChainEffect 0x54AC70) calls virtualA0 DIRECTLY,
        ///   NOT through doEffect — so NO hit roll Generate.
        ///   #1: Damage roll
        ///   #2: Crit roll (if CriticalChance > 0)
        ///
        /// Shadow Lightning total: 3 + (5 × 2) = 13 calls per cast.
        /// </summary>
        public static SpellAttackResult ProcessSpellAttack(
            MersenneTwister rng,
            int attackerLevel,
            int attackerIntellect,
            int attackerAgility,
            int attackerStrength,
            float weaponDamageMultiplier,
            float weaponVolatility,
            SpellData spell,
            Monster target,
            int skillLevel = 1,
            bool isChainTarget = false,
            int criticalDamagePercent = 200)
        {
            var result = new SpellAttackResult();
            result.Spell = spell;
            result.HitRoll = -1;

            if (spell == null || !spell.HasDirectDamageEffect)
            {
                result.Type = AttackResultType.Miss;
                Debug.LogError($"[SPELL-DMG] NO-DIRECT-DAMAGE: {spell?.DisplayName ?? "unknown"} directDamage=False modifierDamage={spell?.HasProjectileModifierDamage ?? false}");
                return result;
            }

            // Binary: SpellEffect::CheckChance only consumes RNG when Chance < 0x6400.
            // Chain targets dispatched via SpellChainEffect -> virtualA0 directly, skipping doEffect.
            int chanceF32 = Math.Min(0x6400, Math.Max(0, spell.ChanceF32));
            bool consumedChanceRng = false;
            if (!isChainTarget && chanceF32 < 0x6400)
            {
                uint hitRaw = NativeRngLedger.Generate(rng, "room", $"{spell.DisplayName ?? "spell"}:SpellEffect::CheckChance", "SpellEffect");
                consumedChanceRng = true;
                result.HitRoll = (int)(hitRaw % 25700);

                if (result.HitRoll >= chanceF32)
                {
                    // Miss — still need to consume crit RNG if CriticalChance > 0?
                    // NO: binary shows virtualA0 is only called on hit (doEffect checks hit first)
                    result.Type = AttackResultType.Miss;
                    Debug.LogError($"[SPELL-DMG] MISS: {spell.DisplayName} hitRoll={result.HitRoll} >= chance={chanceF32} (1 RNG consumed)");
                    return result;
                }
            }

            // ──── Compute damage range based on formula type (no RNG) ────
            int minDmg, maxDmg;

            if (spell.IsWeaponSkill)
            {
                ComputeWeaponSkillDamageRange(
                    attackerLevel, attackerStrength,
                    weaponDamageMultiplier, weaponVolatility,
                    skillLevel,
                    spell.SkillDamageModMin, spell.SkillDamageModMax, spell.SkillDamageModInc,
                    out minDmg, out maxDmg);
            }
            else if (spell.AttackType == AttackType.RANGED)
            {
                ComputeRangedDamageRange(
                    attackerLevel, attackerAgility,
                    weaponDamageMultiplier,
                    spell.DamageMod, spell.DamageVolatility,
                    out minDmg, out maxDmg);
            }
            else
            {
                ComputeSpellDamageRange(
                    attackerLevel, attackerIntellect,
                    spell.DamageMod, spell.DamageVolatility,
                    out minDmg, out maxDmg);
            }

            // ──── Generate #2: Damage roll (virtualA0 +0xC8, VA 0x54FDE8) — ALWAYS ────
            uint damageRaw = NativeRngLedger.Generate(rng, "room", $"{spell.DisplayName ?? "spell"}:SpellEffect::damage", "SpellEffect::virtualA0");
            int damage = spell.IsWeaponSkill
                ? RollDamageRange(minDmg, maxDmg, damageRaw)
                : RollSpellDamageRange(minDmg, maxDmg, damageRaw);

            // ──── Generate #3: Crit roll (virtualA0 +0x270, VA 0x54FF90) ────
            // SEPARATE Generate() — NOT reusing hitRoll!
            // Binary: cmp [ebx+0x74], 0; jle skip — ONLY when CriticalChance > 0
            // On crit: damage *= [unit+0x118]/100, sets flag [edi+0x41] |= 0x01.
            bool isCrit = false;
            if (spell.CriticalChance > 0)
            {
                uint critRaw = NativeRngLedger.Generate(rng, "room", $"{spell.DisplayName ?? "spell"}:SpellEffect::crit", "SpellEffect::virtualA0");
                int critRoll = (int)(critRaw % 25700);

                int critThreshold = (int)(spell.CriticalChance * 25600);
                if (critThreshold > 23040) critThreshold = 23040; // Cap at 90% (0x5A00)

                if (critRoll < critThreshold)
                {
                    isCrit = true;
                    int critPercent = Math.Max(100, criticalDamagePercent);
                    damage = (int)(((long)damage * critPercent) / 100L);
                }
                Debug.LogError($"[SPELL-DMG] CritRoll: {critRoll} vs threshold={critThreshold} ({spell.CriticalChance * 100:F0}%) critPct={Math.Max(100, criticalDamagePercent)} -> {(isCrit ? "CRIT" : "no crit")}");
            }

            result.Type = isCrit ? AttackResultType.Critical : AttackResultType.Hit;
            result.DamageF32 = damage;
            result.MinDamageF32 = minDmg;
            result.MaxDamageF32 = maxDmg;
            result.DamageRaw = damageRaw;
            result.DamageTypeId = ResolveNativeDamageTypeId(spell.DamageType);
            result.CritDamagePercent = Math.Max(100, criticalDamagePercent);

            int rngCalls = (consumedChanceRng ? 1 : 0) + 1 + (spell.CriticalChance > 0 ? 1 : 0);
            string formulaTag = spell.IsWeaponSkill ? "WPNSKILL" : spell.AttackType == AttackType.RANGED ? "RANGED" : "MAGIC";
            string rollTag = spell.IsWeaponSkill ? "weapon-fixed" : "spell-hp";
            Debug.LogError($"[SPELL-DMG] {(isCrit ? "CRIT" : "HIT")}: {spell.DisplayName} [{formulaTag}] {(isChainTarget ? "CHAIN" : "PRIMARY")} dmg={damage / 256} ({damage}) range=[{minDmg / 256},{maxDmg / 256}] dmgRaw=0x{damageRaw:X8} roll={rollTag} hitRoll={result.HitRoll} ({rngCalls} RNG consumed)");
            return result;
        }

        public static SpellAttackResult ProcessProjectileModifierTick(
            MersenneTwister rng,
            int attackerLevel,
            int attackerIntellect,
            int attackerAgility,
            int attackerStrength,
            float weaponDamageMultiplier,
            float weaponVolatility,
            SpellData spell,
            Monster target,
            int skillLevel = 1,
            int criticalDamagePercent = 200)
        {
            var result = new SpellAttackResult
            {
                Spell = spell,
                HitRoll = -1
            };

            if (rng == null || spell == null || target == null)
            {
                result.Type = AttackResultType.Miss;
                return result;
            }

            float damageMod = spell.ProjectileModifierDamageMod > 0f
                ? spell.ProjectileModifierDamageMod
                : spell.DamageMod;
            float damageVolatility = spell.ProjectileModifierDamageVolatility > 0f
                ? spell.ProjectileModifierDamageVolatility
                : spell.DamageVolatility;
            float critChance = spell.ProjectileModifierCriticalChance;
            AttackType modifierAttackType = spell.EffectiveProjectileModifierAttackType;
            DamageElement modifierDamageType = spell.EffectiveProjectileModifierDamageType;

            int minDmg;
            int maxDmg;
            if (modifierAttackType == AttackType.RANGED)
            {
                ComputeRangedDamageRange(
                    attackerLevel,
                    attackerAgility,
                    weaponDamageMultiplier,
                    damageMod,
                    damageVolatility,
                    out minDmg,
                    out maxDmg);
            }
            else
            {
                ComputeSpellDamageRange(
                    attackerLevel,
                    attackerIntellect,
                    damageMod,
                    damageVolatility,
                    out minDmg,
                    out maxDmg);
            }

            uint damageRaw = NativeRngLedger.Generate(rng, "room", "modifier:SpellEffect::damage", "SpellModifier");
            int damage = RollSpellDamageRange(minDmg, maxDmg, damageRaw);

            bool isCrit = false;
            if (critChance > 0f)
            {
                uint critRaw = NativeRngLedger.Generate(rng, "room", "modifier:SpellEffect::crit", "SpellModifier");
                int critRoll = (int)(critRaw % 25700);
                int critThreshold = (int)(critChance * 25600);
                if (critThreshold > 23040) critThreshold = 23040;
                if (critRoll < critThreshold)
                {
                    isCrit = true;
                    int critPercent = Math.Max(100, criticalDamagePercent);
                    damage = (int)(((long)damage * critPercent) / 100L);
                }
                Debug.LogError($"[SPELL-MOD-DMG] CritRoll: {critRoll} vs threshold={critThreshold} ({critChance * 100:F0}%) critPct={Math.Max(100, criticalDamagePercent)} -> {(isCrit ? "CRIT" : "no crit")}");
            }

            result.Type = isCrit ? AttackResultType.Critical : AttackResultType.Hit;
            result.DamageF32 = damage;
            result.MinDamageF32 = minDmg;
            result.MaxDamageF32 = maxDmg;
            result.DamageRaw = damageRaw;
            result.DamageTypeId = ResolveNativeDamageTypeId(modifierDamageType);
            result.CritDamagePercent = Math.Max(100, criticalDamagePercent);

            Debug.LogError($"[SPELL-MOD-DMG] {(isCrit ? "CRIT" : "HIT")}: {spell.DisplayName} modifier [{spell.ProjectileModifierEffectId ?? "modifier"}] atk={modifierAttackType} dmgType={modifierDamageType} dmg={damage / 256} ({damage}) range=[{minDmg / 256},{maxDmg / 256}] dmgRaw=0x{damageRaw:X8} mod={damageMod:F2} vol={damageVolatility:F2} critPct={Math.Max(100, criticalDamagePercent)}");
            return result;
        }

    }

    public class SpellAttackResult
    {
        public AttackResultType Type;
        public SpellData Spell;
        public int DamageF32;
        public int MinDamageF32;
        public int MaxDamageF32;
        public uint DamageRaw;
        public int DamageTypeId;
        public int HitRoll;
        public int CritDamagePercent;
    }

    public class NativeWeaponDamageInput
    {
        public MersenneTwister Rng;
        public int AttackerLevel;
        public int DefenderLevel;
        public int AttackRating;
        public int DefenseRating;
        public int BlockChance;
        public int DamageLevel;
        public int DamageBonus;
        public int DamageMod;
        public int WeaponClassId;
        public int DamageTypeId;
        public int WeaponDamageF32;
        public int WeaponVolatilityF32;
        public int CritThreshold;
        public int CritDamagePercent;
        public string Source;
        public PlayerState AttackerState;
        public bool IncludeWeaponDamageAdds = true;
    }

    public class NativeWeaponDamageResult
    {
        public AttackResultType Type = AttackResultType.Miss;
        public string ResultName = "MISS";
        public uint HitRaw;
        public uint BlockRaw;
        public uint DamageRaw;
        public int HitRoll;
        public int BlockRoll;
        public int HitThreshold;
        public int AttackRating;
        public int DefenseRating;
        public int AttackerLevel;
        public int DefenderLevel;
        public int BlockChance;
        public int DamageLevel;
        public int DamageBonus;
        public int DamageMod;
        public int WeaponClassId;
        public int DamageTypeId;
        public int WeaponDamageF32;
        public int WeaponVolatilityF32;
        public int MinDamageF32;
        public int MaxDamageF32;
        public int DamageF32;
        public uint DamageWire;
        public int TotalDamageF32;
        public uint TotalDamageWire;
        public List<NativeWeaponDamageEvent> DamageAdds = new List<NativeWeaponDamageEvent>();
        public int CritThreshold;
        public int CritDamagePercent;
        public PlayerState AttackerState;
        public bool IsHit;
        public bool IsBlocked;
        public bool IsCritical;
        public int RoomRngAfter;
    }

    public class NativeWeaponDamageAddSlot
    {
        public string Element;
        public int DamageTypeId;
        public string[] WeaponAddStats;
        public string[] DamageBonusStats;
        public string[] DamageModStats;
    }

    public class NativeWeaponDamageEvent
    {
        public string Element;
        public int DamageTypeId;
        public int DamageF32;
        public uint DamageWire;
        public int WeaponAdd;
        public int DamageBonus;
        public int DamageMod;
        public int WeaponDamageF32;
    }

    public enum AttackResultType
    {
        Miss,
        Block,
        Hit,
        Critical
    }
}
