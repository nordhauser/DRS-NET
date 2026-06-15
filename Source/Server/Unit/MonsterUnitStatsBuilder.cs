using DungeonRunners.Data;
using DungeonRunners.Engine;
using DungeonRunners.Networking;

namespace DungeonRunners.Combat
{
    /// <summary>
    /// Builds a <see cref="MonsterUnitStats"/> from a <see cref="MonsterAttackProfile"/>
    /// + monster level + global knobs.
    ///
    /// <para>
    /// <b>v1 — PARTIAL.</b> The exact .gc-Description → unit-field caching formula
    /// is in the client's <c>Unit::readInit</c> (or similar) and hasn't been decompiled
    /// yet for every field. AR/DR, damage mod, and critical chance are native formula
    /// mirrors; remaining style-specific fields are still tracked as open parity work.
    /// </para>
    ///
    /// <para>
    /// Known approximations:
    /// <list type="bullet">
    /// <item>baseAR/baseDR = authored fixed value × native CurveTable lookup</item>
    /// <item>baseDamageMod = UnitDesc::getDamageMod fixed transform</item>
    /// <item>BaseCriticalChance = UnitDesc::getCriticalChance fixed transform</item>
    /// <item>CritMultiplier = 150 (1.5×) — placeholder</item>
    /// <item>BlockChance = 0 (mobs don't block)</item>
    /// <item>All per-style fields default 0 (vanilla mobs have no special bonuses)</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class MonsterUnitStatsBuilder
    {
        public static MonsterUnitStats Build(MonsterAttackProfile profile, int level,
            float damageModOverride = float.NaN, float attackRatingOverride = float.NaN,
            float defenseRatingOverride = float.NaN)
        {
            var data = MonsterAttackData.Instance;
            // S10g: prefer caller-supplied overrides (from Monster.DamageMod/Monster.AttackRating
            // / Monster.DefenseRating set during CombatManager.SpawnMonster — they walk the
            // rank-suffix override chain (e.g. melee01.rank1 → AR=0.15) which the profile
            // doesn't see). See AUDIT_COMBAT/10d.
            float effectiveDamageMod = float.IsNaN(damageModOverride) ? profile.DamageMod : damageModOverride;
            float effectiveAttackRating = float.IsNaN(attackRatingOverride) ? profile.AttackRating : attackRatingOverride;
            float effectiveDefenseRating = float.IsNaN(defenseRatingOverride) ? profile.DefenseRating : defenseRatingOverride;

            // S10e 2026-05-27: discriminator IS the curve lookup key (per asm trace at
            // 0x0050FA85 — MOVZX ECX,byte ptr [ESP+0x14] then SHL ECX,0x8). Disc=2 for
            // all standard mobs.
            const byte discriminator = 2;

            var stats = new MonsterUnitStats
            {
                Level = level,

                AttackStyle = ResolveAttackStyle(profile),

                WeaponDamageType = ResolveDamageTypeCode(profile.WeaponDamageType),

                Discriminator = discriminator,

                // Base attacker stats — S10e: use the actual MonsterAttackRating curve from
                // Tables.gc, not the level-linear approximation. Captured pup (rank1 AR=0.15)
                // gives baseAR = (38 × curve(disc=2)=102362) >> 16 = 59 ≈ 60 ✓
                BaseAttackRating = MonsterCurves.ComputeBaseAR(effectiveAttackRating, discriminator),
                BaseAttackRatingMod = 0,
                BaseDamageMod = ComputeBaseDamageMod(effectiveDamageMod),  // 10d: exact transform
                BaseCriticalChance = ComputeBaseCriticalChance(profile.CritChance),
                CritMultiplier = 200,
                BaseDamageBonus = 0,

                // Base defensive (mob → not used when mob is attacker, but tracked for completeness)
                BaseDefenseRating = MonsterCurves.ComputeBaseDR(effectiveDefenseRating, discriminator),
                BaseDefenseRatingMod = 0,
                BlockChance = 0,

                // Per-style cached fields all 0 (vanilla mobs have no weapon mods)
                MeleeAR = 0, MeleeARMod = 0, MeleeDamageBonus = 0, MeleeDamageMod = 0,
                MeleeCritChance = 0, MeleeDefenseRating = 0, MeleeDefenseRatingMod = 0,
                RangedAR = 0, RangedARMod = 0, RangedDamageBonus = 0, RangedDamageMod = 0,
                RangedCritChance = 0, RangedDefenseRating = 0, RangedDefenseRatingMod = 0,
                Style5AR = 0, Style5ARMod = 0, Style5DamageBonus = 0, Style5DamageMod = 0, Style5CritChance = 0,
                Style6AR = 0, Style6ARMod = 0, Style6DamageBonus = 0, Style6DamageMod = 0, Style6CritChance = 0,

                DamageModScale = 256,

                // Weapon stats
                WeaponDamageFixed = (int)System.Math.Round(profile.WeaponDamage * 256f),         // .gc Damage as Fixed32
                WeaponDamagePerLevel = data.WeaponDamagePerLevel,                                  // ECX factor (GlobalKnobs)
                WeaponVolatilityFixed = (int)System.Math.Round(profile.WeaponDamageVolatility * 256f),
            };
            return stats;
        }

        private static byte ResolveAttackStyle(MonsterAttackProfile profile)
        {
            switch ((profile.WeaponClass ?? "").Trim().ToUpperInvariant())
            {
                case "HTH":      return 1;
                case "2HRANGED": return 3;
                case "1HMELEE":  return 5;
                case "2HMELEE":  return 6;
                case "POLEARM":  return 8;
                case "1HRANGED": return 9;
                case "2HCANNON": return 13;
                default:         return 1;
            }
        }

        private static byte ResolveDamageTypeCode(string damageType)
        {
            // From the .gc DamageType enum names. Order matches client's switch in
            // computeDamageMod (cases 0..7).
            if (string.IsNullOrEmpty(damageType)) return 0;
            switch (damageType.ToUpperInvariant())
            {
                case "SLASHING":   return 0;
                case "PIERCING":   return 1;
                case "BLUDGEONING":return 2;
                case "FIRE":       return 3;
                case "COLD":       return 4;
                case "LIGHTNING":  return 5;
                case "HOLY":       return 6;
                case "DARK":
                case "SHADOW":     return 7;
                default:           return 0;
            }
        }


        // Exact transform per UnitDesc::getDamageMod @ 0x0050FBF0 (see AUDIT_COMBAT/10d):
        //   cached = ((authored_Fixed32 - 256) × 25600) >> 16  ≈ (authored - 1.0) × 100
        //
        // 2026-05-27 update — S10g session: server's CombatManager.SpawnMonster log shows
        // `Monster.DamageMod = 0.50` for the pup at spawn time, matching the client's
        // captured [UnitDesc+0xDC]=128. So the "hidden scaling" S10f was looking for
        // already happens INSIDE the DR Reborn data path (DamageMod is loaded with the
        // 0.5 value already, NOT 1.0 from the .gc inheritance chain). Therefore we apply
        // the formula DIRECTLY to MonsterAttackProfile.DamageMod with no pre-scale —
        // adding a 0.5× pre-scale would double the halving.
        public static int ComputeBaseDamageMod(float authoredDamageMod)
        {
            int authoredFixed = Mathf.RoundToInt(authoredDamageMod * 256f);
            if (authoredFixed - 256 == 256) return 0;  // 2.0 special-case in client
            long delta = (long)(authoredFixed - 256);
            return (int)((delta * 25600L) >> 16);
        }

        // UnitDesc::getCriticalChance @ 0x0050FC40:
        // cached = (authoredCriticalChance_Fixed32 * RPGSettings.MonsterCriticalChance) >> 16.
        public static int ComputeBaseCriticalChance(float authoredCritChance)
        {
            if (authoredCritChance <= 0f) return 0;
            long authoredFixed = (long)Mathf.RoundToInt(authoredCritChance * 256f);
            long globalScalar = (long)MonsterAttackData.Instance.MonsterCriticalChance;
            return (int)((authoredFixed * globalScalar) >> 16);
        }

    }

    /// <summary>
    /// Builds a <see cref="PlayerUnitStats"/> for use as the damage target.
    /// </summary>
    public static class PlayerUnitStatsBuilder
    {
        public static PlayerUnitStats Build(CombatPlayer player, int level = 1)
        {
            var state = player?.PlayerState;
            if (state == null)
            {
                return new PlayerUnitStats { BaseDefenseRating = 10 * level };
            }

            float defensePerStrength = GCDatabase.Instance.GetKnob("DefenseRatingPerStrength", 14f);
            return new PlayerUnitStats
            {
                BaseDefenseRating = Mathf.RoundToInt(state.Strength * defensePerStrength) + state.ArmorDefenseRating,
                BaseDefenseRatingMod = EquipStat(state, "DEFENSE_RATING_MOD"),
                MeleeDefenseRating = EquipStat(state, "MELEE_DEFENSE_RATING"),
                MeleeDefenseRatingMod = EquipStat(state, "MELEE_DEFENSE_RATING_MOD"),
                RangedDefenseRating = EquipStat(state, "RANGE_DEFENSE_RATING"),
                RangedDefenseRatingMod = EquipStat(state, "RANGE_DEFENSE_RATING_MOD"),
                BlockChance = state.EquipmentStats != null && state.EquipmentStats.TryGetValue("BLOCK", out int block)
                    ? Mathf.Clamp(block, 0, 100)
                    : 0,
                Discriminator = 0,
            };
        }

        private static int EquipStat(PlayerState state, string key)
        {
            if (state.EquipmentStats == null) return 0;
            return state.EquipmentStats.TryGetValue(key, out int value) ? value : 0;
        }
    }
}
