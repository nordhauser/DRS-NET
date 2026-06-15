using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using DungeonRunners.Engine;
using DungeonRunners.Utilities;
using DungeonRunners.Data;
using DungeonRunners.Core;
using System.Text;
using System.Reflection;
using System.Runtime.CompilerServices;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using DungeonRunners.Managers;
using DungeonRunners.Database;
using DungeonRunners.Engine.Playables;
using System.Security.Cryptography;
using DungeonRunners.Combat;
using DungeonRunners.Networking.Sync;

namespace DungeonRunners.Networking
{
    public partial class GameServer
    {
        private Dictionary<string, float> _lastAttackTime = new Dictionary<string, float>();
        private Dictionary<string, ushort> _lastAttackTarget = new Dictionary<string, ushort>();
        // ═══ BUFF MODIFIER MAP: skill short name → (modifier GC type, base duration sec, duration inc per level) ═══
        // Used by HandleSelfCastSpell to track self-cast buffs for zone persistence.
        // Duration in seconds — converted to ticks (×1000/24) before storing in tracker.
        private static readonly Dictionary<string, (string modGcType, float durBase, float durInc)> _buffModifierMap
            = new Dictionary<string, (string, float, float)>(StringComparer.OrdinalIgnoreCase)
        {
            { "sprint",                    ("skills.generic.Sprint.Modifier",                    30f,  10f) },
            { "manashield",                ("skills.generic.ManaShield.Modifier",                180f,  0f) },
            { "healsself",                 ("skills.generic.HealSelf.Modifier",                    0f,  0f) },
            { "manaself",                  ("skills.generic.ManaSelf.Modifier",                    0f,  0f) },
            { "blight",                    ("skills.generic.Blight.Modifier",                     30f,  1f) },
            { "charge",                    ("skills.generic.Charge.CastModifier",                150f,  0.5f) },
            { "divineresistbuff",          ("skills.generic.DivineResistBuff.Modifier",           40f,  0f) },
            { "fireresistbuff",            ("skills.generic.FireResistBuff.Modifier",             40f,  0f) },
            { "iceresistbuff",             ("skills.generic.IceResistBuff.Modifier",              40f,  0f) },
            { "poisonresistbuff",          ("skills.generic.PoisonResistBuff.Modifier",           40f,  0f) },
            { "shadowresistbuff",          ("skills.generic.ShadowResistBuff.Modifier",           40f,  0f) },
            { "divinedamagebuff",          ("skills.generic.DivineDamageBuff.Modifier",           30f,  0f) },
            { "firedamagebuff",            ("skills.generic.FireDamageBuff.Modifier",             30f,  0f) },
            { "icedamagebuff",             ("skills.generic.IceDamageBuff.Modifier",              30f,  0f) },
            { "poisondamagebuff",          ("skills.generic.PoisonDamageBuff.Modifier",           30f,  0f) },
            { "shadowdamagebuff",          ("skills.generic.ShadowDamageBuff.Modifier",           30f,  0f) },
            { "1hmeleespeedbuff",          ("skills.generic.1HMeleeSpeedBuff.Modifier",           30f,  0f) },
            { "2hmeleespeedbuff",          ("skills.generic.2HMeleeSpeedBuff.Modifier",           30f,  0f) },
            { "rangedspeedbuff",           ("skills.generic.RangedSpeedBuff.Modifier",            30f,  0f) },
            { "stunresistbuff",            ("skills.generic.StunResistBuff.Modifier",             30f,  0f) },
            { "minmovespeedbuff",          ("skills.generic.MinMoveSpeedBuff.Modifier",           15f,  0f) },
            { "aggroincreasemodbuff",      ("skills.generic.AggroIncreaseModBuff.Modifier",       25f,  5f) },
            { "meleedamagereflectionbuff", ("skills.generic.MeleeDamageReflectionBuff.Modifier",  30f,  0f) },
            { "stomp",                     ("skills.generic.Stomp.VisualModifier",                 0f,  0f) },
            { "poisonblastradius",         ("skills.generic.PoisonBlastRadius.Modifier",           4f,  0.25f) },
            { "shadowrage",                ("skills.generic.ShadowRage.CastModifier",             25f,  1f) },
            { "firecone",                  ("skills.generic.FireCone.CastModifier",                0f,  0f) },
            { "diviineintervention",       ("skills.generic.DivineIntervention.Modifier",         15f,  1f) },
            { "divineintervention",        ("skills.generic.DivineIntervention.Modifier",         15f,  1f) },
            { "strengthbuff",              ("skills.generic.StrengthBuff.Modifier",               30f, 15f) },
            { "shadowtendrils",            ("skills.generic.ShadowTendrils.Modifier",             30f,  0f) },
            { "firetrail",                 ("skills.generic.FireTrail.Modifier",                  25f,  0f) },
            { "poisontrail",               ("skills.generic.PoisonTrail.Modifier",                60f,  0f) },
        };
        // ═══ CREATURE DEBUFF MAP: monster skill name → (modifier GC type, duration seconds) ═══
        private static readonly Dictionary<string, (string modGcType, float durationSec)> _creatureDebuffMap
            = new Dictionary<string, (string, float)>(StringComparer.OrdinalIgnoreCase)
        {
            { "basicslow",                    ("skills.creature.BasicSlow.Modifier",                    15f) },
            { "basicstun",                    ("skills.creature.BasicStun.Modifier",                     3f) },
            { "creaturerend",                 ("skills.creature.CreatureRend.Modifier",                   5f) },
            { "creaturehamstring",            ("skills.creature.CreatureHamstring.Modifier",              5f) },
            { "creatureenfeeble",             ("skills.creature.CreatureEnfeeble.Modifier",              60f) },
            { "creaturegoldstun",             ("skills.creature.CreatureGoldStun.Modifier",              15f) },
            { "creaturedebuffdivine",         ("skills.creature.CreatureDebuffDivine.Modifier",          15f) },
            { "creaturedebufffire",           ("skills.creature.CreatureDebuffFire.Modifier",            15f) },
            { "creaturedebuffice",            ("skills.creature.CreatureDebuffIce.Modifier",             15f) },
            { "creaturedebuffpoison",         ("skills.creature.CreatureDebuffPoison.Modifier",          15f) },
            { "creaturedebuffshadow",         ("skills.creature.CreatureDebuffShadow.Modifier",          15f) },
            { "widowerweb",                   ("skills.creature.WidowerWeb.Modifier",                    10f) },
            { "widowerblackcloud",            ("skills.creature.WidowerBlackCloud.Modifier",             10f) },
            { "agrockintimidate",             ("skills.creature.AgrockIntimidate.Modifier",              15f) },
            { "abaddonflameprison",           ("skills.creature.AbaddonFlamePrison.Modifier",            10f) },
            { "orokruntshotpoison",           ("skills.creature.OrokRuntShotPoison.Modifier",             3f) },
            { "shadowqueenmortalstrike_fear", ("skills.creature.ShadowQueenMortalStrike_Fear.Modifier",  10f) },
            { "bossmortalstrike_fear",        ("skills.creature.BossMortalStrike_Fear.Modifier",          6f) },
            { "heckledebuff",                 ("skills.creature.HeckleDebuff.Modifier",                   5f) },
            { "griefermultiboltsilence",      ("skills.creature.GrieferMultiBoltSilence.Modifier",        5f) },
            { "relicstun",                    ("skills.creature.RelicStun.Modifier",                      1f) },
            { "combatfearself",               ("skills.creature.CombatFearSelf.Modifier",                10f) },
            { "combatfearfriendsaoe",         ("skills.creature.CombatFearFriendsAoE.Modifier",           8f) },
        };

        // ═══ WEAPON DEBUFF MAP: monster weapon GC short name → creature debuff skill name ═══
        private static readonly Dictionary<string, string> _weaponDebuffMap
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "basic",                          "creaturerend" },
            { "wargbase_grunt",                 "creaturerend" },
            { "wargbase_hero",                  "creaturerend" },
            { "wargbase_liger",                 "creaturerend" },
            { "wargbase_pinata_hero",           "creaturerend" },
            { "abba_labba_caster_grunt_base",   "creaturedebufffire" },
            { "abba_labba_caster_hero_base",    "creaturedebufffire" },
            { "boss_caster",                    "creaturedebufffire" },
            // Direct skill-as-manipulator mappings (debuff name maps to itself)
            { "basicslow",                      "basicslow" },
            { "basicstun",                      "basicstun" },
            { "creaturerend",                   "creaturerend" },
            { "creaturehamstring",              "creaturehamstring" },
            { "creatureenfeeble",               "creatureenfeeble" },
            { "creaturegoldstun",               "creaturegoldstun" },
            { "creaturedebuffdivine",           "creaturedebuffdivine" },
            { "creaturedebufffire",             "creaturedebufffire" },
            { "creaturedebuffice",              "creaturedebuffice" },
            { "creaturedebuffpoison",           "creaturedebuffpoison" },
            { "creaturedebuffshadow",           "creaturedebuffshadow" },
            { "widowerweb",                     "widowerweb" },
            { "widowerblackcloud",              "widowerblackcloud" },
            { "agrockintimidate",               "agrockintimidate" },
            { "abaddonflameprison",             "abaddonflameprison" },
            { "orokruntshotpoison",             "orokruntshotpoison" },
            { "shadowqueenmortalstrike_fear",   "shadowqueenmortalstrike_fear" },
            { "bossmortalstrike_fear",          "bossmortalstrike_fear" },
            { "heckledebuff",                   "heckledebuff" },
            { "griefermultiboltsilence",        "griefermultiboltsilence" },
            { "relicstun",                      "relicstun" },
            { "combatfearself",                 "combatfearself" },
            { "combatfearfriendsaoe",           "combatfearfriendsaoe" },
        };

        private float _combatTimer = 0f;
        private uint _nativeCombatTick = 0;
        private float _nativeCombatTime = -1f;
        private const float COMBAT_TICK = 1f / 30f;
        private Dictionary<string, HashSet<byte>> _playerSpellSlots = new Dictionary<string, HashSet<byte>>();
        private Dictionary<string, float> _activeSkillBusyUntil = new Dictionary<string, float>();
        // Maps connKey → { manipulatorId → skill GCClass } — built during Op4 skill writing
        private Dictionary<string, Dictionary<uint, string>> _playerManipMap = new Dictionary<string, Dictionary<uint, string>>();
        // Maps connKey → { skill GCClass → skill level } — tracks trained skill levels
        private Dictionary<string, Dictionary<string, int>> _playerSkillLevels = new Dictionary<string, Dictionary<string, int>>();
        private Dictionary<string, Dictionary<string, uint>> _playerSkillSlots = new Dictionary<string, Dictionary<string, uint>>();

        // ═══ GC-authoritative skill training cost data (from finalconf.json) ═══
        // Binary cost formula at 0x53F390: cost = (RequiredLevel + (nextLevel-1) * GoldValueMod) * SkillValuePerLevel
        // SkillValuePerLevel = 1113.621 from GlobalKnobs (finalconf.json)
        // RequiredLevel and GoldValueMod per-skill from skill Description GC objects
        private float SKILL_VALUE_PER_LEVEL => GCDatabase.Instance.GetKnob("SkillValuePerLevel", 1113.621f);
        private static readonly Dictionary<string, (float goldValueMod, int requiredLevel, int requiredLevelInc, int maxSkillLevel)> _skillTrainData
            = new Dictionary<string, (float, int, int, int)>(StringComparer.OrdinalIgnoreCase)
        {
            { "skills.Generic.SummonSnowman", (2.3f, 30, 5, 15) },
            { "skills.generic.1HMeleeSpeedBuff", (1.75f, 5, 10, 10) },
            { "skills.generic.2HMeleeSpeedBuff", (1.75f, 5, 10, 10) },
            { "skills.generic.AggroIncreaseModBuff", (1.87f, 25, 18, 5) },
            { "skills.generic.Blight", (1.0f, 4, 10, 10) },
            { "skills.generic.BlockKnockdownProcPassive", (5.0f, 25, 7, 1) },
            { "skills.generic.Butcher", (1.0f, 3, 5, 20) },
            { "skills.generic.Charge", (1.25f, 20, 5, 17) },
            { "skills.generic.Cleave", (1.0f, 9, 6, 15) },
            { "skills.generic.CleaveUpgradeProcPassive", (5.1f, 35, 5, 14) },
            { "skills.generic.DivineDamageBuff", (2.0f, 12, 6, 15) },
            { "skills.generic.DivineIntervention", (5.0f, 70, 5, 7) },
            { "skills.generic.DivineMeleeAttack", (1.0f, 5, 5, 20) },
            { "skills.generic.DivineRay", (2.25f, 45, 5, 12) },
            { "skills.generic.DivineResistBuff", (1.0f, 3, 5, 20) },
            { "skills.generic.DivineResistPassive", (2.1f, 30, 5, 15) },
            { "skills.generic.FearMeleeAttack", (1.0f, 14, 21, 5) },
            { "skills.generic.FearResistModPassive", (3.3f, 25, 15, 5) },
            { "skills.generic.FearShot", (1.0f, 10, 10, 10) },
            { "skills.generic.FighterClassPassive", (2.0f, 1, 1, 1) },
            { "skills.generic.FireBolt", (1.0f, 3, 5, 20) },
            { "skills.generic.FireCone", (2.0f, 20, 5, 17) },
            { "skills.generic.FireCurseShot", (4.6f, 50, 5, 11) },
            { "skills.generic.FireDamageBuff", (2.0f, 12, 6, 15) },
            { "skills.generic.FireMeleeSummon", (2.45f, 40, 5, 13) },
            { "skills.generic.FireResistBuff", (1.0f, 3, 5, 20) },
            { "skills.generic.FireResistPassive", (2.1f, 30, 5, 15) },
            { "skills.generic.FireRing", (1.25f, 12, 6, 15) },
            { "skills.generic.FireShot", (1.53f, 30, 5, 10) },
            { "skills.generic.HealSelf", (4.89f, 3, 19, 5) },
            { "skills.generic.IceBolt", (1.0f, 5, 5, 20) },
            { "skills.generic.IceDamageBuff", (2.0f, 12, 6, 15) },
            { "skills.generic.IceMultiBolt", (1.0f, 16, 6, 15) },
            { "skills.generic.IceResistBuff", (1.0f, 3, 5, 20) },
            { "skills.generic.IceResistPassive", (2.1f, 30, 5, 15) },
            { "skills.generic.IceShot", (1.33f, 16, 6, 15) },
            { "skills.generic.IceTargetedBurst", (3.89f, 40, 5, 13) },
            { "skills.generic.IceTargetedBurstUpgradeProcPassive", (5.1f, 55, 5, 10) },
            { "skills.generic.InfectiousPoisonUpgradeProcPassive", (2.67f, 37, 7, 10) },
            { "skills.generic.MageClassPassive", (2.0f, 1, 1, 1) },
            { "skills.generic.MagicDamageModPassive", (2.0f, 1, 1, 1) },
            { "skills.generic.ManaSelf", (4.89f, 3, 19, 5) },
            { "skills.generic.ManaShield", (10.0f, 25, 15, 6) },
            { "skills.generic.MeleeAttackRatingModPassive", (3.75f, 30, 17, 5) },
            { "skills.generic.MeleeAttackSpeedModPassive", (2.0f, 1, 1, 1) },
            { "skills.generic.MeleeDamageReflectionBuff", (1.0f, 11, 22, 5) },
            { "skills.generic.MinMoveSpeedBuff", (2.9f, 10, 30, 4) },
            { "skills.generic.MonsterBaitHealthModPassive", (4.0f, 31, 1, 1) },
            { "skills.generic.NoxiousShot", (1.0f, 15, 6, 15) },
            { "skills.generic.PenetrateKnockdownShot", (1.0f, 25, 5, 16) },
            { "skills.generic.PoisonBlastRadius", (1.0f, 3, 5, 20) },
            { "skills.generic.PoisonDamageBuff", (2.0f, 12, 6, 15) },
            { "skills.generic.PoisonResistBuff", (1.0f, 3, 5, 20) },
            { "skills.generic.PoisonResistPassive", (2.1f, 30, 5, 15) },
            { "skills.generic.PoisonShot", (1.0f, 3, 5, 20) },
            { "skills.generic.PoisonTrail", (1.35f, 40, 5, 13) },
            { "skills.generic.RangeAttackSpeedModPassive", (2.0f, 1, 1, 1) },
            { "skills.generic.RangedSpeedBuff", (1.75f, 5, 10, 10) },
            { "skills.generic.RangerClassPassive", (2.0f, 1, 1, 1) },
            { "skills.generic.ShadowBolt", (1.0f, 16, 6, 15) },
            { "skills.generic.ShadowDamageBuff", (2.0f, 12, 6, 15) },
            { "skills.generic.ShadowLightning", (1.0f, 3, 5, 20) },
            { "skills.generic.ShadowLightningKnockdown", (1.0f, 19, 9, 10) },
            { "skills.generic.ShadowLightningUpgradeProcPassive", (1.0f, 30, 5, 15) },
            { "skills.generic.ShadowRage", (2.79f, 36, 7, 10) },
            { "skills.generic.ShadowResistBuff", (1.0f, 3, 5, 20) },
            { "skills.generic.ShadowResistPassive", (2.1f, 30, 5, 15) },
            { "skills.generic.ShadowTendrils", (4.8f, 60, 5, 9) },
            { "skills.generic.SlowDeBuff", (1.0f, 8, 23, 5) },
            { "skills.generic.SnowmanFreezeAura", (4.23f, 35, 32, 3) },
            { "skills.generic.SnowmanHealthModAuraBuff", (3.52f, 33, 16, 5) },
            { "skills.generic.SnowManIceDamageProcAuraModBuff", (3.52f, 31, 1, 1) },
            { "skills.generic.Sprint", (1.25f, 3, 24, 5) },
            { "skills.generic.Stomp", (1.0f, 3, 5, 20) },
            { "skills.generic.StunResistBuff", (1.0f, 7, 10, 10) },
            { "skills.generic.SummonerClassPassive", (2.0f, 1, 1, 1) },
            { "skills.generic.SummonMonsterBait", (1.25f, 19, 5, 17) },
            { "skills.generic.Teleport", (20.0f, 50, 50, 2) },
        };
        private const int MAX_COMBAT_CATCH_UP_TICKS = 8;

        private float GetNativeCombatNow()
        {
            return _nativeCombatTime >= 0f ? _nativeCombatTime : Time.time;
        }

        private void GetNativeValidationCutoff(out uint cutoffTick, out float cutoffTime)
        {
            CombatManager.Instance.GetNativeValidationCutoff(out cutoffTick, out cutoffTime);
        }

        private bool AdvanceNativeCombatClock(out float tickNow, out uint tickIndex)
        {
            if (_nativeCombatTime < 0f)
                _nativeCombatTime = Time.time;

            tickNow = _nativeCombatTime;
            tickIndex = _nativeCombatTick;
            if (_combatTimer + 0.0001f < COMBAT_TICK)
                return false;

            _combatTimer -= COMBAT_TICK;
            _nativeCombatTick++;
            _nativeCombatTime += COMBAT_TICK;
            tickNow = _nativeCombatTime;
            tickIndex = _nativeCombatTick;
            CombatManager.Instance.SetNativeCombatClock(tickIndex, tickNow, "GameServer.Update");
            Debug.LogError($"[NATIVE-COMBAT-CLOCK] tick={tickIndex} time={tickNow:F3} delta={COMBAT_TICK:F3} source=Update");
            return true;
        }

        private float _autoSaveTimer = 0f;


        void Update()
        {
            _tickTimer += Time.deltaTime;
            _combatTimer += Time.deltaTime;

            if (_tickTimer >= TICK_INTERVAL)
            {
                _tickTimer -= TICK_INTERVAL;
                AdvanceAllAvatarHP();
                int combatTicks = 0;
                uint lastTickIndex = 0;
                float lastTickNow = 0f;
                while (combatTicks < MAX_COMBAT_CATCH_UP_TICKS && AdvanceNativeCombatClock(out float tickNow, out uint tickIndex))
                {
                    bool allowNewMonsterAttacks = true;
                    AdvanceUseTargetApproaches(COMBAT_TICK);
                    TickCombatDeterministicSystems(tickNow, allowNewMonsterAttacks);
                    CombatManager.Instance.MarkNativeEntityUpdateCompleted(tickIndex, tickNow, "GameServer.Update");
                    FlushPendingMonsterBehaviorUpdates(tickNow, tickIndex);
                    lastTickIndex = tickIndex;
                    lastTickNow = tickNow;
                    combatTicks++;
                }
                if (combatTicks > 0)
                {
                    WirePacketTally.Report();
                    Debug.LogError($"[NATIVE-COMBAT-CLOCK] completed ticks={combatTicks} lastTick={lastTickIndex} lastTime={lastTickNow:F3} remaining={_combatTimer:F4}");
                }
                if (_combatTimer >= COMBAT_TICK)
                    Debug.LogError($"[NATIVE-COMBAT-CLOCK] catch-up pending remaining={_combatTimer:F4} maxTicks={MAX_COMBAT_CATCH_UP_TICKS}");
                FlushAllQueues();
                FlushPendingKills();
                ReleaseCompletedUseTargets();
                FlushPendingClientControlResets();

                // PvP matchmaking pass — throttled to 1Hz internally
                try { ProcessMatchmakingTick(); }
                catch (Exception ex) { Debug.LogError($"[PVP-TICK] {ex.Message}"); }
            }

            _merchantRefreshTimer += Time.deltaTime;
            if (_merchantRefreshTimer >= MERCHANT_REFRESH_INTERVAL)
            {
                _merchantRefreshTimer = 0f;
                MerchantManager.ProcessRefreshes(_zoneNPCs, _connections, SendCompressedA, ref _nextEntityId);
            }

            // Cleanup expired dropped items
            _droppedItemCleanupTimer += Time.deltaTime;
            if (_droppedItemCleanupTimer >= DROPPED_ITEM_CLEANUP_INTERVAL)
            {
                _droppedItemCleanupTimer = 0f;
                CleanupExpiredDroppedItems();
            }

            // Auto-save HP/mana/level every 30 seconds
            _autoSaveTimer += Time.deltaTime;
            if (_autoSaveTimer >= AUTO_SAVE_INTERVAL)
            {
                _autoSaveTimer = 0f;
                foreach (var conn in _connections.Values)
                {
                    if (conn != null && conn.LoginName != null)
                        SavePlayerLevel(conn);
                }
            }
        }






        private void AdvanceAllAvatarHP()
        {
        }

        private void AdvanceUseTargetApproaches(float deltaTime)
        {
            if (deltaTime <= 0f) return;
            foreach (var conn in _connections.Values)
            {
                if (conn == null || !conn.HasActiveUseTarget) continue;
                if (!IsBasicMeleeUseTargetFlag(conn.ActiveUseTargetFlags)) continue;

                var monster = CombatManager.Instance.GetMonster(conn.ActiveUseTargetId)
                           ?? CombatManager.Instance.GetMonsterByComponent(conn.ActiveUseTargetId);
                if (monster == null || !monster.IsAlive) continue;

                var state = GetPlayerState(conn.ConnId.ToString());
                if (AdvanceUseTargetApproach(conn, state, monster, deltaTime, out float distance, out float range) && conn.Avatar != null)
                {
                    uint avatarId = (uint)conn.Avatar.Id;
                    bool wasTargeting = monster.AggroTriggered && monster.TargetId == avatarId;
                    bool wasContact = monster.CombatContactTargetId == avatarId && monster.CombatContactUntil > GetNativeCombatNow();
                    bool nativeRangedProjectile = Combat.DamageComputer.IsNativeProjectileWeapon(state);
                    bool nativeWeaponUseStarted = nativeRangedProjectile && conn.ActiveUseTargetInitUsePassed;
                    CombatManager.Instance.SetPlayerActiveClientAttack(avatarId, true, monster.EntityId);
                    CombatManager.Instance.EngageMonsterFromClientAction(monster, avatarId, nativeWeaponUseStarted);
                    if (nativeRangedProjectile && !conn.ActiveUseTargetStartedWeaponUse)
                    {
                        conn.ActiveUseTargetStartedWeaponUse = true;
                        string atkKey = conn.ConnId.ToString();
                        Combat.WeaponCycleTracker.Instance.RegisterAttack(atkKey, conn.ActiveUseTargetId, monster, state, conn, true, distance, range);
                    }
                    if (!wasTargeting || !wasContact)
                        Debug.LogError($"[USETARGET-APPROACH] contact target={monster.EntityId} dist={distance:F1} range={range:F1} initUsePassed={conn.ActiveUseTargetInitUsePassed} startedWeaponUse={conn.ActiveUseTargetStartedWeaponUse} pos=({conn.PlayerPosX:F1},{conn.PlayerPosY:F1})");
                }
            }
        }

        private bool AdvanceUseTargetApproach(RRConnection conn, PlayerState state, Combat.Monster monster, float deltaTime, out float distance, out float range)
        {
            distance = 0f;
            range = 0f;
            if (conn == null || monster == null || deltaTime <= 0f) return false;

            ResolveMonsterClientVisiblePosition(monster, out float targetX, out float targetY);
            float dx = targetX - conn.PlayerPosX;
            float dy = targetY - conn.PlayerPosY;
            distance = Mathf.Sqrt(dx * dx + dy * dy);
            bool nativeRangedProjectile = Combat.DamageComputer.IsNativeProjectileWeapon(state);
            float clientSyncTolerance = 0f;
            string rangeSource = nativeRangedProjectile ? "unknown" : "native-contact-range";
            long distSqFixed8 = 0;
            long thresholdSqFixed8 = 0;
            bool initUsePassed = false;
            if (nativeRangedProjectile)
            {
                range = CombatManager.Instance.ResolveNativeUseTargetInitUseRange(state, monster, out clientSyncTolerance, out rangeSource);
                initUsePassed = CombatManager.Instance.EvaluateNativeUseTargetInitUse(
                    conn.PlayerPosX, conn.PlayerPosY, targetX, targetY,
                    range, clientSyncTolerance, out distance, out distSqFixed8, out thresholdSqFixed8);
                conn.ActiveUseTargetInitUseRange = range;
                conn.ActiveUseTargetInitUseDistance = distance;
                conn.ActiveUseTargetClientSyncTolerance = clientSyncTolerance;
                conn.ActiveUseTargetInitUsePassed = initUsePassed;
                float weaponUseRange = CombatManager.Instance.ResolvePlayerRangedWeaponUseRange(state);
                float projectileRange = CombatManager.Instance.ResolvePlayerRangedProjectileRange(state, monster);
                Debug.LogError($"[USETARGET-INIT] target={monster.EntityId} behavior={monster.BehaviorId} component={conn.ActiveUseTargetComponentId} session={conn.ActiveUseTargetSessionId} flags=0x{conn.ActiveUseTargetFlags:X2} player=({conn.PlayerPosX:F1},{conn.PlayerPosY:F1}) targetPos=({targetX:F1},{targetY:F1}) dist={distance:F2} distSqFixed8={distSqFixed8} initUseRange={range:F1} tolerance={clientSyncTolerance:F1} thresholdSqFixed8={thresholdSqFixed8} source={rangeSource} weaponRange={weaponUseRange:F1} projectileReach={projectileRange:F1} result={(initUsePassed ? "use" : "moving")} rngBefore=-1 rngAfter=-1");
            }
            else
            {
                range = CombatManager.Instance.ResolvePlayerMeleeNativeContactRange(state, monster);
            }
            if (range <= 0f)
                return false;

            if (nativeRangedProjectile)
            {
                if (initUsePassed)
                    return true;
            }
            else if (distance <= range + NATIVE_CONTACT_RANGE_EPSILON || distance <= 0.001f)
            {
                return true;
            }

            string zoneName = !string.IsNullOrWhiteSpace(monster.ZoneName) ? monster.ZoneName : conn.CurrentZoneName;
            string pathMapKey = !string.IsNullOrWhiteSpace(monster.InstanceKey) ? monster.InstanceKey : GetInstanceZoneKey(conn);
            if (string.IsNullOrWhiteSpace(pathMapKey))
                pathMapKey = zoneName;
            var pathMap = !string.IsNullOrWhiteSpace(pathMapKey) ? PathMapManager.Instance.GetPathMap(pathMapKey) : null;
            if (pathMap != null && pathMap.TryCanReachPoint(conn.PlayerPosX, conn.PlayerPosY, targetX, targetY, out bool canReach) && !canReach)
            {
                Debug.LogError($"[USETARGET-APPROACH] path-blocked target={monster.EntityId} dist={distance:F1} range={range:F1} player=({conn.PlayerPosX:F1},{conn.PlayerPosY:F1}) target=({targetX:F1},{targetY:F1}) native=UseTarget::UpdateMoving+PathMap::CanReachPoint action=keep-client-mover");
                return false;
            }

            string approachKey = $"{conn.ConnId}:{monster.EntityId}:{conn.ActiveUseTargetSessionId}";
            if (!_useTargetApproachLogTimes.TryGetValue(approachKey, out float lastApproachLog) ||
                Time.time - lastApproachLog >= 0.5f)
            {
                _useTargetApproachLogTimes[approachKey] = Time.time;
                Debug.LogError($"[USETARGET-APPROACH] pending target={monster.EntityId} dist={distance:F2} range={range:F1} initUsePassed={initUsePassed} distSqFixed8={distSqFixed8} thresholdSqFixed8={thresholdSqFixed8} native=UseTarget::UpdateMoving+CheckInitUse action=keep-client-mover noServerPositionMutation=True");
            }
            return false;
        }

        private static void ResolveMonsterClientVisiblePosition(Combat.Monster monster, out float posX, out float posY)
        {
            posX = monster != null ? monster.PosX : 0f;
            posY = monster != null ? monster.PosY : 0f;
            if (monster != null)
                CombatManager.Instance.TryGetMonsterClientVisiblePosition(monster, CombatManager.Instance.GetNativeCombatTime(), out posX, out posY);
        }

        private static string ResolveConnectionInstanceKey(RRConnection conn)
        {
            if (conn == null)
                return null;
            if (!string.IsNullOrWhiteSpace(conn.RuntimeInstanceKey))
                return conn.RuntimeInstanceKey;
            if (IsPublicZone(conn.CurrentZoneName))
                return conn.CurrentZoneName;
            return $"{conn.CurrentZoneName}_inst{conn.InstanceId}";
        }

        private struct NativeItemDropPlacement
        {
            public float X;
            public float Y;
            public float Z;
            public int Draws;
            public string Result;
        }

        private static uint ConsumeNativeItemAddToWorldHeading(string owner)
        {
            uint headingRaw = NativeRandomStreams.GenerateGlobalStatic(
                "ItemObject.addToWorld.heading",
                owner ?? "ItemObject::addToWorld");
            uint headingFixed8 = (headingRaw % 360u) << 8;
            Debug.LogError($"[ITEM-ADDWORLD-NATIVE] source={owner ?? "unknown"} headingRaw=0x{headingRaw:X8} headingFixed8=0x{headingFixed8:X8} native=ItemObject::addToWorld@0x0058A0A0 NativeRandomStreams.GenerateGlobalStatic");
            return headingRaw;
        }

        private static NativeItemDropPlacement ResolveNativeItemDropPlacement(
            RRConnection conn,
            string zoneName,
            uint instanceId,
            float sourceX,
            float sourceY,
            float sourceZ,
            float sourceHeading,
            string owner)
        {
            string pathMapKey = conn != null ? ResolveConnectionInstanceKey(conn) : zoneName;
            PathMap pathMap = !string.IsNullOrWhiteSpace(pathMapKey) ? PathMapManager.Instance.GetPathMap(pathMapKey) : null;
            if (pathMap == null)
            {
                Debug.LogError($"[ITEM-PLACEMENT] source={owner ?? "unknown"} zone='{zoneName ?? ""}' instance={instanceId:X8} pathMap=missing result=source-fallback draws=0 pos=({sourceX:F1},{sourceY:F1},{sourceZ:F1}) native=ItemObject::SetPositionRandomly@0x0058B400 no-PathMap-no-RNG");
                return new NativeItemDropPlacement { X = sourceX, Y = sourceY, Z = sourceZ, Draws = 0, Result = "source-fallback" };
            }

            uint headingRaw = NativeRandomStreams.GenerateGlobalStatic(
                "ItemObject.SetPositionRandomly.heading",
                owner ?? "ItemObject::SetPositionRandomly");
            uint randomHeading = (headingRaw % 360u) << 8;
            uint sourceHeadingFixed8 = ToNativeHeadingFixed8(sourceHeading);
            uint reverseHeadingFixed8 = sourceHeadingFixed8 - 0xB400u;
            uint[] headings = { randomHeading, sourceHeadingFixed8, reverseHeadingFixed8 };
            string[] labels = { "random", "source", "reverse" };

            for (int i = 0; i < headings.Length; i++)
            {
                if (!TryNativeItemDropProbe(pathMap, sourceX, sourceY, headings[i], out float probeX, out float probeY, out uint minRadius))
                    continue;

                uint radius = NativeRandomStreams.GenerateGlobalStaticRangeInclusive(
                    minRadius,
                    25u,
                    "ItemObject.SetPositionRandomly.radius",
                    owner ?? "ItemObject::SetPositionRandomly");
                NativeHeadingVector(headings[i], out float dirX, out float dirY);
                float x = sourceX + dirX * radius;
                float y = sourceY + dirY * radius;
                float z = pathMap.GetHeightAt(x, y, sourceZ);
                Debug.LogError($"[ITEM-PLACEMENT-NATIVE] source={owner ?? "unknown"} zone='{zoneName ?? ""}' instance={instanceId:X8} pathMap='{pathMapKey}' result=random branch={labels[i]} draws=2 headingRaw=0x{headingRaw:X8} headingFixed8=0x{headings[i]:X8} probe=({probeX:F1},{probeY:F1}) radius={radius} pos=({x:F1},{y:F1},{z:F1}) native=ItemObject::SetPositionRandomly@0x0058B400 PathManager.FindFirstValidPointInDir@0x00589880 ItemObject.SetPositionRandomly.spinGroundRay");
                return new NativeItemDropPlacement { X = x, Y = y, Z = z, Draws = 2, Result = labels[i] };
            }

            Debug.LogError($"[ITEM-PLACEMENT-NATIVE] source={owner ?? "unknown"} zone='{zoneName ?? ""}' instance={instanceId:X8} pathMap='{pathMapKey}' result=source-fallback draws=1 headingRaw=0x{headingRaw:X8} pos=({sourceX:F1},{sourceY:F1},{sourceZ:F1}) native=ItemObject::SetPositionRandomly@0x0058B400 PathManager.FindFirstValidPointInDir@0x00589880 no-radius-draw");
            return new NativeItemDropPlacement { X = sourceX, Y = sourceY, Z = sourceZ, Draws = 1, Result = "source-fallback" };
        }

        private static bool TryNativeItemDropProbe(PathMap pathMap, float sourceX, float sourceY, uint headingFixed8, out float probeX, out float probeY, out uint minRadius)
        {
            NativeHeadingVector(headingFixed8, out float dx, out float dy);
            const int startRadius = 5;
            const int maxProbeRadius = 20;
            probeX = sourceX + dx * startRadius;
            probeY = sourceY + dy * startRadius;
            minRadius = 0;
            if (pathMap == null)
                return false;
            for (int radius = startRadius; radius <= maxProbeRadius; radius++)
            {
                probeX = sourceX + dx * radius;
                probeY = sourceY + dy * radius;
                if (!pathMap.IsWalkable(probeX, probeY))
                    continue;
                minRadius = (uint)Mathf.Clamp(radius, 0, 25);
                return true;
            }
            return false;
        }

        private static uint ToNativeHeadingFixed8(float headingDegrees)
        {
            int rounded = Mathf.RoundToInt(headingDegrees * 256f);
            return unchecked((uint)rounded);
        }

        private static void NativeHeadingVector(uint headingFixed8, out float x, out float y)
        {
            int degrees = (int)((headingFixed8 >> 8) % 360u);
            int tableIndex = (360 - degrees) % 360;
            float radians = tableIndex * Mathf.Deg2Rad;
            x = Mathf.Sin(radians);
            y = Mathf.Cos(radians);
        }

        private static bool IsBasicMeleeUseTargetFlag(byte useFlags)
        {
            return useFlags == 0x0A || useFlags == 0x0B;
        }

        private float ResolveAvatarMoveSpeed(RRConnection conn, PlayerState state)
        {
            string avatarGcType = conn?.AvatarGcType;
            if (string.IsNullOrWhiteSpace(avatarGcType))
                avatarGcType = conn?.Avatar?.GCClass;
            if (string.IsNullOrWhiteSpace(avatarGcType))
                avatarGcType = "avatar.base.avatar";

            var avatar = GCDatabase.Instance?.ResolveWithInheritance(avatarGcType);
            var desc = avatar?.GetChild("Description") ?? avatar;
            float baseSpeed = desc != null ? desc.GetFloat("Speed", 30f) : 30f;
            if (baseSpeed <= 0f) baseSpeed = 30f;
            RefreshMovementSpeedModifiers(conn, state);
            float speed = baseSpeed;
            int speedMod = state != null ? Mathf.Max(-100, state.MovementSpeedModPercent) : 0;
            if (speedMod != 0)
                speed *= Mathf.Max(0f, (100f + speedMod) / 100f);
            if (state != null && state.MinMovementSpeedModValue > 0 && speed < baseSpeed)
                speed = baseSpeed;
            return speed > 0f ? speed : 30f;
        }

        private float ResolveAvatarUseTargetApproachSpeed(RRConnection conn, PlayerState state)
        {
            return ResolveAvatarMoveSpeed(conn, state);
        }

        private void RefreshMovementSpeedModifiers(RRConnection conn, PlayerState state)
        {
            if (state == null) return;
            int speedModPercent = 0;
            int minSpeedModValue = 0;
            if (conn != null && !string.IsNullOrWhiteSpace(conn.LoginName))
            {
                foreach (var mod in _modifierTracker.GetModifiers(conn.LoginName))
                {
                    speedModPercent += ResolveMovementSpeedModPercent(mod);
                    minSpeedModValue = Math.Max(minSpeedModValue, ResolveMinMovementSpeedModValue(mod));
                }
            }
            state.SetMovementSpeedModifiers(speedModPercent, minSpeedModValue);
        }

        private static int ResolveMovementSpeedModPercent(ActiveModifier mod)
        {
            if (mod == null || string.IsNullOrWhiteSpace(mod.GCType)) return 0;
            int levelIndex = Math.Max(0, mod.Level - 1);
            if (string.Equals(mod.GCType, "skills.generic.Sprint.Modifier", StringComparison.OrdinalIgnoreCase))
                return 30 + levelIndex * 10;
            if (string.Equals(mod.GCType, "skills.generic.SlowDebuff.Modifier", StringComparison.OrdinalIgnoreCase))
                return -15 + levelIndex * -15;
            return 0;
        }

        private static int ResolveMinMovementSpeedModValue(ActiveModifier mod)
        {
            if (mod == null || string.IsNullOrWhiteSpace(mod.GCType)) return 0;
            if (string.Equals(mod.GCType, "skills.generic.MinMoveSpeedBuff.Modifier", StringComparison.OrdinalIgnoreCase))
                return 125;
            return 0;
        }

        private void FlushAllQueues()
        {
            foreach (var conn in _connections.Values)
            {
                if (!conn.AllowFlush)
                    continue;  // Don't flush during this player's zone transition

                if (conn.MessageQueue.IsEmpty())
                    continue;

                var writer = new LEWriter();
                writer.WriteByte(0x07); // BeginStream

                var messages = conn.MessageQueue.DequeueAll();
                foreach (var msg in messages)
                {
                    writer.WriteBytes(msg);
                }

                writer.WriteByte(0x06); // EndStream

                SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
            }
        }




        // SERVER → CLIENT RNG seed packet
        // messageType = 0x0D
        // payload = 12 bytes: [seed][0x21][0x0B] (all little-endian u32)
        // NO BeginStream wrapper.
        // TEST A3: A-lane dest=0x00, messageType=0x0D
        private void SendRandomSeed(RRConnection conn, uint seed, bool initializeRng = false)
        {
            if (conn == null)
            {
                Debug.LogError("[RNG-SEED] Cannot send seed - no connection!");
                return;
            }

            string instanceKey = GetInstanceZoneKey(conn);
            if (initializeRng)
                CombatManager.Instance.InitializeRoomRng(instanceKey, seed, "SendRandomSeed-initialize");
            else
            {
                CombatManager.Instance.EnsureRoomRng(instanceKey, seed, "SendRandomSeed-preserve-existing");
                uint effectiveSeed = CombatManager.Instance.GetRoomSeedForInstance(instanceKey);
                if (effectiveSeed != 0 && effectiveSeed != seed)
                {
                    Debug.LogError($"[RNG-SEED] Using preserved room seed instance='{instanceKey}' requested=0x{seed:X8} effective=0x{effectiveSeed:X8}");
                    seed = effectiveSeed;
                }
            }
            conn.EntitySchedulerMirror.Reset(instanceKey, "SendRandomSeed");

            // Entity stream: BeginStream + opcode 0x0C + seed + EndStream
            // NO 0x00 prefix - that causes "No queue for ChannelType(0)" error
            var w = new LEWriter();
            w.WriteByte(0x07);           // BeginStream
                                         // NEW:
            w.WriteByte(0x0C);           // processRandomSeed opcode (Case 7 in client switch)         // processRandomSeed opcode
            w.WriteUInt32(seed);         // Seed value (4 bytes)
            w.WriteByte(0x06);           // EndStream

            SendCompressedA(conn, 0x01, 0x0F, w.ToArray());
            Debug.LogError($"[RNG-SEED] Sent opcode 0x0C seed: 0x{seed:X8} instance='{instanceKey}' initialize={initializeRng}");
        }

        private bool TryPrepareZoneJoinRoomRuntime(
            RRConnection conn,
            string source,
            out Zone spawnZone,
            out string zoneName,
            out string instanceKey,
            out uint roomSeed,
            out uint layoutSeed)
        {
            spawnZone = null;
            zoneName = null;
            instanceKey = null;
            roomSeed = 0;
            layoutSeed = 0;

            if (conn == null || !_zones.TryGetValue(conn.CurrentZoneId, out spawnZone))
                return false;

            zoneName = spawnZone.name;
            instanceKey = GetInstanceZoneKey(conn);
            roomSeed = ResolveRuntimeZoneSeed(conn, zoneName);
            layoutSeed = ResolveZoneLayoutSeed(conn, zoneName);

            SendRandomSeed(conn, roomSeed, false);
            uint effectiveRoomSeed = CombatManager.Instance.GetRoomSeedForInstance(instanceKey);
            if (effectiveRoomSeed != 0)
                roomSeed = effectiveRoomSeed;

            Debug.LogError($"[ZONE-JOIN] Prepared room runtime before player spawn zone='{zoneName}' instance='{instanceKey}' entityManagerOpcode0CSeed=0x{roomSeed:X8} {FormatDungeonLayoutSeedForLog(zoneName, layoutSeed)} source={source ?? "unknown"}");
            return true;
        }

        private static string FormatDungeonLayoutSeedForLog(string zoneName, uint layoutSeed)
        {
            return !string.IsNullOrEmpty(zoneName) && DungeonMazeSpawner.IsProceduralZone(zoneName)
                ? $"dungeonLayoutSeed=0x{layoutSeed:X8}"
                : $"dungeonLayoutSeed=n/a(seedSlot=0x{layoutSeed:X8})";
        }

        private uint ResolveZoneConnectSeed(RRConnection conn, string zoneName)
        {
            uint seed = ResolveDungeonLayoutSeed(conn, zoneName);
            if (conn != null)
                _pendingZoneConnectSeeds[conn.ConnId] = seed;
            return seed;
        }

        private uint ResolveZoneLayoutSeed(RRConnection conn, string zoneName)
        {
            if (!string.IsNullOrEmpty(zoneName)
                && DungeonMazeSpawner.IsProceduralZone(zoneName)
                && conn != null
                && _pendingZoneConnectSeeds.TryGetValue(conn.ConnId, out uint seed))
                return seed;

            return ResolveDungeonLayoutSeed(conn, zoneName);
        }

        private uint ResolveDungeonLayoutSeed(RRConnection conn, string zoneName)
        {
            if (conn == null || string.IsNullOrEmpty(zoneName) || !DungeonMazeSpawner.IsProceduralZone(zoneName))
                return 0xBEEFBEEF;

            string key = GetDungeonLayoutSeedKey(conn, zoneName);
            var group = GroupManager.Instance.GetGroupForConn(conn.ConnId);
            if (group != null && conn.InstanceId == group.GroupId)
            {
                _zoneInstanceLayoutSeeds[key] = group.InstanceSeed;
                return group.InstanceSeed;
            }

            if (_zoneInstanceLayoutSeeds.TryGetValue(key, out uint seed))
                return seed;

            seed = GenerateDungeonLayoutSeed();
            _zoneInstanceLayoutSeeds[key] = seed;
            Debug.LogError($"[LAYOUT-SEED] zone={zoneName} instance={conn.InstanceId:X8} seed=0x{seed:X8} owner={GetSoloDungeonInstanceOwnerKey(conn, zoneName)}");
            return seed;
        }

        private string GetDungeonLayoutSeedKey(RRConnection conn, string zoneName)
        {
            return $"{zoneName ?? string.Empty}:inst{(conn?.InstanceId ?? 0):X8}";
        }

        private string GetSoloDungeonInstanceOwnerKey(uint charId, string zoneName)
        {
            string owner = charId != 0 ? $"char{charId}" : "char0";
            return $"{owner}:{zoneName ?? string.Empty}";
        }

        private string GetSoloDungeonInstanceOwnerKey(RRConnection conn, string zoneName)
        {
            uint charId = conn != null ? GetCharSqlId(conn) : 0;
            if (charId != 0)
                return GetSoloDungeonInstanceOwnerKey(charId, zoneName);

            string owner = $"conn{(conn?.ConnId ?? 0)}";
            return $"{owner}:{zoneName ?? string.Empty}";
        }

        private bool IsSoloDungeonMemoryExpired(string key)
        {
            if (string.IsNullOrEmpty(key))
                return true;
            if (!_soloDungeonLastActiveUtc.TryGetValue(key, out DateTime lastActiveUtc))
                return false;
            return (DateTime.UtcNow - lastActiveUtc).TotalSeconds > SoloDungeonMemorySeconds;
        }

        private void ForgetSoloDungeonInstance(string key, string zoneName, uint instanceId, string reason)
        {
            if (string.IsNullOrEmpty(key))
                return;

            _soloDungeonInstanceIds.Remove(key);
            _soloDungeonLastActiveUtc.Remove(key);
            string instanceKey = $"{zoneName ?? string.Empty}_inst{instanceId}";
            string seedKey = $"{zoneName ?? string.Empty}:inst{instanceId:X8}";
            _zoneInstanceLayoutSeeds.Remove(seedKey);
            _zoneInstanceRoomSeeds.Remove(seedKey);
            ZoneSpawnManager.Instance.ResetZone(instanceKey);
            Debug.LogError($"[INSTANCE-STATE] zone={zoneName ?? ""} owner={key} instance={instanceId:X8} state=forgot reason={reason ?? "unknown"}");
        }

        private bool HasRememberedSoloDungeonInstance(uint charId, string zoneName, out uint instanceId, out bool expired)
        {
            instanceId = 0;
            expired = false;
            if (charId == 0 || string.IsNullOrEmpty(zoneName))
                return false;

            string key = GetSoloDungeonInstanceOwnerKey(charId, zoneName);
            if (!_soloDungeonInstanceIds.TryGetValue(key, out instanceId))
                return false;

            if (IsSoloDungeonMemoryExpired(key))
            {
                expired = true;
                ForgetSoloDungeonInstance(key, zoneName, instanceId, "memory-expired");
                instanceId = 0;
                return false;
            }

            _soloDungeonLastActiveUtc[key] = DateTime.UtcNow;
            return true;
        }

        private void TouchSoloDungeonInstance(RRConnection conn, string reason)
        {
            if (conn == null || string.IsNullOrEmpty(conn.CurrentZoneName) || IsPublicZone(conn.CurrentZoneName))
                return;
            if (GroupManager.Instance.GetGroupForConn(conn.ConnId) != null)
                return;

            string key = GetSoloDungeonInstanceOwnerKey(conn, conn.CurrentZoneName);
            if (!_soloDungeonInstanceIds.ContainsKey(key))
                return;

            _soloDungeonLastActiveUtc[key] = DateTime.UtcNow;
            Debug.LogError($"[INSTANCE-STATE] zone={conn.CurrentZoneName} owner={key} instance={conn.InstanceId:X8} state=touch reason={reason ?? "unknown"}");
        }

        private uint AllocateSoloDungeonInstanceId(RRConnection conn, string zoneName)
        {
            string key = GetSoloDungeonInstanceOwnerKey(conn, zoneName);
            if (_soloDungeonInstanceIds.TryGetValue(key, out uint instanceId))
            {
                if (IsSoloDungeonMemoryExpired(key))
                {
                    ForgetSoloDungeonInstance(key, zoneName, instanceId, "allocate-expired");
                }
                else
                {
                    _soloDungeonLastActiveUtc[key] = DateTime.UtcNow;
                    Debug.LogError($"[INSTANCE-STATE] zone={zoneName ?? ""} owner={key} instance={instanceId:X8} state=late-join activeSolo={_soloDungeonInstanceIds.Count}");
                    return instanceId;
                }
            }

            _nextSoloDungeonInstanceId++;
            if (_nextSoloDungeonInstanceId < 0x80000000u)
                _nextSoloDungeonInstanceId = 0x80000001u;

            _soloDungeonInstanceIds[key] = _nextSoloDungeonInstanceId;
            _soloDungeonLastActiveUtc[key] = DateTime.UtcNow;
            Debug.LogError($"[INSTANCE-STATE] zone={zoneName ?? ""} owner={key} instance={_nextSoloDungeonInstanceId:X8} state=fresh activeSolo={_soloDungeonInstanceIds.Count}");
            return _nextSoloDungeonInstanceId;
        }

        private uint GenerateDungeonLayoutSeed()
        {
            byte[] bytes = new byte[4];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);

            uint seed = BitConverter.ToUInt32(bytes, 0) ^ (uint)(DateTime.Now.Ticks & 0xFFFFFFFF);
            return seed != 0 ? seed : 1u;
        }

        private uint ResolveRuntimeZoneSeed(RRConnection conn, string zoneName)
        {
            if (conn == null)
            {
                uint fallbackSeed = GenerateDungeonLayoutSeed();
                string fallbackKey = $"no-conn:{zoneName ?? string.Empty}:{fallbackSeed:X8}";
                if (_loggedRuntimeZoneSeeds.Add(fallbackKey))
                    Debug.LogError($"[RUNTIME-SEED] conn=<null> zone={zoneName ?? "<null>"} instance=<none> seed=0x{fallbackSeed:X8} source=fallback");
                return fallbackSeed;
            }

            string instanceKey = GetDungeonLayoutSeedKey(conn, zoneName);
            var group = GroupManager.Instance.GetGroupForConn(conn.ConnId);
            if (group != null)
            {
                if (group.EntityManagerSeed == 0)
                {
                    group.EntityManagerSeed = GenerateDungeonLayoutSeed();
                    Debug.LogError($"[RUNTIME-SEED] generated missing group entity-manager seed groupId={group.GroupId} seed=0x{group.EntityManagerSeed:X8}");
                }

                uint groupSeed = group.EntityManagerSeed != 0 ? group.EntityManagerSeed : 1u;
                if (_zoneInstanceRoomSeeds.TryGetValue(instanceKey, out uint cachedGroupSeed) && cachedGroupSeed != groupSeed)
                    Debug.LogError($"[RUNTIME-SEED] replace instance='{instanceKey}' cached=0x{cachedGroupSeed:X8} group=0x{groupSeed:X8} groupId={group.GroupId}");
                _zoneInstanceRoomSeeds[instanceKey] = groupSeed;
                LogRuntimeZoneSeed(conn, zoneName, instanceKey, groupSeed, $"group-room={group.GroupId}");
                return groupSeed;
            }

            if (!_zoneInstanceRoomSeeds.TryGetValue(instanceKey, out uint seed))
            {
                seed = GenerateDungeonLayoutSeed();
                _zoneInstanceRoomSeeds[instanceKey] = seed;
                LogRuntimeZoneSeed(conn, zoneName, instanceKey, seed, "solo-new");
                return seed;
            }

            LogRuntimeZoneSeed(conn, zoneName, instanceKey, seed, "solo-cache");
            return seed;
        }

        private void LogRuntimeZoneSeed(RRConnection conn, string zoneName, string instanceKey, uint seed, string source)
        {
            string key = $"{conn.ConnId}:{zoneName ?? string.Empty}:{instanceKey}:{seed:X8}";
            if (_loggedRuntimeZoneSeeds.Add(key))
                Debug.LogError($"[RUNTIME-SEED] conn={conn.ConnId} zone={zoneName ?? "<null>"} instance='{instanceKey}' seed=0x{seed:X8} source={source}");
        }

        public PlayerState GetPlayerState(string connId)
        {
            if (!_playerStates.ContainsKey(connId))
            {
                _playerStates[connId] = new PlayerState();
            }
            return _playerStates[connId];
        }

        private static bool IsPassiveSkill(string skillGcClass)
        {
            if (string.IsNullOrEmpty(skillGcClass)) return false;
            string lower = skillGcClass.ToLowerInvariant();
            return lower.Contains("passive") || lower.Contains("trait");
        }

        private static bool TryGetClassPassiveForSkill(string skillGcClass, out string className, out ClassPassive passive)
        {
            className = null;
            passive = null;
            if (string.IsNullOrEmpty(skillGcClass)) return false;
            foreach (var kvp in ClassPassiveData.Passives)
            {
                if (string.Equals(kvp.Value.PassiveSkillId, skillGcClass, StringComparison.OrdinalIgnoreCase))
                {
                    className = kvp.Key;
                    passive = kvp.Value;
                    return true;
                }
            }
            return false;
        }

        private static string NormalizeClassPassiveKey(string className)
        {
            if (string.IsNullOrWhiteSpace(className)) return "Fighter";
            string key = className.Trim();
            if (key.EndsWith("Base", StringComparison.OrdinalIgnoreCase))
                key = key.Substring(0, key.Length - 4);
            int lastDot = key.LastIndexOf('.');
            if (lastDot >= 0 && lastDot + 1 < key.Length)
                key = key.Substring(lastDot + 1);
            if (key.Equals("Warlock", StringComparison.OrdinalIgnoreCase))
                return "Mage";
            if (key.Equals("Warrior", StringComparison.OrdinalIgnoreCase))
                return "Fighter";
            if (key.Equals("Ranger", StringComparison.OrdinalIgnoreCase))
                return "Ranger";
            if (key.Equals("Mage", StringComparison.OrdinalIgnoreCase))
                return "Mage";
            if (key.Equals("Fighter", StringComparison.OrdinalIgnoreCase))
                return "Fighter";
            return key;
        }

        private static string ResolveSavedCharacterClassPassiveKey(SavedCharacter savedChar)
        {
            if (!string.IsNullOrWhiteSpace(savedChar?.className))
                return NormalizeClassPassiveKey(savedChar.className);
            if (!string.IsNullOrWhiteSpace(savedChar?.avatarClass))
                return NormalizeClassPassiveKey(savedChar.avatarClass);
            return null;
        }

        private static List<string> GetNativePassiveSkillSources(SavedCharacter savedChar)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string classKey = ResolveSavedCharacterClassPassiveKey(savedChar);
            if (!string.IsNullOrWhiteSpace(classKey) &&
                ClassPassiveData.Passives.TryGetValue(classKey, out ClassPassive classPassive) &&
                !string.IsNullOrWhiteSpace(classPassive.PassiveSkillId) &&
                seen.Add(classPassive.PassiveSkillId))
            {
                result.Add(classPassive.PassiveSkillId);
            }

            if (savedChar?.hotbarSlots != null)
            {
                foreach (HotbarSlotEntry slot in savedChar.hotbarSlots)
                {
                    string skill = slot?.skill;
                    if (string.IsNullOrEmpty(skill) || !IsPassiveSkill(skill) || !seen.Add(skill))
                        continue;
                    result.Add(skill);
                }
            }

            return result;
        }

        private struct PlayerHPPreserve
        {
            public bool HasHP;
            public uint HPWire;
            public uint MaxAtCapture;
            public string Source;
            public bool FromLiveState;
            public bool FromObserved;
            public bool FromSaved;
        }

        private static bool IsPreservedHPWithinTolerance(uint hpWire, uint maxHPWire)
        {
            if (hpWire == 0) return false;
            if (maxHPWire == 0) return true;
            return (ulong)hpWire <= (ulong)maxHPWire + (5u * 256u);
        }

        private static PlayerHPPreserve MakePlayerHPPreserve(uint hpWire, uint maxAtCapture, string source, bool live, bool observed, bool saved)
        {
            return new PlayerHPPreserve
            {
                HasHP = hpWire > 0,
                HPWire = hpWire,
                MaxAtCapture = maxAtCapture,
                Source = source ?? "unknown",
                FromLiveState = live,
                FromObserved = observed,
                FromSaved = saved
            };
        }

        private PlayerHPPreserve CapturePlayerHPPreserve(RRConnection conn, PlayerState playerState, SavedCharacter savedChar, string phase, bool includeSavedCharacter)
        {
            PlayerHPPreserve preserve = default;
            uint maxAtCapture = playerState != null ? playerState.MaxHPWire : 0;

            if (playerState != null)
            {
                if (playerState.HasClientHP && playerState.CurrentHPWire > 0)
                    preserve = MakePlayerHPPreserve(playerState.CurrentHPWire, maxAtCapture, "live-playerstate", true, false, false);
                else if (playerState.HasObservedClientHP && playerState.LastObservedClientHPWire > 0)
                    preserve = MakePlayerHPPreserve(playerState.LastObservedClientHPWire, maxAtCapture, $"observed-client:{playerState.LastObservedClientHPSource ?? "unknown"}", false, true, false);
                else if (playerState.HasClientSyncHP && playerState.SynchHP > 0)
                    Debug.LogError($"[HP-PRESERVE] phase={phase}-ignore-synthetic-sync hp={playerState.SynchHP} maxAtCapture={maxAtCapture} source=server-sync native=client-mirror-only");

            }

            if (!preserve.HasHP && includeSavedCharacter && savedChar != null && savedChar.currentHP > 0)
                preserve = MakePlayerHPPreserve(savedChar.currentHP, maxAtCapture, "saved-character", false, false, true);

            Debug.LogError($"[HP-PRESERVE] phase={phase}-capture source={(preserve.HasHP ? preserve.Source : "none")} hp={preserve.HPWire} maxAtCapture={maxAtCapture} live={preserve.FromLiveState} observed={preserve.FromObserved} saved={preserve.FromSaved}");
            return preserve;
        }

        private bool ApplyPlayerHPPreserve(RRConnection conn, PlayerState playerState, PlayerHPPreserve preserve, string phase, bool applyNativeDamageCooldown)
        {
            if (playerState == null) return false;
            uint beforeHP = playerState.CurrentHPWire;
            uint maxAfter = playerState.MaxHPWire;
            if (!preserve.HasHP)
            {
                Debug.LogError($"[HP-PRESERVE] phase={phase} source=none before={beforeHP} maxAfter={maxAfter} applied={playerState.CurrentHPWire} sync={playerState.SynchHP}");
                return false;
            }

            uint appliedHP = maxAfter > 0 ? Math.Min(preserve.HPWire, maxAfter) : preserve.HPWire;
            playerState.SetCurrentHP(appliedHP, applyNativeDamageCooldown && appliedHP < maxAfter);
            if (conn?.Avatar != null && conn.Avatar.Id != 0)
                HpSyncService.Instance.RegisterPlayer(conn, playerState, (uint)conn.Avatar.Id);
            Debug.LogError($"[HP-PRESERVE] phase={phase} source={preserve.Source} captured={preserve.HPWire} before={beforeHP} maxBefore={preserve.MaxAtCapture} maxAfter={maxAfter} applied={playerState.CurrentHPWire} sync={playerState.SynchHP} live={preserve.FromLiveState} observed={preserve.FromObserved} saved={preserve.FromSaved}");
            return true;
        }

        private void ApplyNativeFullHPBootstrap(RRConnection conn, PlayerState playerState, PlayerHPPreserve ignoredPreserve, string phase)
        {
            if (playerState == null) return;
            uint beforeHP = playerState.CurrentHPWire;
            uint beforeSync = playerState.SynchHP;
            uint maxBefore = playerState.MaxHPWire;
            playerState.RestoreToFull();
            if (conn?.Avatar != null && conn.Avatar.Id != 0)
                HpSyncService.Instance.RegisterPlayer(conn, playerState, (uint)conn.Avatar.Id);
            Debug.LogError($"[ZONE-HP-BOOTSTRAP] phase={phase} ignoredSource={(ignoredPreserve.HasHP ? ignoredPreserve.Source : "none")} ignoredHP={ignoredPreserve.HPWire} before={beforeHP}/{maxBefore} beforeSync={beforeSync} applied={playerState.CurrentHPWire}/{playerState.MaxHPWire} sync={playerState.SynchHP}");
        }

        private static int GetPassiveHealthModPercent(string skillGcClass)
        {
            if (string.Equals(skillGcClass, "skills.generic.MeleeAttackRatingModPassive", StringComparison.OrdinalIgnoreCase))
                return -20;
            return 0;
        }

        private static int GetPassiveMeleeAttackRatingModPercent(string skillGcClass, int skillLevel)
        {
            if (string.Equals(skillGcClass, "skills.generic.MeleeAttackSpeedModPassive", StringComparison.OrdinalIgnoreCase))
                return 100;
            if (string.Equals(skillGcClass, "skills.generic.MeleeAttackRatingModPassive", StringComparison.OrdinalIgnoreCase))
                return 80 + Math.Max(0, skillLevel - 1) * 20;
            return 0;
        }

        private static float GetPassiveMeleeAttackSpeedModPercent(string skillGcClass, int skillLevel)
        {
            if (string.Equals(skillGcClass, "skills.generic.MeleeAttackSpeedModPassive", StringComparison.OrdinalIgnoreCase))
                return 25f;
            if (string.Equals(skillGcClass, "skills.generic.RangeAttackSpeedModPassive", StringComparison.OrdinalIgnoreCase))
                return -6.25f;
            return 0f;
        }

        private static float GetPassiveRangeAttackSpeedModPercent(string skillGcClass, int skillLevel)
        {
            if (string.Equals(skillGcClass, "skills.generic.RangeAttackSpeedModPassive", StringComparison.OrdinalIgnoreCase))
                return 25f;
            if (string.Equals(skillGcClass, "skills.generic.MeleeAttackSpeedModPassive", StringComparison.OrdinalIgnoreCase))
                return -6.25f;
            return 0f;
        }

        private void RecalculateHotbarPassiveBonuses(string connId)
        {
            RRConnection conn = _connections.Values.FirstOrDefault(c => c.ConnId.ToString() == connId);
            if (conn == null || conn.LoginName == null || !_selectedCharacter.ContainsKey(conn.LoginName))
            {
                PlayerState fallbackState = GetPlayerState(connId);
                PlayerHPPreserve fallbackHP = CapturePlayerHPPreserve(conn, fallbackState, null, "passive", false);
                fallbackState.SetPassiveBonuses(0, 0);
                ApplyPlayerHPPreserve(conn, fallbackState, fallbackHP, "passive", true);
                return;
            }

            SavedCharacter savedChar = CharacterRepository.GetCharacter(_selectedCharacter[conn.LoginName].Id);
            RecalculateHotbarPassiveBonuses(conn, savedChar);
        }

        private void RecalculateHotbarPassiveBonuses(RRConnection conn, SavedCharacter savedChar)
        {
            if (conn == null) return;

            PlayerState playerState = GetPlayerState(conn.ConnId.ToString());
            PlayerHPPreserve hpPreserve = CapturePlayerHPPreserve(conn, playerState, savedChar, "passive", false);
            int hpWireBonus = 0;
            int manaWireBonus = 0;
            int healthPercentMod = 0;
            int strengthMod = 0;
            int agilityMod = 0;
            int enduranceMod = 0;
            int intellectMod = 0;
            int meleeAttackRatingModPercent = 0;
            float meleeAttackSpeedModPercent = 0f;
            float rangeAttackSpeedModPercent = 0f;
            List<string> passiveSkills = GetNativePassiveSkillSources(savedChar);

            if (passiveSkills.Count > 0)
            {
                foreach (string skillGcClass in passiveSkills)
                {
                    if (TryGetClassPassiveForSkill(skillGcClass, out string className, out ClassPassive passive))
                    {
                        hpWireBonus += ClassPassiveData.CalculateHPBonusWire(className, playerState.Level, playerState.AllocatedEndurance);
                        manaWireBonus += ClassPassiveData.CalculateManaBonusWire(className, playerState.Level, playerState.AllocatedIntellect);
                        strengthMod += passive.StrengthMod;
                        agilityMod += passive.AgilityMod;
                        enduranceMod += passive.EnduranceMod;
                        intellectMod += passive.IntellectMod;
                        rangeAttackSpeedModPercent += passive.RangeAttackSpeedMod;
                    }

                    int skillLevel = Math.Max(1, savedChar.GetSkillLevel(skillGcClass));
                    healthPercentMod += GetPassiveHealthModPercent(skillGcClass);
                    meleeAttackRatingModPercent += GetPassiveMeleeAttackRatingModPercent(skillGcClass, skillLevel);
                    meleeAttackSpeedModPercent += GetPassiveMeleeAttackSpeedModPercent(skillGcClass, skillLevel);
                    rangeAttackSpeedModPercent += GetPassiveRangeAttackSpeedModPercent(skillGcClass, skillLevel);
                }
            }

            if (healthPercentMod != 0)
                hpWireBonus += (int)Math.Round(((long)playerState.MaxHPWireWithoutPassives + hpWireBonus) * (healthPercentMod / 100.0));

            playerState.SetPassiveBonuses(hpWireBonus, manaWireBonus, meleeAttackRatingModPercent, meleeAttackSpeedModPercent, rangeAttackSpeedModPercent, strengthMod, agilityMod, enduranceMod, intellectMod);
            ApplyPlayerHPPreserve(conn, playerState, hpPreserve, "passive", true);
            Debug.LogError($"[PASSIVE-STATS] {conn.LoginName}: source=native-class-plus-active-hotbar class={ResolveSavedCharacterClassPassiveKey(savedChar) ?? "unknown"} passives={string.Join(",", passiveSkills)} HP passive {hpWireBonus} wire, Mana passive {manaWireBonus} wire, STR={strengthMod} AGI={agilityMod} END={enduranceMod} INT={intellectMod}, MeleeARMod={meleeAttackRatingModPercent}, MeleeSpeedMod={meleeAttackSpeedModPercent:F2}, RangeSpeedMod={rangeAttackSpeedModPercent:F2}");
        }

        private static int ResolveNativeItemModSlotDivisor(GCObject item, string gcClass)
        {
            if (item == null)
                return 8;

            string nativeClass = item.NativeClass ?? string.Empty;
            if (nativeClass == "MeleeWeapon" || nativeClass == "RangedWeapon")
                return 8;

            uint slot = item.TargetSlot ?? 0;
            if (slot == 0)
                slot = item.GetEquipmentSlotFromGCClass();

            return slot switch
            {
                2 or 8 => 20,   // gloves, shoulders
                6 => 5,         // body armor
                5 or 7 or 11 => 10, // helm, boots, shield
                _ => 8
            };
        }

        private static int ResolveNativeEquipmentItemModLevel(GCObject item, string gcClass, int rarity, int playerLevel)
        {
            if (item != null && item.StoredLevel >= 0)
                return Math.Max(1, item.StoredLevel);
            if (rarity == (int)ItemRarity.Mythic)
                return Math.Max(1, playerLevel + 3);
            return Math.Max(1, RarityHelper.GetItemLevel(gcClass));
        }

        private bool TryResolveNativeWeaponDescIds(
            string source,
            string weaponPath,
            string weaponClass,
            string damageType,
            out int weaponClassId,
            out int damageTypeId)
        {
            bool classOk = DamageComputer.TryResolveNativeWeaponClassId(weaponClass, out weaponClassId);
            bool typeOk = DamageComputer.TryResolveNativeDamageTypeId(damageType, out damageTypeId);
            if (classOk && typeOk)
                return true;

            RuntimeEvidenceManager.LogFallbackHit(
                "damage-weapon-desc",
                "unresolved-native-id",
                $"source={source ?? "<null>"} weapon={weaponPath ?? "<null>"} weaponClass={weaponClass ?? "<null>"} damageType={damageType ?? "<null>"} classOk={classOk} typeOk={typeOk} native=Weapon::ComputeAttributes-blocked",
                64);
            return false;
        }

        public void CalculateEquipmentBonuses(string connId, GCObject avatar)
        {
            PlayerState playerState = GetPlayerState(connId);
            RRConnection hpConn = _connections.Values.FirstOrDefault(c => c.ConnId.ToString() == connId);
            SavedCharacter hpSavedChar = null;
            if (hpConn != null && hpConn.LoginName != null && _selectedCharacter.ContainsKey(hpConn.LoginName))
                hpSavedChar = CharacterRepository.GetCharacter(_selectedCharacter[hpConn.LoginName].Id);
            PlayerHPPreserve hpPreserve = CapturePlayerHPPreserve(hpConn, playerState, hpSavedChar, "equip", false);
            playerState.ClearEquipmentBonuses();

            // Reset weapon to defaults — will be overwritten if weapon found
            float bestWeaponDamage = 0.79f;
            float bestWeaponVolatility = 0.25f;
            int bestWeaponLevel = 1;
            int bestWeaponStoredLevel = -1;
            string bestWeaponClass = "";
            string bestWeaponDamageType = "";
            string bestWeaponCategory = "";
            int bestNativeWeaponClassId = 0;
            int bestNativeDamageTypeId = -1;
            int bestWeaponRange = 0;
            float bestWeaponCooldown = 0f;
            float bestWeaponSpeed = 105f;
            bool bestWeaponUsesProjectile = false;
            float bestWeaponProjectileSpeed = 0f;
            float bestWeaponProjectileSize = 0f;
            int bestWeaponBurstCount = 1;
            bool foundWeapon = false;

            if (avatar == null) { Debug.LogError("[EQUIP-STATS] avatar is NULL"); return; }
            var equipment = avatar.Children?.FirstOrDefault(c => c.GCClass == "avatar.base.Equipment");

            // Build item list: tracked items are authoritative only after they contain
            // real equip/unequip state. An empty tracker can exist before the first
            // live equip event; the avatar equipment tree is still the spawn truth then.
            var allItems = new Dictionary<string, GCObject>(StringComparer.OrdinalIgnoreCase);
            bool usedTrackedItems = false;
            if (_playerEquippedItems.TryGetValue(connId, out var tracked) && tracked != null && tracked.Count > 0)
            {
                foreach (var kvp in tracked)
                    if (kvp.Value?.GCClass != null)
                        allItems[kvp.Value.GCClass] = kvp.Value;
                usedTrackedItems = allItems.Count > 0;
                Debug.LogError($"[EQUIP-STATS] Using {allItems.Count}/{tracked.Count} TRACKED items (authoritative)");
            }
            if (!usedTrackedItems && equipment?.Children != null)
            {
                // Fallback: first spawn before any tracking exists
                foreach (var child in equipment.Children)
                    allItems[child.GCClass ?? ""] = child;
                Debug.LogError($"[EQUIP-STATS] Using {allItems.Count} AVATAR children (runtime equipment fallback)");
            }

            Debug.LogError($"[EQUIP-STATS] Processing {allItems.Count} equipped items, DB loaded={DungeonRunners.Data.ItemStatDatabase.Instance.IsLoaded}");

            var itemStatDb = DungeonRunners.Data.ItemStatDatabase.Instance;

            foreach (var item in allItems.Values)
            {
                string gc = item.GCClass ?? "";
                int rarity = item.GetEffectiveRarity();
                string pattern = DungeonRunners.Data.ItemStatDatabase.ExtractPattern(gc);
                bool isWeapon = item.NativeClass == "MeleeWeapon" || item.NativeClass == "RangedWeapon";
                bool isArmor = item.NativeClass == "Armor";
                if (isArmor)
                {
                    float armorDefense = GCDatabase.Instance.GetArmorDefenseRating(gc);
                    if (armorDefense > 0f)
                    {
                        int armorLevel = Math.Max(1, item.StoredLevel >= 0 ? item.StoredLevel : DungeonRunners.Managers.RarityHelper.GetItemLevel(gc));
                        float itemDefensePerLevel = GCDatabase.Instance.GetKnob("ItemDefenseRatingPerLevel", 8.26f);
                        int defenseRating = Mathf.Max(0, Mathf.FloorToInt(itemDefensePerLevel * armorLevel * armorDefense) + 1);
                        playerState.AddArmorDefenseRating(defenseRating);
                        if (playerState.EquipmentStats.ContainsKey("DEFENSE_RATING"))
                            playerState.EquipmentStats["DEFENSE_RATING"] += defenseRating;
                        else
                            playerState.EquipmentStats["DEFENSE_RATING"] = defenseRating;
                        Debug.LogError($"[EQUIP-ARMOR] {gc}: level={armorLevel} armorDefense={armorDefense:F4} defenseRating={defenseRating}");
                    }
                }

                if ((isWeapon || isArmor) && itemStatDb.IsLoaded && itemStatDb.HasItem(gc, rarity))
                {
                    int slotDivisor = ResolveNativeItemModSlotDivisor(item, gc);
                    int itemModLevel = ResolveNativeEquipmentItemModLevel(item, gc, rarity, playerState.Level);
                    var stats = itemStatDb.GetItemStatsAtItemLevel(gc, itemModLevel, slotDivisor, rarity);
                    var attrs = itemStatDb.GetItemAttributes(gc, rarity);
                    Debug.LogError($"[EQUIP-ITEM] {gc} -> DB hit rarity={rarity} itemLevel={itemModLevel} slotDivisor={slotDivisor} {stats.Count} stats: {string.Join(", ", attrs)}");

                    stats.TryGetValue("MAX_HIT_POINTS", out int hpBonus);
                    stats.TryGetValue("ENDURANCE", out int endBonus);
                    stats.TryGetValue("MAX_MANA_POINTS", out int manaBonus);
                    stats.TryGetValue("INTELLECT", out int intBonus);

                    if (hpBonus > 0) playerState.AddTotalHealthBonus(hpBonus);
                    if (endBonus > 0) playerState.AddEnduranceBonus(endBonus);
                    if (manaBonus > 0) playerState.AddManaBonus(manaBonus);
                    if (intBonus > 0) playerState.AddIntellectManaBonus(intBonus);

                    // Accumulate ALL stats for character tracking
                    foreach (var kvp in stats)
                    {
                        if (playerState.EquipmentStats.ContainsKey(kvp.Key))
                            playerState.EquipmentStats[kvp.Key] += kvp.Value;
                        else
                            playerState.EquipmentStats[kvp.Key] = kvp.Value;
                    }

                    if (hpBonus > 0 || endBonus > 0 || manaBonus > 0 || intBonus > 0)
                        Debug.LogError($"[EQUIP-STATS] {gc}: HP+{hpBonus} END+{endBonus} MANA+{manaBonus} INT+{intBonus}(+{intBonus * GCDatabase.Instance.GetKnobInt("PowerPerIntellect", 17)}mp)");
                }
                else if (isArmor || isWeapon)
                {
                    Debug.LogError($"[EQUIP-ITEM] {gc} -> no authored ItemModifier rows for nativeClass={item.NativeClass} pattern={pattern} rarity={rarity}; stats unchanged native=ItemModifier::AddModifiers@0x00588890");
                }

                // Check if this is a weapon — update WeaponDamage for combat formula
                if (item.NativeClass == "MeleeWeapon" || item.NativeClass == "RangedWeapon")
                {
                    var weaponData = DatabaseLoader.FindItem(gc);
                    var weaponNode = GCDatabase.Instance.ResolveWithInheritance(gc);
                    (float damage, float volatility, float range, float cooldown, string weaponClass, float weaponSpeed,
                     string damageType, string weaponCategory, bool useProjectile, float projectileSpeed, float projectileSize, int burstCount) weaponStats = default;
                    if (weaponNode == null)
                    {
                        RuntimeEvidenceManager.LogFallbackHit("damage-weapon-desc", "missing-gc-node", $"source=equip weapon={gc} native=Weapon::ComputeAttributes-return", 64);
                        continue;
                    }

                    weaponStats = GCDatabase.Instance.GetWeaponStats(gc);
                    float authoredWeaponDamage = weaponStats.damage > 0f ? weaponStats.damage : 0f;
                    float authoredWeaponVolatility = weaponStats.volatility > 0f ? weaponStats.volatility : 0.25f;
                    if (authoredWeaponDamage > 0f)
                    {
                        string resolvedWeaponClass = !string.IsNullOrEmpty(weaponStats.weaponClass) ? weaponStats.weaponClass : weaponData != null && !string.IsNullOrEmpty(weaponData.weaponClass) ? weaponData.weaponClass : "";
                        string resolvedWeaponDamageType = !string.IsNullOrEmpty(weaponStats.damageType) ? weaponStats.damageType : "";
                        if (!TryResolveNativeWeaponDescIds("equip", gc, resolvedWeaponClass, resolvedWeaponDamageType, out int nativeWeaponClassId, out int nativeDamageTypeId))
                            continue;

                        bestWeaponDamage = authoredWeaponDamage;
                        bestWeaponVolatility = Mathf.Clamp(authoredWeaponVolatility, 0f, 0.95f);
                        bestWeaponLevel = Math.Max(1, item.StoredLevel >= 0 ? item.StoredLevel : DungeonRunners.Managers.RarityHelper.GetItemLevel(gc));
                        bestWeaponStoredLevel = item.StoredLevel;
                        bestWeaponClass = resolvedWeaponClass;
                        bestWeaponDamageType = resolvedWeaponDamageType;
                        bestWeaponCategory = !string.IsNullOrEmpty(weaponStats.weaponCategory) ? weaponStats.weaponCategory : "";
                        bestNativeWeaponClassId = nativeWeaponClassId;
                        bestNativeDamageTypeId = nativeDamageTypeId;
                        bestWeaponRange = weaponStats.range > 0 ? Mathf.RoundToInt(weaponStats.range) : weaponData != null && weaponData.range > 0 ? weaponData.range : 0;
                        bestWeaponCooldown = weaponStats.cooldown > 0 ? weaponStats.cooldown : weaponData != null && weaponData.cooldown > 0 ? weaponData.cooldown : 0f;
                        bestWeaponSpeed = weaponStats.weaponSpeed > 0 ? weaponStats.weaponSpeed : weaponData != null && weaponData.weaponSpeed > 0 ? weaponData.weaponSpeed : 105f;
                        bestWeaponUsesProjectile = weaponStats.useProjectile;
                        bestWeaponProjectileSpeed = weaponStats.projectileSpeed;
                        bestWeaponProjectileSize = weaponStats.projectileSize;
                        bestWeaponBurstCount = Math.Max(1, weaponStats.burstCount);
                        foundWeapon = true;
                        Debug.LogError($"[EQUIP-WEAPON] {gc}: dmg={bestWeaponDamage:F2} vol={bestWeaponVolatility:F2} level={bestWeaponLevel} class={bestWeaponClass} damageType={bestWeaponDamageType} category={bestWeaponCategory} range={bestWeaponRange} cd={bestWeaponCooldown:F2} speed={bestWeaponSpeed:F2} useProjectile={bestWeaponUsesProjectile} projectileSpeed={bestWeaponProjectileSpeed:F2} projectileSize={bestWeaponProjectileSize:F2} burst={bestWeaponBurstCount}");
                    }
                }
            }
            RecalculateHotbarPassiveBonuses(connId);
            playerState.RecalculateCurrentHP();
            ApplyPlayerHPPreserve(hpConn, playerState, hpPreserve, "equip", true);

            // Mana: always set to new max after equipment change (client fills mana on equip/unequip)
            playerState.SetCurrentMana(playerState.MaxManaWire);

            // Log total equipment stats
            Debug.LogError($"[EQUIP-TOTAL] MaxHP={playerState.MaxHPWire / 256} MaxMana={playerState.MaxManaWire / 256} EquipStats={playerState.EquipmentStats.Count}");
            foreach (var kvp in playerState.EquipmentStats)
                Debug.LogError($"[EQUIP-TOTAL]   {kvp.Key} = {kvp.Value}");

            // Update weapon stats on PlayerState
            if (foundWeapon)
            {
                playerState.WeaponDamage = bestWeaponDamage;
                playerState.WeaponDamageVolatility = bestWeaponVolatility;
                playerState.WeaponLevel = bestWeaponLevel;
                playerState.WeaponClass = bestWeaponClass;
                playerState.WeaponDamageType = bestWeaponDamageType;
                playerState.WeaponCategory = bestWeaponCategory;
                playerState.WeaponStatsResolved = true;
                playerState.NativeWeaponClassId = bestNativeWeaponClassId;
                playerState.NativeDamageTypeId = bestNativeDamageTypeId;
                DamageComputer.ApplyNativeWeaponRuntimeBaseDamage(playerState, playerState.Level, bestWeaponStoredLevel, bestWeaponLevel, "equip");
                playerState.WeaponRange = bestWeaponRange;
                playerState.WeaponCooldown = bestWeaponCooldown;
                playerState.WeaponSpeed = bestWeaponSpeed;
                playerState.WeaponUsesProjectile = bestWeaponUsesProjectile;
                playerState.WeaponProjectileSpeed = bestWeaponProjectileSpeed;
                playerState.WeaponProjectileSize = bestWeaponProjectileSize;
                playerState.WeaponBurstCount = bestWeaponBurstCount;
                Debug.LogError($"[EQUIP-WEAPON] PlayerState updated: dmg={bestWeaponDamage:F2} vol={bestWeaponVolatility:F2} level={bestWeaponLevel} nativeDamageLevel={playerState.NativeWeaponDamageLevel} nativeBaseDamage={playerState.NativeWeaponBaseDamage} nativeBaseSource={playerState.NativeWeaponBaseDamageSource} class={bestWeaponClass}/{playerState.NativeWeaponClassId} damageType={bestWeaponDamageType}/{playerState.NativeDamageTypeId} category={bestWeaponCategory} cooldown={bestWeaponCooldown:F2} speed={bestWeaponSpeed:F2} useProjectile={bestWeaponUsesProjectile} projectileSpeed={bestWeaponProjectileSpeed:F2} projectileSize={bestWeaponProjectileSize:F2} burst={bestWeaponBurstCount}");
            }
        }
        public Dictionary<uint, GCObject> GetAllEquippedItems(string connId)
        {
            var items = new Dictionary<uint, GCObject>();

            // Check all possible equipment slots
            // Check all possible equipment slots
            uint[] slots = { 1, 2, 3, 4, 5, 6, 7, 8, 10, 11 }; // amulet, gloves, ring1, ring2, helm, armor, boots, shoulders, weapon, shield

            foreach (uint slot in slots)
            {
                GCObject item = GetEquippedItem(connId, slot);
                if (item != null)
                {
                    items[slot] = item;
                }
            }

            return items;
        }



        private int EstimateItemLevel(string gcClass, int playerLevel)
        {
            for (int i = gcClass.Length - 1; i >= 0; i--)
            {
                if (char.IsDigit(gcClass[i]))
                {
                    int j = i;
                    while (j > 0 && char.IsDigit(gcClass[j - 1]))
                        j--;

                    string tierStr = gcClass.Substring(j, i - j + 1);
                    if (int.TryParse(tierStr, out int tier))
                        return Math.Min(tier * 10, playerLevel);
                }
            }
            return playerLevel / 2;
        }






        private string GetComponentType(string connId, ushort componentId)
        {
            if (_playerComponentTypes.ContainsKey(connId) && _playerComponentTypes[connId].ContainsKey(componentId))
            {
                return _playerComponentTypes[connId][componentId];
            }
            return "Unknown";
        }

        private void TrackComponent(string connId, ushort componentId, string componentType)
        {
            if (!_playerComponentTypes.ContainsKey(connId))
            {
                _playerComponentTypes[connId] = new Dictionary<ushort, string>();
            }
            _playerComponentTypes[connId][componentId] = componentType;
            Debug.LogError($"[COMPONENT-TRACK] Player {connId}: ComponentID 0x{componentId:X4} = {componentType}");
        }

        public void TrackEquippedItem(string connId, uint slot, GCObject item)
        {
            if (!_playerEquippedItems.ContainsKey(connId))
            {
                _playerEquippedItems[connId] = new Dictionary<uint, GCObject>();
            }
            _playerEquippedItems[connId][slot] = item;
            Debug.LogError($"[EQUIP-TRACK] Player {connId}: Slot {slot} = {item.GCClass}");
        }
        /// <summary>
        /// Gets synch value with UpdateNumber packed into low byte
        /// CRITICAL: UpdateNumber increments after each call!
        /// </summary>
        // TO:
        /* private uint GetSynchValue(RRConnection conn)
         {
             PlayerState state = GetPlayerState(conn.ConnId.ToString());
             // Don't mask - the class bonus (46 for Fighter) is in low bits
             // UpdateNumber is NOT validated by client, only displayed in crash log
             Debug.LogError($"[SYNCH] SynchHP=0x{state.SynchHP:X8} ({state.SynchHP})");
             return state.SynchHP;
         }*/


        /*   private uint GetSynchValue(RRConnection conn, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
           {
               PlayerState state = GetPlayerState(conn.ConnId.ToString());
               conn.UpdateNumber++;

               uint synchValue = (state.SynchHP & 0xFFFFFF00) | ((uint)conn.UpdateNumber & 0xFF);

               Debug.LogError($"[SYNCH-TRACK] ══════════════════════════════════════════");
               Debug.LogError($"[SYNCH-TRACK] Caller: {caller}");
               Debug.LogError($"[SYNCH-TRACK] conn.UpdateNumber: {conn.UpdateNumber}");
               Debug.LogError($"[SYNCH-TRACK] state.SynchHP raw: 0x{state.SynchHP:X8}");
               Debug.LogError($"[SYNCH-TRACK] HP masked: 0x{(state.SynchHP & 0xFFFFFF00):X8}");
               Debug.LogError($"[SYNCH-TRACK] UpdateNumber byte: 0x{((uint)conn.UpdateNumber & 0xFF):X2}");
               Debug.LogError($"[SYNCH-TRACK] Combined synch: 0x{synchValue:X8}");
               Debug.LogError($"[SYNCH-TRACK] ══════════════════════════════════════════");

               return synchValue;
           }*/

        private uint GetSynchValue(RRConnection conn)
        {
            return GetSynchValue(conn, false);
        }

        public bool WritePlayerEntitySynch(RRConnection conn, LEWriter writer)
        {
            return TryWritePlayerEntitySynch(conn, writer, SyncContext.PlayerActionResponse, "WritePlayerEntitySynch", true, true);
        }

        public bool WritePlayerEntitySynch(RRConnection conn, LEWriter writer, SyncContext context)
        {
            return TryWritePlayerEntitySynch(conn, writer, context, context.ToString(), true, true);
        }

        private bool WritePlayerEntitySynchNoFlush(RRConnection conn, LEWriter writer)
        {
            return TryWritePlayerEntitySynch(conn, writer, SyncContext.PlayerActionResponse, "WritePlayerEntitySynchNoFlush", true, false);
        }

        private bool WritePlayerEntitySynchNoCombatFlush(RRConnection conn, LEWriter writer)
        {
            return TryWritePlayerEntitySynch(conn, writer, SyncContext.PlayerActionResponse, "WritePlayerEntitySynchNoCombatFlush", true, false);
        }

        private bool TryWritePlayerEntitySynch(RRConnection conn, LEWriter writer, SyncContext context, string packetName, bool advanceClientSync, bool flushCombat)
        {
            if (writer == null) return false;
            if (flushCombat)
            {
                GetNativeValidationCutoff(out _, out float validationCutoffTime);
                FlushCombatBeforeSynch(conn, validationCutoffTime);
            }

            if (TryResolveWriterComponentUpdate(conn, writer, out ushort componentId, out byte subtype))
                return TryWriteEntitySynchForComponent(conn, writer, componentId, subtype, context, packetName, advanceClientSync);

            if (!TryResolvePlayerSynchronizedHP(conn, context, packetName, advanceClientSync, out uint hpWire))
            {
                Debug.LogError($"[SYNC-SUFFIX-UNRESOLVED] packet={packetName} context={context} owner=Avatar reason=player-hp-unresolved");
                return false;
            }

            GetNativeValidationCutoff(out uint fallbackCutoffTick, out float fallbackCutoffTime);
            string fallbackRuntimeKey = conn != null ? GetInstanceZoneKey(conn) : null;
            int fallbackRngPos = !string.IsNullOrWhiteSpace(fallbackRuntimeKey) ? CombatManager.Instance.GetRoomRngPosForInstance(fallbackRuntimeKey) : -1;
            return TryWriteResolvedEntitySynchInfo(writer, 0, 0, context, packetName, EntitySynchInfoDecision.HP(EntitySynchInfoOwner.Avatar, hpWire, packetName, conn?.Avatar != null ? (uint)conn.Avatar.Id : 0u, 0, 0, GetNativeCombatNow(), $"player-fallback; validationCutoffTick={fallbackCutoffTick} validationCutoffTime={fallbackCutoffTime:F3}", fallbackCutoffTick, fallbackCutoffTime, fallbackRuntimeKey, conn?.EntitySchedulerMirror?.SchedulerTick ?? 0, conn?.EntitySchedulerMirror?.SubEntityPhase ?? false, fallbackRngPos));
        }

        private bool TryWriteEntitySynchForComponent(RRConnection conn, LEWriter writer, ushort componentId, byte subtype, string tag, bool advanceClientSync)
        {
            return TryWriteEntitySynchForComponent(conn, writer, componentId, subtype, SyncContextFromTag(tag), tag, advanceClientSync);
        }

        private static uint ResolveAuthoredUnitMaxHealthWire(string gcType, uint fallbackHPWire = NativeNonCombatInteractiveHPWire)
        {
            if (string.IsNullOrEmpty(gcType) || GCDatabase.Instance == null || !GCDatabase.Instance.IsLoaded)
                return fallbackHPWire;

            var node = GCDatabase.Instance.ResolveWithInheritance(gcType);
            var desc = node?.GetChild("Description") ?? node;
            if (desc == null || !desc.HasProperty("MaxHealth"))
                return fallbackHPWire;

            float maxHealth = desc.GetFloat("MaxHealth", fallbackHPWire / 256f);
            if (maxHealth <= 0f || float.IsNaN(maxHealth) || float.IsInfinity(maxHealth))
                return fallbackHPWire;

            return (uint)Mathf.Max(1, Mathf.RoundToInt(maxHealth * 256f));
        }

        private static bool AuthoredExtendsNativeClass(string gcType, string nativeClass)
        {
            if (string.IsNullOrEmpty(gcType) || string.IsNullOrEmpty(nativeClass) ||
                GCDatabase.Instance == null || !GCDatabase.Instance.IsLoaded)
                return false;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string current = gcType;
            for (int depth = 0; depth < 32 && !string.IsNullOrEmpty(current); depth++)
            {
                if (!seen.Add(current)) return false;
                var node = GCDatabase.Instance.Resolve(current);
                if (node == null) return false;
                if (AuthoredNameMatches(node.Name, nativeClass) || AuthoredNameMatches(node.Extends, nativeClass))
                    return true;
                current = node.Extends;
            }

            return false;
        }

        private static bool AuthoredNameMatches(string authoredName, string nativeClass)
        {
            if (string.IsNullOrEmpty(authoredName)) return false;
            if (string.Equals(authoredName, nativeClass, StringComparison.OrdinalIgnoreCase)) return true;
            int dot = authoredName.LastIndexOf('.');
            return dot >= 0 &&
                string.Equals(authoredName.Substring(dot + 1), nativeClass, StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveAuthoredItemNativeClass(string gcType)
        {
            if (AuthoredExtendsNativeClass(gcType, "RangedWeapon")) return "RangedWeapon";
            if (AuthoredExtendsNativeClass(gcType, "MeleeWeapon")) return "MeleeWeapon";
            if (AuthoredExtendsNativeClass(gcType, "ActiveItem")) return "ActiveItem";
            if (AuthoredExtendsNativeClass(gcType, "Armor")) return "Armor";
            if (AuthoredExtendsNativeClass(gcType, "Item")) return "Item";

            string gcLower = (gcType ?? string.Empty).ToLowerInvariant();
            if (gcLower.Contains("ring") || gcLower.Contains("amulet"))
                return "Item";
            if (gcLower.Contains("crossbow") || gcLower.Contains("bow") ||
                gcLower.Contains("gun") || gcLower.Contains("cannon") ||
                gcLower.Contains("ranged"))
                return "RangedWeapon";
            if (gcLower.Contains("sword") || gcLower.Contains("axe") ||
                gcLower.Contains("mace") || gcLower.Contains("dagger") ||
                gcLower.Contains("hammer") || gcLower.Contains("staff") ||
                gcLower.Contains("spear") || gcLower.Contains("pick") ||
                gcLower.Contains("club") || gcLower.Contains("scepter") ||
                gcLower.Contains("wand") || gcLower.Contains("katana") ||
                gcLower.Contains("polearm") || gcLower.Contains("melee"))
                return "MeleeWeapon";
            return "Armor";
        }

        private static void WriteNonCombatInteractiveEntitySynchInfo(LEWriter writer, string gcType = null)
        {
            uint hpWire = ResolveAuthoredUnitMaxHealthWire(gcType);
            writer.WriteByte(0x02);
            writer.WriteUInt32(hpWire);
            Debug.LogError($"[SYNC-SUFFIX] packet=NCI owner=NonCombatInteractive gc={gcType ?? "<default>"} flags=0x02 hp={hpWire}");
        }

        private bool TryWriteEntitySynchForComponent(RRConnection conn, LEWriter writer, ushort componentId, byte subtype, SyncContext context, string packetName, bool advanceClientSync)
        {
            if (advanceClientSync && IsAvatarHPSyncComponentId(conn, componentId))
            {
                GetNativeValidationCutoff(out _, out float validationCutoffTime);
                FlushCombatBeforeSynch(conn, validationCutoffTime);
            }
            if (!ResolveEntitySynchInfoForComponent(conn, componentId, subtype, context, 0, packetName, advanceClientSync, out EntitySynchInfoDecision decision))
            {
                Debug.LogError($"[SYNC-SUFFIX-UNRESOLVED] packet={packetName} context={context} component={componentId} sub=0x{subtype:X2} owner={decision.Owner} reason={decision.Reason}");
                return false;
            }

            return TryWriteResolvedEntitySynchInfo(writer, componentId, subtype, context, packetName, decision, conn);
        }

        private bool TryWriteResolvedEntitySynchInfo(LEWriter writer, ushort componentId, byte subtype, SyncContext context, string packetName, EntitySynchInfoDecision decision, RRConnection conn = null)
        {
            if (!decision.Allow)
            {
                Debug.LogError($"[SYNC-SUFFIX-BLOCK] packet={packetName} context={context} component={componentId} sub=0x{subtype:X2} owner={decision.Owner} reason={decision.Reason}");
                return false;
            }

            if (decision.Owner == EntitySynchInfoOwner.Avatar && (decision.Flags & 0x02) == 0 && !ShouldKeepPlayerComponentSyncEmpty(context))
            {
                if (conn != null && TryResolvePlayerSynchronizedHP(conn, context, packetName, false, out uint avatarHPWire))
                {
                    decision.Flags |= 0x02;
                    decision.HPWire = avatarHPWire;
                    decision.OwnerEntityId = conn.Avatar != null ? (uint)conn.Avatar.Id : decision.OwnerEntityId;
                    decision.Reason = $"{decision.Reason}; avatar-hp-required";
                    decision.Provenance = string.IsNullOrEmpty(decision.Provenance) ? "avatar-hp-recovered" : decision.Provenance + "; avatar-hp-recovered";
                    decision.HpMutationSource = string.IsNullOrEmpty(decision.HpMutationSource) ? "avatar-hp-required" : decision.HpMutationSource;
                }
                else
                {
                    Debug.LogError($"[SYNC-SUFFIX-BLOCK] Avatar suffix without HP packet={packetName} context={context} component={componentId} sub=0x{subtype:X2} flags=0x{decision.Flags:X2} reason={decision.Reason}");
                    return false;
                }
            }

            if (decision.Owner == EntitySynchInfoOwner.Monster && (decision.Flags & 0x02) == 0)
            {
                if (!ShouldKeepMonsterComponentSyncEmpty(context, packetName))
                {
                    Debug.LogError($"[SYNC-SUFFIX-BLOCK] Monster suffix without HP packet={packetName} context={context} component={componentId} sub=0x{subtype:X2} flags=0x{decision.Flags:X2} reason={decision.Reason}");
                    return false;
                }
            }

            new EntitySynchInfoPayload(decision.Flags, decision.HPWire).Write(writer);

            if (VerboseSynchLogging || decision.Owner == EntitySynchInfoOwner.Avatar || decision.Owner == EntitySynchInfoOwner.Monster || decision.Owner == EntitySynchInfoOwner.NonUnit)
            {
                string hpText = (decision.Flags & 0x02) != 0 ? decision.HPWire.ToString() : "none";
                string useTargetState = conn != null && conn.HasActiveUseTarget
                    ? $" useTarget={conn.ActiveUseTargetId} initUsePassed={conn.ActiveUseTargetInitUsePassed} visibleHit={conn.ActiveUseTargetVisibleHit} lastProjectileSeq={conn.ActiveUseTargetLastProjectileSeq} lastImpactTick={conn.ActiveUseTargetLastImpactTick}"
                    : " useTarget=0 initUsePassed=False visibleHit=False lastProjectileSeq=0 lastImpactTick=-1";
                Debug.LogError($"[SYNC-SUFFIX] packet={packetName} context={context} component={componentId} sub=0x{subtype:X2} owner={decision.Owner} ownerEntity={decision.OwnerEntityId} flags=0x{decision.Flags:X2} hp={hpText} nativeNow={decision.NativeNow:F3} cutoffTick={decision.ValidationCutoffTick} cutoffTime={decision.ValidationCutoffTime:F3} runtime='{decision.RuntimeInstanceKey ?? ""}' schedulerTick={decision.SchedulerTick} subentity={decision.SubEntityPhase} rngPos={decision.RngPos} hpMutation='{decision.HpMutationSource ?? ""}' reason={decision.Reason} provenance={decision.Provenance}{useTargetState}");
            }
            return true;
        }

        private bool TryWriteResolvedEntitySynchInfo(LEWriter writer, ushort componentId, byte subtype, SyncContext context, string packetName, uint ownerEntityId, EntitySynchInfoDecision decision)
        {
            return TryWriteResolvedEntitySynchInfo(writer, componentId, subtype, context, packetName, decision);
        }

        private bool TryWriteRemoteAvatarEntitySynchInfo(RRConnection sourceConn, LEWriter writer, ushort componentId, byte subtype, string packetName)
        {
            if (!TryResolvePlayerSynchronizedHP(sourceConn, SyncContext.PlayerActionResponse, packetName, false, out uint hpWire))
            {
                Debug.LogError($"[SYNC-SUFFIX-UNRESOLVED] packet={packetName} owner=RemoteAvatar reason=player-hp-unresolved");
                return false;
            }

            string source = sourceConn?.LoginName ?? "unknown";
            GetNativeValidationCutoff(out uint validationCutoffTick, out float validationCutoffTime);
            string runtimeKey = sourceConn != null ? GetInstanceZoneKey(sourceConn) : null;
            int rngPos = !string.IsNullOrWhiteSpace(runtimeKey) ? CombatManager.Instance.GetRoomRngPosForInstance(runtimeKey) : -1;
            return TryWriteResolvedEntitySynchInfo(writer, componentId, subtype, SyncContext.PlayerActionResponse, packetName, EntitySynchInfoDecision.HP(EntitySynchInfoOwner.Avatar, hpWire, $"{packetName} source={source}", sourceConn?.Avatar != null ? (uint)sourceConn.Avatar.Id : 0u, componentId, subtype, GetNativeCombatNow(), $"remote-avatar; validationCutoffTick={validationCutoffTick} validationCutoffTime={validationCutoffTime:F3}", validationCutoffTick, validationCutoffTime, runtimeKey, sourceConn?.EntitySchedulerMirror?.SchedulerTick ?? 0, sourceConn?.EntitySchedulerMirror?.SubEntityPhase ?? false, rngPos));
        }

        private bool ResolveEntitySynchInfoForComponent(RRConnection conn, ushort componentId, byte subtype, SyncContext context, uint ownerEntityId, string packetName, bool advanceClientSync, out EntitySynchInfoDecision decision)
        {
            decision = EntitySynchInfoDecision.Empty(EntitySynchInfoOwner.Unknown, packetName);

            if (conn == null || (componentId == 0 && ownerEntityId == 0))
                return true;

            if (IsNonUnitPlayerComponentId(conn, componentId))
            {
                decision = EntitySynchInfoDecision.Empty(EntitySynchInfoOwner.NonUnit, $"{packetName} non-unit-player-component");
                return true;
            }

            RegisterHpSyncPlayer(conn);
            Monster monster = ResolveMonsterForComponent(componentId, ownerEntityId);
            if (monster != null)
            {
                HpSyncService.Instance.RegisterMonster(monster);
                string monsterPacketName = $"{packetName} context={context} cid={componentId} sub=0x{subtype:X2} owner={monster.EntityId}";
                float suffixNativeNow = GetNativeCombatNow();
                CombatManager.NativeHpVisibilityCutoff hpCutoff = CombatManager.Instance.GetEntitySynchInfoValidationCutoff(context, monsterPacketName);
                uint validationCutoffTick = hpCutoff.Tick;
                float validationCutoffTime = hpCutoff.Time;
                string runtimeInstanceKey = !string.IsNullOrWhiteSpace(monster.InstanceKey) ? monster.InstanceKey : monster.ZoneName;
                int rngPos = CombatManager.Instance.GetRoomRngPosForInstance(runtimeInstanceKey);
                conn.EntitySchedulerMirror.ObserveSuffixCutoff(runtimeInstanceKey, validationCutoffTick, validationCutoffTime, hpCutoff.IncludeSubEntityEffects, hpCutoff.Phase, monsterPacketName);
                FlushMonsterRuntimeBeforeSynch(conn, monster, context, monsterPacketName, validationCutoffTime, suffixNativeNow, validationCutoffTick);
                PrimeMonsterHPBeforeSync(monster, validationCutoffTime);
                if (ShouldKeepMonsterComponentSyncEmpty(context, packetName))
                {
                    decision = EntitySynchInfoDecision.Empty(EntitySynchInfoOwner.Monster, monsterPacketName);
                    return true;
                }
                uint monsterHPWire = CombatManager.Instance.PeekMonsterCurrentHPWire(monster);
                string monsterHPReason = "runtime-hp";
                HpSyncService.Instance.RecordMonsterOutboundHP(monster, monsterHPWire, monsterPacketName);
                string provenance = $"{monsterHPReason}; visibleCutoffTick={validationCutoffTick}; visibleCutoffTime={validationCutoffTime:F3}; cutoffPhase={hpCutoff.Phase}; includeSubEntity={hpCutoff.IncludeSubEntityEffects}; cutoffReason={hpCutoff.Reason}; lastEntity={hpCutoff.LastEntityTick}@{hpCutoff.LastEntityTime:F3}; lastSubEntity={hpCutoff.LastSubEntityTick}@{hpCutoff.LastSubEntityTime:F3}";
                decision = EntitySynchInfoDecision.HP(EntitySynchInfoOwner.Monster, monsterHPWire, $"{monsterPacketName} {monsterHPReason}", monster.EntityId, componentId != 0 ? componentId : monster.BehaviorId, subtype, suffixNativeNow, provenance, validationCutoffTick, validationCutoffTime, runtimeInstanceKey, conn.EntitySchedulerMirror.SchedulerTick, conn.EntitySchedulerMirror.SubEntityPhase, rngPos, monsterHPReason);
                return true;
            }

            if (conn.Avatar != null && !IsZoneSpawnInvulnerabilityBlockingCombat(conn))
                FlushWeaponCycleBeforeSynch(conn, $"ResolveEntitySynchInfo:{packetName}", false);

            if (!IsAvatarHPSyncComponentId(conn, componentId))
            {
                decision = EntitySynchInfoDecision.Empty(EntitySynchInfoOwner.Unknown, packetName);
                return true;
            }

            if (!TryResolvePlayerSynchronizedHP(conn, context, packetName, advanceClientSync, out uint avatarHPWire))
            {
                var state = GetPlayerState(conn.ConnId.ToString());
                avatarHPWire = state != null ? state.SynchHP : 0;
                Debug.LogError($"[SYNC-SUFFIX-RECOVER] packet={packetName} owner=Avatar component={componentId} hp={avatarHPWire} reason=avatar-hp-unresolved");
            }

            GetNativeValidationCutoff(out uint avatarCutoffTick, out float avatarCutoffTime);
            string avatarRuntimeKey = GetInstanceZoneKey(conn);
            int avatarRngPos = !string.IsNullOrWhiteSpace(avatarRuntimeKey) ? CombatManager.Instance.GetRoomRngPosForInstance(avatarRuntimeKey) : -1;
            decision = EntitySynchInfoDecision.HP(EntitySynchInfoOwner.Avatar, avatarHPWire, packetName, conn.Avatar != null ? (uint)conn.Avatar.Id : 0u, componentId, subtype, GetNativeCombatNow(), $"avatar-hp; validationCutoffTick={avatarCutoffTick} validationCutoffTime={avatarCutoffTime:F3}", avatarCutoffTick, avatarCutoffTime, avatarRuntimeKey, conn.EntitySchedulerMirror.SchedulerTick, conn.EntitySchedulerMirror.SubEntityPhase, avatarRngPos);
            return true;
        }

        private bool TryResolveWriterComponentUpdate(RRConnection conn, LEWriter writer, out ushort componentId, out byte subtype)
        {
            componentId = 0;
            subtype = 0;
            byte[] data = writer?.GetBuffer();
            if (data == null || data.Length < 3) return false;

            bool found = false;
            for (int i = 0; i + 2 < data.Length; i++)
            {
                byte opcode = data[i];
                if (opcode != 0x35 && opcode != 0x36) continue;
                if (opcode == 0x35 && i + 3 >= data.Length) continue;
                ushort cid = (ushort)(data[i + 1] | (data[i + 2] << 8));
                bool knownPlayerComponent = IsAvatarHPSyncComponentId(conn, cid)
                    || IsNonUnitPlayerComponentId(conn, cid);
                if (!knownPlayerComponent && ResolveMonsterForComponent(cid, 0) == null)
                    continue;
                componentId = cid;
                subtype = opcode == 0x35 ? data[i + 3] : (byte)0;
                found = true;
            }

            return found;
        }

        private Monster ResolveMonsterForComponent(uint componentId, uint ownerEntityId)
        {
            if (CombatManager.Instance == null) return null;
            Monster monster = null;
            if (componentId != 0)
            {
                monster = CombatManager.Instance.GetMonsterByComponent(componentId)
                    ?? CombatManager.Instance.GetMonsterByBehaviorId(componentId)
                    ?? CombatManager.Instance.GetMonsterBySkillsId(componentId)
                    ?? CombatManager.Instance.GetMonsterByManipulatorsId(componentId);
            }
            if (monster == null && ownerEntityId != 0)
                monster = CombatManager.Instance.GetMonster(ownerEntityId);
            return monster;
        }

        private static SyncContext SyncContextFromTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return SyncContext.Unknown;
            switch (tag)
            {
                case "WorldInterval": return SyncContext.WorldInterval;
                case "WorldRuntimeBootstrap": return SyncContext.BootstrapReplay;
                case "WorldRuntimeRecoveryReplay": return SyncContext.RecoveryReplay;
                case "WorldRuntimeRepeatResync": return SyncContext.RepeatResync;
                case "WorldRuntimeInventorySync": return SyncContext.InventoryReplay;
                case "WorldRuntimeEquipResync": return SyncContext.EquipmentReplay;
                case "WorldLateArmorSync": return SyncContext.LateArmorSync;
                case "WorldUnitBehaviorControlGrant": return SyncContext.ControlGrant;
                case "WorldUnitBehaviorControlAck": return SyncContext.ControlAck;
                case "WorldMoverAck": return SyncContext.MoverAck;
                case "PlayerBasicAttackResponse": return SyncContext.PlayerBasicAttackResponse;
            }
            if (tag.StartsWith("MON-ATTACK", StringComparison.Ordinal)) return SyncContext.MonsterAction;
            if (tag.StartsWith("MON-MOVE", StringComparison.Ordinal)) return SyncContext.MonsterMove;
            if (tag.StartsWith("DAMAGE-HP", StringComparison.Ordinal)) return SyncContext.MonsterDamage;
            if (tag.Contains("Inventory")) return SyncContext.InventoryReplay;
            if (tag.Contains("Equip")) return SyncContext.EquipmentReplay;
            return SyncContext.PlayerActionResponse;
        }

        private static bool ShouldKeepPlayerComponentSyncEmpty(SyncContext context)
        {
            return false;
        }

        private static bool ShouldKeepMonsterComponentSyncEmpty(SyncContext context, string packetName = null)
        {
            return false;
        }

        private static bool ShouldKeepPlayerActionLaneAlive(SyncContext context)
        {
            return context == SyncContext.PlayerBasicAttackResponse || context == SyncContext.PlayerActionResponse;
        }

        private static bool CanApplyPlayerHPBeforeSuffix(SyncContext context, string packetName = null)
        {
            return true;
        }

        private static bool CanAdvancePlayerClientSyncHP(uint playerEntityId)
        {
            if (playerEntityId == 0) return true;
            return !CombatManager.Instance.HasPendingClientVisibleMonsterAttack(playerEntityId);
        }

        private void RegisterHpSyncPlayer(RRConnection conn)
        {
            if (conn?.Avatar == null) return;
            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            if (state == null) return;
            HpSyncService.Instance.RegisterPlayer(conn, state, (uint)conn.Avatar.Id);
        }

        private void FlushCombatBeforeSynch(RRConnection conn, float nativeNowOverride = -1f)
        {
            if (conn?.Avatar == null) return;
            conn.LastCombatSyncFlushFrame = Time.frameCount;
            CombatManager.Instance.UpdatePlayerPosition((uint)conn.Avatar.Id, conn.PlayerPosX, conn.PlayerPosY);
            float flushNow = nativeNowOverride >= 0f ? nativeNowOverride : GetNativeCombatNow();
            if (IsZoneSpawnInvulnerabilityBlockingCombat(conn))
            {
                FlushPendingKills();
                return;
            }
            FlushPendingKills();
            FlushWeaponCycleBeforeSynch(conn, "FlushCombatBeforeSynch", true, flushNow);
            CombatManager.Instance.FlushPlayerCombatBeforeSync((uint)conn.Avatar.Id, 0f, "FlushCombatBeforeSynch", flushNow);
        }

        private void FlushWeaponCycleBeforeSynch(RRConnection conn, string source, bool flushKillsAfter, float nativeNowOverride = -1f)
        {
            if (conn?.Avatar == null) return;
            uint playerEntityId = (uint)conn.Avatar.Id;
            CombatManager.Instance.UpdatePlayerPosition(playerEntityId, conn.PlayerPosX, conn.PlayerPosY);
            float flushNow = nativeNowOverride >= 0f ? nativeNowOverride : GetNativeCombatNow();
            if (ShouldAdvancePlayerActionSliceBeforeAvatarSuffix(conn, playerEntityId))
            {
                conn.LastAvatarPreSuffixActionSliceFrame = Time.frameCount;
                var player = CombatManager.Instance.GetPlayer(playerEntityId);
                Debug.LogError($"[PLAYER-ACTION-PRE-SUFFIX] source={source ?? "unknown"} player={conn.LoginName ?? conn.ConnId.ToString()} activeUseTarget={conn.HasActiveUseTarget} activeAttack={player?.HasActiveClientAttack ?? false} target={conn.ActiveUseTargetId} flushNow={flushNow:F3} slice=due-drain");
            }
            string instanceKey = conn != null ? GetInstanceZoneKey(conn) : null;
            Combat.WeaponCycleTracker.Instance.FlushPlayerEntityBeforeSynch(playerEntityId, CombatManager.Instance.GetRoomRngForInstance(instanceKey), flushNow, source);
            if (flushKillsAfter)
            {
                DrainWeaponCycleKills(source ?? "FlushWeaponCycleBeforeSynch");
                FlushPendingKills();
            }
        }

        private bool ShouldAdvancePlayerActionSliceBeforeAvatarSuffix(RRConnection conn, uint playerEntityId)
        {
            if (conn == null || playerEntityId == 0) return false;
            if (conn.LastAvatarPreSuffixActionSliceFrame == Time.frameCount) return false;
            var player = CombatManager.Instance.GetPlayer(playerEntityId);
            return conn.HasActiveUseTarget || (player != null && player.HasActiveClientAttack);
        }

        private bool TryResolvePlayerSynchronizedHP(RRConnection conn, string packetName, bool advanceClientSync, out uint hpWire)
        {
            return TryResolvePlayerSynchronizedHP(conn, SyncContextFromTag(packetName), packetName, advanceClientSync, out hpWire);
        }

        private bool TryResolvePlayerSynchronizedHP(RRConnection conn, SyncContext context, string packetName, bool advanceClientSync, out uint hpWire)
        {
            hpWire = 0;
            if (conn == null) return false;
            RefreshZoneSpawnInvulnerability(conn);
            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            if (state == null) return false;
            uint playerEntityId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0;
            if (playerEntityId != 0)
                HpSyncService.Instance.RegisterPlayer(conn, state, playerEntityId);
            GetNativeValidationCutoff(out uint validationCutoffTick, out float validationCutoffTime);
            bool canApplyPlayerHP = CanApplyPlayerHPBeforeSuffix(context, packetName);
            bool pendingClientVisibleAttack = false;
            uint runtimeHPWire = state.SynchHP;
            if (canApplyPlayerHP && playerEntityId != 0 && !IsZoneSpawnInvulnerabilityBlockingCombat(conn))
            {
                CombatManager.Instance.UpdatePlayerPosition(playerEntityId, conn.PlayerPosX, conn.PlayerPosY);
                FlushPendingKills();
                FlushWeaponCycleBeforeSynch(conn, $"TryResolvePlayerSynchronizedHP:{packetName}", true, validationCutoffTime);
                CombatManager.Instance.FlushPlayerCombatBeforeSync(playerEntityId, 0f, $"TryResolvePlayerSynchronizedHP:{packetName}", validationCutoffTime);
                if (!CombatManager.Instance.FlushPlayerHPRuntimeBeforeSync(playerEntityId, packetName, out runtimeHPWire, out bool unsafeAttack, validationCutoffTime))
                {
                    Debug.LogError($"[{packetName}] player HP runtime flush incomplete for {conn.LoginName ?? conn.ConnId.ToString()}: serverHP={state.CurrentHPWire / 256f:F2}/{state.MaxHPWire / 256f:F2} syncHP={state.SynchHP / 256f:F2} runtimeHP={runtimeHPWire / 256f:F2} unsafeAttack={unsafeAttack}");
                }
            }
            pendingClientVisibleAttack = playerEntityId != 0 && CombatManager.Instance.HasPendingClientVisibleMonsterAttack(playerEntityId);
            bool canAdvanceClientSync = advanceClientSync && !pendingClientVisibleAttack;
            if (playerEntityId != 0)
            {
                float nativeNow = validationCutoffTime;
                var resolve = HpSyncService.Instance.ResolveOutboundPlayer(conn, state, playerEntityId, context, packetName, canAdvanceClientSync, nativeNow, out hpWire);
                if (!resolve.AllowPacket)
                {
                    Debug.LogError($"[{packetName}] player HP sync unresolved by HpSyncService for {conn.LoginName ?? conn.ConnId.ToString()}: serverHP={state.CurrentHPWire / 256f:F2}/{state.MaxHPWire / 256f:F2} syncHP={state.SynchHP / 256f:F2} lastOutbound={conn.LastOutboundHPWire / 256f:F2} reason={resolve.Reason}");
                    return false;
                }
                if (resolve.HasHP && (context == SyncContext.PlayerActionResponse || context == SyncContext.PlayerBasicAttackResponse || hpWire != state.CurrentHPWire))
                    Debug.LogError($"[PLAYER-HP-SUFFIX] packet={packetName} context={context} player={conn.LoginName ?? conn.ConnId.ToString()} currentHP={state.CurrentHPWire} syncHP={state.SynchHP} outboundHP={hpWire} advanceRequested={advanceClientSync} canAdvance={canAdvanceClientSync} pendingClientVisibleAttack={pendingClientVisibleAttack} runtimeProbeHP={runtimeHPWire} cutoffTick={validationCutoffTick} cutoffTime={validationCutoffTime:F3}");
                return resolve.HasHP;
            }
            if (canAdvanceClientSync)
                state.AdvanceClientSyncHP(validationCutoffTime, packetName);
            hpWire = state.SynchHP;
            return true;
        }

        private uint GetSynchValue(RRConnection conn, bool advanceClientSync)
        {
            string caller = null;
            string connIdStr = conn.ConnId.ToString();
            bool existed = _playerStates.ContainsKey(connIdStr);
            PlayerState state = GetPlayerState(connIdStr);

            if (VerboseSynchLogging)
            {
                var trace = new System.Diagnostics.StackTrace(1, false);
                caller = trace.GetFrame(0)?.GetMethod()?.Name ?? "unknown";
                Debug.LogWarning($"[GETSYNCH] Called by {caller}, returning {state.SynchHP}");
            }

            if (VerboseSynchLogging)
            {
                Debug.LogError($"[SYNCH-DEBUG] ConnId={conn.ConnId}, Key='{connIdStr}', ExistedBefore={existed}, SynchHP={state.SynchHP}, Level={state.Level}");
                Debug.LogError($"[GETSYNCH] Returning SynchHP: {state.SynchHP} CurrentHPWire: {state.CurrentHPWire}");
                if (state.SynchHP == 0)
                {
                    Debug.LogError($"[SYNCH-DEBUG] ⚠️ WARNING: SynchHP is 0! Dumping all known PlayerStates:");
                    foreach (var kvp in _playerStates)
                    {
                        Debug.LogError($"[SYNCH-DEBUG]   Key='{kvp.Key}', SynchHP={kvp.Value.SynchHP}, Level={kvp.Value.Level}");
                    }
                }
            }
            // return state.MaxHPWire;
            return state.SynchHP;
            // return state.SynchHP;
        }


        // ═══════════════════════════════════════════════════════════════════════════════
        // INVENTORY ITEM TRACKING
        // ═══════════════════════════════════════════════════════════════════════════════

        // Container keys: 0x0B (main inv) maps to the raw connId to preserve all existing
        // dict accesses across the codebase. Bank containers (0x0C, 0x0E-0x13) get suffixed keys.
        // Bank pages are 10x14 (per Bank.gc); main inv is 10x8.
        private static string InvKey(string connId, byte containerId)
            => containerId == 0x0B ? connId : $"{connId}:0x{containerId:X2}";
        private System.Collections.Concurrent.ConcurrentQueue<PendingSpell> _pendingSpells = new System.Collections.Concurrent.ConcurrentQueue<PendingSpell>();
        private long _nextPendingSpellProjectileSequence;

        /// <summary>
        /// Kill entry point. Server-computed native damage owns monster death, despawn, XP, and loot.
        /// </summary>
        private bool TryFinalizeMonsterKill(RRConnection conn, Combat.Monster monster, string source)
        {
            if (monster == null) return false;

            string killSource = source ?? "unknown";
            Debug.LogError($"[KILL-FINALIZE] {killSource}: {monster.Name} eid={monster.EntityId} conn={(conn != null ? conn.ConnId.ToString() : "null")}");
            _pendingKills.Remove(monster.EntityId);
            if (!_finalizedMonsterKills.Add(monster.EntityId))
            {
                Debug.LogError($"[KILL-DEDUP] {killSource}: {monster.Name} already finalized");
                return false;
            }
            try
            {
                ProcessMonsterKill(conn, monster, killSource);
                return true;
            }
            catch (Exception ex)
            {
                _finalizedMonsterKills.Remove(monster.EntityId);
                Debug.LogError($"[KILL-ERROR] {killSource}: failed to finalize {monster.Name}#{monster.EntityId}: {ex}");
                throw;
            }
        }

        /// <summary>
        /// Call every tick — drains legacy pending kills through the same native server finalizer.
        /// </summary>
        private void FlushPendingKills()
        {
            if (_pendingKills.Count == 0) return;

            var pending = new System.Collections.Generic.List<uint>(_pendingKills.Keys);
            foreach (uint eid in pending)
            {
                if (!_pendingKills.ContainsKey(eid))
                    continue;
                var pk = _pendingKills[eid];
                _pendingKills.Remove(eid);
                if (pk.conn == null || pk.monster == null)
                    continue;
                TryFinalizeMonsterKill(pk.conn, pk.monster, $"{pk.source}-pending-flush");
            }
        }
        /// <summary>
        /// Clears finalized kill tracking for a monster (call on respawn/despawn).
        /// </summary>
        private void ClearFinalizedKill(uint entityId)
        {
            _finalizedMonsterKills.Remove(entityId);
        }

        private static bool IsUseTargetingMonster(RRConnection conn, Combat.Monster monster)
        {
            if (conn == null || monster == null || !conn.HasActiveUseTarget)
                return false;

            ushort targetId = conn.ActiveUseTargetId;
            return targetId == (ushort)monster.EntityId ||
                   targetId == (ushort)monster.BehaviorId ||
                   targetId == (ushort)monster.UnitId ||
                   CombatManager.Instance.GetMonster(targetId) == monster ||
                   CombatManager.Instance.GetMonsterByComponent(targetId) == monster;
        }

        private void ProcessMonsterKill(RRConnection conn, Combat.Monster monster, string source)
        {
            // Dedup handled by _finalizedMonsterKills in TryFinalizeMonsterKill
            CombatManager.Instance.SetMonsterHPWire(monster, 0, true);
            CombatManager.Instance.MarkMonsterNativeDead(monster, source);
            _serverKillCount++;
            Debug.LogError($"[KILL] ═══════════════════════════════════════════════════");
            Debug.LogError($"[KILL] ★ KILL #{_serverKillCount}: {monster.Name} via [{source}]");
            Debug.LogError($"[KILL] EntityId={monster.EntityId} GCType={monster.GCType} Level={monster.Level}");
            CombatManager.Instance.BeginNativeMonsterDeathLifecycle(monster, source);
            if (conn != null)
                ClearUseTargetAndReleaseControl(conn, "ProcessMonsterKill", sendClientControlReset: true, requireActiveUseTargetForReset: false);

            PlayerState playerState = conn != null ? GetPlayerState(conn.ConnId.ToString()) : null;
            if (playerState != null)
            {
                uint packetXP = ResolveMonsterExperienceReward(monster);
                uint sourceLevel = (uint)Math.Max(1, (int)monster.Level);
                uint effectiveXP = ResolveClientVisibleExperienceReward(conn, monster, playerState, packetXP, sourceLevel);
                if (packetXP > 0 && effectiveXP > 0)
                {
                    int oldLevel = playerState.Level;
                    uint oldHPWire = playerState.CurrentHPWire;
                    uint oldMaxHPWire = playerState.MaxHPWire;
                    bool leveled = playerState.AddExperience(effectiveXP);
                    CommitPlayerHPTruth(conn, playerState, leveled ? "LEVEL-UP-XP" : "KILL-XP", playerState.CurrentHPWire, false, false);
                    SendHeroAddExperienceUpdate(conn, packetXP, sourceLevel);
                    SavePlayerLevel(conn);
                    Debug.LogError($"[KILL-XP] {monster.Name}: packetXP={packetXP} effectiveXP={effectiveXP} sourceLevel={sourceLevel} level={oldLevel}->{playerState.Level}{(leveled ? " LEVELUP" : "")} HP={oldHPWire}->{playerState.CurrentHPWire}/{playerState.MaxHPWire} maxHP={oldMaxHPWire}->{playerState.MaxHPWire}");
                }
            }

            // Boss gate opening — DoorsToOpenOnDeath = "Boss00ExitGate"
            if (monster.GCType != null && (
                monster.GCType.Equals("creatures.whiskers.broodling.basic.champion", StringComparison.OrdinalIgnoreCase) ||
                monster.GCType.Equals("world.dungeon00.mob.boss", StringComparison.OrdinalIgnoreCase)))
            {
                Debug.LogError($"[BOSS] ★★★ RATTLE TOOTH KILLED! Opening boss gate ★★★");

                if (WorldEntitySpawner.Instance.FindEntityByName("BossGate", monster.ZoneName, out ushort gateId, out var gateData))
                {
                    // Gate-open packet — restored to the original, known-working bytes.
                    //
                    // Format: [0x03][id:2][0x0A][0x00]
                    //   0x03 = processEntityUpdate opcode (CEM @ 0x5dae30)
                    //   id   = gate entity id (uint16 LE)
                    //   0x0A = Door sub-opcode (door activate / open)
                    //   0x00 = EntitySynchInfo.flags — NOT optional.
                    //
                    // Evidence (DungeonRunnersCrash-04102026-01-29-31-392-00.log):
                    //   ClientEntityManager::processEntityUpdate logged a
                    //   synch error for "Door / RattleGate" when flags byte
                    //   was accidentally 0x06 (= has UpdateNumber + has HP).
                    //   That means processEntityUpdate reads a sync info
                    //   block after the sub-opcode:
                    //      flags(1) | if flags&?: updateNumber(4) | ... |
                    //      position | level | HP | MP | ...
                    //   Setting flags=0 tells the client to skip the whole
                    //   sync block and just process the state flip. That's
                    //   why the 5-byte packet worked and any attempt to
                    //   strip the trailing 0x00 will corrupt the next field.
                    //
                    // Delivered via MessageQueue.Enqueue (same as before) —
                    // the per-tick flush already wraps all queued messages
                    // in a single BeginStream/EndStream envelope before
                    // SendCompressedA.
                    var gateWriter = new LEWriter();
                    gateWriter.WriteByte(0x03);  // processEntityUpdate
                    gateWriter.WriteUInt16(gateId);
                    gateWriter.WriteByte(0x0A);  // Door activate (open)
                    gateWriter.WriteByte(0x00);  // EntitySynchInfo.flags = 0 (skip sync block)
                    byte[] gatePacket = gateWriter.ToArray();

                    Debug.LogError($"[BOSS] Gate entity {gateId} (0x{gateId:X4}), monster.ZoneName={monster.ZoneName}, conn.CurrentZoneName={conn.CurrentZoneName}, conn.InstanceId={conn.InstanceId}");

                    int sentCount = 0;
                    foreach (var zoneConn in _connections.Values)
                    {
                        if (!zoneConn.IsConnected || !zoneConn.IsSpawned) continue;
                        if (!string.Equals(zoneConn.CurrentZoneName, monster.ZoneName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (zoneConn.InstanceId != conn.InstanceId) continue;
                        zoneConn.MessageQueue.Enqueue(gatePacket);
                        sentCount++;
                    }

                    // Fallback: also send directly to killer if broadcast missed them
                    if (sentCount == 0)
                    {
                        Debug.LogError($"[BOSS] WARNING: Broadcast matched 0 players! Sending directly to {conn.LoginName}");
                        conn.MessageQueue.Enqueue(gatePacket);
                        sentCount = 1;
                    }

                    Debug.LogError($"[BOSS] Sent gate open (0x03/0x0A flags=0) for entity {gateId} to {sentCount} players");

                    // ═══ BOSS-GATE-OPEN CHAT-LOG MESSAGE ═══
                    // Reference: user screenshot from original live game shows
                    // "The boss gate is open!" as a small message on the right
                    // side chat/event log, NOT a center-screen red announce.
                    //
                    // Previous code used chat subtype 0x10 with extra flag+sender
                    // bytes. That's the "Announce" path and either rendered
                    // incorrectly or silently dropped in this build — the user
                    // never saw the message.
                    //
                    // Fix: use the same byte layout as SendSystemMessage (subtype
                    // 0x0D / GlobalAnnouncement, no extra fields), which is
                    // proven to render in the chat feed. Broadcast to TWO sets
                    // of receivers, dedup'd by ConnId so nobody gets doubled:
                    //   (a) everyone in the same zone+instance as the killer
                    //       (the existing scope — covers solo players and
                    //        instance-sharing group members)
                    //   (b) every member of the killer's group, regardless of
                    //       where they are (belt-and-suspenders — handles the
                    //       case where an instance-id mismatch would have
                    //       dropped a legitimate party member in (a))
                    const string bossGateMsg = "The boss gate is open!";
                    var _bossMsgSeen = new System.Collections.Generic.HashSet<int>();
                    int _bossMsgSent = 0;

                    // (0) GUARANTEED: always send to the killer first.
                    // Mirrors the gate-open fallback at the top of this block.
                    // Rationale: the existing zone+instance filter used by the
                    // gate-open loop and scope (a) below rejects the killer
                    // when conn.CurrentZoneName doesn't exactly equal
                    // monster.ZoneName — which is common in boss sub-zones
                    // (e.g. player tracked as "dungeon00_level03" while the
                    // boss entity is in "dungeon00_level03_boss", or any
                    // other race/casing drift). The gate packet has its own
                    // "if (sentCount == 0) send to killer" fallback — that's
                    // why the gate animates but a pure-filter message loop
                    // sends to nobody. Instead of trying to diagnose the
                    // filter, just always include the killer up front and
                    // let the dedup hash set handle the overlap.
                    // Boss-gate on-screen popup: UNSOLVED.
                    //
                    // What we know from the RE:
                    //   - ChatClient::processMessage (0x5ff450) has 5 top-level
                    //     cases. Cases 3 and 4 go to ChatSettings::ReadData
                    //     (config, not display). Case 0 handles all standard
                    //     chat subtypes (0x02, 0x03, 0x04, 0x05, 0x0B, 0x0C,
                    //     0x0D, 0x10) and every one of them converges at
                    //     0x5ff92a -> std::vector<ChatClient::ChatMessage>
                    //     ::push_back. That means NO chat subtype produces a
                    //     separate right-side popup — they all go to the chat
                    //     log. Firing four subtypes was a waste; dropping it.
                    //   - BossGate.gc literally has
                    //       OpenMessage = "The boss gate is open!";
                    //     and the client has PropertyDoorDescOpenMessage +
                    //     PropertyNonCombatInteractiveDescOpenMessage — so the
                    //     popup IS a real client-side mechanism, just not a
                    //     chat one. Most likely path: when the client sees the
                    //     boss die, its own engine reads
                    //     UnitDesc::DoorsToOpenOnDeath (boss.gc sets this to
                    //     "Boss00ExitGate") and locally opens the door,
                    //     which triggers the description's OpenMessage. That
                    //     requires a proper unit-death packet, not the
                    //     hand-crafted 0x03/0x0A gate update this server is
                    //     sending today.
                    //
                    // For now: send ONE 0x0D chat message to the killer only.
                    // No group broadcast, no zone broadcast, no subtype
                    // shotgun. Keeps chat clean while the real popup path is
                    // still being reversed.
                    if (conn != null && conn.IsConnected)
                    {
                        SendSystemMessage(conn, bossGateMsg);
                        _bossMsgSent = 1;
                    }

                    Debug.LogError($"[BOSS] Boss-gate message '{bossGateMsg}' sent to {_bossMsgSent} player(s) (zone+instance+group dedup'd)");

                    // === BOSS-GATE ON-SCREEN POPUP via Quest Add+Progress+Remove ===
                    // Fires LAST in the boss-kill flow so gate-open and chat are
                    // already done. Wrapped in try/catch so a failure in this path
                    // does not break the gate animation or chat fallback above.
                    // Broadcasts to every player in killer's zone+instance.
                    try
                    {
                        int _popupSent = 0;
                        var _popupSeen = new System.Collections.Generic.HashSet<int>();
                        foreach (var popupConn in _connections.Values)
                        {
                            if (popupConn == null) continue;
                            if (!popupConn.IsConnected || !popupConn.IsSpawned) continue;
                            if (popupConn.QuestManagerId == 0) continue;
                            if (!string.Equals(popupConn.CurrentZoneName, monster.ZoneName, StringComparison.OrdinalIgnoreCase)) continue;
                            if (popupConn.InstanceId != conn.InstanceId) continue;
                            if (!_popupSeen.Add(popupConn.ConnId)) continue;
                            SendBossGatePopup(popupConn);
                            _popupSent++;
                        }
                        if (conn != null && conn.IsConnected && conn.QuestManagerId != 0 && _popupSeen.Add(conn.ConnId))
                        {
                            SendBossGatePopup(conn);
                            _popupSent++;
                        }
                        Debug.LogError($"[BOSS-POPUP] Triggered popup sequence for {_popupSent} player(s)");
                    }
                    catch (Exception _popupEx)
                    {
                        Debug.LogError($"[BOSS-POPUP] FAILED (non-fatal): {_popupEx.Message}");
                    }
                }
                else
                {
                    Debug.LogError($"[BOSS] WARNING: Could not find BossGate entity in zone {monster.ZoneName}");
                }
            }

            var candidateGcTypes = new System.Collections.Generic.List<string>();
            if (monster.AuthoredArchetypeAncestry != null)
            {
                foreach (string ancestryPath in monster.AuthoredArchetypeAncestry)
                {
                    if (!string.IsNullOrWhiteSpace(ancestryPath) &&
                        !candidateGcTypes.Contains(ancestryPath, StringComparer.OrdinalIgnoreCase))
                        candidateGcTypes.Add(ancestryPath);
                }
            }
            if (!string.IsNullOrEmpty(monster.SpawnGCType))
                if (!candidateGcTypes.Contains(monster.SpawnGCType, StringComparer.OrdinalIgnoreCase))
                    candidateGcTypes.Add(monster.SpawnGCType);
            if (!string.IsNullOrEmpty(monster.GCType))
                if (!candidateGcTypes.Contains(monster.GCType, StringComparer.OrdinalIgnoreCase))
                    candidateGcTypes.Add(monster.GCType);
            var questUpdates = QuestManager.Instance.OnCreatureKilled(conn, candidateGcTypes);
            if (questUpdates != null && questUpdates.Count > 0)
            {
                Debug.LogError($"[KILL] Quest objectives updated: {questUpdates.Count}");
                SavePlayerQuests(conn);  // Persist kill progress immediately
            }
            if (playerState != null)
            {
                SavePlayerLevel(conn);
                Debug.LogError($"[KILL] Saved XP={playerState.Experience} Level={playerState.Level}");
            }

            // ═══ LOOT GENERATION — DLL-confirmed kill ═══
            try
            {
                int pLevel = playerState?.Level ?? 1;
                List<LootDrop> drops;

                if (WorldObjectSpawner.IsDestroyableObject(monster.GCType))
                    drops = LootManager.Instance.GenerateDestroyableLoot(monster, pLevel, conn != null && !IsPlayerFree(conn.LoginName));
                else
                    drops = LootManager.Instance.GenerateMobLoot(monster, pLevel, conn != null && !IsPlayerFree(conn.LoginName));

                // Member players get Major potions (MaxStackSize=10) instead of
                // Minor potions (MaxStackSize=5). Free players keep minors.
                UpgradePotionsForMembers(drops, conn);

                foreach (var drop in drops)
                {
                    if (drop.IsGold)
                    {
                        uint lootGold = (uint)drop.GoldAmount;
                        Debug.LogError($"[LOOT] +{lootGold} gold pile from {monster.Name}");
                        try
                        {
                            var goldPlacement = ResolveNativeItemDropPlacement(
                                conn,
                                conn.CurrentZoneName,
                                conn.InstanceId,
                                monster.PosX,
                                monster.PosY,
                                monster.PosZ,
                                monster.Heading,
                                $"mob-gold:{monster.Name}#{monster.EntityId}");
                            float gpx = goldPlacement.X;
                            float gpy = goldPlacement.Y;
                            float gpz = goldPlacement.Z;
                            ushort goldEntityId = GetNextLootEntityId();

                            var goldInfo = new DroppedItemInfo
                            {
                                Item = null,
                                DbId = 0,
                                Zone = conn.CurrentZoneName ?? "",
                                ZoneId = conn.CurrentZoneId,
                                InstanceId = conn.InstanceId,
                                PosX = gpx,
                                PosY = gpy,
                                PosZ = gpz,
                                PlayerLevel = playerState?.Level ?? 1,
                                DroppedBy = conn.LoginName ?? "",
                                IsGoldDrop = true,
                                GoldAmount = lootGold
                            };
                            _droppedItems[goldEntityId] = goldInfo;
                            SendGoldPileSpawnPacket(conn, goldEntityId, gpx, gpy, gpz);
                        }
                        catch (Exception goldLootEx)
                        {
                            Debug.LogError($"[LOOT] ❌ gold pile spawn failed: {goldLootEx.Message}");
                        }
                    }
                    else if (drop.IsKingsCoin)
                    {
                        // Kings Coin GROUND drop. Build a GCObject for QuestItemPAL.Token
                        // and route through the same TrackDroppedItem path the regular
                        // weapon/armor drops use. The pickup handler stacks Kings Coins
                        // automatically when added to inventory (BaseQuestItem extends Item
                        // and has Stackable=true MaxStackSize=100 in QuestItemPAL.gc).
                        //
                        // CRITICAL: this branch must exist BEFORE the regular item else,
                        // because LootDrop.KingsCoin sets GCType=null which would NPE in
                        // the NativeClass detection path. RollKingsCoin in LootManager
                        // adds these to the drops list whenever a tier-based roll succeeds.
                        try
                        {
                            int kcCount = drop.KingsCoinCount > 0 ? drop.KingsCoinCount : 1;
                            for (int kcIdx = 0; kcIdx < kcCount; kcIdx++)
                            {
                                var kcItem = new GCObject
                                {
                                    GCClass = "QuestItemPAL.Token",
                                    NativeClass = "Item",
                                    PresetScaleMod = "ScaleModPAL.Binder.Mod1",
                                    StoredRarity = (int)ItemRarity.Normal,
                                    StoredLevel = pLevel
                                };
                                var kcPlacement = ResolveNativeItemDropPlacement(
                                    conn,
                                    conn.CurrentZoneName,
                                    conn.InstanceId,
                                    monster.PosX,
                                    monster.PosY,
                                    monster.PosZ,
                                    monster.Heading,
                                    $"mob-kingscoin:{monster.Name}#{monster.EntityId}:{kcIdx}");
                                float kcPx = kcPlacement.X;
                                float kcPy = kcPlacement.Y;
                                float kcPz = kcPlacement.Z;
                                ushort kcEntityId = GetNextLootEntityId();
                                TrackDroppedItem(kcEntityId, kcItem, conn, 1, kcPx, kcPy, kcPz, pLevel);
                                SendDroppedItemSpawnPacket(conn, kcEntityId, _droppedItems[kcEntityId]);
                            }
                            Debug.LogError($"[LOOT-KC] dropped {kcCount} Kings Coin(s) on ground from {monster.Name}");
                        }
                        catch (Exception kcEx)
                        {
                            Debug.LogError($"[LOOT-KC] ground drop failed (non-fatal): {kcEx.Message}");
                        }
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(drop.GCType)) { Debug.LogError($"[LOOT] ⚠️ Skipping null GCType from {monster.Name}"); continue; }
                        string _detectedNativeClass = ResolveAuthoredItemNativeClass(drop.GCType);

                        var item = new GCObject
                        {
                            GCClass = drop.GCType,
                            NativeClass = _detectedNativeClass,
                            PresetScaleMod = drop.ScaleMod,
                            StoredRarity = (int)drop.Rarity,
                            StoredLevel = drop.ItemLevel
                        };
                        var itemPlacement = ResolveNativeItemDropPlacement(
                            conn,
                            conn.CurrentZoneName,
                            conn.InstanceId,
                            monster.PosX,
                            monster.PosY,
                            monster.PosZ,
                            monster.Heading,
                            $"mob-item:{monster.Name}#{monster.EntityId}:{drop.GCType}");
                        float px = itemPlacement.X;
                        float py = itemPlacement.Y;
                        float pz = itemPlacement.Z;

                        ushort lootEntityId = GetNextLootEntityId();
                        TrackDroppedItem(lootEntityId, item, conn, 1, px, py, pz, pLevel);

                        SendDroppedItemSpawnPacket(conn, lootEntityId, _droppedItems[lootEntityId]);
                        Debug.LogError($"[LOOT] ★ {drop.Label} ({drop.Rarity}) at ({px:F0},{py:F0},{pz:F0})");
                    }
                }

                if (drops.Count > 0)
                    Debug.LogError($"[LOOT] {monster.Name}: {drops.Count} drops");
            }
            catch (Exception lootEx)
            {
                Debug.LogError($"[LOOT] ERROR: {lootEx.Message}\n{lootEx.StackTrace}");
            }
            // ═══ END LOOT ═══

            // ═══ QUEST ITEM DROPS — KillDropTrigger from quest GC files ═══
            // For each active quest, use the same authored ancestry candidate
            // set as quest kill objectives so drop rules do not depend on
            // prefix/string matching between world.* and creatures.* paths.
            try
            {
                var pState = QuestManager.Instance.GetPlayerState(conn.ConnId.ToString());
                if (pState != null && pState.ActiveQuests != null && pState.ActiveQuests.Count > 0)
                {
                    var qdCandidates = new System.Collections.Generic.List<string>();
                    if (monster.AuthoredArchetypeAncestry != null)
                    {
                        foreach (string ancestryPath in monster.AuthoredArchetypeAncestry)
                        {
                            if (!string.IsNullOrWhiteSpace(ancestryPath) &&
                                !qdCandidates.Contains(ancestryPath, StringComparer.OrdinalIgnoreCase))
                                qdCandidates.Add(ancestryPath);
                        }
                    }
                    if (!string.IsNullOrEmpty(monster.SpawnGCType))
                        if (!qdCandidates.Contains(monster.SpawnGCType, StringComparer.OrdinalIgnoreCase))
                            qdCandidates.Add(monster.SpawnGCType);
                    if (!string.IsNullOrEmpty(monster.GCType))
                        if (!qdCandidates.Contains(monster.GCType, StringComparer.OrdinalIgnoreCase))
                            qdCandidates.Add(monster.GCType);

                    var activeIds = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var aq in pState.ActiveQuests)
                        if (!string.IsNullOrEmpty(aq.QuestId)) activeIds.Add(aq.QuestId);

                    // Dedup credited items within this single kill so a wolf
                    // pelt can be credited at most once even if multiple rules
                    // match (e.g. if pup gctype matches via both SpawnGCType
                    // and creature GCType lookups).
                    var creditedItemTypes = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    int qdRolled = 0, qdHit = 0;
                    foreach (var cand in qdCandidates)
                    {
                        if (string.IsNullOrEmpty(cand)) continue;
                        if (!DatabaseLoader.QuestKillDropsByMonster.TryGetValue(cand, out var rules)) continue;

                        foreach (var rule in rules)
                        {
                            if (!activeIds.Contains(rule.QuestId)) continue;
                            if (creditedItemTypes.Contains(rule.ItemGcType)) continue;
                            if (rule.Chance < 1) continue;

                            qdRolled++;
                            uint killDropRaw = NativeRandomStreams.GenerateGlobalStatic(
                                "KillDropTrigger::doEvent.server-chance",
                                $"quest={rule.QuestId}:item={rule.ItemGcType}:monster={monster.Name}#{monster.EntityId}");
                            int killDropRoll = (int)(killDropRaw % (uint)rule.Chance);
                            Debug.LogError($"[KILLDROP-NATIVE] quest={rule.QuestId} item={rule.ItemGcType} monster={monster.Name}#{monster.EntityId} event=0x20 chanceDenom={rule.Chance} raw=0x{killDropRaw:X8} roll={killDropRoll} stream=globalStatic native=KillDropTrigger::doEvent@0x005CACB0 KillDropTrigger::doEvent.server-chance NativeRandomStreams.GenerateGlobalStatic");
                            if (killDropRoll != 0) continue;

                            // Hit! Put the quest item in the player's inventory
                            // via the established GiveStackedItem path (the same
                            // path used for King's Coin rewards on quest turn-in
                            // — opcode 0x1E AddItem for new stacks, 0x22
                            // UpdateQuantity for topping up existing ones).
                            //
                            // Then notify QuestManager so the objective counter
                            // increments and the client gets a progress packet.
                            // GiveStackedItem doesn't call NotifyQuestItemAcquired
                            // internally — we have to do it explicitly.
                            creditedItemTypes.Add(rule.ItemGcType);
                            qdHit++;
                            Debug.LogError($"[QUEST-DROP] ★ {rule.ItemGcType} for {conn.LoginName} ({rule.QuestId}, 1-in-{rule.Chance})");
                            // Spawn quest item on ground near dead mob
                            var qdItem = new GCObject
                            {
                                GCClass = rule.ItemGcType,
                                NativeClass = "Item",
                                StoredLevel = 1
                            };
                            var qdPlacement = ResolveNativeItemDropPlacement(
                                conn,
                                conn.CurrentZoneName,
                                conn.InstanceId,
                                monster.PosX,
                                monster.PosY,
                                monster.PosZ,
                                monster.Heading,
                                $"killdrop-item:{monster.Name}#{monster.EntityId}:{rule.ItemGcType}");
                            float qdPx = qdPlacement.X;
                            float qdPy = qdPlacement.Y;
                            float qdPz = qdPlacement.Z;
                            ushort qdEntityId = GetNextLootEntityId();
                            TrackDroppedItem(qdEntityId, qdItem, conn, 1, qdPx, qdPy, qdPz, 1);
                            if (_droppedItems.TryGetValue(qdEntityId, out var qdInfo))
                                qdInfo.IsQuestItem = true;
                            SendDroppedItemSpawnPacket(conn, qdEntityId, _droppedItems[qdEntityId]);
                        }
                    }
                    if (qdRolled > 0)
                        Debug.LogError($"[QUEST-DROP] {monster.Name}: rolled {qdRolled}, credited {qdHit}");
                }
            }
            catch (Exception qdEx)
            {
                Debug.LogError($"[QUEST-DROP] ERROR: {qdEx.Message}\n{qdEx.StackTrace}");
            }
            // ═══ END QUEST ITEM DROPS ═══

            Debug.LogError($"[KILL] ═══════════════════════════════════════════════════");
        }

        /// <summary>
        /// Sends XP update to client via Entity::sendUpdate (0x28) type 0x0F on avatar entity.
        /// Client processes this at Hero::processUpdateAddExperience with own scaling.
        /// Payload: packet XP seed(uint32) + sourceLevel(byte); client applies native source-level/ExperienceMod scaling.
        /// </summary>
        private void SendHeroAddExperienceUpdate(RRConnection conn, uint baseXP, uint sourceLevel)
        {
            uint avatarId = GetPlayerAvatarId(conn.LoginName);
            if (avatarId == 0) return;

            var writer = new LEWriter();
            writer.WriteByte(0x07);              // BeginStream
            writer.WriteByte(0x03);              // processEntityUpdate — CEM dispatch table @ 0x5DA730
            writer.WriteUInt16((ushort)avatarId);
            writer.WriteByte(0x0F);              // update type = AddExperience
            writer.WriteUInt32(baseXP);
            writer.WriteByte((byte)sourceLevel); // source level is byte, not uint32 — binary @ 0x4F91A0
            WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);              // EndStream

            SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
            Debug.LogError($"[XP-PACKET] Sent 0x03 via CompressedA: baseXP={baseXP} sourceLevel={sourceLevel}");
        }

        /// <summary>
        /// Public wrapper for admin level-up command to send XP to client.
        /// </summary>
        public void SendAdminXPUpdate(RRConnection conn, uint baseXP, uint sourceLevel)
        {
            SendHeroAddExperienceUpdate(conn, baseXP, sourceLevel);
        }

        /// <summary>
        /// Send FreePlayerExperienceModifier to client.
        /// Binary: Modifiers::processUpdate type 0x00 → processAddModifier @ 0x502280
        /// Reads GC type via readType @ 0x5E3C40, creates instance, calls readData @ 0x4FF390.
        /// TTD-PROVEN: vtable[0xB8] = Modifier::readData (NOT readInit 0x4FF4C0).
        /// readData reads 14 bytes: uint32 ID + byte Level + uint32 PowerLevel + uint32 Duration + byte SourceIsSelf.
        /// CEM reads EntitySynchInfo after processUpdate returns (0x5DB605).
        /// </summary>
        public void SendFreePlayerModifier(RRConnection conn)
        {
            if (conn.ModifiersId == 0)
            {
                Debug.LogError("[XP-MOD] Cannot send FreePlayerModifier — ModifiersId not set");
                return;
            }

            var writer = new LEWriter();
            writer.WriteByte(0x07);              // BeginStream
            writer.WriteByte(0x35);              // ComponentUpdate
            writer.WriteUInt16((ushort)conn.ModifiersId);
            writer.WriteByte(0x00);              // processAddModifier type
            WriteGCType(writer, "avatar.base.FreePlayerExperienceModifier", preserveCase: true);
            // Modifier::readData — 14 bytes (TTD-proven @ 0x4FF390):
            writer.WriteUInt32(1);               // [+0x78] ID
            writer.WriteByte(0);                 // [+0x86] Level
            writer.WriteUInt32(0);               // [+0x80] PowerLevel
            writer.WriteUInt32(0x00000000);      // [+0x7C] Duration (0=permanent)
            writer.WriteByte(0x01);              // [+0x87] SourceIsSelf bit 0 = permanent
            WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);              // EndStream

            SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
            Debug.LogError($"[XP-MOD] Sent FreePlayerModifier (single) modifiersId={conn.ModifiersId}");
        }

        private System.Collections.IEnumerator SendDelayedFreePlayerModifier(RRConnection conn, float delay)
        {
            yield return new DungeonRunners.Engine.WaitForSeconds(delay);
            if (!_freePlayerModifierSent.Contains(conn.LoginName + "_sent"))
            {
                _freePlayerModifierSent.Add(conn.LoginName + "_sent");
                SendFreePlayerModifier(conn);
            }
        }

        /// <summary>
        /// Send a tracked modifier to the client. Same packet format as SendFreePlayerModifier
        /// but with variable GCType/data from the ActiveModifier record.
        /// Binary: Modifiers::processUpdate type 0x00 → processAddModifier @ 0x502280
        /// </summary>
        private void SendTrackedModifier(RRConnection conn, ActiveModifier mod)
        {
            if (conn.ModifiersId == 0)
            {
                Debug.LogError($"[MOD-RESEND] Cannot send '{mod.GCType}' — ModifiersId not set");
                return;
            }

            var writer = new LEWriter();
            writer.WriteByte(0x07);              // BeginStream
            writer.WriteByte(0x35);              // ComponentUpdate
            writer.WriteUInt16((ushort)conn.ModifiersId);
            writer.WriteByte(0x00);              // processAddModifier type
            WriteGCType(writer, mod.GCType, preserveCase: true);
            // Modifier::readData — 14 bytes (TTD-proven @ 0x4FF390):
            writer.WriteUInt32(mod.Id);          // [+0x78] ID
            writer.WriteByte(mod.Level);         // [+0x86] Level
            writer.WriteUInt32(mod.PowerLevel);  // [+0x80] PowerLevel
            writer.WriteUInt32(mod.Duration);    // [+0x7C] Duration (0=permanent)
            writer.WriteByte(mod.SourceIsSelf);  // [+0x87] SourceIsSelf
            WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);              // EndStream

            SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
            Debug.LogError($"[MOD-RESEND] Sent '{mod.GCType}' id={mod.Id} dur={mod.Duration} ticks ({mod.Duration * 24.0 / 1000.0:F1}s) to {conn.LoginName}");
        }

        /// <summary>
        /// Re-send all tracked modifiers after zone transition spawn.
        /// <summary>
        /// Public API for any code to track a modifier sent to a player.
        /// All tracked modifiers are automatically re-sent on zone transition.
        /// Do NOT track FreePlayerModifier — it persists on its own.
        /// </summary>
        public void TrackModifierSent(string loginName, string gcType, uint modId,
            byte level = 0, uint powerLevel = 0, uint duration = 0, byte sourceIsSelf = 0)
        {
            _modifierTracker.TrackModifier(loginName, new ActiveModifier
            {
                GCType = gcType,
                Id = modId,
                Level = level,
                PowerLevel = powerLevel,
                Duration = duration,
                SourceIsSelf = sourceIsSelf,
                AddedAt = DateTime.UtcNow
            });
        }

        /// <summary>
        /// Public API to remove a tracked modifier by GCType.
        /// </summary>
        public void UntrackModifier(string loginName, string gcType)
        {
            _modifierTracker.RemoveModifier(loginName, gcType);
        }

        /// <summary>
        /// Re-send all tracked modifiers after zone transition spawn.
        /// Called after SendPlayerEntitySpawn in both zone transition paths.
        /// Only admin buffs live here — FreePlayer persists on its own.
        /// </summary>
        private void ResendAllModifiers(RRConnection conn)
        {
            if (conn.LoginName == null || conn.ModifiersId == 0) return;

            var mods = _modifierTracker.GetModifiers(conn.LoginName);
            if (mods.Count == 0) return;

            Debug.LogError($"[MOD-RESEND] Re-sending {mods.Count} modifiers for {conn.LoginName} after zone transition");
            foreach (var mod in mods)
            {
                SendTrackedModifier(conn, mod);
            }
        }

        /// <summary>
        /// Iterates a monster's Manipulators, extracts the short GC name,
        /// and resolves it through _weaponDebuffMap then _creatureDebuffMap.
        /// Returns list of (modifierGcType, durationInTicks).
        /// </summary>
        private List<(string modGcType, uint durationTicks)> ResolveMonsterDebuffs(Combat.Monster monster)
        {
            var result = new List<(string, uint)>();
            if (monster?.Manipulators == null) return result;

            // Manipulators is Dictionary<string, ManipulatorData>; key = manipulator name/gcType
            foreach (var kvp in monster.Manipulators)
            {
                // Use the ManipulatorData.gcType if available, fall back to the key
                string gcType = !string.IsNullOrEmpty(kvp.Value?.gcType) ? kvp.Value.gcType : kvp.Key;
                if (string.IsNullOrEmpty(gcType)) continue;

                // Extract short name: "skills.creature.BasicSlow" → "basicslow"
                string shortName = gcType.ToLowerInvariant();
                int dot = shortName.LastIndexOf('.');
                if (dot >= 0) shortName = shortName.Substring(dot + 1);

                // Look up weapon map first (maps weapon GC names → debuff skill names),
                // then try direct creature debuff map lookup
                string debuffKey = null;
                if (_weaponDebuffMap.TryGetValue(shortName, out var wk)) debuffKey = wk;
                else if (_creatureDebuffMap.ContainsKey(shortName)) debuffKey = shortName;

                if (debuffKey == null) continue;
                if (!_creatureDebuffMap.TryGetValue(debuffKey, out var debuffInfo)) continue;

                uint ticks = debuffInfo.durationSec <= 0 ? 0
                    : (uint)(debuffInfo.durationSec * (1000.0 / 24.0));
                result.Add((debuffInfo.modGcType, ticks));
            }
            return result;
        }

        /// <summary>
        /// Sends processAddModifier for each resolved monster debuff and tracks it.
        /// Respects per-modifier cooldown so the same debuff is not re-applied
        /// until its duration has expired.
        /// [MON-DEBUFF] log tag.
        /// </summary>
        private void ApplyMonsterDebuffs(RRConnection conn, Combat.Monster monster)
        {
            if (conn.ModifiersId == 0 || conn.LoginName == null) return;

            var debuffs = ResolveMonsterDebuffs(monster);
            if (debuffs.Count == 0) return;
            Debug.LogError($"[MON-DEBUFF] skipped state-message debuff lane player={conn.LoginName} monster={monster?.Name ?? "unknown"}#{monster?.EntityId ?? 0} count={debuffs.Count} native=ActiveSkill::doSkillEffect@0x00539630/SpellModEffect::doEffect@0x00554460 reason=not-owned-by-StateMachine-message");
        }


        /// <summary>
        /// Sends RemoveExperience to client via Entity::sendUpdate type 0x10.
        /// Binary: Hero::onRemoveExperience @ 0x4F88B0 — RAW subtraction, floors at level threshold.
        /// Used to correct client's auto-calculated kill XP when xpMultiplier &lt; ExperienceMod(5.0).
        /// </summary>
        private void SendHeroRemoveExperienceUpdate(RRConnection conn, uint xpAmount)
        {
            uint avatarId = GetPlayerAvatarId(conn.LoginName);
            if (avatarId == 0) return;

            var writer = new LEWriter();
            writer.WriteByte(0x07);              // BeginStream
            writer.WriteByte(0x03);              // processEntityUpdate
            writer.WriteUInt16((ushort)avatarId);
            writer.WriteByte(0x10);              // update type = RemoveExperience
            writer.WriteUInt32(xpAmount);         // XP to remove (RAW, no scaling)
            WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);              // EndStream

            SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
            Debug.LogError($"[XP-PACKET] Sent RemoveExperience: {xpAmount}");
        }

        /// <summary>
        /// Send HP sync after admin level change so client updates HP bar.
        /// </summary>
        public void SendAdminHPSync(RRConnection conn, PlayerState ps)
        {
            if (conn.UnitBehaviorId == 0) return;

            var writer = new LEWriter();
            writer.WriteByte(0x07);              // BeginStream
            writer.WriteByte(0x35);              // ComponentUpdate on UnitBehavior
            writer.WriteUInt16((ushort)conn.UnitBehaviorId);
            writer.WriteByte(0x65);              // UnitMoverUpdate
            writer.WriteByte(conn.SessionID++);
            writer.WriteByte(0x01);              // Update count
            writer.WriteByte(0x03);              // Flags: heading + position
            writer.WriteInt32((int)(conn.PlayerHeading * 256));
            writer.WriteInt32((int)(conn.PlayerPosX * 256));
            writer.WriteInt32((int)(conn.PlayerPosY * 256));
            WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);              // EndStream
            SendToClient(conn, writer.ToArray());
            Debug.LogError($"[ADMIN-HP] Sent HP sync via UnitBehavior: {ps.CurrentHPWire / 256}/{ps.MaxHPWire / 256}");
        }

        /// <summary>
        /// Callback for admin LevelSelf button: sends XP packets to trigger client-side
        /// level-up effects (Avatar::OnLevelUp -> LevelEffect + LevelUpSound), then HP sync.
        /// </summary>
        private void HandleAdminLevelUp(RRConnection conn, PlayerState ps, int oldLevel, int newLevel)
        {
            // Gate: only admins can use /adminui level-up
            if (!IsPlayerAdmin(conn.LoginName))
            {
                SendSystemMessage(conn, "You do not have permission.");
                return;
            }

            if (conn.Avatar != null)
                CalculateEquipmentBonuses(conn.ConnId.ToString(), conn.Avatar);
            ps.RestoreToFull();

            // Client formula: clientXP = (packetXP * ExperienceMod + 128) >> 8
            // Reverse: packetXP = threshold * 256 / ExperienceMod + margin
            for (int lv = oldLevel; lv < newLevel; lv++)
            {
                uint threshold = PlayerState.GetClientThreshold(lv + 1);
                uint packetXP = threshold * 256 / 5 + 100;
                SendAdminXPUpdate(conn, packetXP, (uint)lv);
                Debug.LogError($"[ADMIN-LEVELUP] Level {lv}->{lv + 1}: threshold={threshold} packetXP={packetXP}");
            }

            SendAdminHPSync(conn, ps);
            Debug.LogError($"[ADMIN-LEVELUP] Sent {newLevel - oldLevel} XP packet(s) + HP sync for level {oldLevel}->{newLevel}");
            // Posse: refresh other members' rosters with the new Lvl value.
            try { if (conn.CharSqlId != 0) PosseManager.Instance.NotifyMemberStateChange(conn.CharSqlId, this); }
            catch (Exception px) { Debug.LogError($"[POSSE] level-up notify failed: {px.Message}"); }
        }


        /// <summary>
        /// Handles client movement updates (0x02) - WASD or pathfinding movement
        /// </summary>
        private void HandleClientMove(RRConnection conn, LEReader reader, ushort componentId)
        {
            try
            {
                byte sessionId = reader.ReadByte();
                byte moveCount = reader.ReadByte();

                float lastX = 0, lastY = 0, lastHeading = 0;
                float previousX = conn.PlayerPosX;
                float previousY = conn.PlayerPosY;
                float previousHeading = conn.PlayerHeading;
                bool hadActiveUseTarget = conn.HasActiveUseTarget;
                ushort activeUseTargetId = conn.ActiveUseTargetId;
                byte activeUseTargetFlags = conn.ActiveUseTargetFlags;
                ushort activeUseTargetComponentId = conn.ActiveUseTargetComponentId != 0 ? conn.ActiveUseTargetComponentId : componentId;
                byte activeUseTargetSessionId = conn.ActiveUseTargetSessionId;
                bool activeUseTargetStartedWeaponUse = conn.ActiveUseTargetStartedWeaponUse;
                Monster activeUseTargetMonster = hadActiveUseTarget
                    ? CombatManager.Instance.GetMonster(activeUseTargetId) ?? CombatManager.Instance.GetMonsterByComponent(activeUseTargetId)
                    : null;
                byte lastMoveFlags = 0;
                byte mergedMoveFlags = 0;
                // Capture raw move data for multiplayer relay
                int rawStartPos = reader.Position;
                for (int i = 0; i < moveCount; i++)
                {
                    byte moveType = reader.ReadByte();
                    int heading = reader.ReadInt32();
                    int posX = reader.ReadInt32();
                    int posY = reader.ReadInt32();

                    lastMoveFlags = moveType;
                    mergedMoveFlags |= (byte)(moveType & 0x07);
                    lastX = posX / 256.0f;
                    lastY = posY / 256.0f;
                    lastHeading = heading / 256.0f;
                }
                int rawEndPos = reader.Position;
                TryConsumeClientSyncSuffix(conn, reader, "MOVE-SYNC");

                if (moveCount > 0)
                {
                    bool positionChanged = !conn.HasLivePlayerPosition || Math.Abs(lastX - previousX) > 0.001f || Math.Abs(lastY - previousY) > 0.001f;
                    bool headingChanged = HasNativeHeadingDelta(previousHeading, lastHeading, 0.01f);
                    if (ShouldCancelUseTargetFromClientMove(hadActiveUseTarget, moveCount, mergedMoveFlags, positionChanged, headingChanged))
                    {
                        ClearUseTargetForClientMovement(
                            conn,
                            activeUseTargetMonster,
                            activeUseTargetId,
                            activeUseTargetFlags,
                            activeUseTargetComponentId,
                            activeUseTargetSessionId,
                            activeUseTargetStartedWeaponUse,
                            sessionId,
                            lastMoveFlags,
                            mergedMoveFlags,
                            previousX,
                            previousY,
                            previousHeading,
                            lastX,
                            lastY,
                            lastHeading);
                    }
                    if (positionChanged && IsZoneSpawnInvulnerabilityBlockingCombat(conn))
                        ClearZoneSpawnInvulnerability(conn, "MOVE");
                    conn.PlayerPosX = lastX;
                    conn.PlayerPosY = lastY;
                    conn.PlayerHeading = lastHeading;
                    conn.HasLivePlayerPosition = true;
                    conn.LivePlayerPosX = lastX;
                    conn.LivePlayerPosY = lastY;
                    conn.LivePlayerPosZ = conn.PlayerPosZ;
                    conn.LivePlayerHeading = lastHeading;
                    conn.LivePlayerPositionTime = Time.time;

                    uint avatarId = GetPlayerAvatarId(conn.LoginName);
                    if (avatarId != 0)
                    {
                        CombatManager.Instance.UpdatePlayerPosition(avatarId, lastX, lastY);
                    }
                    // Throttled proximity check for goto quest objectives (1x/sec max)
                    CheckGotoProximity(conn);
                    CheckPendingMerchantActivation(conn);
                    // CheckWorldEntityProximity removed — click activation works via TTD-proven UnitBehavior
                }
                conn.SessionID = sessionId;

                // ═══ MULTIPLAYER: Movement relay ═══
                byte[] rawMoveData = reader.GetRawBytes(rawStartPos, rawEndPos - rawStartPos);
                QueueLocalPlayerMovementAck(conn, sessionId, moveCount, rawMoveData);
                // If force relay active (clicked obelisk/world object), wait for the player
                // to move significantly, then send ONE clean walk-to and suppress.
                // First few 0x65 after click are near the start position — skip those.
                bool usedForceRelay = false;
                if (_forceRelayUntil.TryGetValue(conn.ConnId, out float relayDeadline) && Time.time < relayDeadline)
                {
                    _lastRelayPosX.TryGetValue(conn.LoginName, out float relayLastX);
                    _lastRelayPosY.TryGetValue(conn.LoginName, out float relayLastY);
                    float rdx = lastX - relayLastX;
                    float rdy = lastY - relayLastY;
                    float rDistSq = rdx * rdx + rdy * rdy;

                    if (rDistSq > 4.0f)  // Moved > 2 units — real pathfinding movement
                    {
                        _forceRelayUntil.Remove(conn.ConnId);
                        BroadcastWalkToPosition(conn, lastX, lastY);
                        usedForceRelay = true;
                        Debug.LogError($"[MP-WALK] Force-relay: walk-to ({lastX:F1}, {lastY:F1}) dist={Math.Sqrt(rDistSq):F1}");
                    }
                }
                if (!usedForceRelay)
                {
                    BroadcastPlayerMovement(conn, sessionId, moveCount, rawMoveData);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MOVE] ERROR: {ex.Message}");
            }
        }

        private static bool ShouldCancelUseTargetFromClientMove(bool hadActiveUseTarget, byte moveCount, byte mergedMoveFlags, bool positionChanged, bool headingChanged)
        {
            if (!hadActiveUseTarget || moveCount == 0)
                return false;
            if (!positionChanged && !headingChanged)
                return false;
            return (mergedMoveFlags & 0x07) != 0 || positionChanged || headingChanged;
        }

        private void ClearUseTargetForClientMovement(
            RRConnection conn,
            Monster target,
            ushort targetId,
            byte useFlags,
            ushort componentId,
            byte useTargetSessionId,
            bool startedWeaponUse,
            byte moveSessionId,
            byte lastMoveFlags,
            byte mergedMoveFlags,
            float previousX,
            float previousY,
            float previousHeading,
            float lastX,
            float lastY,
            float lastHeading)
        {
            if (conn == null)
                return;

            bool hadUseTarget = conn.HasActiveUseTarget;
            ClearUseTarget(conn);
            Combat.WeaponCycleTracker.Instance.CancelConnectionUseTargetIntent(conn.ConnId.ToString(), "MOVE-CANCEL-USETARGET");
            if (conn.Avatar != null)
                CombatManager.Instance.SetPlayerActiveClientAttack((uint)conn.Avatar.Id, false);

            float moveDx = lastX - previousX;
            float moveDy = lastY - previousY;
            float moved = (float)Math.Sqrt((moveDx * moveDx) + (moveDy * moveDy));
            float headingDelta = NativeHeadingDelta(previousHeading, lastHeading);
            float oldTargetDist = target != null ? Distance2D(previousX, previousY, target.PosX, target.PosY) : -1f;
            float newTargetDist = target != null ? Distance2D(lastX, lastY, target.PosX, target.PosY) : -1f;
            string targetText = target != null
                ? $"{target.EntityId}/{target.BehaviorId}"
                : targetId.ToString();

            Debug.LogError($"[MOVE-CANCEL-USETARGET] player={conn.ConnId} target={targetText} hadUseTarget={hadUseTarget} useFlags=0x{useFlags:X2} component=0x{componentId:X4} useSession={useTargetSessionId} startedWeaponUse={startedWeaponUse} moveSession={moveSessionId} lastMoveFlags=0x{lastMoveFlags:X2} mergedMoveFlags=0x{mergedMoveFlags:X2} pos=({previousX:F1},{previousY:F1})->({lastX:F1},{lastY:F1}) moved={moved:F2} heading={previousHeading:F1}->{lastHeading:F1} headingDelta={headingDelta:F2} targetDist={oldTargetDist:F2}->{newTargetDist:F2} sendControlReset=False preservedProjectiles=True native=ClientUnitBehavior::DoMoveToPoint+StartMovingInDirection+DoTurn");
        }

        private static bool HasNativeHeadingDelta(float previousHeading, float nextHeading, float epsilon)
        {
            return NativeHeadingDelta(previousHeading, nextHeading) > epsilon;
        }

        private static float NativeHeadingDelta(float previousHeading, float nextHeading)
        {
            float delta = (nextHeading - previousHeading) % 360f;
            if (delta > 180f)
                delta -= 360f;
            else if (delta < -180f)
                delta += 360f;
            return Math.Abs(delta);
        }

        private static float Distance2D(float ax, float ay, float bx, float by)
        {
            float dx = bx - ax;
            float dy = by - ay;
            return (float)Math.Sqrt((dx * dx) + (dy * dy));
        }

        private uint ResolveMonsterExperienceReward(Combat.Monster monster)
        {
            if (monster == null) return 0;
            double difficulty = monster.ExperienceDifficulty;
            if (difficulty <= 0d) return 0;
            int sourceLevel = Math.Max(1, (int)monster.Level);
            double reward = PlayerState.GetBaseXPForLevel(sourceLevel) * difficulty;
            if (reward < 1d) reward = 1d;
            if (reward >= uint.MaxValue) return uint.MaxValue;
            return (uint)Math.Round(reward, MidpointRounding.AwayFromZero);
        }

        private uint ResolveClientVisibleExperienceReward(RRConnection conn, Combat.Monster monster, PlayerState playerState, uint packetXP, uint sourceLevel)
        {
            if (monster == null || playerState == null || packetXP == 0) return 0;
            int playerLevel = Math.Max(1, playerState.Level);
            int nativeSourceLevel = Math.Max(1, (int)Math.Min(255u, sourceLevel));
            if (nativeSourceLevel <= playerLevel - 5)
                return 0;

            int effectiveLevel = Math.Min(nativeSourceLevel, playerLevel);
            long numerator = (long)(effectiveLevel << 8) << 8;
            int denominator = playerLevel << 8;
            int ratioF32 = denominator != 0 ? (int)(numerator / denominator) : 0;
            long xpF32 = ((long)packetXP * ratioF32) >> 8;

            bool isFree = conn != null && !string.IsNullOrEmpty(conn.LoginName) && IsPlayerFree(conn.LoginName);
            if (isFree)
            {
                float freePlayerXPMult = GCDatabase.Instance.GetKnob("FreePlayerExperienceMult", 0.87f);
                int freeMultF32 = (int)(freePlayerXPMult * 256.0f);
                xpF32 = (xpF32 * freeMultF32) >> 8;
            }

            long scaledXPBase = (xpF32 * 100L) >> 8;
            float experienceMod = GCDatabase.Instance.GetKnob("ExperienceMod", 5.0f);
            int experienceModPercent = (int)(experienceMod * 100.0f);
            long finalXP = scaledXPBase * experienceModPercent / 100L;

            if (finalXP <= 0) return 0;
            if (finalXP >= uint.MaxValue) return uint.MaxValue;
            uint result = (uint)finalXP;
            Debug.LogError($"[XP-NATIVE] monster={monster.Name}#{monster.EntityId} packetXP={packetXP} difficulty={monster.ExperienceDifficulty:F2} sourceLevel={nativeSourceLevel} playerLevel={playerLevel} effectiveLevel={effectiveLevel} ratioF32={ratioF32} free={isFree} xpF32={xpF32} effectiveXP={result}");
            return result;
        }

        private void QueueLocalPlayerMovementAck(RRConnection conn, byte sessionId, byte moveCount, byte[] rawMoveData)
        {
            if (moveCount == 0 || rawMoveData == null || rawMoveData.Length == 0) return;
            const int recordSize = 13;
            int rawCount = Math.Min(moveCount, rawMoveData.Length / recordSize);
            if (rawCount <= 0) return;
            int rawBytes = rawCount * recordSize;
            var normalized = new byte[rawBytes];
            Buffer.BlockCopy(rawMoveData, 0, normalized, 0, rawBytes);
            lock (conn)
            {
                if (conn.PendingLocalMoveSessionId == sessionId && conn.PendingLocalMoveCount > 0 && conn.PendingLocalMoveData != null && conn.PendingLocalMoveData.Length > 0)
                {
                    int pendingCount = Math.Min(conn.PendingLocalMoveCount, conn.PendingLocalMoveData.Length / recordSize);
                    int pendingBytes = pendingCount * recordSize;
                    var combined = new byte[pendingBytes + rawBytes];
                    Buffer.BlockCopy(conn.PendingLocalMoveData, 0, combined, 0, pendingBytes);
                    Buffer.BlockCopy(normalized, 0, combined, pendingBytes, rawBytes);
                    int combinedCount = combined.Length / recordSize;
                    int keepCount = Math.Min(255, combinedCount);
                    int keepBytes = keepCount * recordSize;
                    var kept = new byte[keepBytes];
                    Buffer.BlockCopy(combined, combined.Length - keepBytes, kept, 0, keepBytes);
                    conn.PendingLocalMoveData = kept;
                    conn.PendingLocalMoveCount = (byte)keepCount;
                }
                else
                {
                    conn.PendingLocalMoveSessionId = sessionId;
                    conn.PendingLocalMoveCount = (byte)Math.Min(255, rawCount);
                    conn.PendingLocalMoveData = normalized;
                }
                float due = Time.time + 0.008f;
                if (conn.PendingLocalMoveFlushAt <= 0f || conn.PendingLocalMoveFlushAt > due)
                {
                    conn.PendingLocalMoveFlushAt = due;
                }
            }
        }

        private static bool TryNormalizeUnitMoverUpdateData(byte moveCount, byte[] rawMoveData, out byte safeMoveCount, out byte[] safeMoveData)
        {
            const int recordSize = 13;
            safeMoveCount = 0;
            safeMoveData = Array.Empty<byte>();
            if (moveCount == 0 || rawMoveData == null || rawMoveData.Length == 0) return false;
            int availableCount = Math.Min(moveCount, rawMoveData.Length / recordSize);
            if (availableCount <= 0) return false;
            int safeBytes = availableCount * recordSize;
            safeMoveCount = (byte)availableCount;
            if (safeBytes == rawMoveData.Length)
            {
                safeMoveData = rawMoveData;
                return true;
            }
            safeMoveData = new byte[safeBytes];
            Buffer.BlockCopy(rawMoveData, 0, safeMoveData, 0, safeBytes);
            return true;
        }

        private bool TryWritePendingLocalPlayerMovementAck(RRConnection conn, LEWriter writer)
        {
            if (conn == null || !conn.IsConnected) return false;
            if (conn.UnitBehaviorId == 0) return false;
            byte sessionId;
            byte moveCount;
            byte[] rawMoveData;
            lock (conn)
            {
                if (conn.PendingLocalMoveCount == 0 || conn.PendingLocalMoveData == null || conn.PendingLocalMoveData.Length == 0) return false;
                if (Time.time < conn.PendingLocalMoveFlushAt) return false;
                sessionId = conn.PendingLocalMoveSessionId;
                moveCount = conn.PendingLocalMoveCount;
                rawMoveData = conn.PendingLocalMoveData;
            }
            if (!TryNormalizeUnitMoverUpdateData(moveCount, rawMoveData, out byte safeMoveCount, out byte[] safeMoveData))
                return false;
            if (safeMoveCount != moveCount || !ReferenceEquals(safeMoveData, rawMoveData))
                Debug.LogError($"[WORLD-MOVERACK] normalized session=0x{sessionId:X2} count={moveCount}->{safeMoveCount} raw={rawMoveData?.Length ?? 0}->{safeMoveData.Length}");
            var ackWriter = new LEWriter();
            ackWriter.WriteByte(0x35);
            ackWriter.WriteUInt16((ushort)conn.UnitBehaviorId);
            ackWriter.WriteByte(0x65);
            ackWriter.WriteByte(sessionId);
            ackWriter.WriteByte(safeMoveCount);
            ackWriter.WriteBytes(safeMoveData);
            if (!TryWriteEntitySynchForComponent(conn, ackWriter, (ushort)conn.UnitBehaviorId, 0x65, SyncContext.MoverAck, "WorldMoverAck", true))
                return false;
            lock (conn)
            {
                if (conn.PendingLocalMoveSessionId == sessionId && conn.PendingLocalMoveCount == moveCount && ReferenceEquals(conn.PendingLocalMoveData, rawMoveData))
                {
                    conn.PendingLocalMoveCount = 0;
                    conn.PendingLocalMoveData = Array.Empty<byte>();
                    conn.PendingLocalMoveFlushAt = 0f;
                }
            }
            writer.WriteBytes(ackWriter.ToArray());
            return true;
        }
        private void HandlePlayerAttack(RRConnection conn, ushort componentId, ushort targetEntityId, byte responseId, byte sessionId)
        {
            Debug.LogError($"[ATTACK] Player {conn.LoginName} attacking target {targetEntityId}");

            // Check if target is a monster
            var monster = CombatManager.Instance.GetMonster(targetEntityId);
            if (monster == null)
            {
                Debug.LogError($"[ATTACK] Target {targetEntityId} is not a monster");
                return;
            }

            if (!monster.IsAlive)
            {
                Debug.LogError($"[ATTACK] Monster {monster.Name} is already dead");
                return;
            }

            // Get player's avatar ID for combat
            uint avatarId = GetPlayerAvatarId(conn.LoginName);

            // HP DESYNC FIX: Do NOT calculate damage server-side!
            // Client calculates damage locally via Mersenne Twister RNG.
            // Server must use client-reported values (from 0x36 sync and 0x08 CombatTick).
            int damage = 0; // Real damage comes from client
            var result = new DamageResult { Success = true, DamageDealt = 0, DefenderDied = false, NewHPWire = 0 };
            var monster_check = CombatManager.Instance.GetMonster(targetEntityId);
            if (monster_check != null)
            {
                result.NewHPWire = CombatManager.Instance.GetMonsterCurrentHPWire(monster_check, "HANDLE-PLAYER-ATTACK");
                result.DefenderDied = !monster_check.IsAlive;
            }

            if (result.Success)
            {
                Debug.LogError($"[ATTACK] Hit {monster.Name} for {damage} damage! HP: {CombatManager.Instance.PeekMonsterCurrentHPWire(monster) / 256}/{monster.MaxHPWire / 256}");

                // Send damage event to client
                var damageEvt = new DamageEvent
                {
                    AttackerId = avatarId,
                    DefenderId = targetEntityId,
                    DamageAmount = damage,
                    DamageWire = (uint)(damage * 256),
                    IsCritical = false,
                    PosX = monster.PosX,
                    PosY = monster.PosY,
                    PosZ = monster.PosZ
                };

                // Send damage to attacking player only (multiplayer: shared monster IDs needed for broadcast)
                byte[] damagePacket = CombatPackets.BuildDamagePacket(damageEvt);
                SendCompressedA(conn, 0x01, 0x0F, damagePacket);

                // If monster died, handle death
                if (result.DefenderDied)
                {
                    TryFinalizeMonsterKill(conn, monster, "HandlePlayerAttack");
                }
            }
        }



        // 🔥 ADD THIS NEW METHOD:
        private void HandleComponentUpdate(RRConnection conn, LEReader reader)
        {
            try
            {
                if (reader.Remaining < 3)
                {
                    Debug.LogError($"[COMPONENT] ERROR: Not enough data! Remaining={reader.Remaining}");
                    return;
                }
                ushort componentId = reader.ReadUInt16();
                byte subMessage = reader.ReadByte();

                // ═══ DIAGNOSTIC: Log ALL component updates to find skill equip ═══
                if (VerbosePacketLogging && subMessage >= 0x30 && subMessage <= 0x3F)
                {
                    byte[] diagRaw = reader.PeekRemaining();
                    string diagHex = diagRaw.Length > 0 ? BitConverter.ToString(diagRaw) : "(empty)";
                    Debug.LogError($"[DIAG-0x3x] cid=0x{componentId:X4} sub=0x{subMessage:X2} remaining={diagRaw.Length} hex={diagHex}");
                }
                // --- 0x64: State Machine dispatch ---
                if (subMessage == 0x64)
                {
                    // Monster state machine (cid 50000-60000)
                    if (componentId >= 50000 && componentId < 60000)
                    {
                        HandleMonsterStateMachineUpdate(conn, reader, componentId);
                        return;
                    }

                    // Player state machine
                    // Player state machine — binary-verified flag format from 0x5F0C70
                    if (reader.Remaining >= 1)
                    {
                        byte flags = reader.ReadByte();
                        ushort messageType = 0xFFFF;
                        ushort scope = 0xFFFF;
                        ushort target = 0;
                        uint value = 0;

                        // bit 1 (0x02): uint16 messageType
                        if ((flags & 0x02) != 0 && reader.Remaining >= 2)
                            messageType = reader.ReadUInt16();
                        // bit 2 (0x04): uint16 scope
                        if ((flags & 0x04) != 0 && reader.Remaining >= 2)
                            scope = reader.ReadUInt16();
                        // bit 3 (0x08): uint16 target
                        if ((flags & 0x08) != 0 && reader.Remaining >= 2)
                            target = reader.ReadUInt16();
                        // bit 5 (0x20): uint32 value
                        if ((flags & 0x20) != 0 && reader.Remaining >= 4)
                            value = reader.ReadUInt32();
                        // bit 4 (0x10): watcher list
                        if ((flags & 0x10) != 0 && reader.Remaining >= 2)
                        {
                            ushort wCount = reader.ReadUInt16();
                            for (int w = 0; w < wCount && reader.Remaining >= 2; w++)
                                reader.ReadUInt16();
                        }

                        // Consume sync suffix
                        if (reader.Remaining >= 1)
                        {
                            byte syncFlags = reader.ReadByte();
                            if ((syncFlags & 0x02) != 0 && reader.Remaining >= 4)
                            {
                                uint syncHP = reader.ReadUInt32();
                                ObserveClientPlayerHP(conn, syncHP, "PLAYER-STATE-SYNC");
                            }
                        }

                        if (VerbosePacketLogging) Debug.LogError($"[PLAYER-STATE] cid={componentId} flags=0x{flags:X2} type={messageType} value={value}");

                        // Type 0x1C (28) = LEVEL UP CONFIRMATION from client's Avatar::OnLevelUp
                        // Server XP is authoritative — 0x1C is just the client confirming it processed
                        // the level-up locally. Do NOT reinitialize stats or reset XP here.
                        if (messageType == 0x1C)
                        {
                            var playerState = GetPlayerState(conn.ConnId.ToString());
                            if (playerState != null)
                            {
                                Debug.LogError($"[LEVEL-UP-0x1C] Client confirms level-up. Server level={playerState.Level} XP={playerState.Experience}");
                            }
                        }
                    }
                    return;
                }

                if (subMessage == 0x65)
                {
                    if (componentId >= 50000 && componentId < 60000)
                    {
                        try
                        {
                            if (reader.Remaining > 0)
                            {
                                byte[] rawPeek = reader.PeekRemaining();
                                if (VerbosePacketLogging) Debug.LogError($"[MONSTER-0x65] cid={componentId} remaining={reader.Remaining} raw={BitConverter.ToString(rawPeek)}");
                            }

                            var monster = CombatManager.Instance.GetMonsterByComponent(componentId);
                            int componentOffset = CombatManager.Instance.GetComponentOffset(componentId);
                            if (monster != null && componentOffset == 1 && reader.Remaining >= 2)
                            {
                                byte sessionId = reader.ReadByte();
                                byte moveCount = reader.ReadByte();
                                int applied = 0;
                                for (int i = 0; i < moveCount && reader.Remaining >= 13; i++)
                                {
                                    byte moveFlags = reader.ReadByte();
                                    int headingRaw = reader.ReadInt32();
                                    int posXRaw = reader.ReadInt32();
                                    int posYRaw = reader.ReadInt32();
                                    monster.SessionId = sessionId;
                                    monster.Heading = headingRaw / 256f;
                                    monster.PosX = posXRaw / 256f;
                                    monster.PosY = posYRaw / 256f;
                                    if (monster.EntityId <= ushort.MaxValue)
                                        _allEntityPositions[(ushort)monster.EntityId] = (monster.PosX, monster.PosY, monster.PosZ);
                                    applied++;
                                    if (VerbosePacketLogging) Debug.LogError($"[MONSTER-MOVE-0x65] {monster.Name} cid={componentId} flags=0x{moveFlags:X2} session={sessionId} pos=({monster.PosX:F1},{monster.PosY:F1}) heading={monster.Heading:F1}");
                                }
                                if (applied != moveCount)
                                    Debug.LogError($"[MONSTER-MOVE-0x65] {monster.Name} cid={componentId} expected={moveCount} applied={applied} remaining={reader.Remaining}");
                            }

                            if (reader.Remaining >= 1)
                            {
                                byte syncFlags = reader.ReadByte();
                                if (VerbosePacketLogging) Debug.LogError($"[MONSTER-0x65] cid={componentId} offset={componentOffset} syncFlags=0x{syncFlags:X2}");

                                if ((syncFlags & 0x02) != 0 && reader.Remaining >= 4)
                                {
                                    uint clientHP = reader.ReadUInt32();
                                    int clientActual = (int)(clientHP / 256);
                                    if (monster != null)
                                    {
                                        int serverActual = (int)(CombatManager.Instance.PeekMonsterCurrentHPWire(monster) / 256);
                                        if (VerbosePacketLogging) Debug.LogError($"[MONSTER-0x65] {monster.Name} (eid={monster.EntityId}) CLIENT_HP={clientActual} SERVER_HP={serverActual} DELTA={serverActual - clientActual}");

                                        CombatManager.Instance.ObserveClientMonsterHP(monster, clientHP, "MONSTER-MOVE-HP-0x65");
                                    }
                                    else
                                    {
                                        if (VerbosePacketLogging) Debug.LogError($"[MONSTER-0x65] WARNING: cid={componentId} not found in CombatManager!");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[MONSTER-0x65] Parse error: {ex.Message}");
                        }
                        return;
                    }

                    HandleClientMove(conn, reader, componentId);
                    return;  // Don't fall through to other handlers
                }
                else if (subMessage == 0x02)
                {
                    if (componentId == conn.QuestManagerId)
                    {
                        Debug.LogError($"[QUEST-0x02] CLOSE/CANCEL - clearing pending state");
                        // CLEAR pending state, DO NOT accept!
                        conn.PendingQuestHash = 0;
                        conn.PendingQuestNpcEntityId = 0;
                        conn.ViewingQuestInstanceId = 0;
                    }
                }
                // In 0x03 handler:
                // subMessage 0x03 = ABANDON QUEST (from quest log panel)
                // In 0x03 handler:
                else if (subMessage == 0x03)
                {
                    // ROOT CAUSE FIX: must match conn.QuestManagerId (typically 0x0210),
                    // not the hardcoded 0x0018. The actual quest manager component id is
                    // assigned per-session at SendPlayerEntitySpawn time. With the old
                    // hardcoded check, every abandon click fell through to HandleCancelAction
                    // which read the first byte of the instanceId as a "sessionId" and sent
                    // back a malformed 0x35 response. The client's processUpdateQuest then
                    // misinterpreted the response bytes as instanceId+questSubmsg+leftovers,
                    // and the sync trailer reader picked up a byte from GetSynchValue()'s
                    // u32 as the sync flag - if that byte was 0x01 the validator crashed
                    // with "Entity synch error" because the player avatar entity expects
                    // either flag 0x00 or 0x02, never 0x01.
                    if (componentId == conn.QuestManagerId)
                    {
                        // QUEST ABANDON.
                        // Wire format from binary RE: client abandon() at 0x5c06c0 writes
                        // exactly 4 bytes (u32 instanceId) and calls sendRequest with type
                        // 0x03. The server receives [opcode 0x35][cid:u16][submsg:u8 = 0x03]
                        // [instanceId:u32]. We respond with SendRemovePacket which writes
                        // submsg 0x02 (= processRemoveQuest in client dispatch table at
                        // 0x5c3630). The client's processRemoveQuest at 0x5c3970 reads u32
                        // instanceId, calls findQuestByInstanceId at 0x5c11b0, and removes
                        // the quest from the local quest log.
                        try
                        {
                            uint instanceId = reader.ReadUInt32();
                            Debug.LogError($"[QUEST-ABANDON] cid=0x{componentId:X4} instanceId=0x{instanceId:X8}");

                            // Drain any trailing bytes (binary RE shows client always sends
                            // exactly 4, but be defensive).
                            while (reader.Remaining > 0)
                            {
                                byte trail = reader.ReadByte();
                                Debug.LogError($"[QUEST-ABANDON] drained trailing byte 0x{trail:X2}");
                            }

                            // Remove from server-side active quest list.
                            var playerState = QuestManager.Instance.GetPlayerState(conn.ConnId.ToString());
                            if (playerState != null)
                            {
                                var abandoned = playerState.ActiveQuests.FirstOrDefault(q => q.InstanceId == instanceId);
                                if (abandoned != null)
                                {
                                    playerState.ActiveQuests.Remove(abandoned);
                                    Debug.LogError($"[QUEST-ABANDON] Removed {abandoned.QuestId} from server-side active list");
                                }
                                else
                                {
                                    Debug.LogError($"[QUEST-ABANDON] WARNING: instanceId 0x{instanceId:X8} not found in active list");
                                }
                            }

                            // Tell the client to remove the quest from its local quest log.
                            QuestManager.Instance.SendRemovePacket(conn, instanceId);

                            // Persist so the abandon survives zone transitions.
                            SavePlayerQuests(conn);

                            Debug.LogError($"[QUEST-ABANDON] complete");
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[QUEST-ABANDON] EXCEPTION: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                        }
                    }
                    else
                    {
                        Debug.LogError($"[COMPONENT] 0x03 from componentId={componentId} - not quest");
                        HandleCancelAction(conn, reader, componentId);
                    }
                }
                else if (subMessage == 0x05)
                {
                    if (componentId == conn.QuestManagerId)
                    {
                        // 0x05 is sent for BOTH Accept AND Complete Quest clicks
                        if (conn.PendingTurnInInstanceId != 0)
                        {
                            // TURN-IN: client clicked "Complete Quest" — complete() sends submsg 0x05
                            Debug.LogError($"[QUEST-TURNIN] 0x05 = COMPLETE QUEST! InstanceId={conn.PendingTurnInInstanceId}");
                            uint instanceId = conn.PendingTurnInInstanceId;
                            conn.PendingTurnInInstanceId = 0;

                            var completingQuest = QuestManager.Instance.GetQuestByInstanceId(conn.ConnId.ToString(), instanceId);
                            QuestData completedQuestData = null;
                            if (completingQuest != null)
                                completedQuestData = DatabaseLoader.Quests
                                    .FirstOrDefault(q => q.id.Equals(completingQuest.QuestId, StringComparison.OrdinalIgnoreCase));

                            RemoveQuestItemsFromInventory(conn, completingQuest);
                            QuestManager.Instance.HandleTurnInConfirmed(conn, instanceId);
                            SavePlayerQuests(conn);

                            // Apply quest rewards using GC formulas (gold/XP/King's Coins/XP buff)
                            ApplyQuestRewards(conn, completedQuestData);
                        }
                        else
                        {
                            // ACCEPT: client clicked "Accept" on quest offer
                            Debug.LogError($"[QUEST] 📨 Received submessage 0x05 (Accept Button)");
                            uint npcEntityId = reader.ReadUInt32();
                            byte gcTypeIndicator = reader.ReadByte();
                            uint questHash = reader.ReadUInt32();
                            Debug.LogError($"[QUEST-ACCEPT] NPC={npcEntityId}, Hash=0x{questHash:X8}");
                            QuestManager.Instance.HandleAcceptConfirmed(conn, npcEntityId, questHash);
                            // Give onAcceptItem if quest has one
                            if (DatabaseLoader.QuestsByHash.TryGetValue(questHash, out var acceptedQuest1) && !string.IsNullOrEmpty(acceptedQuest1.onAcceptItem))
                                GiveOnAcceptItem(conn, acceptedQuest1.onAcceptItem);
                            TryAutoCompleteWishingWellQuest(conn, questHash);
                        }
                    }
                }
                else if (subMessage == 0x06)
                {
                    if (componentId == conn.QuestManagerId)
                    {
                        uint npcEntityId = reader.ReadUInt32();
                        byte gcTypeIndicator = reader.ReadByte();
                        uint questHash = reader.ReadUInt32();
                        Debug.LogError($"[QUEST] NPC={npcEntityId}, Hash=0x{questHash:X8}, Pending=0x{conn.PendingQuestHash:X8}");

                        if (conn.PendingQuestHash == questHash)
                        {
                            // Second 0x06 with same hash = Accept clicked
                            Debug.LogError($"[QUEST] ✅ ACCEPTING QUEST");
                            conn.PendingQuestHash = 0;
                            QuestManager.Instance.HandleAcceptConfirmed(conn, npcEntityId, questHash);
                            if (DatabaseLoader.QuestsByHash.TryGetValue(questHash, out var acceptedQuest2) && !string.IsNullOrEmpty(acceptedQuest2.onAcceptItem))
                                GiveOnAcceptItem(conn, acceptedQuest2.onAcceptItem);
                            TryAutoCompleteWishingWellQuest(conn, questHash);
                        }
                        else
                        {
                            // First 0x06 = show dialog
                            Debug.LogError($"[QUEST] 📋 SHOWING DIALOG");
                            conn.PendingQuestHash = questHash;
                            conn.PendingQuestNpcEntityId = npcEntityId;
                            QuestManager.Instance.SendQueryResponse(conn, questHash, npcEntityId);
                        }
                    }
                }

                // subMessage 0x04 = VIEW QUEST from quest log (just clicking, not abandoning)
                // subMessage 0x04 = TURN-IN or VIEW quest (clicking yellow ? OR viewing from quest log)
                else if (subMessage == 0x04)
                {
                    if (componentId == conn.QuestManagerId)
                    {
                        uint instanceId = reader.ReadUInt32();
                        Debug.LogError($"[QUEST-0x04] instanceId={instanceId} - checking if turn-in...");

                        var playerState = QuestManager.Instance.GetPlayerState(conn.ConnId.ToString());
                        var activeQuest = playerState?.ActiveQuests.FirstOrDefault(q => q.InstanceId == instanceId);

                        if (activeQuest != null)
                        {
                            var objectives = activeQuest.Objectives ?? new System.Collections.Generic.List<DungeonRunners.Managers.QuestProgress>();
                            bool allComplete = objectives.Count > 0 && objectives.All(o => o.IsComplete);
                            Debug.LogError($"[QUEST-0x04] Found quest {activeQuest.QuestId}, allComplete={allComplete}");

                            if (allComplete)
                            {
                                Debug.LogError($"[QUEST-0x04] 🎯 SENDING TURN-IN DIALOG for instanceId={instanceId}");
                                QuestManager.Instance.SendTurnInDialog(conn, instanceId);
                            }
                            else
                            {
                                Debug.LogError($"[QUEST-0x04] Quest not complete yet, just viewing");
                            }
                        }
                        else
                        {
                            Debug.LogError($"[QUEST-0x04] No active quest with instanceId={instanceId}");
                        }
                    }
                }
                else if (subMessage == 0x0A)
                {
                    if (componentId == conn.QuestManagerId)
                    {
                        Debug.LogError($"[QUEST-0x0A] TOWN PORTAL USE from obelisk!");
                        if (conn.HasSavedTownPortal)
                        {
                            Debug.LogError($"[QUEST-0x0A] Teleporting to {conn.TownPortalZoneName} at ({conn.TownPortalPosX:F1}, {conn.TownPortalPosY:F1})");
                            ChangeZoneToPosition(conn, conn.TownPortalZoneName,
                                conn.TownPortalPosX, conn.TownPortalPosY, conn.TownPortalPosZ);
                            // Don't clear HasSavedTownPortal here — SpawnReturnTownPortal needs it on zone-in
                        }
                        else
                        {
                            Debug.LogError($"[QUEST-0x0A] No saved town portal!");
                        }
                    }
                }
                else if (subMessage == 0x01)
                {
                    // QUEST ACCEPT: 0x01 with empty payload from the session QuestManager component.
                    if (componentId == conn.QuestManagerId && reader.Remaining == 0)
                    {
                        if (conn.PendingQuestHash != 0)
                        {
                            Debug.LogError($"[QUEST-ACCEPT] 0x01 empty = ACCEPT! Hash=0x{conn.PendingQuestHash:X8}");
                            uint questHash = conn.PendingQuestHash;
                            uint npcEntityId = conn.PendingQuestNpcEntityId;
                            conn.PendingQuestHash = 0;
                            conn.PendingQuestNpcEntityId = 0;
                            QuestManager.Instance.HandleAcceptConfirmed(conn, npcEntityId, questHash);
                            if (DatabaseLoader.QuestsByHash.TryGetValue(questHash, out var acceptedQuest3) && !string.IsNullOrEmpty(acceptedQuest3.onAcceptItem))
                                GiveOnAcceptItem(conn, acceptedQuest3.onAcceptItem);
                            TryAutoCompleteWishingWellQuest(conn, questHash);
                            SavePlayerQuests(conn);
                        }
                        else if (conn.PendingTurnInInstanceId != 0)
                        {
                            Debug.LogError($"[QUEST-TURNIN] 0x01 empty = COMPLETE! InstanceId={conn.PendingTurnInInstanceId}");
                            uint instanceId = conn.PendingTurnInInstanceId;
                            conn.PendingTurnInInstanceId = 0;

                            // Get quest data BEFORE completing so we can award rewards
                            var completingQuest = QuestManager.Instance.GetQuestByInstanceId(conn.ConnId.ToString(), instanceId);
                            QuestData completedQuestData = null;
                            if (completingQuest != null)
                                completedQuestData = DatabaseLoader.Quests
                                    .FirstOrDefault(q => q.id.Equals(completingQuest.QuestId, StringComparison.OrdinalIgnoreCase));

                            RemoveQuestItemsFromInventory(conn, completingQuest);
                            QuestManager.Instance.HandleTurnInConfirmed(conn, instanceId);
                            SavePlayerQuests(conn);

                            // Apply quest rewards using GC formulas (gold/XP/King's Coins/XP buff)
                            ApplyQuestRewards(conn, completedQuestData);
                        }
                        else
                        {
                            Debug.LogError($"[QUEST-0x01] Empty but no pending quest - viewing quest");
                            conn.ViewingQuestInstanceId = 1;
                        }
                        return;
                    }
                    Debug.LogError($"[SUBMSG-0x01] PASSED QUEST CHECK - about to read action! remaining={reader.Remaining}");

                    // 🔥 ACTIVATE ACTION (NPC or Item Pickup)
                    byte responseId = reader.ReadByte();
                    Debug.LogError($"[ACTION-READ] responseId={responseId}, remaining={reader.Remaining}");

                    byte actionType = reader.ReadByte();
                    Debug.LogError($"[ACTION-READ] actionType=0x{actionType:X2}, remaining={reader.Remaining}");

                    // ── 0x52 SELF-CAST SPELLS (Gaseous Blast, buffs) ──
                    // 0x52 self-cast only has 2 bytes remaining: sessionID(1) + slotID(1)
                    // Must intercept BEFORE the uint16 read which would underflow/throw
                    // Checkpoint 0x52 has more bytes (targetEntityID + gcType string) so falls through
                    if (actionType == 0x52 && reader.Remaining <= 2)
                    {
                        byte spellSessionID = reader.Remaining >= 1 ? reader.ReadByte() : (byte)0;
                        byte slotID = reader.Remaining >= 1 ? reader.ReadByte() : (byte)0;
                        Debug.LogError($"[SPELL-0x52] ═══ SELF-CAST! sessionID={spellSessionID}, slotID={slotID}, componentId={componentId}, remaining={reader.Remaining}");
                        if (conn.HasActiveUseTarget)
                            ClearUseTargetAndReleaseControl(conn, "ACTION-0x52", componentId);

                        // Response: Rainbow format via MessageQueue (same delivery as 0x51)
                        var msg52 = new LEWriter();
                        msg52.WriteByte(0x35);              // ComponentUpdate
                        msg52.WriteUInt16(componentId);
                        msg52.WriteByte(0x01);              // ActionResponse
                        msg52.WriteByte(responseId);
                        msg52.WriteByte(0x52);              // BehaviourActionUse
                        msg52.WriteByte(spellSessionID);    // sessionID
                        msg52.WriteByte(slotID);            // echo slotID back
                        if (!TryWriteEntitySynchForComponent(conn, msg52, componentId, 0x01, SyncContext.PlayerActionResponse, "PlayerActionResponse", false))
                        {
                            Debug.LogError($"[SPELL-0x52] failed ActionResponse sync: component={componentId} slot={slotID}");
                            return;
                        }
                        ClearZoneSpawnInvulnerability(conn, $"ACTION-0x{actionType:X2}");
                        conn.MessageQueue.Enqueue(msg52.ToArray());
                        Debug.LogError($"[SPELL-0x52] <<< Sent ActionResponse via MessageQueue");

                        // MULTIPLAYER: Relay self-cast animation to other players
                        BroadcastSelfCast(conn, responseId, spellSessionID, slotID);

                        // Server-side: mana cost + AoE damage to nearby monsters
                        if (componentId < 50000)
                        {
                            PlayerState state52 = GetPlayerState(conn.ConnId.ToString());
                            if (state52 != null)
                            {
                                HandleSelfCastSpell(conn, state52, slotID, componentId);
                            }
                        }
                        // All bytes consumed — no sync suffix to read
                        return;
                    }

                    byte sessionID = reader.ReadByte();
                    Debug.LogError($"[ACTION-READ] sessionID={sessionID}, remaining={reader.Remaining}");

                    ushort targetEntityID = reader.ReadUInt16();
                    Debug.LogError($"[ACTION-READ] targetEntityID={targetEntityID}, remaining={reader.Remaining}");

                    // 🔥 LOG ALL ACTION TYPES TO SEE WHAT CLIENT SENDS
                    Debug.LogError($"[ACTION] ActionType=0x{actionType:X2}, ResponseId={responseId}, SessionID={sessionID}, Target={targetEntityID}");
                    if (actionType != 0x50 && conn.HasActiveUseTarget)
                        ClearUseTargetAndReleaseControl(conn, $"ACTION-0x{actionType:X2}", componentId);

                    if (actionType == 0x06) // BehaviourActionActivate (left-click)
                                            // ... rest of your existing code continues here
                    {
                        // MULTIPLAYER: Broadcast walk-to-target for other players.
                        // Do NOT update conn.PlayerPosX/Y here — that field is only
                        // updated from actual movement packets (HandleClientMove).
                        // Overwriting it with the target's position would cause the
                        // tick to echo the wrong position back to the player → screen snap.
                        bool broadcastSent = false;
                        if (_npcPositions.TryGetValue(targetEntityID, out var npcPos))
                        {
                            BroadcastWalkToPosition(conn, npcPos.X, npcPos.Y);
                            broadcastSent = true;
                        }
                        else if (_portalEntities.TryGetValue(targetEntityID, out var portal2))
                        {
                            BroadcastWalkToPosition(conn, portal2.PosX, portal2.PosY);
                            broadcastSent = true;
                        }
                        else if (_checkpointEntities.TryGetValue(targetEntityID, out var cp))
                        {
                            BroadcastWalkToPosition(conn, cp.PosX, cp.PosY);
                            broadcastSent = true;
                        }

                        // Fallback for ANY unknown entity (obelisks, quest objects, etc.)
                        // Use tracked entity position if available
                        if (!broadcastSent && targetEntityID != 0)
                        {
                            if (_allEntityPositions.TryGetValue(targetEntityID, out var entityPos))
                            {
                                Debug.LogError($"[MP-WALK] Entity {targetEntityID} found in position tracker at ({entityPos.X}, {entityPos.Y})");
                                BroadcastWalkToPosition(conn, entityPos.X, entityPos.Y);
                            }
                            else
                            {
                                // Unknown entity — broadcast current player position as walk start
                                Debug.LogError($"[MP-WALK] Unknown entity {targetEntityID} — broadcasting current pos + enabling force-relay");
                                BroadcastWalkToPosition(conn, conn.PlayerPosX, conn.PlayerPosY);
                                _forceRelayUntil[conn.ConnId] = Time.time + 3.0f;
                            }
                        }

                        // Dropped item pickup -> straight to inventory bag.
                        // The DR client sends the same actionType 0x06 with no modifier flag
                        // regardless of which mouse button or modifier key is held (verified
                        // from server packet diagnostics: every click had identical bytes,
                        // remaining=0, no shift state in the wire format). So we just route
                        // every dropped-item click through the auto-bag handler. If the bag
                        // is full it falls back to HandleItemPickup (cursor) automatically.
                        if (IsDroppedItem(targetEntityID))
                        {
                            Debug.LogError($"[PICKUP] Player clicked dropped item, auto-bagging. Target={targetEntityID}");
                            HandleItemRightClickPickup(conn, componentId, targetEntityID, responseId, sessionID);
                        }
                        // Check if this is a portal
                        else if (IsPortal(targetEntityID, out var portal))
                        {
                            Debug.LogError($"[PORTAL] Player clicked portal! Target={targetEntityID} → {portal.TargetZone}");
                            HandlePortalActivation(conn, componentId, targetEntityID, responseId, sessionID, portal);
                        }
                        // Check if this is a treasure chest
                        else if (IsChest(targetEntityID, out var chestData))
                        {
                            Debug.LogError($"[CHEST] Player clicked chest! Target={targetEntityID} ({chestData.Label})");
                            HandleChestActivation(conn, componentId, targetEntityID, responseId, sessionID, chestData);
                        }
                        // Check if this is a checkpoint
                        else if (IsCheckpoint(targetEntityID, out var checkpoint))
                        {
                            Debug.LogError($"[CHECKPOINT] Player clicked checkpoint! Target={targetEntityID}");
                            HandleCheckpointActivation(conn, componentId, targetEntityID, responseId, sessionID, checkpoint);
                        }
                        // Check if this is a world entity (teleporter, shrine, gate, etc)
                        else if (WorldEntitySpawner.Instance.TryGetEntity(targetEntityID, out var weData))
                        {
                            Debug.LogError($"[WORLD-ENTITY] Player clicked {weData.EntityType}: {weData.Label} (id=0x{targetEntityID:X4})");
                            if (weData.IsTeleporter)
                            {
                                HandleTeleporterActivation(conn, componentId, targetEntityID, responseId, sessionID, weData);
                            }
                            else if (weData.IsGate)
                            {
                                // Boss gates only open on boss kill — block manual click
                                // ACK the behavior action (unfreezes player WASD)
                                var ackMsg = new LEWriter();
                                ackMsg.WriteByte(0x35);
                                ackMsg.WriteUInt16(componentId);
                                ackMsg.WriteByte(0x01);
                                ackMsg.WriteByte(responseId);
                                ackMsg.WriteByte(0x06);
                                ackMsg.WriteByte(sessionID);
                                ackMsg.WriteUInt16(targetEntityID);
                                WritePlayerEntitySynch(conn, ackMsg);
                                conn.MessageQueue.Enqueue(ackMsg.ToArray());

                                SendSystemMessage(conn, "The gate is sealed. Defeat the boss to open it.");
                                Debug.LogError($"[WORLD-ENTITY] Gate click BLOCKED — boss must be killed first: {weData.Label}");
                            }
                            else
                            {
                                // ACK the behavior action first (unfreezes player WASD)
                                var ackMsg = new LEWriter();
                                ackMsg.WriteByte(0x35);
                                ackMsg.WriteUInt16(componentId);
                                ackMsg.WriteByte(0x01);
                                ackMsg.WriteByte(responseId);
                                ackMsg.WriteByte(0x06);
                                ackMsg.WriteByte(sessionID);
                                ackMsg.WriteUInt16(targetEntityID);
                                WritePlayerEntitySynch(conn, ackMsg);
                                conn.MessageQueue.Enqueue(ackMsg.ToArray());

                                // NCI::processUpdate type 0x0A = activate
                                var nciMsg = new LEWriter();
                                nciMsg.WriteByte(0x03);               // processEntityUpdate
                                nciMsg.WriteUInt16(targetEntityID);    // entityId
                                nciMsg.WriteByte(0x0A);               // NCI activate
                                nciMsg.WriteUInt32(0x00000000);       // activation data (counter)
                                WriteNonCombatInteractiveEntitySynchInfo(nciMsg, weData.GCType);
                                conn.MessageQueue.Enqueue(nciMsg.ToArray());
                                Debug.LogError($"[WORLD-ENTITY] Sent NCI activate (0x03/0x0A) for {weData.EntityType}: {weData.Label}");

                                // ═══ PORTRAIT QUEST HOOK — Q02_a2 "Suspicious portrait" ═══
                                // The Rattle Tooth quest Q02_a2 has two objectives:
                                //   MainObjective1: kill Rattle Tooth (works)
                                //   MainObjective2: "Suspicious portrait" (item type,
                                //     target=QuestItemPAL2.D00_Q02_a2_01) — the
                                //     tracker shows it but clicking the portrait
                                //     never ticked it because the generic NCI
                                //     click handler only sends the activate
                                //     response and returns.
                                //
                                // The portrait entity in dungeon00_level03_boss is
                                // registered in zone_world_entities as:
                                //   name='NCIPortrait'
                                //   gc_type='world.dungeon00.npc.NCIPortrait'
                                //   entity_type='npc'
                                //
                                // Fix: detect the portrait by gc_type suffix and
                                // call OnItemPickedUp with the quest-item target.
                                // OnItemPickedUp runs through QuestManager's normal
                                // item-objective match (type='item' + target match)
                                // and fires SendProgressPacket, which is what
                                // updates the on-screen tracker. No DB changes,
                                // no .gc changes, no client modifications.
                                try
                                {
                                    if (!string.IsNullOrEmpty(weData.GCType) &&
                                        weData.GCType.IndexOf("NCIPortrait", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        Debug.LogError($"[QUEST] Portrait clicked ({weData.GCType}) — ticking Q02_a2 'Suspicious portrait' objective");
                                        var _portraitUpdates = QuestManager.Instance.OnItemPickedUp(conn, "QuestItemPAL2.D00_Q02_a2_01");
                                        if (_portraitUpdates != null && _portraitUpdates.Count > 0)
                                        {
                                            foreach (var _pu in _portraitUpdates)
                                                Debug.LogError($"[QUEST] Portrait tick: quest={_pu.QuestId} objective={_pu.ObjectiveName} now {_pu.Current}/{_pu.Required}");
                                        }
                                        else
                                        {
                                            Debug.LogError($"[QUEST] Portrait click produced no objective updates — either Q02_a2 not active or objective already complete");
                                        }
                                    }
                                }
                                catch (Exception _portraitEx)
                                {
                                    Debug.LogError($"[QUEST] Portrait quest hook failed (non-fatal): {_portraitEx.Message}");
                                }

                                // ═══ HERMIT LOCKBOX QUEST HOOK — Q03_a1 "The Hermit's Hoop" ═══
                                // When player clicks Entity_HermitLockbox, drop HermitRing quest item.
                                // GC: ActivateDropTrigger → EntityType=world.dungeon00.data.Entity_HermitLockbox,
                                //     Chance=100, Item=QuestItemPAL2.D00_Q03_a1_01
                                try
                                {
                                    if (!string.IsNullOrEmpty(weData.Name) &&
                                        weData.Name.Equals("HermitLockbox", StringComparison.OrdinalIgnoreCase))
                                    {
                                        Debug.LogError($"[QUEST] Hermit Lockbox clicked ({weData.GCType}) — dropping HermitRing on ground");
                                        uint hermitChanceRaw = NativeRandomStreams.GenerateGlobalStatic(
                                            "ActivateDropTrigger::doEvent.chance",
                                            "world/dungeon00/quest/Q03_a1:Entity_HermitLockbox");
                                        uint hermitChanceRoll = (hermitChanceRaw % 100u) + 1u;
                                        Debug.LogError($"[ACTIVATEDROP-NATIVE] entity={weData.Name} gc={weData.GCType} item=QuestItemPAL2.D00_Q03_a1_01 chance=100 roll={hermitChanceRoll} pass={(hermitChanceRoll <= 100u)} native=ActivateDropTrigger::doEvent@0x005CB460");
                                        if (hermitChanceRoll > 100u)
                                            return;
                                        var hermitItem = new GCObject
                                        {
                                            GCClass = "QuestItemPAL2.D00_Q03_a1_01",
                                            NativeClass = "Item",
                                            StoredLevel = 1
                                        };
                                        ConsumeNativeItemAddToWorldHeading("activatedrop:HermitLockbox:QuestItemPAL2.D00_Q03_a1_01");
                                        var hermitPlacement = ResolveNativeItemDropPlacement(
                                            conn,
                                            !string.IsNullOrWhiteSpace(weData.Zone) ? weData.Zone : conn.CurrentZoneName,
                                            conn.InstanceId,
                                            weData.PosX,
                                            weData.PosY,
                                            weData.PosZ,
                                            weData.Heading,
                                            "activatedrop:HermitLockbox:QuestItemPAL2.D00_Q03_a1_01");
                                        float hpx = hermitPlacement.X;
                                        float hpy = hermitPlacement.Y;
                                        ushort hermitEid = GetNextLootEntityId();
                                        TrackDroppedItem(hermitEid, hermitItem, conn, 1, hpx, hpy, hermitPlacement.Z, 1);
                                        if (_droppedItems.TryGetValue(hermitEid, out var hermitInfo))
                                            hermitInfo.IsQuestItem = true;
                                        SendDroppedItemSpawnPacket(conn, hermitEid, _droppedItems[hermitEid]);
                                    }
                                }
                                catch (Exception _hermitEx)
                                {
                                    Debug.LogError($"[QUEST] Hermit Lockbox hook failed (non-fatal): {_hermitEx.Message}");
                                }

                                // For chests, generate loot
                                if (weData.EntityType == "chest" || weData.EntityType == "boss_chest")
                                {
                                    PlayerState chestPS = GetPlayerState(conn.ConnId.ToString());
                                    int chestLevel = chestPS?.Level ?? 1;
                                    var chestDrops = new List<LootDrop>();
                                    foreach (var (generator, count, slot) in weData.GetNativeChestGenerators("TreasureChestIG", 2))
                                    {
                                        var slotDrops = LootManager.Instance.GenerateChestLoot(generator, count, chestLevel);
                                        chestDrops.AddRange(slotDrops);
                                        Debug.LogError($"[CHEST-WE] {weData.Label}: slot={slot} generator={generator} count={count} drops={slotDrops.Count} native=NCI-chest-generator-slots-1-5");
                                    }
                                    Debug.LogError($"[CHEST-WE] {weData.Label}: totalDrops={chestDrops.Count}");
                                    foreach (var drop in chestDrops)
                                    {
                                        if (drop.IsGold)
                                        {
                                            Debug.LogError($"[CHEST-WE] +{drop.GoldAmount} gold from {weData.Label}");
                                        }
                                        else
                                        {
                                            if (string.IsNullOrEmpty(drop.GCType)) { Debug.LogError("[CHEST-WE] ⚠️ Skipping null GCType"); continue; }
                                            string _weNative = ResolveAuthoredItemNativeClass(drop.GCType);
                                            var item = new GCObject { GCClass = drop.GCType, NativeClass = _weNative, PresetScaleMod = drop.ScaleMod, StoredRarity = (int)drop.Rarity, StoredLevel = drop.ItemLevel };
                                            var wePlacement = ResolveNativeItemDropPlacement(
                                                conn,
                                                !string.IsNullOrWhiteSpace(weData.Zone) ? weData.Zone : conn.CurrentZoneName,
                                                conn.InstanceId,
                                                weData.PosX,
                                                weData.PosY,
                                                weData.PosZ,
                                                weData.Heading,
                                                $"worldentity-chest:{weData.Label}:{drop.GCType}");
                                            float lpx = wePlacement.X;
                                            float lpy = wePlacement.Y;
                                            ushort lootId = GetNextLootEntityId();
                                            TrackDroppedItem(lootId, item, conn, 1, lpx, lpy, wePlacement.Z, chestLevel);
                                            SendDroppedItemSpawnPacket(conn, lootId, _droppedItems[lootId]);
                                            Debug.LogError($"[CHEST-WE] ★ {drop.Label} ({drop.Rarity}) from {weData.Label}");
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            Debug.LogError($"[NPC] Player clicked NPC! Target={targetEntityID}");
                            HandleNPCClick(conn, componentId, targetEntityID, responseId, sessionID);
                        }
                    }
                    else if (actionType == 0x52) // BehaviourActionUse (checkpoint teleport)
                    {
                        Debug.LogError($"[ACTION] 🎯 CHECKPOINT USE (0x52)! Target={targetEntityID}");
                        HandleCheckpointUse(conn, componentId, responseId, sessionID, reader);
                    }
                    else if (actionType == 0xA0) // BehaviourActionRetrieveItem - never observed in logs, kept as no-op for safety
                    {
                        Debug.LogError($"[ACTION] 0xA0 received (unexpected) target={targetEntityID}");
                    }
                    else if (actionType == 0x50) // BehaviourActionUseTarget (melee/Lightning)
                    {
                        // UseTarget has DIFFERENT format than Activate!
                        byte manipulatorId = sessionID;
                        byte useFlags = (byte)(targetEntityID & 0xFF);
                        byte targetIdLow = (byte)((targetEntityID >> 8) & 0xFF);
                        byte targetIdHigh = reader.ReadByte();
                        ushort actualTargetId = (ushort)(targetIdLow | (targetIdHigh << 8));
                        var gnomeManager = BlingGnomeManager.Instance;
                        bool isBlingGnomeTarget = gnomeManager.TryResolveGnomeTarget(conn, actualTargetId,
                            out uint gnomeEntityId, out ushort gnomeBehaviorId, out bool gnomeBootstrapped, out string gnomeTargetReason);
                        Combat.Monster actionTargetMonster = isBlingGnomeTarget ? null : Combat.CombatManager.Instance.FindMonsterForTarget(actualTargetId, conn.PlayerPosX, conn.PlayerPosY, GetInstanceZoneKey(conn));
                        TryConsumeClientSyncSuffix(conn, reader, "ACTION-0x50-SYNC", actionTargetMonster, out bool acceptedActionMonsterHP);

                        Debug.LogError($"[ATTACK] 0x50: componentId={componentId}, manipulatorId={manipulatorId}, flags={useFlags}, targetId={actualTargetId}, gnome={gnomeEntityId}, gnomeBehavior={gnomeBehaviorId}, gnomeBootstrapped={gnomeBootstrapped}, gnomeMatch={gnomeTargetReason}");
                        if (isBlingGnomeTarget)
                        {
                            if (componentId != 0)
                                conn.UnitBehaviorId = componentId;

                            var msg = new LEWriter();
                            msg.WriteByte(0x07);
                            msg.WriteByte(0x35);
                            msg.WriteUInt16(componentId);
                            msg.WriteByte(0x01);
                            msg.WriteByte(responseId);
                            msg.WriteByte(0x50);
                            msg.WriteByte(manipulatorId);
                            msg.WriteByte(useFlags);
                            msg.WriteUInt16(actualTargetId);
                            const SyncContext actionResponseContext = SyncContext.PlayerActionResponse;
                            const string actionResponseSyncTag = "PlayerActionResponse";
                            if (!TryWriteEntitySynchForComponent(conn, msg, componentId, 0x01, actionResponseContext, actionResponseSyncTag, true))
                            {
                                Debug.LogError($"[GNOME-ACTIVATE] failed ActionResponse sync: component={componentId} target={actualTargetId} flags={useFlags}; sending release fallback");
                                ClearUseTargetAndReleaseControl(conn, "GNOME-ACTIVATE-sync-failed", componentId);
                                return;
                            }
                            msg.WriteByte(0x06);
                            bool actionResponseSent = SendCompressedA(conn, 0x01, 0x0F, msg.ToArray(), actionResponseContext, actionResponseSyncTag);

                            bool activated = false;
                            try
                            {
                                activated = gnomeManager.ActivateGnome(conn, actualTargetId);
                                Debug.LogError($"[GNOME-ACTIVATE] UseTarget target={actualTargetId} manip={manipulatorId} flags={useFlags} component={componentId} sent={actionResponseSent} activated={activated}");
                            }
                            catch (Exception ex)
                            {
                                Debug.LogError($"[GNOME-ACTIVATE] exception target={actualTargetId} component={componentId}: {ex}");
                            }

                            if (!actionResponseSent)
                            {
                                ClearUseTargetAndReleaseControl(conn, "GNOME-ACTIVATE-send-failed", componentId);
                                return;
                            }

                            if (!activated)
                                Debug.LogError($"[GNOME-ACTIVATE] UseTarget target={actualTargetId} did not start conversion");
                        }
                        else if (componentId >= 50000 && componentId < 60000)
                        {
                            Debug.LogError($"[ATTACK] >>> MONSTER->PLAYER! Monster component={componentId}, target player={actualTargetId}");
                            var attackingMonster = Combat.CombatManager.Instance.GetMonsterByComponent(componentId);
                            if (attackingMonster != null)
                                Debug.LogError($"[ATTACK] Monster {attackingMonster.Name} attacking player {actualTargetId}");
                            else
                                Debug.LogError($"[ATTACK] WARNING: No monster found for component {componentId}");
                        }
                        else
                        {
                            if (componentId != 0)
                                conn.UnitBehaviorId = componentId;
                            Debug.LogError($"[ATTACK] >>> PLAYER UseTarget! component={componentId}, target={actualTargetId}, manip={manipulatorId}, flags={useFlags}");
                            bool handled = false;
                            try
                            {
                                var monster = actionTargetMonster ?? Combat.CombatManager.Instance.FindMonsterForTarget(actualTargetId, conn.PlayerPosX, conn.PlayerPosY, GetInstanceZoneKey(conn));
                                if (monster != null)
                                {
                                    HandlePlayerAttackMonster(conn, componentId, responseId, manipulatorId, useFlags, actualTargetId, monster, acceptedActionMonsterHP);
                                    handled = true;
                                }
                                else
                                {
                                    Debug.LogError($"[ATTACK] No monster for target {actualTargetId}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.LogError($"[ATTACK] EXCEPTION: {ex.Message}\n{ex.StackTrace}");
                            }

                            if (!handled)
                            {
                                var unresolvedMonster = Combat.CombatManager.Instance.GetMonster(actualTargetId)
                                                       ?? Combat.CombatManager.Instance.GetMonsterByComponent(actualTargetId);
                                if (unresolvedMonster != null)
                                {
                                    uint unresolvedHP = Combat.CombatManager.Instance.PeekMonsterCurrentHPWire(unresolvedMonster);
                                    bool deadUseTarget = !unresolvedMonster.IsAlive || unresolvedMonster.CurrentHPWire == 0 || unresolvedHP == 0 || _finalizedMonsterKills.Contains((uint)actualTargetId);
                                    Debug.LogError($"[ATTACK] Unresolved UseTarget target={actualTargetId} alive={unresolvedMonster.IsAlive} hp={unresolvedMonster.CurrentHPWire} resolvedHP={unresolvedHP} dead={deadUseTarget}");
                                    if (deadUseTarget)
                                    {
                                        bool sent = SendUseTargetActionResponse(conn, componentId, responseId, manipulatorId, useFlags, actualTargetId, SyncContext.PlayerActionResponse, "PlayerActionResponse", "dead-target");
                                        if (!sent)
                                            Debug.LogError($"[ATTACK] failed dead-target ActionResponse target={actualTargetId} component={componentId} flags={useFlags}");
                                        ClearUseTargetAndReleaseControl(conn, "ATTACK-dead-target", componentId, true, false);
                                    }
                                    else
                                    {
                                        var msg = new LEWriter();
                                        msg.WriteByte(0x07);
                                        msg.WriteByte(0x35);
                                        msg.WriteUInt16(componentId);
                                        msg.WriteByte(0x01);
                                        msg.WriteByte(responseId);
                                        msg.WriteByte(0x50);
                                        msg.WriteByte(manipulatorId);
                                        msg.WriteByte(useFlags);
                                        msg.WriteUInt16(actualTargetId);
                                        WritePlayerEntitySynch(conn, msg);
                                        msg.WriteByte(0x06);
                                        SendCompressedA(conn, 0x01, 0x0F, msg.ToArray());
                                        Debug.LogError($"[ATTACK] <<< Sent unresolved live-target ActionResponse target={actualTargetId} alive={unresolvedMonster.IsAlive} hp={unresolvedMonster.CurrentHPWire}");
                                        ClearUseTargetAndReleaseControl(conn, "ATTACK-unresolved-live-target", componentId);
                                    }
                                }
                                else
                                {
                                    if (_finalizedMonsterKills.Contains((uint)actualTargetId))
                                    {
                                        bool sent = SendUseTargetActionResponse(conn, componentId, responseId, manipulatorId, useFlags, actualTargetId, SyncContext.PlayerActionResponse, "PlayerActionResponse", "finalized-dead-target");
                                        if (!sent)
                                            Debug.LogError($"[ATTACK] failed finalized dead-target ActionResponse target={actualTargetId} component={componentId} flags={useFlags}");
                                        ClearUseTargetAndReleaseControl(conn, "ATTACK-finalized-target-missing", componentId, true, false);
                                    }
                                    else
                                    {
                                        var msg = new LEWriter();
                                        msg.WriteByte(0x07);
                                        msg.WriteByte(0x35);
                                        msg.WriteUInt16(componentId);
                                        msg.WriteByte(0x01);
                                        msg.WriteByte(responseId);
                                        msg.WriteByte(0x50);
                                        msg.WriteByte(manipulatorId);
                                        msg.WriteByte(useFlags);
                                        msg.WriteUInt16(actualTargetId);
                                        WritePlayerEntitySynch(conn, msg);
                                        msg.WriteByte(0x06);
                                        SendCompressedA(conn, 0x01, 0x0F, msg.ToArray());
                                        Debug.LogError($"[ATTACK] <<< Sent fallback ActionResponse target={actualTargetId}");
                                        ClearUseTargetAndReleaseControl(conn, "ATTACK-fallback-target-missing", componentId);

                                        // MULTIPLAYER: Still broadcast swing animation even when monster not found
                                        BroadcastMeleeAttack(conn, responseId, manipulatorId, useFlags);
                                    }
                                }
                            }
                        }
                    }
                    else if (actionType == 0x51) // BehaviourActionUsePosition (Fire Bolt, etc.)
                    {
                        // Rainbow server format: after header, reads actionID(1) + posX(4) + posY(4) + posZ(4)
                        // Our header already consumed targetEntityID (2 bytes of that 13).
                        // Read remaining 11 bytes of position data.
                        byte manipulatorId = sessionID;
                        byte actionID = (byte)(targetEntityID & 0xFF);
                        byte posXByte0 = (byte)((targetEntityID >> 8) & 0xFF);
                        byte[] posRemain = reader.Remaining >= 11 ? reader.ReadBytes(11) : reader.ReadBytes(reader.Remaining);

                        // Reconstruct position from bytes
                        int posX = 0, posY = 0, posZ = 0;
                        if (posRemain.Length >= 11)
                        {
                            posX = posXByte0 | (posRemain[0] << 8) | (posRemain[1] << 16) | (posRemain[2] << 24);
                            posY = posRemain[3] | (posRemain[4] << 8) | (posRemain[5] << 16) | (posRemain[6] << 24);
                            posZ = posRemain[7] | (posRemain[8] << 8) | (posRemain[9] << 16) | (posRemain[10] << 24);
                        }
                        float fPosX = posX / 256f;
                        float fPosY = posY / 256f;
                        float fPosZ = posZ / 256f;
                        Debug.LogError($"[SPELL-0x51] UsePosition: manip={manipulatorId} actionID={actionID} pos=({fPosX:F1},{fPosY:F1},{fPosZ:F1})");

                        PlayerState state = componentId < 50000 ? GetPlayerState(conn.ConnId.ToString()) : null;
                        var resolvedSpell = state != null ? ResolveActionSpell(conn, state, actionID) : null;
                        if (resolvedSpell != null && IsActiveSkillBusy(conn, componentId, actionID, resolvedSpell, out float skillBusyRemaining))
                        {
                            Debug.LogError($"[SPELL-BUSY] UsePosition native-busy actionID={actionID} spell={resolvedSpell.DisplayName ?? resolvedSpell.SkillId ?? "UNKNOWN"} remaining={skillBusyRemaining:F3}s not-using native=ActiveSkill::isBusy@0x005394F0 timer=ActiveSkill+0x7e");
                            if (reader.Remaining >= 1)
                                TryConsumeClientSyncSuffix(conn, reader, "ACTION-0x51-BUSY-SYNC");
                            return;
                        }

                        float positionDistance = 0f;
                        float positionRange = 0f;
                        if (state != null && resolvedSpell != null &&
                            !IsSpellPositionWithinServerRange(conn, resolvedSpell, fPosX, fPosY, out positionDistance, out positionRange))
                        {
                            Debug.LogError($"[SPELL-0x51] CheckInitUse pending/outside-range: {resolvedSpell.DisplayName ?? resolvedSpell.SkillId ?? "UNKNOWN"} dist={positionDistance:F1} range={positionRange:F1} pos=({fPosX:F1},{fPosY:F1}) native=UsePosition::CheckInitUse@0x00547850 result=no-use");
                            if (reader.Remaining >= 1)
                                TryConsumeClientSyncSuffix(conn, reader, "ACTION-0x51-RANGE-SYNC");
                            return;
                        }

                        // Rainbow response format: 0x35 + cid + 0x01 + responseId + 0x51 + sessionID + actionID + pos + sync
                        // Via MessageQueue (no 0x07/0x06 wrappers)
                        var msg = new LEWriter();
                        msg.WriteByte(0x35);
                        msg.WriteUInt16(componentId);
                        msg.WriteByte(0x01);              // ActionResponse
                        msg.WriteByte(responseId);
                        msg.WriteByte(0x51);              // UsePosition (HAS sessionID per Rainbow)
                        msg.WriteByte(manipulatorId);     // sessionID
                        msg.WriteByte(actionID);          // echo actionID
                        msg.WriteUInt32((uint)posX);      // echo posX
                        msg.WriteUInt32((uint)posY);      // echo posY
                        msg.WriteUInt32((uint)posZ);      // echo posZ
                        if (!TryWriteEntitySynchForComponent(conn, msg, componentId, 0x01, SyncContext.PlayerActionResponse, "PlayerActionResponse", false))
                        {
                            Debug.LogError($"[SPELL-0x51] failed ActionResponse sync: component={componentId} actionID={actionID}");
                            return;
                        }
                        ClearZoneSpawnInvulnerability(conn, $"ACTION-0x{actionType:X2}");
                        conn.MessageQueue.Enqueue(msg.ToArray());
                        StartActiveSkillBusy(conn, componentId, actionID, resolvedSpell);
                        Debug.LogError($"[SPELL-0x51] <<< Sent ActionResponse via MessageQueue");

                        // MULTIPLAYER: Relay spell cast to other players
                        BroadcastSpellCast(conn, responseId, manipulatorId, actionID, posX, posY, posZ);

                        // Server-side damage tracking
                        if (componentId < 50000)
                        {
                            if (state != null)
                            {
                                // Try to resolve actionID as a slot ID for spell lookup
                                Debug.LogError($"[SPELL-0x51] actionID={actionID} → {resolvedSpell?.DisplayName ?? "NOT RESOLVED"} | sessionCtr={manipulatorId} pos=({fPosX:F1},{fPosY:F1})");

                                var nearest = ResolvePositionSpellTarget(conn, resolvedSpell, fPosX, fPosY, out float projectileHitDistance);
                                if (resolvedSpell != null && resolvedSpell.ProjectileSize > 0f && resolvedSpell.ProjectileSpeed > 0f)
                                {
                                    if (nearest != null && nearest.IsAlive)
                                        nearest.UseTargetCount++;
                                    float startX = conn.PlayerPosX;
                                    float startY = conn.PlayerPosY;
                                    float hitHint = nearest != null && nearest.IsAlive
                                        ? projectileHitDistance
                                        : 0f;
                                    var pending = CreatePendingSpellProjectile(conn, state, nearest, resolvedSpell, actionID, actionID, componentId, startX, startY, fPosX, fPosY, nearest == null || !nearest.IsAlive, hitHint);
                                    _pendingSpells.Enqueue(pending);
                                    Debug.LogError($"[SPELL-0x51] Queued projectile runtime spell={resolvedSpell?.DisplayName ?? resolvedSpell?.SkillId ?? "UNKNOWN"} slotId={actionID} initialTarget={(nearest != null ? nearest.Name + "#" + nearest.EntityId.ToString() : "none")} delay={pending.ProjectileDelay:F3}s hitHint={pending.ProjectileHitDistance:F2} speed={pending.ProjectileSpeed:F1} step={pending.StepDistance:F3} initPreStep={pending.InitialDistance:F3} maxDist={pending.MaxDistance:F2} seq={pending.Sequence}");
                                }
                                else
                                {
                                    if (nearest != null && nearest.IsAlive)
                                    {
                                        nearest.UseTargetCount++;
                                        float projectileDelay = ResolveProjectileImpactDelay(resolvedSpell, projectileHitDistance);
                                        float dueTime = GetNativeCombatNow() + projectileDelay;
                                        _pendingSpells.Enqueue(new PendingSpell { Conn = conn, State = state, Monster = nearest, Spell = resolvedSpell, ManipId = actionID, UseFlags = actionID, ComponentId = componentId, StartX = conn.PlayerPosX, StartY = conn.PlayerPosY, AimX = fPosX, AimY = fPosY, InstanceKey = GetInstanceZoneKey(conn), DueTime = dueTime, ProjectileHitDistance = projectileHitDistance, ProjectileDelay = projectileDelay });
                                        Debug.LogError($"[SPELL-0x51] Queued damage on {nearest.Name} slotId={actionID} delay={projectileDelay:F3}s hitDist={projectileHitDistance:F2} speed={resolvedSpell?.ProjectileSpeed ?? 0f:F1}");
                                    }
                                    else
                                    {
                                        Debug.LogError($"[SPELL-0x51] No target for non-projectile spell={resolvedSpell?.DisplayName ?? resolvedSpell?.SkillId ?? "UNKNOWN"} slotId={actionID} aim=({fPosX:F1},{fPosY:F1})");
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        // Unknown action type — log only, don't send response
                        Debug.LogError($"[ACTION] Unhandled actionType=0x{actionType:X2} target={targetEntityID}");
                    }

                    // ── READ SYNC SUFFIX ──
                    // Binary: 0x5DD840 — every ComponentUpdate ends with [syncFlags] [optional HP]
                    // 0x51 already consumed its position bytes + sync above, so only 0x50 hits this
                    if (reader.Remaining >= 1)
                    {
                        TryConsumeClientSyncSuffix(conn, reader, "ACTION-SYNC");
                    }

                }
                else if (subMessage == 0x64)
                {
                    // Client responding to our FollowClient message
                    Debug.LogError($"[COMPONENT] Client sent 0x64 response - consuming data");
                    HandleClientControlResponse(conn, reader, componentId);
                }
                else if ((subMessage == 0x35 || subMessage == 0x36))
                {
                    string connKey = conn.ConnId.ToString();
                    SavedCharacter savedChar = null;
                    if (conn.LoginName != null && _selectedCharacter.ContainsKey(conn.LoginName))
                        savedChar = CharacterRepository.GetCharacter(_selectedCharacter[conn.LoginName].Id);
                    if (savedChar == null) { while (reader.Remaining > 0) reader.ReadByte(); return; }
                    if (savedChar.hotbarSlots == null) savedChar.hotbarSlots = new List<HotbarSlotEntry>();
                    if (!_playerManipMap.ContainsKey(connKey)) _playerManipMap[connKey] = new Dictionary<uint, string>();

                    if (subMessage == 0x36 && reader.Remaining >= 4)
                    {
                        uint slot = reader.ReadUInt32();
                        Debug.LogError($"[HOTBAR] REMOVE slot {slot}");
                        string removedSkill = null;
                        if (_playerManipMap[connKey].ContainsKey(slot))
                        {
                            removedSkill = _playerManipMap[connKey][slot];
                            _playerManipMap[connKey].Remove(slot);
                        }
                        var removedSlotEntry = savedChar.hotbarSlots.FirstOrDefault(h => h.slot == slot);
                        if (removedSkill == null && removedSlotEntry != null)
                            removedSkill = removedSlotEntry.skill;
                        savedChar.hotbarSlots.RemoveAll(h => h.slot == slot);
                        if (removedSkill != null)
                            savedChar.hotbarSlots.RemoveAll(h => string.Equals(h.skill, removedSkill, StringComparison.OrdinalIgnoreCase));
                        CharacterRepository.SaveCharacter(savedChar);
                        if (IsPassiveSkill(removedSkill))
                            RecalculateHotbarPassiveBonuses(conn, savedChar);
                        Debug.LogError($"[HOTBAR] Saved remove: slot {slot}, was '{removedSkill}'");

                        // ═══ Bling Gnome: despawn when skill is removed from tray ═══
                        if (removedSkill != null &&
                            (removedSkill.IndexOf("BlingGnome", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             removedSkill.IndexOf("SummonBling", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            // Only despawn if BlingGnome is no longer on ANY hotbar slot
                            bool stillOnBar = savedChar.hotbarSlots.Any(h =>
                                h.skill.IndexOf("BlingGnome", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                h.skill.IndexOf("SummonBling", StringComparison.OrdinalIgnoreCase) >= 0);
                            if (!stillOnBar && BlingGnomeManager.Instance.HasGnome(conn.ConnId))
                            {
                                Debug.LogError($"[HOTBAR] BlingGnome removed from tray - despawning for {conn.LoginName}");
                                BlingGnomeManager.Instance.DespawnGnome(conn,
                                    (c, d, t, b) => SendCompressedA(c, d, t, b));
                            }
                        }

                        ushort manipCid = 0;
                        if (_playerManipulatorsIds.TryGetValue(connKey, out ushort mid))
                            manipCid = mid;
                        if (manipCid != 0 && removedSkill != null)
                        {
                            var pkt = new LEWriter();
                            pkt.WriteByte(0x07);
                            pkt.WriteByte(0x35);
                            pkt.WriteUInt16(manipCid);
                            pkt.WriteByte(0x01);                // subMsg = Remove
                            pkt.WriteUInt32(slot);              // manipulatorId (= slot ID) to remove
                            WritePlayerEntitySynch(conn, pkt);
                            pkt.WriteByte(0x06);
                            SendCompressedE(conn, pkt.ToArray());
                            Debug.LogError($"[HOTBAR-MANIP] Sent Remove slot={slot} '{removedSkill}'");
                        }
                    }
                    else if (subMessage == 0x35 && reader.Remaining >= 9)
                    {
                        uint slot = reader.ReadUInt32();
                        byte typeFlag = reader.ReadByte();
                        uint gcHash = reader.ReadUInt32();
                        Debug.LogError($"[HOTBAR] PLACE slot {slot} hash=0x{gcHash:X8}");

                        string skillGcClass = null;
                        if (_skillHashToGcClass.TryGetValue(gcHash, out string hr))
                            skillGcClass = hr;
                        if (skillGcClass == null)
                            foreach (var kv in _playerManipMap[connKey])
                            {
                                uint h = 5381;
                                foreach (char c in kv.Value.ToLower()) h = ((h << 5) + h) + (uint)c;
                                if (h == gcHash) { skillGcClass = kv.Value; break; }
                            }

                        if (skillGcClass != null)
                        {
                            string displacedSkill = null;
                            if (_playerManipMap[connKey].ContainsKey(slot))
                            {
                                string existing = _playerManipMap[connKey][slot];
                                if (!string.Equals(existing, skillGcClass, StringComparison.OrdinalIgnoreCase))
                                    displacedSkill = existing;
                            }
                            uint? oldSlot = null;
                            foreach (var kv in _playerManipMap[connKey])
                                if (string.Equals(kv.Value, skillGcClass, StringComparison.OrdinalIgnoreCase) && kv.Key != slot)
                                { oldSlot = kv.Key; break; }

                            if (oldSlot.HasValue) _playerManipMap[connKey].Remove(oldSlot.Value);
                            if (displacedSkill != null) _playerManipMap[connKey].Remove(slot);
                            _playerManipMap[connKey][slot] = skillGcClass;

                            savedChar.hotbarSlots.RemoveAll(h => h.slot == slot || string.Equals(h.skill, skillGcClass, StringComparison.OrdinalIgnoreCase));
                            if (displacedSkill != null)
                                savedChar.hotbarSlots.RemoveAll(h => string.Equals(h.skill, displacedSkill, StringComparison.OrdinalIgnoreCase));
                            savedChar.hotbarSlots.Add(new HotbarSlotEntry { slot = slot, skill = skillGcClass });
                            CharacterRepository.SaveCharacter(savedChar);
                            if (IsPassiveSkill(skillGcClass) || IsPassiveSkill(displacedSkill))
                                RecalculateHotbarPassiveBonuses(conn, savedChar);
                            Debug.LogError($"[HOTBAR] Saved: slot {slot} = '{skillGcClass}' displaced='{displacedSkill}' oldSlot={oldSlot}");

                            // ═══ Bling Gnome: spawn immediately when skill hits the tray ═══
                            if (skillGcClass.IndexOf("BlingGnome", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                skillGcClass.IndexOf("SummonBling", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                BlingGnomeManager.Instance.SetServer(this);
                                if (!BlingGnomeManager.Instance.HasGnome(conn.ConnId))
                                {
                                    Debug.LogError($"[HOTBAR] BlingGnome placed on tray - spawning for {conn.LoginName}");
                                    BlingGnomeManager.Instance.SpawnGnome(conn,
                                        (c, d, t, b) => SendCompressedA(c, d, t, b),
                                        (c, m) => SendSystemMessage(c, m));
                                }
                            }
                            // If BlingGnome was displaced by this new skill, despawn it
                            else if (displacedSkill != null &&
                                (displacedSkill.IndexOf("BlingGnome", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 displacedSkill.IndexOf("SummonBling", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                bool stillOnBar = savedChar.hotbarSlots.Any(h =>
                                    h.skill.IndexOf("BlingGnome", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    h.skill.IndexOf("SummonBling", StringComparison.OrdinalIgnoreCase) >= 0);
                                if (!stillOnBar && BlingGnomeManager.Instance.HasGnome(conn.ConnId))
                                {
                                    Debug.LogError($"[HOTBAR] BlingGnome displaced from tray - despawning for {conn.LoginName}");
                                    BlingGnomeManager.Instance.DespawnGnome(conn,
                                        (c, d, t, b) => SendCompressedA(c, d, t, b));
                                }
                            }

                            ushort manipCid = 0;
                            if (_playerManipulatorsIds.TryGetValue(connKey, out ushort mid))
                                manipCid = mid;

                            if (manipCid != 0)
                            {
                                byte skillLv = (byte)savedChar.GetSkillLevel(skillGcClass);

                                var pkt = new LEWriter();
                                pkt.WriteByte(0x07);
                                pkt.WriteByte(0x35);
                                pkt.WriteUInt16(manipCid);
                                pkt.WriteByte(0x00);                                // subMsg = Add
                                pkt.WriteByte(0xFF);
                                pkt.WriteCString(skillGcClass.ToLower());
                                pkt.WriteUInt32(slot);
                                pkt.WriteByte(skillLv);
                                WritePlayerEntitySynch(conn, pkt);
                                pkt.WriteByte(0x06);
                                SendCompressedE(conn, pkt.ToArray());
                                Debug.LogError($"[HOTBAR-MANIP] Sent Add '{skillGcClass}' slot={slot} lv={skillLv} manipCid=0x{manipCid:X4}");
                            }
                        }
                        else
                        {
                            Debug.LogError($"[HOTBAR] Could not resolve hash 0x{gcHash:X8} to any known skill");
                        }
                    }
                    else { while (reader.Remaining > 0) reader.ReadByte(); }
                }
                else if (subMessage == 0x21 || subMessage == 0x22 || subMessage == 0x23 || subMessage == 0x25 || subMessage == 0x26 || subMessage == 0x27 || subMessage == 0x28 || subMessage == 0x29)
                {
                    Debug.LogError($"[COMPONENT] Detected inventory/equipment operation 0x{subMessage:X2}");

                    string componentType = GetComponentType(conn.ConnId.ToString(), componentId);
                    Debug.LogError($"[COMPONENT-ROUTE] ComponentID 0x{componentId:X4} routed to: {componentType}");
                    if (componentType == "Equipment")
                    {
                        _equipmentHandler.HandleEquipmentUpdate(conn, reader, componentId, subMessage);
                    }
                    else if (componentType == "UnitContainer")
                    {
                        int unitContainerPayloadRemaining = reader.Remaining;
                        _inventoryHandler.HandleUnitContainerUpdate(conn, reader, componentId, subMessage);
                        if (subMessage == 0x22 && unitContainerPayloadRemaining == 0)
                        {
                            MerchantManager.FlushClientMerchantRefreshOnClientBoundary(conn, SendCompressedA);
                        }
                    }
                    else
                    {
                        Debug.LogError($"[COMPONENT-ROUTE] ❌ Unknown component type for ID 0x{componentId:X4}!");
                    }
                }
                else if (subMessage == 0x1E)
                {
                    bool isMerchant = false;
                    foreach (var kvp in _zoneNPCs)
                    {
                        foreach (var npc in kvp.Value)
                        {
                            if (npc.IsMerchant && npc.MerchantId == componentId)
                            {
                                isMerchant = true;
                                break;
                            }
                        }
                        if (isMerchant) break;
                    }

                    if (isMerchant)
                    {
                        // Debug: show raw bytes before reading
                        byte[] peek = reader.PeekRemaining();
                        Debug.LogError($"[MERCHANT] RAW BYTES (remaining {peek.Length}): {BitConverter.ToString(peek)}");

                        byte buyByte1 = reader.ReadByte();   // First byte of the 2-byte field
                        byte buyByte2 = reader.ReadByte();   // Second byte — possibly inventory index?
                        uint itemId = reader.ReadUInt32();    // ACTUAL item ID
                        Debug.LogError($"[MERCHANT] 🛒 BUY REQUEST! componentId=0x{componentId:X4}, byte1=0x{buyByte1:X2}({buyByte1}), byte2=0x{buyByte2:X2}({buyByte2}), itemId={itemId}");
                        MerchantManager.HandleBuyItem(conn, componentId, (ushort)itemId, _zoneNPCs, _selectedCharacter, SendCompressedA, this, buyByte1, buyByte2, IsPlayerFree(conn.LoginName));
                    }
                }
                else if (subMessage == 0x1F)
                {
                    bool isMerchant = false;
                    foreach (var kvp in _zoneNPCs)
                    {
                        foreach (var npc in kvp.Value)
                        {
                            if (npc.IsMerchant && npc.MerchantId == componentId)
                            {
                                isMerchant = true;
                                break;
                            }
                        }
                        if (isMerchant) break;
                    }

                    if (isMerchant)
                    {
                        byte[] sellPeek = reader.PeekRemaining();
                        Debug.LogError($"[MERCHANT] SELL RAW BYTES (remaining {sellPeek.Length}): {BitConverter.ToString(sellPeek)}");
                        ushort entityRef = reader.ReadUInt16();   // Entity ref for client's findItem
                        uint itemId = reader.ReadUInt32();        // Item handle
                        Debug.LogError($"[MERCHANT] 💰 SELL REQUEST! componentId=0x{componentId:X4}, entityRef=0x{entityRef:X4}, itemId={itemId}, remaining after read={reader.Remaining}");
                        MerchantManager.HandleSellItem(conn, componentId, (ushort)itemId, entityRef, _zoneNPCs, _selectedCharacter, SendCompressedA, GetPlayerState(conn.ConnId.ToString()), this, 0);
                        // Drain any remaining bytes to prevent stream desync
                        if (reader.Remaining > 0)
                        {
                            int drained = 0;
                            while (reader.Remaining > 0) { reader.ReadByte(); drained++; }
                            Debug.LogError($"[MERCHANT] Drained {drained} leftover sell bytes");
                        }
                    }
                }
                else
                {
                    // Check if this is a SkillTrainer component
                    ZoneNPC trainerNpc = null;
                    foreach (var kvp in _zoneNPCs)
                    {
                        foreach (var npc in kvp.Value)
                        {
                            if (npc.IsTrainer && npc.TrainerId == componentId)
                            {
                                trainerNpc = npc;
                                break;
                            }
                        }
                        if (trainerNpc != null) break;
                    }

                    if (trainerNpc != null)
                    {
                        byte[] trainerRaw = reader.PeekRemaining();
                        Debug.LogError($"[TRAINER] 🎓 SkillTrainer message! NPC={trainerNpc.Name} componentId=0x{componentId:X4} subMessage=0x{subMessage:X2} remaining={trainerRaw.Length} hex={BitConverter.ToString(trainerRaw)}");
                        HandleSkillTrainRequest(conn, reader, componentId, subMessage, trainerNpc);
                    }
                    else
                    {
                        // ═══════════════════════════════════════════════════════
                        // Check if this is a skill equip/unequip request (0x39)
                        // on the player's Skills component.
                        //
                        // Binary-verified: Skills::equipSkill @ 0x5419C0
                        //   Client sends subMessage 0x39 with:
                        //     - entityRef (0xFF + cstring skillGcClass)
                        //     - byte slotIndex (1-8 for equip, 0 for unequip)
                        //     - syncFlags + optional HP
                        //
                        // Without handling this, the unconsumed bytes corrupt the
                        // packet stream — every subsequent component update in the
                        // same frame gets misinterpreted (explains 0x22 with 0 bytes).
                        // ═══════════════════════════════════════════════════════
                        string skillConnKey = conn.ConnId.ToString();
                        ushort playerSkillsCid = 0;
                        _playerSkillsComponentId.TryGetValue(skillConnKey, out playerSkillsCid);

                        if (playerSkillsCid != 0 && componentId == playerSkillsCid && subMessage == 0x39)
                        {
                            byte[] rawData = reader.PeekRemaining();
                            Debug.LogError($"[SKILL-EQUIP] 0x39 on Skills cid=0x{componentId:X4} remaining={rawData.Length} hex={BitConverter.ToString(rawData)}");

                            // Parse request: entityRef (skill) + byte (slot index)
                            string skillGcClass = "";
                            byte slotByte = 0;
                            try
                            {
                                if (reader.Remaining > 0)
                                {
                                    byte refType = reader.ReadByte();
                                    if (refType == 0xFF && reader.Remaining > 0)
                                        skillGcClass = reader.ReadCString();
                                    else if (reader.Remaining >= 2)
                                    {
                                        // Entity ID ref (uint16) — less common
                                        ushort refId = reader.ReadUInt16();
                                        Debug.LogError($"[SKILL-EQUIP] EntityID ref: {refId}");
                                    }
                                }
                                if (reader.Remaining > 0)
                                    slotByte = reader.ReadByte();

                                // Consume sync suffix
                                if (reader.Remaining > 0)
                                {
                                    byte syncFlags = reader.ReadByte();
                                    if ((syncFlags & 0x02) != 0 && reader.Remaining >= 4)
                                        reader.ReadUInt32();
                                }
                            }
                            catch (Exception parseEx)
                            {
                                Debug.LogError($"[SKILL-EQUIP] Parse error: {parseEx.Message}");
                                // Drain remaining to prevent desync
                                while (reader.Remaining > 0) reader.ReadByte();
                            }

                            Debug.LogError($"[SKILL-EQUIP] Skill='{skillGcClass}' slot={slotByte}");

                            // Client already assigned the skill to the slot locally before
                            // sending this request. Do NOT send 0x38 processUpdateSkillSlot
                            // back — that function calls deactivate+reset on the slot which
                            // UNDOES the assignment. Just acknowledge silently.
                            Debug.LogError($"[SKILL-EQUIP] ✅ Accepted '{skillGcClass}' → slot {slotByte} (no response needed, client assigned locally)");
                        }
                        else if (subMessage == 0x07 && componentId == conn.QuestManagerId)
                        {
                            uint cpHash = 0;
                            if (reader.Remaining >= 1)
                            {
                                byte tag = reader.ReadByte();
                                if (tag == 0x04 && reader.Remaining >= 4) cpHash = reader.ReadUInt32();
                                else if (tag == 0x02 && reader.Remaining >= 2) cpHash = reader.ReadUInt16();
                                else if (tag == 0x01 && reader.Remaining >= 1) cpHash = reader.ReadByte();
                            }
                            while (reader.Remaining > 0) reader.ReadByte();

                            string destZone = null;
                            string connId = conn.ConnId.ToString();
                            var playerState = QuestManager.Instance.GetPlayerState(connId);
                            if (playerState != null)
                            {
                                foreach (var cpId in playerState.UnlockedCheckpoints)
                                {
                                    if (DatabaseLoader.ComputeDJB2Hash(cpId) == cpHash)
                                    {
                                        var cp = DatabaseLoader.Checkpoints.FirstOrDefault(c =>
                                            c.id.Equals(cpId, StringComparison.OrdinalIgnoreCase));
                                        if (cp != null)
                                            destZone = cp.zone;
                                        else if (_checkpointZoneMap.TryGetValue(cpId, out string mz))
                                            destZone = mz;
                                        Debug.LogError($"[CP-TELEPORT] Hash 0x{cpHash:X8} → '{cpId}' → zone '{destZone}'");
                                        break;
                                    }
                                }
                            }

                            if (!string.IsNullOrEmpty(destZone))
                            {
                                var zone = _zones.Values.FirstOrDefault(z =>
                                    z.name.Equals(destZone, StringComparison.OrdinalIgnoreCase));
                                if (zone != null)
                                {
                                    conn.PendingSpawnX = zone.spawnX;
                                    conn.PendingSpawnY = zone.spawnY;
                                    conn.PendingSpawnZ = zone.spawnZ;
                                }
                                ChangeZone(conn, destZone, "");
                            }
                            else
                            {
                                Debug.LogError($"[CP-TELEPORT] Hash 0x{cpHash:X8} not matched — fallback to rotator");
                                HandleObeliskTeleport(conn);
                            }
                        }
                        else if (subMessage == 0x0C && componentId == conn.QuestManagerId)
                        {
                            while (reader.Remaining > 0) reader.ReadByte();
                            if (!string.IsNullOrEmpty(conn.ZonePortalSource))
                            {
                                Debug.LogError($"[ZONE-PORTAL] Teleporting to '{conn.ZonePortalSource}'");
                                var zone = _zones.Values.FirstOrDefault(z =>
                                    z.name.Equals(conn.ZonePortalSource, StringComparison.OrdinalIgnoreCase));
                                if (zone != null)
                                {
                                    conn.PendingSpawnX = zone.spawnX;
                                    conn.PendingSpawnY = zone.spawnY;
                                    conn.PendingSpawnZ = zone.spawnZ;
                                }
                                ChangeZone(conn, conn.ZonePortalSource, "");
                            }
                            else
                                Debug.LogError("[ZONE-PORTAL] No zone portal source set");
                        }
                        else
                        {
                            Debug.LogError($"[COMPONENT] ⚠️ Unknown subMessage: 0x{subMessage:X2} cid=0x{componentId:X4}");
                            // CRITICAL: Drain remaining bytes to prevent stream desync!
                            // Without this, leftover bytes get interpreted as the next
                            // componentId+subMessage, corrupting everything downstream.
                            int drained = 0;
                            while (reader.Remaining > 0) { reader.ReadByte(); drained++; }
                            if (drained > 0)
                                Debug.LogError($"[COMPONENT] Drained {drained} bytes to prevent desync");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[COMPONENT] ERROR: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle state machine updates from client for monsters.
        /// Binary-verified: StateMachine::ReadMessage @ 0x5F0C70 uses BITFIELD format:
        ///   flags(1) → bit1:msgType(u16) bit2:scope(u16) bit3:target(u16) bit5:value(u32) bit4:watchers
        /// Then EntitySynchInfo::ReadFromStream @ 0x5DD840:
        ///   syncFlags(1) → if bit1: HP(u32)
        /// 
        /// Unit::onDead sends SM messages 0x9D5 (2517) and 0x925 (2341) — DEATH SIGNALS.
        /// OLD BUG: reader.ReadBytes(reader.Remaining) ate the entire stream, destroying
        /// all subsequent messages including death notifications.
        /// </summary>
        /// <summary>
        /// Handle state machine updates from client for monsters.
        /// Component types read DIFFERENT amounts before sync suffix:
        ///   BehaviorId (offset 1): 1 byte toggle only
        ///   SkillsId (offset 2): full bitfield ReadMessage (where death 0x925/0x9D5 appears)
        ///   ManipulatorsId (offset 3): 1 byte
        /// Then EntitySynchInfo::ReadFromStream @ 0x5DD840: syncFlags(1) + optional HP(u32)
        /// </summary>
        private void HandleMonsterStateMachineUpdate(RRConnection conn, LEReader reader, ushort componentId)
        {
            if (reader.Remaining < 1) return;
            if (VerbosePacketLogging) Debug.LogError($"[MONSTER-SM-RAW] cid={componentId} offset={(componentId - 50000) % 6} remaining={reader.Remaining} hex={BitConverter.ToString(reader.PeekRemaining())}");

            int componentOffset = (componentId - 50000) % 6;

            ushort messageType = 0xFFFF;
            ushort scope = 0xFFFF;
            ushort target = 0;
            uint value = 0;
            byte flags = 0;

            if (componentOffset == 2) // SkillsId — StateMachine::ReadMessage bitfield format
            {
                flags = reader.ReadByte();
                if ((flags & 0x02) != 0 && reader.Remaining >= 2)
                    messageType = reader.ReadUInt16();
                if ((flags & 0x04) != 0 && reader.Remaining >= 2)
                    scope = reader.ReadUInt16();
                if ((flags & 0x08) != 0 && reader.Remaining >= 2)
                    target = reader.ReadUInt16();
                if ((flags & 0x20) != 0 && reader.Remaining >= 4)
                    value = reader.ReadUInt32();
                if ((flags & 0x10) != 0 && reader.Remaining >= 2)
                {
                    ushort wCount = reader.ReadUInt16();
                    for (int w = 0; w < wCount && reader.Remaining >= 2; w++)
                        reader.ReadUInt16();
                }
            }
            else if (componentOffset == 1) // BehaviorId — 1 byte toggle only
            {
                flags = reader.ReadByte();
            }
            else // ManipulatorsId, ModifiersId, etc
            {
                flags = reader.ReadByte();
            }

            // === PHASE 2: EntitySynchInfo::ReadFromStream (binary @ 0x5DD840) ===
            // Sync suffix follows state machine message — Phase 1 already consumed message fields.
            // This is JUST the EntitySynchInfo: bit 1 (0x02) = HP wire(uint32) follows.
            uint syncHP = 0;
            bool hasSyncHP = false;
            if (reader.Remaining >= 1)
            {
                byte syncFlags = reader.ReadByte();
                if (syncFlags != 0 && (syncFlags & 0x02) != 0 && reader.Remaining >= 4)
                {
                    syncHP = reader.ReadUInt32();
                    hasSyncHP = true;
                }
            }

            // === PHASE 3: Resolve monster and log ===
            string messageName = messageType switch
            {
                0 => "Halt",
                1 => "GoToPrevious",
                2 => "Go",
                3 => "Arrive",
                4 => "CheckDest",
                5 => "Wait/LeaveCombat",
                6 => "Timer",
                7 => "ReturnHome",
                8 => "CombatTick",
                9 => "AGGRO",
                10 => "SecondaryTarget",
                11 => "Forget",
                12 => "Fidget/GOAGGRO",
                13 => "CombatAck/ServerAggro",
                0x925 => "DEAD (0x925)",
                0x9D5 => "DEAD (0x9D5)",
                _ => $"Unknown(0x{messageType:X4})"
            };

            string hpStr = hasSyncHP ? $" HP={syncHP}({syncHP / 256})" : "";
            if (VerbosePacketLogging) Debug.LogError($"[MONSTER-SM] cid={componentId} offset={componentOffset} flags=0x{flags:X2} msg={messageType}({messageName}) scope={scope} target={target} value={value}{hpStr}");

            var monster = CombatManager.Instance.GetMonsterByComponent(componentId)
                       ?? CombatManager.Instance.GetMonsterByBehaviorId(componentId)
                       ?? CombatManager.Instance.GetMonsterBySkillsId(componentId)
                       ?? CombatManager.Instance.GetMonsterByManipulatorsId(componentId);

            if (monster == null)
            {
                if (VerbosePacketLogging) Debug.LogError($"[MONSTER-SM] No monster found for cid={componentId}");
                return;
            }

            // === PHASE 4: Update HP from sync suffix ===
            if (hasSyncHP)
            {
                uint clientHPWire = syncHP;
                CombatManager.Instance.ObserveClientMonsterHP(monster, clientHPWire, "MONSTER-SM-HP-0x64");
            }

            // === PHASE 5: Handle death messages from Unit::onDead ===
            if (messageType == 0x9D5 || messageType == 0x925)
            {
                Debug.LogError($"[MONSTER-DEATH] {monster.Name} DEATH SIGNAL msg=0x{messageType:X4} from client!");
                if (!_finalizedMonsterKills.Contains(monster.EntityId))
                {
                    TryFinalizeMonsterKill(conn, monster, $"DeathMsg-0x{messageType:X4}");
                }
                return;
            }

            // === PHASE 6: Handle combat state transitions ===
            if (messageType == 9)
            {
                if (VerbosePacketLogging) Debug.LogError($"[MONSTER-SM] {monster.Name} AGGRO! value={value} target={target}");
                uint targetId = target != 0 ? target : (uint)(conn.Avatar?.Id ?? 0);
                if (targetId != 0)
                {
                    CombatManager.Instance.EngageMonsterFromClientAction(monster, targetId);
                }
            }
            else if (messageType == 13)
            {
                if (VerbosePacketLogging) Debug.LogError($"[MONSTER-SM] CombatAck value={value} target={target}");
                monster.State = MonsterState.Combat;
                if (target != 0) monster.TargetId = target;
                string instanceKey = !string.IsNullOrWhiteSpace(monster.InstanceKey) ? monster.InstanceKey : monster.ZoneName;
                uint roomSeed = CombatManager.Instance.GetRoomSeedForInstance(instanceKey);
                bool ready = CombatManager.Instance.TryGetRoomRuntime(instanceKey, out var runtime) && runtime.Initialized;
                Debug.LogError($"[RNG-SEED] Skipped CombatAck reseed for {monster.Name}#{monster.EntityId} target={target} instance='{instanceKey}' current=0x{roomSeed:X8} ready={ready}");
                try { ApplyMonsterDebuffs(conn, monster); } catch (Exception ex) { Debug.LogError($"[DEBUFF] Error: {ex.Message}"); }
            }
            else if (messageType == 10)
            {
                monster.State = MonsterState.Combat;
                if (target != 0) monster.TargetId = target;
                try { ApplyMonsterDebuffs(conn, monster); } catch (Exception ex) { Debug.LogError($"[DEBUFF] Error: {ex.Message}"); }
            }
            else if (messageType == 8)
            {
                try { ApplyMonsterDebuffs(conn, monster); } catch (Exception ex) { Debug.LogError($"[DEBUFF] Error: {ex.Message}"); }
            }
            else if (messageType == 12)
            {
                if (monster.TargetId == 0 && conn.Avatar != null)
                    monster.TargetId = (uint)conn.Avatar.Id;
                monster.State = MonsterState.Combat;
            }

        }


        public void SendSkillAttackUDP(RRConnection conn, ushort skillsComponentId, uint targetEntityId)
        {
            var session = GetUDPSessionForConnection(conn);
            if (session == null || !session.IsEstablished) return;
            if (!ResolveEntitySynchInfoForComponent(conn, skillsComponentId, 0x64, SyncContext.MonsterAction, 0, "UDP-SKILL", false, out EntitySynchInfoDecision decision))
                return;

            var writer = new LEWriter();
            writer.WriteByte(0x07);           // BeginStream
            writer.WriteByte(0x35);           // ComponentUpdate
            writer.WriteUInt16(skillsComponentId);
            writer.WriteByte(0x64);           // StateMachine message
            writer.WriteByte(0x03);           // flags = type + scope
            writer.WriteUInt16(0x08);         // Type 8 = CombatTick (TRIGGERS ATTACK!)
            writer.WriteUInt16(0xFFFF);       // scope = GLOBAL
            writer.WriteUInt32(0);            // value
            writer.WriteUInt16((ushort)targetEntityId);
            if (!TryWriteResolvedEntitySynchInfo(writer, skillsComponentId, 0x64, SyncContext.MonsterAction, "UDP-SKILL", decision))
                return;
            writer.WriteByte(0x06);           // EndStream
            byte[] packet = writer.ToArray();
            int padLen = (8 - (packet.Length % 8)) % 8;
            if (padLen > 0)
            {
                byte[] padded = new byte[packet.Length + padLen];
                Array.Copy(packet, padded, packet.Length);
                packet = padded;
            }
            SendEncryptedUDP(session, packet);
            Debug.LogError($"[UDP-SKILL] ⚔️ Sent Type 8 CombatTick to skillsId={skillsComponentId} target={targetEntityId}");
        }

        public void SendDespawnEntity(RRConnection conn, ushort entityId)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);           // BeginStream
            writer.WriteByte(0x05);           // Despawn
            writer.WriteUInt16(entityId);     // Entity to remove
            writer.WriteByte(0x06);           // EndStream
            SendToClient(conn, writer.ToArray());
            Debug.LogError($"[DESPAWN] Sent despawn for entity {entityId}");
        }



        private void SendClientControlReset(RRConnection conn, ushort componentId)
        {
            Debug.LogError($"[CONTROL] SendClientControlReset componentId=0x{componentId:X4}");

            var msg = new LEWriter();
            msg.WriteByte(0x07);
            if (!WriteClientControlUpdate(conn, msg, componentId, false) ||
                !WriteClientControlUpdate(conn, msg, componentId, true))
            {
                Debug.LogError($"[CONTROL] Dropped client control reset componentId=0x{componentId:X4}");
                ScheduleClientControlResetRetry(conn, componentId, "write-blocked");
                return;
            }
            msg.WriteByte(0x06);

            SendCompressedA(conn, 0x01, 0x0F, msg.ToArray(), SyncContext.ControlAck, "CLIENT-CONTROL");
            ClearPendingClientControlReset(conn, componentId);
            Debug.LogError($"[CONTROL] Sent client control reset");
        }

        private void ScheduleClientControlResetRetry(RRConnection conn, ushort componentId, string reason)
        {
            if (conn == null || componentId == 0 || !conn.IsConnected) return;
            conn.PendingClientControlReset = true;
            conn.PendingClientControlResetComponentId = componentId;
            conn.PendingClientControlResetAttempts = (byte)Math.Min(255, conn.PendingClientControlResetAttempts + 1);
            float delay = conn.PendingClientControlResetAttempts <= 3 ? 0.10f : conn.PendingClientControlResetAttempts <= 10 ? 0.25f : 1.00f;
            conn.PendingClientControlResetNextAttemptTime = Time.time + delay;
            Debug.LogError($"[CONTROL] Scheduled client control reset retry componentId=0x{componentId:X4} attempt={conn.PendingClientControlResetAttempts} reason={reason} delay={delay:F2}");
        }

        private void ClearPendingClientControlReset(RRConnection conn, ushort componentId)
        {
            if (conn == null || !conn.PendingClientControlReset) return;
            if (componentId != 0 && conn.PendingClientControlResetComponentId != componentId) return;
            conn.PendingClientControlReset = false;
            conn.PendingClientControlResetComponentId = 0;
            conn.PendingClientControlResetNextAttemptTime = 0f;
            conn.PendingClientControlResetAttempts = 0;
        }

        private void FlushPendingClientControlResets()
        {
            foreach (var conn in _connections.Values)
            {
                if (conn == null || !conn.PendingClientControlReset) continue;
                if (!conn.IsConnected)
                {
                    ClearPendingClientControlReset(conn, 0);
                    continue;
                }
                if (Time.time < conn.PendingClientControlResetNextAttemptTime) continue;
                ushort componentId = conn.PendingClientControlResetComponentId;
                if (componentId == 0)
                {
                    ClearPendingClientControlReset(conn, 0);
                    continue;
                }
                Debug.LogError($"[CONTROL] Retrying pending client control reset componentId=0x{componentId:X4} attempt={conn.PendingClientControlResetAttempts}");
                SendClientControlReset(conn, componentId);
            }
        }

        private bool WriteClientControlUpdate(RRConnection conn, LEWriter writer, ushort componentId, bool followClient)
        {
            return WriteClientControlUpdate(conn, writer, componentId, followClient, "CLIENT-CONTROL", null);
        }

        private bool WriteClientControlUpdate(RRConnection conn, LEWriter writer, ushort componentId, bool followClient, string packetName, uint? forcedHPWire)
        {
            writer.WriteByte(0x35);
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x64);
            writer.WriteByte(followClient ? (byte)0x01 : (byte)0x00);
            if (forcedHPWire.HasValue)
            {
                GetNativeValidationCutoff(out uint validationCutoffTick, out float validationCutoffTime);
                uint avatarEntityId = conn?.Avatar != null ? (uint)conn.Avatar.Id : 0u;
                return TryWriteResolvedEntitySynchInfo(writer, componentId, 0x64, SyncContext.ControlAck, packetName, EntitySynchInfoDecision.HP(EntitySynchInfoOwner.Avatar, forcedHPWire.Value, packetName, avatarEntityId, componentId, 0x64, GetNativeCombatNow(), $"forced-player-hp; validationCutoffTick={validationCutoffTick} validationCutoffTime={validationCutoffTime:F3}", validationCutoffTick, validationCutoffTime));
            }
            return TryWriteEntitySynchForComponent(conn, writer, componentId, 0x64, SyncContext.ControlAck, packetName, true);
        }



        // 🔥 ADD THIS NEW METHOD:
        /// <summary>
        /// Handles NPC click (0x01) - triggers pathfinding to NPC
        /// </summary>
        private void HandleNPCClick(RRConnection conn, ushort componentId, ushort targetEntityID, byte responseId, byte sessionID)
        {
            Debug.LogError($"[NPC] ═══════════════════════════════════════════════════");
            Debug.LogError($"[NPC] Activate target=0x{targetEntityID:X4} responseId={responseId} sessionID={sessionID}");
            conn.SessionID = sessionID;

            // 🔥 SET NPC GCTYPE FOR QUEST SYSTEM
            if (_zoneNPCs.TryGetValue(conn.CurrentZoneId, out var npcs))
            {
                var npc = npcs.FirstOrDefault(n => n.Id == targetEntityID);
                if (npc != null)
                {
                    conn.CurrentDialogNpcId = npc.GCClass;
                    if (npc.IsMerchant)
                    {
                        TrackPendingMerchantActivation(conn, npc);
                    }
                    if (npc.IsPosseMagnate)
                    {
                        // Tad's dialog Create-Posse button is currently NOT surfaced (NPC component
                        // GCType still unknown — both "PosseRegistryOption" and "posse" got Zone
                        // Error 10). Fall back to a chat hint until we find the right string.
                        SendSystemMessage(conn, "Open the Posse tab in your menu, or type /posse create <name> to start a posse (level 15+, 1,000,000 gold). Type /posse help for the full list of commands.");
                        Debug.LogError($"[POSSE] Player clicked PosseMagnate {npc.GCClass} — chat hint sent");
                    }
                    Debug.LogError($"[NPC] ✅ Set CurrentDialogNpcId = {conn.CurrentDialogNpcId}");
                }
            }

            var msg = new LEWriter();
            msg.WriteByte(0x35);
            msg.WriteUInt16(componentId);
            msg.WriteByte(0x01);
            msg.WriteByte(responseId);
            msg.WriteByte(0x06);
            msg.WriteByte(sessionID);
            msg.WriteUInt16(targetEntityID);
            WritePlayerEntitySynch(conn, msg);
            conn.MessageQueue.Enqueue(msg.ToArray());
            Debug.LogError($"[NPC] ✅ Queued activate response");

            // Check if clicked on BlingGnome
            if (BlingGnomeManager.Instance.HasGnome(conn.ConnId))
            {
                uint gnomeEntityId = BlingGnomeManager.Instance.GetGnomeEntityId(conn.ConnId);
                if (targetEntityID == gnomeEntityId)
                {
                    Debug.LogError($"[NPC] 🎯 BLINGGNOME CLICKED!");
                    Debug.LogError($"[NPC] 🔊 SHOULD play greeting sound");
                    Debug.LogError($"[NPC] 🔊 Sound 120 = Bling_Gnomes_Summon_01-04");
                    Debug.LogError($"[NPC] (Gnome doesn't respond - sounds not implemented in client)");
                }
            }
        }

        private void TrackPendingMerchantActivation(RRConnection conn, ZoneNPC npc)
        {
            conn.PendingMerchantNpcGcClass = npc.GCClass;
            conn.PendingMerchantComponentId = (ushort)npc.MerchantId;
            conn.PendingMerchantTargetX = npc.PosX;
            conn.PendingMerchantTargetY = npc.PosY;

            if (IsPendingMerchantReached(conn))
                ActivatePendingMerchantRefresh(conn);
            else
                Debug.LogError($"[NPC] Pending merchant refresh activation for {npc.GCClass} at ({npc.PosX:F1},{npc.PosY:F1})");
        }

        private void CheckPendingMerchantActivation(RRConnection conn)
        {
            if (string.IsNullOrWhiteSpace(conn.PendingMerchantNpcGcClass))
                return;

            if (!IsPendingMerchantReached(conn))
                return;

            ActivatePendingMerchantRefresh(conn);
        }

        private bool IsPendingMerchantReached(RRConnection conn)
        {
            float dx = conn.PlayerPosX - conn.PendingMerchantTargetX;
            float dy = conn.PlayerPosY - conn.PendingMerchantTargetY;
            return dx * dx + dy * dy <= MERCHANT_ACTIVATION_DISTANCE_SQ;
        }

        private void ActivatePendingMerchantRefresh(RRConnection conn)
        {
            string npcGcType = conn.PendingMerchantNpcGcClass;
            ushort componentId = conn.PendingMerchantComponentId;

            conn.PendingMerchantNpcGcClass = null;
            conn.PendingMerchantComponentId = 0;

            MerchantManager.ScheduleClientMerchantRefresh(conn, npcGcType, componentId, DateTime.UtcNow);
            Debug.LogError($"[NPC] Activated client merchant refresh schedule for {npcGcType}");
        }

        private bool SendUseTargetActionResponse(RRConnection conn, ushort componentId, byte responseId, byte manipulatorId, byte useFlags, ushort targetId, SyncContext actionResponseContext, string actionResponseSyncTag, string reason)
        {
            if (conn == null || componentId == 0)
                return false;

            var msg = new LEWriter();
            msg.WriteByte(0x07);
            msg.WriteByte(0x35);
            msg.WriteUInt16(componentId);
            msg.WriteByte(0x01);
            msg.WriteByte(responseId);
            msg.WriteByte(0x50);
            msg.WriteByte(manipulatorId);
            msg.WriteByte(useFlags);
            msg.WriteUInt16(targetId);

            if (!TryWriteEntitySynchForComponent(conn, msg, componentId, 0x01, actionResponseContext, actionResponseSyncTag, true))
                return false;

            msg.WriteByte(0x06);
            bool sent = SendCompressedA(conn, 0x01, 0x0F, msg.ToArray(), actionResponseContext, actionResponseSyncTag);
            if (sent)
            {
                PlayerState state = GetPlayerState(conn.ConnId.ToString());
                Debug.LogError($"[ATTACK] <<< Sent {reason} ActionResponse target={targetId} component={componentId} responseId={responseId} manip={manipulatorId} flags={useFlags} playerHP={state?.SynchHP ?? 0}");
            }
            return sent;
        }

        private void HandlePlayerAttackMonster(RRConnection conn, ushort componentId, byte responseId,
     byte manipulatorId, byte useFlags, ushort targetId, Combat.Monster monster, bool acceptedActionMonsterHP)
        {
            Debug.LogError($"[ATTACK] >>> UseTarget on {monster.Name} (ID:{targetId}) sessionCtr={manipulatorId} slotId={useFlags} responseId={responseId}");
            bool isSkillAction = useFlags >= 100;
            bool inAttackRange = true;
            float attackDistance = 0f;
            float attackRange = 0f;
            float nativeAttackRange = 0f;
            float initUseTolerance = 0f;
            string initUseSource = "native-contact-range";
            bool nativeMeleeContact = false;
            bool nativeRangedBasic = false;
            bool nativeRangedProjectileBasic = false;
            bool canStartWeaponCycle = false;
            float weaponCycleRange = 0f;
            bool activatedUseTargetBeforeSuffix = false;
            PlayerState state = null;
            Combat.SpellData actionSpell = null;
            if (monster.IsAlive)
                state = GetPlayerState(conn.ConnId.ToString());
            string responseKey = conn != null ? $"{conn.ConnId}:{targetId}:{useFlags}" : "";
            bool redundantBasicAttack = monster.IsAlive && !isSkillAction && IsRedundantUseTarget(conn, targetId, useFlags);
            bool zoneInvulnerabilityBlocking = IsZoneSpawnInvulnerabilityBlockingCombat(conn);
            bool hasLastUseTargetResponse = false;
            float repeatedUseTargetElapsed = float.MaxValue;
            bool coalesceRedundantBasicUseTarget = false;
            if (zoneInvulnerabilityBlocking)
            {
                ClearZoneSpawnInvulnerability(conn, $"ACTION-0x50 flags={useFlags} target={targetId}");
                zoneInvulnerabilityBlocking = false;
            }
            if (redundantBasicAttack && conn != null)
            {
                hasLastUseTargetResponse = _useTargetResponseTimes.TryGetValue(responseKey, out float lastResponse);
                repeatedUseTargetElapsed = hasLastUseTargetResponse ? Time.time - lastResponse : float.MaxValue;
                string ackAge = hasLastUseTargetResponse ? repeatedUseTargetElapsed.ToString("F2") : "none";
                Debug.LogError($"[ATTACK] Repeated UseTarget ack path on {monster.Name} target={targetId} flags={useFlags} lastResponseAge={ackAge} started={conn.ActiveUseTargetStartedWeaponUse} init={conn.ActiveUseTargetInitUsePassed} native=UseTarget::IsRedundant");
                coalesceRedundantBasicUseTarget = true;
            }
            if (monster.IsAlive && !zoneInvulnerabilityBlocking)
            {
                if (Combat.CombatManager.Instance.IsMonsterDeathPendingClientConfirmation(monster))
                {
                    Debug.LogError($"[ATTACK] target pending client death confirmation; releasing UseTarget target={targetId} hp={CombatManager.Instance.PeekMonsterCurrentHPWire(monster)}");
                    ClearUseTargetAndReleaseControl(conn, "ATTACK-death-pending", componentId);
                    return;
                }
                nativeRangedBasic = !isSkillAction && state != null && Combat.DamageComputer.IsNativeRangedWeapon(state);
                nativeRangedProjectileBasic = nativeRangedBasic && Combat.DamageComputer.IsNativeProjectileWeapon(state);
                if (!isSkillAction)
                {
                    inAttackRange = nativeRangedBasic
                        ? IsRangedBasicTargetWithinServerRange(conn, state, monster, out attackDistance, out attackRange)
                        : IsMeleeTargetWithinServerRange(conn, state, monster, out attackDistance, out attackRange);
                }
                else
                {
                    actionSpell = ResolveActionSpell(conn, state, useFlags);
                    inAttackRange = IsSkillTargetWithinServerRange(conn, actionSpell, monster, out attackDistance, out attackRange);
                }
                if (!isSkillAction)
                {
                    nativeAttackRange = nativeRangedBasic
                        ? Combat.CombatManager.Instance.ResolveNativeUseTargetInitUseRange(state, monster, out initUseTolerance, out initUseSource)
                        : Combat.CombatManager.Instance.ResolvePlayerMeleeNativeContactRange(state, monster);
                    if (nativeRangedProjectileBasic)
                    {
                        ResolveMonsterClientVisiblePosition(monster, out float targetX, out float targetY);
                        nativeMeleeContact = Combat.CombatManager.Instance.EvaluateNativeUseTargetInitUse(
                            conn.PlayerPosX, conn.PlayerPosY, targetX, targetY,
                            nativeAttackRange, initUseTolerance, out attackDistance,
                            out _, out _);
                    }
                    else
                    {
                        nativeMeleeContact = nativeAttackRange > 0f && attackDistance <= nativeAttackRange + NATIVE_CONTACT_RANGE_EPSILON;
                    }
                }
                if (!isSkillAction && state != null)
                {
                    float playerX = conn != null ? conn.PlayerPosX : 0f;
                    float playerY = conn != null ? conn.PlayerPosY : 0f;
                    string lane = nativeRangedBasic ? "ranged" : "melee";
                    float projectileReach = nativeRangedProjectileBasic ? WeaponCycleTracker.NativeProjectileRadiusFromAuthoredSize(state.WeaponProjectileSize) + Mathf.Max(0f, state.WeaponProjectileSpeed) * COMBAT_TICK : 0f;
                    ResolveMonsterClientVisiblePosition(monster, out float targetX, out float targetY);
                    Debug.LogError($"[ATTACK-RANGE] lane={lane} weaponClass={state.WeaponClass} weaponRange={state.WeaponRange} weaponSpeed={state.WeaponSpeed:F1} useProjectile={state.WeaponUsesProjectile} projectileSpeed={state.WeaponProjectileSpeed:F1} projectileSize={state.WeaponProjectileSize:F1} projectileReach={projectileReach:F1} player=({playerX:F1},{playerY:F1}) monster=({targetX:F1},{targetY:F1}) dist={attackDistance:F1} range={attackRange:F1} nativeRange={nativeAttackRange:F1} inRange={inAttackRange} nativeContact={nativeMeleeContact} flags={useFlags} target={targetId}");
                }
                if (!isSkillAction)
                {
                    canStartWeaponCycle = nativeRangedProjectileBasic
                        ? (inAttackRange && nativeMeleeContact)
                        : (inAttackRange || nativeMeleeContact);
                    weaponCycleRange = nativeAttackRange;
                    if (coalesceRedundantBasicUseTarget)
                    {
                        if (nativeRangedProjectileBasic && conn != null)
                        {
                            conn.ActiveUseTargetInitUsePassed = nativeMeleeContact;
                            conn.ActiveUseTargetInitUseRange = nativeAttackRange;
                            conn.ActiveUseTargetInitUseDistance = attackDistance;
                            conn.ActiveUseTargetClientSyncTolerance = initUseTolerance;
                        }
                        if (state != null)
                        {
                            string redundantAtkKey = conn.ConnId.ToString();
                            Combat.WeaponCycleTracker.Instance.RegisterAttack(redundantAtkKey, targetId, monster, state, conn, canStartWeaponCycle, attackDistance, weaponCycleRange);
                        }
                        Debug.LogError($"[ATTACK] Native redundant UseTarget coalesced target={targetId} flags={useFlags} sessionCtr={manipulatorId} activeSession={conn.ActiveUseTargetSessionId} lastResponseAge={repeatedUseTargetElapsed:F2} started={conn.ActiveUseTargetStartedWeaponUse} init={conn.ActiveUseTargetInitUsePassed} canStart={canStartWeaponCycle} dist={attackDistance:F1} range={weaponCycleRange:F1} native=UseTarget::IsRedundant+Behavior::doActionLocal action=keep-current-UseTarget mirror=weapon-cycle noTimingFloor=True");
                        return;
                    }
                    ActivateUseTarget(conn, targetId, useFlags, componentId, manipulatorId);
                    activatedUseTargetBeforeSuffix = true;
                    if (nativeRangedProjectileBasic && conn != null)
                    {
                        conn.ActiveUseTargetInitUsePassed = nativeMeleeContact;
                        conn.ActiveUseTargetInitUseRange = nativeAttackRange;
                        conn.ActiveUseTargetInitUseDistance = attackDistance;
                        conn.ActiveUseTargetClientSyncTolerance = initUseTolerance;
                    }
                }
                if (state != null && conn?.Avatar != null)
                {
                    if (!isSkillAction)
                        Combat.CombatManager.Instance.SetPlayerActiveClientAttack((uint)conn.Avatar.Id, true, monster.EntityId);
                    bool nativeWeaponUseStarted = !isSkillAction && nativeRangedProjectileBasic && nativeMeleeContact;
                    Combat.CombatManager.Instance.EngageMonsterFromClientAction(monster, (uint)conn.Avatar.Id, nativeWeaponUseStarted);
                }
                if (state != null)
                {
                    string atkKey = conn.ConnId.ToString();
                    var oldTarget = Combat.WeaponCycleTracker.Instance.GetActiveTarget(atkKey);
                    bool targetSwitch = oldTarget != null && oldTarget.EntityId != monster.EntityId;
                    if (targetSwitch)
                    {
                        Debug.LogError($"[ATTACK] Target switch from {oldTarget.Name} to {monster.Name}");
                    }
                    monster.UseTargetCount++;
                    if (isSkillAction)
                    {
                        if (inAttackRange)
                        {
                            float projectileHitDistance = Mathf.Max(0f, attackDistance);
                            if (actionSpell != null && actionSpell.ProjectileSize > 0f && actionSpell.ProjectileSpeed > 0f)
                            {
                                ResolveMonsterClientVisiblePosition(monster, out float targetX, out float targetY);
                                var pending = CreatePendingSpellProjectile(conn, state, monster, actionSpell, useFlags, useFlags, componentId, conn.PlayerPosX, conn.PlayerPosY, targetX, targetY, false, projectileHitDistance);
                                _pendingSpells.Enqueue(pending);
                                Debug.LogError($"[SPELL] UseTarget projectile runtime: slotId={useFlags} on {monster.Name} delay={pending.ProjectileDelay:F3}s hitHint={pending.ProjectileHitDistance:F2} speed={pending.ProjectileSpeed:F1} step={pending.StepDistance:F3} initPreStep={pending.InitialDistance:F3} maxDist={pending.MaxDistance:F2} seq={pending.Sequence}");
                            }
                            else
                            {
                                float projectileDelay = ResolveProjectileImpactDelay(actionSpell, projectileHitDistance);
                                float dueTime = projectileDelay > 0f ? GetNativeCombatNow() + projectileDelay : 0f;
                                Debug.LogError($"[SPELL] UseTarget spell: slotId={useFlags} on {monster.Name} queued delay={projectileDelay:F3}s hitDist={projectileHitDistance:F2} speed={actionSpell?.ProjectileSpeed ?? 0f:F1}");
                                ResolveMonsterClientVisiblePosition(monster, out float targetX, out float targetY);
                                _pendingSpells.Enqueue(new PendingSpell { Conn = conn, State = state, Monster = monster, Spell = actionSpell, ManipId = useFlags, UseFlags = useFlags, ComponentId = componentId, StartX = conn.PlayerPosX, StartY = conn.PlayerPosY, AimX = targetX, AimY = targetY, InstanceKey = GetInstanceZoneKey(conn), DueTime = dueTime, ProjectileHitDistance = projectileHitDistance, ProjectileDelay = projectileDelay });
                            }
                        }
                        else
                        {
                            Debug.LogError($"[SPELL] Target outside range: {actionSpell?.DisplayName ?? "UNKNOWN"} on {monster.Name} dist={attackDistance:F1} range={attackRange:F1}");
                        }
                    }
                    else
                    {
                        string weaponLane = nativeRangedBasic ? "RangedWeapon" : "Melee";
                        if (nativeRangedProjectileBasic)
                        {
                            Combat.WeaponCycleTracker.Instance.RegisterAttack(atkKey, targetId, monster, state, conn, canStartWeaponCycle, attackDistance, weaponCycleRange);
                            Debug.LogError($"[ATTACK] {weaponLane} UseTarget armed: slotId={useFlags} on {monster.Name} sessionCtr={manipulatorId} dist={attackDistance:F1} initUseRange={nativeAttackRange:F1} weaponRange={state.WeaponRange:F1} contactRange={weaponCycleRange:F1} actionRange={attackRange:F1} currentInitUseWouldPass={nativeMeleeContact} inInitRange={inAttackRange} processedByUseTargetTick={canStartWeaponCycle} rngAdvanced=False hpMutated=False");
                        }
                        else if (canStartWeaponCycle)
                        {
                            Debug.LogError($"[ATTACK] {weaponLane}: slotId={useFlags} on {monster.Name} sessionCtr={manipulatorId} dist={attackDistance:F1} range={attackRange:F1} nativeRange={nativeAttackRange:F1}");
                            Combat.WeaponCycleTracker.Instance.RegisterAttack(atkKey, targetId, monster, state, conn, true, attackDistance, weaponCycleRange);
                            Debug.LogError($"[ATTACK] Registered with WeaponCycleTracker");
                        }
                        else
                        {
                            Combat.WeaponCycleTracker.Instance.RegisterAttack(atkKey, targetId, monster, state, conn, false, attackDistance, weaponCycleRange);
                            Debug.LogError($"[ATTACK] {weaponLane} approach cycle: {monster.Name} dist={attackDistance:F1} range={attackRange:F1} nativeRange={nativeAttackRange:F1}");
                        }
                    }
                }
                else
                {
                    Debug.LogError($"[COMBAT] WARNING: No PlayerState for {conn.ConnId}! Skipping damage.");
                }
            }

            var msg = new LEWriter();
            msg.WriteByte(0x07);  // BeginStream
            msg.WriteByte(0x35);              // ComponentUpdate
            msg.WriteUInt16(componentId);
            msg.WriteByte(0x01);              // ActionResponse
            msg.WriteByte(responseId);
            msg.WriteByte(0x50);              // UseTarget action type
            msg.WriteByte(manipulatorId);
            msg.WriteByte(useFlags);
            msg.WriteUInt16(targetId);
            SyncContext actionResponseContext = isSkillAction ? SyncContext.PlayerActionResponse : SyncContext.PlayerBasicAttackResponse;
            string actionResponseSyncTag = isSkillAction ? "PlayerActionResponse" : "PlayerBasicAttackResponse";
            if (!activatedUseTargetBeforeSuffix && !zoneInvulnerabilityBlocking && !isSkillAction && monster.IsAlive)
            {
                ActivateUseTarget(conn, targetId, useFlags, componentId, manipulatorId);
                activatedUseTargetBeforeSuffix = true;
            }
            if (!TryWriteEntitySynchForComponent(conn, msg, componentId, 0x01, actionResponseContext, actionResponseSyncTag, true))
            {
                Debug.LogError($"[ATTACK] failed ActionResponse sync: component={componentId} target={targetId} flags={useFlags}; sending release fallback");
                ClearUseTargetAndReleaseControl(conn, "ATTACK-sync-failed", componentId);
                return;
            }
            msg.WriteByte(0x06);  // EndStream

            byte[] packet = msg.ToArray();
            if (!SendCompressedA(conn, 0x01, 0x0F, packet, actionResponseContext, actionResponseSyncTag))
            {
                ClearUseTargetAndReleaseControl(conn, "ATTACK-send-failed", componentId);
                return;
            }
            if (!zoneInvulnerabilityBlocking && conn?.Avatar != null && monster.IsAlive)
                Combat.CombatManager.Instance.SetPlayerActiveClientAttack((uint)conn.Avatar.Id, true, monster.EntityId);
            if (!string.IsNullOrEmpty(responseKey))
                _useTargetResponseTimes[responseKey] = Time.time;
            Debug.LogError($"[ATTACK] <<< Sent ActionResponse | alive={monster.IsAlive} playerHP={state?.SynchHP ?? 0} targetHP={CombatManager.Instance.PeekMonsterCurrentHPWire(monster)}/{monster.MaxHPWire}");
            if (!activatedUseTargetBeforeSuffix && !zoneInvulnerabilityBlocking && !isSkillAction && monster.IsAlive)
                ActivateUseTarget(conn, targetId, useFlags, componentId, manipulatorId);
            else if (!monster.IsAlive && IsUseTargetingMonster(conn, monster))
                ClearUseTargetAndReleaseControl(conn, "ATTACK-target-dead", componentId);

            if (!zoneInvulnerabilityBlocking && inAttackRange && !nativeRangedBasic)
                BroadcastMeleeAttack(conn, responseId, manipulatorId, useFlags);
        }

        private static float ResolveBasicAttackResponseInterval(PlayerState state)
        {
            int ticks = Combat.DamageComputer.ResolveNativeBasicAttackCooldownTicks(state);
            return ticks / 30f;
        }

        private static float ResolveBasicAttackHitDelay(PlayerState state)
        {
            float speed = state != null ? state.WeaponSpeed : 105f;
            float speedPct = Combat.DamageComputer.ResolveNativeWeaponAttackSpeedPct(state);
            float speedScale = 1f + (speedPct / 100f);
            if (speedScale < 0.05f) speedScale = 0.05f;
            speed *= speedScale;
            if (speed <= 1f) speed = 105f;
            int speedField = Math.Max(1, Mathf.RoundToInt(speed));
            int hitTicks = Math.Max(1, (15 * 100) / speedField);
            return hitTicks / 30f;
        }

        private static string GetActiveSkillBusyKey(RRConnection conn, ushort componentId, byte actionId)
        {
            int connId = conn != null ? conn.ConnId : 0;
            return $"{connId}:{componentId}:{actionId}";
        }

        private static float ResolveActiveSkillBusySeconds(Combat.SpellData spell)
        {
            if (spell == null) return 0f;
            int repeatCount = Mathf.Max(0, spell.RepeatCount);
            if (repeatCount <= 0) return 0f;
            int animationFrames = Mathf.Max(1, spell.AnimationLengthFrames);
            return repeatCount * animationFrames * COMBAT_TICK;
        }

        private bool IsActiveSkillBusy(RRConnection conn, ushort componentId, byte actionId, Combat.SpellData spell, out float remaining)
        {
            remaining = 0f;
            if (spell == null) return false;
            string key = GetActiveSkillBusyKey(conn, componentId, actionId);
            if (!_activeSkillBusyUntil.TryGetValue(key, out float busyUntil))
                return false;

            remaining = busyUntil - Time.time;
            if (remaining > 0f)
                return true;

            _activeSkillBusyUntil.Remove(key);
            remaining = 0f;
            return false;
        }

        private void StartActiveSkillBusy(RRConnection conn, ushort componentId, byte actionId, Combat.SpellData spell)
        {
            float busySeconds = ResolveActiveSkillBusySeconds(spell);
            if (busySeconds <= 0f) return;
            _activeSkillBusyUntil[GetActiveSkillBusyKey(conn, componentId, actionId)] = Time.time + busySeconds;
        }

        private static bool IsRedundantUseTarget(RRConnection conn, ushort targetId, byte useFlags)
        {
            if (conn == null || !conn.HasActiveUseTarget || conn.ActiveUseTargetId != targetId)
                return false;
            byte activeFlags = conn.ActiveUseTargetFlags;
            bool activeBasicAttack = activeFlags == 0x0A || activeFlags == 0x0B;
            bool incomingBasicAttack = useFlags == 0x0A || useFlags == 0x0B;
            return (activeBasicAttack && incomingBasicAttack) || activeFlags == useFlags;
        }

        private static void ActivateUseTarget(RRConnection conn, ushort targetId, byte useFlags, ushort componentId = 0, byte sessionId = 0)
        {
            if (conn == null) return;
            bool sameBasicTarget = conn.HasActiveUseTarget
                && conn.ActiveUseTargetId == targetId
                && IsBasicMeleeUseTargetFlag(conn.ActiveUseTargetFlags)
                && IsBasicMeleeUseTargetFlag(useFlags);
            bool startedWeaponUse = sameBasicTarget && conn.ActiveUseTargetStartedWeaponUse;
            bool initUsePassed = sameBasicTarget && conn.ActiveUseTargetInitUsePassed;
            bool visibleHit = sameBasicTarget && conn.ActiveUseTargetVisibleHit;
            float initUseRange = sameBasicTarget ? conn.ActiveUseTargetInitUseRange : 0f;
            float initUseDistance = sameBasicTarget ? conn.ActiveUseTargetInitUseDistance : 0f;
            float tolerance = sameBasicTarget ? conn.ActiveUseTargetClientSyncTolerance : 0f;
            long lastProjectileSeq = sameBasicTarget ? conn.ActiveUseTargetLastProjectileSeq : 0;
            int lastImpactTick = sameBasicTarget ? conn.ActiveUseTargetLastImpactTick : -1;

            conn.HasActiveUseTarget = true;
            conn.ActiveUseTargetId = targetId;
            conn.ActiveUseTargetFlags = useFlags;
            conn.ActiveUseTargetComponentId = componentId;
            conn.ActiveUseTargetSessionId = sessionId;
            conn.ActiveUseTargetStartedWeaponUse = startedWeaponUse;
            conn.ActiveUseTargetInitUsePassed = initUsePassed;
            conn.ActiveUseTargetVisibleHit = visibleHit;
            conn.ActiveUseTargetInitUseRange = initUseRange;
            conn.ActiveUseTargetInitUseDistance = initUseDistance;
            conn.ActiveUseTargetClientSyncTolerance = tolerance;
            conn.ActiveUseTargetLastProjectileSeq = lastProjectileSeq;
            conn.ActiveUseTargetLastImpactTick = lastImpactTick;
        }

        private static void ClearUseTarget(RRConnection conn)
        {
            if (conn == null) return;
            conn.HasActiveUseTarget = false;
            conn.ActiveUseTargetId = 0;
            conn.ActiveUseTargetFlags = 0;
            conn.ActiveUseTargetComponentId = 0;
            conn.ActiveUseTargetSessionId = 0;
            conn.ActiveUseTargetInitUsePassed = false;
            conn.ActiveUseTargetStartedWeaponUse = false;
            conn.ActiveUseTargetVisibleHit = false;
            conn.ActiveUseTargetInitUseRange = 0f;
            conn.ActiveUseTargetInitUseDistance = 0f;
            conn.ActiveUseTargetClientSyncTolerance = 0f;
            conn.ActiveUseTargetLastProjectileSeq = 0;
            conn.ActiveUseTargetLastImpactTick = -1;
        }

        private static ushort ResolveClientControlComponentId(RRConnection conn, ushort componentId)
        {
            if (componentId != 0) return componentId;
            if (conn == null) return 0;
            if (conn.UnitBehaviorId != 0 && conn.UnitBehaviorId <= ushort.MaxValue)
                return (ushort)conn.UnitBehaviorId;
            return conn.BehaviorComponentId;
        }

        private void ClearUseTargetAndReleaseControl(RRConnection conn, string source = "unknown", ushort componentId = 0, bool sendClientControlReset = true, bool requireActiveUseTargetForReset = false)
        {
            if (conn == null) return;
            bool hadUseTarget = conn.HasActiveUseTarget;
            ushort targetId = conn.ActiveUseTargetId;
            byte sessionId = conn.ActiveUseTargetSessionId;
            ushort controlComponentId = ResolveClientControlComponentId(conn, componentId);
            ClearUseTarget(conn);
            _useTargetApproachLogTimes.Remove($"{conn.ConnId}:{targetId}:{sessionId}");
            Combat.WeaponCycleTracker.Instance.ClearConnection(conn.ConnId.ToString());
            if (conn.Avatar != null)
                Combat.CombatManager.Instance.SetPlayerActiveClientAttack((uint)conn.Avatar.Id, false);

            if (sendClientControlReset && (!requireActiveUseTargetForReset || hadUseTarget) && controlComponentId != 0 && conn.IsConnected)
            {
                Debug.LogError($"[CONTROL] Release UseTarget source={source} target={targetId} hadUseTarget={hadUseTarget} componentId=0x{controlComponentId:X4}");
                SendClientControlReset(conn, controlComponentId);
            }
            else
            {
                Debug.LogError($"[CONTROL] Release UseTarget source={source} target={targetId} hadUseTarget={hadUseTarget} no-reset componentId=0x{controlComponentId:X4} connected={conn.IsConnected}");
            }
        }

        private void TryClearUseTargetAndReleaseControl(RRConnection conn, string source)
        {
            if (conn == null) return;
            try
            {
                ClearUseTargetAndReleaseControl(conn, source);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CONTROL-ERROR] {source}: {ex.Message}\n{ex.StackTrace}");
                ClearUseTarget(conn);
            }
        }

        private void ReleaseCompletedUseTargets()
        {
            foreach (var conn in _connections.Values)
                ReleaseCompletedUseTarget(conn);
        }

        private void ReleaseCompletedUseTarget(RRConnection conn)
        {
            if (conn == null || !conn.HasActiveUseTarget) return;
            var monster = CombatManager.Instance.GetMonster(conn.ActiveUseTargetId)
                       ?? CombatManager.Instance.GetMonsterByComponent(conn.ActiveUseTargetId);
            if (monster == null || !monster.IsAlive || CombatManager.Instance.PeekMonsterCurrentHPWire(monster) == 0 || CombatManager.Instance.IsMonsterDeathPendingClientConfirmation(monster))
            {
                string state = monster == null ? "missing" : $"alive={monster.IsAlive} hp={CombatManager.Instance.PeekMonsterCurrentHPWire(monster)}";
                Debug.LogError($"[CONTROL] Releasing completed UseTarget target={conn.ActiveUseTargetId} {state}");
                ClearUseTargetAndReleaseControl(conn, "ReleaseCompletedUseTarget");
            }
        }

        private bool IsMeleeTargetWithinServerRange(RRConnection conn, PlayerState state, Combat.Monster monster, out float distance, out float allowedRange)
        {
            distance = 0f;
            allowedRange = 0f;
            if (conn == null || monster == null) return false;
            allowedRange = CombatManager.Instance.ResolvePlayerMeleeRange(state, monster);
            ResolveMonsterClientVisiblePosition(monster, out float targetX, out float targetY);
            float dx = targetX - conn.PlayerPosX;
            float dy = targetY - conn.PlayerPosY;
            distance = Mathf.Sqrt(dx * dx + dy * dy);
            return distance <= allowedRange + NATIVE_CONTACT_RANGE_EPSILON;
        }

        private bool IsRangedProjectileTargetWithinServerRange(RRConnection conn, PlayerState state, Combat.Monster monster, out float distance, out float allowedRange)
        {
            distance = 0f;
            allowedRange = 0f;
            if (conn == null || monster == null) return false;
            allowedRange = CombatManager.Instance.ResolveNativeUseTargetInitUseRange(state, monster, out _, out _);
            ResolveMonsterClientVisiblePosition(monster, out float targetX, out float targetY);
            float dx = targetX - conn.PlayerPosX;
            float dy = targetY - conn.PlayerPosY;
            distance = Mathf.Sqrt(dx * dx + dy * dy);
            return distance <= allowedRange + NATIVE_CONTACT_RANGE_EPSILON;
        }

        private bool IsRangedBasicTargetWithinServerRange(RRConnection conn, PlayerState state, Combat.Monster monster, out float distance, out float allowedRange)
        {
            return IsRangedProjectileTargetWithinServerRange(conn, state, monster, out distance, out allowedRange);
        }

        private Combat.SpellData ResolveActionSpell(RRConnection conn, PlayerState state, byte manipulatorId)
        {
            Combat.SpellDatabase.Initialize();
            var spell = ResolveSpellFromManip(conn, manipulatorId);
            if (spell == null && state != null)
                spell = Combat.SpellDatabase.GetSpellForClass(state.ClassName);
            return spell;
        }

        private bool IsSkillTargetWithinServerRange(RRConnection conn, Combat.SpellData spell, Combat.Monster monster, out float distance, out float allowedRange)
        {
            distance = 0f;
            allowedRange = 0f;
            if (conn == null || monster == null || spell == null || spell.Range <= 0) return false;
            allowedRange = Mathf.Max(1f, spell.Range) + 14f;
            ResolveMonsterClientVisiblePosition(monster, out float targetX, out float targetY);
            float dx = targetX - conn.PlayerPosX;
            float dy = targetY - conn.PlayerPosY;
            distance = Mathf.Sqrt(dx * dx + dy * dy);
            return distance <= allowedRange;
        }

        private bool IsSpellPositionWithinServerRange(RRConnection conn, Combat.SpellData spell, float posX, float posY, out float distance, out float allowedRange)
        {
            distance = 0f;
            allowedRange = 0f;
            if (conn == null || spell == null || spell.Range <= 0) return false;
            allowedRange = Mathf.Max(1f, spell.Range) + 14f;
            float dx = posX - conn.PlayerPosX;
            float dy = posY - conn.PlayerPosY;
            distance = Mathf.Sqrt(dx * dx + dy * dy);
            return distance <= allowedRange;
        }

        private Combat.Monster ResolvePositionSpellTarget(RRConnection conn, Combat.SpellData spell, float posX, float posY, out float projectileHitDistance)
        {
            projectileHitDistance = 0f;
            if (spell != null && spell.ProjectileSize > 0f)
                return FindFirstProjectileMonsterHit(conn, spell, posX, posY, out projectileHitDistance);

            return Combat.CombatManager.Instance.GetNearestMonster(posX, posY, 50f, GetInstanceZoneKey(conn));
        }

        private Combat.Monster ResolvePositionSpellTargetFromStart(RRConnection conn, Combat.SpellData spell, float startX, float startY, float posX, float posY, out float projectileHitDistance)
        {
            projectileHitDistance = 0f;
            if (spell != null && spell.ProjectileSize > 0f)
                return FindFirstProjectileMonsterHitFromStart(conn, spell, startX, startY, posX, posY, out projectileHitDistance);

            return Combat.CombatManager.Instance.GetNearestMonster(posX, posY, 50f, GetInstanceZoneKey(conn));
        }

        private float ResolveProjectileImpactDelay(Combat.SpellData spell, float projectileHitDistance)
        {
            if (spell == null || spell.ProjectileSize <= 0f || spell.ProjectileSpeed <= 0f)
                return 0f;
            return Combat.WeaponCycleTracker.NativeProjectileImpactDelaySeconds(projectileHitDistance, spell.ProjectileSpeed);
        }

        private float ResolveProjectileMaxDistance(Combat.SpellData spell, float aimDistance)
        {
            if (spell == null || spell.ProjectileSpeed <= 0f)
                return Mathf.Max(0f, aimDistance);
            if (spell.ProjectileLifespan > 0f)
                return Mathf.Max(0f, spell.ProjectileSpeed * spell.ProjectileLifespan * COMBAT_TICK);
            float rangeDistance = spell.Range > 0 ? Mathf.Max(0f, spell.Range + 14f) : 0f;
            return Mathf.Max(Mathf.Max(0f, aimDistance), rangeDistance);
        }

        private PathMap ResolveProjectilePathMap(RRConnection conn, Combat.Monster monster)
        {
            string zoneName = monster?.ZoneName;
            if (string.IsNullOrWhiteSpace(zoneName))
                zoneName = conn?.CurrentZoneName;
            string pathMapKey = !string.IsNullOrWhiteSpace(monster?.InstanceKey)
                ? monster.InstanceKey
                : (conn != null ? GetInstanceZoneKey(conn) : null);
            if (string.IsNullOrWhiteSpace(pathMapKey))
                pathMapKey = zoneName;
            return !string.IsNullOrWhiteSpace(pathMapKey) ? PathMapManager.Instance.GetPathMap(pathMapKey) : null;
        }

        private void LogProjectilePathMapForUnitFirst(PathMap pathMap, Combat.SpellData spell, float startX, float startY, float pathX, float pathY, float pathLen, float impactDistance, Combat.Monster monster, bool predictedMove)
        {
            if (pathMap == null)
                return;
            if (pathLen <= 0.001f)
                return;

            float clampedDistance = Mathf.Clamp(impactDistance, 0f, pathLen);
            float t = clampedDistance / pathLen;
            float impactX = startX + pathX * t;
            float impactY = startY + pathY * t;
            if (pathMap.CanReachPoint(startX, startY, impactX, impactY))
                return;

            string spellName = spell?.DisplayName ?? spell?.SkillId ?? "spell";
            string targetName = monster != null ? $"{monster.Name}#{monster.EntityId}" : "<none>";
            Debug.LogError($"[PROJECTILE-PATHMAP-DIAG] {spellName} path=({startX:F1},{startY:F1})->impact=({impactX:F1},{impactY:F1}) wouldBlock=True target={targetName} predictedMove={predictedMove} nativeUnitFirst=True diagnosticOnly=True native=ProjectileChecker::testFirstTime->WorldCollisionManager-not-PathMap");
        }

        private Combat.Monster FindFirstProjectileMonsterHit(RRConnection conn, Combat.SpellData spell, float aimX, float aimY, out float projectileHitDistance)
        {
            projectileHitDistance = 0f;
            if (conn == null)
                return null;
            return FindFirstProjectileMonsterHitFromStart(conn, spell, conn.PlayerPosX, conn.PlayerPosY, aimX, aimY, out projectileHitDistance);
        }

        private Combat.Monster FindFirstProjectileMonsterHitFromStart(RRConnection conn, Combat.SpellData spell, float startX, float startY, float aimX, float aimY, out float projectileHitDistance)
        {
            projectileHitDistance = 0f;
            if (conn == null || spell == null || spell.ProjectileSize <= 0f)
                return null;

            float pathX = aimX - startX;
            float pathY = aimY - startY;
            float pathLenSq = pathX * pathX + pathY * pathY;
            if (pathLenSq <= 0.0001f)
                return null;
            float pathLen = Mathf.Sqrt(pathLenSq);
            float projectileMaxDistance = ResolveProjectileMaxDistance(spell, pathLen);
            if (projectileMaxDistance <= 0.001f)
                return null;
            float projectileDirX = pathX / pathLen;
            float projectileDirY = pathY / pathLen;
            float projectilePathX = projectileDirX * projectileMaxDistance;
            float projectilePathY = projectileDirY * projectileMaxDistance;

            Combat.Monster best = null;
            float bestImpactDistance = float.MaxValue;
            float bestDistSq = float.MaxValue;
            bool bestPredictedMove = false;
            float bestHitTime = 0f;
            string instanceKey = GetInstanceZoneKey(conn);

            foreach (var monster in Combat.CombatManager.Instance.GetActiveMonsters())
            {
                if (monster == null || !monster.IsAlive)
                    continue;
                if (!Combat.CombatManager.Instance.MatchesInstance(monster, instanceKey))
                    continue;
                if (Combat.CombatManager.Instance.PeekMonsterCurrentHPWire(monster) == 0)
                    continue;

                PathMap projectilePathMap = ResolveProjectilePathMap(conn, monster);
                Combat.CombatManager.Instance.TryGetMonsterClientVisiblePosition(monster, GetNativeCombatNow(), out float visibleMonsterX, out float visibleMonsterY);
                float monsterX = visibleMonsterX - startX;
                float monsterY = visibleMonsterY - startY;
                float projectedDistance = monsterX * projectileDirX + monsterY * projectileDirY;
                float hitRadius = WeaponCycleTracker.NativeProjectileCollisionRadius(monster.CollisionRadius, spell.ProjectileSize);
                float hitRadiusSq = hitRadius * hitRadius;

                if (projectedDistance + hitRadius >= 0f && projectedDistance - hitRadius <= projectileMaxDistance)
                {
                    float closestDistance = Mathf.Clamp(projectedDistance, 0f, projectileMaxDistance);
                    float closestX = startX + (projectileDirX * closestDistance);
                    float closestY = startY + (projectileDirY * closestDistance);
                    float dx = visibleMonsterX - closestX;
                    float dy = visibleMonsterY - closestY;
                    float distSq = dx * dx + dy * dy;

                    if (distSq <= hitRadiusSq)
                    {
                        float entryOffset = Mathf.Sqrt(Mathf.Max(0f, hitRadiusSq - distSq));
                        float impactDistance = Mathf.Clamp(projectedDistance - entryOffset, 0f, projectileMaxDistance);
                        LogProjectilePathMapForUnitFirst(projectilePathMap, spell, startX, startY, projectilePathX, projectilePathY, projectileMaxDistance, impactDistance, monster, false);
                        if (impactDistance < bestImpactDistance || (Mathf.Abs(impactDistance - bestImpactDistance) <= 0.0001f && distSq < bestDistSq))
                        {
                            best = monster;
                            bestImpactDistance = impactDistance;
                            bestDistSq = distSq;
                            bestPredictedMove = false;
                            bestHitTime = 0f;
                        }
                    }
                }

                if (TryResolveMovingProjectileMonsterHit(conn, monster, spell, startX, startY, projectilePathX, projectilePathY, projectileMaxDistance, hitRadius, out float movingImpactDistance, out float movingDistSq, out float movingHitTime))
                {
                    LogProjectilePathMapForUnitFirst(projectilePathMap, spell, startX, startY, projectilePathX, projectilePathY, projectileMaxDistance, movingImpactDistance, monster, true);
                    if (movingImpactDistance < bestImpactDistance || (Mathf.Abs(movingImpactDistance - bestImpactDistance) <= 0.0001f && movingDistSq < bestDistSq))
                    {
                        best = monster;
                        bestImpactDistance = movingImpactDistance;
                        bestDistSq = movingDistSq;
                        bestPredictedMove = true;
                        bestHitTime = movingHitTime;
                    }
                }
            }

            if (best == null)
            {
                foreach (var monster in Combat.CombatManager.Instance.GetActiveMonsters())
                {
                    if (monster == null || !monster.IsAlive)
                        continue;
                    if (!Combat.CombatManager.Instance.MatchesInstance(monster, instanceKey))
                        continue;
                    if (Combat.CombatManager.Instance.PeekMonsterCurrentHPWire(monster) == 0)
                        continue;

                    Combat.CombatManager.Instance.TryGetMonsterClientVisiblePosition(monster, GetNativeCombatNow(), out float visibleMonsterX, out float visibleMonsterY);
                    float hitRadius = WeaponCycleTracker.NativeProjectileCollisionRadius(monster.CollisionRadius, spell.ProjectileSize);
                    float dx = visibleMonsterX - aimX;
                    float dy = visibleMonsterY - aimY;
                    float endpointDistSq = dx * dx + dy * dy;
                    if (endpointDistSq > hitRadius * hitRadius)
                        continue;

                    float monsterX = visibleMonsterX - startX;
                    float monsterY = visibleMonsterY - startY;
                    float projectedDistance = monsterX * projectileDirX + monsterY * projectileDirY;
                    if (projectedDistance < 0f || projectedDistance > projectileMaxDistance + hitRadius)
                        continue;

                    best = monster;
                    bestImpactDistance = Mathf.Clamp(projectedDistance, 0f, projectileMaxDistance);
                    bestDistSq = endpointDistSq;
                    bestPredictedMove = false;
                    bestHitTime = 0f;
                    Debug.LogError($"[PROJECTILE-HIT] {spell.DisplayName ?? spell.SkillId ?? "spell"} endpoint unit-first fallback aim=({aimX:F1},{aimY:F1}) hit={monster.Name}#{monster.EntityId} dist={Mathf.Sqrt(endpointDistSq):F2} radius={hitRadius:F2} projected={projectedDistance:F2}");
                    break;
                }
            }

            if (best != null)
            {
                Combat.CombatManager.Instance.SyncMonsterWanderClientVisiblePosition(best, "ProjectileChecker-hit");
                projectileHitDistance = bestImpactDistance;
                string predictedMove = bestPredictedMove ? $" predictedMove=True hitTime={bestHitTime:F3}s" : string.Empty;
                string pastAim = bestImpactDistance > pathLen + 0.01f ? " pastAim=True" : string.Empty;
                Debug.LogError($"[PROJECTILE-HIT] {spell.DisplayName ?? spell.SkillId ?? "spell"} path=({startX:F1},{startY:F1})->aim=({aimX:F1},{aimY:F1}) hit={best.Name}#{best.EntityId} hitDist={bestImpactDistance:F2} aimDist={pathLen:F2} maxDist={projectileMaxDistance:F2} delay={ResolveProjectileImpactDelay(spell, bestImpactDistance):F3}s dist={Mathf.Sqrt(bestDistSq):F2} radius={WeaponCycleTracker.NativeProjectileCollisionRadius(best.CollisionRadius, spell.ProjectileSize):F2} projectileRadius={WeaponCycleTracker.NativeProjectileRadiusFromAuthoredSize(spell.ProjectileSize):F2}{pastAim}{predictedMove}");
            }
            else
            {
                Debug.LogError($"[PROJECTILE-HIT] {spell.DisplayName ?? spell.SkillId ?? "spell"} path=({startX:F1},{startY:F1})->aim=({aimX:F1},{aimY:F1}) no unit hit size={spell.ProjectileSize:F1} aimDist={pathLen:F2} maxDist={projectileMaxDistance:F2}");
            }

            return best;
        }

        private bool TryResolveMovingProjectileMonsterHit(
            RRConnection conn,
            Combat.Monster monster,
            Combat.SpellData spell,
            float startX,
            float startY,
            float pathX,
            float pathY,
            float pathLen,
            float hitRadius,
            out float impactDistance,
            out float distSq,
            out float hitTime)
        {
            impactDistance = 0f;
            distSq = float.MaxValue;
            hitTime = 0f;

            if (conn == null || conn.Avatar == null || monster == null || spell == null)
                return false;
            if (spell.ProjectileSpeed <= 0f || pathLen <= 0.001f || hitRadius <= 0f)
                return false;

            uint playerEntityId = (uint)conn.Avatar.Id;
            if (!monster.AggroTriggered || monster.TargetId != playerEntityId || monster.AttackPending)
                return false;

            float targetX = conn.PlayerPosX;
            float targetY = conn.PlayerPosY;
            ResolveMonsterClientVisiblePosition(monster, out float monsterStartX, out float monsterStartY);
            float moveX = targetX - monsterStartX;
            float moveY = targetY - monsterStartY;
            float moveLen = Mathf.Sqrt(moveX * moveX + moveY * moveY);
            if (moveLen <= 0.001f)
                return false;

            float stopRange = Combat.CombatManager.Instance.GetMonsterEffectiveAttackRange(monster);
            float moveLimit = Mathf.Max(0f, moveLen - stopRange);
            if (moveLimit <= 0.001f)
                return false;

            float monsterSpeed = Combat.CombatManager.Instance.GetMonsterMovementSpeed(monster);
            if (monsterSpeed <= 0f)
                return false;

            float maxTime = pathLen / spell.ProjectileSpeed;
            if (maxTime <= 0f)
                return false;

            float projectileDirX = pathX / pathLen;
            float projectileDirY = pathY / pathLen;
            float moveDirX = moveX / moveLen;
            float moveDirY = moveY / moveLen;
            float hitRadiusSq = hitRadius * hitRadius;
            int samples = Mathf.Max(1, Mathf.CeilToInt(maxTime / COMBAT_TICK));

            for (int sample = 1; sample <= samples; sample++)
            {
                float t = Mathf.Min(maxTime, sample * COMBAT_TICK);
                float projectileDistance = Mathf.Min(pathLen, spell.ProjectileSpeed * t);
                float projectileX = startX + projectileDirX * projectileDistance;
                float projectileY = startY + projectileDirY * projectileDistance;
                float monsterDistance = Mathf.Min(moveLimit, monsterSpeed * t);
                float monsterX = monsterStartX + moveDirX * monsterDistance;
                float monsterY = monsterStartY + moveDirY * monsterDistance;
                float dx = monsterX - projectileX;
                float dy = monsterY - projectileY;
                float sampleDistSq = dx * dx + dy * dy;
                if (sampleDistSq <= hitRadiusSq)
                {
                    impactDistance = projectileDistance;
                    distSq = sampleDistSq;
                    hitTime = t;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Resolve manipulatorId → SpellData using the Op4-built mapping.
        /// Falls back to GCClass-based lookup if manipMap doesn't have it.
        /// </summary>
        private Combat.SpellData ResolveSpellFromManip(RRConnection conn, byte manipulatorId)
        {
            Combat.SpellDatabase.Initialize();
            string connKey = conn.ConnId.ToString();
            if (_playerManipMap.TryGetValue(connKey, out var map))
            {
                if (map.TryGetValue(manipulatorId, out string gcClass))
                {
                    // gcClass is like "skills.generic.PoisonBlastRadius" — try full name then short name
                    var spell = Combat.SpellDatabase.GetSpell(gcClass);
                    if (spell != null)
                    {
                        Debug.LogError($"[MANIP-RESOLVE] manipId={manipulatorId} → {gcClass} → {spell.DisplayName}");
                        return spell;
                    }
                    // Try extracting short name from gcClass (e.g. "skills.generic.PoisonBlastRadius" → "PoisonBlastRadius")
                    string shortName = gcClass;
                    int lastDot = gcClass.LastIndexOf('.');
                    if (lastDot >= 0) shortName = gcClass.Substring(lastDot + 1);
                    spell = Combat.SpellDatabase.GetSpell(shortName);
                    if (spell != null)
                    {
                        Debug.LogError($"[MANIP-RESOLVE] manipId={manipulatorId} → {gcClass} → short={shortName} → {spell.DisplayName}");
                        return spell;
                    }
                    Debug.LogError($"[MANIP-RESOLVE] manipId={manipulatorId} → {gcClass} but NOT in SpellDatabase!");
                }
                else
                {
                    Debug.LogError($"[MANIP-RESOLVE] manipId={manipulatorId} NOT in manipMap (keys: {string.Join(",", map.Keys)})");
                }
            }
            else
            {
                Debug.LogError($"[MANIP-RESOLVE] No manipMap for connection {connKey}");
            }
            return null;
        }

        /// <summary>
        /// Handle skill training request from client.
        /// Binary-verified: vtable[54] @ 0x544F30 → SkillTrainer::processRequest
        /// subMessage 0x32 handler at 0x5450E0.
        ///
        /// Client → Server payload (9 bytes after subMessage 0x32):
        ///   uint32 skillIndex     — 0-based index into AvailableSkill list from GC
        ///   byte   entityRefType  — always 0x04 (uint32 entity ref)
        ///   uint32 entityId       — NPC instance ID (client-side, ignored by server)
        ///
        /// Server → Client response (same subMessage 0x32):
        ///   Echoes back: uint32 skillIndex + byte(0x04) + uint32(npcEntityId)
        ///   Client handler at 0x5450E0 reads this, resolves the skill,
        ///   deducts gold locally, and updates skill level in the UI.
        ///
        /// Cost formula (0x53F390, Fixed32 8.8 arithmetic):
        ///   cost = (GoldValueMod × nextLevel) >> 8
        ///   GC defaults: GoldValueMod = 1.0 (0x100 in Fixed32), MaxSkillLevel = 100
        /// </summary>
        /// <summary>
        /// Handle skill training request from client.
        /// Binary-verified: vtable[54] @ 0x544F30 → SkillTrainer::processRequest
        /// subMessage 0x32 handler at 0x5450E0.
        ///
        /// Client → Server payload (9 bytes after subMessage 0x32):
        ///   uint32 playerEntityId  — [playerSkills+0x80], always matches avatar ID (validation)
        ///   byte   entityRefType   — 0x04 (uint32 follows)
        ///   uint32 skillHash       — DJB2 hash of lowercase skill GC class path
        ///                            e.g. DJB2("skills.generic.blight") = 0x5E5B060A
        ///
        /// Verified against 4 live captures:
        ///   0xA6CCC405 = skills.generic.1HMeleeSpeedBuff (Fighter)
        ///   0xBC568FC5 = skills.generic.PoisonResistBuff (Ranger)
        ///   0x86501370 = skills.generic.Sprint (Ranger)
        ///   0x5E5B060A = skills.generic.Blight (Ranger)
        ///
        /// No response packet — client 0x35 handler at 0x5DB520 calls vtable[53]
        /// which is ret 8 (NOOP) for SkillTrainer. Any payload corrupts the stream.
        /// Server-side training takes effect in combat immediately; trainer UI
        /// updates on next dialog open/zone-in.
        ///
        /// Gold cost formula (binary 0x53F390, Fixed32 8.8):
        ///   cost = (RequiredLevel + (nextLevel-1) × GoldValueMod) × SkillValuePerLevel
        ///   SkillValuePerLevel = 1113.621 from GlobalKnobs
        /// </summary>
        private void HandleSkillTrainRequest(RRConnection conn, LEReader reader, ushort componentId, byte subMessage, ZoneNPC trainerNpc)
        {
            Debug.LogError($"[TRAINER] ═══════════════════════════════════════════════════════════");
            Debug.LogError($"[TRAINER] 🎓 SKILL TRAIN REQUEST from {conn.LoginName}");
            Debug.LogError($"[TRAINER]   NPC: {trainerNpc.Name} ({trainerNpc.GCClass}) cid=0x{componentId:X4}");

            // ── Parse the 9-byte payload ──
            if (reader.Remaining < 9)
            {
                Debug.LogError($"[TRAINER] ❌ Not enough data: {reader.Remaining} bytes (need 9)");
                if (reader.Remaining > 0) reader.ReadBytes(reader.Remaining);
                return;
            }

            uint playerEntityId = reader.ReadUInt32();  // [playerSkills+0x80] = avatar entity ID
            byte entityRefType = reader.ReadByte();      // 0x04 = uint32 hash follows
            uint skillHash = reader.ReadUInt32();         // DJB2 hash of lowercase skill GC path

            Debug.LogError($"[TRAINER]   playerEntityId=0x{playerEntityId:X} refType=0x{entityRefType:X2} skillHash=0x{skillHash:X8}");

            // Consume any trailing sync suffix
            if (reader.Remaining > 0)
            {
                byte[] trailing = reader.ReadBytes(reader.Remaining);
                Debug.LogError($"[TRAINER]   trailing sync: {BitConverter.ToString(trailing)}");
            }

            // ── Resolve skill hash to GC class via DJB2 lookup table ──
            if (!_skillHashToGcClass.TryGetValue(skillHash, out string skillGcClass))
            {
                Debug.LogError($"[TRAINER] ❌ Unknown skill hash 0x{skillHash:X8} — not in DJB2 table");
                return;
            }
            Debug.LogError($"[TRAINER]   Resolved: 0x{skillHash:X8} → {skillGcClass}");

            // ── Get saved character ──
            if (!_selectedCharacter.ContainsKey(conn.LoginName))
            {
                Debug.LogError($"[TRAINER] ❌ No selected character for {conn.LoginName}");
                return;
            }
            var savedChar = CharacterRepository.GetCharacter(_selectedCharacter[conn.LoginName].Id);
            if (savedChar == null)
            {
                Debug.LogError($"[TRAINER] ❌ SavedCharacter not found for {conn.LoginName}");
                return;
            }

            // ── Get current and next skill level ──
            int currentLevel = savedChar.GetSkillLevel(skillGcClass);
            string connKey = conn.ConnId.ToString();
            bool playerHasSkill = false;
            if (_playerSkillLevels.TryGetValue(connKey, out var existingLevels))
                playerHasSkill = existingLevels.ContainsKey(skillGcClass);
            if (!playerHasSkill && savedChar.skills != null)
                playerHasSkill = savedChar.skills.Contains(skillGcClass);

            int nextLevel = playerHasSkill ? currentLevel + 1 : 1;

            // ── Look up GC-authoritative skill data ──
            float goldValueMod = 1.0f;
            int requiredLevel = 1;
            int maxSkillLevel = 100;
            int requiredLevelInc = 1;
            if (_skillTrainData.TryGetValue(skillGcClass, out var trainData))
            {
                goldValueMod = trainData.goldValueMod;
                requiredLevel = trainData.requiredLevel;
                requiredLevelInc = trainData.requiredLevelInc;
                maxSkillLevel = trainData.maxSkillLevel;
            }
            else
            {
                Debug.LogError($"[TRAINER] ⚠️ No GC cost data for {skillGcClass} — using defaults");
            }

            if (nextLevel > maxSkillLevel)
            {
                Debug.LogError($"[TRAINER] ❌ Already at max level {currentLevel}/{maxSkillLevel} for {skillGcClass}");
                return;
            }

            // ── Calculate gold cost (binary 0x53F390, Fixed32 8.8, 3 stages) ──
            // Stage 1: (RequiredLevel + (nextLevel-1) × GoldValueMod)
            // Stage 2: × SkillValuePerLevel (1113.621 from GlobalKnobs)
            // Stage 3: × GoldValueMod again ([skillDef+0xC0] = overall multiplier)
            // Verified: 1H/2HMeleeSpeedBuff (ReqLvl=5, GVM=1.75) = 5×1113.621×1.75 = 9744
            int goldCost = (int)((requiredLevel + (nextLevel - 1) * goldValueMod) * SKILL_VALUE_PER_LEVEL * goldValueMod);
            if (goldCost < 1) goldCost = 1;

            Debug.LogError($"[TRAINER]   Level: {currentLevel} → {nextLevel} (max {maxSkillLevel})");
            Debug.LogError($"[TRAINER]   Gold cost: {goldCost} | Player gold: {savedChar.gold}");

            // ── Check gold ──
            if (savedChar.gold < (uint)goldCost)
            {
                Debug.LogError($"[TRAINER] ❌ Not enough gold: have {savedChar.gold}, need {goldCost}");
                return;
            }

            // ── Deduct gold ──
            savedChar.gold -= (uint)goldCost;
            Debug.LogError($"[TRAINER]   💰 Gold: {savedChar.gold + goldCost} → {savedChar.gold}");

            // ── Update skill level ──
            savedChar.SetSkillLevel(skillGcClass, nextLevel);
            if (!playerHasSkill)
            {
                if (savedChar.skills == null)
                    savedChar.skills = new List<string>();
                if (!savedChar.skills.Contains(skillGcClass))
                    savedChar.skills.Add(skillGcClass);
                Debug.LogError($"[TRAINER]   📚 Learned NEW skill: {skillGcClass}");
            }

            // ── Update runtime skill level tracking ──
            if (!_playerSkillLevels.ContainsKey(connKey))
                _playerSkillLevels[connKey] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _playerSkillLevels[connKey][skillGcClass] = nextLevel;
            int lastDot = skillGcClass.LastIndexOf('.');
            if (lastDot >= 0)
                _playerSkillLevels[connKey][skillGcClass.Substring(lastDot + 1)] = nextLevel;

            // ── Save character to disk ──
            CharacterRepository.SaveCharacter(savedChar);
            Debug.LogError($"[TRAINER]   💾 Character saved");

            // ═══════════════════════════════════════════════════════════════
            // SEND CLIENT FEEDBACK
            // Binary-verified: Skills::processUpdate at 0x541D60
            // Jump table (subMessage - 0x32):
            //   0x32 → processUpdateSkill: entityRef + byte level
            //          Path 1: findSkill succeeds → update Skill+0x75, event 0x141E
            //          Path 2: findSkill fails → readObject + addSkill, event 0x1421
            //   0x33 → processUpdateSkillPoints: uint32 points → Skills+0x70, event 0x141F
            //   0x37 → processUpdateAddProfession: avatarRef + readObject → addSkill, event 0x1422
            //
            // Response goes to PLAYER's Skills component (NOT SkillTrainer!)
            // ═══════════════════════════════════════════════════════════════

            ushort skillsCid = 0;
            _playerSkillsComponentId.TryGetValue(connKey, out skillsCid);
            Debug.LogError($"[TRAINER-RESPONSE] connKey={connKey} skillsCid=0x{skillsCid:X4}");

            if (skillsCid != 0)
            {
                // ═══════════════════════════════════════════════════════
                // COMBINED PACKET: gold (0x33) + level (0x32) in ONE stream
                // Both must be in the same BeginStream/EndStream so the client
                // processes gold write BEFORE the level update fires initialize()
                // ═══════════════════════════════════════════════════════

                // ── Look up skill entity ID from spawn slot map ──
                uint skillEntityId = 0;
                bool hasSlot = false;
                if (_playerSkillSlots.TryGetValue(connKey, out var slots))
                {
                    hasSlot = slots.TryGetValue(skillGcClass, out skillEntityId);
                    if (!hasSlot)
                    {
                        string sn = skillGcClass;
                        int dot = sn.LastIndexOf('.');
                        if (dot >= 0) sn = sn.Substring(dot + 1);
                        hasSlot = slots.TryGetValue(sn, out skillEntityId);
                    }
                    Debug.LogError($"[TRAINER-RESPONSE] Slot lookup: '{skillGcClass}' → entityId={skillEntityId} found={hasSlot}");
                    Debug.LogError($"[TRAINER-RESPONSE] All slots: {string.Join(", ", slots.Select(kv => $"{kv.Key}={kv.Value}"))}");
                }
                else
                {
                    Debug.LogError($"[TRAINER-RESPONSE] ⚠️ No slot map for connKey={connKey}");
                }

                {
                    var combined = new LEWriter();
                    combined.WriteByte(0x07);                // BeginStream (ONE stream for both)

                    // ── Part 1: 0x33 processUpdateSkillPoints (gold) ──
                    combined.WriteByte(0x35);                // ComponentUpdate
                    combined.WriteUInt16(skillsCid);
                    combined.WriteByte(0x33);                // subMessage = gold
                    combined.WriteUInt32(savedChar.gold);
                    WritePlayerEntitySynch(conn, combined);

                    // ── Part 2: 0x32 processUpdateSkill (level) ──
                    combined.WriteByte(0x35);                // ComponentUpdate
                    combined.WriteUInt16(skillsCid);
                    combined.WriteByte(0x32);                // subMessage = level
                    combined.WriteByte(0xFF);                // entity ref = name-based
                    combined.WriteCString(skillGcClass);     // ORIGINAL CASE
                    combined.WriteByte((byte)nextLevel);     // new level
                    WritePlayerEntitySynch(conn, combined);

                    combined.WriteByte(0x06);                // EndStream

                    byte[] pktCombined = combined.ToArray();
                    Debug.LogError($"[TRAINER-COMBINED] hex ({pktCombined.Length}b): {BitConverter.ToString(pktCombined)}");
                    SendCompressedE(conn, pktCombined);
                    Debug.LogError($"[TRAINER-COMBINED] ✅ Sent gold={savedChar.gold} + level={nextLevel} for '{skillGcClass}'");
                }

                // ── Gold update via UnitContainer (same as MerchantManager.HandleBuyItem) ──
                // This is the REAL gold display update — merchant uses this and it works in real time.
                // Skills+0x70 (0x33) is an internal counter; UnitContainer 0x21 is what the UI reads.
                if (conn.UnitContainerId != 0)
                {
                    var goldWriter = new LEWriter();
                    goldWriter.WriteByte(0x07);  // BeginStream
                    goldWriter.WriteByte(0x35);  // ComponentUpdate
                    goldWriter.WriteUInt16(conn.UnitContainerId);
                    goldWriter.WriteByte(0x20);          // AddCurrency (unconditional — 0x21 is gated)
                    goldWriter.WriteInt32(-goldCost);    // negative = subtract via two's complement
                    goldWriter.WriteByte(0x00);          // source
                    goldWriter.WriteUInt32(0x00000000);  // entityHandle
                    goldWriter.WriteByte(0x01);          // notifyFlag (triggers gold jingle 0x138A)
                    WritePlayerEntitySynch(conn, goldWriter);
                    goldWriter.WriteByte(0x06);
                    byte[] goldPacket = goldWriter.ToArray();
                    Debug.LogError($"[TRAINER-GOLD] 💰 RemoveCurrency {goldCost} via UnitContainer 0x{conn.UnitContainerId:X4}");
                    SendCompressedA(conn, 0x01, 0x0F, goldPacket);  // same transport as merchant
                }
                else
                {
                    Debug.LogError($"[TRAINER-GOLD] ⚠️ UnitContainerId=0, can't send gold update");
                }
            }
            else
            {
                Debug.LogError($"[TRAINER-RESPONSE] ⚠️ SkillsComponentId=0 for {connKey} — rezone needed");
            }

            Debug.LogError($"[TRAINER] ✅ TRAINED {skillGcClass} to Lv{nextLevel} for {conn.LoginName}");
            Debug.LogError($"[TRAINER] ═══════════════════════════════════════════════════════════");

            // ── Add new skill to Manipulators in-memory (combat works immediately) ──
            if (!playerHasSkill)
            {
                bool isPassive = skillGcClass.ToLower().Contains("passive") || skillGcClass.ToLower().Contains("trait");
                if (!isPassive)
                {
                    var manip = conn.Avatar?.Children?.FirstOrDefault(c => c.GCClass == "Manipulators");
                    if (manip != null)
                    {
                        var newSkill = new GCObject
                        {
                            GCClass = skillGcClass,
                            NativeClass = "ActiveSkill",
                            Name = skillGcClass,
                            Id = _nextEntityId++
                        };
                        manip.AddChild(newSkill);
                        if (_playerSkillSlots.TryGetValue(connKey, out var slotMap))
                            slotMap[skillGcClass] = (uint)newSkill.Id;
                        if (_playerManipMap.TryGetValue(connKey, out var manipMap))
                        {
                            uint nextManipId = 100;
                            foreach (var k in manipMap.Keys)
                                if (k >= nextManipId) nextManipId = k + 1;
                            manipMap[nextManipId] = skillGcClass;
                        }
                        Debug.LogError($"[TRAINER] Added '{skillGcClass}' to Manipulators, eid={newSkill.Id}");
                    }
                }
            }

            // ── Send 0xDF to DSOUND.dll on client port 2605 ──
            // Get client IP from TCP connection (always available), no UDP session needed
            try
            {
                var tcpEP = conn.Client.Client.RemoteEndPoint as System.Net.IPEndPoint;
                if (tcpEP == null)
                {
                    Debug.LogError("[TRAINER-DLL] Cannot get client IP from TCP connection");
                }
                else
                {
                    byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(skillGcClass);
                    byte[] dfPacket = new byte[10 + nameBytes.Length];
                    dfPacket[0] = 0xDF;
                    // Look up skill entity ID from slot map
                    ushort skillEntityIdForDll = 0;
                    if (_playerSkillSlots.TryGetValue(connKey, out var dllSlots))
                    {
                        uint eid = 0;
                        if (dllSlots.TryGetValue(skillGcClass, out eid))
                            skillEntityIdForDll = (ushort)eid;
                    }
                    BitConverter.GetBytes(skillEntityIdForDll).CopyTo(dfPacket, 1);
                    BitConverter.GetBytes(savedChar.gold).CopyTo(dfPacket, 3);
                    dfPacket[7] = (byte)nextLevel;
                    dfPacket[8] = (byte)(playerHasSkill ? 0 : 1);
                    dfPacket[9] = (byte)nameBytes.Length;
                    Array.Copy(nameBytes, 0, dfPacket, 10, nameBytes.Length);
                    SendToDll(conn, dfPacket);
                    Debug.LogError($"[TRAINER-DLL] Sent 0xDF ({dfPacket.Length}b): eid={skillEntityIdForDll} '{skillGcClass}' lv={nextLevel} gold={savedChar.gold}");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[TRAINER-DLL] Send failed: {ex.Message}");
            }

            // ── System message ──
            string shortName = skillGcClass;
            int dotIdx = skillGcClass.LastIndexOf('.');
            if (dotIdx >= 0) shortName = skillGcClass.Substring(dotIdx + 1);
            SendSystemMessage(conn, playerHasSkill
                ? $"{shortName} → Rank {nextLevel}! ({savedChar.gold} gold)"
                : $"Learned {shortName}! ({savedChar.gold} gold)");
        }

        /// <summary>
        /// Get current skill level for a player's spell.
        /// Returns 1 if no level tracking exists yet (default starting level).
        /// </summary>
        private int GetPlayerSkillLevel(RRConnection conn, Combat.SpellData spell)
        {
            string connKey = conn.ConnId.ToString();
            if (_playerSkillLevels.TryGetValue(connKey, out var levels))
            {
                // Try both ShortName and SkillId
                if (levels.TryGetValue(spell.ShortName, out int lvl)) return lvl;
                if (!string.IsNullOrEmpty(spell.SkillId) && levels.TryGetValue(spell.SkillId, out lvl)) return lvl;
            }
            return 1; // Default skill level
        }

        /// <summary>
        /// Initialize skill levels for a player from their saved character data.
        /// Called during zone-in / character load.
        /// </summary>
        private void InitializePlayerSkillLevels(RRConnection conn, SavedCharacter savedChar)
        {
            string connKey = conn.ConnId.ToString();
            var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            if (savedChar.skills != null)
            {
                foreach (var skillGc in savedChar.skills)
                {
                    // Check if saved level exists, otherwise default to 1
                    int savedLevel = savedChar.GetSkillLevel(skillGc);

                    levels[skillGc] = savedLevel;
                    // Also add short name for lookup convenience
                    int lastDot = skillGc.LastIndexOf('.');
                    if (lastDot >= 0)
                        levels[skillGc.Substring(lastDot + 1)] = savedLevel;
                }
            }
            _playerSkillLevels[connKey] = levels;
            Debug.LogError($"[SKILL-LEVELS] Initialized {levels.Count} skill entries for {conn.LoginName}");
            foreach (var kvp in levels)
                Debug.LogError($"[SKILL-LEVELS]   {kvp.Key} = Lv{kvp.Value}");
        }

        private void HandleSpellAttack(RRConnection conn, PlayerState state, Combat.Monster monster, byte manipulatorId, byte useFlags, float aimX = 0, float aimY = 0, float nativeEffectTime = -1f)
        {
            Combat.SpellDatabase.Initialize();
            var spell = ResolveSpellFromManip(conn, manipulatorId);
            if (spell == null)
                spell = Combat.SpellDatabase.GetSpellForClass(state.ClassName);
            if (spell == null)
            {
                Debug.LogError($"[SPELL] No spell found for manipId={manipulatorId} class={state.ClassName}");
                return;
            }
            int skillLevel = GetPlayerSkillLevel(conn, spell);
            Debug.LogError($"[SPELL] {spell.DisplayName} (Lv{skillLevel}) cast by {conn.LoginName} on {monster.Name} (flags={useFlags} manip={manipulatorId}) AoE={spell.IsAoE}");

            // Track mana cost — ManaCostMod × level × 256 (wire format)
            float effectNow = nativeEffectTime >= 0f ? nativeEffectTime : GetNativeCombatNow();
            state.AdvanceClientSyncHP(effectNow, $"MANA-{spell.DisplayName}-pre-cost");
            uint manaCostWire = (uint)(spell.ManaCostMod * state.Level * 256);
            uint oldMana = state.CurrentManaWire;
            if (state.CurrentManaWire > manaCostWire)
                state.SetCurrentMana(state.CurrentManaWire - manaCostWire, $"spell:{spell.DisplayName}");
            else
                state.SetCurrentMana(0, $"spell:{spell.DisplayName}");
            Debug.LogError($"[MANA] {spell.DisplayName} cost={manaCostWire / 256} mana | {oldMana / 256} → {state.CurrentManaWire / 256} / {state.MaxManaWire / 256}");

            // Save mana to DB (mana only — don't touch XP/level)
            try
            {
                if (_selectedCharacter.ContainsKey(conn.LoginName))
                    using (var manaDb = GameDatabase.GetConnection())
                        GameDatabase.ExecuteNonQuery(manaDb,
                            "UPDATE characters SET current_mana=@mp WHERE id=@id",
                            ("@mp", (int)state.CurrentManaWire), ("@id", (int)_selectedCharacter[conn.LoginName].Id));
            }
            catch (System.Exception ex) { Debug.LogError($"[SAVE] current_mana persist failed for {conn.LoginName}: {ex.Message}"); }

            var rng = CombatManager.Instance.GetRoomRngForMonster(monster);
            ApplySpellDamageToMonster(conn, state, spell, monster, rng, skillLevel, false, effectNow);
            if (spell.IsChainSpell && monster != null)
                HandleChainSpell(conn, state, monster, spell, rng, skillLevel, effectNow);
        }

        private void ApplySpellDamageToMonster(RRConnection conn, PlayerState state,
            Combat.SpellData spell, Combat.Monster target, Combat.MersenneTwister rng,
            int skillLevel, bool isAoETarget, float nativeEffectTime = -1f)
        {
            if (spell == null || target == null) return;
            string tag = isAoETarget ? "SPELL-AOE" : "SPELL";
            if (rng == null)
            {
                Debug.LogError($"[{tag}] {spell.DisplayName} -> {target.Name}: room RNG unavailable, damage not applied");
                LogMonsterOnAttackedBlocked(conn, target, tag, "missing-rng-no-Damage::apply");
                return;
            }

            SpellWeaponDamageEffectResult weaponEffect = default;
            if (spell.HasImmediateWeaponDamageEffect)
            {
                weaponEffect = ApplySpellWeaponDamageEffect(conn, state, spell, target, rng, skillLevel, tag, nativeEffectTime);
                if (!weaponEffect.Landed)
                {
                    LogMonsterOnAttackedBlocked(conn, target, tag, "weapon-effect-not-landed");
                    return;
                }
                if (!weaponEffect.Applied)
                {
                    LogMonsterOnAttackedBlocked(conn, target, tag, "weapon-effect-not-applied");
                    return;
                }

                bool lethalWeaponDamage = weaponEffect.Died || weaponEffect.NewHPWire == 0 || CombatManager.Instance.PeekMonsterCurrentHPWire(target) == 0;
                if (lethalWeaponDamage)
                {
                    try
                    {
                        Debug.LogError($"[{tag}-KILL] Finalizing target={target.EntityId} hp={weaponEffect.OldHPWire}->{weaponEffect.NewHPWire} source=SpellWeaponDamageEffect");
                        bool finalized = TryFinalizeMonsterKill(conn, target, $"{tag}-weapon-kill");
                        if (state != null)
                            CommitPlayerHPTruth(conn, state, $"{tag}-WEAPON-KILL-AFTER-FINALIZE", state.CurrentHPWire, false, false);
                        Debug.LogError($"[{tag}-KILL] Finalize result target={target.EntityId} finalized={finalized} playerLevel={(state != null ? state.Level : 0)} playerHP={(state != null ? state.SynchHP : 0)}");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[{tag}-KILL] SpellWeaponDamageEffect finalize failed target={target.EntityId}: {ex}");
                    }
                    CombatManager.Instance.CancelMonsterPendingAttack(target, $"{tag}-weapon-kill");
                    if (IsUseTargetingMonster(conn, target))
                        ClearUseTargetAndReleaseControl(conn, $"{tag}-weapon-kill", sendClientControlReset: true, requireActiveUseTargetForReset: true);
                    return;
                }
            }

            if (spell.HasProjectileModifierDamage)
            {
                uint sourceEntityId = conn?.Avatar != null ? (uint)conn.Avatar.Id : 0;
                var modifierResult = CombatManager.Instance.ApplyProjectileModifierFromSpell(
                    target,
                    sourceEntityId,
                    state,
                    spell,
                    rng,
                    skillLevel,
                    nativeEffectTime >= 0f ? nativeEffectTime : GetNativeCombatNow(),
                    tag);

                if (!modifierResult.AppliedModifier)
                {
                    Debug.LogError($"[{tag}] {spell.DisplayName} -> {target.Name}: projectile modifier not applied reason={modifierResult.Reason} hp={CombatManager.Instance.PeekMonsterCurrentHPWire(target)} rngAfter={rng.CallsSinceReseed}");
                    LogMonsterOnAttackedBlocked(conn, target, tag, $"modifier-not-applied:{modifierResult.Reason}");
                    return;
                }

                Debug.LogError($"[POISON-SHOT-CHAIN] spell={spell.SkillId} projectileEffect={spell.ProjectileEffectId} modifier={spell.ProjectileModifierId} modifierEffect={spell.ProjectileModifierEffectId} ticks={modifierResult.TicksApplied} hp={modifierResult.OldHPWire}->{modifierResult.NewHPWire} rngAfterImpact={rng.CallsSinceReseed} status=modifier-attached-first-tick-deferred");
                LogMonsterOnAttackedBlocked(conn, target, tag, "modifier-attached-no-Damage::apply");
                return;
            }

            if (spell.HasImmediateWeaponDamageEffect && !spell.HasDirectDamageEffect)
            {
                return;
            }

            var result = Combat.DamageComputer.ProcessSpellAttack(
                rng,
                state.Level,
                state.Intelligence,
                state.Agility,
                state.Strength,
                state.WeaponDamage,
                state.WeaponDamageVolatility,
                spell,
                target,
                skillLevel,
                isAoETarget,
                Combat.DamageComputer.ResolveNativeCriticalDamagePercent(state));

            string resultName = result.Type.ToString().ToUpperInvariant();
            if (result.Type == Combat.AttackResultType.Miss || result.DamageF32 <= 0)
            {
                Debug.LogError($"[COMBAT-EVENT] actor=player-spell actorId={(conn?.Avatar != null ? conn.Avatar.Id : 0)} target=monster targetId={target.EntityId} result={resultName} damageWire=0 hp={CombatManager.Instance.PeekMonsterCurrentHPWire(target)}->{CombatManager.Instance.PeekMonsterCurrentHPWire(target)} spell={spell.DisplayName} rngAfter={rng.CallsSinceReseed} marker={tag}");
                return;
            }

            bool applied = CombatManager.Instance.ApplyNativePlayerDamageToMonsterWire(
                target,
                (uint)result.DamageF32,
                tag,
                out uint oldHPWire,
                out uint newHPWire,
                out bool died,
                nativeDamageTime: nativeEffectTime >= 0f ? nativeEffectTime : GetNativeCombatNow(),
                damageTypeId: result.DamageTypeId,
                rawDamageWire: (uint)result.DamageF32);

            if (!applied)
            {
                Debug.LogError($"[{tag}] {spell.DisplayName} -> {target.Name}: damage not applied alive={target.IsAlive} hp={CombatManager.Instance.PeekMonsterCurrentHPWire(target)}");
                return;
            }
            uint effectRaw = CombatManager.Instance.ConsumeNativeOnApplyDamageEffectRng(
                rng,
                "player-spell",
                target.EntityId,
                target.Name,
                oldHPWire,
                newHPWire,
                target.MaxHPWire,
                (uint)result.DamageF32,
                tag);
            if (state != null && oldHPWire > newHPWire)
                state.ApplyNativeOnDamageCallback(oldHPWire - newHPWire, nativeEffectTime >= 0f ? nativeEffectTime : GetNativeCombatNow(), tag);

            Debug.LogError($"[COMBAT-EVENT] actor=player-spell actorId={(conn?.Avatar != null ? conn.Avatar.Id : 0)} target=monster targetId={target.EntityId} result={resultName} damageWire={result.DamageF32} hp={oldHPWire}->{newHPWire} range=[{result.MinDamageF32},{result.MaxDamageF32}] damageRaw=0x{result.DamageRaw:X8} effectRaw=0x{effectRaw:X8} spell={spell.DisplayName} rngAfter={rng.CallsSinceReseed} marker={tag}");
            NotifyMonsterDamagedByConnection(conn, target, tag);

            bool lethalSpellDamage = died || newHPWire == 0 || CombatManager.Instance.PeekMonsterCurrentHPWire(target) == 0;
            if (lethalSpellDamage)
            {
                if (!died)
                    Debug.LogError($"[{tag}-KILL] Lethal HP reached without died flag target={target.EntityId} hp={oldHPWire}->{newHPWire}");
                try
                {
                    Debug.LogError($"[{tag}-KILL] Finalizing target={target.EntityId} hp={oldHPWire}->{newHPWire}");
                    bool finalized = TryFinalizeMonsterKill(conn, target, $"{tag}-kill");
                    if (state != null)
                        CommitPlayerHPTruth(conn, state, $"{tag}-KILL-AFTER-FINALIZE", state.CurrentHPWire, false, false);
                    Debug.LogError($"[{tag}-KILL] Finalize result target={target.EntityId} finalized={finalized} playerLevel={(state != null ? state.Level : 0)} playerHP={(state != null ? state.SynchHP : 0)}");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[{tag}-KILL] Finalize failed target={target.EntityId}: {ex}");
                }
                CombatManager.Instance.CancelMonsterPendingAttack(target, $"{tag}-kill");
                if (IsUseTargetingMonster(conn, target))
                    ClearUseTargetAndReleaseControl(conn, $"{tag}-kill", sendClientControlReset: true, requireActiveUseTargetForReset: true);
                return;
            }
        }

        private SpellWeaponDamageEffectResult ApplySpellWeaponDamageEffect(
            RRConnection conn,
            PlayerState state,
            Combat.SpellData spell,
            Combat.Monster target,
            Combat.MersenneTwister rng,
            int skillLevel,
            string tag,
            float nativeEffectTime)
        {
            var result = new SpellWeaponDamageEffectResult
            {
                Attempted = true,
                OldHPWire = target != null ? CombatManager.Instance.PeekMonsterCurrentHPWire(target) : 0,
                NewHPWire = target != null ? CombatManager.Instance.PeekMonsterCurrentHPWire(target) : 0,
                ResultName = "MISS"
            };

            if (state == null || spell == null || target == null || rng == null)
            {
                Debug.LogError($"[SPELL-WEAPON-DAMAGE] skipped spell={spell?.DisplayName ?? "unknown"} target={target?.EntityId ?? 0} state={(state != null)} rng={(rng != null)} tag={tag ?? "SPELL"}");
                return result;
            }

            int arMod = ResolveSpellEffectPercent(spell.ARModMin, spell.ARModMax, spell.ARModInc, skillLevel, 100);
            int damageModRaw = ResolveSpellEffectRawMod(spell.WeaponEffectDamageModMin, spell.WeaponEffectDamageModMax, spell.WeaponEffectDamageModInc, skillLevel);
            int baseAttackRating = DamageComputer.ResolveNativeAvatarAttackRating(state);
            int attackRating = Mathf.Clamp((baseAttackRating * Math.Max(0, arMod)) / 100, 0, 0xFFFF);
            int baseDamageMod = DamageComputer.ResolveNativeDamageMod(state);
            int damagePct = Math.Max(0, 100 + damageModRaw);
            int damageMod = Mathf.Clamp((baseDamageMod * damagePct) / 100, 0, 0xFFFF);
            int attackerLevel = Math.Max(0, state.Level);
            int defenderLevel = Math.Max(0, (int)target.Level);

            var damageInput = new NativeWeaponDamageInput
            {
                Rng = rng,
                Source = $"{tag ?? "SPELL"}-SpellWeaponDamageEffect",
                AttackerLevel = attackerLevel,
                DefenderLevel = defenderLevel,
                AttackRating = attackRating,
                DefenseRating = DamageComputer.ResolveNativeMonsterDefenseRating(target),
                BlockChance = 0,
                DamageLevel = DamageComputer.ResolveNativeWeaponDamageLevel(state),
                DamageBonus = DamageComputer.ResolveNativeWeaponDamageBonus(state),
                DamageMod = damageMod,
                WeaponClassId = DamageComputer.ResolveNativeWeaponClassId(state),
                DamageTypeId = DamageComputer.ResolveNativeDamageTypeId(state),
                WeaponDamageF32 = DamageComputer.GetWeaponBaseDamageF32(state),
                WeaponVolatilityF32 = DamageComputer.GetWeaponVolatilityF32(state),
                CritThreshold = DamageComputer.ResolveNativeCriticalThreshold(state, target),
                CritDamagePercent = DamageComputer.ResolveNativeCriticalDamagePercent(state),
                AttackerState = state,
                IncludeWeaponDamageAdds = true
            };

            DamageComputer.LogNativeDamageSlots(state, damageInput, target, damageInput.Source);
            NativeWeaponDamageResult damageResult = DamageComputer.ResolveNativeWeaponDamage(damageInput);
            result.HitRaw = damageResult.HitRaw;
            result.BlockRaw = damageResult.BlockRaw;
            result.DamageRaw = damageResult.DamageRaw;
            result.HitRoll = damageResult.HitRoll;
            result.BlockRoll = damageResult.BlockRoll;
            result.HitThreshold = damageResult.HitThreshold;
            result.AttackRating = damageResult.AttackRating;
            result.DefenseRating = damageResult.DefenseRating;
            result.DamageMod = damageMod;
            result.SkillDamageModRaw = damageModRaw;
            result.ARMod = arMod;
            result.MinDamageWire = damageResult.MinDamageF32;
            result.MaxDamageWire = damageResult.MaxDamageF32;
            result.DamageWire = damageResult.DamageWire;
            result.IsCritical = damageResult.IsCritical;
            result.ResultName = damageResult.ResultName;

            bool landed = damageResult.IsHit && !damageResult.IsBlocked && damageResult.DamageWire > 0;
            if (!landed)
            {
                Debug.LogError($"[SPELL-WEAPON-DAMAGE] spell={spell.DisplayName} result={damageResult.ResultName} target={target.Name}#{target.EntityId} hp={result.OldHPWire}->{result.NewHPWire} arMod={arMod} ar={baseAttackRating}->{attackRating} dr={damageResult.DefenseRating} hitRaw=0x{damageResult.HitRaw:X8} hitRoll={damageResult.HitRoll} threshold={damageResult.HitThreshold} blockRaw=0x{damageResult.BlockRaw:X8} blockRoll={damageResult.BlockRoll} rngAfter={rng.CallsSinceReseed} tag={tag ?? "SPELL"}");
                Debug.LogError($"[COMBAT-EVENT] actor=player-spell-weapon actorId={(conn?.Avatar != null ? conn.Avatar.Id : 0)} target=monster targetId={target.EntityId} result={damageResult.ResultName} damageWire=0 hp={result.OldHPWire}->{result.NewHPWire} spell={spell.DisplayName} arMod={arMod} damageModRaw={damageModRaw} rngAfter={rng.CallsSinceReseed} marker={tag ?? "SPELL"}");
                return result;
            }

            bool applied = CombatManager.Instance.ApplyNativePlayerWeaponDamageToMonsterWire(
                target,
                damageResult,
                $"{tag}-SpellWeaponDamageEffect",
                out uint oldHPWire,
                out uint newHPWire,
                out bool died,
                out uint effectRaw,
                rng,
                "player-spell-weapon",
                nativeEffectTime >= 0f ? nativeEffectTime : GetNativeCombatNow(),
                0);
            if (applied)
                NotifyMonsterDamagedByConnection(conn, target, $"{tag}-weapon");

            result.Landed = true;
            result.Applied = applied;
            result.Died = died;
            result.OldHPWire = oldHPWire;
            result.NewHPWire = newHPWire;

            string resultName = damageResult.IsCritical ? "CRIT" : "HIT";
            Debug.LogError($"[SPELL-WEAPON-DAMAGE] spell={spell.DisplayName} result={resultName} target={target.Name}#{target.EntityId} damageWire={damageResult.DamageWire} totalWire={damageResult.TotalDamageWire} addCount={(damageResult.DamageAdds != null ? damageResult.DamageAdds.Count : 0)} hp={oldHPWire}->{newHPWire} applied={applied} died={died} arMod={arMod} ar={baseAttackRating}->{attackRating} dr={damageResult.DefenseRating} damageMod={baseDamageMod}->{damageMod} damageModRaw={damageModRaw} range=[{damageResult.MinDamageF32},{damageResult.MaxDamageF32}] hitRaw=0x{damageResult.HitRaw:X8} blockRaw=0x{damageResult.BlockRaw:X8} dmgRaw=0x{damageResult.DamageRaw:X8} crit={damageResult.IsCritical} effectRaw=0x{effectRaw:X8} rngAfter={rng.CallsSinceReseed} tag={tag ?? "SPELL"}");
            Debug.LogError($"[COMBAT-EVENT] actor=player-spell-weapon actorId={(conn?.Avatar != null ? conn.Avatar.Id : 0)} target=monster targetId={target.EntityId} result={resultName} damageWire={damageResult.DamageWire} totalWire={damageResult.TotalDamageWire} addCount={(damageResult.DamageAdds != null ? damageResult.DamageAdds.Count : 0)} hp={oldHPWire}->{newHPWire} range=[{damageResult.MinDamageF32},{damageResult.MaxDamageF32}] hitRaw=0x{damageResult.HitRaw:X8} blockRaw=0x{damageResult.BlockRaw:X8} damageRaw=0x{damageResult.DamageRaw:X8} effectRaw=0x{effectRaw:X8} arMod={arMod} damageModRaw={damageModRaw} spell={spell.DisplayName} rngAfter={rng.CallsSinceReseed} marker={tag ?? "SPELL"}");
            return result;
        }

        private static int ResolveSpellEffectRawMod(int min, int max, int inc, int skillLevel)
        {
            int raw = min + (Math.Max(1, skillLevel) * inc);
            if (max > 0 && raw > max)
                raw = max;
            return raw;
        }

        private static int ResolveSpellEffectPercent(int min, int max, int inc, int skillLevel, int fallback)
        {
            int raw = ResolveSpellEffectRawMod(min, max, inc, skillLevel);
            return raw > 0 ? raw : fallback;
        }

        private void NotifyMonsterDamagedByConnection(RRConnection conn, Combat.Monster monster, string reason)
        {
            if (conn?.Avatar == null || monster == null || !monster.IsAlive || CombatManager.Instance.PeekMonsterCurrentHPWire(monster) == 0) return;
            Combat.CombatManager.Instance.NotifyMonsterNativeOnAttackedAdmission(monster, (uint)conn.Avatar.Id, reason);
        }

        private void LogMonsterOnAttackedBlocked(RRConnection conn, Combat.Monster monster, string source, string reason)
        {
            if (monster == null) return;
            uint playerId = conn?.Avatar != null ? (uint)conn.Avatar.Id : 0u;
            Debug.LogError($"[MON-ONATTACKED-BLOCKED] monster={monster.Name}#{monster.EntityId} player={playerId} source={source ?? "unknown"} reason={reason ?? "unknown"} native=Damage::apply@0x004F6580->MonsterBehavior2::onAttacked@0x0051B550 proof=missing-no-admission");
        }

        private void HandleChainSpell(RRConnection conn, PlayerState state, Combat.Monster source,
    Combat.SpellData spell, Combat.MersenneTwister rng, int skillLevel = 1, float nativeEffectTime = -1f)
        {
            string instanceKey = !string.IsNullOrWhiteSpace(source?.InstanceKey)
                ? source.InstanceKey
                : GetInstanceZoneKey(conn);
            var nearby = Combat.CombatManager.Instance.GetMonstersInRange(
                source.PosX, source.PosY, spell.ChainRange, instanceKey);
            int chainsLeft = spell.NumChains;
            foreach (var target in nearby)
            {
                if (chainsLeft <= 0) break;
                if (target.EntityId == source.EntityId) continue;
                if (!target.IsAlive) continue;
                ApplySpellDamageToMonster(conn, state, spell, target, rng, skillLevel, true, nativeEffectTime);
                chainsLeft--;
            }
        }


        /// <summary>
        /// Handle 0x52 self-cast spells (Gaseous Blast, buffs, etc.)
        /// These are AoE/self-target spells that don't send a targetEntityID.
        /// slotID is actually the manipulatorId — same ID assigned during Op4.
        /// </summary>
        private void HandleSelfCastSpell(RRConnection conn, PlayerState state, byte slotID, ushort componentId)
        {
            Combat.SpellDatabase.Initialize();

            // slotID=101 etc. IS the manipulatorId — resolve via Op4 mapping
            var spell = ResolveSpellFromManip(conn, slotID);

            // ═══ BLING GNOME SKILL CHECK ═══
            string connKey2 = conn.ConnId.ToString();
            if (_playerManipMap.TryGetValue(connKey2, out var manipMap2) &&
                manipMap2.TryGetValue(slotID, out string gcClass2))
            {
                if (gcClass2.IndexOf("BlingGnome", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    gcClass2.IndexOf("SummonBling", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Debug.LogError($"[SPELL-BLING] Bling Gnome skill detected: {gcClass2}");
                    BlingGnomeManager.Instance.SetServer(this);
                    BlingGnomeManager.Instance.ToggleGnome(conn,
                        (c, d, t, b) => SendCompressedA(c, d, t, b),
                        (c, m) => SendSystemMessage(c, m));
                    return;
                }
            }

            // Fallback: try class-based AoE lookup
            if (spell == null)
            {
                string fallback = state.ClassName?.ToLower() switch
                {
                    "fighter" => "Stomp",
                    "ranger" => "PoisonBlastRadius",
                    "mage" => "ShadowRage",
                    _ => null
                };
                if (fallback != null) spell = Combat.SpellDatabase.GetSpell(fallback);
                if (spell == null) spell = Combat.SpellDatabase.GetSpellForClass(state.ClassName);
            }

            int skillLevel = spell != null ? GetPlayerSkillLevel(conn, spell) : 1;
            Debug.LogError($"[SPELL-0x52] HandleSelfCast: class={state.ClassName} manipId={slotID} spell={spell?.DisplayName ?? "UNKNOWN"} skillLv={skillLevel}");

            // Mana cost
            if (spell != null)
            {
                state.AdvanceClientSyncHP(GetNativeCombatNow(), $"MANA-0x52-{spell.DisplayName}-pre-cost");
                uint manaCostWire = (uint)(spell.ManaCostMod * state.Level * 256);
                uint oldMana = state.CurrentManaWire;
                if (state.CurrentManaWire > manaCostWire)
                    state.SetCurrentMana(state.CurrentManaWire - manaCostWire, $"selfspell:{spell.DisplayName}");
                else
                    state.SetCurrentMana(0, $"selfspell:{spell.DisplayName}");
                Debug.LogError($"[MANA-0x52] {spell.DisplayName} cost={manaCostWire / 256} mana | {oldMana / 256} → {state.CurrentManaWire / 256} / {state.MaxManaWire / 256}");

                // Save mana to DB (mana only — don't touch XP/level)
                try
                {
                    if (_selectedCharacter.ContainsKey(conn.LoginName))
                        using (var manaDb2 = GameDatabase.GetConnection())
                            GameDatabase.ExecuteNonQuery(manaDb2,
                                "UPDATE characters SET current_mana=@mp WHERE id=@id",
                                ("@mp", (int)state.CurrentManaWire), ("@id", (int)_selectedCharacter[conn.LoginName].Id));
                }
                catch (System.Exception ex) { Debug.LogError($"[SAVE] current_mana persist failed for {conn.LoginName}: {ex.Message}"); }
            }

            if (spell != null && spell.IsAoE)
            {
                float playerX = conn.PlayerPosX;
                float playerY = conn.PlayerPosY;
                float range = spell.AoERadius > 0 ? spell.AoERadius : (spell.Range > 0 ? spell.Range : 30f);

                var monstersInRange = Combat.CombatManager.Instance.GetMonstersInRange(playerX, playerY, range, GetInstanceZoneKey(conn));
                Debug.LogError($"[SPELL-0x52] AoE scan: {monstersInRange?.Count ?? 0} monsters within range={range} of ({playerX:F1},{playerY:F1})");

                int maxTargets = int.MaxValue;
                if (spell.NumTargetsMax > 0)
                {
                    int cap = (int)(spell.NumTargetsMin + skillLevel * spell.NumTargetsInc);
                    if (cap > (int)spell.NumTargetsMax) cap = (int)spell.NumTargetsMax;
                    if (cap < 1) cap = 1;
                    maxTargets = cap;
                }

                if (monstersInRange != null)
                {
                    int hits = 0;
                    foreach (var monster in monstersInRange)
                    {
                        if (!monster.IsAlive) continue;
                        if (hits >= maxTargets) break;

                        ApplySpellDamageToMonster(conn, state, spell, monster, CombatManager.Instance.GetRoomRngForMonster(monster), skillLevel, true);
                        hits++;
                    }
                    if (hits > 0)
                        Debug.LogError($"[SPELL-0x52] {spell.DisplayName} observed {hits} targets (max={maxTargets}, range={range})");
                }
            }
            else
            {
                Debug.LogError($"[SPELL-0x52] Non-AoE self-cast (buff?) — no damage to apply. slotID={slotID}");
            }

            // ═══ BUFF TRACKING: Track self-cast modifier for zone persistence ═══
            {
                string buffConnKey = conn.ConnId.ToString();
                string buffGcClass = null;
                if (_playerManipMap.TryGetValue(buffConnKey, out var buffMap))
                    buffMap.TryGetValue(slotID, out buffGcClass);
                if (buffGcClass != null)
                {
                    string shortKey = buffGcClass.ToLowerInvariant();
                    int lastDot = shortKey.LastIndexOf('.');
                    if (lastDot >= 0) shortKey = shortKey.Substring(lastDot + 1);
                    if (_buffModifierMap.TryGetValue(shortKey, out var buffInfo))
                    {
                        uint durTicks = buffInfo.durBase == 0 ? 0
                            : (uint)((buffInfo.durBase + skillLevel * buffInfo.durInc) * (1000.0 / 24.0));
                        uint modId = _modifierTracker.NextId();
                        TrackModifierSent(conn.LoginName, buffInfo.modGcType, modId,
                            level: (byte)skillLevel, duration: durTicks, sourceIsSelf: 0x01);
                        Debug.LogError($"[BUFF-TRACK] {shortKey} → '{buffInfo.modGcType}' dur={durTicks} ticks ({buffInfo.durBase + skillLevel * buffInfo.durInc}s) for {conn.LoginName}");
                    }
                }
            }
        }



        private void HandleItemPickup(RRConnection conn, ushort componentId, ushort targetEntityID, byte responseId, byte sessionID)
        {
            // Gold piles have Item=null — redirect to right-click handler which has gold logic
            if (_droppedItems.TryGetValue(targetEntityID, out var goldCheck) && goldCheck.GoldAmount > 0)
            {
                HandleItemRightClickPickup(conn, componentId, targetEntityID, responseId, sessionID);
                return;
            }

            Debug.LogError("╔═══════════════════════════════════════════════════════════════════════════════╗");
            Debug.LogError("║                              ITEM PICKUP START                                ║");
            Debug.LogError("╚═══════════════════════════════════════════════════════════════════════════════╝");

            // Get the dropped item and remove from tracking
            // Check quest/gold flags BEFORE removal since DroppedItemInfo gets deleted
            bool isQuestPickup = false;
            bool isGoldPickup = false;
            uint goldPickupAmount = 0;
            if (_droppedItems.TryGetValue(targetEntityID, out var preInfo))
            {
                isQuestPickup = preInfo.IsQuestItem;
                isGoldPickup = preInfo.IsGoldDrop;
                goldPickupAmount = preInfo.GoldAmount;
            }
            int droppedQty; GCObject item = GetAndRemoveDroppedItem(targetEntityID, out droppedQty);
            if (item == null)
            {
                Debug.LogError($"[PICKUP] ❌ Item not found for entity {targetEntityID}");
                return;
            }

            Debug.LogError($"[PICKUP] Found item: {item.GCClass} isQuestItem={isQuestPickup} isGold={isGoldPickup}");

            // ═══ GOLD PILE PICKUP — credit currency, remove entity, done ═══
            if (isGoldPickup && goldPickupAmount > 0)
            {
                Debug.LogError($"[PICKUP] Gold pile: +{goldPickupAmount} gold");
                try
                {
                    if (_selectedCharacter.TryGetValue(conn.LoginName, out var goldGcObj))
                    {
                        var goldChar = DungeonRunners.Database.CharacterRepository.GetCharacter(goldGcObj.Id);
                        if (goldChar != null)
                        {
                            goldChar.gold += goldPickupAmount;
                            DungeonRunners.Database.CharacterRepository.SaveCharacter(goldChar);
                        }
                    }
                    var goldWriter = new LEWriter();
                    goldWriter.WriteByte(0x07);
                    goldWriter.WriteByte(0x05);
                    goldWriter.WriteUInt16(targetEntityID);
                    if (conn.UnitContainerId != 0)
                    {
                        goldWriter.WriteByte(0x35);
                        goldWriter.WriteUInt16(conn.UnitContainerId);
                        goldWriter.WriteByte(0x20);           // AddCurrency
                        goldWriter.WriteUInt32(goldPickupAmount);
                        goldWriter.WriteByte(0x00);
                        goldWriter.WriteUInt32(0x00000000);
                        goldWriter.WriteByte(0x01);
                        WritePlayerEntitySynch(conn, goldWriter);
                    }
                    goldWriter.WriteByte(0x06);
                    SendToClient(conn, goldWriter.ToArray());
                }
                catch (Exception gpEx)
                {
                    Debug.LogError($"[PICKUP] ❌ Gold credit failed: {gpEx.Message}");
                }
                return;
            }

            string connId = conn.ConnId.ToString();
            PlayerState playerState = GetPlayerState(connId);
            ushort unitContainerId = GetUnitContainerComponentId(connId);

            Debug.LogError($"[PICKUP] UnitBehavior ID: 0x{componentId:X4}");
            Debug.LogError($"[PICKUP] UnitContainer ID: 0x{unitContainerId:X4}");

            var writer = new LEWriter();
            writer.WriteByte(0x07); // BeginStream

            // ========================================
            // Part 1: Activate Action Response (UnitBehavior)
            // ========================================
            writer.WriteByte(0x35);              // ComponentUpdate
            writer.WriteUInt16(componentId);     // UnitBehavior component
            writer.WriteByte(0x01);              // ActionResponse
            writer.WriteByte(responseId);
            writer.WriteByte(0x06);              // BehaviourActionActivate
            writer.WriteByte(sessionID);
            writer.WriteUInt16(targetEntityID);  // Target entity
            WritePlayerEntitySynch(conn, writer);
            Debug.LogError($"[PICKUP] ✅ Wrote Activate response");

            // ========================================
            // Part 2: Remove ItemObject Entity from World
            // ========================================
            writer.WriteByte(0x05);              // Remove entity opcode
            writer.WriteUInt16(targetEntityID);
            Debug.LogError($"[PICKUP] ✅ Wrote Remove entity {targetEntityID}");

            // ========================================
            // Part 3: Set Item as ActiveItem (UnitContainer)
            // ========================================
            writer.WriteByte(0x35);              // ComponentUpdate
            writer.WriteUInt16(unitContainerId); // UnitContainer component
            writer.WriteByte(0x28);              // SetActiveItem
            string pickupGcCheck = item.GCClass.ToLower();
            bool isPickupConsumable = pickupGcCheck.Contains("potion") || pickupGcCheck.Contains("consumable")
                || pickupGcCheck.Contains("townportal") || pickupGcCheck.Contains("scroll")
                || pickupGcCheck.Contains("questitem") || pickupGcCheck.Contains("itempal")
                || pickupGcCheck.Contains("skillbook") || pickupGcCheck.Contains("voucher");
            if (isPickupConsumable)
            {
                // Consumable: ActiveItem::readData@0x581710 format
                writer.WriteByte(0xFF);
                writer.WriteCString(GCObject.GetPacketGCClassFor(item.GCClass));
                writer.WriteUInt32(0);                       // id
                writer.WriteByte(0x00);                      // invX
                writer.WriteByte(0x00);                      // invY
                writer.WriteByte(0x01);                      // qty
                int cLevel = item.StoredLevel >= 0 ? item.StoredLevel : 1;
                writer.WriteByte((byte)cLevel);              // level
                writer.WriteByte(0x00);                      // flags
                if (pickupGcCheck.Contains("dragonjuice") || pickupGcCheck.Contains("intbuff"))
                    writer.WriteByte(0x00);                  // transient Mod1 flags
                writer.WriteByte(0x00);                      // modifier count
            }
            else
            {
                item.WriteInitWithoutWeaponBytes(writer, playerState.Level);
            }
            // WriteSynch for UnitContainer - component update uses HP
                WritePlayerEntitySynch(conn, writer);

            writer.WriteByte(0x06); // EndStream

            byte[] packet = writer.ToArray();
            Debug.LogError($"[PICKUP] Packet size: {packet.Length} bytes");
            Debug.LogError($"[PICKUP] Packet hex: {BitConverter.ToString(packet)}");

            SendToClient(conn, packet);

            // ═══ QUEST ITEM PICKUP: notify quest system so objective counter updates ═══
            if (isQuestPickup)
            {
                Debug.LogError($"[PICKUP] Quest item picked up: {item.GCClass}");
                NotifyQuestItemAcquired(conn, item.GCClass);
            }

            // ═══ MULTIPLAYER: Remove dropped item entity for other players ═══
            foreach (var other in _connections.Values)
            {
                if (other == conn) continue;
                if (!other.IsSpawned) continue;
                if (other.CurrentZoneGcType != conn.CurrentZoneGcType) continue;
                if (other.InstanceId != conn.InstanceId) continue;
                SendDespawnEntity(other, targetEntityID);
            }

            // Update server state - item is now in player's hand
            // Update server state - item is now in player's hand
            playerState.ActiveItem = item;

            var pickedUpWeapon = DatabaseLoader.FindItem(item.GCClass);
            var pickedUpWeaponNode = GCDatabase.Instance.ResolveWithInheritance(item.GCClass);
            (float damage, float volatility, float range, float cooldown, string weaponClass, float weaponSpeed,
             string damageType, string weaponCategory, bool useProjectile, float projectileSpeed, float projectileSize, int burstCount) pickedUpWeaponStats = default;
            if (pickedUpWeaponNode == null)
                RuntimeEvidenceManager.LogFallbackHit("damage-weapon-desc", "missing-gc-node", $"source=pickup weapon={item.GCClass} native=Weapon::ComputeAttributes-return", 64);
            else
                pickedUpWeaponStats = GCDatabase.Instance.GetWeaponStats(item.GCClass);
            float pickedUpAuthoredDamage = pickedUpWeaponStats.damage > 0f ? pickedUpWeaponStats.damage : 0f;
            float pickedUpAuthoredVolatility = pickedUpWeaponStats.volatility > 0f ? pickedUpWeaponStats.volatility : 0.25f;
            string pickedUpWeaponClass = !string.IsNullOrEmpty(pickedUpWeaponStats.weaponClass) ? pickedUpWeaponStats.weaponClass : pickedUpWeapon != null && !string.IsNullOrEmpty(pickedUpWeapon.weaponClass) ? pickedUpWeapon.weaponClass : "";
            string pickedUpDamageType = !string.IsNullOrEmpty(pickedUpWeaponStats.damageType) ? pickedUpWeaponStats.damageType : "";
            if (pickedUpAuthoredDamage > 0f &&
                TryResolveNativeWeaponDescIds("pickup", item.GCClass, pickedUpWeaponClass, pickedUpDamageType, out int pickedUpNativeWeaponClassId, out int pickedUpNativeDamageTypeId))
            {
                playerState.WeaponDamage = pickedUpAuthoredDamage;
                playerState.WeaponDamageVolatility = Mathf.Clamp(pickedUpAuthoredVolatility, 0f, 0.95f);
                playerState.WeaponLevel = Math.Max(1, item.StoredLevel >= 0 ? item.StoredLevel : DungeonRunners.Managers.RarityHelper.GetItemLevel(item.GCClass));
                playerState.WeaponClass = pickedUpWeaponClass;
                playerState.WeaponDamageType = pickedUpDamageType;
                playerState.WeaponCategory = !string.IsNullOrEmpty(pickedUpWeaponStats.weaponCategory) ? pickedUpWeaponStats.weaponCategory : "";
                playerState.WeaponStatsResolved = true;
                playerState.NativeWeaponClassId = pickedUpNativeWeaponClassId;
                playerState.NativeDamageTypeId = pickedUpNativeDamageTypeId;
                DamageComputer.ApplyNativeWeaponRuntimeBaseDamage(playerState, playerState.Level, item.StoredLevel, playerState.WeaponLevel, "pickup");
                playerState.WeaponRange = pickedUpWeaponStats.range > 0 ? Mathf.RoundToInt(pickedUpWeaponStats.range) : pickedUpWeapon != null && pickedUpWeapon.range > 0 ? pickedUpWeapon.range : 0;
                playerState.WeaponCooldown = pickedUpWeaponStats.cooldown > 0 ? pickedUpWeaponStats.cooldown : pickedUpWeapon != null && pickedUpWeapon.cooldown > 0 ? pickedUpWeapon.cooldown : 0f;
                playerState.WeaponSpeed = pickedUpWeaponStats.weaponSpeed > 0 ? pickedUpWeaponStats.weaponSpeed : pickedUpWeapon != null && pickedUpWeapon.weaponSpeed > 0 ? pickedUpWeapon.weaponSpeed : 105f;
                playerState.WeaponUsesProjectile = pickedUpWeaponStats.useProjectile;
                playerState.WeaponProjectileSpeed = pickedUpWeaponStats.projectileSpeed;
                playerState.WeaponProjectileSize = pickedUpWeaponStats.projectileSize;
                playerState.WeaponBurstCount = Math.Max(1, pickedUpWeaponStats.burstCount);
                Debug.LogError($"[PICKUP] Weapon damage updated to {playerState.WeaponDamage} vol={playerState.WeaponDamageVolatility:F2} level={playerState.WeaponLevel} nativeDamageLevel={playerState.NativeWeaponDamageLevel} nativeBaseDamage={playerState.NativeWeaponBaseDamage} nativeBaseSource={playerState.NativeWeaponBaseDamageSource} class={playerState.WeaponClass}/{playerState.NativeWeaponClassId} damageType={playerState.WeaponDamageType}/{playerState.NativeDamageTypeId} category={playerState.WeaponCategory} range={playerState.WeaponRange} cooldown={playerState.WeaponCooldown:F2} speed={playerState.WeaponSpeed:F2} useProjectile={playerState.WeaponUsesProjectile} projectileSpeed={playerState.WeaponProjectileSpeed:F2} projectileSize={playerState.WeaponProjectileSize:F2} burst={playerState.WeaponBurstCount} from '{item.GCClass}'");
            }

            Debug.LogError($"[PICKUP] ✅ Item picked up and now in hand!");

            // Notify QuestManager for item-type quest objectives (e.g. wolf pelts, quest items)
            if (item.GCClass != null)
                NotifyQuestItemAcquired(conn, item.GCClass);

            Debug.LogError("╔═══════════════════════════════════════════════════════════════════════════════╗");
            Debug.LogError("║                              ITEM PICKUP COMPLETE                             ║");
            Debug.LogError("╚═══════════════════════════════════════════════════════════════════════════════╝");
        }

        // Auto-bag pickup: puts the dropped item directly into the inventory bag.
        // directly into the player's inventory bag instead of onto the cursor.
        // Mirrors the wire format of HandlePlaceItemInInventory (0x29 sync + 0x1E place)
        // and the merchant equipment-buy path. Falls back to left-click cursor pickup if
        // the inventory is full so the player never loses an item.
        private void HandleItemRightClickPickup(RRConnection conn, ushort componentId, ushort targetEntityID, byte responseId, byte sessionID)
        {
            Debug.LogError($"[PICKUP-RC] Right-click target=0x{targetEntityID:X4}");

            // ═══ GOLD PILE PICKUP — Item is null for gold, handle before GetAndRemoveDroppedItem ═══
            if (_droppedItems.TryGetValue(targetEntityID, out DroppedItemInfo goldCheck) && goldCheck.GoldAmount > 0)
            {
                uint goldAmount = goldCheck.GoldAmount;
                _droppedItems.Remove(targetEntityID);
                Debug.LogError($"[GOLD-PICKUP] +{goldAmount} gold (entity 0x{targetEntityID:X4})");

                try
                {
                    if (_selectedCharacter.TryGetValue(conn.LoginName, out var lootGcObj))
                    {
                        var lootChar = DungeonRunners.Database.CharacterRepository.GetCharacter(lootGcObj.Id);
                        if (lootChar != null)
                        {
                            lootChar.gold += goldAmount;
                            DungeonRunners.Database.CharacterRepository.SaveCharacter(lootChar);
                        }
                    }
                }
                catch (Exception ex) { Debug.LogError($"[GOLD-PICKUP] DB error: {ex.Message}"); }

                string connId2 = conn.ConnId.ToString();
                ushort unitContainerId2 = GetUnitContainerComponentId(connId2);
                var gw = new LEWriter();
                gw.WriteByte(0x07);
                gw.WriteByte(0x35);
                gw.WriteUInt16(componentId);
                gw.WriteByte(0x01);
                gw.WriteByte(responseId);
                gw.WriteByte(0x06);
                gw.WriteByte(sessionID);
                gw.WriteUInt16(targetEntityID);
                WritePlayerEntitySynch(conn, gw);
                gw.WriteByte(0x05);
                gw.WriteUInt16(targetEntityID);
                if (unitContainerId2 != 0)
                {
                    gw.WriteByte(0x35);
                    gw.WriteUInt16(unitContainerId2);
                    gw.WriteByte(0x20);
                    gw.WriteUInt32(goldAmount);
                    gw.WriteByte(0x00);
                    gw.WriteUInt32(0x00000000);
                    gw.WriteByte(0x01);
                    WritePlayerEntitySynch(conn, gw);
                }
                gw.WriteByte(0x06);
                SendToClient(conn, gw.ToArray());
                return;
            }

            int droppedQty; GCObject item = GetAndRemoveDroppedItem(targetEntityID, out droppedQty);
            if (item == null)
            {
                Debug.LogError($"[PICKUP-RC] Item not in tracker - probably already picked up");
                return;
            }

            string connId = conn.ConnId.ToString();
            PlayerState playerState = GetPlayerState(connId);
            ushort unitContainerId = GetUnitContainerComponentId(connId);

            // === STACK MERGE: top up an existing stack of the same type if there is one ===
            // Stackable simple items: potions, scrolls, town portals, consumables.
            // Quest items are excluded - the quest system tracks counts separately and
            // each pickup needs its own slot to fire NotifyQuestItemAcquired correctly.
            // Equipment is excluded - it has stats/scale per instance and can't merge.
            // Max stack: free-to-play players get 5, members get 10. (Same item db,
            // server-enforced cap to mirror the original DR membership behavior.)
            string gcCheckMerge = item.GCClass.ToLower();
            bool isStackableSimple = !gcCheckMerge.Contains("questitem")
                                     && (gcCheckMerge.Contains("potion")
                                         || gcCheckMerge.Contains("scroll")
                                         || gcCheckMerge.Contains("townportal")
                                         || gcCheckMerge.Contains("consumable")
                                         || gcCheckMerge.Contains("skillbook")
                                         || gcCheckMerge.Contains("voucher"));
            if (isStackableSimple)
            {
                int maxStack = IsPlayerFree(conn.LoginName) ? 5 : 10;
                if (_playerInventoryItems.ContainsKey(connId))
                {
                    foreach (var kvp in _playerInventoryItems[connId])
                    {
                        uint existingSlot = kvp.Key;
                        var entry = kvp.Value;
                        if (entry.item == null) continue;
                        if (!string.Equals(entry.item.GCClass, item.GCClass, StringComparison.OrdinalIgnoreCase)) continue;

                        int currentCount = GetStackCount(connId, existingSlot);
                        if (currentCount >= maxStack) continue;  // this stack is full, try the next one

                        int newCount = currentCount + droppedQty;
                        Debug.LogError($"[PICKUP-RC] STACK MERGE: {item.GCClass} -> slot {existingSlot} {currentCount}→{newCount} (max {maxStack}, free={IsPlayerFree(conn.LoginName)})");

                        // Build packet: action ack + remove entity + 0x22 UpdateQuantity
                        var mWriter = new LEWriter();
                        mWriter.WriteByte(0x07);  // BeginStream

                        // ActionResponse ack (same shape as the new-stack path)
                        mWriter.WriteByte(0x35);
                        mWriter.WriteUInt16(componentId);
                        mWriter.WriteByte(0x01);
                        mWriter.WriteByte(responseId);
                        mWriter.WriteByte(0x06);  // BehaviourActionActivate
                        mWriter.WriteByte(sessionID);
                        mWriter.WriteUInt16(targetEntityID);
                        WritePlayerEntitySynch(conn, mWriter);

                        // Remove the ground entity
                        mWriter.WriteByte(0x05);
                        mWriter.WriteUInt16(targetEntityID);

                        // 0x22 UpdateQuantity on UnitContainer
                        // Binary: client processUpdateQuantity at 0x57dc50
                        // Reads u32 itemSlotId + u8 newQuantity, stores at item+0x82
                        mWriter.WriteByte(0x35);
                        mWriter.WriteUInt16(unitContainerId);
                        mWriter.WriteByte(0x22);
                        mWriter.WriteUInt32(existingSlot);
                        mWriter.WriteByte((byte)(newCount > 255 ? 255 : newCount));
                        WritePlayerEntitySynch(conn, mWriter);

                        mWriter.WriteByte(0x06);  // EndStream

                        SendToClient(conn, mWriter.ToArray());
                        SetStackCount(connId, existingSlot, newCount);

                        // Multiplayer: despawn ground item for others
                        foreach (var other in _connections.Values)
                        {
                            if (other == conn) continue;
                            if (!other.IsSpawned) continue;
                            if (other.CurrentZoneGcType != conn.CurrentZoneGcType) continue;
                            if (other.InstanceId != conn.InstanceId) continue;
                            SendDespawnEntity(other, targetEntityID);
                        }

                        SavePlayerInventoryPublic(conn);
                        if (item.GCClass != null) NotifyQuestItemAcquired(conn, item.GCClass);
                        return;  // merged - skip the new-stack path entirely
                    }
                }
                Debug.LogError($"[PICKUP-RC] No mergeable stack for {item.GCClass} (max {maxStack}) - creating new stack");
            }

            // Look up dimensions to find a free slot
            ItemData itemData = DatabaseLoader.FindItem(item.GCClass);
            int itemWidth = itemData?.inventoryWidth ?? 1;
            int itemHeight = itemData?.inventoryHeight ?? 1;

            var (slotX, slotY) = FindNextFreeInventorySlot(connId, itemWidth, itemHeight);
            if (slotX < 0 || slotY < 0)
            {
                // Inventory full — put item BACK on ground, send message, don't pick up
                Debug.LogError($"[PICKUP-RC] Inventory full - leaving item on ground");
                TrackDroppedItem(targetEntityID, item, conn);
                SendSystemMessage(conn, "Your inventory is full!");
                // MUST send ActionResponse or client action state machine is stuck (can't move)
                var fullWriter = new LEWriter();
                fullWriter.WriteByte(0x07);
                fullWriter.WriteByte(0x35);
                fullWriter.WriteUInt16(componentId);
                fullWriter.WriteByte(0x01);              // ActionResponse
                fullWriter.WriteByte(responseId);
                fullWriter.WriteByte(0x06);              // BehaviourActionActivate
                fullWriter.WriteByte(sessionID);
                fullWriter.WriteUInt16(targetEntityID);
                WritePlayerEntitySynch(conn, fullWriter);
                fullWriter.WriteByte(0x06);
                SendToClient(conn, fullWriter.ToArray());
                return;
            }

            uint trackingSlot = GetNextInventorySlot(connId);
            Debug.LogError($"[PICKUP-RC] {item.GCClass} -> slot ({slotX},{slotY}) trackingSlot={trackingSlot}");

            var writer = new LEWriter();
            writer.WriteByte(0x07); // BeginStream

            // Part 1: ActionResponse acknowledging the 0x06 click (same as left-click handler)
            writer.WriteByte(0x35);              // ComponentUpdate
            writer.WriteUInt16(componentId);     // UnitBehavior
            writer.WriteByte(0x01);              // ActionResponse subMessage
            writer.WriteByte(responseId);
            writer.WriteByte(0x06);              // BehaviourActionActivate (must match what client sent)
            writer.WriteByte(sessionID);
            writer.WriteUInt16(targetEntityID);
            WritePlayerEntitySynch(conn, writer);

            // Part 2: Remove the ground entity
            writer.WriteByte(0x05);
            writer.WriteUInt16(targetEntityID);

            // Part 3: 0x29 sync (defensive cursor clear, matches HandlePlaceItemInInventory)
            writer.WriteByte(0x35);
            writer.WriteUInt16(unitContainerId);
            writer.WriteByte(0x29);
            WritePlayerEntitySynch(conn, writer);

            // Part 4: 0x1E place item directly in inventory bag 0x0B at (slotX, slotY)
            writer.WriteByte(0x35);
            writer.WriteUInt16(unitContainerId);
            writer.WriteByte(0x1E);
            writer.WriteByte(0x0B);  // player main bag

            string gcCheck = item.GCClass.ToLower();
            if (gcCheck.Contains("questitem") || gcCheck.Contains("consumable") || gcCheck.Contains("potion") || gcCheck.Contains("townportal") || gcCheck.Contains("scroll") || gcCheck.Contains("skillbook") || gcCheck.Contains("voucher"))
            {
                // Simple item: bare readData format (matches HandlePlaceItemInInventory)
                writer.WriteByte(0xFF);
                writer.WriteCString(GCObject.GetPacketGCClassFor(gcCheck));
                writer.WriteUInt32(trackingSlot);
                writer.WriteByte((byte)slotX);
                writer.WriteByte((byte)slotY);
                writer.WriteByte((byte)droppedQty);  // stack count from dropped item
                writer.WriteByte(0x01);  // level
                writer.WriteByte(0x00);  // flags
                // Binary RE: ActiveItem::readData@0x581710 then 0x583920 walks
                // transient children. DragonJuice/IntBuff have transient Mod1
                // (ItemAttributeModifier) whose readData@0x588AE0 reads 1 byte.
                if (gcCheck.Contains("dragonjuice") || gcCheck.Contains("intbuff"))
                    writer.WriteByte(0x00);  // transient Mod1 flags
                writer.WriteByte(0x00);  // modifier count
                SetStackCount(connId, trackingSlot, droppedQty);
            }
            else
            {
                // Equipment: full WriteInitForInventory (handles ScaleMod, level, etc.)
                int itemLevel = item.GetItemRequiredLevel();
                item.WriteInitForInventory(writer, (byte)slotX, (byte)slotY, trackingSlot, itemLevel);
            }
            WritePlayerEntitySynch(conn, writer);

            writer.WriteByte(0x06); // EndStream

            byte[] packet = writer.ToArray();
            Debug.LogError($"[PICKUP-RC] Packet size: {packet.Length} bytes");
            // For skill books / vouchers specifically — dump the bytes so we can see what's being sent
            if (gcCheck.Contains("skillbook") || gcCheck.Contains("voucher"))
            {
                Debug.LogError($"[PICKUP-RC] ITEM: {item.GCClass}, gcPkt={GCObject.GetPacketGCClassFor(gcCheck)}, trackingSlot={trackingSlot}, slot=({slotX},{slotY}), qty={droppedQty}");
                Debug.LogError($"[PICKUP-RC] HEX: {BitConverter.ToString(packet)}");
            }
            SendToClient(conn, packet);

            // Server-side state: occupy slots, track item, save
            OccupyInventorySlots(connId, (byte)slotX, (byte)slotY, itemWidth, itemHeight);
            TrackInventoryItem(connId, trackingSlot, item, (byte)slotX, (byte)slotY);

            // Multiplayer: despawn the ground item for everyone else in the same instance
            foreach (var other in _connections.Values)
            {
                if (other == conn) continue;
                if (!other.IsSpawned) continue;
                if (other.CurrentZoneGcType != conn.CurrentZoneGcType) continue;
                if (other.InstanceId != conn.InstanceId) continue;
                SendDespawnEntity(other, targetEntityID);
            }

            SavePlayerInventoryPublic(conn);

            // Notify QuestManager for item-type quest objectives (wolf pelts, quest items, etc.)
            if (item.GCClass != null)
                NotifyQuestItemAcquired(conn, item.GCClass);

            Debug.LogError($"[PICKUP-RC] ✅ {item.GCClass} placed in inventory at ({slotX},{slotY})");
        }







        // Client responding to our Follow Client message - must read the data!
        private void HandleClientControlResponse(RRConnection conn, LEReader reader, ushort componentId)
        {
            try
            {
                Debug.Log($"[CLIENT-CONTROL] ⚠️ CLIENT RESPONDED TO OUR 0x64 MESSAGE!");
                Debug.Log($"[CLIENT-CONTROL] ComponentId: {componentId:X4}, Remaining bytes: {reader.Remaining}");

                // Read and log ALL bytes
                while (reader.Remaining > 0)
                {
                    byte b = reader.ReadByte();
                    Debug.Log($"[CLIENT-CONTROL] Read byte: 0x{b:X2}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CLIENT-CONTROL] Error: {ex.Message}");
            }
        }

        // Cancel response - echo back what client sent
        /// <summary>
        /// Handles cancel action (0x03) - client wants to stop current action
        /// </summary>
        private void HandleCancelAction(RRConnection conn, LEReader reader, ushort componentId)
        {
            try
            {
                byte sessionId = reader.ReadByte();
                Debug.LogError($"[CANCEL-ACTION] Client wants to cancel, sessionId=0x{sessionId:X2}");
                string atkKey = conn.ConnId.ToString();
                var oldTarget = Combat.WeaponCycleTracker.Instance.GetActiveTarget(atkKey);
                if (oldTarget != null)
                {
                    Debug.LogError($"[CANCEL-ACTION] Clearing target {oldTarget.Name} (UseTargets={oldTarget.UseTargetCount})");
                }
                var msg = new LEWriter();
                msg.WriteByte(0x07);  // BeginStream
                msg.WriteByte(0x35);
                msg.WriteUInt16(componentId);
                msg.WriteByte(0x03);
                msg.WriteByte(sessionId);
                if (!TryWriteEntitySynchForComponent(conn, msg, componentId, 0x03, SyncContext.ControlAck, "CANCEL-ACTION", true))
                {
                    Debug.LogError($"[CANCEL-ACTION] Dropped cancel response because Avatar HP suffix was unresolved component=0x{componentId:X4}");
                    return;
                }
                msg.WriteByte(0x06);  // EndStream
                SendToClient(conn, msg.ToArray());
                Debug.LogError($"[CANCEL-ACTION] ✅ Sent cancel response");
                if (conn.HasActiveUseTarget || oldTarget != null)
                    ClearUseTargetAndReleaseControl(conn, "CANCEL-ACTION", componentId);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CANCEL-ACTION] Error: {ex.Message}");
            }
        }


        // Handle direct 0x06 submessage
        private void HandleActionType06(RRConnection conn, LEReader reader, ushort componentId)
        {
            try
            {
                Debug.Log($"[ACTION-06] Client sent 0x06 submessage, remaining bytes: {reader.Remaining}");
                // Consume any remaining data to keep stream aligned
                while (reader.Remaining > 0)
                {
                    byte b = reader.ReadByte();
                    Debug.Log($"[ACTION-06] Read byte: 0x{b:X2}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ACTION-06] Error: {ex.Message}");
            }
        }

        ////////////ADDING IN ZONEPORTALS///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

        public class ZonePortal
        {
            public uint Id;
            public string GCType;
            public string Name;
            public float PosX;
            public float PosY;
            public float PosZ;
            public float Heading;
            public int Width;
            public int Height;
            public string TargetZone;
            public string SpawnPoint;
            public uint Color;
        }

        public class ZoneCheckpoint
        {
            public uint Id;
            public string GCType;
            public string Name;
            public float PosX;
            public float PosY;
            public float PosZ;
            public float Heading;
        }





        private void InitializeZonePortals()
        {
            Debug.LogError("[InitPortals] Loading zone portals from database...");

            foreach (var zone in _zones.Values)
            {
                string zoneName = zone.name.ToLower();
                var portalData = DatabaseLoader.GetPortalsForZone(zoneName);

                if (portalData == null || portalData.Count == 0)
                    continue;

                _zonePortals[zone.id] = new List<ZonePortal>();

                foreach (var data in portalData)
                {
                    var portal = new ZonePortal
                    {
                        Id = 0,  // Will be assigned at send time
                        GCType = data.gcType,
                        Name = data.name,
                        PosX = data.posX,
                        PosY = data.posY,
                        PosZ = data.posZ,
                        Heading = data.heading,
                        Width = data.width,
                        Height = data.height,
                        TargetZone = data.targetZone,
                        SpawnPoint = data.spawnPoint,
                        Color = data.color
                    };

                    _zonePortals[zone.id].Add(portal);
                    Debug.LogError($"[InitPortals] ✓ {zone.name}: {portal.Name} (ID: {portal.Id}) → {portal.TargetZone}");
                }
            }

            int totalPortals = _zonePortals.Values.Sum(list => list.Count);
            Debug.LogError($"[InitPortals] ✅ Total portals loaded: {totalPortals}");
        }

        private void InitializeZoneCheckpoints()
        {
            Debug.LogError("[InitCheckpoints] Loading zone checkpoints...");

            foreach (var zone in _zones.Values)
            {
                string zoneName = zone.name.ToLower();
                var checkpointData = DatabaseLoader.GetCheckpointsForZone(zoneName);

                if (checkpointData == null || checkpointData.Count == 0)
                    continue;

                _zoneCheckpoints[zone.id] = new List<ZoneCheckpoint>();

                foreach (var data in checkpointData)
                {
                    var checkpoint = new ZoneCheckpoint
                    {
                        Id = 0,  // Assigned at send time
                        GCType = data.gcType,
                        Name = data.name,
                        PosX = data.posX,
                        PosY = data.posY,
                        PosZ = data.posZ,
                        Heading = data.heading
                    };

                    _zoneCheckpoints[zone.id].Add(checkpoint);
                    Debug.LogError($"[InitCheckpoints] ✓ {zone.name}: {checkpoint.GCType}");
                }
            }

            Debug.LogError($"[InitCheckpoints] ✅ Total checkpoints: {_zoneCheckpoints.Values.Sum(list => list.Count)}");

            foreach (var kvp in _zoneCheckpoints)
            {
                var zone = _zones.ContainsKey(kvp.Key) ? _zones[kvp.Key] : null;
                if (zone == null) continue;
                foreach (var cp in kvp.Value)
                {
                    string cpClass = cp.GCType;
                    if (cpClass.EndsWith("Entity", StringComparison.OrdinalIgnoreCase))
                        cpClass = cpClass.Substring(0, cpClass.Length - 6);
                    _checkpointZoneMap[cpClass] = zone.name;
                }
            }
            Debug.LogError($"[InitCheckpoints] Checkpoint→zone map: {_checkpointZoneMap.Count} entries");
        }

        private enum DungeonPortalRole
        {
            Entry,
            Exit
        }

        private static bool ContainsIgnoreCase(string value, string needle)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int GetDungeon00LevelOrdinal(string zoneName)
        {
            if (string.IsNullOrWhiteSpace(zoneName))
                return -1;
            if (zoneName.IndexOf("boss", StringComparison.OrdinalIgnoreCase) >= 0)
                return 4;

            int idx = zoneName.IndexOf("dungeon00_level", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return -1;

            idx += "dungeon00_level".Length;
            int value = 0;
            int digits = 0;
            while (idx < zoneName.Length && char.IsDigit(zoneName[idx]))
            {
                value = value * 10 + (zoneName[idx] - '0');
                idx++;
                digits++;
            }

            return digits > 0 ? value : -1;
        }

        private static DungeonPortalRole ResolveDungeonPortalRole(string currentZoneName, string gcType, string name, string targetZone, string spawnPoint)
        {
            int currentLevel = GetDungeon00LevelOrdinal(currentZoneName);
            int targetLevel = GetDungeon00LevelOrdinal(targetZone);
            if (targetLevel > 0 && currentLevel > 0 && targetLevel > currentLevel)
                return DungeonPortalRole.Exit;
            if (ContainsIgnoreCase(targetZone, "boss") && currentLevel > 0)
                return DungeonPortalRole.Exit;
            if (ContainsIgnoreCase(gcType, "oneway")
                || ContainsIgnoreCase(name, "to_level")
                || ContainsIgnoreCase(name, "to_boss"))
                return DungeonPortalRole.Exit;

            if (ContainsIgnoreCase(gcType, "hub")
                || ContainsIgnoreCase(name, "to_tutorial")
                || ContainsIgnoreCase(targetZone, "tutorial")
                || ContainsIgnoreCase(targetZone, "town"))
                return DungeonPortalRole.Entry;

            if (targetLevel > 0 && currentLevel > 0 && targetLevel <= currentLevel)
                return DungeonPortalRole.Entry;

            return DungeonPortalRole.Exit;
        }

        private static void ResolveDungeonPortalAnchor(
            DungeonMazeSpawner.ProceduralDungeonSnapshot snapshot,
            DungeonPortalRole role,
            out Vector3 position,
            out float heading,
            out int sourceIndex,
            out string tileType,
            out int gridX,
            out int gridY,
            out Vector3 local,
            out string source)
        {
            if (role == DungeonPortalRole.Entry)
            {
                position = snapshot.EntryPortalSpawn;
                heading = snapshot.EntryPortalHeading;
                sourceIndex = snapshot.EntrySourceIndex;
                tileType = snapshot.EntryTileType;
                gridX = snapshot.EntryGridX;
                gridY = snapshot.EntryGridY;
                local = snapshot.EntryPortalAnchorLocal;
                source = snapshot.EntryPortalAnchorSource;
                return;
            }

            position = snapshot.ExitPortalSpawn;
            heading = snapshot.ExitPortalHeading;
            sourceIndex = snapshot.ExitSourceIndex;
            tileType = snapshot.ExitTileType;
            gridX = snapshot.ExitGridX;
            gridY = snapshot.ExitGridY;
            local = snapshot.ExitPortalAnchorLocal;
            source = snapshot.ExitPortalAnchorSource;
        }

        private const uint DungeonPortalHubColor = 0x8200ADFFu;

        private void SendCombatStart(Managers.DuelManager.DuelInfo duel)
        {
            var challenger = FindConnectionByLogin(duel.ChallengerLogin);
            var target     = FindConnectionByLogin(duel.TargetLogin);
            if (challenger != null)
            {
                SendToClient(challenger, PVPPackets.BuildDuelStatus(
                    PVPPackets.DuelStatusType.InProgress, duel.TargetCharSqlId, 0, 0));
                SendToClient(challenger, PVPPackets.BuildPVPStatusChanged(pvpState: 1, matchId: 0));
            }
            if (target != null)
            {
                SendToClient(target, PVPPackets.BuildDuelStatus(
                    PVPPackets.DuelStatusType.InProgress, duel.ChallengerCharSqlId, 0, 0));
                SendToClient(target, PVPPackets.BuildPVPStatusChanged(pvpState: 1, matchId: 0));
            }
            Debug.LogError($"[PVP-DUEL] Combat start sent: {duel.ChallengerLogin} vs {duel.TargetLogin}");
        }

        private void SendDuelEndPackets(string winnerLogin, string loserLogin, Managers.DuelManager.DuelInfo duel)
        {
            // Resolve CharSqlIds from the duel record so we don't depend on connection state.
            uint winnerCharSqlId = string.Equals(duel.ChallengerLogin, winnerLogin, StringComparison.OrdinalIgnoreCase)
                ? duel.ChallengerCharSqlId : duel.TargetCharSqlId;
            uint loserCharSqlId  = string.Equals(duel.ChallengerLogin, loserLogin,  StringComparison.OrdinalIgnoreCase)
                ? duel.ChallengerCharSqlId : duel.TargetCharSqlId;

            var winnerConn = FindConnectionByLogin(winnerLogin);
            var loserConn  = FindConnectionByLogin(loserLogin);

            if (winnerConn != null)
            {
                SendToClient(winnerConn, PVPPackets.BuildDuelStatus(
                    PVPPackets.DuelStatusType.Won, loserCharSqlId, 0, 0));
                SendToClient(winnerConn, PVPPackets.BuildPVPStatusChanged(pvpState: 0, matchId: 0));
            }
            if (loserConn != null)
            {
                SendToClient(loserConn, PVPPackets.BuildDuelStatus(
                    PVPPackets.DuelStatusType.Lost, winnerCharSqlId, 0, 0));
                SendToClient(loserConn, PVPPackets.BuildPVPStatusChanged(pvpState: 0, matchId: 0));
            }
            Debug.LogError($"[PVP-DUEL] End packets sent: winner={winnerLogin} loser={loserLogin}");
        }

        private RRConnection FindConnectionByLogin(string loginName)
        {
            return FindConnectionByName(loginName);
        }

        // ══════════════════════════════════════════════════════════════
        // PVP MATCH SYSTEM (Phase 3) — opcodes 0x29/0x2A/0x2B/0x2C
        // Backed by Managers.PVPMatchManager.Instance (singleton).
        // ══════════════════════════════════════════════════════════════
        private DateTime _lastMatchmakingTick = DateTime.MinValue;
    }
}
