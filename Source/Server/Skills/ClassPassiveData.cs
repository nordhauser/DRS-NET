using System.Collections.Generic;
using System;
using DungeonRunners.Engine;

namespace DungeonRunners.Data
{
    public static class ClassPassiveData
    {
        public const int BASE_ENDURANCE = 10;
        public const int BASE_INTELLECT = 10;
        public const int HERO_HEALTH_PER_LEVEL = 16;
        public const int HEALTH_PER_ENDURANCE = 25;
        public const int POWER_PER_INTELLECT = 17;
        public const int POWER_PER_LEVEL = 5;
        public const float BASE_HP_PER_ENDURANCE = 1.0f;

        private static uint ClampWire(long wire)
        {
            if (wire <= 0) return 0;
            if (wire >= uint.MaxValue) return uint.MaxValue;
            return (uint)wire;
        }

        public static uint CalculateHPWire(int level, int endurance, int healthPerEnduranceModPercent)
        {
            int clientLevel = Math.Max(1, level);
            int clientEndurance = Math.Max(1, endurance);
            int percent = Math.Max(0, 100 + healthPerEnduranceModPercent);
            int percentFixed = (int)(((long)percent * 0x10000L) / 0x6400L);
            long hpPerEnduranceFixed = ((long)HEALTH_PER_ENDURANCE * 256L * percentFixed) >> 8;
            long enduranceHP = (((long)clientEndurance << 8) * hpPerEnduranceFixed) >> 16;
            long levelHP = (long)clientLevel * HERO_HEALTH_PER_LEVEL;
            return ClampWire((enduranceHP + levelHP) * 256L);
        }

        public static uint CalculateManaWire(int level, int intellect, int manaPerIntellectModPercent)
        {
            int clientLevel = Math.Max(1, level);
            int clientIntellect = Math.Max(1, intellect);
            int percent = Math.Max(0, 100 + manaPerIntellectModPercent);
            int percentFixed = (int)(((long)percent * 0x10000L) / 0x6400L);
            long manaPerIntellectFixed = ((long)POWER_PER_INTELLECT * 256L * percentFixed) >> 8;
            long intellectMana = (((long)clientIntellect << 8) * manaPerIntellectFixed) >> 16;
            long levelMana = (long)clientLevel * POWER_PER_LEVEL;
            return ClampWire((intellectMana + levelMana) * 256L);
        }

        public static readonly Dictionary<string, ClassPassive> Passives = new Dictionary<string, ClassPassive>
        {
            ["Fighter"] = new ClassPassive
            {
                PassiveSkillId = "skills.generic.FighterClassPassive",
                Profession = "skills.professions.Warrior",
                HealthPerEnduranceMod = 50,
                EnduranceMod = -5,
                StrengthMod = 5,
                AgilityMod = 5,
                IntellectMod = -5,
                ManaPerIntellectMod = -25,
                RangeAttackSpeedMod = -10
            },

            ["Mage"] = new ClassPassive
            {
                PassiveSkillId = "skills.generic.MageClassPassive",
                Profession = "skills.professions.Warlock",
                HealthPerEnduranceMod = -25,
                EnduranceMod = 5,
                StrengthMod = -5,
                AgilityMod = -5,
                IntellectMod = 5,
                ManaPerIntellectMod = 100,
                RangeAttackSpeedMod = 0
            },

            ["Ranger"] = new ClassPassive
            {
                PassiveSkillId = "skills.generic.RangerClassPassive",
                Profession = "skills.professions.Ranger",
                HealthPerEnduranceMod = 10,
                EnduranceMod = 5,
                StrengthMod = -5,
                AgilityMod = 5,
                IntellectMod = -5,
                ManaPerIntellectMod = -5,
                RangeAttackSpeedMod = 0
            }
        };

        public static int CalculateHPBonusWire(string className, int level = 1, int allocatedEndurance = 0)
        {
            int baseEndurance = BASE_ENDURANCE + Math.Max(0, allocatedEndurance);
            uint noPassiveHP = CalculateHPWire(level, baseEndurance, 0);

            if (!Passives.TryGetValue(className, out ClassPassive passive))
            {
                Debug.LogWarning($"[CLASS-PASSIVE] class='{className}' missing hpBonusWire=0");
                return 0;
            }

            int passiveEndurance = Math.Max(1, baseEndurance + passive.EnduranceMod);
            uint passiveHP = CalculateHPWire(level, passiveEndurance, passive.HealthPerEnduranceMod);
            long bonus = (long)passiveHP - noPassiveHP;
            if (bonus > int.MaxValue) bonus = int.MaxValue;
            if (bonus < int.MinValue) bonus = int.MinValue;
            Debug.LogError($"[CLASS-PASSIVE] class={className} hpBonusWire={bonus} noPassive={noPassiveHP} passive={passiveHP} level={level} end={baseEndurance}->{passiveEndurance} hpeMod={passive.HealthPerEnduranceMod}");
            return (int)bonus;
        }

        public static int CalculateManaBonusWire(string className, int level = 1, int allocatedIntellect = 0)
        {
            int baseIntellect = BASE_INTELLECT + Math.Max(0, allocatedIntellect);
            uint noPassiveMana = CalculateManaWire(level, baseIntellect, 0);

            if (!Passives.TryGetValue(className, out ClassPassive passive))
            {
                Debug.LogWarning($"[CLASS-PASSIVE] class='{className}' missing manaBonusWire=0");
                return 0;
            }

            int passiveIntellect = Math.Max(1, baseIntellect + passive.IntellectMod);
            uint passiveMana = CalculateManaWire(level, passiveIntellect, passive.ManaPerIntellectMod);
            long bonus = (long)passiveMana - noPassiveMana;
            if (bonus > int.MaxValue) bonus = int.MaxValue;
            if (bonus < int.MinValue) bonus = int.MinValue;
            Debug.LogError($"[CLASS-PASSIVE] class={className} manaBonusWire={bonus} noPassive={noPassiveMana} passive={passiveMana} level={level} int={baseIntellect}->{passiveIntellect} mpiMod={passive.ManaPerIntellectMod}");
            return (int)bonus;
        }

    }

    public class ClassPassive
    {
        public string PassiveSkillId { get; set; }
        public string Profession { get; set; }
        public int HealthPerEnduranceMod { get; set; }
        public int EnduranceMod { get; set; }
        public int StrengthMod { get; set; }
        public int AgilityMod { get; set; }
        public int IntellectMod { get; set; }
        public int ManaPerIntellectMod { get; set; }
        public int RangeAttackSpeedMod { get; set; }
    }

}
