using System;
using System.Collections.Generic;
using DungeonRunners.Engine;
using DungeonRunners.Networking;
using DungeonRunners.Networking.Sync;
using DungeonRunners.Core;
using DungeonRunners.Data;
using System.Linq;
using System.IO;
namespace DungeonRunners.Combat
{
    public class CombatManager
    {
        private static CombatManager _instance;
        public static CombatManager Instance => _instance ??= new CombatManager();

        private Dictionary<uint, Monster> _activeMonsters = new Dictionary<uint, Monster>();
        private Dictionary<uint, CombatPlayer> _players = new Dictionary<uint, CombatPlayer>();
        private readonly Dictionary<string, NativeRoomRuntime> _roomRuntimes = new Dictionary<string, NativeRoomRuntime>(StringComparer.OrdinalIgnoreCase);
        private string _currentRoomRuntimeKey = NativeRoomRuntime.DefaultInstanceKey;
        private readonly List<uint> _nativeEntityOrder = new List<uint>();
        private readonly HashSet<uint> _nativeEntityOrderSet = new HashSet<uint>();
        private Dictionary<uint, float> _playerCombatAdvanceTime = new Dictionary<uint, float>();
        private List<RespawnEntry> _respawnQueue = new List<RespawnEntry>();
        private Dictionary<uint, uint> _monsterRuntimeHPWire = new Dictionary<uint, uint>();
        private Dictionary<uint, MonsterHpAuthorityState> _monsterHPAuthority = new Dictionary<uint, MonsterHpAuthorityState>();
        private HashSet<uint> _monsterRuntimeDamageCommitted = new HashSet<uint>();
        private Dictionary<uint, float> _monsterHPRegenLastTime = new Dictionary<uint, float>();
        private Dictionary<uint, float> _monsterHPRegenCarryWire = new Dictionary<uint, float>();
        private Dictionary<uint, ushort> _monsterHPRegenCooldownTicks = new Dictionary<uint, ushort>();
        private Dictionary<uint, float> _monsterManaRegenLastTime = new Dictionary<uint, float>();
        private Dictionary<uint, ushort> _monsterManaRegenCooldownTicks = new Dictionary<uint, ushort>();
        private Dictionary<uint, float> _monsterDeathUpdateAccum = new Dictionary<uint, float>();
        private Dictionary<uint, string> _monsterStateTraceSignatures = new Dictionary<uint, string>();
        private readonly Dictionary<string, NativeEncounterRuntime> _nativeEncounterRuntimes = new Dictionary<string, NativeEncounterRuntime>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, ActiveMonsterModifier> _activeMonsterModifiers = new Dictionary<string, ActiveMonsterModifier>(StringComparer.Ordinal);
        private Dictionary<string, ActivePlayerDamageModifier> _activePlayerDamageModifiers = new Dictionary<string, ActivePlayerDamageModifier>(StringComparer.Ordinal);
        private readonly Dictionary<string, uint> _playerModifierNetworkIds = new Dictionary<string, uint>(StringComparer.Ordinal);
        private uint _nextPlayerModifierNetworkId = 0x40000000u;
        private uint _nextPlayerModifierStackSerial = 1u;
        private Queue<PendingModifierKill> _pendingModifierKills = new Queue<PendingModifierKill>();
        private Dictionary<uint, float> _monsterFarTargetActionLogTime = new Dictionary<uint, float>();
        private readonly List<PendingMonsterProjectileImpact> _pendingMonsterProjectiles = new List<PendingMonsterProjectileImpact>();
        private long _nextMonsterProjectileSequence;
        private const int NATIVE_PROJECTILE_SPEED_FIXED_DENOMINATOR = 0x1e00;
        private bool _advancingMonsterModifiers;
        private float _lastCombatTraceSummaryTime;
        private uint _nativeCombatTick;
        private float _nativeCombatTime = -1f;
        private bool _hasCompletedNativeEntityUpdate;
        private uint _lastCompletedNativeEntityUpdateTick;
        private float _lastCompletedNativeEntityUpdateTime = -1f;
        private bool _hasCompletedNativeSubEntityUpdate;
        private uint _lastCompletedNativeSubEntityUpdateTick;
        private float _lastCompletedNativeSubEntityUpdateTime = -1f;
        private const float NATIVE_UNIT_TICK_INTERVAL = 1f / 30f;
        private const int MONSTER_HP_HISTORY_LIMIT = 32;
        private const ushort NATIVE_DAMAGE_REGEN_COOLDOWN_TICKS = 300;
        private const int NATIVE_UNIT_REGEN_DIVISOR = 3000;
        private const int NATIVE_PERCENT_SCALE = 100;
        private const ushort NATIVE_STOCKUNIT_FADE_TICKS = 35;
        private const float NATIVE_CONTACT_RANGE_EPSILON = 1f / 16f;
        private const float NATIVE_DEFAULT_UNIT_PERCEPTION = 100f;
        private const float NATIVE_DEFAULT_UNIT_SCAN_FREQUENCY = 1f;
        private const float NATIVE_DEFAULT_UNIT_FLEE_RANGE = 0f;
        private const int NATIVE_DEFAULT_UNIT_COLLISION_BAND = 1;
        private const int NATIVE_DEFAULT_UNIT_COLLISION_PRIORITY = 0;
        private const bool NATIVE_DEFAULT_UNIT_AUTO_SCAN = false;
        private const bool NATIVE_DEFAULT_UNIT_AVOID_UNITS = true;
        private const bool NATIVE_DEFAULT_UNIT_TURN_BEFORE_MOVING = true;
        private const bool NATIVE_DEFAULT_UNIT_PLAYER_CONTROLLED = true;
        private const float NATIVE_DEFAULT_MONSTER_AGGRO_RANGE = 40f;
        private const float NATIVE_DEFAULT_MONSTER_SHOUT_RANGE = 50f;
        private const float NATIVE_DEFAULT_MONSTER_WANDER_RANGE = 100f;
        private const float NATIVE_DEFAULT_MONSTER_LEASH_RANGE = 0f;
        private const float NATIVE_DEFAULT_MONSTER_TELEPORT_FREQUENCY = 150f;
        private const float NATIVE_DEFAULT_MONSTER_TELEPORT_LIMBO_TIME = 60f;
        private const float NATIVE_DEFAULT_MONSTER_RETREAT_RANGE_SQUARED = 640000f;
        private const float NATIVE_DEFAULT_MONSTER_BASE_TIME = 30f;
        private const float NATIVE_DEFAULT_MONSTER_VARIABLE_TIME = 0f;
        private const bool NATIVE_DEFAULT_MONSTER_RETREATABLE = true;
        private const bool NATIVE_DEFAULT_MONSTER_LEASHED = false;
        private const bool NATIVE_DEFAULT_MONSTER_USE_IDLE_TIME = false;
        private const byte NATIVE_PLAYER_STUN_ACTION_KNOCKBACK_ID = 0x0A;
        private const byte NATIVE_RUNTIME_MODIFIER_SOURCE_IS_SELF = 1;

        private class MonsterHpAuthorityState
        {
            public bool RuntimeInitialized;
            public uint RuntimeHPWire;
            public float LastClientHPReportTime;
            public uint LastClientHPReportWire;
            public uint LastPacketHPWire;
            public readonly List<MonsterHpHistoryEntry> History = new List<MonsterHpHistoryEntry>();
        }

        private class MonsterHpHistoryEntry
        {
            public uint BeforeHPWire;
            public uint AfterHPWire;
            public uint NativeTick;
            public float NativeTime;
            public string Source;
            public bool CommittedDamage;
            public bool SubEntityMutation;
            public string MutationPhase;
        }

        public struct NativeHpVisibilityCutoff
        {
            public uint Tick;
            public float Time;
            public bool IncludeSubEntityEffects;
            public string Reason;
            public string Phase;
            public SyncContext Context;
            public string SourceContext;
            public bool HasEntityCutoff;
            public uint LastEntityTick;
            public float LastEntityTime;
            public bool HasSubEntityCutoff;
            public uint LastSubEntityTick;
            public float LastSubEntityTime;
        }

        private class ActiveMonsterModifier
        {
            public uint TargetEntityId;
            public uint SourceEntityId;
            public PlayerState SourceState;
            public SpellData Spell;
            public int SkillLevel;
            public int LastNativeTick;
            public ushort DurationTicksInitial;
            public int DurationTicksRemaining;
            public ushort FrequencyTicks;
            public int FrequencyCountdownTicks;
            public int TicksApplied;
            public string ModifierKey;
        }

        private class ActivePlayerDamageModifier
        {
            public uint TargetEntityId;
            public uint SourceEntityId;
            public string SkillPath;
            public string EffectPath;
            public string ModifierPath;
            public string ModifierEffectPath;
            public string AttackType;
            public string DamageType;
            public int DamageTypeId;
            public byte DamageKind;
            public float DamageMod;
            public float DamageVolatility;
            public int SourceLevel;
            public int SourceIntellect;
            public int SourceAgility;
            public uint PowerLevel;
            public ushort DurationTicks;
            public ushort FrequencyTicks;
            public float ApplyTime;
            public float NextTickTime;
            public float Frequency;
            public float ExpireTime;
            public int MaxTicks;
            public int TicksApplied;
            public bool RemoveOnDeath;
            public string StackRule;
            public string ModifierKey;
        }

        public class MonsterModifierApplyResult
        {
            public bool AppliedModifier;
            public bool DamageApplied;
            public bool Died;
            public uint OldHPWire;
            public uint NewHPWire;
            public int TicksApplied;
            public string Reason;
        }

        private class MonsterHitPointRegenSkillEffect
        {
            public string SkillPath;
            public string EffectPath;
            public string ModifierPath;
            public int HitPointRegenBonus;
            public ushort DurationTicks;
            public float DurationSeconds;
            public bool RemoveOnDeath;
            public string StackRule;
        }

        private class MonsterWeaponDamageSkillEffect
        {
            public string SkillPath;
            public string EffectPath;
            public string WeaponEffectPath;
            public int AttackRatingMod;
            public int DamageMod;
            public bool HasKnockBack;
            public string KnockBackEffectPath;
            public int KnockBackStrength;
            public int KnockBackChanceWire;
            public bool HasKnockDown;
            public string KnockDownEffectPath;
            public int KnockDownStrength;
            public int KnockDownChanceWire;
            public MonsterDamageModifierSkillEffect DamageModifier;
        }

        private class MonsterStunActionSkillEffect
        {
            public string SkillPath;
            public string EffectPath;
            public string ActionEffectPath;
            public string ActionFamily;
            public int Strength;
            public int ChanceWire;
            public bool IsKnockDown;
        }

        public class PlayerStunActionResolved
        {
            public string SkillPath;
            public string EffectPath;
            public string EffectFamily;
            public string ActionClassName;
            public byte ActionClassId;
            public ushort HeadingWire;
            public ushort StrengthWire;
            public int AuthoredStrength;
            public int ChanceWire;
            public uint ChanceRaw;
            public uint ChanceRoll;
            public int StunResistWire;
            public uint StunRaw;
            public uint StunRoll;
            public string Source;
            public bool KnockDownPlayerBranch;
        }

        public class PlayerModifierLifecycle
        {
            public string Visual;
            public string InitSound;
            public string InitEffect;
            public string RemoveEffect;
            public string OverlayIcon;
            public int OverlayDuration;
            public bool HasClientLocalLifecycle => !string.IsNullOrWhiteSpace(Visual)
                || !string.IsNullOrWhiteSpace(InitSound)
                || !string.IsNullOrWhiteSpace(InitEffect)
                || !string.IsNullOrWhiteSpace(RemoveEffect)
                || !string.IsNullOrWhiteSpace(OverlayIcon)
                || OverlayDuration > 0;
        }

        public class PlayerModifierNetworkEvent
        {
            public bool Add;
            public string ModifierKey;
            public string GCType;
            public uint ModifierId;
            public byte Level;
            public uint PowerLevel;
            public uint DurationTicks;
            public byte SourceIsSelf;
            public bool Replace;
            public string SkillPath;
            public string EffectPath;
            public string Source;
            public string Native;
            public PlayerModifierLifecycle Lifecycle;
        }

        private class MonsterDamageModifierSkillEffect
        {
            public string SkillPath;
            public string EffectPath;
            public string ModifierPath;
            public string ModifierEffectPath;
            public string AttackType;
            public string DamageType;
            public int DamageTypeId;
            public byte DamageKind;
            public float DamageMod;
            public float DamageVolatility;
            public float DurationSeconds;
            public ushort DurationTicks;
            public float FrequencySeconds;
            public ushort FrequencyTicks;
            public bool RemoveOnDeath;
            public string StackRule;
        }

        private class MonsterSkillEffectSupport
        {
            public string SkillPath;
            public string EffectPath;
            public readonly List<string> Families = new List<string>();
            public readonly List<string> UnsupportedFamilies = new List<string>();
            public string Status = "UNKNOWN";
            public string ModifierPath;
            public string Attribute;
            public string Reason;
        }

        public class PendingModifierKill
        {
            public uint SourceEntityId;
            public uint TargetEntityId;
            public string Source;
            public float NativeDamageTime;
        }

        public bool HasPendingModifierKills => _pendingModifierKills.Count > 0;
        public uint NativeCombatTick => _nativeCombatTick;
        public float NativeCombatTime => GetNativeCombatTime();
        public uint LastCompletedNativeEntityUpdateTick => _hasCompletedNativeEntityUpdate ? _lastCompletedNativeEntityUpdateTick : 0u;
        public float LastCompletedNativeEntityUpdateTime => _hasCompletedNativeEntityUpdate ? _lastCompletedNativeEntityUpdateTime : Mathf.Max(0f, GetNativeCombatTime() - NATIVE_UNIT_TICK_INTERVAL);
        public uint LastCompletedNativeSubEntityUpdateTick => _hasCompletedNativeSubEntityUpdate ? _lastCompletedNativeSubEntityUpdateTick : 0u;
        public float LastCompletedNativeSubEntityUpdateTime => _hasCompletedNativeSubEntityUpdate ? _lastCompletedNativeSubEntityUpdateTime : Mathf.Max(0f, GetNativeCombatTime() - NATIVE_UNIT_TICK_INTERVAL);

        public void SetNativeCombatClock(uint tick, float time, string source = null)
        {
            if (time < 0f) time = Time.time;
            bool backwards = _nativeCombatTime >= 0f && time + 0.0001f < _nativeCombatTime;
            if (backwards)
            {
                Debug.LogError($"[NATIVE-COMBAT-CLOCK] ignored backwards clock tick={tick} time={time:F3} currentTick={_nativeCombatTick} currentTime={_nativeCombatTime:F3} source={source ?? "unknown"}");
                return;
            }
            _nativeCombatTick = tick;
            _nativeCombatTime = time;
        }

        public void MarkNativeEntityUpdateCompleted(uint tick, float time, string source = null)
        {
            if (time < 0f) time = GetNativeCombatTime();
            if (_hasCompletedNativeEntityUpdate && time + 0.0001f < _lastCompletedNativeEntityUpdateTime)
            {
                return;
            }

            _hasCompletedNativeEntityUpdate = true;
            _lastCompletedNativeEntityUpdateTick = tick;
            _lastCompletedNativeEntityUpdateTime = time;
        }

        public void MarkNativeSubEntityUpdateCompleted(uint tick, float time, string source = null)
        {
            if (time < 0f) time = GetNativeCombatTime();
            if (_hasCompletedNativeSubEntityUpdate && time + 0.0001f < _lastCompletedNativeSubEntityUpdateTime)
            {
                return;
            }

            _hasCompletedNativeSubEntityUpdate = true;
            _lastCompletedNativeSubEntityUpdateTick = tick;
            _lastCompletedNativeSubEntityUpdateTime = time;
        }

        public void GetNativeValidationCutoff(out uint tick, out float time)
        {
            if (_hasCompletedNativeSubEntityUpdate &&
                (!_hasCompletedNativeEntityUpdate || _lastCompletedNativeSubEntityUpdateTime > _lastCompletedNativeEntityUpdateTime + 0.0001f))
            {
                tick = _lastCompletedNativeSubEntityUpdateTick;
                time = _lastCompletedNativeSubEntityUpdateTime;
                return;
            }

            if (_hasCompletedNativeEntityUpdate)
            {
                tick = _lastCompletedNativeEntityUpdateTick;
                time = _lastCompletedNativeEntityUpdateTime;
                return;
            }

            tick = _nativeCombatTick > 0 ? _nativeCombatTick - 1u : 0u;
            time = Mathf.Max(0f, GetNativeCombatTime() - NATIVE_UNIT_TICK_INTERVAL);
        }

        public NativeHpVisibilityCutoff GetEntitySynchInfoValidationCutoff(SyncContext context, string source = null)
        {
            NativeHpVisibilityCutoff cutoff = new NativeHpVisibilityCutoff
            {
                Tick = _nativeCombatTick > 0 ? _nativeCombatTick - 1u : 0u,
                Time = Mathf.Max(0f, GetNativeCombatTime() - NATIVE_UNIT_TICK_INTERVAL),
                IncludeSubEntityEffects = false,
                Reason = "fallback-previous-entity-update",
                Phase = "fallback-previous-entity",
                Context = context,
                SourceContext = source ?? "unknown",
                HasEntityCutoff = _hasCompletedNativeEntityUpdate,
                LastEntityTick = _hasCompletedNativeEntityUpdate ? _lastCompletedNativeEntityUpdateTick : 0u,
                LastEntityTime = _hasCompletedNativeEntityUpdate ? _lastCompletedNativeEntityUpdateTime : -1f,
                HasSubEntityCutoff = _hasCompletedNativeSubEntityUpdate,
                LastSubEntityTick = _hasCompletedNativeSubEntityUpdate ? _lastCompletedNativeSubEntityUpdateTick : 0u,
                LastSubEntityTime = _hasCompletedNativeSubEntityUpdate ? _lastCompletedNativeSubEntityUpdateTime : -1f
            };

            bool subEntityAfterEntity = _hasCompletedNativeSubEntityUpdate
                && (!_hasCompletedNativeEntityUpdate
                    || _lastCompletedNativeSubEntityUpdateTick > _lastCompletedNativeEntityUpdateTick
                    || _lastCompletedNativeSubEntityUpdateTime > _lastCompletedNativeEntityUpdateTime + 0.0001f);
            bool subEntityStrictlyBeforeEntity = _hasCompletedNativeSubEntityUpdate
                && _hasCompletedNativeEntityUpdate
                && (_lastCompletedNativeSubEntityUpdateTick < _lastCompletedNativeEntityUpdateTick
                    || (_lastCompletedNativeSubEntityUpdateTick == _lastCompletedNativeEntityUpdateTick
                        && _lastCompletedNativeSubEntityUpdateTime + 0.0001f < _lastCompletedNativeEntityUpdateTime));
            bool subEntitySameEntityTick = _hasCompletedNativeSubEntityUpdate
                && _hasCompletedNativeEntityUpdate
                && _lastCompletedNativeSubEntityUpdateTick == _lastCompletedNativeEntityUpdateTick
                && Mathf.Abs(_lastCompletedNativeSubEntityUpdateTime - _lastCompletedNativeEntityUpdateTime) <= 0.0001f;

            if (IsRuntimeMonsterHPSuffixContext(context) && subEntityAfterEntity)
            {
                if (_hasCompletedNativeEntityUpdate)
                {
                    cutoff.Tick = _lastCompletedNativeEntityUpdateTick;
                    cutoff.Time = _lastCompletedNativeEntityUpdateTime;
                    cutoff.Phase = "entity-before-subentity";
                }
                else
                {
                    cutoff.Phase = "fallback-before-subentity";
                }
                cutoff.Reason = "completed-subentity-pending-client-update";
                cutoff.IncludeSubEntityEffects = false;
            }
            else if (_hasCompletedNativeEntityUpdate)
            {
                cutoff.Tick = _lastCompletedNativeEntityUpdateTick;
                cutoff.Time = _lastCompletedNativeEntityUpdateTime;
                cutoff.Reason = "completed-entity-update";
                cutoff.Phase = "entity";
                cutoff.IncludeSubEntityEffects = subEntityStrictlyBeforeEntity;
                if (cutoff.IncludeSubEntityEffects)
                    cutoff.Phase = "entity-after-subentity";
            }

            Debug.LogError($"[NATIVE-HP-VISIBILITY] context={context} phase={cutoff.Phase} tick={cutoff.Tick} time={cutoff.Time:F3} includeSubEntity={cutoff.IncludeSubEntityEffects} reason={cutoff.Reason} source={source ?? "unknown"} lastEntity={(_hasCompletedNativeEntityUpdate ? _lastCompletedNativeEntityUpdateTick.ToString() : "none")}@{(_hasCompletedNativeEntityUpdate ? _lastCompletedNativeEntityUpdateTime.ToString("F3") : "none")} lastSubEntity={(_hasCompletedNativeSubEntityUpdate ? _lastCompletedNativeSubEntityUpdateTick.ToString() : "none")}@{(_hasCompletedNativeSubEntityUpdate ? _lastCompletedNativeSubEntityUpdateTime.ToString("F3") : "none")} subEntityAfterEntity={subEntityAfterEntity} subEntityStrictlyBeforeEntity={subEntityStrictlyBeforeEntity} subEntitySameEntityTick={subEntitySameEntityTick}");
            if (cutoff.Reason.StartsWith("fallback", StringComparison.OrdinalIgnoreCase) ||
                cutoff.Phase.StartsWith("fallback", StringComparison.OrdinalIgnoreCase) ||
                cutoff.Reason.Equals("completed-subentity-pending-client-update", StringComparison.OrdinalIgnoreCase))
            {
                RuntimeEvidenceManager.LogFallbackHit(
                    "hp-cutoff",
                    cutoff.Reason,
                    $"context={context} phase={cutoff.Phase} source='{source ?? "unknown"}' tick={cutoff.Tick}",
                    64);
            }
            return cutoff;
        }

        public float GetNativeCombatTime()
        {
            return _nativeCombatTime >= 0f ? _nativeCombatTime : Time.time;
        }

        public PendingModifierKill DequeuePendingModifierKill()
        {
            return _pendingModifierKills.Count > 0 ? _pendingModifierKills.Dequeue() : null;
        }

        // Maps ANY component ID (EntityId, BehaviorId, SkillsId, etc.) to the monster's EntityId
        private Dictionary<uint, uint> _componentToEntityMap = new Dictionary<uint, uint>();

        // Maps client-sent target IDs to our server entity IDs (learned at runtime)
        private Dictionary<uint, uint> _clientToServerIdMap = new Dictionary<uint, uint>();

        private uint _nextMonsterId = 50000;
        private float? _avatarCombatRadius;

        public event Action<Monster> OnMonsterSpawned;
        public event Action<Monster> OnMonsterDespawned;
        public event Action<DamageEvent> OnDamageDealt;
        public event Action<uint, uint> OnEntityDeath;
        public event Action<Monster> OnMonsterPositionChanged;
        public event Action<Monster, CombatPlayer, byte> OnMonsterAttackStarted;
        public event Action<Monster, CombatPlayer, bool, uint> OnMonsterAttackResolved;
        public event Action<Monster, CombatPlayer, bool, uint, string> OnPlayerDamageResolved;
        public event Action<Monster, CombatPlayer, PlayerStunActionResolved> OnPlayerStunActionResolved;
        public event Action<Monster, CombatPlayer, PlayerModifierNetworkEvent> OnPlayerModifierNetworkEvent;

        // OnMonsterAttack and OnMonsterAggro REMOVED.
        // Client is authoritative on combat — sends type 9 for aggro.
        // Server-side aggro used monster.PosX which was always spawn position (never updated).

        public uint AllocateComponentId()
        {
            return _nextMonsterId++;
        }
        public CombatManager()
        {
            Debug.LogError("[CombatManager] Initialized");
            Debug.LogError("[CombatManager] Room RNG pending native seed");
        }

        private void RegisterNativeEntityOrder(uint entityId)
        {
            if (entityId == 0 || !_nativeEntityOrderSet.Add(entityId))
                return;
            _nativeEntityOrder.Add(entityId);
        }

        private void UnregisterNativeEntityOrder(uint entityId)
        {
            if (!_nativeEntityOrderSet.Remove(entityId))
                return;
            _nativeEntityOrder.Remove(entityId);
        }

        public List<uint> GetNativeEntityOrderSnapshot()
        {
            return new List<uint>(_nativeEntityOrder);
        }

        public bool IsNativeMonsterEntity(uint entityId)
        {
            return _activeMonsters.ContainsKey(entityId);
        }

        public bool IsNativePlayerEntity(uint entityId)
        {
            return _players.ContainsKey(entityId);
        }

        public bool TryGetCombatPlayerForController(uint entityId, out CombatPlayer player)
        {
            return _players.TryGetValue(entityId, out player);
        }

        public IEnumerable<Monster> GetMonstersInZone(string zoneName)
        {
            string instanceKey = string.IsNullOrWhiteSpace(zoneName)
                ? null
                : NativeRoomRuntime.NormalizeInstanceKey(zoneName);
            return _activeMonsters.Values.Where(m =>
                string.Equals(m.ZoneName, zoneName, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(instanceKey) && MatchesInstance(m, instanceKey)));
        }

        /// <summary>
        /// Removes all monsters tagged with the given zone name (or any instance of it).
        /// Matches both exact "dungeon00_level01" and instanced "dungeon00_level01_inst0".
        /// Used when @behavior changes mode so mobs respawn with new behavior on next zone entry.
        /// </summary>
        public int ClearZoneMobs(string zoneName)
        {
            var toRemove = _activeMonsters.Values
                .Where(m => m.ZoneName != null && (
                    string.Equals(m.ZoneName, zoneName, StringComparison.OrdinalIgnoreCase) ||
                    m.ZoneName.StartsWith(zoneName + "_inst", StringComparison.OrdinalIgnoreCase)))
                .Select(m => m.EntityId)
                .ToList();

            foreach (uint eid in toRemove)
            {
                if (_activeMonsters.TryGetValue(eid, out var mon))
                {
                    _componentToEntityMap.Remove(mon.BehaviorId);
                    _componentToEntityMap.Remove(mon.SkillsId);
                    _componentToEntityMap.Remove(mon.ManipulatorsId);
                    _componentToEntityMap.Remove(mon.ModifiersId);
                    _componentToEntityMap.Remove(mon.UnitId);
                    _componentToEntityMap.Remove(eid);
                    _monsterRuntimeHPWire.Remove(eid);
                    _monsterHPAuthority.Remove(eid);
                    _monsterRuntimeDamageCommitted.Remove(eid);
                    _monsterHPRegenLastTime.Remove(eid);
                    _monsterHPRegenCarryWire.Remove(eid);
                    _monsterHPRegenCooldownTicks.Remove(eid);
                    _monsterManaRegenLastTime.Remove(eid);
                    _monsterManaRegenCooldownTicks.Remove(eid);
                    _monsterStateTraceSignatures.Remove(eid);
                    _monsterFarTargetActionLogTime.Remove(eid);
                    RemoveMonsterModifiersForTarget(eid, "ClearZoneMobs");
                    RemovePlayerModifiersFromSource(eid, "ClearZoneMobs");
                    WanderSimulator.Instance.UnregisterEntity(eid);
                    UnregisterNativeEntityOrder(eid);
                }
                _activeMonsters.Remove(eid);
            }

            Debug.LogError($"[BEHAVIOR] ClearZoneMobs('{zoneName}'): removed {toRemove.Count} monsters");
            return toRemove.Count;
        }
        public CombatPlayer RegisterPlayer(uint entityId, string name, PlayerState state, float posX, float posY, string instanceKey = null)
        {
            string normalizedInstanceKey = string.IsNullOrWhiteSpace(instanceKey)
                ? null
                : NativeRoomRuntime.NormalizeInstanceKey(instanceKey);
            if (string.IsNullOrWhiteSpace(normalizedInstanceKey))
            {
                RuntimeEvidenceManager.LogFallbackHit("rng-instance", "register-player-missing-instance", $"player={entityId} name='{name ?? ""}'", 64);
                Debug.LogError($"[RNG-INSTANCE] source=RegisterPlayer reason=missing-instance player={entityId} name='{name ?? ""}'");
            }

            var player = new CombatPlayer
            {
                EntityId = entityId,
                Name = name,
                PlayerState = state,
                PosX = posX,
                PosY = posY,
                InstanceKey = normalizedInstanceKey,
                IsAlive = true
            };

            _players[entityId] = player;
            if (state != null)
            {
                state.OnNativeAttributeModifierRemoved -= HandlePlayerNativeAttributeModifierRemoved;
                state.OnNativeAttributeModifierRemoved += HandlePlayerNativeAttributeModifierRemoved;
            }
            RegisterNativeEntityOrder(entityId);
            _playerCombatAdvanceTime[entityId] = GetNativeCombatTime();
            Debug.LogError($"[Combat] Registered player {name} (ID:{entityId}) at pos=({posX:F1}, {posY:F1})");
            return player;
        }
        public string DumpPlayerIds()
        {
            var ids = string.Join(", ", _players.Keys);
            return $"[{ids}] (count={_players.Count})";
        }
        public void UpdatePlayerPosition(uint entityId, float posX, float posY)
        {
            if (_players.TryGetValue(entityId, out var player))
            {
                player.PosX = posX;
                player.PosY = posY;
            }
        }

        public bool SyncMonsterWanderClientVisiblePosition(Monster monster, string source)
        {
            if (monster == null || !monster.IsAlive || monster.AggroTriggered)
                return false;
            if (!TryGetMonsterWanderClientVisiblePosition(monster, out float visualX, out float visualY))
                return false;

            float delta = Distance2D(monster.PosX, monster.PosY, visualX, visualY);
            if (delta <= 0.001f)
                return false;

            float oldX = monster.PosX;
            float oldY = monster.PosY;
            monster.PosX = visualX;
            monster.PosY = visualY;
            Debug.LogError($"[WANDER-SYNC] {monster.Name}#{monster.EntityId} source={source ?? "unknown"} authoritative=({oldX:F1},{oldY:F1})->clientVisible=({visualX:F1},{visualY:F1}) delta={delta:F1}");
            return true;
        }

        public bool TryGetMonsterWanderClientVisiblePosition(Monster monster, out float visualX, out float visualY)
        {
            visualX = monster != null ? monster.PosX : 0f;
            visualY = monster != null ? monster.PosY : 0f;
            if (monster == null || !monster.IsAlive || monster.AggroTriggered)
                return false;
            return WanderSimulator.Instance.TryGetClientVisiblePosition(monster.EntityId, out visualX, out visualY);
        }

        public void ResetMonsterClientVisiblePosition(Monster monster, float posX, float posY, float nativeNow, string source)
        {
            if (monster == null) return;
            if (nativeNow < 0f) nativeNow = GetNativeCombatTime();
            monster.ClientVisibleMoveInitialized = true;
            monster.ClientVisibleMoveActive = false;
            monster.ClientVisiblePosX = posX;
            monster.ClientVisiblePosY = posY;
            monster.ClientVisibleMoveTargetX = posX;
            monster.ClientVisibleMoveTargetY = posY;
            monster.ClientVisibleMoveLastTime = nativeNow;
            Debug.LogError($"[MON-CLIENT-POS] reset {monster.Name}#{monster.EntityId} source={source ?? "unknown"} visible=({posX:F1},{posY:F1}) server=({monster.PosX:F1},{monster.PosY:F1}) nativeNow={nativeNow:F3}");
        }

        public void RecordMonsterMoveClientVisible(Monster monster, float targetX, float targetY, float nativeNow, string source)
        {
            if (monster == null || !monster.IsAlive) return;
            if (nativeNow < 0f) nativeNow = GetNativeCombatTime();
            if (!monster.ClientVisibleMoveInitialized)
                ResetMonsterClientVisiblePosition(monster, monster.PosX, monster.PosY, nativeNow, $"{source ?? "unknown"}-lazy");

            AdvanceMonsterClientVisiblePosition(monster, nativeNow);
            monster.ClientVisibleMoveTargetX = targetX;
            monster.ClientVisibleMoveTargetY = targetY;
            monster.ClientVisibleMoveLastTime = nativeNow;
            monster.ClientVisibleMoveActive = Distance2D(monster.ClientVisiblePosX, monster.ClientVisiblePosY, targetX, targetY) > 0.001f;
            Debug.LogError($"[MON-CLIENT-POS] move {monster.Name}#{monster.EntityId} source={source ?? "unknown"} visible=({monster.ClientVisiblePosX:F1},{monster.ClientVisiblePosY:F1}) dest=({targetX:F1},{targetY:F1}) server=({monster.PosX:F1},{monster.PosY:F1}) speed={ResolveMonsterMovementSpeed(monster):F1} nativeNow={nativeNow:F3}");
        }

        public bool TryGetMonsterClientVisiblePosition(Monster monster, float nativeNow, out float visualX, out float visualY)
        {
            visualX = monster != null ? monster.PosX : 0f;
            visualY = monster != null ? monster.PosY : 0f;
            if (monster == null || !monster.IsAlive)
                return false;

            if (!monster.AggroTriggered)
                return TryGetMonsterWanderClientVisiblePosition(monster, out visualX, out visualY);

            if (nativeNow < 0f) nativeNow = GetNativeCombatTime();
            if (!monster.ClientVisibleMoveInitialized)
                ResetMonsterClientVisiblePosition(monster, monster.PosX, monster.PosY, nativeNow, "client-visible-lazy");

            AdvanceMonsterClientVisiblePosition(monster, nativeNow);
            visualX = monster.ClientVisiblePosX;
            visualY = monster.ClientVisiblePosY;
            return true;
        }

        private void AdvanceMonsterClientVisiblePosition(Monster monster, float nativeNow)
        {
            if (monster == null || !monster.ClientVisibleMoveInitialized)
                return;
            if (nativeNow < monster.ClientVisibleMoveLastTime)
            {
                monster.ClientVisibleMoveLastTime = nativeNow;
                return;
            }

            float elapsed = nativeNow - monster.ClientVisibleMoveLastTime;
            if (elapsed <= 0.0001f)
                return;

            if (!monster.ClientVisibleMoveActive)
            {
                monster.ClientVisibleMoveLastTime = nativeNow;
                return;
            }

            float dx = monster.ClientVisibleMoveTargetX - monster.ClientVisiblePosX;
            float dy = monster.ClientVisibleMoveTargetY - monster.ClientVisiblePosY;
            float distance = Mathf.Sqrt(dx * dx + dy * dy);
            float speed = ResolveMonsterMovementSpeed(monster);
            float step = speed * elapsed;
            if (distance <= 0.001f || speed <= 0f || step >= distance)
            {
                monster.ClientVisiblePosX = monster.ClientVisibleMoveTargetX;
                monster.ClientVisiblePosY = monster.ClientVisibleMoveTargetY;
                monster.ClientVisibleMoveActive = false;
                monster.ClientVisibleMoveLastTime = nativeNow;
                return;
            }

            float invDistance = 1f / distance;
            monster.ClientVisiblePosX += dx * invDistance * step;
            monster.ClientVisiblePosY += dy * invDistance * step;
            monster.ClientVisibleMoveLastTime = nativeNow;
        }

        public void EngageMonsterFromClientAction(Monster monster, uint playerEntityId, bool nativeWeaponUseStarted = false)
        {
            if (monster == null || !monster.IsAlive) return;
            if (monster.DeathPendingClientConfirmation) return;
            if (!_players.TryGetValue(playerEntityId, out var player) || player == null) return;
            float allowedRange = ResolveMonsterEffectiveAttackRange(monster);
            float monsterX = monster.PosX;
            float monsterY = monster.PosY;
            TryGetMonsterClientVisiblePosition(monster, GetNativeCombatTime(), out monsterX, out monsterY);
            float dist = Distance2D(monsterX, monsterY, player.PosX, player.PosY);
            float contactRange = ResolveNativeClientContactRange(monster, player, allowedRange);
            bool inNativeContact = contactRange > 0f && dist <= contactRange + NATIVE_CONTACT_RANGE_EPSILON;
            float aggroRange = ResolveMonsterAggroAdmissionRange(monster);
            bool inAuthoredAggro = aggroRange > 0f && dist <= aggroRange + NATIVE_CONTACT_RANGE_EPSILON;
            float nativeNow = GetNativeCombatTime();
            bool alreadyTargetingPlayer = monster.AggroTriggered && monster.TargetId == player.EntityId;
            bool contactWasActive = monster.CombatContactTargetId == player.EntityId && monster.CombatContactUntil > nativeNow;
            if (!alreadyTargetingPlayer && !inAuthoredAggro && !inNativeContact && !nativeWeaponUseStarted)
            {
                if (monster.CombatContactTargetId == player.EntityId)
                {
                    monster.CombatContactTargetId = 0;
                    monster.CombatContactUntil = 0f;
                }
                Debug.LogError($"[AGGRO-OBSERVE] client intent outside aggro {monster.Name}#{monster.EntityId}->{player.Name} dist={dist:F1} aggro={aggroRange:F1} targetSearch={ResolveMonsterTargetSearchRange(monster, false):F1} nativeRange={contactRange:F1} action=log-only native=MonsterBehavior2::onAttacked");
                return;
            }
            if (!alreadyTargetingPlayer && !inAuthoredAggro && !inNativeContact && nativeWeaponUseStarted)
            {
                if (monster.CombatContactTargetId == player.EntityId)
                {
                    monster.CombatContactTargetId = 0;
                    monster.CombatContactUntil = 0f;
                }
                Debug.LogError($"[AGGRO-OBSERVE] native weapon use outside aggro {monster.Name}#{monster.EntityId}->{player.Name} dist={dist:F1} aggro={aggroRange:F1} targetSearch={ResolveMonsterTargetSearchRange(monster, false):F1} nativeRange={contactRange:F1} action=projectile-pending native=RangedWeapon::doHit+Projectile::doImpact->MonsterBehavior2::onAttacked");
                return;
            }
            SyncMonsterWanderClientVisiblePosition(monster, "client-action");
            bool attackPathClear = IsMonsterAttackPathClear(monster, player, inNativeContact ? "client-contact" : null);
            if (!attackPathClear)
            {
                inNativeContact = false;
                ClearMonsterCombatContact(monster, player);
            }
            string aggroReason = nativeWeaponUseStarted && !inAuthoredAggro && !inNativeContact ? "client-native-use" : "client";
            AggroMonster(monster, player, aggroReason, false);
            if (inNativeContact)
            {
                monster.CombatContactTargetId = player.EntityId;
                monster.CombatContactUntil = nativeNow + Mathf.Max(0.25f, ResolveMonsterAttackWindup(monster) + 0.25f);
                if (!contactWasActive)
                    Debug.LogError($"[MON-CONTACT] {monster.Name}#{monster.EntityId}->{player.Name} dist={dist:F1} range={allowedRange:F1} nativeRange={contactRange:F1}");
            }
            else if (monster.CombatContactTargetId == player.EntityId)
            {
                monster.CombatContactTargetId = 0;
                monster.CombatContactUntil = 0f;
            }
            TraceMonsterState(monster, "client-action", player, dist, allowedRange, inNativeContact ? "native-contact" : aggroReason);
        }

        private MonsterHpAuthorityState GetMonsterHPAuthority(Monster monster)
        {
            if (monster == null) return null;
            if (!_monsterHPAuthority.TryGetValue(monster.EntityId, out var state))
            {
                state = new MonsterHpAuthorityState();
                _monsterHPAuthority[monster.EntityId] = state;
            }

            if (!state.RuntimeInitialized)
            {
                uint hp = 0;
                if (!_monsterRuntimeHPWire.TryGetValue(monster.EntityId, out hp))
                    hp = monster.IsAlive ? monster.CurrentHPWire : 0;
                state.RuntimeHPWire = hp > monster.MaxHPWire ? monster.MaxHPWire : hp;
                state.RuntimeInitialized = true;
            }

            if (monster.LastClientHPReportTime > state.LastClientHPReportTime)
            {
                state.LastClientHPReportTime = monster.LastClientHPReportTime;
                state.LastClientHPReportWire = monster.LastClientHPReportWire;
            }
            SyncMonsterHPAuthority(monster, state);
            return state;
        }

        private void SyncMonsterHPAuthority(Monster monster, MonsterHpAuthorityState state)
        {
            if (monster == null || state == null) return;
            if (!state.RuntimeInitialized)
            {
                state.RuntimeHPWire = monster.IsAlive ? monster.CurrentHPWire : 0;
                state.RuntimeInitialized = true;
            }
            if (state.RuntimeHPWire > monster.MaxHPWire)
                state.RuntimeHPWire = monster.MaxHPWire;
            _monsterRuntimeHPWire[monster.EntityId] = state.RuntimeHPWire;
            SyncMonsterHPFields(monster, state);
            HpSyncService.Instance.RegisterMonster(monster);
            if (_activeMonsters.TryGetValue(monster.EntityId, out var active) && active != monster)
            {
                SyncMonsterHPFields(active, state);
                HpSyncService.Instance.RegisterMonster(active);
            }
        }

        private void SyncMonsterHPFields(Monster monster, MonsterHpAuthorityState state)
        {
            if (monster == null || state == null) return;
            monster.CurrentHPWire = state.RuntimeHPWire;
            monster.LastClientHPReportTime = state.LastClientHPReportTime;
            monster.LastClientHPReportWire = state.LastClientHPReportWire;
        }

        private uint ResolveNativeTickForTime(float nativeTime, uint explicitNativeTick = 0)
        {
            if (explicitNativeTick != 0)
                return explicitNativeTick;
            if (_nativeCombatTime >= 0f && nativeTime >= 0f && Mathf.Abs(nativeTime - _nativeCombatTime) <= NATIVE_UNIT_TICK_INTERVAL + 0.0001f)
                return _nativeCombatTick;
            if (nativeTime >= 0f)
                return (uint)Mathf.Max(0, Mathf.FloorToInt((nativeTime / NATIVE_UNIT_TICK_INTERVAL) + 0.0001f));
            return _nativeCombatTick;
        }

        private static bool IsSubEntityMonsterHPMutationSource(string source)
        {
            if (string.IsNullOrEmpty(source))
                return false;
            return source.IndexOf("RangedProjectile", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("SpellProjectile", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("Projectile-HIT", StringComparison.OrdinalIgnoreCase) >= 0
                || source.IndexOf("subentity", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ResolveMonsterHPMutationPhase(string source, bool committedDamage, bool subEntityMutation)
        {
            if (subEntityMutation)
                return "subentity";
            if (!string.IsNullOrEmpty(source) && source.IndexOf("regen", StringComparison.OrdinalIgnoreCase) >= 0)
                return "regen";
            return committedDamage ? "entity-damage" : "runtime";
        }

        private void RecordMonsterHPHistory(Monster monster, MonsterHpAuthorityState state, uint beforeHPWire, uint afterHPWire, bool committedDamage, float nativeTime, string source, uint explicitNativeTick = 0)
        {
            if (monster == null || state == null || beforeHPWire == afterHPWire)
                return;

            float resolvedTime = nativeTime >= 0f ? nativeTime : GetNativeCombatTime();
            uint nativeTick = ResolveNativeTickForTime(resolvedTime, explicitNativeTick);
            bool subEntityMutation = IsSubEntityMonsterHPMutationSource(source);
            var entry = new MonsterHpHistoryEntry
            {
                BeforeHPWire = beforeHPWire,
                AfterHPWire = afterHPWire,
                NativeTick = nativeTick,
                NativeTime = resolvedTime,
                Source = source ?? "unknown",
                CommittedDamage = committedDamage,
                SubEntityMutation = subEntityMutation,
                MutationPhase = ResolveMonsterHPMutationPhase(source, committedDamage, subEntityMutation)
            };

            state.History.Add(entry);
            if (state.History.Count > MONSTER_HP_HISTORY_LIMIT)
                state.History.RemoveRange(0, state.History.Count - MONSTER_HP_HISTORY_LIMIT);
            Debug.LogError($"[MON-HP-HISTORY] entity={monster.EntityId} name={monster.Name} phase={entry.MutationPhase} tick={nativeTick} time={resolvedTime:F3} hp={beforeHPWire}->{afterHPWire}/{monster.MaxHPWire} committed={committedDamage} subentity={entry.SubEntityMutation} source={source ?? "unknown"}");
        }

        private static bool IsMonsterHpHistoryVisible(MonsterHpHistoryEntry entry, NativeHpVisibilityCutoff cutoff)
        {
            if (entry == null)
                return true;
            const float timeEpsilon = 0.0001f;
            if (entry.SubEntityMutation)
            {
                if (entry.NativeTick > cutoff.Tick)
                    return false;
                if (entry.NativeTick == cutoff.Tick && !cutoff.IncludeSubEntityEffects)
                    return false;
                if (entry.NativeTick < cutoff.Tick)
                    return true;
            }
            if (entry.NativeTime + timeEpsilon < cutoff.Time)
                return true;
            if (entry.NativeTime > cutoff.Time + timeEpsilon)
                return false;
            return !entry.SubEntityMutation || cutoff.IncludeSubEntityEffects;
        }

        private bool TryResolveMonsterHPFromHistory(Monster monster, MonsterHpAuthorityState state, NativeHpVisibilityCutoff cutoff, out uint hpWire, out string hpView, out string mutationSource, out string excludedSameTickProjectile)
        {
            hpWire = state != null ? state.RuntimeHPWire : 0;
            hpView = "runtime-current";
            mutationSource = "none";
            excludedSameTickProjectile = "none";
            if (monster == null || state == null || state.History.Count == 0)
                return false;

            for (int i = state.History.Count - 1; i >= 0; i--)
            {
                MonsterHpHistoryEntry entry = state.History[i];
                if (IsMonsterHpHistoryVisible(entry, cutoff))
                    break;

                hpWire = entry.BeforeHPWire;
                hpView = "history-cutoff";
                mutationSource = $"{entry.Source ?? "unknown"} phase={entry.MutationPhase ?? "unknown"} tick={entry.NativeTick} time={entry.NativeTime:F3}";
                if (entry.SubEntityMutation)
                {
                    string visibility = entry.NativeTick > cutoff.Tick ? "future-subentity" : "same-tick-subentity";
                    excludedSameTickProjectile = $"{visibility} source={entry.Source ?? "unknown"} phase={entry.MutationPhase ?? "subentity"} tick={entry.NativeTick} time={entry.NativeTime:F3} cutoffPhase={cutoff.Phase ?? "unknown"} cutoff={cutoff.Tick}@{cutoff.Time:F3} includeSubEntity={cutoff.IncludeSubEntityEffects} hp={entry.BeforeHPWire}->{entry.AfterHPWire}";
                }
            }

            if (hpWire > monster.MaxHPWire)
                hpWire = monster.MaxHPWire;
            return hpView == "history-cutoff";
        }

        private uint PeekRuntimeMonsterHPWire(Monster monster)
        {
            var state = GetMonsterHPAuthority(monster);
            return state != null ? state.RuntimeHPWire : 0;
        }

        private int ResolveMonsterHealthRegenFactor(Monster monster)
        {
            if (monster == null || !monster.HasAuthoredHealthRegen)
                return 0;
            float authoredGlobal = GCDatabase.Instance.GetKnob("MonsterHealthRegen", 2f);
            float authoredUnit = Mathf.Max(0f, monster.HealthRegen);
            return ComputeNativeUnitDescRegenFactor(authoredUnit, authoredGlobal);
        }

        private int ResolveMonsterHealthRegenModPct(Monster monster)
        {
            return 0;
        }

        private int ResolveMonsterAdditiveHealthRegen(Monster monster)
        {
            return 0;
        }

        internal static int ComputeNativeUnitRegenDeltaWire(uint maxHPWire, int baseRegen, int regenModPct, int additiveRegen, bool cooldownActive)
        {
            if (maxHPWire == 0) return 0;
            long regen;
            long bonus = additiveRegen;
            if (cooldownActive)
            {
                if (bonus == 0) return 0;
                regen = bonus;
            }
            else
            {
                regen = (((long)regenModPct + NATIVE_PERCENT_SCALE) * baseRegen) / NATIVE_PERCENT_SCALE + bonus;
            }

            long delta = (regen * maxHPWire) / NATIVE_UNIT_REGEN_DIVISOR;
            if (!cooldownActive)
                delta += 1;
            if (delta > int.MaxValue) return int.MaxValue;
            if (delta < int.MinValue) return int.MinValue;
            return (int)delta;
        }

        internal static uint ApplyNativeUnitHPShiftWire(uint hpWire, uint maxHPWire, int deltaWire)
        {
            long shifted = (long)hpWire + deltaWire;
            if (shifted <= 0) return 0;
            if (shifted >= maxHPWire) return maxHPWire;
            return (uint)shifted;
        }

        private int ComputeNativeMonsterHealthRegenDeltaWire(Monster monster, bool cooldownActive)
        {
            if (monster == null) return 0;
            return ComputeNativeUnitRegenDeltaWire(
                monster.MaxHPWire,
                ResolveMonsterHealthRegenFactor(monster),
                ResolveMonsterHealthRegenModPct(monster),
                ResolveMonsterAdditiveHealthRegen(monster),
                cooldownActive);
        }

        private void ResetMonsterHPRegenClock(Monster monster, float now)
        {
            if (monster == null) return;
            if (now < 0f) now = GetNativeCombatTime();
            _monsterHPRegenLastTime[monster.EntityId] = now;
            _monsterHPRegenCarryWire[monster.EntityId] = 0f;
        }

        private int ResolveMonsterManaRegenFactor(Monster monster)
        {
            if (monster == null || !monster.HasAuthoredManaRegen)
                return 0;
            float authoredGlobal = GCDatabase.Instance.GetKnob("MonsterPowerRegen", 2f);
            float authoredUnit = Mathf.Max(0f, monster.ManaRegen);
            return ComputeNativeUnitDescRegenFactor(authoredUnit, authoredGlobal);
        }

        private static int ComputeNativeUnitDescRegenFactor(float authoredUnit, float authoredGlobal)
        {
            if (authoredUnit <= 0f || authoredGlobal <= 0f)
                return 0;
            long unitF32 = DamageComputer.NativeFixed32FromAuthoredDecimal(authoredUnit);
            long globalF32 = DamageComputer.NativeFixed32FromAuthoredDecimal(authoredGlobal);
            long value = (unitF32 * globalF32) >> 16;
            if (value <= 0) return 0;
            return value > ushort.MaxValue ? ushort.MaxValue : (int)value;
        }

        private uint ComputeNativeMonsterManaRegenDeltaWire(Monster monster)
        {
            if (monster == null || monster.MaxManaWire == 0) return 0;
            int regenFactor = ResolveMonsterManaRegenFactor(monster);
            if (regenFactor <= 0) return 0;
            long delta = ((long)regenFactor * monster.MaxManaWire) / 3000L + 1L;
            if (delta <= 0) return 0;
            return delta > uint.MaxValue ? uint.MaxValue : (uint)delta;
        }

        private void ResetMonsterManaRegenClock(Monster monster, float now)
        {
            if (monster == null) return;
            if (now < 0f) now = GetNativeCombatTime();
            _monsterManaRegenLastTime[monster.EntityId] = now;
        }

        private uint ApplyNativeMonsterHealthRegen(Monster monster, string source, float now = -1f)
        {
            if (monster == null) return 0;
            uint hp = PeekRuntimeMonsterHPWire(monster);
            if (now < 0f) now = GetNativeCombatTime();
            if (!monster.IsAlive || hp == 0 || monster.MaxHPWire == 0)
            {
                ResetMonsterHPRegenClock(monster, now);
                _monsterHPRegenCooldownTicks.Remove(monster.EntityId);
                return hp;
            }
            if (hp >= monster.MaxHPWire && ResolveMonsterAdditiveHealthRegen(monster) >= 0)
            {
                ResetMonsterHPRegenClock(monster, now);
                _monsterHPRegenCooldownTicks.Remove(monster.EntityId);
                return hp;
            }

            if (!_monsterHPRegenLastTime.TryGetValue(monster.EntityId, out float lastTime) || lastTime <= 0f)
            {
                _monsterHPRegenLastTime[monster.EntityId] = now;
                return hp;
            }

            float elapsed = now - lastTime;
            if (elapsed <= 0f) return hp;

            int ticks = Mathf.FloorToInt(elapsed / NATIVE_UNIT_TICK_INTERVAL);
            if (ticks <= 0) return hp;

            _monsterHPRegenLastTime[monster.EntityId] = lastTime + ticks * NATIVE_UNIT_TICK_INTERVAL;

            uint oldHP = hp;
            _monsterHPRegenCooldownTicks.TryGetValue(monster.EntityId, out ushort cooldown);
            int regenFactor = ResolveMonsterHealthRegenFactor(monster);
            int regenMod = ResolveMonsterHealthRegenModPct(monster);
            int additiveRegen = ResolveMonsterAdditiveHealthRegen(monster);
            for (int i = 0; i < ticks && (hp < monster.MaxHPWire || additiveRegen < 0); i++)
            {
                if (cooldown > 0)
                    cooldown--;

                int regenWire = ComputeNativeMonsterHealthRegenDeltaWire(monster, cooldown > 0);
                if (regenWire == 0) continue;
                hp = ApplyNativeUnitHPShiftWire(hp, monster.MaxHPWire, regenWire);
                if (hp == 0) break;
            }

            if (cooldown > 0)
                _monsterHPRegenCooldownTicks[monster.EntityId] = cooldown;
            else
                _monsterHPRegenCooldownTicks.Remove(monster.EntityId);

            if (oldHP == hp) return hp;

            if (hp >= monster.MaxHPWire)
            {
                hp = monster.MaxHPWire;
                _monsterRuntimeDamageCommitted.Remove(monster.EntityId);
                _monsterHPRegenCarryWire[monster.EntityId] = 0f;
            }

            var state = GetMonsterHPAuthority(monster);
            RecordMonsterHPHistory(monster, state, oldHP, hp, false, now, source ?? "regen");
            state.RuntimeHPWire = hp;
            state.RuntimeInitialized = true;
            SyncMonsterHPAuthority(monster, state);
            Debug.LogError($"[MON-REGEN] {monster.Name}#{monster.EntityId} source={source ?? "unknown"} hp={oldHP / 256f:F2}->{hp / 256f:F2}/{monster.MaxHPWire / 256f:F2} ticks={ticks} cooldown={cooldown} base={regenFactor} mod={regenMod} additive={additiveRegen}");
            return hp;
        }

        private void ApplyNativeMonsterManaRegen(Monster monster, string source, float now = -1f)
        {
            if (monster == null) return;
            if (now < 0f) now = GetNativeCombatTime();
            if (!monster.IsAlive || monster.MaxManaWire == 0)
            {
                ResetMonsterManaRegenClock(monster, now);
                _monsterManaRegenCooldownTicks.Remove(monster.EntityId);
                return;
            }
            if (monster.CurrentManaWire > monster.MaxManaWire)
                monster.CurrentManaWire = monster.MaxManaWire;
            if (monster.CurrentManaWire >= monster.MaxManaWire)
            {
                ResetMonsterManaRegenClock(monster, now);
                _monsterManaRegenCooldownTicks.Remove(monster.EntityId);
                return;
            }
            if (ResolveMonsterManaRegenFactor(monster) <= 0)
            {
                ResetMonsterManaRegenClock(monster, now);
                return;
            }
            if (!_monsterManaRegenLastTime.TryGetValue(monster.EntityId, out float lastTime) || lastTime <= 0f)
            {
                _monsterManaRegenLastTime[monster.EntityId] = now;
                return;
            }

            float elapsed = now - lastTime;
            if (elapsed <= 0f) return;
            int ticks = Mathf.FloorToInt(elapsed / NATIVE_UNIT_TICK_INTERVAL);
            if (ticks <= 0) return;
            _monsterManaRegenLastTime[monster.EntityId] = lastTime + ticks * NATIVE_UNIT_TICK_INTERVAL;

            uint oldMana = monster.CurrentManaWire;
            uint mana = oldMana;
            _monsterManaRegenCooldownTicks.TryGetValue(monster.EntityId, out ushort cooldown);
            for (int i = 0; i < ticks && mana < monster.MaxManaWire; i++)
            {
                if (cooldown > 0)
                    cooldown--;
                if (cooldown > 0)
                    continue;

                uint regenWire = ComputeNativeMonsterManaRegenDeltaWire(monster);
                if (regenWire == 0) continue;
                mana = regenWire >= monster.MaxManaWire - mana ? monster.MaxManaWire : mana + regenWire;
            }

            if (cooldown > 0)
                _monsterManaRegenCooldownTicks[monster.EntityId] = cooldown;
            else
                _monsterManaRegenCooldownTicks.Remove(monster.EntityId);

            if (oldMana == mana) return;
            monster.CurrentManaWire = mana;
            Debug.LogError($"[MON-MANA-REGEN] {monster.Name}#{monster.EntityId} source={source ?? "unknown"} mana={oldMana / 256f:F2}->{mana / 256f:F2}/{monster.MaxManaWire / 256f:F2} ticks={ticks} cooldown={cooldown} factor={ResolveMonsterManaRegenFactor(monster)}");
        }

        private uint ApplyNativeMonsterVitalsRegen(Monster monster, string source, float now = -1f)
        {
            uint hp = ApplyNativeMonsterHealthRegen(monster, source, now);
            ApplyNativeMonsterManaRegen(monster, source, now);
            return hp;
        }

        public uint AdvanceMonsterVitalsToNativeTime(Monster monster, float now, string source = null)
        {
            if (now < 0f) now = GetNativeCombatTime();
            return monster != null ? ApplyNativeMonsterVitalsRegen(monster, source ?? "AdvanceMonsterVitalsToNativeTime", now) : 0u;
        }

        public uint AdvanceMonsterRuntimeBeforeSync(Monster monster, float now, string source = null)
        {
            if (now < 0f) now = GetNativeCombatTime();
            if (!_advancingMonsterModifiers)
                AdvanceMonsterModifierRuntime(GetRoomRngForMonster(monster), now, source ?? "AdvanceMonsterRuntimeBeforeSync", monster?.EntityId ?? 0u);
            uint hp = AdvanceMonsterVitalsToNativeTime(monster, now, source ?? "AdvanceMonsterRuntimeBeforeSync");
            return monster != null ? PeekRuntimeMonsterHPWire(monster) : hp;
        }

        private uint GetRuntimeMonsterHPWire(Monster monster, string source = null, float now = -1f)
        {
            return PeekRuntimeMonsterHPWire(monster);
        }

        private static string BuildMonsterModifierKey(uint targetEntityId, uint sourceEntityId, SpellData spell)
        {
            string modifierId = spell?.ProjectileModifierId;
            if (string.IsNullOrEmpty(modifierId))
                modifierId = spell?.ProjectileModifierEffectId ?? "modifier";
            return $"{targetEntityId}:{sourceEntityId}:{modifierId}";
        }

        private void RemoveMonsterModifiersForTarget(uint targetEntityId, string source)
        {
            if (targetEntityId == 0 || _activeMonsterModifiers.Count == 0) return;
            var keys = _activeMonsterModifiers
                .Where(kvp => kvp.Value != null && kvp.Value.TargetEntityId == targetEntityId)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (string key in keys)
                _activeMonsterModifiers.Remove(key);
            if (keys.Count > 0)
                Debug.LogError($"[POISON-SHOT-MOD] remove target={targetEntityId} count={keys.Count} source={source ?? "unknown"}");
        }

        private string BuildPlayerDamageModifierKey(uint targetEntityId, uint sourceEntityId, MonsterDamageModifierSkillEffect effect)
        {
            return BuildPlayerRuntimeModifierKey(targetEntityId, sourceEntityId, effect?.ModifierPath ?? effect?.ModifierEffectPath ?? "modifier", effect?.StackRule ?? string.Empty, NATIVE_RUNTIME_MODIFIER_SOURCE_IS_SELF);
        }

        private string BuildPlayerRuntimeModifierKey(uint targetEntityId, uint sourceEntityId, string modifierId, string stackRule, byte sourceIsSelf)
        {
            string key = BuildPlayerModifierStackKey(targetEntityId, sourceEntityId, modifierId, stackRule, sourceIsSelf);
            if (IsNativeAdditiveModifierStack(stackRule))
                return $"{key}:stack:{_nextPlayerModifierStackSerial++}";
            return key;
        }

        private static bool IsNativeAdditiveModifierStack(string stackRule)
        {
            return string.IsNullOrWhiteSpace(stackRule) || string.Equals(stackRule, "NONE", StringComparison.OrdinalIgnoreCase) || string.Equals(stackRule, "0", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildPlayerModifierStackKey(uint targetEntityId, uint sourceEntityId, string modifierId, string stackRule, byte sourceIsSelf)
        {
            if (string.IsNullOrWhiteSpace(modifierId))
                modifierId = "modifier";
            if (string.Equals(stackRule, "UNIQUEBYSOURCE", StringComparison.OrdinalIgnoreCase) && sourceIsSelf == 0 && sourceEntityId != 0)
                return $"{targetEntityId}:{sourceEntityId}:{modifierId}";
            return $"{targetEntityId}:{modifierId}";
        }

        private static uint ResolveActivePlayerModifierRemainingTicks(ActivePlayerDamageModifier mod, float now)
        {
            if (mod == null || mod.DurationTicks == 0)
                return 0;
            float endTime = float.IsPositiveInfinity(mod.ExpireTime) ? mod.ApplyTime + (mod.DurationTicks / 30f) : mod.ExpireTime;
            float remaining = endTime - now;
            if (remaining <= 0f)
                return 0;
            int remainingTicks = Mathf.Clamp(Mathf.CeilToInt(remaining * 30f - 0.0001f), 0, ushort.MaxValue);
            return (uint)remainingTicks;
        }

        private static bool ShouldAcceptNativeModifierStack(string stackRule, uint incomingPowerLevel, uint incomingDurationTicks, uint existingPowerLevel, uint existingDurationTicks)
        {
            if (string.IsNullOrWhiteSpace(stackRule))
                return true;
            if (string.Equals(stackRule, "UNIQUEBYSOURCE", StringComparison.OrdinalIgnoreCase))
                return incomingPowerLevel >= existingPowerLevel;
            if (string.Equals(stackRule, "UNIQUEBYTYPE", StringComparison.OrdinalIgnoreCase))
            {
                if (incomingPowerLevel <= existingPowerLevel)
                {
                    if (incomingDurationTicks == 0)
                        return existingDurationTicks != 0;
                    if (existingDurationTicks == 0 || incomingDurationTicks <= existingDurationTicks)
                        return false;
                }
                return true;
            }
            return true;
        }

        private uint AllocatePlayerModifierNetworkId(string key, bool track = true)
        {
            if (string.IsNullOrWhiteSpace(key)) key = "modifier";
            uint id = _nextPlayerModifierNetworkId++;
            if (track)
                _playerModifierNetworkIds[key] = id;
            return id;
        }

        private uint ResolveMonsterSkillPowerLevelWire(Monster monster, string skillPath)
        {
            int level = 1;
            GCNode skill = null;
            if (!string.IsNullOrWhiteSpace(skillPath))
                skill = GCDatabase.Instance?.ResolveWithInheritance(skillPath);
            GCNode desc = skill?.GetChild("Description") ?? skill;
            int basePower = desc != null ? desc.GetInt("PowerLevel", desc.GetInt("PowerLevelMin", 1)) : 1;
            int powerInc = desc != null ? desc.GetInt("PowerLevelInc", 0) : 0;
            long power = ((long)basePower << 8) + ((long)Math.Max(0, level - 1) * powerInc << 8);
            if (power <= 0 && monster != null) power = Math.Max(1, (int)monster.Level) << 8;
            if (power <= 0) power = 0x100;
            if (power > uint.MaxValue) return uint.MaxValue;
            return (uint)power;
        }

        private PlayerModifierLifecycle ResolvePlayerModifierLifecycle(string modifierPath)
        {
            var lifecycle = new PlayerModifierLifecycle();
            if (string.IsNullOrWhiteSpace(modifierPath))
                return lifecycle;

            GCNode modifier = GCDatabase.Instance?.ResolveWithInheritance(modifierPath);
            GCNode desc = modifier?.GetChild("Description") ?? modifier;
            if (desc == null)
                return lifecycle;

            lifecycle.Visual = desc.GetString("Visual", null);
            lifecycle.InitSound = desc.GetString("InitSound", null);
            lifecycle.InitEffect = desc.GetString("InitEffect", null);
            lifecycle.RemoveEffect = desc.GetString("RemoveEffect", null);
            lifecycle.OverlayIcon = desc.GetString("OverlayIcon", null);
            lifecycle.OverlayDuration = desc.GetInt("OverlayDuration", 0);
            return lifecycle;
        }

        private void RaisePlayerModifierAdd(Monster sourceMonster, CombatPlayer target, string modifierKey, string gcType, byte level, uint powerLevel, uint durationTicks, byte sourceIsSelf, bool replace, string skillPath, string effectPath, string source, string native, bool trackModifierId = true)
        {
            if (target == null || string.IsNullOrWhiteSpace(gcType))
                return;
            uint id = AllocatePlayerModifierNetworkId(modifierKey, trackModifierId);
            OnPlayerModifierNetworkEvent?.Invoke(sourceMonster, target, new PlayerModifierNetworkEvent
            {
                Add = true,
                ModifierKey = modifierKey,
                GCType = gcType,
                ModifierId = id,
                Level = level,
                PowerLevel = powerLevel,
                DurationTicks = durationTicks,
                SourceIsSelf = sourceIsSelf,
                Replace = replace,
                SkillPath = skillPath,
                EffectPath = effectPath,
                Source = source,
                Native = native,
                Lifecycle = ResolvePlayerModifierLifecycle(gcType)
            });
        }

        private void RaisePlayerModifierRemove(Monster sourceMonster, CombatPlayer target, string modifierKey, string gcType, string skillPath, string effectPath, string source, string native)
        {
            if (target == null || string.IsNullOrWhiteSpace(modifierKey))
                return;
            if (!_playerModifierNetworkIds.TryGetValue(modifierKey, out uint id))
                return;
            _playerModifierNetworkIds.Remove(modifierKey);
            OnPlayerModifierNetworkEvent?.Invoke(sourceMonster, target, new PlayerModifierNetworkEvent
            {
                Add = false,
                ModifierKey = modifierKey,
                GCType = gcType,
                ModifierId = id,
                SkillPath = skillPath,
                EffectPath = effectPath,
                Source = source,
                Native = native,
                Lifecycle = ResolvePlayerModifierLifecycle(gcType)
            });
        }

        private void HandlePlayerNativeAttributeModifierRemoved(PlayerState state, PlayerState.NativeAttributeModifierRemoval removal)
        {
            if (state == null || removal == null)
                return;
            CombatPlayer target = _players.Values.FirstOrDefault(player => player != null && ReferenceEquals(player.PlayerState, state));
            if (target == null)
                return;
            _activeMonsters.TryGetValue(removal.SourceEntityId, out Monster sourceMonster);
            RaisePlayerModifierRemove(sourceMonster, target, removal.ModifierKey, removal.ModifierType, removal.SkillPath, removal.EffectPath, removal.Source ?? "unknown", removal.Native ?? "Modifiers::processRemoveModifier@0x00502390");
            Debug.LogError($"[PLAYER-MODIFIER] network-remove target={target.Name}#{target.EntityId} modifier={removal.ModifierType ?? ""} key={removal.ModifierKey ?? ""} sourceMonster={sourceMonster?.Name ?? "none"}#{sourceMonster?.EntityId ?? 0u} source={removal.Source ?? "unknown"} native={removal.Native ?? "Modifiers::processRemoveModifier@0x00502390"}");
        }

        private void RemovePlayerDamageModifiersForTarget(uint targetEntityId, string source, bool removeOnlyOnDeath = true)
        {
            if (targetEntityId == 0 || _activePlayerDamageModifiers.Count == 0) return;
            var removals = _activePlayerDamageModifiers
                .Where(kvp => kvp.Value != null && kvp.Value.TargetEntityId == targetEntityId && (!removeOnlyOnDeath || kvp.Value.RemoveOnDeath))
                .Select(kvp => new { Key = kvp.Key, Mod = kvp.Value })
                .ToList();
            _players.TryGetValue(targetEntityId, out CombatPlayer target);
            foreach (var removal in removals)
            {
                _activeMonsters.TryGetValue(removal.Mod.SourceEntityId, out Monster sourceMonster);
                RaisePlayerModifierRemove(sourceMonster, target, removal.Key, removal.Mod.ModifierPath, removal.Mod.SkillPath, removal.Mod.EffectPath, source ?? "unknown", "Modifiers::processRemoveModifier@0x00502390");
                _activePlayerDamageModifiers.Remove(removal.Key);
            }
            if (removals.Count > 0)
                Debug.LogError($"[PLAYER-EFFECTMOD] remove target={targetEntityId} count={removals.Count} source={source ?? "unknown"} removeOnlyOnDeath={removeOnlyOnDeath} native=ModifierDesc.RemoveOnDeath");
        }

        private void RemovePlayerModifiersFromSource(uint sourceEntityId, string source)
        {
            if (sourceEntityId == 0) return;
            _activeMonsters.TryGetValue(sourceEntityId, out Monster sourceMonster);
            var removals = _activePlayerDamageModifiers
                .Where(kvp => kvp.Value != null && kvp.Value.SourceEntityId == sourceEntityId)
                .Select(kvp => new { Key = kvp.Key, Mod = kvp.Value })
                .ToList();
            foreach (var removal in removals)
            {
                _players.TryGetValue(removal.Mod.TargetEntityId, out CombatPlayer target);
                RaisePlayerModifierRemove(sourceMonster, target, removal.Key, removal.Mod.ModifierPath, removal.Mod.SkillPath, removal.Mod.EffectPath, source ?? "unknown", "Modifier::update@0x004FF1B0 Modifiers::processRemoveModifier@0x00502390");
                _activePlayerDamageModifiers.Remove(removal.Key);
            }

            int attributeRemoved = 0;
            foreach (var player in _players.Values.ToList())
            {
                if (player?.PlayerState == null)
                    continue;
                attributeRemoved += player.PlayerState.RemoveNativeAttributeModifiersFromSource(sourceEntityId, source ?? "source-unit-removed");
            }

            if (removals.Count > 0 || attributeRemoved > 0)
                Debug.LogError($"[PLAYER-MODIFIER] remove-source sourceEntity={sourceEntityId} effectMods={removals.Count} attributeMods={attributeRemoved} source={source ?? "unknown"} native=Modifier::update@0x004FF1B0 Modifiers::processRemoveModifier@0x00502390");
        }

        private bool ApplyPlayerDamageModifierFromMonster(Monster sourceMonster, CombatPlayer target, MonsterDamageModifierSkillEffect effect, float now, string source)
        {
            if (sourceMonster == null || target == null || target.PlayerState == null || effect == null)
                return false;
            if (!target.IsAlive || target.PlayerState.CurrentHPWire == 0)
                return false;

            float applyTime = now >= 0f ? now : GetNativeCombatTime();
            float frequency = effect.FrequencySeconds > 0f ? effect.FrequencySeconds : 1f;
            float duration = effect.DurationSeconds > 0f ? effect.DurationSeconds : 0f;
            ushort durationTicks = effect.DurationTicks > 0 ? effect.DurationTicks : ComputeNativeSpellModDurationTicks(duration);
            ushort frequencyTicks = effect.FrequencyTicks > 0 ? effect.FrequencyTicks : ComputeNativeEffectModFrequencyTicks(frequency);
            int maxTicks = ComputeNativeEffectModApplyTickBudget(durationTicks, frequencyTicks);
            float intervalSeconds = ComputeNativeEffectModIntervalSeconds(frequencyTicks);
            float expireTime = ComputeNativeEffectModExpireTime(applyTime, durationTicks);
            string key = BuildPlayerDamageModifierKey(target.EntityId, sourceMonster.EntityId, effect);
            bool replace = _activePlayerDamageModifiers.TryGetValue(key, out ActivePlayerDamageModifier existing);
            uint powerLevel = ResolveMonsterSkillPowerLevelWire(sourceMonster, effect.SkillPath);
            uint existingRemainingTicks = replace ? ResolveActivePlayerModifierRemainingTicks(existing, applyTime) : 0u;
            bool stackAccepted = !replace || ShouldAcceptNativeModifierStack(effect.StackRule, powerLevel, durationTicks, existing.PowerLevel, existingRemainingTicks);
            RaisePlayerModifierAdd(sourceMonster, target, key, effect.ModifierPath, 1, powerLevel, durationTicks, NATIVE_RUNTIME_MODIFIER_SOURCE_IS_SELF, replace, effect.SkillPath, effect.EffectPath, source ?? "unknown", "SpellModEffect::doEffect@0x00554460 Modifiers::processAddModifier@0x00502280", stackAccepted);
            if (!stackAccepted)
            {
                Debug.LogError($"[PLAYER-MODIFIER-STACK] result=reject target={target.Name}#{target.EntityId} source={sourceMonster.Name}#{sourceMonster.EntityId} skill={effect.SkillPath} modifier={effect.ModifierPath} stack={effect.StackRule ?? "UNKNOWN"} key={key} incomingPower={powerLevel} existingPower={existing.PowerLevel} incomingDuration={durationTicks} existingRemaining={existingRemainingTicks} sourceIsSelf={NATIVE_RUNTIME_MODIFIER_SOURCE_IS_SELF} native=Modifiers::addModifierLocal@0x00501770");
                return false;
            }
            _activePlayerDamageModifiers[key] = new ActivePlayerDamageModifier
            {
                TargetEntityId = target.EntityId,
                SourceEntityId = sourceMonster.EntityId,
                SkillPath = effect.SkillPath,
                EffectPath = effect.EffectPath,
                ModifierPath = effect.ModifierPath,
                ModifierEffectPath = effect.ModifierEffectPath,
                AttackType = effect.AttackType,
                DamageType = effect.DamageType,
                DamageTypeId = effect.DamageTypeId,
                DamageKind = effect.DamageKind,
                DamageMod = effect.DamageMod,
                DamageVolatility = effect.DamageVolatility,
                SourceLevel = Math.Max(1, (int)sourceMonster.Level),
                SourceIntellect = ResolveMonsterSpellIntellect(sourceMonster),
                SourceAgility = ResolveMonsterSpellAgility(sourceMonster),
                PowerLevel = powerLevel,
                DurationTicks = durationTicks,
                FrequencyTicks = frequencyTicks,
                ApplyTime = applyTime,
                NextTickTime = applyTime + intervalSeconds,
                Frequency = intervalSeconds,
                ExpireTime = expireTime,
                MaxTicks = maxTicks,
                TicksApplied = 0,
                RemoveOnDeath = effect.RemoveOnDeath,
                StackRule = effect.StackRule,
                ModifierKey = key
            };

            Debug.LogError($"[PLAYER-EFFECTMOD] {(replace ? "replace" : "add")} target={target.Name}#{target.EntityId} source={sourceMonster.Name}#{sourceMonster.EntityId} skill={effect.SkillPath} modifier={effect.ModifierPath} effect={effect.ModifierEffectPath} attackType={effect.AttackType} damageTypeId={effect.DamageTypeId} damageKind={effect.DamageKind} duration={duration:F2}s frequency={frequency:F2}s durationTicks={durationTicks} frequencyTicks={frequencyTicks} maxTicks={maxTicks} power={powerLevel} stack={effect.StackRule ?? "UNKNOWN"} firstTickAt={applyTime + intervalSeconds:F3} expireAt={expireTime:F3} source={source ?? "unknown"} native=SpellModEffect::doEffect@0x00554460 EffectMod::init@0x0055FE20 EffectMod::update@0x0055FE70 Modifiers::addModifierLocal@0x00501770");
            return true;
        }

        private static int ResolveMonsterSpellIntellect(Monster monster)
        {
            if (monster?.NativeSlots == null) return 10;
            return Math.Max(1, monster.NativeSlots.Get(NativeUnitSlot.Intellect, 10));
        }

        private static int ResolveMonsterSpellAgility(Monster monster)
        {
            if (monster?.NativeSlots == null) return 10;
            return Math.Max(1, monster.NativeSlots.Get(NativeUnitSlot.Agility, 10));
        }

        private MonsterModifierApplyResult AdvancePlayerDamageModifierRuntime(uint onlyTargetEntityId, float now, string source)
        {
            var aggregate = new MonsterModifierApplyResult
            {
                Reason = source ?? "player-effectmod-runtime"
            };
            if (_activePlayerDamageModifiers.Count == 0)
                return aggregate;
            if (now < 0f) now = GetNativeCombatTime();

            var keys = _activePlayerDamageModifiers.Keys.ToList();
            foreach (string key in keys)
            {
                if (!_activePlayerDamageModifiers.TryGetValue(key, out var mod) || mod == null)
                    continue;
                if (onlyTargetEntityId != 0 && mod.TargetEntityId != onlyTargetEntityId)
                    continue;
                if (!_players.TryGetValue(mod.TargetEntityId, out var target) || target == null || target.PlayerState == null)
                {
                    _activePlayerDamageModifiers.Remove(key);
                    _playerModifierNetworkIds.Remove(key);
                    Debug.LogError($"[PLAYER-EFFECTMOD] remove target={mod.TargetEntityId} modifier={mod.ModifierKey} source={source ?? "unknown"} reason=missing-target native=EffectMod::update@0x0055FE70");
                    continue;
                }
                if (!target.IsAlive || target.PlayerState.CurrentHPWire == 0)
                {
                    if (mod.RemoveOnDeath)
                    {
                        RaisePlayerModifierRemove(null, target, key, mod.ModifierPath, mod.SkillPath, mod.EffectPath, source ?? "unknown", "ModifierDesc.RemoveOnDeath Modifiers::processRemoveModifier@0x00502390");
                        _activePlayerDamageModifiers.Remove(key);
                        Debug.LogError($"[PLAYER-EFFECTMOD] remove target={target.Name}#{target.EntityId} modifier={mod.ModifierKey} source={source ?? "unknown"} reason=RemoveOnDeath native=ModifierDesc.RemoveOnDeath");
                    }
                    continue;
                }
                if (!_activeMonsters.TryGetValue(mod.SourceEntityId, out var sourceMonster) || sourceMonster == null)
                {
                    RaisePlayerModifierRemove(null, target, key, mod.ModifierPath, mod.SkillPath, mod.EffectPath, source ?? "unknown", "EffectMod::update@0x0055FE70 Modifiers::processRemoveModifier@0x00502390");
                    _activePlayerDamageModifiers.Remove(key);
                    Debug.LogError($"[PLAYER-EFFECTMOD] remove target={target.Name}#{target.EntityId} modifier={mod.ModifierKey} source={source ?? "unknown"} reason=missing-source-monster native=EffectMod::update@0x0055FE70");
                    continue;
                }

                MersenneTwister modRng = GetRoomRngForMonster(sourceMonster);
                if (modRng == null)
                {
                    RuntimeEvidenceManager.LogFallbackHit(
                        "rng-instance",
                        "player-effectmod-missing-instance-rng",
                        $"target={target.EntityId} sourceMonster={sourceMonster.EntityId} source={source ?? "unknown"}",
                        64);
                    Debug.LogError($"[PLAYER-EFFECTMOD] skip tick target={target.Name}#{target.EntityId} sourceMonster={sourceMonster.Name}#{sourceMonster.EntityId} source={source ?? "unknown"} reason=missing-instance-rng");
                    continue;
                }
                bool expiredBeforeTick = IsNativeEffectModExpired(mod.DurationTicks, mod.ExpireTime, now);
                if (expiredBeforeTick && (mod.MaxTicks == 0 || mod.TicksApplied >= mod.MaxTicks || mod.NextTickTime > mod.ExpireTime + 0.0001f))
                {
                    RaisePlayerModifierRemove(sourceMonster, target, key, mod.ModifierPath, mod.SkillPath, mod.EffectPath, source ?? "unknown", "Modifiers::update@0x00501E50 Modifiers::removeModifierLocal@0x00501B50");
                    _activePlayerDamageModifiers.Remove(key);
                    Debug.LogError($"[PLAYER-EFFECTMOD] complete target={target.Name}#{target.EntityId} source={sourceMonster.Name}#{sourceMonster.EntityId} ticks={mod.TicksApplied}/{mod.MaxTicks} hp={target.PlayerState.CurrentHPWire} reason=duration-expired expireAt={mod.ExpireTime:F3} now={now:F3} source={source ?? "unknown"} native=Modifier::update@0x004FF1B0 EffectMod::update@0x0055FE70");
                    continue;
                }
                if (now + 0.0001f < mod.NextTickTime)
                    continue;

                while (now + 0.0001f >= mod.NextTickTime && mod.TicksApplied < mod.MaxTicks && mod.NextTickTime <= mod.ExpireTime + 0.0001f && target.IsAlive && target.PlayerState.CurrentHPWire > 0)
                {
                    int tickIndex = mod.TicksApplied + 1;
                    int minDamage;
                    int maxDamage;
                    if (string.Equals(mod.AttackType, "RANGED", StringComparison.OrdinalIgnoreCase))
                        DamageComputer.ComputeRangedDamageRange(mod.SourceLevel, Math.Max(1, mod.SourceAgility), 1f, mod.DamageMod, mod.DamageVolatility, out minDamage, out maxDamage);
                    else
                        DamageComputer.ComputeSpellDamageRange(mod.SourceLevel, Math.Max(1, mod.SourceIntellect), mod.DamageMod, mod.DamageVolatility, out minDamage, out maxDamage);

                    uint damageRaw = NativeRngLedger.Generate(modRng, "room", "EffectMod::applyEffect:SpellDamageEffect::damage", $"{sourceMonster.Name}#{sourceMonster.EntityId}->{target.Name}#{target.EntityId}");
                    int rawDamageWire = DamageComputer.RollSpellDamageRange(minDamage, maxDamage, damageRaw);
                    uint damageWire = (uint)Math.Max(0, rawDamageWire);
                    uint beforeHP = target.PlayerState.CurrentHPWire;
                    NativeDamageQueryResult query = ApplyNativePlayerDamageQueryWire(damageWire, target, sourceMonster, mod.DamageTypeId, mod.DamageKind, mod.NextTickTime, source ?? "EffectMod::applyEffect", mod.SourceLevel, modRng);
                    uint adjustedDamageWire = query.AdjustedDamageWire;
                    uint afterHP = beforeHP;
                    uint effectRaw = 0;
                    bool applied = adjustedDamageWire > 0;

                    if (applied)
                    {
                        target.PlayerState.TakeQueriedDamage(adjustedDamageWire, mod.NextTickTime);
                        afterHP = target.PlayerState.CurrentHPWire;
                        target.IsAlive = afterHP > 0;
                        effectRaw = ConsumeNativeOnApplyDamageEffectRng(modRng, "monster-effectmod", target.EntityId, target.Name, beforeHP, afterHP, target.PlayerState.MaxHPWire, adjustedDamageWire, source ?? "EffectMod::applyEffect");
                        if (mod.DamageKind != 3 && beforeHP > afterHP)
                            ApplyNativeMonsterOnDamageCallback(sourceMonster, beforeHP - afterHP, mod.NextTickTime, source ?? "EffectMod::applyEffect");
                    }

                    string resultName = applied ? "HIT" : query.ResultName;
                    Debug.LogError($"[PLAYER-EFFECTMOD-TICK] target={target.Name}#{target.EntityId} source={sourceMonster.Name}#{sourceMonster.EntityId} tick={tickIndex}/{mod.MaxTicks} result={resultName} skill={mod.SkillPath} modifier={mod.ModifierPath} effect={mod.ModifierEffectPath} attackType={mod.AttackType} damageType={mod.DamageType} damageTypeId={mod.DamageTypeId} damageKind={mod.DamageKind} preQueryWire={damageWire} damageWire={adjustedDamageWire} hp={beforeHP}->{afterHP}/{target.PlayerState.MaxHPWire} range=[{minDamage},{maxDamage}] damageRaw=0x{damageRaw:X8} effectRaw=0x{effectRaw:X8} resist={NativeDamageResistLogCode(query)} due={mod.NextTickTime:F3} now={now:F3} rngAfter={modRng.CallsSinceReseed} native=EffectMod::applyEffect@0x0055FF70 SpellDamageEffect::doEffect@0x0054FD20");
                    Debug.LogError($"[NATIVE-DAMAGE-CONTRACT] source={source ?? "EffectMod::applyEffect"} target={target.Name}#{target.EntityId} damageTypeId={mod.DamageTypeId} damageKind={mod.DamageKind} rawRollWire={damageWire} preQueryWire={damageWire} damageTakenMod={query.DamageTakenMod:F2} typeMod={query.DamageTypeMod:F2} postQueryWire={adjustedDamageWire} hp={beforeHP}->{afterHP}/{target.PlayerState.MaxHPWire} resist={query.ResultName} resistRaw=0x{query.ResistRaw:X8} resistChance={query.ResistChanceWire / 256f:F2} nativeDamageTime={mod.NextTickTime:F3} target=Avatar native=Damage::apply@0x004F6580 Unit::onQueryApplyDamage@0x0050B9C0");
                    Debug.LogError($"[PLAYER-DAMAGE] source=monster-effectmod attacker={sourceMonster.Name}#{sourceMonster.EntityId} target={target.Name}#{target.EntityId} result={resultName} skill={mod.SkillPath} damageWire={adjustedDamageWire} preQueryWire={damageWire} hp={beforeHP}->{afterHP}/{target.PlayerState.MaxHPWire} marker=SPELL-MOD source={source} damageRaw=0x{damageRaw:X8} effectRaw=0x{effectRaw:X8} rngPos={modRng.CallsSinceReseed}");
                    if (applied)
                        OnPlayerDamageResolved?.Invoke(sourceMonster, target, true, afterHP, source != null ? $"EffectMod::applyEffect:{source}" : "EffectMod::applyEffect");

                    aggregate.DamageApplied |= applied;
                    aggregate.Died |= afterHP == 0;
                    if (aggregate.TicksApplied == 0)
                        aggregate.OldHPWire = beforeHP;
                    aggregate.NewHPWire = afterHP;
                    mod.TicksApplied++;
                    aggregate.TicksApplied++;
                    mod.NextTickTime += mod.Frequency;
                    if (afterHP == 0)
                    {
                        RemovePlayerDamageModifiersForTarget(target.EntityId, source ?? "EffectMod::target-death");
                        break;
                    }
                }

                if (_activePlayerDamageModifiers.TryGetValue(key, out var afterMod))
                {
                    bool targetDead = !target.IsAlive || target.PlayerState.CurrentHPWire == 0;
                    bool expired = IsNativeEffectModExpired(afterMod.DurationTicks, afterMod.ExpireTime, now);
                    bool removeAfterTick = expired || (targetDead && afterMod.RemoveOnDeath);
                    if (removeAfterTick)
                    {
                        string reason = targetDead && afterMod.RemoveOnDeath ? "RemoveOnDeath" : "duration-expired";
                        RaisePlayerModifierRemove(sourceMonster, target, key, afterMod.ModifierPath, afterMod.SkillPath, afterMod.EffectPath, source ?? "unknown", reason == "RemoveOnDeath" ? "ModifierDesc.RemoveOnDeath Modifiers::processRemoveModifier@0x00502390" : "Modifiers::update@0x00501E50 Modifiers::removeModifierLocal@0x00501B50");
                        _activePlayerDamageModifiers.Remove(key);
                        Debug.LogError($"[PLAYER-EFFECTMOD] complete target={target.Name}#{target.EntityId} source={sourceMonster.Name}#{sourceMonster.EntityId} ticks={afterMod.TicksApplied}/{afterMod.MaxTicks} hp={target.PlayerState.CurrentHPWire} reason={reason} expireAt={afterMod.ExpireTime:F3} now={now:F3} source={source ?? "unknown"} native=EffectMod::update@0x0055FE70");
                    }
                }
            }

            return aggregate;
        }

        public MonsterModifierApplyResult ApplyProjectileModifierFromSpell(Monster target, uint sourceEntityId, PlayerState sourceState, SpellData spell, MersenneTwister rng, int skillLevel, float now, string source)
        {
            var result = new MonsterModifierApplyResult
            {
                Reason = source ?? "spell-mod",
                OldHPWire = target != null ? PeekRuntimeMonsterHPWire(target) : 0,
                NewHPWire = target != null ? PeekRuntimeMonsterHPWire(target) : 0
            };

            if (target == null || spell == null || !spell.HasProjectileModifierDamage)
            {
                result.Reason = "missing-target-or-modifier";
                return result;
            }
            if (!target.IsAlive || PeekRuntimeMonsterHPWire(target) == 0)
            {
                result.Reason = "target-dead";
                return result;
            }
            if (sourceState == null)
            {
                result.Reason = "missing-source-state";
                return result;
            }
            if (rng == null)
            {
                result.Reason = "missing-rng";
                return result;
            }

            float applyTime = now >= 0f ? now : GetNativeCombatTime();
            float frequency = spell.ProjectileModifierFrequency > 0f ? spell.ProjectileModifierFrequency : 1f;
            float duration = spell.ProjectileModifierDuration > 0f ? spell.ProjectileModifierDuration : 0f;
            ushort durationTicks = ComputeNativeSpellModDurationTicks(duration);
            ushort frequencyTicks = ComputeNativeEffectModFrequencyTicks(frequency);
            int startNativeTick = (int)ResolveNativeTickForTime(applyTime);
            int initialFrequencyCountdown = Math.Max(1, (int)frequencyTicks);
            string key = BuildMonsterModifierKey(target.EntityId, sourceEntityId, spell);
            bool replace = _activeMonsterModifiers.ContainsKey(key);
            _activeMonsterModifiers[key] = new ActiveMonsterModifier
            {
                TargetEntityId = target.EntityId,
                SourceEntityId = sourceEntityId,
                SourceState = sourceState,
                Spell = spell,
                SkillLevel = skillLevel,
                LastNativeTick = startNativeTick,
                DurationTicksInitial = durationTicks,
                DurationTicksRemaining = durationTicks,
                FrequencyTicks = frequencyTicks,
                FrequencyCountdownTicks = initialFrequencyCountdown,
                TicksApplied = 0,
                ModifierKey = key
            };

            result.AppliedModifier = true;
            result.NewHPWire = PeekRuntimeMonsterHPWire(target);
            Debug.LogError($"[POISON-SHOT-MOD] {(replace ? "replace" : "add")} target={target.Name}#{target.EntityId} source={sourceEntityId} modifier={spell.ProjectileModifierId} effect={spell.ProjectileModifierEffectId} duration={duration:F2}s frequency={frequency:F2}s durationTicks={durationTicks} frequencyTicks={frequencyTicks} countdown={initialFrequencyCountdown} stack={spell.ProjectileModifierStackRule ?? "UNKNOWN"} firstTick=deferred impactTime={applyTime:F3} nativeTick={startNativeTick} hp={result.OldHPWire}->{result.NewHPWire} rngBefore={rng.CallsSinceReseed} source={source ?? "unknown"} native=SpellModEffect::doEffect@0x00554460 EffectMod::init@0x0055FE20 EffectMod::update@0x0055FE70");
            return result;
        }

        public MonsterModifierApplyResult AdvanceMonsterModifierRuntimeForTarget(uint targetEntityId, MersenneTwister rng, float now, string source)
        {
            return AdvanceMonsterModifierRuntime(rng, now, source, targetEntityId);
        }

        private MonsterModifierApplyResult AdvanceMonsterModifierRuntime(MersenneTwister rng, float now, string source, uint onlyTargetEntityId = 0)
        {
            var aggregate = new MonsterModifierApplyResult
            {
                Reason = source ?? "modifier-runtime"
            };
            if (_advancingMonsterModifiers || _activeMonsterModifiers.Count == 0)
                return aggregate;
            if (now < 0f) now = GetNativeCombatTime();
            int nowTick = (int)ResolveNativeTickForTime(now);

            _advancingMonsterModifiers = true;
            try
            {
                var keys = _activeMonsterModifiers.Keys.ToList();
                foreach (string key in keys)
                {
                    if (!_activeMonsterModifiers.TryGetValue(key, out var mod) || mod == null)
                        continue;
                    if (onlyTargetEntityId != 0 && mod.TargetEntityId != onlyTargetEntityId)
                        continue;
                    if (!_activeMonsters.TryGetValue(mod.TargetEntityId, out var monster) || monster == null || !monster.IsAlive || PeekRuntimeMonsterHPWire(monster) == 0)
                    {
                        _activeMonsterModifiers.Remove(key);
                        Debug.LogError($"[POISON-SHOT-MOD] remove target={mod.TargetEntityId} modifier={mod.ModifierKey} source={source ?? "unknown"} reason=RemoveOnDeathOrMissing");
                        continue;
                    }
                    if (mod.SourceState == null || mod.Spell == null)
                    {
                        _activeMonsterModifiers.Remove(key);
                        Debug.LogError($"[POISON-SHOT-MOD] remove target={mod.TargetEntityId} modifier={mod.ModifierKey} source={source ?? "unknown"} reason=missing-source-state");
                        continue;
                    }
                    MersenneTwister modRng = GetRoomRngForMonster(monster);
                    if (modRng == null)
                    {
                        RuntimeEvidenceManager.LogFallbackHit(
                            "rng-instance",
                            "modifier-missing-instance-rng",
                            $"target={monster.EntityId} source={source ?? "unknown"}",
                            64);
                        Debug.LogError($"[POISON-SHOT-MOD] skip tick target={monster.Name}#{monster.EntityId} source={source ?? "unknown"} reason=missing-instance-rng");
                        continue;
                    }
                    if (nowTick <= mod.LastNativeTick)
                        continue;

                    int ticksToProcess = Math.Min(256, nowTick - mod.LastNativeTick);
                    string removeAfterTick = null;
                    for (int i = 0; i < ticksToProcess && monster.IsAlive && PeekRuntimeMonsterHPWire(monster) > 0; i++)
                    {
                        mod.LastNativeTick++;
                        if (mod.DurationTicksInitial > 0)
                        {
                            if (mod.DurationTicksRemaining > 0)
                                mod.DurationTicksRemaining--;
                            if (mod.DurationTicksRemaining == 0)
                            {
                                removeAfterTick = "duration-expired";
                                break;
                            }
                        }

                        if (mod.FrequencyCountdownTicks > 0)
                            mod.FrequencyCountdownTicks--;
                        if (mod.FrequencyCountdownTicks != 0)
                            continue;

                        int tickIndex = mod.TicksApplied + 1;
                        float nativeDamageTime = mod.LastNativeTick * NATIVE_UNIT_TICK_INTERVAL;
                        var damage = DamageComputer.ProcessProjectileModifierTick(
                            modRng,
                            mod.SourceState.Level,
                            mod.SourceState.Intelligence,
                            mod.SourceState.Agility,
                            mod.SourceState.Strength,
                            mod.SourceState.WeaponDamage,
                            mod.SourceState.WeaponDamageVolatility,
                            mod.Spell,
                            monster,
                            mod.SkillLevel,
                            DamageComputer.ResolveNativeCriticalDamagePercent(mod.SourceState));

                        if (damage.Type == AttackResultType.Miss || damage.DamageF32 <= 0)
                        {
                            Debug.LogError($"[POISON-SHOT-TICK] target={monster.Name}#{monster.EntityId} tick={tickIndex} result={damage.Type} damageWire=0 hp={PeekRuntimeMonsterHPWire(monster)} durationRemaining={mod.DurationTicksRemaining} frequencyTicks={mod.FrequencyTicks} nativeTick={mod.LastNativeTick} source={source ?? "unknown"} rngAfter={modRng.CallsSinceReseed}");
                        }
                        else
                        {
                            bool applied = ApplyNativePlayerDamageToMonsterWire(
                                monster,
                                (uint)damage.DamageF32,
                                "SPELL-MOD",
                                out uint oldHPWire,
                                out uint newHPWire,
                                out bool died,
                                nativeDamageTime: nativeDamageTime,
                                nativeDamageTick: (uint)Math.Max(0, mod.LastNativeTick),
                                damageTypeId: damage.DamageTypeId,
                                rawDamageWire: (uint)damage.DamageF32);
                            if (applied)
                                NotifyMonsterDamagedByPlayer(monster, mod.SourceEntityId, "modifier-tick");
                            uint effectRaw = applied
                                ? ConsumeNativeOnApplyDamageEffectRng(modRng, "player-spell-mod", monster.EntityId, monster.Name, oldHPWire, newHPWire, monster.MaxHPWire, (uint)damage.DamageF32, source ?? "modifier-tick")
                                : 0;
                            string resultName = damage.Type.ToString().ToUpperInvariant();
                            Debug.LogError($"[POISON-SHOT-TICK] target={monster.Name}#{monster.EntityId} source={mod.SourceEntityId} tick={tickIndex} result={resultName} damageWire={damage.DamageF32} hp={oldHPWire}->{newHPWire} range=[{damage.MinDamageF32},{damage.MaxDamageF32}] damageRaw=0x{damage.DamageRaw:X8} effectRaw=0x{effectRaw:X8} durationRemaining={mod.DurationTicksRemaining} frequencyTicks={mod.FrequencyTicks} nativeTick={mod.LastNativeTick} due={nativeDamageTime:F3} now={now:F3} applied={applied} died={died} rngAfter={modRng.CallsSinceReseed}");
                            Debug.LogError($"[COMBAT-EVENT] actor=player-spell-mod actorId={mod.SourceEntityId} target=monster targetId={monster.EntityId} result={resultName} damageWire={damage.DamageF32} hp={oldHPWire}->{newHPWire} range=[{damage.MinDamageF32},{damage.MaxDamageF32}] damageRaw=0x{damage.DamageRaw:X8} effectRaw=0x{effectRaw:X8} spell={mod.Spell.DisplayName} rngAfter={modRng.CallsSinceReseed} marker=SPELL-MOD");
                            aggregate.DamageApplied |= applied;
                            aggregate.Died |= died;
                            if (aggregate.TicksApplied == 0)
                                aggregate.OldHPWire = oldHPWire;
                            aggregate.NewHPWire = newHPWire;
                            if (died)
                            {
                                _pendingModifierKills.Enqueue(new PendingModifierKill
                                {
                                    SourceEntityId = mod.SourceEntityId,
                                    TargetEntityId = monster.EntityId,
                                    Source = source ?? "modifier-tick",
                                    NativeDamageTime = nativeDamageTime
                                });
                            }
                        }

                        mod.TicksApplied++;
                        aggregate.TicksApplied++;
                        mod.FrequencyCountdownTicks = Math.Max(1, (int)mod.FrequencyTicks);
                        if (!monster.IsAlive || PeekRuntimeMonsterHPWire(monster) == 0)
                            break;
                    }

                    if (removeAfterTick != null || !monster.IsAlive || PeekRuntimeMonsterHPWire(monster) == 0)
                    {
                        _activeMonsterModifiers.Remove(key);
                        string reason = !monster.IsAlive || PeekRuntimeMonsterHPWire(monster) == 0 ? "target-dead" : removeAfterTick;
                        Debug.LogError($"[POISON-SHOT-MOD] complete target={monster.Name}#{monster.EntityId} source={mod.SourceEntityId} ticks={mod.TicksApplied} durationTicks={mod.DurationTicksInitial} remaining={mod.DurationTicksRemaining} hp={PeekRuntimeMonsterHPWire(monster)} reason={reason ?? "target-dead"} source={source ?? "unknown"} native=Modifier::update@0x004FF1B0 EffectMod::update@0x0055FE70");
                    }
                }
            }
            finally
            {
                _advancingMonsterModifiers = false;
            }

            return aggregate;
        }

        private void SetRuntimeMonsterHPWire(Monster monster, uint hp, bool committedDamage, float regenClockTime = -1f, string source = "SET")
        {
            SetRuntimeMonsterHPWire(monster, hp, committedDamage, regenClockTime, source, 0u);
        }

        private void SetRuntimeMonsterHPWire(Monster monster, uint hp, bool committedDamage, float regenClockTime, string source, uint nativeTick)
        {
            if (monster == null) return;
            if (hp > monster.MaxHPWire) hp = monster.MaxHPWire;
            var state = GetMonsterHPAuthority(monster);
            uint oldHP = state.RuntimeHPWire;
            float historyTime = regenClockTime >= 0f ? regenClockTime : GetNativeCombatTime();
            RecordMonsterHPHistory(monster, state, oldHP, hp, committedDamage, historyTime, source, nativeTick);
            state.RuntimeHPWire = hp;
            state.RuntimeInitialized = true;
            if (hp == 0 || hp >= monster.MaxHPWire)
            {
                monster.DeathPendingClientConfirmation = false;
                monster.DeathPendingSince = 0f;
            }
            if (committedDamage && hp > 0 && hp < monster.MaxHPWire)
            {
                _monsterRuntimeDamageCommitted.Add(monster.EntityId);
                ushort cooldown = ResolveNativeDamageRegenCooldownTicks(monster);
                if (cooldown > 0)
                    _monsterHPRegenCooldownTicks[monster.EntityId] = cooldown;
                else
                    _monsterHPRegenCooldownTicks.Remove(monster.EntityId);
                Debug.LogError($"[MON-REGEN-COOLDOWN] entity={monster.EntityId} name={monster.Name} cooldown={cooldown} stockUnitDelayClear={ShouldClearStockUnitDamageRegenDelay(monster)} source={source ?? "unknown"}");
            }
            else if (hp == 0 || (!committedDamage && hp >= monster.MaxHPWire))
            {
                _monsterRuntimeDamageCommitted.Remove(monster.EntityId);
                _monsterHPRegenCooldownTicks.Remove(monster.EntityId);
            }
            SyncMonsterHPAuthority(monster, state);
            ResetMonsterHPRegenClock(monster, regenClockTime);
            if (oldHP != hp)
                Debug.LogError($"[MON-HP-CANON] write entity={monster.EntityId} old={oldHP} new={hp} committed={committedDamage} source={source ?? "unknown"}");
        }

        private ushort ResolveNativeDamageRegenCooldownTicks(Monster monster)
        {
            return ShouldClearStockUnitDamageRegenDelay(monster) ? (ushort)0 : NATIVE_DAMAGE_REGEN_COOLDOWN_TICKS;
        }

        private bool ShouldClearStockUnitDamageRegenDelay(Monster monster)
        {
            return monster != null && ResolveNativeStockUnitDamageRegenDelayClearFlag();
        }

        private bool ResolveNativeStockUnitDamageRegenDelayClearFlag()
        {
            int nativeWorldSettingsField = GCDatabase.Instance.GetKnobInt("MinLevelForWorldChat", 15);
            return (nativeWorldSettingsField & 0x00000800) != 0;
        }

        public uint GetMonsterCurrentHPWire(Monster monster)
        {
            return GetRuntimeMonsterHPWire(monster, "HP-READ");
        }

        public uint GetMonsterCurrentHPWire(Monster monster, string source)
        {
            return GetRuntimeMonsterHPWire(monster, source);
        }

        public uint GetMonsterCurrentHPWire(Monster monster, string source, float now)
        {
            return GetRuntimeMonsterHPWire(monster, source, now);
        }

        public uint PeekMonsterCurrentHPWire(Monster monster)
        {
            return PeekRuntimeMonsterHPWire(monster);
        }

        private static bool IsRuntimeMonsterHPSuffixContext(SyncContext context)
        {
            return context == SyncContext.MonsterAction
                || context == SyncContext.MonsterMove
                || context == SyncContext.MonsterDamage;
        }

        public string DescribeMonsterHPAuthority(Monster monster)
        {
            if (monster == null) return "monster=<null>";
            var state = GetMonsterHPAuthority(monster);
            if (state == null) return $"{monster.Name}#{monster.EntityId} state=<null>";

            float observedAge = state.LastClientHPReportTime > 0f ? Time.time - state.LastClientHPReportTime : -1f;
            bool dirty = _monsterRuntimeDamageCommitted.Contains(monster.EntityId);
            return $"{monster.Name}#{monster.EntityId} runtime={state.RuntimeHPWire}/{monster.MaxHPWire} dirty={dirty} client={state.LastClientHPReportWire} clientAge={observedAge:F3} lastPacket={state.LastPacketHPWire}";
        }

        public bool TryResolveMonsterSynchronizedHP(Monster monster, string packetName, out uint hpWire)
        {
            return TryResolveMonsterSynchronizedHP(monster, SyncContext.Unknown, packetName, out hpWire, out _);
        }

        public bool TryResolveMonsterSynchronizedHP(Monster monster, SyncContext context, string packetName, out uint hpWire, out string reason)
        {
            return TryResolveMonsterSynchronizedHP(monster, context, packetName, -1f, out hpWire, out reason);
        }

        public bool TryResolveMonsterSynchronizedHP(Monster monster, SyncContext context, string packetName, float now, out uint hpWire, out string reason)
        {
            NativeHpVisibilityCutoff cutoff = new NativeHpVisibilityCutoff
            {
                Tick = ResolveNativeTickForTime(now >= 0f ? now : GetNativeCombatTime()),
                Time = now >= 0f ? now : GetNativeCombatTime(),
                IncludeSubEntityEffects = true,
                Reason = "legacy-runtime-cutoff",
                Phase = "legacy-runtime",
                Context = context,
                SourceContext = packetName ?? "unknown",
                HasEntityCutoff = _hasCompletedNativeEntityUpdate,
                LastEntityTick = _hasCompletedNativeEntityUpdate ? _lastCompletedNativeEntityUpdateTick : 0u,
                LastEntityTime = _hasCompletedNativeEntityUpdate ? _lastCompletedNativeEntityUpdateTime : -1f,
                HasSubEntityCutoff = _hasCompletedNativeSubEntityUpdate,
                LastSubEntityTick = _hasCompletedNativeSubEntityUpdate ? _lastCompletedNativeSubEntityUpdateTick : 0u,
                LastSubEntityTime = _hasCompletedNativeSubEntityUpdate ? _lastCompletedNativeSubEntityUpdateTime : -1f
            };
            RuntimeEvidenceManager.LogFallbackHit(
                "hp-cutoff",
                "legacy-runtime-cutoff",
                $"context={context} packet='{packetName ?? "unknown"}' tick={cutoff.Tick}",
                64);
            return TryResolveMonsterSynchronizedHP(monster, context, packetName, cutoff, out hpWire, out reason);
        }

        public bool TryResolveMonsterSynchronizedHP(Monster monster, SyncContext context, string packetName, NativeHpVisibilityCutoff cutoff, out uint hpWire, out string reason)
        {
            hpWire = 0;
            reason = "missing-monster";
            if (monster == null) return false;
            uint serverHPWire = PeekRuntimeMonsterHPWire(monster);
            var state = GetMonsterHPAuthority(monster);
            if (state == null)
            {
                reason = "missing-monster-hp-state";
                return false;
            }

            bool dirtyRuntimeHP = _monsterRuntimeDamageCommitted.Contains(monster.EntityId)
                && serverHPWire > 0
                && serverHPWire < monster.MaxHPWire;
            bool exactClientObservedHP = state.LastClientHPReportTime > 0f
                && state.LastClientHPReportWire == serverHPWire;

            bool usedHistory = TryResolveMonsterHPFromHistory(monster, state, cutoff, out uint visibleHPWire, out string hpView, out string mutationSource, out string excludedSameTickProjectile);
            hpWire = visibleHPWire;
            state.LastPacketHPWire = hpWire;
            SyncMonsterHPAuthority(monster, state);
            reason = exactClientObservedHP && hpWire == serverHPWire
                ? "client-confirmed-hp"
                : dirtyRuntimeHP ? "server-runtime-dirty-hp" : "server-runtime-hp";
            reason = $"{reason}; hpView={hpView}; visibleCutoffTick={cutoff.Tick}; visibleCutoffTime={cutoff.Time:F3}; cutoffPhase={cutoff.Phase}; includeSubEntity={cutoff.IncludeSubEntityEffects}; cutoffReason={cutoff.Reason}; mutationSource={mutationSource}; excludedSameTickProjectile={excludedSameTickProjectile}; runtimeHP={serverHPWire}; usedHistory={usedHistory}; lastEntity={cutoff.LastEntityTick}@{cutoff.LastEntityTime:F3}; lastSubEntity={cutoff.LastSubEntityTick}@{cutoff.LastSubEntityTime:F3}";
            if (usedHistory)
                Debug.LogError($"[MON-HP-VISIBLE] packet={packetName ?? "unknown"} context={context} entity={monster.EntityId} name={monster.Name} runtimeHP={serverHPWire} visibleHP={hpWire} cutoffPhase={cutoff.Phase} cutoffTick={cutoff.Tick} cutoffTime={cutoff.Time:F3} includeSubEntity={cutoff.IncludeSubEntityEffects} mutationSource={mutationSource} excludedSameTickProjectile={excludedSameTickProjectile}");
            return true;
        }

        private void CommitClientReportedMonsterHP(Monster monster, MonsterHpAuthorityState state, uint hpWire, string source)
        {
            if (monster == null || state == null) return;
            if (hpWire > monster.MaxHPWire) hpWire = monster.MaxHPWire;
            if (state.RuntimeInitialized && state.RuntimeHPWire == hpWire) return;
            SetRuntimeMonsterHPWire(monster, hpWire, hpWire < monster.MaxHPWire, Time.time, $"client-authority:{source ?? "unknown"}");
        }

        public void RecordMonsterHPObservation(Monster monster, uint hpWire, string source)
        {
            if (monster == null) return;
            if (hpWire > monster.MaxHPWire) hpWire = monster.MaxHPWire;
            var state = GetMonsterHPAuthority(monster);
            if (state == null) return;
            state.LastClientHPReportTime = Time.time;
            state.LastClientHPReportWire = hpWire;
            SyncMonsterHPAuthority(monster, state);
            CommitClientReportedMonsterHP(monster, state, hpWire, source);
            Debug.LogError($"[MON-HP-CLIENT] client-authoritative {monster.Name}#{monster.EntityId} hp={hpWire / 256f:F2}/{monster.MaxHPWire / 256f:F2} source={source ?? "unknown"}");
            if (HpSyncService.Instance.TryResolveMonsterOwner(monster, out HpOwnerRef owner))
                HpSyncService.Instance.ObserveClientHpReport(owner, hpWire, HpSyncService.ClassifyReportSource(source), source ?? "client-monster-hp", true, out _);
        }

        public void RecordMonsterOutboundHP(Monster monster, uint hpWire, string source)
        {
            if (monster == null) return;
            if (hpWire > monster.MaxHPWire) hpWire = monster.MaxHPWire;
            var state = GetMonsterHPAuthority(monster);
            if (state == null) return;
            state.LastPacketHPWire = hpWire;
            SyncMonsterHPAuthority(monster, state);
            HpSyncService.Instance.RecordMonsterOutboundHP(monster, hpWire, source ?? "outbound");
        }

        public void SetMonsterHPWire(Monster monster, uint hp, bool committedDamage = false, string source = "SET")
        {
            SetRuntimeMonsterHPWire(monster, hp, committedDamage, -1f, source);
        }

        public void NotifyMonsterDamagedByPlayer(Monster monster, uint playerEntityId, string reason)
        {
            NotifyMonsterNativeOnAttackedAdmission(monster, playerEntityId, reason);
        }

        public void NotifyMonsterNativeOnAttackedAdmission(Monster monster, uint playerEntityId, string reason)
        {
            if (monster == null || !monster.IsAlive || monster.DeathPendingClientConfirmation || playerEntityId == 0) return;
            if (!_players.TryGetValue(playerEntityId, out var player) || player == null || !player.IsAlive) return;
            Debug.LogError($"[MON-ONATTACKED-ADMISSION] monster={monster.Name}#{monster.EntityId} player={player.Name}#{player.EntityId} source={reason ?? "unknown"} rangeGate=False smmsg=0x09 native=Damage::apply@0x004F6580->MonsterBehavior2::onAttacked@0x0051B550");
            AggroMonster(monster, player, reason, false);
        }

        public bool IsMonsterDeathPendingClientConfirmation(Monster monster)
        {
            return monster != null && monster.DeathPendingClientConfirmation;
        }

        public void MarkMonsterNativeDead(Monster monster, string source)
        {
            MarkMonsterNativeDead(monster, source, true);
        }

        private void MarkMonsterNativeDead(Monster monster, string source, bool mirrorActive)
        {
            if (monster == null) return;

            bool hadRuntimeState = monster.IsAlive
                || monster.State != MonsterState.Dead
                || monster.TargetId != 0
                || monster.AggroTriggered
                || monster.AggroSent
                || monster.AlertSourceEntityId != 0
                || monster.AttackPending
                || monster.AttackSoundPending
                || monster.AttackClientVisible
                || monster.AttackNativeContactOnly
                || monster.CombatContactTargetId != 0;

            monster.DeathPendingClientConfirmation = false;
            monster.DeathPendingSince = 0f;
            monster.IsAlive = false;
            monster.State = MonsterState.Dead;
            monster.ClearTarget();
            monster.AggroTriggered = false;
            monster.AggroSent = false;
            monster.AttackPending = false;
            monster.AttackSoundPending = false;
            monster.AttackClientVisible = false;
            monster.AttackNativeContactOnly = false;
            monster.AttackHitResolved = false;
            monster.AttackStartedTime = 0f;
            monster.AttackCommitTime = 0f;
            monster.AttackSoundTime = 0f;
            monster.AttackEndTime = 0f;
            monster.AttackUseRaw = 0;
            monster.UsePrimaryActiveSkillThisAttack = false;
            monster.AttackCommitTargetX = 0f;
            monster.AttackCommitTargetY = 0f;
            monster.AttackSearchTargetId = 0;
            monster.CombatContactTargetId = 0;
            monster.CombatContactUntil = 0f;
            monster.NativeAi?.PostMessage(NativeMonsterMessageId.DeathWarning, source);
            monster.NativeAi?.SetState(NativeMonsterStateId.DeathWarning, "death");
            NativeEncounterOnUnitDied(monster, source ?? "death");
            _monsterFarTargetActionLogTime.Remove(monster.EntityId);
            RemoveMonsterModifiersForTarget(monster.EntityId, source ?? "death");
            RemovePlayerModifiersFromSource(monster.EntityId, source ?? "death");
            WanderSimulator.Instance.UnregisterEntity(monster.EntityId);

            if (hadRuntimeState)
                Debug.LogError($"[MON-DEATH-STATE] cleared action/move target for {monster.Name}#{monster.EntityId} source={source ?? "unknown"}");

            if (mirrorActive && _activeMonsters.TryGetValue(monster.EntityId, out var active) && active != monster)
                MarkMonsterNativeDead(active, source, false);
        }

        private void MarkMonsterDeathPendingClientConfirmation(Monster monster, string source)
        {
            if (monster == null) return;
            bool keepPendingNativeAttack = monster.AttackPending
                && monster.AttackNativeContactOnly
                && !monster.AttackHitResolved
                && monster.AttackCommitTime > 0f
                && monster.TargetId != 0;
            monster.DeathPendingClientConfirmation = true;
            monster.DeathPendingSince = GetNativeCombatTime();
            if (!keepPendingNativeAttack)
            {
                monster.AttackPending = false;
                monster.AttackClientVisible = false;
                monster.AttackNativeContactOnly = false;
                monster.AttackSoundPending = false;
                monster.AttackHitResolved = false;
                monster.AttackStartedTime = 0f;
                monster.AttackEndTime = 0f;
                monster.AttackCommitTime = 0f;
                monster.AttackSoundTime = 0f;
                monster.CombatContactTargetId = 0;
                monster.CombatContactUntil = 0f;
                monster.TargetId = 0;
                monster.AlertSourceEntityId = 0;
                monster.AggroTriggered = false;
                if (monster.IsAlive)
                    monster.State = MonsterState.Idle;
                monster.NativeAi?.PostMessage(NativeMonsterMessageId.DeathWarning, source);
                monster.NativeAi?.SetState(NativeMonsterStateId.DeathWarning, "death-pending");
            }
            Debug.LogError($"[MON-DEATH-GATE] pending client death confirmation {monster.Name}#{monster.EntityId} source={source ?? "unknown"} hp={GetRuntimeMonsterHPWire(monster, "death-pending")} keepNativeAttack={keepPendingNativeAttack}");
        }

        public void BeginNativeMonsterDeathLifecycle(Monster monster, string source)
        {
            if (monster == null) return;
            MarkMonsterNativeDead(monster, source);
            if (monster.NativeDeathLifecycleActive)
                return;

            monster.NativeDeathLifecycleActive = true;
            monster.NativeDeathRemoveSent = false;
            monster.NativeDeathState = 7;
            monster.NativeCorpseTicksRemaining = monster.CorpseLingerTicks;
            monster.NativeFadeTicksRemaining = 0;
            _monsterDeathUpdateAccum[monster.EntityId] = 0f;
            WanderSimulator.Instance.UnregisterEntity(monster.EntityId);
            Debug.LogError($"[MON-DEATH-LIFECYCLE] state=7 corpse {monster.Name}#{monster.EntityId} ticks={monster.NativeCorpseTicksRemaining} source={source ?? "unknown"}");
        }

        private void ProcessNativeDeathLifecycles(float deltaTime)
        {
            if (deltaTime <= 0f || _activeMonsters.Count == 0)
                return;

            var monsterIds = new List<uint>(_activeMonsters.Keys);
            foreach (uint entityId in monsterIds)
            {
                if (!_activeMonsters.TryGetValue(entityId, out var monster))
                    continue;
                if (!monster.NativeDeathLifecycleActive || monster.NativeDeathRemoveSent)
                    continue;

                _monsterDeathUpdateAccum.TryGetValue(entityId, out float accum);
                accum += deltaTime;
                int ticks = Mathf.Min(256, Mathf.FloorToInt(accum / NATIVE_UNIT_TICK_INTERVAL));
                if (ticks <= 0)
                {
                    _monsterDeathUpdateAccum[entityId] = accum;
                    continue;
                }

                accum -= ticks * NATIVE_UNIT_TICK_INTERVAL;
                _monsterDeathUpdateAccum[entityId] = accum;

                for (int i = 0; i < ticks; i++)
                {
                    if (monster.NativeDeathState == 7)
                    {
                        if (monster.NativeCorpseTicksRemaining > 0)
                            monster.NativeCorpseTicksRemaining--;
                        if (monster.NativeCorpseTicksRemaining == 0)
                        {
                            monster.NativeDeathState = 9;
                            monster.NativeFadeTicksRemaining = NATIVE_STOCKUNIT_FADE_TICKS;
                            Debug.LogError($"[MON-DEATH-LIFECYCLE] state=9 fade {monster.Name}#{monster.EntityId} ticks={monster.NativeFadeTicksRemaining}");
                        }
                        continue;
                    }

                    if (monster.NativeDeathState == 9)
                    {
                        if (monster.NativeFadeTicksRemaining > 0)
                            monster.NativeFadeTicksRemaining--;
                        if (monster.NativeFadeTicksRemaining == 0)
                        {
                            monster.NativeDeathRemoveSent = true;
                            float respawnDelaySeconds = monster.RespawnRateTicks * NATIVE_UNIT_TICK_INTERVAL;
                            Debug.LogError($"[MON-DEATH-LIFECYCLE] remove {monster.Name}#{monster.EntityId} after corpse/fade autoRespawn={monster.AutoRespawn} respawnTicks={monster.RespawnRateTicks}");
                            DespawnMonster(entityId, monster.AutoRespawn, respawnDelaySeconds);
                            break;
                        }
                    }
                }
            }
        }

        public void LogMonsterClientVisibleSwingNoDamage(Monster monster, string reason)
        {
            if (monster == null || !monster.IsAlive) return;
            uint hp = GetRuntimeMonsterHPWire(monster, "SWING-NO-DAMAGE");
            Debug.LogError($"[MON-HP-TRUTH] NO-DAMAGE {monster.Name}#{monster.EntityId} hp={hp / 256f:F2}/{monster.MaxHPWire / 256f:F2} reason={reason ?? "unknown"}");
        }

        private void RecordMonsterHPReport(Monster monster, MonsterHpAuthorityState state, uint hpWire)
        {
            if (monster == null || state == null) return;
            state.LastClientHPReportTime = Time.time;
            state.LastClientHPReportWire = hpWire;
            SyncMonsterHPAuthority(monster, state);
        }

        public bool ApplyNativePlayerDamageToMonsterWire(Monster monster, uint damageWire, string source, out uint oldHPWire, out uint newHPWire, out bool died, float nativeDamageTime = -1f, uint nativeDamageTick = 0, int damageTypeId = 0, int weaponClassId = 0, uint rawDamageWire = 0, int attackerLevel = 1, byte damageKind = 0)
        {
            oldHPWire = 0;
            newHPWire = 0;
            died = false;
            if (monster == null || !monster.IsAlive || damageWire == 0) return false;

            float damageTime = nativeDamageTime >= 0f ? nativeDamageTime : GetNativeCombatTime();
            uint preAdvanceHP = PeekRuntimeMonsterHPWire(monster);
            AdvanceMonsterVitalsToNativeTime(monster, damageTime, $"PRE-DAMAGE:{source ?? "damage"}");
            oldHPWire = PeekRuntimeMonsterHPWire(monster);
            newHPWire = oldHPWire;
            if (preAdvanceHP != oldHPWire)
                Debug.LogError($"[MON-HP-TRUTH] PRE-DAMAGE {monster.Name}#{monster.EntityId} hp={preAdvanceHP}->{oldHPWire}/{monster.MaxHPWire} nativeDamageTime={damageTime:F3} source={source ?? "unknown"}");

            NativeDamageQueryResult query = ApplyNativeDamageQueryWire(damageWire, monster, damageTypeId, damageKind, nativeDamageTime, source, attackerLevel);
            uint adjustedDamageWire = query.AdjustedDamageWire;
            if (adjustedDamageWire == 0)
            {
                newHPWire = oldHPWire;
                Debug.LogError($"[MONSTER-DAMAGE] source={source ?? "unknown"} target={monster.Name}#{monster.EntityId} damageWire={damageWire} adjustedWire=0 hp={oldHPWire}->{newHPWire}/{monster.MaxHPWire} died=False nativeDamageTime={damageTime:F3} result={query.ResultName}");
                Debug.LogError($"[NATIVE-DAMAGE-CONTRACT] source={source ?? "unknown"} target={monster.Name}#{monster.EntityId} weaponClassId={weaponClassId} damageTypeId={damageTypeId} rawRollWire={(rawDamageWire != 0 ? rawDamageWire : damageWire)} preQueryWire={damageWire} damageTakenMod={query.DamageTakenMod:F2} typeMod={query.DamageTypeMod:F2} postQueryWire=0 hp={oldHPWire}->{newHPWire}/{monster.MaxHPWire} resist={query.ResultName} resistRaw=0x{query.ResistRaw:X8} resistChance={query.ResistChanceWire / 256f:F2} nativeDamageTime={damageTime:F3} nativeTick={nativeDamageTick}");
                return false;
            }

            newHPWire = adjustedDamageWire >= oldHPWire ? 0u : oldHPWire - adjustedDamageWire;
            SetRuntimeMonsterHPWire(monster, newHPWire, true, damageTime, source ?? "damage", nativeDamageTick);

            if (newHPWire == 0)
            {
                MarkMonsterNativeDead(monster, source);
                died = true;
            }

            Debug.LogError($"[MONSTER-DAMAGE] source={source ?? "unknown"} target={monster.Name}#{monster.EntityId} damageWire={damageWire} adjustedWire={adjustedDamageWire} hp={oldHPWire}->{newHPWire}/{monster.MaxHPWire} died={died} nativeDamageTime={damageTime:F3} result={query.ResultName}");
            Debug.LogError($"[NATIVE-DAMAGE-CONTRACT] source={source ?? "unknown"} target={monster.Name}#{monster.EntityId} weaponClassId={weaponClassId} damageTypeId={damageTypeId} rawRollWire={(rawDamageWire != 0 ? rawDamageWire : damageWire)} preQueryWire={damageWire} damageTakenMod={query.DamageTakenMod:F2} typeMod={query.DamageTypeMod:F2} postQueryWire={adjustedDamageWire} hp={oldHPWire}->{newHPWire}/{monster.MaxHPWire} resist={query.ResultName} resistRaw=0x{query.ResistRaw:X8} resistChance={query.ResistChanceWire / 256f:F2} nativeDamageTime={damageTime:F3} nativeTick={nativeDamageTick}");
            Debug.LogError($"[MON-HP-TRUTH] COMPUTED {monster.Name}#{monster.EntityId} source={source ?? "unknown"} hp={oldHPWire / 256f:F2}->{newHPWire / 256f:F2}/{monster.MaxHPWire / 256f:F2} dmg={adjustedDamageWire / 256f:F2}");
            return true;
        }

        public bool ApplyNativePlayerWeaponDamageToMonsterWire(Monster monster, NativeWeaponDamageResult damageResult, string source, out uint oldHPWire, out uint newHPWire, out bool died, float nativeDamageTime = -1f, uint nativeDamageTick = 0)
        {
            return ApplyNativePlayerWeaponDamageToMonsterWire(monster, damageResult, source, out oldHPWire, out newHPWire, out died, out _, null, null, nativeDamageTime, nativeDamageTick);
        }

        public bool ApplyNativePlayerWeaponDamageToMonsterWire(Monster monster, NativeWeaponDamageResult damageResult, string source, out uint oldHPWire, out uint newHPWire, out bool died, out uint effectRaw, MersenneTwister effectRng, string effectActor, float nativeDamageTime = -1f, uint nativeDamageTick = 0)
        {
            oldHPWire = 0;
            newHPWire = monster != null ? PeekRuntimeMonsterHPWire(monster) : 0;
            died = false;
            effectRaw = 0;
            if (monster == null || damageResult == null || !damageResult.IsHit || damageResult.IsBlocked || damageResult.DamageWire == 0)
                return false;

            string baseSource = source ?? "WeaponDamage";
            string rngActor = effectActor ?? "player-weapon";
            bool applied = ApplyNativePlayerDamageToMonsterWire(
                monster,
                damageResult.DamageWire,
                baseSource,
                out oldHPWire,
                out newHPWire,
                out died,
                nativeDamageTime,
                nativeDamageTick,
                damageResult.DamageTypeId,
                damageResult.WeaponClassId,
                damageResult.DamageWire,
                damageResult.AttackerLevel,
                0);
            if (!applied)
                return false;

            effectRaw = ConsumeNativeOnApplyDamageEffectRng(effectRng, rngActor, monster.EntityId, monster.Name, oldHPWire, newHPWire, monster.MaxHPWire, damageResult.DamageWire, baseSource);

            uint primaryAppliedWire = oldHPWire > newHPWire ? oldHPWire - newHPWire : 0;
            uint totalAppliedWire = primaryAppliedWire;
            if (damageResult.DamageAdds != null)
            {
                foreach (NativeWeaponDamageEvent add in damageResult.DamageAdds)
                {
                    if (add == null || add.DamageWire == 0 || died || monster == null || !monster.IsAlive || PeekRuntimeMonsterHPWire(monster) == 0)
                        break;

                    string addSource = $"{baseSource}-WeaponDamageAdd-{add.Element}";

                    bool addApplied = ApplyNativePlayerDamageToMonsterWire(
                        monster,
                        add.DamageWire,
                        addSource,
                        out uint addOldHPWire,
                        out uint addNewHPWire,
                        out bool addDied,
                        nativeDamageTime,
                        nativeDamageTick,
                        add.DamageTypeId,
                        damageResult.WeaponClassId,
                        add.DamageWire,
                        damageResult.AttackerLevel,
                        3);
                    if (!addApplied)
                        continue;

                    uint addEffectRaw = ConsumeNativeOnApplyDamageEffectRng(effectRng, rngActor, monster.EntityId, monster.Name, addOldHPWire, addNewHPWire, monster.MaxHPWire, add.DamageWire, addSource);

                    uint appliedWire = addOldHPWire > addNewHPWire ? addOldHPWire - addNewHPWire : 0;
                    totalAppliedWire = ClampWireAdd(totalAppliedWire, appliedWire);
                    newHPWire = addNewHPWire;
                    died |= addDied;
                    Debug.LogError($"[NATIVE-DAMAGE-ADD] source={baseSource} target={monster.Name}#{monster.EntityId} element={add.Element} damageTypeId={add.DamageTypeId} weaponAdd={add.WeaponAdd} bonus={add.DamageBonus} mod={add.DamageMod} weaponDamage={DamageComputer.ToFloat(add.WeaponDamageF32):F4} preQueryWire={add.DamageWire} hp={addOldHPWire}->{addNewHPWire}/{monster.MaxHPWire} died={addDied} effectRaw=0x{addEffectRaw:X8} nativeDamageTime={nativeDamageTime:F3} nativeTick={nativeDamageTick}");
                }
            }

            if (damageResult.AttackerState != null && primaryAppliedWire > 0)
                damageResult.AttackerState.ApplyNativeOnDamageCallback(primaryAppliedWire, nativeDamageTime, baseSource);

            Debug.LogError($"[NATIVE-WEAPON-DAMAGE-REPLAY] source={baseSource} target={monster.Name}#{monster.EntityId} primaryWire={damageResult.DamageWire} addCount={(damageResult.DamageAdds != null ? damageResult.DamageAdds.Count : 0)} totalRawWire={damageResult.TotalDamageWire} totalAppliedWire={totalAppliedWire} hp={oldHPWire}->{newHPWire}/{monster.MaxHPWire} result={damageResult.ResultName} died={died} effectRaw=0x{effectRaw:X8}");
            return true;
        }

        private bool ApplyNativeMonsterOnDamageCallback(Monster monster, uint appliedDamageWire, float nativeNow, string source)
        {
            if (monster == null || appliedDamageWire == 0 || monster.NativeSlots == null) return false;
            int hpSteal = monster.NativeSlots.Get(NativeUnitSlot.HitPointSteal);
            int manaSteal = monster.NativeSlots.Get(NativeUnitSlot.ManaPointSteal);
            if (hpSteal <= 0 && manaSteal <= 0) return false;

            uint oldHP = PeekRuntimeMonsterHPWire(monster);
            uint oldMana = monster.CurrentManaWire;
            uint nextHP = oldHP;
            uint nextMana = oldMana;

            if (hpSteal > 0 && oldHP > 0 && monster.MaxHPWire > 0)
            {
                ulong heal = ((ulong)appliedDamageWire * (uint)hpSteal) / 100UL;
                if (heal > 0)
                {
                    ulong hp = (ulong)oldHP + heal;
                    nextHP = hp >= monster.MaxHPWire ? monster.MaxHPWire : (uint)hp;
                    if (nextHP != oldHP)
                        SetRuntimeMonsterHPWire(monster, nextHP, false, nativeNow, $"{source ?? "Damage::apply"}:Unit::onDamageCallback");
                }
            }

            if (manaSteal > 0 && monster.MaxManaWire > 0)
            {
                ulong restore = ((ulong)appliedDamageWire * (uint)manaSteal) / 100UL;
                if (restore > 0)
                {
                    ulong mana = (ulong)oldMana + restore;
                    nextMana = mana >= monster.MaxManaWire ? monster.MaxManaWire : (uint)mana;
                    monster.CurrentManaWire = nextMana;
                }
            }

            bool changed = oldHP != nextHP || oldMana != nextMana;
            if (changed)
                Debug.LogError($"[DAMAGE-CALLBACK] source=monster monster={monster.Name}#{monster.EntityId} hpSteal={hpSteal} manaSteal={manaSteal} appliedWire={appliedDamageWire} hp={oldHP}->{nextHP}/{monster.MaxHPWire} mana={oldMana}->{nextMana}/{monster.MaxManaWire} native=Unit::onDamageCallback@0x0050C470");
            return changed;
        }

        private static uint ClampWireAdd(uint left, uint right)
        {
            ulong sum = (ulong)left + right;
            return sum >= uint.MaxValue ? uint.MaxValue : (uint)sum;
        }

        public bool ObserveClientMonsterHP(Monster monster, uint clientHPWire, string source)
        {
            if (monster == null) return false;
            const uint toleranceWire = 5u * 256u;
            if (clientHPWire > monster.MaxHPWire + toleranceWire)
            {
                Debug.LogError($"[{source}] Monster HP rejected: {monster.Name}#{monster.EntityId} client={clientHPWire / 256f:F2} max={monster.MaxHPWire / 256f:F2}");
                return false;
            }

            var state = GetMonsterHPAuthority(monster);
            if (state == null) return false;
            uint observedHPWire = clientHPWire > monster.MaxHPWire ? monster.MaxHPWire : clientHPWire;
            if (HpSyncService.Instance.TryResolveMonsterOwner(monster, out HpOwnerRef owner))
                HpSyncService.Instance.ObserveClientHpReport(owner, observedHPWire, HpSyncService.ClassifyReportSource(source), source ?? "client-monster-hp", true, out _);
            RecordMonsterHPReport(monster, state, observedHPWire);
            CommitClientReportedMonsterHP(monster, state, observedHPWire, source);
            Debug.LogError($"[{source}] Monster HP client-authoritative: {monster.Name}#{monster.EntityId} client={observedHPWire / 256f:F2} runtime={state.RuntimeHPWire / 256f:F2}/{monster.MaxHPWire / 256f:F2} wire={observedHPWire}");
            return true;
        }

        public bool CanSendMonsterSynchronizedHP(Monster monster, string packetName)
        {
            if (monster == null) return false;
            return TryResolveMonsterSynchronizedHP(monster, packetName, out _);
        }

        private bool AggroMonster(Monster monster, CombatPlayer player, string reason, bool alignForCombat)
        {
            if (monster == null || player == null || !monster.IsAlive || monster.DeathPendingClientConfirmation) return false;
            bool firstAggro = !monster.AggroTriggered || monster.TargetId != player.EntityId;
            monster.AggroTriggered = true;
            monster.TargetId = player.EntityId;
            monster.AlertSourceEntityId = 0;
            monster.State = MonsterState.Combat;
            monster.NativeAi?.PostMessage(NativeMonsterMessageId.TargetChanged, reason);
            monster.NativeAi?.SetState(NativeMonsterStateId.Attack, "aggro-target");
            NativeEncounterMarkActive(monster, "aggro-target");
            if (alignForCombat) AlignMonsterForClientCombat(monster, player);
            if (firstAggro)
            {
                WanderSimulator.Instance.UnregisterEntity(monster.EntityId);
                monster.AttackPending = false;
                monster.AttackSoundPending = false;
                Debug.LogError($"[SERVER-AGGRO] {monster.Name} -> {player.Name} reason={reason}");
                OnMonsterAggro?.Invoke(monster, player);
                PropagateMonsterShout(monster, player);
            }
            TraceMonsterState(monster, "aggro", player, Distance2D(monster.PosX, monster.PosY, player.PosX, player.PosY), ResolveMonsterEffectiveAttackRange(monster), reason);
            return firstAggro;
        }

        private void PropagateMonsterShout(Monster source, CombatPlayer player)
        {
            if (source == null || player == null || source.ShoutRange <= 0f) return;
            float shoutSq = source.ShoutRange * source.ShoutRange;
            string pathMapKey = ResolveMonsterPathMapKey(source);
            PathMap pathMap = !string.IsNullOrWhiteSpace(pathMapKey) ? PathMapManager.Instance.GetPathMap(pathMapKey) : null;
            foreach (var monster in _activeMonsters.Values)
            {
                if (monster == source || !monster.IsAlive || monster.AggroTriggered || monster.TargetId != 0) continue;
                if (!string.Equals(monster.ZoneName, source.ZoneName, StringComparison.OrdinalIgnoreCase)) continue;

                float dx = monster.PosX - source.PosX;
                float dy = monster.PosY - source.PosY;
                float distSq = dx * dx + dy * dy;
                if (distSq > shoutSq) continue;
                if (pathMap != null
                    && pathMap.TryCanReachPoint(source.PosX, source.PosY, monster.PosX, monster.PosY, out bool canAssistReach)
                    && !canAssistReach) continue;

                if (!HasNativeAlertEncounterRelation(monster, source, out string relation))
                {
                    Debug.LogError($"[SERVER-SHOUT] source={source.EntityId} target={monster.EntityId} shout={source.ShoutRange:F1} dist={Mathf.Sqrt(distSq):F1} action=ignored relation={relation} sourceGroup={source.EncounterGroupKey ?? ""} targetGroup={monster.EncounterGroupKey ?? ""}");
                    continue;
                }

                monster.AlertSourceEntityId = source.EntityId;
                monster.NativeAi?.PostMessage(NativeMonsterMessageId.AlertChanged, "shout-alert");
                Debug.LogError($"[SERVER-SHOUT] source={source.EntityId} target={monster.EntityId} shout={source.ShoutRange:F1} dist={Mathf.Sqrt(distSq):F1} action=alert-source relation={relation} sourceTarget={source.TargetId} sourceGroup={source.EncounterGroupKey ?? ""} targetGroup={monster.EncounterGroupKey ?? ""}");
                TraceMonsterState(monster, "alert", player, Mathf.Sqrt(distSq), ResolveMonsterEffectiveAttackRange(monster), $"source={source.EntityId} relation={relation}");
            }
        }

        private void ProcessMonsterAssistAlerts(Monster onlyMonster = null)
        {
            foreach (var monster in SelectMonsters(onlyMonster))
                TryAssistFromAlertSource(monster, "assist-update");
        }

        private bool TryAssistFromAlertSource(Monster monster, string source)
        {
            if (monster == null || !monster.IsAlive || monster.DeathPendingClientConfirmation)
                return false;
            if (monster.AlertSourceEntityId == 0 || monster.AggroTriggered || monster.TargetId != 0)
                return false;
            if (!_activeMonsters.TryGetValue(monster.AlertSourceEntityId, out var alertSource) || alertSource == null || !alertSource.IsAlive)
            {
                Debug.LogError($"[SERVER-ASSIST] target={monster.Name}#{monster.EntityId} source={monster.AlertSourceEntityId} action=clear reason=missing-alert-source");
                monster.AlertSourceEntityId = 0;
                return false;
            }
            if (!HasNativeAlertEncounterRelation(monster, alertSource, out string relation))
            {
                Debug.LogError($"[SERVER-ASSIST] source={alertSource.EntityId} target={monster.EntityId} action=clear relation={relation} sourceGroup={alertSource.EncounterGroupKey ?? ""} targetGroup={monster.EncounterGroupKey ?? ""}");
                monster.AlertSourceEntityId = 0;
                return false;
            }
            if (alertSource.TargetId == 0 || !_players.TryGetValue(alertSource.TargetId, out var target) || target == null || !target.IsAlive || target.PlayerState == null)
            {
                Debug.LogError($"[SERVER-ASSIST] source={alertSource.EntityId} target={monster.EntityId} action=clear relation={relation} reason=no-source-target sourceTarget={alertSource.TargetId}");
                monster.AlertSourceEntityId = 0;
                return false;
            }
            if (target.PlayerState.IsZoneSpawnDamageImmune || (target.PlayerState.CurrentHPWire == 0 && target.PlayerState.SynchHP == 0))
                return false;

            monster.AggroTriggered = true;
            monster.TargetId = target.EntityId;
            monster.State = MonsterState.Combat;
            monster.NativeAi?.SetState(NativeMonsterStateId.Assist, source);
            monster.NativeAi?.PostMessage(NativeMonsterMessageId.AlertChanged, source);
            NativeEncounterLogAssistNotActive(monster, source ?? "assist");
            WanderSimulator.Instance.UnregisterEntity(monster.EntityId);
            monster.AttackPending = false;
            monster.AttackSoundPending = false;
            Debug.LogError($"[SERVER-ASSIST] source={alertSource.Name}#{alertSource.EntityId} target={monster.Name}#{monster.EntityId} copiedTarget={target.Name}#{target.EntityId} relation={relation} source={source ?? "unknown"}");
            TraceMonsterState(monster, "assist", target, Distance2D(monster.PosX, monster.PosY, target.PosX, target.PosY), ResolveMonsterEffectiveAttackRange(monster), $"source={alertSource.EntityId} relation={relation}");
            return true;
        }

        private bool PlayerHasIncomingAttacker(uint playerEntityId, uint exceptEntityId = 0)
        {
            foreach (var monster in _activeMonsters.Values)
            {
                if (monster == null || !monster.IsAlive || !monster.AggroTriggered) continue;
                if (exceptEntityId != 0 && monster.EntityId == exceptEntityId) continue;
                if (monster.TargetId == playerEntityId) return true;
            }
            return false;
        }

        private void AlignMonsterForClientCombat(Monster monster, CombatPlayer player)
        {
            float allowedRange = ResolveMonsterEffectiveAttackRange(monster);
            if (allowedRange <= 0f) return;

            float dx = monster.PosX - player.PosX;
            float dy = monster.PosY - player.PosY;
            float dist = Mathf.Sqrt(dx * dx + dy * dy);
            if (dist <= allowedRange && dist > 0.001f) return;

            if (dist <= 0.001f)
            {
                float headingRad = monster.Heading * Mathf.Deg2Rad;
                dx = Mathf.Cos(headingRad);
                dy = Mathf.Sin(headingRad);
                dist = Mathf.Sqrt(dx * dx + dy * dy);
            }

            if (dist <= 0.001f)
            {
                dx = 1f;
                dy = 0f;
                dist = 1f;
            }

            float contactRange = Mathf.Max(1f, allowedRange - 1f);
            monster.PosX = player.PosX + dx / dist * contactRange;
            monster.PosY = player.PosY + dy / dist * contactRange;
        }
        public IEnumerable<Monster> GetAllMonsters()
        {
            return _activeMonsters.Values;
        }
        public void UnregisterPlayer(uint entityId)
        {
            _players.Remove(entityId);
            UnregisterNativeEntityOrder(entityId);
            _playerCombatAdvanceTime.Remove(entityId);
            int clearedMonsters = 0;
            foreach (var monster in _activeMonsters.Values)
            {
                if (monster == null) continue;
                if (ClearMonsterTargetStateForLostPlayer(monster, entityId, "UnregisterPlayer"))
                    clearedMonsters++;
            }
            Debug.LogError($"[COMBAT-LIFECYCLE] unregistered player {entityId} and cleared monster targeting state count={clearedMonsters}");
        }

        private bool ClearMonsterTargetStateForLostPlayer(Monster monster, uint lostPlayerEntityId, string source)
        {
            if (monster == null || !monster.IsAlive || monster.State == MonsterState.Dead || monster.DeathPendingClientConfirmation)
                return false;

            bool touchesLostPlayer = lostPlayerEntityId != 0 &&
                (monster.TargetId == lostPlayerEntityId ||
                 monster.CombatContactTargetId == lostPlayerEntityId ||
                 monster.AttackSearchTargetId == lostPlayerEntityId);
            bool hasRuntimeTargetState =
                monster.TargetId != 0 ||
                monster.CombatContactTargetId != 0 ||
                monster.AttackSearchTargetId != 0 ||
                monster.AlertSourceEntityId != 0 ||
                monster.AggroTriggered ||
                monster.AggroSent ||
                monster.AttackPending ||
                monster.AttackSoundPending ||
                monster.AttackClientVisible ||
                monster.AttackNativeContactOnly ||
                monster.AttackHitResolved ||
                monster.UsePrimaryActiveSkillThisAttack ||
                monster.State == MonsterState.Chase ||
                monster.State == MonsterState.Combat ||
                monster.State == MonsterState.Attacking;
            bool targetMissing = monster.TargetId == 0 || !_players.ContainsKey(monster.TargetId);
            if (!hasRuntimeTargetState || (!touchesLostPlayer && !targetMissing))
                return false;

            MonsterState oldState = monster.State;
            uint oldTarget = monster.TargetId;
            uint oldContact = monster.CombatContactTargetId;
            bool oldAggro = monster.AggroTriggered;
            bool oldPending = monster.AttackPending;

            monster.ClearTarget();
            monster.AggroTriggered = false;
            monster.AggroSent = false;
            monster.AttackPending = false;
            monster.AttackSoundPending = false;
            monster.AttackClientVisible = false;
            monster.AttackNativeContactOnly = false;
            monster.AttackHitResolved = false;
            monster.UsePrimaryActiveSkillThisAttack = false;
            monster.AttackStartedTime = 0f;
            monster.AttackCommitTime = 0f;
            monster.AttackSoundTime = 0f;
            monster.AttackEndTime = 0f;
            monster.AttackUseRaw = 0;
            monster.AttackSoundRaw = 0;
            monster.AttackSoundGateRaw = 0;
            monster.AttackSoundRepeatRaw = 0;
            monster.AttackCommitTargetX = 0f;
            monster.AttackCommitTargetY = 0f;
            monster.AttackSearchTargetId = 0;
            monster.AttackSearchRaw = 0;
            monster.AttackSearchTieRaw = 0;
            monster.CombatContactTargetId = 0;
            monster.CombatContactUntil = 0f;
            if (monster.IsAlive)
                monster.State = MonsterState.Idle;
            _monsterFarTargetActionLogTime.Remove(monster.EntityId);

            Debug.LogError($"[MON-TARGET-CLEAR] monster={monster.Name}#{monster.EntityId} lostPlayer={lostPlayerEntityId} oldState={oldState} oldTarget={oldTarget} oldContact={oldContact} oldAggro={oldAggro} oldPending={oldPending} hp={GetRuntimeMonsterHPWire(monster, "target-clear")}/{monster.MaxHPWire} pos=({monster.PosX:F1},{monster.PosY:F1},{monster.PosZ:F1}) source={source ?? "unknown"} native=MonsterBehavior2::ClearTargets/UpdateTargets");
            TraceMonsterState(monster, "target-clear", null, -1f, ResolveMonsterEffectiveAttackRange(monster), source);
            return true;
        }
        private NativeRoomRuntime CurrentRoomRuntime => GetRoomRuntime(_currentRoomRuntimeKey);

        public MersenneTwister RoomRng => CurrentRoomRuntime.RoomRng;

        public uint RoomSeed => CurrentRoomRuntime.Seed;

        public bool IsRoomRngReady => CurrentRoomRuntime.Initialized;

        public int RoomRngCallsSinceReseed => CurrentRoomRuntime.RngCallsSinceReseed;

        public MersenneTwister SyncedRandom => CurrentRoomRuntime.RoomRng;
        public uint RandomSeed => CurrentRoomRuntime.Seed;

        public string CurrentRoomRuntimeKey => _currentRoomRuntimeKey;

        public NativeRoomRuntime GetRoomRuntime(string instanceKey)
        {
            string key = NativeRoomRuntime.NormalizeInstanceKey(instanceKey);
            if (!_roomRuntimes.TryGetValue(key, out var runtime))
            {
                runtime = new NativeRoomRuntime(key);
                _roomRuntimes[key] = runtime;
            }
            return runtime;
        }

        public bool TryGetRoomRuntime(string instanceKey, out NativeRoomRuntime runtime)
        {
            string key = NativeRoomRuntime.NormalizeInstanceKey(instanceKey);
            return _roomRuntimes.TryGetValue(key, out runtime);
        }

        public bool TryGetInitializedRoomRuntime(string instanceKey, out NativeRoomRuntime runtime)
        {
            runtime = null;
            if (string.IsNullOrWhiteSpace(instanceKey))
            {
                RuntimeEvidenceManager.LogFallbackHit("rng-instance", "missing-instance-key", "source=TryGetInitializedRoomRuntime", 64);
                Debug.LogError("[RNG-INSTANCE] source=TryGetInitializedRoomRuntime reason=missing-instance-key rng=null");
                return false;
            }

            string key = NativeRoomRuntime.NormalizeInstanceKey(instanceKey);
            if (string.Equals(key, NativeRoomRuntime.DefaultInstanceKey, StringComparison.OrdinalIgnoreCase))
            {
                RuntimeEvidenceManager.LogFallbackHit("rng-instance", "default-instance-key", $"source=TryGetInitializedRoomRuntime instance='{key}'", 64);
                Debug.LogError($"[RNG-INSTANCE] source=TryGetInitializedRoomRuntime reason=default-instance-key instance='{key}' rng=null");
                return false;
            }

            if (!_roomRuntimes.TryGetValue(key, out runtime) || runtime == null || !runtime.Initialized)
            {
                RuntimeEvidenceManager.LogFallbackHit("rng-instance", "uninitialized-instance", $"source=TryGetInitializedRoomRuntime instance='{key}'", 64);
                Debug.LogError($"[RNG-INSTANCE] source=TryGetInitializedRoomRuntime reason=uninitialized-instance instance='{key}' rng=null");
                runtime = null;
                return false;
            }

            return true;
        }

        public NativeRoomRuntime RequireInitializedRoomRuntime(string instanceKey, string source)
        {
            if (TryGetInitializedRoomRuntime(instanceKey, out var runtime))
                return runtime;

            Debug.LogError($"[RNG-INSTANCE] source={source ?? "unknown"} reason=required-runtime-unavailable instance='{instanceKey ?? "<null>"}'");
            return null;
        }

        public void SetCurrentRoomRuntime(string instanceKey, string source = null)
        {
            _currentRoomRuntimeKey = NativeRoomRuntime.NormalizeInstanceKey(instanceKey);
            GetRoomRuntime(_currentRoomRuntimeKey);
            Debug.LogError($"[ROOM-RUNTIME] current='{_currentRoomRuntimeKey}' source={source ?? "unknown"}");
        }

        private string ResolveMonsterRuntimeKey(Monster monster)
        {
            if (!string.IsNullOrWhiteSpace(monster?.InstanceKey))
                return monster.InstanceKey;
            RuntimeEvidenceManager.LogFallbackHit(
                "rng-instance",
                "monster-missing-instance",
                $"monster={monster?.Name ?? "<null>"}#{monster?.EntityId ?? 0}",
                64);
            return null;
        }

        public NativeRoomRuntime GetRoomRuntimeForMonster(Monster monster)
        {
            return RequireInitializedRoomRuntime(ResolveMonsterRuntimeKey(monster), "monster-runtime");
        }

        public NativeCombatContext GetNativeCombatContextForMonster(Monster monster, string source = null)
        {
            NativeRoomRuntime runtime = GetRoomRuntimeForMonster(monster);
            return runtime != null ? runtime.CreateContext(source) : new NativeCombatContext(null, source);
        }

        public MersenneTwister GetRoomRngForMonster(Monster monster)
        {
            return GetRoomRuntimeForMonster(monster)?.RoomRng;
        }

        public MersenneTwister GetRoomRngForInstance(string instanceKey)
        {
            return RequireInitializedRoomRuntime(instanceKey, "instance-rng")?.RoomRng;
        }

        public string GetPlayerInstanceKey(uint playerEntityId)
        {
            return _players.TryGetValue(playerEntityId, out var player) && !string.IsNullOrWhiteSpace(player?.InstanceKey)
                ? player.InstanceKey
                : null;
        }

        public MersenneTwister GetRoomRngForPlayerEntity(uint playerEntityId)
        {
            string instanceKey = GetPlayerInstanceKey(playerEntityId);
            if (string.IsNullOrWhiteSpace(instanceKey) || instanceKey == NativeRoomRuntime.DefaultInstanceKey)
            {
                RuntimeEvidenceManager.LogFallbackHit(
                    "rng-instance",
                    "player-missing-instance",
                    $"player={playerEntityId}",
                    64);
                Debug.LogError($"[RNG-INSTANCE] source=GetRoomRngForPlayerEntity reason=player-missing-instance player={playerEntityId} rng=null");
                return null;
            }
            return GetRoomRngForInstance(instanceKey);
        }

        public uint GetRoomSeedForInstance(string instanceKey)
        {
            return TryGetInitializedRoomRuntime(instanceKey, out var runtime) ? runtime.Seed : 0u;
        }

        public int GetRoomRngPosForInstance(string instanceKey)
        {
            return TryGetInitializedRoomRuntime(instanceKey, out var runtime) ? runtime.RngCallsSinceReseed : -1;
        }

        public void InitializeRoomRng(uint seed)
        {
            InitializeRoomRng(_currentRoomRuntimeKey, seed, "legacy-current");
        }

        public void InitializeRoomRng(string instanceKey, uint seed, string source = null)
        {
            string key = NativeRoomRuntime.NormalizeInstanceKey(instanceKey);
            _currentRoomRuntimeKey = key;
            NativeRoomRuntime runtime = GetRoomRuntime(key);
            runtime.Initialize(seed, source ?? "InitializeRoomRng");
            Debug.LogError($"[RNG-SEED] room initialize instance='{key}' seed=0x{seed:X8} rngPos=0 monsters={_activeMonsters.Count(m => string.Equals(ResolveMonsterRuntimeKey(m.Value), key, StringComparison.OrdinalIgnoreCase))} players={_players.Count}");
        }

        public void EnsureRoomRng(string instanceKey, uint seed, string source = null)
        {
            string key = NativeRoomRuntime.NormalizeInstanceKey(instanceKey);
            _currentRoomRuntimeKey = key;
            NativeRoomRuntime runtime = GetRoomRuntime(key);
            bool initialized = runtime.EnsureInitialized(seed, source ?? "EnsureRoomRng");
            Debug.LogError($"[RNG-SEED] ensure instance='{key}' seed=0x{seed:X8} initialized={initialized} current=0x{runtime.Seed:X8} rngPos={runtime.RngCallsSinceReseed} source={source ?? "unknown"}");
        }

        public void AdvanceRoomRng(int count, string source)
        {
            CurrentRoomRuntime.Advance(count, source);
        }

        public void AdvanceRoomRng(string instanceKey, int count, string source)
        {
            GetRoomRuntime(instanceKey).Advance(count, source);
        }

        public void ReseedRoomRng(uint seed)
        {
            ReseedRoomRng(_currentRoomRuntimeKey, seed, "legacy-current");
        }

        public void ReseedRoomRng(string instanceKey, uint seed, string source = null)
        {
            string key = NativeRoomRuntime.NormalizeInstanceKey(instanceKey);
            _currentRoomRuntimeKey = key;
            GetRoomRuntime(key).Reseed(seed, source ?? "ReseedRoomRng");
        }
        public void InitializeRandomSeed(uint seed)
        {
            NativeRoomRuntime runtime = CurrentRoomRuntime;
            if (runtime.Initialized)
            {
                if (runtime.Seed != seed)
                    Debug.LogError($"[ROOM-RNG] Ignored legacy reseed request instance='{runtime.InstanceKey}' seed=0x{seed:X8} current=0x{runtime.Seed:X8} rngPos={runtime.RngCallsSinceReseed}");
                return;
            }
            InitializeRoomRng(seed);
        }
        public event Action<Monster, CombatPlayer> OnMonsterAggro;
        public CombatPlayer GetPlayer(uint entityId)
        {
            return _players.TryGetValue(entityId, out var p) ? p : null;
        }

        public void SetPlayerActiveClientAttack(uint entityId, bool active, uint targetId = 0)
        {
            if (_players.TryGetValue(entityId, out var player) && player != null)
            {
                player.HasActiveClientAttack = active;
                player.ActiveClientAttackTargetId = active ? targetId : 0;
            }
        }

        public void FlushPlayerCombatBeforeSync(uint playerEntityId, float deltaTime, string source = null, float nativeNowOverride = -1f)
        {
            if (playerEntityId == 0) return;
            float nativeNow = nativeNowOverride >= 0f ? nativeNowOverride : GetNativeCombatTime();
            float nativeDelta = 0f;
            float elapsed = 0f;
            int dueTicks = 0;
            int consumedTicks = 0;
            float previousAdvanceTime = nativeNow;
            float nextAdvanceTime = nativeNow;
            if (deltaTime > 0f)
            {
                nativeDelta = ResolvePlayerCombatAdvanceDelta(playerEntityId, deltaTime, out elapsed, out dueTicks, out consumedTicks, out previousAdvanceTime, out nextAdvanceTime);
            }
            else if (!_playerCombatAdvanceTime.ContainsKey(playerEntityId))
            {
                _playerCombatAdvanceTime[playerEntityId] = nativeNow;
            }
            TracePlayerPreSuffixCombatAdvance(playerEntityId, source ?? "FlushPlayerCombatBeforeSync", deltaTime, nativeDelta, elapsed, dueTicks, consumedTicks, previousAdvanceTime, nextAdvanceTime, nativeNow);
            AdvancePlayerDamageModifierRuntime(playerEntityId, nativeNow, source ?? "FlushPlayerCombatBeforeSync");
            AdvanceMonsterModifierRuntime(null, nativeNow, source ?? "FlushPlayerCombatBeforeSync");
            ProcessMonsterAttacks(0f, playerEntityId, false, null, nativeNow);
        }

        private float ResolvePlayerCombatAdvanceDelta(uint playerEntityId, float deltaTime, out float elapsed, out int dueTicks, out int consumedTicks, out float previousAdvanceTime, out float nextAdvanceTime)
        {
            float now = GetNativeCombatTime();
            elapsed = 0f;
            dueTicks = 0;
            consumedTicks = 0;
            previousAdvanceTime = now;
            nextAdvanceTime = now;

            if (deltaTime > 0f)
            {
                elapsed = deltaTime;
                dueTicks = Mathf.Max(1, Mathf.CeilToInt(deltaTime / NATIVE_UNIT_TICK_INTERVAL));
                consumedTicks = dueTicks;
                _playerCombatAdvanceTime[playerEntityId] = now;
                return deltaTime;
            }

            if (!_playerCombatAdvanceTime.TryGetValue(playerEntityId, out previousAdvanceTime) || previousAdvanceTime <= 0f || previousAdvanceTime > now)
            {
                previousAdvanceTime = now;
                _playerCombatAdvanceTime[playerEntityId] = previousAdvanceTime;
                nextAdvanceTime = previousAdvanceTime;
                return 0f;
            }

            elapsed = Mathf.Max(0f, now - previousAdvanceTime);
            dueTicks = Mathf.FloorToInt((elapsed + 0.0001f) / NATIVE_UNIT_TICK_INTERVAL);
            if (dueTicks <= 0)
            {
                nextAdvanceTime = previousAdvanceTime;
                return 0f;
            }

            consumedTicks = dueTicks;
            float nativeDelta = consumedTicks * NATIVE_UNIT_TICK_INTERVAL;
            nextAdvanceTime = previousAdvanceTime + nativeDelta;
            if (nextAdvanceTime > now)
                nextAdvanceTime = now;
            _playerCombatAdvanceTime[playerEntityId] = nextAdvanceTime;
            return nativeDelta;
        }

        private void TracePlayerPreSuffixCombatAdvance(uint playerEntityId, string source, float requestedDelta, float nativeDelta, float elapsed, int dueTicks, int consumedTicks, float previousAdvanceTime, float nextAdvanceTime, float nativeNow)
        {
            if (!_players.TryGetValue(playerEntityId, out var player) || player == null)
                return;

            foreach (var monster in _activeMonsters.Values)
            {
                if (monster == null || !monster.AggroTriggered || !monster.IsAlive)
                    continue;
                bool targetsPlayer = monster.TargetId == playerEntityId;
                bool contactsPlayer = monster.CombatContactTargetId == playerEntityId && nativeNow <= monster.CombatContactUntil;
                if (!targetsPlayer && !contactsPlayer)
                    continue;

                float dist = Distance2D(monster.PosX, monster.PosY, player.PosX, player.PosY);
                float allowedRange = ResolveMonsterEffectiveAttackRange(monster);
                string action = nativeDelta > 0f ? "advance" : "due-drain";
                Debug.LogError($"[PRE-SUFFIX-DUE-DRAIN] action={action} source={source ?? "unknown"} player={player.Name}#{player.EntityId} monster={monster.Name}#{monster.EntityId} behavior={monster.BehaviorId} unit={monster.UnitId} gc='{monster.GCType}' spawnGc='{monster.SpawnGCType}' state={monster.State} target={monster.TargetId} pending={monster.AttackPending} hitResolved={monster.AttackHitResolved} clientVisible={monster.AttackClientVisible} nativeContact={monster.AttackNativeContactOnly} contactTarget={monster.CombatContactTargetId} dist={dist:F1} range={allowedRange:F1} requestedDelta={requestedDelta:F3} nativeDelta={nativeDelta:F3} elapsed={elapsed:F3} dueTicks={dueTicks} consumedTicks={consumedTicks} clock={previousAdvanceTime:F3}->{nextAdvanceTime:F3} nativeNow={nativeNow:F3} lastAttack={monster.LastAttackTime:F3} commit={monster.AttackCommitTime:F3} end={monster.AttackEndTime:F3} pathGate=native");
            }
        }

        public IEnumerable<CombatPlayer> GetAllPlayers()
        {
            return _players.Values;
        }
        private static Dictionary<string, string> _zoneBehaviors;

        private void LoadZoneBehaviors()
        {
            _zoneBehaviors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // Read from SQLite zone_behaviors table
                using (var conn = DungeonRunners.Database.GameDatabase.GetConnection())
                using (var reader = DungeonRunners.Database.GameDatabase.ExecuteReader(conn,
                    "SELECT zone_name, behavior_mode FROM zone_behaviors WHERE enabled = 1"))
                {
                    while (reader.Read())
                    {
                        string zone = reader.GetString(0);
                        string mode = reader.GetString(1);
                        _zoneBehaviors[zone] = mode;
                    }
                }
                Debug.LogError($"[CombatManager] Loaded {_zoneBehaviors.Count} zone behaviors from SQLite");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[CombatManager] Failed to load zone behaviors from SQLite: {ex.Message}");
            }
        }

        public string GetBehaviorMode(string zoneName)
        {
            return "dungeon_specific";
        }

        private string GetBehaviorForZone(string zoneName)
        {
            string mode = GetBehaviorMode(zoneName);
            if (mode == "guard")
            {
                Debug.LogError($"[BEHAVIOR] Zone '{zoneName}' → GUARD → using world.dungeon09.mob.base.oneoff_behavior");
                return "world.dungeon09.mob.base.oneoff_behavior";
            }
            Debug.LogError($"[BEHAVIOR] Zone '{zoneName}' → '{mode}' → using authored creature behavior");
            return null;  // wander and dungeon_specific use creature's own type
        }

        private string ResolveSpawnBehaviourType(string zoneBehaviourType, string spawnGcType, string baseGcType)
        {
            if (!string.IsNullOrEmpty(zoneBehaviourType))
                return zoneBehaviourType;
            return ResolveAuthoredChildPath(spawnGcType, "Behavior") ?? ResolveAuthoredChildPath(baseGcType, "Behavior");
        }

        public Monster SpawnMonster(string gcType, float posX, float posY, float posZ, float heading = 0f, string zoneName = null, string encounterGroupKey = null, float encounterDifficulty = 1f, string spawnGcTypeOverride = null, string instanceKey = null)
        {
            var creatureData = DatabaseLoader.FindCreature(gcType);
            if (creatureData == null)
            {
                Debug.LogError($"[Combat] CREATURE NOT FOUND: '{gcType}'");
                return null;
            }

            string spawnGcType = !string.IsNullOrEmpty(spawnGcTypeOverride)
                ? spawnGcTypeOverride
                : ResolveDungeonCreaturePath(zoneName, creatureData.gcType);
            string zoneBehaviourType = GetBehaviorForZone(zoneName);
            GCNode authoredCreature = ResolveAuthoredCreatureNode(spawnGcType, creatureData.gcType);
            string spawnBehaviourType = ResolveSpawnBehaviourType(zoneBehaviourType, spawnGcType, creatureData.gcType);
            GCNode authoredDesc = authoredCreature?.GetChild("Description") ?? authoredCreature;
            GCNode authoredWeapon = GetAuthoredWeaponDescription(authoredCreature, spawnGcType, creatureData.gcType);
            GCNode authoredBehavior = ResolveAuthoredBehaviorNode(spawnBehaviourType);
            var spawnManipulators = BuildSpawnManipulators(spawnGcType, creatureData.gcType, authoredCreature, creatureData.manipulators);
            string creatureDifficulty = GetAuthoredString(authoredDesc, "CreatureDifficulty", creatureData.creatureDifficulty);
            float unitDifficulty = GetAuthoredFloat(authoredDesc, "Difficulty", MonsterHealthTable.GetDifficultyModifier(creatureDifficulty));
            float maxHealth = GetAuthoredFloat(authoredDesc, "MaxHealth", creatureData.maxHealth);
            float perceptionRange = GetAuthoredBehaviorFloat(authoredBehavior, authoredCreature, "Perception", NATIVE_DEFAULT_UNIT_PERCEPTION);
            float aggroFallback = NATIVE_DEFAULT_MONSTER_AGGRO_RANGE;
            float attackCooldown = GetAuthoredFloat(authoredWeapon, "CoolDown", 1.75f);
            bool weaponUsesProjectile = GetAuthoredBool(authoredWeapon, "UseProjectile", false);
            float weaponProjectileSpeed = Mathf.Max(0f, GetAuthoredFloat(authoredWeapon, "ProjectileSpeed", 0f));
            float weaponProjectileSize = Mathf.Max(0f, GetAuthoredFloat(authoredWeapon, "ProjectileSize", 0f));
            float weaponDescRange = Mathf.Max(0f, GetAuthoredFloat(authoredWeapon, "Range", 0f));
            NativeAttackTiming attackTiming = ResolveNativeAttackTiming(authoredDesc, weaponUsesProjectile, attackCooldown, spawnGcType ?? creatureData.gcType);
            string attackType = GetAuthoredBehaviorString(authoredBehavior, authoredCreature, "AttackType", "0");
            string idleAction = GetAuthoredBehaviorString(authoredBehavior, authoredCreature, "IdleAction", "3");
            string logicType = GetAuthoredBehaviorString(authoredBehavior, authoredCreature, "LogicType", "0");
            string attackStyle = GetAuthoredBehaviorString(authoredBehavior, authoredCreature, "AttackStyle", "0");
            bool retreatable = GetAuthoredBehaviorBool(authoredBehavior, authoredCreature, "Retreatable", NATIVE_DEFAULT_MONSTER_RETREATABLE);
            bool leashed = GetAuthoredBehaviorBool(authoredBehavior, authoredCreature, "Leashed", NATIVE_DEFAULT_MONSTER_LEASHED);
            bool useIdleTime = GetAuthoredBehaviorBool(authoredBehavior, authoredCreature, "UseIdleTime", NATIVE_DEFAULT_MONSTER_USE_IDLE_TIME);
            bool autoScan = GetAuthoredBehaviorBool(authoredBehavior, authoredCreature, "AutoScan", NATIVE_DEFAULT_UNIT_AUTO_SCAN);
            bool avoidUnits = GetAuthoredBehaviorBool(authoredBehavior, authoredCreature, "AvoidUnits", NATIVE_DEFAULT_UNIT_AVOID_UNITS);
            bool turnBeforeMoving = GetAuthoredBehaviorBool(authoredBehavior, authoredCreature, "TurnBeforeMoving", NATIVE_DEFAULT_UNIT_TURN_BEFORE_MOVING);
            bool playerControlled = GetAuthoredBehaviorBool(authoredBehavior, authoredCreature, "PlayerControlled", NATIVE_DEFAULT_UNIT_PLAYER_CONTROLLED);
            int collisionBand = GetAuthoredBehaviorInt(authoredBehavior, authoredCreature, "CollisionBand", NATIVE_DEFAULT_UNIT_COLLISION_BAND);
            int collisionPriority = GetAuthoredBehaviorInt(authoredBehavior, authoredCreature, "CollisionPriority", NATIVE_DEFAULT_UNIT_COLLISION_PRIORITY);
            float scanFrequency = GetAuthoredBehaviorFloat(authoredBehavior, authoredCreature, "ScanFrequency", NATIVE_DEFAULT_UNIT_SCAN_FREQUENCY);
            float fleeRange = GetAuthoredBehaviorFloat(authoredBehavior, authoredCreature, "FleeRange", NATIVE_DEFAULT_UNIT_FLEE_RANGE);
            float retreatRangeSquared = GetAuthoredBehaviorFloat(authoredBehavior, authoredCreature, "RetreatRangeSquared", NATIVE_DEFAULT_MONSTER_RETREAT_RANGE_SQUARED);
            float teleportFrequency = GetAuthoredBehaviorFloat(authoredBehavior, authoredCreature, "TeleportFrequency", NATIVE_DEFAULT_MONSTER_TELEPORT_FREQUENCY);
            float teleportLimboTime = GetAuthoredBehaviorFloat(authoredBehavior, authoredCreature, "TeleportLimboTime", NATIVE_DEFAULT_MONSTER_TELEPORT_LIMBO_TIME);
            float baseTime = GetAuthoredBehaviorFloat(authoredBehavior, authoredCreature, "BaseTime", NATIVE_DEFAULT_MONSTER_BASE_TIME);
            float variableTime = GetAuthoredBehaviorFloat(authoredBehavior, authoredCreature, "VariableTime", NATIVE_DEFAULT_MONSTER_VARIABLE_TIME);
            float leashRange = GetAuthoredBehaviorFloat(authoredBehavior, authoredCreature, "LeashRange", NATIVE_DEFAULT_MONSTER_LEASH_RANGE);

            string runtimeInstanceKey = !string.IsNullOrWhiteSpace(instanceKey) ? instanceKey : zoneName;
            NativeRoomRuntime spawnRuntime = RequireInitializedRoomRuntime(runtimeInstanceKey, "SpawnMonster");
            if (spawnRuntime == null)
            {
                Debug.LogError($"[ROOM-RNG] Blocked monster spawn before native seed gcType='{gcType}' zone='{zoneName}' instance='{runtimeInstanceKey ?? "<null>"}'");
                return null;
            }
            if (!spawnRuntime.Initialized)
            {
                Debug.LogError($"[ROOM-RNG] Blocked monster spawn before native seed gcType='{gcType}' zone='{zoneName}' instance='{spawnRuntime.InstanceKey}'");
                return null;
            }
            SetCurrentRoomRuntime(spawnRuntime.InstanceKey, "SpawnMonster");

            // Calculate level from tier + zone base
            byte tierLevel = GetLevelForTier(creatureDifficulty);
            byte zoneBase = GetZoneBaseLevel(zoneName);
            byte calculatedLevel = (byte)Math.Min(110, tierLevel + zoneBase);
            Debug.LogError($"[Combat] Level calc: tier={creatureDifficulty}({tierLevel}) + zone={zoneName}({zoneBase}) = {calculatedLevel}");

            uint entityId = _nextMonsterId++;
            uint behaviorId = _nextMonsterId++;
            uint skillsId = _nextMonsterId++;
            uint manipulatorsId = _nextMonsterId++;
            uint modifiersId = _nextMonsterId++;
            uint unitId = _nextMonsterId++;

            var monster = new Monster
            {
                EntityId = entityId,
                BehaviorId = behaviorId,
                SkillsId = skillsId,
                ManipulatorsId = manipulatorsId,
                ModifiersId = modifiersId,
                UnitId = unitId,

                GCType = creatureData.gcType,
                SpawnGCType = spawnGcType,
                BehaviourType = creatureData.behaviourType,
                Name = GetAuthoredString(authoredDesc, "Label", creatureData.name),
                Faction = creatureData.faction,

                CreatureType = creatureData.creatureType,
                Element = creatureData.element,
                Tier = creatureDifficulty,
                Level = calculatedLevel,
                Difficulty = unitDifficulty,
                ExperienceDifficulty = encounterDifficulty,

                MaxHPWire = MonsterHealthTable.CalculateHPWire(calculatedLevel, unitDifficulty, maxHealth),
                CurrentHPWire = MonsterHealthTable.CalculateHPWire(calculatedLevel, unitDifficulty, maxHealth),


                MaxManaWire = (uint)(creatureData.manaPoints * 256),
                CurrentManaWire = (uint)(creatureData.manaPoints * 256),
                BaseDamage = creatureData.baseDamage,
                AttackRating = GetAuthoredFloat(authoredDesc, "AttackRating", creatureData.AttackRatingF),
                DamageMod = GetAuthoredFloat(authoredDesc, "DamageMod", creatureData.DamageModF),
                DamageTakenMod = GetAuthoredFloat(authoredDesc, "DamageTakenMod", 100f),
                DamageImmunity = GetAuthoredFloat(authoredDesc, "DamageImmunity", 0f),
                DamageResist = GetAuthoredFloat(authoredDesc, "DamageResist", 0f),
                CrushingResist = GetAuthoredFloat(authoredDesc, "CrushingResist", 0f),
                PiercingResist = GetAuthoredFloat(authoredDesc, "PiercingResist", 0f),
                SlashingResist = GetAuthoredFloat(authoredDesc, "SlashingResist", 0f),
                DefenseRating = GetAuthoredFloat(authoredDesc, "DefenseRating", creatureData.DefenseRatingF),
                CritChance = GetAuthoredFloat(authoredDesc, "CriticalChance", creatureData.CritChanceF),
                DivineResist = GetAuthoredFloat(authoredDesc, "DivineResist", creatureData.DivineResistF),
                FireResist = GetAuthoredFloat(authoredDesc, "FireResist", creatureData.FireResistF),
                IceResist = GetAuthoredFloat(authoredDesc, "IceResist", creatureData.IceResistF),
                PoisonResist = GetAuthoredFloat(authoredDesc, "PoisonResist", creatureData.PoisonResistF),
                ShadowResist = GetAuthoredFloat(authoredDesc, "ShadowResist", creatureData.ShadowResistF),
                MagicDamageResist = GetAuthoredFloat(authoredDesc, "MagicResist", GetAuthoredFloat(authoredDesc, "MagicDamageResist", 0f)),
                HealthRegen = GetAuthoredFloat(authoredDesc, "HealthRegen", 0f),
                HasAuthoredHealthRegen = authoredDesc != null && authoredDesc.HasProperty("HealthRegen"),
                ManaRegen = GetAuthoredFloat(authoredDesc, "ManaRegen", GetAuthoredFloat(authoredDesc, "PowerRegen", 0f)),
                HasAuthoredManaRegen = authoredDesc != null && (authoredDesc.HasProperty("ManaRegen") || authoredDesc.HasProperty("PowerRegen")),
                DamageVolatility = GetAuthoredFloat(authoredWeapon, "DamageVolatility", 0.5f),
                WeaponDamage = GetAuthoredFloat(authoredWeapon, "Damage", 1.0f),
                WeaponClass = GetAuthoredString(authoredWeapon, "WeaponClass", "HTH"),
                WeaponDamageType = GetAuthoredString(authoredWeapon, "DamageType", "CRUSHING"),
                WeaponUsesProjectile = weaponUsesProjectile,
                WeaponProjectileSpeed = weaponProjectileSpeed,
                WeaponProjectileSize = weaponProjectileSize,
                WeaponRange = weaponDescRange,
                PosX = posX,
                PosY = posY,
                PosZ = posZ,
                SpawnPosX = posX,
                SpawnPosY = posY,
                SpawnPosZ = posZ,
                Heading = heading,

                PerceptionRange = perceptionRange,
                AggroRange = GetAuthoredBehaviorFloat(authoredBehavior, authoredCreature, "AgroRange", aggroFallback),
                ShoutRange = GetAuthoredBehaviorFloat(authoredBehavior, authoredCreature, "ShoutRange", NATIVE_DEFAULT_MONSTER_SHOUT_RANGE),
                LeashRange = leashRange,
                AttackRange = GetAuthoredAttackRange(authoredDesc, authoredWeapon, creatureData),
                ClientSyncTolerance = GetAuthoredFloat(authoredWeapon, "ClientSyncTolerance", 10f),
                CollisionRadius = GetAuthoredFloat(authoredDesc, "CollisionRadius", 5f),
                AttackType = attackType,
                IdleAction = idleAction,
                LogicType = logicType,
                AttackStyle = attackStyle,
                Retreatable = retreatable,
                Leashed = leashed,
                UseIdleTime = useIdleTime,
                AutoScan = autoScan,
                AvoidUnits = avoidUnits,
                TurnBeforeMoving = turnBeforeMoving,
                PlayerControlled = playerControlled,
                CollisionBand = collisionBand,
                CollisionPriority = collisionPriority,
                ScanFrequency = scanFrequency,
                FleeRange = fleeRange,
                RetreatRangeSquared = retreatRangeSquared,
                TeleportFrequency = teleportFrequency,
                TeleportLimboTime = teleportLimboTime,
                BaseTime = baseTime,
                VariableTime = variableTime,
                CorpseLingerTicks = (ushort)Mathf.Clamp(GetAuthoredInt(authoredDesc, "CorpseLingerTime", 900), 0, ushort.MaxValue),
                AutoRespawn = GetAuthoredBool(authoredCreature, "AutoRespawn", GetAuthoredBool(authoredDesc, "AutoRespawn", false)),
                RespawnRateTicks = (ushort)Mathf.Clamp(GetAuthoredInt(authoredCreature, "RespawnRate", GetAuthoredInt(authoredDesc, "RespawnRate", 3600)), 0, ushort.MaxValue),
                AttackSpeed = GetAuthoredFloat(authoredDesc, "AttackSpeed", 1f),
                AttackCooldown = attackCooldown,
                AttackLeadDelay = attackTiming.AttackLeadDelay,
                AttackSoundLeadDelay = attackTiming.AttackSoundLeadDelay,
                HasAttackSound = HasAuthoredAttackSound(authoredDesc),
                AttackWeaponSoundCount = CountAuthoredSounds(authoredDesc, "WEAPONATTACK"),
                AttackRepeatSoundCount = CountAuthoredSounds(authoredDesc, "ATTACK"),
                AttackTotalFrames = attackTiming.TotalFrames,
                AttackHitFrames = attackTiming.HitFrames,
                AttackSoundFrames = attackTiming.SoundFrames,
                AttackFrameResolved = attackTiming.VariantResolved,
                AttackTimingSource = attackTiming.Source,
                AttackTimingReason = attackTiming.Reason,
                MoveSpeed = GetAuthoredMoveSpeed(authoredDesc, creatureData),
                WalkSpeed = GetAuthoredWalkSpeed(authoredCreature, authoredDesc, creatureData),
                WanderRange = GetAuthoredWanderRange(authoredBehavior, authoredCreature),

                Manipulators = spawnManipulators,

                State = MonsterState.Idle,
                IsAlive = true,
                SpawnTime = Time.time,
                ZoneName = zoneName,
                InstanceKey = spawnRuntime.InstanceKey,
                AuthoredArchetypeAncestry = BuildAuthoredArchetypeAncestry(spawnGcType, creatureData.gcType),
                EncounterGroupKey = encounterGroupKey,
                SpawnBehaviourType = spawnBehaviourType
            };

            monster.NativeWeaponClassId = DamageComputer.ResolveNativeWeaponClassId(monster.WeaponClass);
            monster.NativeDamageTypeId = DamageComputer.ResolveNativeDamageTypeId(monster.WeaponDamageType);
            MaterializeMonsterNativeUnitSlots(monster);
            InitializeMonsterNativeAiRuntime(monster);
            ResetMonsterClientVisiblePosition(monster, monster.PosX, monster.PosY, GetNativeCombatTime(), "SPAWN");
            _activeMonsters[entityId] = monster;
            RegisterNativeEntityOrder(entityId);
            SetRuntimeMonsterHPWire(monster, monster.CurrentHPWire, false, Time.time, "SPAWN");
            monster.RngSeed = spawnRuntime.Seed;
            monster.Rng = spawnRuntime.RoomRng;
            ConfigureMonsterPrimaryActiveSkill(monster);
            ConsumeNativeState0MultiAttackDraw(monster, spawnRuntime.RoomRng);
            Debug.LogError($"[Combat] Monster {monster.Name} using room runtime '{monster.InstanceKey}' seed: 0x{spawnRuntime.Seed:X8}");

            _componentToEntityMap[entityId] = entityId;
            _componentToEntityMap[behaviorId] = entityId;
            _componentToEntityMap[skillsId] = entityId;
            _componentToEntityMap[manipulatorsId] = entityId;
            _componentToEntityMap[modifiersId] = entityId;
            _componentToEntityMap[unitId] = entityId;

            if (ShouldRegisterWander(idleAction, monster.WanderRange, zoneName))
            {
                string wanderLeashedEnv = Environment.GetEnvironmentVariable("DR_SERVER_WANDER_LEASHED");
                bool wanderLeashedOptIn = !string.IsNullOrWhiteSpace(wanderLeashedEnv)
                    && wanderLeashedEnv.Trim() != "0"
                    && !wanderLeashedEnv.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
                bool nativeWanderEncounterOwner = wanderLeashedOptIn && HasNativeWanderEncounterOwner(monster);
                Debug.LogError($"[WANDER-LEASH] {monster.Name}#{monster.EntityId} canWander(leashed)={nativeWanderEncounterOwner} optIn={wanderLeashedOptIn} hasEncounter={HasNativeWanderEncounterOwner(monster)} native=MonsterBehavior2::DoIdleAction@0x0051D8B8(this+0x188!=0)");
                WanderSimulator.Instance.RegisterMonster(monster, nativeWanderEncounterOwner);
            }

            int nativeAttackRating = DamageComputer.ResolveNativeMonsterAttackRating(monster);
            int nativeDefenseRating = DamageComputer.ResolveNativeMonsterDefenseRating(monster);
            float monsterDamageTable = ResolveMonsterDamageTable(monster.Level);
            float effectiveWeaponDamage = monster.WeaponDamage > 0f ? monster.WeaponDamage : 1f;
            float effectiveDamage = monsterDamageTable * ResolveMonsterDamageModifier(monster) * effectiveWeaponDamage;
            Debug.LogError($"[Combat] SPAWNED: {monster.Name} (ID:{entityId}) Level:{calculatedLevel} HP:{monster.MaxHP} DMG:{creatureData.baseDamage} UnitDifficulty={monster.Difficulty:F2} DamageMod={monster.DamageMod:F2} EncounterDifficulty={monster.ExperienceDifficulty:F2} EffectiveDamageMod={ResolveMonsterDamageModifier(monster):F2}");
            Debug.LogError($"[SPAWN-AUDIT] id={entityId} name='{monster.Name}' baseGc='{monster.GCType}' spawnGc='{monster.SpawnGCType}' zone='{zoneName}' group='{monster.EncounterGroupKey}' level={monster.Level} hpWire={monster.MaxHPWire} manaWire={monster.MaxManaWire} hpRegen={monster.HealthRegen:F3} manaRegen={monster.ManaRegen:F3} hpRegenFactor={ResolveMonsterHealthRegenFactor(monster)} manaRegenFactor={ResolveMonsterManaRegenFactor(monster)} unitDiff={monster.Difficulty:F2} encounterDiff={monster.ExperienceDifficulty:F2} attackRatingAuth={monster.AttackRating:F3} attackRatingNative={nativeAttackRating} defenseRatingAuth={monster.DefenseRating:F3} defenseRatingNative={nativeDefenseRating} crit={monster.CritChance:F2} damageTable={monsterDamageTable:F3} damageMod={monster.DamageMod:F3} weaponClass={monster.WeaponClass}/{monster.NativeWeaponClassId} damageType={monster.WeaponDamageType}/{monster.NativeDamageTypeId} weaponDamage={monster.WeaponDamage:F3} volatility={monster.DamageVolatility:F3} effectiveDamage={effectiveDamage:F3} aggro={monster.AggroRange:F1} wander={monster.WanderRange:F1} pos=({monster.PosX:F1},{monster.PosY:F1},{monster.PosZ:F1})");
            Debug.LogError($"[Combat]   ComponentIDs: Entity={entityId}, Behavior={behaviorId}, Skills={skillsId}, Manip={manipulatorsId}, Mods={modifiersId}, Unit={unitId}");
            Debug.LogError($"[Combat]   Position: ({posX:F1}, {posY:F1}, {posZ:F1}) PerceptionRange={monster.PerceptionRange} AggroRange={monster.AggroRange} ShoutRange={monster.ShoutRange} LeashRange={monster.LeashRange} AttackRange={monster.AttackRange} SyncTolerance={monster.ClientSyncTolerance} CollisionRadius={monster.CollisionRadius} AttackSpeed={monster.AttackSpeed} Cooldown={monster.AttackCooldown} WalkSpeed={monster.WalkSpeed} WanderRange={monster.WanderRange} Group={monster.EncounterGroupKey}");
            Debug.LogError($"[Combat]   NativeAI: AttackType={monster.AttackType} IdleAction={monster.IdleAction} LogicType={monster.LogicType} AttackStyle={monster.AttackStyle} Retreatable={monster.Retreatable} Leashed={monster.Leashed} UseIdleTime={monster.UseIdleTime} AutoScan={monster.AutoScan} AvoidUnits={monster.AvoidUnits} TurnBeforeMoving={monster.TurnBeforeMoving} PlayerControlled={monster.PlayerControlled} CollisionBand={monster.CollisionBand} CollisionPriority={monster.CollisionPriority} ScanFrequency={monster.ScanFrequency} FleeRange={monster.FleeRange} RetreatRangeSquared={monster.RetreatRangeSquared} TeleportFrequency={monster.TeleportFrequency} TeleportLimboTime={monster.TeleportLimboTime} BaseTime={monster.BaseTime} VariableTime={monster.VariableTime}");
            TraceMonsterState(monster, "spawn", null, -1f, monster.AttackRange, "spawn");

            OnMonsterSpawned?.Invoke(monster);
            return monster;
        }

        private static void MaterializeMonsterNativeUnitSlots(Monster monster)
        {
            if (monster == null)
                return;

            monster.NativeSlots ??= new NativeUnitSlotState();
            monster.NativeSlots.ResetNativeDefaults();
            monster.NativeSlots[NativeUnitSlot.AttackRating] = NativeFixed.ToWire(monster.AttackRating);
            monster.NativeSlots[NativeUnitSlot.DamageMod] = NativeFixed.Percent(monster.DamageMod * 100f);
            monster.NativeSlots[NativeUnitSlot.DefenseRating] = NativeFixed.ToWire(monster.DefenseRating);
            monster.NativeSlots[NativeUnitSlot.CriticalChance] = NativeFixed.Percent(monster.CritChance);
            monster.NativeSlots[NativeUnitSlot.MaxHealth] = (int)Math.Min(int.MaxValue, monster.MaxHPWire);
            monster.NativeSlots[NativeUnitSlot.MaxMana] = (int)Math.Min(int.MaxValue, monster.MaxManaWire);
            monster.NativeSlots[NativeUnitSlot.HealthRegen] = NativeFixed.ToWire(monster.HealthRegen);
            monster.NativeSlots[NativeUnitSlot.ManaRegen] = NativeFixed.ToWire(monster.ManaRegen);
            monster.NativeSlots[NativeUnitSlot.GlobalDamageTakenMod] = NativeFixed.Percent(monster.DamageTakenMod);
            monster.NativeSlots[NativeUnitSlot.DamageImmunity] = NativeFixed.Percent(monster.DamageImmunity);
            monster.NativeSlots[NativeUnitSlot.DamageResist] = NativeFixed.Percent(monster.DamageResist);
            monster.NativeSlots[NativeUnitSlot.CrushingResist] = NativeFixed.Percent(monster.CrushingResist);
            monster.NativeSlots[NativeUnitSlot.PiercingResist] = NativeFixed.Percent(monster.PiercingResist);
            monster.NativeSlots[NativeUnitSlot.SlashingResist] = NativeFixed.Percent(monster.SlashingResist);
            monster.NativeSlots[NativeUnitSlot.FireResist] = NativeFixed.Percent(monster.FireResist);
            monster.NativeSlots[NativeUnitSlot.IceResist] = NativeFixed.Percent(monster.IceResist);
            monster.NativeSlots[NativeUnitSlot.PoisonResist] = NativeFixed.Percent(monster.PoisonResist);
            monster.NativeSlots[NativeUnitSlot.ShadowResist] = NativeFixed.Percent(monster.ShadowResist);
            monster.NativeSlots[NativeUnitSlot.DivineResist] = NativeFixed.Percent(monster.DivineResist);
            monster.NativeSlots[NativeUnitSlot.MagicResist] = NativeFixed.Percent(monster.MagicDamageResist);
            monster.NativeSlots[NativeUnitSlot.FireDamageTakenMod] = NativeFixed.Percent(monster.FireDamageTakenMod);
            monster.NativeSlots[NativeUnitSlot.IceDamageTakenMod] = NativeFixed.Percent(monster.IceDamageTakenMod);
            monster.NativeSlots[NativeUnitSlot.PoisonDamageTakenMod] = NativeFixed.Percent(monster.PoisonDamageTakenMod);
            monster.NativeSlots[NativeUnitSlot.ShadowDamageTakenMod] = NativeFixed.Percent(monster.ShadowDamageTakenMod);
            monster.NativeSlots[NativeUnitSlot.DivineDamageTakenMod] = NativeFixed.Percent(monster.DivineDamageTakenMod);
            Debug.LogError($"[NATIVE-SLOTS] monster={monster.Name}#{monster.EntityId} source=Unit::computeAttributes slots='{monster.NativeSlots.DescribeDamageCore()}' status=materialized-desc-only activeSkillMods=PARTIAL");
        }

        private void InitializeMonsterNativeAiRuntime(Monster monster)
        {
            if (monster == null)
                return;

            monster.NativeAi ??= new NativeMonsterAiRuntime();
            monster.NativeAi.SetState(NativeMonsterStateId.IdleSearch, "spawn->idle");
            monster.NativeAi.SkillListBuilt = false;
            monster.NativeAi.TargetEntityId = 0;
            monster.NativeAi.AlertSourceEntityId = 0;
            if (HasNativeEncounterObject(monster))
            {
                NativeEncounterRuntime encounter = GetOrCreateNativeEncounterRuntime(monster);
                encounter.AddUnit(monster.EntityId);
                monster.NativeEncounter = encounter;
                SyncNativeEncounterGroup(encounter);
            }
            Debug.LogError($"[MON-FSM] monster={monster.Name}#{monster.EntityId} nativeState={monster.NativeAi.StateId} encounterState={monster.NativeEncounterObjectState} live={monster.NativeEncounterLiveUnitCount} returning={monster.NativeEncounterReturningUnitCount} native=MonsterBehavior2::States+EncounterObject::update status=schema-only");
        }

        private string ResolveNativeEncounterRuntimeKey(Monster monster)
        {
            if (monster == null || string.IsNullOrWhiteSpace(monster.EncounterGroupKey))
                return null;
            string instance = !string.IsNullOrWhiteSpace(monster.InstanceKey)
                ? NativeRoomRuntime.NormalizeInstanceKey(monster.InstanceKey)
                : NativeRoomRuntime.NormalizeInstanceKey(monster.ZoneName);
            return $"{instance}:{monster.EncounterGroupKey}";
        }

        private NativeEncounterRuntime GetOrCreateNativeEncounterRuntime(Monster monster)
        {
            string key = ResolveNativeEncounterRuntimeKey(monster);
            if (string.IsNullOrWhiteSpace(key))
                return null;
            if (!_nativeEncounterRuntimes.TryGetValue(key, out NativeEncounterRuntime runtime))
            {
                runtime = new NativeEncounterRuntime { Key = key };
                _nativeEncounterRuntimes[key] = runtime;
                Debug.LogError($"[ENCOUNTER-RUNTIME] create key='{key}' native=EncounterObject::update shared=True packetRuntime=BLOCKED");
            }
            return runtime;
        }

        private static void SyncNativeEncounterMirror(Monster monster)
        {
            NativeEncounterRuntime encounter = monster?.NativeEncounter;
            if (monster == null || encounter == null)
                return;

            monster.NativeEncounterObjectState = encounter.StateByte;
            monster.NativeEncounterLiveUnitCount = encounter.LiveUnitCount;
            monster.NativeEncounterReturningUnitCount = encounter.ReturningUnitCount;
            monster.NativeEncounterActiveTimer = encounter.ActiveTimer;
            monster.NativeEncounterScanTimer = encounter.ScanTimer;
            monster.NativeEncounterScanEnabled = encounter.ScanEnabled;
        }

        private void SyncNativeEncounterGroup(NativeEncounterRuntime encounter)
        {
            if (encounter == null)
                return;
            foreach (var monster in _activeMonsters.Values)
            {
                if (monster?.NativeEncounter == encounter)
                    SyncNativeEncounterMirror(monster);
            }
        }

        private void NativeEncounterMarkActive(Monster monster, string reason)
        {
            if (!HasNativeEncounterObject(monster))
                return;
            NativeEncounterRuntime encounter = monster.NativeEncounter ?? GetOrCreateNativeEncounterRuntime(monster);
            if (encounter == null)
                return;
            monster.NativeEncounter = encounter;
            encounter.MarkActive();
            SyncNativeEncounterGroup(encounter);
            Debug.LogError($"[ENCOUNTER-RUNTIME] key='{encounter.Key}' state={encounter.StateByte} live={encounter.LiveUnitCount} returning={encounter.ReturningUnitCount} reason={reason ?? "active"} native=EncounterObject+0x142 shared=True");
        }

        private void NativeEncounterLogAssistNotActive(Monster monster, string reason)
        {
            if (!HasNativeEncounterObject(monster))
                return;
            NativeEncounterRuntime encounter = monster.NativeEncounter ?? GetOrCreateNativeEncounterRuntime(monster);
            if (encounter == null)
                return;
            monster.NativeEncounter = encounter;
            SyncNativeEncounterMirror(monster);
            Debug.LogError($"[ENCOUNTER-RUNTIME] key='{encounter.Key}' state={encounter.StateByte} live={encounter.LiveUnitCount} returning={encounter.ReturningUnitCount} reason={reason ?? "assist"} action=not-active native=MonsterBehavior2::States@0x0051BB30 assist=0x0C no-EncounterObject+0x142-write");
        }

        private void NativeEncounterOnUnitDied(Monster monster, string reason)
        {
            if (!HasNativeEncounterObject(monster))
                return;
            NativeEncounterRuntime encounter = monster.NativeEncounter ?? GetOrCreateNativeEncounterRuntime(monster);
            if (encounter == null)
                return;
            monster.NativeEncounter = encounter;
            encounter.MarkUnitDied(monster.EntityId);
            SyncNativeEncounterGroup(encounter);
            SyncNativeEncounterMirror(monster);
            Debug.LogError($"[ENCOUNTER-RUNTIME] key='{encounter.Key}' onUnitDied unit={monster.EntityId} state={encounter.StateByte} live={encounter.LiveUnitCount} returning={encounter.ReturningUnitCount} scan={encounter.ScanEnabled} activeTimer={encounter.ActiveTimer} reason={reason ?? "death"} native=EncounterObject::OnUnitDied shared=True");
        }

        private void NativeEncounterOnUnitRemoved(Monster monster, string reason)
        {
            if (!HasNativeEncounterObject(monster))
                return;
            NativeEncounterRuntime encounter = monster.NativeEncounter ?? GetOrCreateNativeEncounterRuntime(monster);
            if (encounter == null)
                return;
            monster.NativeEncounter = encounter;
            bool reset = encounter.MarkUnitRemoved(monster.EntityId);
            SyncNativeEncounterGroup(encounter);
            SyncNativeEncounterMirror(monster);
            Debug.LogError($"[ENCOUNTER-RUNTIME] key='{encounter.Key}' onUnitRemoved unit={monster.EntityId} reset={reset} state={encounter.StateByte} live={encounter.LiveUnitCount} returning={encounter.ReturningUnitCount} scan={encounter.ScanEnabled} scanTimer={encounter.ScanTimer} activeTimer={encounter.ActiveTimer} reason={reason ?? "remove"} native=EncounterObject::OnUnitRemoved shared=True");
        }

        private List<string> BuildAuthoredArchetypeAncestry(params string[] roots)
        {
            var ancestry = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded)
                return ancestry;

            foreach (string root in roots)
            {
                if (string.IsNullOrWhiteSpace(root))
                    continue;
                foreach (string path in gc.GetInheritanceChainPaths(root))
                {
                    if (seen.Add(path))
                        ancestry.Add(path);
                }
            }
            return ancestry;
        }
        private string ResolveDungeonCreaturePath(string zoneName, string baseGcType)
        {
            if (string.IsNullOrEmpty(zoneName) || string.IsNullOrEmpty(baseGcType))
                return null;
            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded)
                return null;

            // Strip instance suffix: "dungeon00_level01_inst2147483649" → "dungeon00_level01"
            string lookupZone = zoneName;
            int instIdx = zoneName.IndexOf("_inst", StringComparison.OrdinalIgnoreCase);
            if (instIdx > 0)
                lookupZone = zoneName.Substring(0, instIdx);

            // Extract "dungeonNN" prefix and level number.
            // Only run for zones that look like "dungeonNN_levelMM[...]".
            if (!lookupZone.StartsWith("dungeon", StringComparison.OrdinalIgnoreCase))
                return null;
            int underscoreIdx = lookupZone.IndexOf('_');
            if (underscoreIdx <= 0) return null;
            string dungeonPrefix = lookupZone.Substring(0, underscoreIdx); // "dungeon00"

            int lvlIdx = lookupZone.IndexOf("_level", StringComparison.OrdinalIgnoreCase);
            if (lvlIdx < 0 || lvlIdx + 8 > lookupZone.Length) return null;
            string numStr = lookupZone.Substring(lvlIdx + 6, 2);
            if (!int.TryParse(numStr, out int levelNum) || levelNum < 1 || levelNum > 3)
                return null;
            int rank = levelNum; // level01→rank1, level02→rank2, level03→rank3

            // Special case: this exact creature is rendered as Rattle Tooth (the
            // dungeon00 unique boss) via MapToBaseGCType in CombatPackets.cs which
            // maps it to "world.dungeon00.mob.boss". Return null so SpawnGCType
            // stays unset and BuildMonsterSpawnPacket falls through to the raw
            // GCType → MapToBaseGCType translation. Without this exception my
            // prefix table grabs it as a regular melee03 ratling and the @boss
            // command spawns a rat instead of the boss.
            if (baseGcType.Equals("creatures.whiskers.broodling.basic.champion", StringComparison.OrdinalIgnoreCase))
                return null;

            string rankSuffix = $".rank{rank}";
            string dungeonMobPrefix = $"world.{dungeonPrefix}.mob.";
            foreach (string candidate in gc.RegisteredPaths)
            {
                if (string.IsNullOrWhiteSpace(candidate) ||
                    !candidate.EndsWith(rankSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                string resolved = null;
                if (candidate.StartsWith(dungeonMobPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    resolved = candidate;
                }
                else if (candidate.IndexOf('.') == candidate.LastIndexOf('.'))
                {
                    resolved = dungeonMobPrefix + candidate;
                }

                if (string.IsNullOrEmpty(resolved))
                    continue;
                var candidateAncestry = BuildResolvedAuthoredAncestrySet(gc, resolved);
                if (!candidateAncestry.Contains(baseGcType))
                    continue;

                Debug.LogError($"[SPAWN-ARCHETYPE] base='{baseGcType}' zone='{lookupZone}' resolved='{resolved}' source=authored-ancestry");
                return resolved;
            }

            RuntimeEvidenceManager.LogFallbackHit("spawn-archetype", "dungeon-family-unresolved", $"zone={lookupZone} base={baseGcType} rank={rank}", 32);
            return null;
        }

        private static HashSet<string> BuildResolvedAuthoredAncestrySet(GCDatabase gc, string path)
        {
            var ancestry = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (gc == null || string.IsNullOrWhiteSpace(path))
                return ancestry;

            string current = path.Trim();
            for (int depth = 0; depth < 32 && !string.IsNullOrWhiteSpace(current); depth++)
            {
                if (!ancestry.Add(current))
                    break;
                var node = gc.Resolve(current);
                if (node == null)
                    break;
                if (!string.IsNullOrWhiteSpace(node.Name))
                    ancestry.Add(node.Name);
                current = node.Extends;
            }

            return ancestry;
        }

        private GCNode ResolveAuthoredCreatureNode(string spawnGcType, string baseGcType)
        {
            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded) return null;

            GCNode node = null;
            if (!string.IsNullOrEmpty(spawnGcType))
                node = gc.ResolveWithInheritance(spawnGcType);
            if (node == null && !string.IsNullOrEmpty(baseGcType))
                node = gc.ResolveWithInheritance(baseGcType);
            return node;
        }

        private GCNode ResolveAuthoredBehaviorNode(string behaviorGcType)
        {
            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded || string.IsNullOrEmpty(behaviorGcType)) return null;
            return gc.ResolveWithInheritance(behaviorGcType);
        }

        private string ResolveAuthoredChildPath(string rootPath, string childPath)
        {
            if (string.IsNullOrEmpty(rootPath) || string.IsNullOrEmpty(childPath)) return null;
            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded) return null;

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string currentPath = rootPath;
            while (!string.IsNullOrEmpty(currentPath) && visited.Add(currentPath))
            {
                var node = gc.Resolve(currentPath);
                if (node == null) return null;
                if (RawChildPathExists(node, childPath))
                    return currentPath + "." + childPath;
                currentPath = node.Extends;
            }
            return null;
        }

        private bool RawChildPathExists(GCNode node, string childPath)
        {
            if (node == null || string.IsNullOrEmpty(childPath)) return false;
            var current = node;
            foreach (string part in childPath.Split('.'))
            {
                if (current == null || !current.Children.TryGetValue(part, out current))
                    return false;
            }
            return true;
        }

        private GCNode ResolveEffectiveAuthoredChild(string rootPath, string childPath)
        {
            if (string.IsNullOrEmpty(rootPath) || string.IsNullOrEmpty(childPath)) return null;
            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded) return null;
            return gc.ResolveWithInheritance(rootPath)?.ResolvePath(childPath);
        }

        private Dictionary<string, ManipulatorData> BuildSpawnManipulators(string spawnGcType, string baseGcType, GCNode authoredCreature, Dictionary<string, ManipulatorData> fallback)
        {
            var result = new Dictionary<string, ManipulatorData>(StringComparer.OrdinalIgnoreCase);
            var seenSkillPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            NativeSidecarMonsterSkills sidecarSkillRow = ResolveNativeSidecarSpawnSkillRow(spawnGcType, baseGcType, out string sidecarSourcePath);
            bool useAuthoritativeSidecarSkills = sidecarSkillRow != null;
            var manipulatorCandidates = new[]
            {
                authoredCreature?.GetChild("Manipulators"),
                ResolveEffectiveAuthoredChild(spawnGcType, "Manipulators"),
                ResolveEffectiveAuthoredChild(baseGcType, "Manipulators")
            };
            bool hadManipulatorNode = false;
            bool hadPrimaryWeaponNode = false;
            int skillIndex = 1;
            foreach (var manipulators in manipulatorCandidates)
            {
                if (manipulators == null)
                    continue;
                hadManipulatorNode = true;
                var primaryWeapon = manipulators.GetChild("PrimaryWeapon");
                if (primaryWeapon != null && !result.ContainsKey("primaryweapon"))
                {
                    hadPrimaryWeaponNode = true;
                    string primaryWeaponPath = ResolveManipulatorRuntimeType(primaryWeapon);
                    if (!string.IsNullOrEmpty(primaryWeaponPath))
                        result["primaryweapon"] = CreateSpawnManipulator(primaryWeaponPath, primaryWeapon);
                    else
                    {
                        RuntimeEvidenceManager.LogFallbackHit(
                            "spawn-manipulator",
                            "primaryweapon-unresolved",
                            $"spawn='{spawnGcType ?? ""}' base='{baseGcType ?? ""}'",
                            16);
                    }
                }

                if (useAuthoritativeSidecarSkills)
                    continue;

                if (manipulators.AnonymousChildren != null)
                {
                    foreach (var child in manipulators.AnonymousChildren)
                    {
                        if (child == null || !IsActiveSkillManipulatorPath(child.Extends)) continue;
                        if (!seenSkillPaths.Add(child.Extends)) continue;
                        result[$"skill{skillIndex++}"] = CreateSpawnManipulator(child.Extends, child);
                    }
                }

                foreach (var kvp in manipulators.Children)
                {
                    if (kvp.Key.Equals("PrimaryWeapon", StringComparison.OrdinalIgnoreCase)) continue;
                    string manipulatorPath = ResolveManipulatorRuntimeType(kvp.Value);
                    if (string.IsNullOrEmpty(manipulatorPath))
                        manipulatorPath = ResolveAuthoredChildPath(spawnGcType, "Manipulators." + kvp.Key) ?? ResolveAuthoredChildPath(baseGcType, "Manipulators." + kvp.Key);
                    if (string.IsNullOrEmpty(manipulatorPath) && IsActiveSkillManipulatorPath(kvp.Value.Extends))
                        manipulatorPath = kvp.Value.Extends;
                    if (string.IsNullOrEmpty(manipulatorPath)) continue;
                    if (!IsActiveSkillManipulatorPath(manipulatorPath) && !IsActiveSkillManipulatorPath(kvp.Value.Extends)) continue;
                    if (!seenSkillPaths.Add(manipulatorPath)) continue;
                    result[$"skill{skillIndex++}"] = CreateSpawnManipulator(manipulatorPath, kvp.Value);
                }
            }

            if (useAuthoritativeSidecarSkills)
            {
                skillIndex = ApplyNativeSidecarSpawnSkillsAuthoritative(result, sidecarSkillRow, spawnGcType, baseGcType, sidecarSourcePath);
                Debug.LogError($"[MON-SKILL-INHERIT] spawn='{spawnGcType ?? ""}' base='{baseGcType ?? ""}' skills={skillIndex - 1} authoritativeSidecar=True native=GCObject::getChildByType@0x005E9E50 createChildInstances@0x005E9840");
                return result;
            }

            if (result.Count > 0)
            {
                if (hadManipulatorNode && skillIndex > 1)
                    Debug.LogError($"[MON-SKILL-INHERIT] spawn='{spawnGcType ?? ""}' base='{baseGcType ?? ""}' skills={skillIndex - 1} native=GCObject::createChildInstances@0x005E9840");
                return result;
            }

            if (authoredCreature != null)
            {
                string reason = hadManipulatorNode
                    ? (hadPrimaryWeaponNode ? "authored-primary-unresolved" : "authored-empty")
                    : "authored-missing-manipulators";
                RuntimeEvidenceManager.LogFallbackHit(
                    "spawn-manipulator",
                    reason,
                    $"spawn='{spawnGcType ?? ""}' base='{baseGcType ?? ""}'",
                    16);
                Debug.LogError($"[AUTHORED-COVERAGE] area=spawn-manipulator status=blocked reason={reason} spawn='{spawnGcType ?? ""}' base='{baseGcType ?? ""}' dbFallbackSuppressed=True");
                return result;
            }

            RuntimeEvidenceManager.LogFallbackHit(
                "spawn-manipulator",
                hadManipulatorNode ? "authored-empty-blocked" : "missing-authored-blocked",
                $"spawn='{spawnGcType ?? ""}' base='{baseGcType ?? ""}' dbFallbackSuppressed=True dbCount={fallback?.Count ?? 0}",
                16);
            Debug.LogError($"[AUTHORED-COVERAGE] area=spawn-manipulator status=blocked reason={(hadManipulatorNode ? "authored-empty" : "missing-authored")} spawn='{spawnGcType ?? ""}' base='{baseGcType ?? ""}' dbFallbackSuppressed=True dbCount={fallback?.Count ?? 0}");
            return result;
        }

        private NativeSidecarMonsterSkills ResolveNativeSidecarSpawnSkillRow(string spawnGcType, string baseGcType, out string sourcePath)
        {
            sourcePath = null;
            var sidecar = NativeSidecarCatalog.Instance;
            if (sidecar == null || !sidecar.IsLoaded)
                return null;

            NativeSidecarMonsterSkills row = null;
            if (!string.IsNullOrWhiteSpace(spawnGcType) && sidecar.TryGetEffectiveMonsterSkills(spawnGcType, out row))
            {
                sourcePath = spawnGcType;
                return row;
            }
            else if (!string.IsNullOrWhiteSpace(baseGcType) && sidecar.TryGetEffectiveMonsterSkills(baseGcType, out row))
            {
                sourcePath = baseGcType;
                return row;
            }

            return null;
        }

        private int ApplyNativeSidecarSpawnSkillsAuthoritative(Dictionary<string, ManipulatorData> result, NativeSidecarMonsterSkills row, string spawnGcType, string baseGcType, string sourcePath)
        {
            int skillIndex = 1;
            if (result != null)
            {
                var staleSkillKeys = new List<string>();
                foreach (string key in result.Keys)
                {
                    if (key.StartsWith("skill", StringComparison.OrdinalIgnoreCase))
                        staleSkillKeys.Add(key);
                }
                foreach (string key in staleSkillKeys)
                    result.Remove(key);
            }

            if (row?.Skills == null || row.Skills.Count == 0)
            {
                Debug.LogError($"[MON-SKILL-SIDECAR] authoritative=True spawn='{spawnGcType ?? ""}' base='{baseGcType ?? ""}' source='{sourcePath ?? row?.MonsterPath ?? ""}' selectedManipulatorsNodeId='{row?.ManipulatorsNodeId ?? ""}' manipulatorSource='{row?.ManipulatorsSource ?? ""}' skillCount={row?.SkillCount ?? 0} loadedSkills=0 native=GCObject::getChildByType@0x005E9E50 createChildInstances@0x005E9840");
                return skillIndex;
            }

            foreach (var skill in row.Skills)
            {
                string skillPath = !string.IsNullOrWhiteSpace(skill.SkillPath) ? skill.SkillPath : skill.ExtendsRaw;
                if (string.IsNullOrWhiteSpace(skillPath))
                    continue;
                result[$"skill{skillIndex++}"] = CreateSpawnManipulatorFromSidecar(skill);
            }

            Debug.LogError($"[MON-SKILL-SIDECAR] authoritative=True spawn='{spawnGcType ?? ""}' base='{baseGcType ?? ""}' source='{sourcePath ?? row.MonsterPath ?? ""}' selectedManipulatorsNodeId='{row.ManipulatorsNodeId ?? ""}' manipulatorSource='{row.ManipulatorsSource ?? ""}' skillCount={row.SkillCount} loadedSkills={row.Skills.Count} totalSkills={skillIndex - 1} native=GCObject::getChildByType@0x005E9E50 createChildInstances@0x005E9840");
            return skillIndex;
        }

        private ManipulatorData CreateSpawnManipulatorFromSidecar(NativeSidecarSkill skill)
        {
            string skillPath = !string.IsNullOrWhiteSpace(skill.SkillPath) ? skill.SkillPath : skill.ExtendsRaw;
            var data = new ManipulatorData { gcType = skillPath };
            if (skill.EffectiveProperties != null)
            {
                foreach (var kvp in skill.EffectiveProperties)
                    data.properties[kvp.Key] = kvp.Value;
            }
            if (skill.LocalOverrideProperties != null)
            {
                foreach (var kvp in skill.LocalOverrideProperties)
                {
                    data.properties[kvp.Key] = kvp.Value;
                    if (kvp.Key.StartsWith("Description.", StringComparison.OrdinalIgnoreCase))
                        data.properties[kvp.Key.Substring("Description.".Length)] = kvp.Value;
                }
            }
            if (skill.IsPrimaryAttack)
                data.properties["IsPrimaryAttack"] = "true";
            return data;
        }

        private string ResolveManipulatorRuntimeType(GCNode manipulatorNode)
        {
            if (manipulatorNode == null) return null;
            if (!string.IsNullOrEmpty(manipulatorNode.Extends))
                return manipulatorNode.Extends;
            return null;
        }

        private ManipulatorData CreateSpawnManipulator(string gcType, GCNode node)
        {
            var data = new ManipulatorData { gcType = gcType };
            CopyManipulatorProperties(data, ResolveAuthoredWeaponDescription(node));
            CopyManipulatorProperties(data, node);
            CopyManipulatorProperties(data, node?.GetChild("Description"));
            return data;
        }

        private void ConsumeNativeState0MultiAttackDraw(Monster monster, MersenneTwister roomRng)
        {
            if (monster == null || roomRng == null)
                return;
            if (!monster.HasMultiAttackStateDraw || monster.MultiAttackCountRange == 0)
            {
                Debug.LogError($"[MON-STATE0] {monster?.Name}#{monster?.EntityId} multiAttackDraw=skip flag={monster?.HasMultiAttackStateDraw} base={monster?.MultiAttackBaseCount} range={monster?.MultiAttackCountRange} roomRngPos={roomRng?.CallsSinceReseed} native=MonsterBehavior2::States@0x0051bd7f-0x0051bdae(bit4-unset)");
                return;
            }
            uint raw = NativeRngLedger.Generate(roomRng, "room", "monster-state0:MonsterBehavior2::States+multiAttack", monster.InstanceKey);
            uint attacks = (uint)monster.MultiAttackBaseCount + (raw % monster.MultiAttackCountRange);
            Debug.LogError($"[MON-STATE0] {monster.Name}#{monster.EntityId} multiAttackDraw=fire raw=0x{raw:X8} base={monster.MultiAttackBaseCount} range={monster.MultiAttackCountRange} attacks={attacks} roomRngPos={roomRng.CallsSinceReseed} native=MonsterBehavior2::States@0x0051bdae Random::generate@0x0044b1f0");
        }

        private void ConfigureMonsterPrimaryActiveSkill(Monster monster)
        {
            if (monster == null) return;
            monster.ActiveSkills.Clear();
            monster.SelectedActiveSkill = null;
            monster.PrimaryActiveSkillPath = null;
            monster.PrimaryActiveSkillId = 10;
            monster.PrimaryActiveSkillRange = 0f;
            monster.PrimaryActiveSkillSpellUseRange = 0f;
            monster.PrimaryActiveSkillMinimumRange = -1f;
            monster.PrimaryActiveSkillTargetType = null;
            monster.PrimaryActiveSkillSpellUse = null;
            monster.PrimaryActiveSkillHasSelfHealthPct = false;
            monster.PrimaryActiveSkillSelfHealthPct = 0f;
            monster.PrimaryActiveSkillHasTargetHealthPct = false;
            monster.PrimaryActiveSkillTargetHealthPct = 0f;
            monster.PrimaryActiveSkillCooldownSeconds = 0f;
            monster.PrimaryActiveSkillCooldownTicks = 0;
            monster.PrimaryActiveSkillCooldownRemainingTicks = 0;
            monster.PrimaryActiveSkillCooldownLastTime = GetNativeCombatTime();
            monster.PrimaryActiveSkillAnimationId = 0;
            monster.PrimaryActiveSkillEffect = null;
            monster.PrimaryActiveSkillCastModifier = null;
            monster.UsePrimaryActiveSkillThisAttack = false;
            monster.HasMultiAttackStateDraw = false;
            monster.MultiAttackBaseCount = 0;
            monster.MultiAttackCountRange = 0;

            if (monster.Manipulators == null) return;
            foreach (var manipulator in monster.Manipulators.Values)
            {
                if (!IsActiveSkillManipulatorPath(manipulator?.gcType))
                    continue;

                var skill = CreateMonsterActiveSkillRuntime(manipulator, IsNativePrimaryActiveSkillManipulator(manipulator));
                skill.CooldownLastTime = GetNativeCombatTime();
                monster.ActiveSkills.Add(skill);
                LogMonsterActiveSkillEffectSupport(monster, skill);

                if (!monster.HasMultiAttackStateDraw &&
                    TryGetManipulatorBool(manipulator, "RepeatAnimation", out bool repeatAnimation) && repeatAnimation)
                {
                    int repeatCount = GetManipulatorInt(manipulator, "RepeatCount", 0);
                    if (repeatCount > 0)
                    {
                        monster.MultiAttackBaseCount = (ushort)Mathf.Clamp(repeatCount, 0, ushort.MaxValue);
                        monster.MultiAttackCountRange = (ushort)Mathf.Clamp(repeatCount, 0, ushort.MaxValue);
                        monster.HasMultiAttackStateDraw = monster.MultiAttackCountRange != 0;
                    }
                }
            }

            MonsterActiveSkillRuntime selected = monster.ActiveSkills.FirstOrDefault(s => s.IsPrimaryAttack)
                ?? monster.ActiveSkills.FirstOrDefault();
            if (selected != null)
                ApplyMonsterActiveSkillSelection(monster, selected);
            Debug.LogError($"[MON-SKILL] build-list {monster.Name}#{monster.EntityId} active={monster.ActiveSkills.Count} primary={monster.PrimaryActiveSkillPath ?? "none"} native=MonsterBehavior2::BuildSkillLists");
        }

        private MonsterActiveSkillRuntime CreateMonsterActiveSkillRuntime(ManipulatorData manipulator, bool isPrimaryAttack)
        {
            float cooldown = Mathf.Max(0f, GetManipulatorFloat(manipulator, "CoolDown", 0f));
            bool hasMinimumRange = TryGetManipulatorFloat(manipulator, "SpellUseMinimumRange", out float minimumRange);
            bool hasSelfHealthPct = TryGetManipulatorFloat(manipulator, "SelfHealthPct", out float selfHealthPct);
            bool hasTargetHealthPct = TryGetManipulatorFloat(manipulator, "TargetHealthPct", out float targetHealthPct);
            return new MonsterActiveSkillRuntime
            {
                Path = manipulator.gcType,
                Id = GetManipulatorByte(manipulator, "ID", 10),
                Range = GetManipulatorFloat(manipulator, "Range", 0f),
                SpellUseRange = GetManipulatorFloat(manipulator, "SpellUseRange", 0f),
                SpellUseMinimumRange = hasMinimumRange ? minimumRange : -1f,
                TargetType = GetManipulatorString(manipulator, "TargetType", null),
                SpellUse = GetManipulatorString(manipulator, "SpellUse", null),
                HasSelfHealthPct = hasSelfHealthPct,
                SelfHealthPct = selfHealthPct,
                HasTargetHealthPct = hasTargetHealthPct,
                TargetHealthPct = targetHealthPct,
                CooldownSeconds = cooldown,
                CooldownTicks = (ushort)Mathf.Clamp(Mathf.RoundToInt(cooldown * 30f), 0, ushort.MaxValue),
                CooldownRemainingTicks = 0,
                AnimationId = GetManipulatorInt(manipulator, "AnimationID", 0),
                Effect = GetManipulatorString(manipulator, "Effect", null),
                CastModifier = GetManipulatorString(manipulator, "CastModifier", null),
                IsPrimaryAttack = isPrimaryAttack
            };
        }

        private static void ApplyMonsterActiveSkillSelection(Monster monster, MonsterActiveSkillRuntime skill)
        {
            if (monster == null || skill == null) return;
            monster.SelectedActiveSkill = skill;
            monster.PrimaryActiveSkillPath = skill.Path;
            monster.PrimaryActiveSkillId = skill.Id;
            monster.PrimaryActiveSkillRange = skill.Range;
            monster.PrimaryActiveSkillSpellUseRange = skill.SpellUseRange;
            monster.PrimaryActiveSkillMinimumRange = skill.SpellUseMinimumRange;
            monster.PrimaryActiveSkillTargetType = skill.TargetType;
            monster.PrimaryActiveSkillSpellUse = skill.SpellUse;
            monster.PrimaryActiveSkillHasSelfHealthPct = skill.HasSelfHealthPct;
            monster.PrimaryActiveSkillSelfHealthPct = skill.SelfHealthPct;
            monster.PrimaryActiveSkillHasTargetHealthPct = skill.HasTargetHealthPct;
            monster.PrimaryActiveSkillTargetHealthPct = skill.TargetHealthPct;
            monster.PrimaryActiveSkillCooldownSeconds = skill.CooldownSeconds;
            monster.PrimaryActiveSkillCooldownTicks = skill.CooldownTicks;
            monster.PrimaryActiveSkillCooldownRemainingTicks = skill.CooldownRemainingTicks;
            monster.PrimaryActiveSkillCooldownLastTime = skill.CooldownLastTime;
            monster.PrimaryActiveSkillAnimationId = skill.AnimationId;
            monster.PrimaryActiveSkillEffect = skill.Effect;
            monster.PrimaryActiveSkillCastModifier = skill.CastModifier;
        }

        private bool IsNativePrimaryActiveSkillManipulator(ManipulatorData manipulator)
        {
            if (manipulator == null || string.IsNullOrWhiteSpace(manipulator.gcType))
                return false;
            if (!IsActiveSkillManipulatorPath(manipulator.gcType))
                return false;
            if (TryGetManipulatorBool(manipulator, "IsPrimaryAttack", out bool primaryFromManipulator))
                return primaryFromManipulator;

            var node = GCDatabase.Instance?.ResolveWithInheritance(manipulator.gcType);
            var desc = node?.GetChild("Description") ?? node;
            return desc != null && desc.GetBool("IsPrimaryAttack", false);
        }

        private bool IsActiveSkillManipulatorPath(string gcType)
        {
            var gc = GCDatabase.Instance;
            string current = gcType;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (!string.IsNullOrWhiteSpace(current) && visited.Add(current))
            {
                if (current.Equals("ActiveSkill", StringComparison.OrdinalIgnoreCase)
                    || current.EndsWith(".ActiveSkill", StringComparison.OrdinalIgnoreCase))
                    return true;

                var node = gc?.Resolve(current);
                current = node?.Extends;
            }
            return !string.IsNullOrEmpty(gcType) && gcType.StartsWith("skills.", StringComparison.OrdinalIgnoreCase);
        }

        private void CopyManipulatorProperties(ManipulatorData data, GCNode node)
        {
            if (data == null || node == null) return;
            foreach (var kvp in node.Properties)
                data.properties[kvp.Key] = kvp.Value;
        }

        private static float GetManipulatorFloat(ManipulatorData manipulator, string property, float fallback)
        {
            if (manipulator?.properties == null || !manipulator.properties.TryGetValue(property, out string raw))
                return fallback;
            return float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value)
                ? value
                : fallback;
        }

        private static bool TryGetManipulatorFloat(ManipulatorData manipulator, string property, out float value)
        {
            value = 0f;
            if (manipulator?.properties == null || !manipulator.properties.TryGetValue(property, out string raw))
                return false;
            return float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        private static int GetManipulatorInt(ManipulatorData manipulator, string property, int fallback)
        {
            if (manipulator?.properties == null || !manipulator.properties.TryGetValue(property, out string raw))
                return fallback;
            return int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value)
                ? value
                : fallback;
        }

        private static byte GetManipulatorByte(ManipulatorData manipulator, string property, byte fallback)
        {
            int value = GetManipulatorInt(manipulator, property, fallback);
            return (byte)Mathf.Clamp(value, byte.MinValue, byte.MaxValue);
        }

        private static string GetManipulatorString(ManipulatorData manipulator, string property, string fallback)
        {
            if (manipulator?.properties == null || !manipulator.properties.TryGetValue(property, out string raw))
                return fallback;
            return string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim().Trim('"');
        }

        private static bool TryGetManipulatorBool(ManipulatorData manipulator, string property, out bool value)
        {
            value = false;
            if (manipulator?.properties == null || !manipulator.properties.TryGetValue(property, out string raw))
                return false;
            raw = raw?.Trim().Trim('"');
            if (bool.TryParse(raw, out value))
                return true;
            if (int.TryParse(raw, out int intValue))
            {
                value = intValue != 0;
                return true;
            }
            return false;
        }

        private GCNode GetAuthoredWeaponDescription(GCNode creatureNode, string spawnGcType = null, string baseGcType = null)
        {
            var manipulators = creatureNode?.GetChild("Manipulators");
            var weapon = manipulators?.GetChild("PrimaryWeapon") ??
                         ResolveEffectiveAuthoredChild(spawnGcType, "Manipulators.PrimaryWeapon") ??
                         ResolveEffectiveAuthoredChild(baseGcType, "Manipulators.PrimaryWeapon");
            return ResolveAuthoredWeaponDescription(weapon);
        }

        private GCNode GetAuthoredBehaviorDescription(GCNode creatureNode)
        {
            var behavior = creatureNode?.GetChild("Behavior");
            return ResolveAuthoredBehaviorDescription(behavior);
        }

        private GCNode ResolveAuthoredBehaviorDescription(GCNode behaviorNode)
        {
            return ResolveAuthoredBehaviorDescription(behaviorNode, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        private GCNode ResolveAuthoredBehaviorDescription(GCNode behaviorNode, HashSet<string> visited)
        {
            if (behaviorNode == null) return null;
            string key = behaviorNode.Name + "|" + (behaviorNode.Extends ?? "");
            if (!visited.Add(key)) return behaviorNode.GetChild("Description") ?? behaviorNode;

            GCNode baseDescription = null;
            if (!string.IsNullOrEmpty(behaviorNode.Extends))
            {
                var baseBehavior = GCDatabase.Instance?.ResolveWithInheritance(behaviorNode.Extends);
                baseDescription = ResolveAuthoredBehaviorDescription(baseBehavior, visited);
            }

            var description = behaviorNode.GetChild("Description") ?? behaviorNode;
            return baseDescription == null ? description : MergeAuthoredNodes(baseDescription, description);
        }

        private GCNode ResolveAuthoredWeaponDescription(GCNode weaponNode)
        {
            return ResolveAuthoredWeaponDescription(weaponNode, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        private GCNode ResolveAuthoredWeaponDescription(GCNode weaponNode, HashSet<string> visited)
        {
            if (weaponNode == null) return null;
            string key = weaponNode.Name + "|" + (weaponNode.Extends ?? "");
            if (!visited.Add(key)) return weaponNode.GetChild("Description") ?? weaponNode;

            GCNode baseDescription = null;
            if (!string.IsNullOrEmpty(weaponNode.Extends))
            {
                var baseWeapon = GCDatabase.Instance?.ResolveWithInheritance(weaponNode.Extends);
                baseDescription = ResolveAuthoredWeaponDescription(baseWeapon, visited);
            }

            var description = weaponNode.GetChild("Description") ?? weaponNode;
            return baseDescription == null ? description : MergeAuthoredNodes(baseDescription, description);
        }

        private GCNode MergeAuthoredNodes(GCNode parent, GCNode child)
        {
            if (parent == null) return child;
            if (child == null) return parent;

            var merged = new GCNode
            {
                Name = child.Name,
                Extends = child.Extends ?? parent.Extends,
                IsStatic = child.IsStatic || parent.IsStatic,
                IsAnonymous = child.IsAnonymous || parent.IsAnonymous,
                SourceFile = child.SourceFile
            };

            foreach (var kvp in parent.Properties) merged.Properties[kvp.Key] = kvp.Value;
            foreach (var kvp in child.Properties) merged.Properties[kvp.Key] = kvp.Value;

            foreach (var kvp in parent.Children) merged.Children[kvp.Key] = kvp.Value;
            foreach (var kvp in child.Children)
            {
                merged.Children[kvp.Key] = merged.Children.TryGetValue(kvp.Key, out var existing)
                    ? MergeAuthoredNodes(existing, kvp.Value)
                    : kvp.Value;
            }

            foreach (var entry in parent.AnonymousChildren) merged.AnonymousChildren.Add(entry);
            foreach (var entry in child.AnonymousChildren) merged.AnonymousChildren.Add(entry);

            return merged;
        }

        private float GetAuthoredFloat(GCNode node, string property, float fallback)
        {
            return node != null && node.HasProperty(property) ? node.GetFloat(property, fallback) : fallback;
        }

        private string GetAuthoredString(GCNode node, string property, string fallback)
        {
            return node != null && node.HasProperty(property) ? node.GetString(property, fallback) : fallback;
        }

        private int GetAuthoredInt(GCNode node, string property, int fallback)
        {
            return node != null && node.HasProperty(property) ? node.GetInt(property, fallback) : fallback;
        }

        private bool GetAuthoredBool(GCNode node, string property, bool fallback)
        {
            return node != null && node.HasProperty(property) ? node.GetBool(property, fallback) : fallback;
        }

        private float GetAuthoredBehaviorFloat(GCNode creatureNode, string property, float fallback)
        {
            var desc = GetAuthoredBehaviorDescription(creatureNode);
            return GetAuthoredFloat(desc, property, fallback);
        }

        private float GetAuthoredBehaviorFloat(GCNode behaviorNode, GCNode creatureNode, string property, float fallback)
        {
            var behaviorDesc = ResolveAuthoredBehaviorDescription(behaviorNode);
            if (behaviorDesc != null && behaviorDesc.HasProperty(property))
                return behaviorDesc.GetFloat(property, fallback);
            return GetAuthoredBehaviorFloat(creatureNode, property, fallback);
        }

        private string GetAuthoredBehaviorString(GCNode behaviorNode, GCNode creatureNode, string property, string fallback)
        {
            var behaviorDesc = ResolveAuthoredBehaviorDescription(behaviorNode);
            if (behaviorDesc != null && behaviorDesc.HasProperty(property))
                return behaviorDesc.GetString(property, fallback);
            var creatureDesc = GetAuthoredBehaviorDescription(creatureNode);
            return GetAuthoredString(creatureDesc, property, fallback);
        }

        private int GetAuthoredBehaviorInt(GCNode behaviorNode, GCNode creatureNode, string property, int fallback)
        {
            var behaviorDesc = ResolveAuthoredBehaviorDescription(behaviorNode);
            if (behaviorDesc != null && behaviorDesc.HasProperty(property))
                return behaviorDesc.GetInt(property, fallback);
            var creatureDesc = GetAuthoredBehaviorDescription(creatureNode);
            return GetAuthoredInt(creatureDesc, property, fallback);
        }

        private bool GetAuthoredBehaviorBool(GCNode behaviorNode, GCNode creatureNode, string property, bool fallback)
        {
            var behaviorDesc = ResolveAuthoredBehaviorDescription(behaviorNode);
            if (behaviorDesc != null && behaviorDesc.HasProperty(property))
                return behaviorDesc.GetBool(property, fallback);
            var creatureDesc = GetAuthoredBehaviorDescription(creatureNode);
            return GetAuthoredBool(creatureDesc, property, fallback);
        }

        private bool ShouldRegisterWander(string idleAction, float wanderRange, string zoneName)
        {
            string action = NormalizeAuthoredEnumToken(idleAction);
            if (!string.IsNullOrEmpty(action))
            {
                if (IsWanderIdleAction(action))
                    return true;
                if (action.Equals("FOLLOW", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (action.Equals("GUARD", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (action.Equals("NOTHING", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return wanderRange > 0f;
        }

        private static bool IsWanderIdleAction(string idleAction)
        {
            string action = NormalizeAuthoredEnumToken(idleAction);
            return action.Equals("WANDER", StringComparison.OrdinalIgnoreCase) ||
                   action.Equals("3", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeAuthoredEnumToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            return value.Trim().Trim('"', '\'');
        }

        private static bool HasNativeEncounterObject(Monster monster)
        {
            return monster != null && !string.IsNullOrEmpty(monster.EncounterGroupKey);
        }

        private static bool HasNativeWanderEncounterOwner(Monster monster)
        {
            return HasNativeEncounterObject(monster);
        }

        private static bool HasNativeEncounterObjectActiveSearch(Monster monster)
        {
            SyncNativeEncounterMirror(monster);
            return HasNativeEncounterObject(monster) && monster.NativeEncounterObjectState == 2;
        }

        private static bool HasNativeAlertEncounterRelation(Monster listener, Monster source, out string relation)
        {
            relation = "invalid";
            if (listener == null || source == null)
                return false;
            if (!HasNativeEncounterObject(listener))
            {
                relation = "listener-no-encounter";
                return true;
            }
            if (!HasNativeEncounterObject(source))
            {
                relation = "source-no-encounter";
                return false;
            }
            if (string.Equals(listener.EncounterGroupKey, source.EncounterGroupKey, StringComparison.OrdinalIgnoreCase))
            {
                relation = "same-encounter";
                return true;
            }
            relation = "different-encounter";
            return false;
        }

        private float GetAuthoredLeashRange(GCNode behaviorNode, GCNode creatureNode, string tier)
        {
            if (TryGetAuthoredLeashRange(ResolveAuthoredBehaviorDescription(behaviorNode), out float leashRange))
                return leashRange;
            if (TryGetAuthoredLeashRange(GetAuthoredBehaviorDescription(creatureNode), out leashRange))
                return leashRange;
            return GetLeashRangeForTier(tier);
        }

        private bool TryGetAuthoredLeashRange(GCNode behaviorDesc, out float leashRange)
        {
            leashRange = 0f;
            if (behaviorDesc == null) return false;
            if (behaviorDesc.HasProperty("Leashed") && !behaviorDesc.GetBool("Leashed", true))
                return true;
            if (behaviorDesc.HasProperty("LeashRange"))
            {
                leashRange = behaviorDesc.GetFloat("LeashRange", 0f);
                return true;
            }
            return false;
        }

        private float GetAuthoredMoveSpeed(GCNode authoredDesc, CreatureData creature)
        {
            float speed = GetAuthoredFloat(authoredDesc, "Speed", float.NaN);
            if (!float.IsNaN(speed)) return speed;
            return GetMoveSpeedFromCreature(creature);
        }

        private float GetAuthoredWalkSpeed(GCNode authoredCreature, GCNode authoredDesc, CreatureData creature)
        {
            float speed = GetAuthoredFloat(authoredDesc, "WalkSpeed", float.NaN);
            if (!float.IsNaN(speed)) return speed;
            speed = GetAuthoredFloat(authoredCreature, "WalkSpeed", float.NaN);
            if (!float.IsNaN(speed)) return speed;
            speed = GetAuthoredFloat(GCDatabase.Instance?.ResolveWithInheritance("creatures.base.UnitStock"), "WalkSpeed", float.NaN);
            if (!float.IsNaN(speed)) return speed;
            return Mathf.Min(GetMoveSpeedFromCreature(creature), 25f);
        }

        private float GetAuthoredWanderRange(GCNode behaviorNode, GCNode creatureNode)
        {
            float range = GetAuthoredFloat(creatureNode, "WanderRange", float.NaN);
            if (!float.IsNaN(range)) return range;
            range = GetAuthoredBehaviorFloat(behaviorNode, creatureNode, "WanderRange", float.NaN);
            if (!float.IsNaN(range)) return range;
            return NATIVE_DEFAULT_MONSTER_WANDER_RANGE;
        }

        private float GetAuthoredAttackRange(GCNode authoredDesc, GCNode authoredWeapon, CreatureData creature)
        {
            float weaponRange = GetAuthoredFloat(authoredWeapon, "Range", float.NaN);
            if (!float.IsNaN(weaponRange)) return weaponRange;
            float attackRange = GetAuthoredFloat(authoredDesc, "AttackRange", float.NaN);
            if (!float.IsNaN(attackRange)) return attackRange;
            Debug.LogError($"[AUTHORED-COVERAGE] area=spawn-manipulator status=native-default reason=missing-weapon-range creature='{creature?.gcType ?? ""}' native=WeaponDesc::WeaponDesc@0x00599DD0 range=0");
            return 0f;
        }

        private struct NativeAttackTiming
        {
            public float AttackLeadDelay;
            public float AttackSoundLeadDelay;
            public int[] TotalFrames;
            public int[] HitFrames;
            public int[] SoundFrames;
            public bool[] VariantResolved;
            public NativeResolutionSource Source;
            public string Reason;
        }

        private static void ResolveNativeAttackDefaultFrames(bool useProjectile, out int totalFrames, out int hitFrame, out int soundFrame)
        {
            if (useProjectile)
            {
                totalFrames = 10;
                hitFrame = 5;
                soundFrame = 15;
                return;
            }

            totalFrames = 30;
            hitFrame = 15;
            soundFrame = 10;
        }

        private static void ApplyNativeAttackDefaultFrame(NativeAttackTiming timing, int variant, bool useProjectile)
        {
            ResolveNativeAttackDefaultFrames(useProjectile, out int totalFrames, out int hitFrame, out int soundFrame);
            timing.TotalFrames[variant] = totalFrames;
            timing.HitFrames[variant] = hitFrame;
            timing.SoundFrames[variant] = soundFrame;
            timing.VariantResolved[variant] = true;
        }

        private static void ApplyNativeAttackDefaultFrames(NativeAttackTiming timing, bool useProjectile)
        {
            for (int i = 0; i < timing.VariantResolved.Length; i++)
                ApplyNativeAttackDefaultFrame(timing, i, useProjectile);
        }

        private NativeAttackTiming ResolveNativeAttackTiming(GCNode authoredDesc, bool weaponUsesProjectile, float attackCooldown, string source)
        {
            ResolveNativeAttackDefaultFrames(weaponUsesProjectile, out int defaultTotalFrames, out int defaultHitFrame, out int defaultSoundFrame);
            var timing = new NativeAttackTiming
            {
                AttackLeadDelay = Mathf.Max(1f / 30f, defaultHitFrame / 30f),
                AttackSoundLeadDelay = Mathf.Max(1f / 30f, defaultSoundFrame / 30f),
                TotalFrames = new[] { 0, 0, 0 },
                HitFrames = new[] { 0, 0, 0 },
                SoundFrames = new[] { 0, 0, 0 },
                VariantResolved = new[] { false, false, false },
                Source = NativeResolutionSource.Blocked,
                Reason = "unresolved"
            };

            string animationsPath = GetAuthoredString(authoredDesc, "Animations", "");
            if (string.IsNullOrEmpty(animationsPath))
            {
                ApplyNativeAttackDefaultFrames(timing, weaponUsesProjectile);
                timing.Source = NativeResolutionSource.Native;
                timing.Reason = "native-default-missing-animations-path";
                Debug.LogError($"[ATTACK-TIMING] source='{source ?? "unknown"}' reason=missing-animations-path sourceKind={timing.Source} frames={defaultTotalFrames}/{defaultHitFrame}/{defaultSoundFrame} native={(weaponUsesProjectile ? "RangedWeapon::update projectile" : "MeleeWeapon::update")}");
                return timing;
            }

            var animations = GCDatabase.Instance.ResolveWithInheritance(animationsPath);
            if (animations == null || animations.AnonymousChildren == null || animations.AnonymousChildren.Count == 0)
            {
                ApplyNativeAttackDefaultFrames(timing, weaponUsesProjectile);
                timing.Source = NativeResolutionSource.Native;
                timing.Reason = "native-default-empty-animation-node";
                Debug.LogError($"[ATTACK-TIMING] source='{source ?? "unknown"}' path='{animationsPath}' reason=animations-node-empty sourceKind={timing.Source} frames={defaultTotalFrames}/{defaultHitFrame}/{defaultSoundFrame} native={(weaponUsesProjectile ? "RangedWeapon::update projectile" : "MeleeWeapon::update")}");
                return timing;
            }

            var animationById = new Dictionary<int, GCNode>();
            foreach (var animation in animations.AnonymousChildren)
            {
                if (animation == null) continue;
                int id = animation.GetInt("ID", 0);
                if (id > 0 && !animationById.ContainsKey(id))
                    animationById[id] = animation;
            }

            bool foundAny = false;
            bool invalidAny = false;
            GCNode leadRow = null;
            for (int variant = 0; variant < 3; variant++)
            {
                int normalId = 110 + variant;
                int specialId = 510 + variant;
                GCNode row = null;
                int resolvedId = 0;
                if (animationById.TryGetValue(normalId, out row))
                    resolvedId = normalId;
                else if (animationById.TryGetValue(specialId, out row))
                    resolvedId = specialId;

                if (row == null)
                {
                    ApplyNativeAttackDefaultFrame(timing, variant, weaponUsesProjectile);
                    foundAny = true;
                    Debug.LogError($"[ATTACK-TIMING-ROW] source='{source ?? "unknown"}' path='{animationsPath}' variant={variant} animationId=missing total={timing.TotalFrames[variant]} hit={timing.HitFrames[variant]} sound={timing.SoundFrames[variant]} sourceKind={NativeResolutionSource.Native} reason=native-default-missing-animation");
                    continue;
                }

                int total = row.GetInt("NumFrames", 0);
                int hit = row.GetInt("TriggerTime", 0);
                int sound = row.GetInt("SoundTriggerTime", 0);
                if (total <= 0 || hit <= 0 || sound <= 0)
                {
                    invalidAny = true;
                    RuntimeEvidenceManager.LogFallbackHit(
                        "attack-animation",
                        "invalid-authored-attack-frames",
                        $"source='{source ?? "unknown"}' path='{animationsPath}' variant={variant} animationId={resolvedId} total={total} hit={hit} sound={sound}",
                        32);
                    continue;
                }
                timing.TotalFrames[variant] = total;
                timing.HitFrames[variant] = hit;
                timing.SoundFrames[variant] = sound;
                timing.VariantResolved[variant] = true;
                if (leadRow == null)
                    leadRow = row;
                foundAny = true;
                Debug.LogError($"[ATTACK-TIMING-ROW] source='{source ?? "unknown"}' path='{animationsPath}' variant={variant} animationId={resolvedId} total={timing.TotalFrames[variant]} hit={timing.HitFrames[variant]} sound={timing.SoundFrames[variant]} sourceKind={NativeResolutionSource.Native}");
            }

            if (foundAny && !invalidAny)
            {
                if (leadRow != null)
                {
                    float trigger = leadRow.GetFloat("TriggerTime", float.NaN);
                    if (!float.IsNaN(trigger) && trigger > 0f)
                        timing.AttackLeadDelay = Mathf.Max(1f / 30f, trigger / 30f);
                    float soundTrigger = leadRow.GetFloat("SoundTriggerTime", float.NaN);
                    if (!float.IsNaN(soundTrigger) && soundTrigger > 0f)
                        timing.AttackSoundLeadDelay = Mathf.Max(1f / 30f, soundTrigger / 30f);
                }
                timing.Source = NativeResolutionSource.Native;
                timing.Reason = "animation-id-map";
            }
            else if (!foundAny)
            {
                ApplyNativeAttackDefaultFrames(timing, weaponUsesProjectile);
                timing.Source = NativeResolutionSource.Native;
                timing.Reason = "native-default-missing-attack-trigger";
            }
            else
            {
                timing.Source = NativeResolutionSource.Blocked;
                timing.Reason = "invalid-authored-attack-frames";
            }

            Debug.LogError($"[ATTACK-TIMING] source='{source ?? "unknown"}' path='{animationsPath}' lead={timing.AttackLeadDelay:F3} soundLead={timing.AttackSoundLeadDelay:F3} total=[{string.Join(",", timing.TotalFrames)}] hit=[{string.Join(",", timing.HitFrames)}] sound=[{string.Join(",", timing.SoundFrames)}] sourceKind={timing.Source} reason={timing.Reason}");
            return timing;
        }

        private float GetAuthoredAttackLeadDelay(GCNode authoredDesc, float fallback, out float soundLeadDelay)
        {
            soundLeadDelay = Mathf.Max(0.1f, fallback * (10f / 15f));
            string animationsPath = GetAuthoredString(authoredDesc, "Animations", "");
            if (string.IsNullOrEmpty(animationsPath)) return fallback;

            var animations = GCDatabase.Instance.ResolveWithInheritance(animationsPath);
            if (animations == null || animations.AnonymousChildren == null || animations.AnonymousChildren.Count == 0) return fallback;

            float firstTrigger = float.NaN;
            foreach (var animation in animations.AnonymousChildren)
            {
                float trigger = animation.GetFloat("TriggerTime", float.NaN);
                if (float.IsNaN(trigger) || trigger <= 0f) continue;
                if (float.IsNaN(firstTrigger)) firstTrigger = trigger;
                int id = animation.GetInt("ID", 0);
                if (id >= 110 && id <= 119)
                {
                    float soundTrigger = animation.GetFloat("SoundTriggerTime", float.NaN);
                    if (!float.IsNaN(soundTrigger) && soundTrigger > 0f)
                        soundLeadDelay = Mathf.Max(0.1f, soundTrigger / 30f);
                    return Mathf.Max(0.1f, trigger / 30f);
                }
            }

            return float.IsNaN(firstTrigger) ? fallback : Mathf.Max(0.1f, firstTrigger / 30f);
        }

        private int[] GetAuthoredAttackFrames(GCNode authoredDesc, string property, int fallback)
        {
            int[] values = { fallback, fallback, fallback };
            string animationsPath = GetAuthoredString(authoredDesc, "Animations", "");
            if (string.IsNullOrEmpty(animationsPath)) return values;
            var animations = GCDatabase.Instance.ResolveWithInheritance(animationsPath);
            if (animations?.AnonymousChildren == null) return values;
            foreach (var animation in animations.AnonymousChildren)
            {
                int id = animation.GetInt("ID", 0);
                int index = id >= 110 && id <= 112 ? id - 110 : (id >= 510 && id <= 512 ? id - 510 : -1);
                if (index < 0 || index >= values.Length) continue;
                int value = animation.GetInt(property, fallback);
                if (value > 0) values[index] = value;
            }
            return values;
        }

        private int CountAuthoredSounds(GCNode authoredDesc, string soundId)
        {
            string soundsPath = GetAuthoredString(authoredDesc, "Sounds", "");
            if (string.IsNullOrEmpty(soundsPath)) return 0;
            var sounds = GCDatabase.Instance.ResolveWithInheritance(soundsPath);
            if (sounds?.AnonymousChildren == null) return 0;
            foreach (var sound in sounds.AnonymousChildren)
            {
                string current = sound.GetString("SoundId", "");
                if (!string.Equals(current, soundId, StringComparison.OrdinalIgnoreCase)) continue;
                string list = sound.GetString("Sounds", "");
                if (string.IsNullOrWhiteSpace(list)) return 0;
                int count = 0;
                foreach (string item in list.Split(','))
                {
                    if (!string.IsNullOrWhiteSpace(item.Trim().Trim('"'))) count++;
                }
                return count;
            }
            return 0;
        }

        private bool HasAuthoredAttackSound(GCNode authoredDesc)
        {
            return CountAuthoredSounds(authoredDesc, "ATTACK") > 0 || CountAuthoredSounds(authoredDesc, "WEAPONATTACK") > 0;
        }

        private float GetManipulatorFloat(CreatureData creature, string propName, float fallback)
        {
            if (creature.manipulators == null) return fallback;
            foreach (var manip in creature.manipulators.Values)
            {
                if (manip.properties != null && manip.properties.TryGetValue(propName, out string val))
                {
                    if (float.TryParse(val, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float result))
                        return result;
                }
            }
            return fallback;
        }
        public List<Monster> SpawnFactionGroup(string faction, string tier, float centerX, float centerY, float centerZ, int count, float radius = 10f, string zoneName = null)
        {
            var spawned = new List<Monster>();
            var creatures = DatabaseLoader.GetCreaturesByFaction(faction);

            if (!string.IsNullOrEmpty(tier))
                creatures = creatures.FindAll(c => c.tier.Equals(tier, StringComparison.OrdinalIgnoreCase));

            if (creatures.Count == 0)
            {
                Debug.LogError($"[Combat] No creatures for faction '{faction}' tier '{tier}'");
                return spawned;
            }

            for (int i = 0; i < count; i++)
            {
                var randomCreature = creatures[DungeonRunners.Engine.Random.Range(0, creatures.Count)];
                float angle = DungeonRunners.Engine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
                float dist = DungeonRunners.Engine.Random.Range(0f, radius);
                float px = centerX + Mathf.Cos(angle) * dist;
                float pz = centerZ + Mathf.Sin(angle) * dist;

                var monster = SpawnMonster(randomCreature.gcType, px, centerY, pz, angle * Mathf.Rad2Deg, zoneName);
                if (monster != null)
                    spawned.Add(monster);
            }

            return spawned;
        }
        public Monster GetMonsterByComponent(uint componentId)
        {
            if (_componentToEntityMap.TryGetValue(componentId, out uint entityId))
                return GetMonster(entityId);
            return null;
        }
        public void ResetAllMonsters()
        {
            foreach (var kvp in _activeMonsters)
            {
                kvp.Value.IsAlive = true;
                SetRuntimeMonsterHPWire(kvp.Value, kvp.Value.MaxHPWire, false, Time.time, "RESET");
                var state = GetMonsterHPAuthority(kvp.Value);
                state.LastClientHPReportTime = 0f;
                state.LastClientHPReportWire = kvp.Value.MaxHPWire;
                SyncMonsterHPAuthority(kvp.Value, state);
                kvp.Value.UseTargetCount = 0;
                kvp.Value.AlertSourceEntityId = 0;
            }
            Debug.LogError($"[Combat] Reset {_activeMonsters.Count} monsters for zone transition");
        }
        public int ResetMonstersForInstanceToSpawn(string instanceKey)
        {
            string normalizedInstanceKey = NativeRoomRuntime.NormalizeInstanceKey(instanceKey);
            int resetCount = 0;
            foreach (var kvp in _activeMonsters)
            {
                var monster = kvp.Value;
                if (string.IsNullOrWhiteSpace(normalizedInstanceKey) || !MatchesInstance(monster, normalizedInstanceKey)) continue;
                monster.IsAlive = true;
                monster.AggroTriggered = false;
                monster.TargetId = 0;
                monster.CombatContactTargetId = 0;
                monster.CombatContactUntil = 0f;
                monster.UseTargetCount = 0;
                monster.AlertSourceEntityId = 0;
                monster.State = MonsterState.Idle;
                SetRuntimeMonsterHPWire(monster, monster.MaxHPWire, false, Time.time, "REENTRY-RESET");
                var state = GetMonsterHPAuthority(monster);
                state.LastClientHPReportTime = 0f;
                state.LastClientHPReportWire = monster.MaxHPWire;
                SyncMonsterHPAuthority(monster, state);
                HpSyncService.Instance.UnregisterMonster(monster.EntityId);
                HpSyncService.Instance.RegisterMonster(monster);
                resetCount++;
            }
            Debug.LogError($"[Combat] ResetMonstersForInstanceToSpawn('{normalizedInstanceKey}'): reset {resetCount} monsters to spawn HP");
            return resetCount;
        }
        /// <summary>
        /// Returns the component offset (0=Entity, 1=Behavior, 2=Skills, 3=Manipulators, 4=Modifiers, 5=Unit)
        /// Returns -1 if the CID is not a known monster component.
        /// </summary>
        public int GetComponentOffset(uint componentId)
        {
            if (_componentToEntityMap.TryGetValue(componentId, out uint entityId))
                return (int)(componentId - entityId);
            return -1;
        }
        public void DespawnMonster(uint entityId, bool allowRespawn = true, float respawnDelaySeconds = 30f)
        {
            if (!_activeMonsters.TryGetValue(entityId, out var monster))
                return;

            NativeEncounterOnUnitRemoved(monster, allowRespawn ? "despawn-respawn" : "despawn");
            RemovePlayerModifiersFromSource(monster.EntityId, allowRespawn ? "despawn-respawn" : "despawn");

            _componentToEntityMap.Remove(monster.EntityId);
            _componentToEntityMap.Remove(monster.BehaviorId);
            _componentToEntityMap.Remove(monster.SkillsId);
            _componentToEntityMap.Remove(monster.ManipulatorsId);
            _componentToEntityMap.Remove(monster.ModifiersId);
            _componentToEntityMap.Remove(monster.UnitId);
            _monsterRuntimeHPWire.Remove(entityId);
            _monsterHPAuthority.Remove(entityId);
            _monsterRuntimeDamageCommitted.Remove(entityId);
            _monsterHPRegenLastTime.Remove(entityId);
            _monsterHPRegenCarryWire.Remove(entityId);
            _monsterHPRegenCooldownTicks.Remove(entityId);
            _monsterManaRegenLastTime.Remove(entityId);
            _monsterManaRegenCooldownTicks.Remove(entityId);
            _monsterDeathUpdateAccum.Remove(entityId);
            _monsterStateTraceSignatures.Remove(entityId);
            ClearMonsterProjectilesForMonster(entityId);

            WanderSimulator.Instance.UnregisterEntity(entityId);
            OnMonsterDespawned?.Invoke(monster);
            UnregisterNativeEntityOrder(entityId);
            _activeMonsters.Remove(entityId);

            if (allowRespawn)
            {
                _respawnQueue.Add(new RespawnEntry
                {
                    GCType = monster.GCType,
                    ZoneName = monster.ZoneName,
                    InstanceKey = monster.InstanceKey,
                    PosX = monster.SpawnPosX,
                    PosY = monster.SpawnPosY,
                    PosZ = monster.SpawnPosZ,
                    Heading = monster.Heading,
                    EncounterGroupKey = monster.EncounterGroupKey,
                    EncounterDifficulty = monster.ExperienceDifficulty,
                    RespawnTime = Time.time + Mathf.Max(0f, respawnDelaySeconds)
                });
            }
        }
        public void RemoveComponentMapping(uint componentId)
        {
            _componentToEntityMap.Remove(componentId);
        }

        public void AddComponentMapping(uint componentId, uint entityId)
        {
            _componentToEntityMap[componentId] = entityId;
        }
        public Monster GetMonster(uint entityId)
        {
            return _activeMonsters.TryGetValue(entityId, out var m) ? m : null;
        }

        public bool IsMonster(uint entityId)
        {
            return _activeMonsters.ContainsKey(entityId);
        }

        public IEnumerable<Monster> GetActiveMonsters() => _activeMonsters.Values;

        public Monster GetNearestMonster(float posX, float posY, float maxRange = 50f)
        {
            return GetNearestMonster(posX, posY, maxRange, null);
        }

        public Monster GetNearestMonster(float posX, float posY, float maxRange, string instanceKey)
        {
            Monster nearest = null;
            float nearestDist = maxRange * maxRange;
            string normalizedInstanceKey = string.IsNullOrWhiteSpace(instanceKey)
                ? null
                : NativeRoomRuntime.NormalizeInstanceKey(instanceKey);

            Debug.LogError($"[GetNearest] Searching for monster near ({posX:F1},{posY:F1}) range={maxRange} instance={normalizedInstanceKey ?? "any"} monsters={_activeMonsters.Count}");

            foreach (var monster in _activeMonsters.Values)
            {
                if (!IsMonsterCombatSelectable(monster)) continue;
                if (!MatchesInstance(monster, normalizedInstanceKey)) continue;

                float dx = monster.PosX - posX;
                float dy = monster.PosY - posY;
                float distSq = dx * dx + dy * dy;
                float dist = Mathf.Sqrt(distSq);

                Debug.LogError($"[GetNearest]   {monster.Name} pos=({monster.PosX:F1},{monster.PosY:F1}) dist={dist:F1}");

                if (distSq < nearestDist)
                {
                    nearestDist = distSq;
                    nearest = monster;
                }
            }

            if (nearest != null)
                Debug.LogError($"[GetNearest] → Found: {nearest.Name} at ({nearest.PosX:F1},{nearest.PosY:F1})");
            else
                Debug.LogError($"[GetNearest] → NONE within range {maxRange}");

            return nearest;
        }

        private bool IsMonsterCombatSelectable(Monster monster)
        {
            return monster != null && monster.IsAlive && GetRuntimeMonsterHPWire(monster, "SELECT") > 0;
        }

        public bool MatchesInstance(Monster monster, string instanceKey)
        {
            if (monster == null)
                return false;
            if (string.IsNullOrWhiteSpace(instanceKey))
                return true;
            string normalizedInstanceKey = string.IsNullOrWhiteSpace(instanceKey)
                ? null
                : NativeRoomRuntime.NormalizeInstanceKey(instanceKey);
            string monsterKey = NativeRoomRuntime.NormalizeInstanceKey(monster.InstanceKey);
            return string.Equals(monsterKey, normalizedInstanceKey, StringComparison.OrdinalIgnoreCase);
        }

        public Monster FindMonsterForTarget(ushort clientTargetId, float playerPosX, float playerPosY, string instanceKey = null)
        {
            Debug.LogError($"[Combat] FindMonsterForTarget target={clientTargetId} player=({playerPosX:F1}, {playerPosY:F1}) active={_activeMonsters.Count}");

            var monster = GetMonster(clientTargetId);
            if (monster != null)
            {
                if (!IsMonsterCombatSelectable(monster))
                {
                    Debug.LogError($"[Combat] Target {clientTargetId} resolved to inactive monster {monster.Name} alive={monster.IsAlive} HP={monster.CurrentHPWire / 256}/{monster.MaxHPWire / 256}");
                    return null;
                }
                Debug.LogError($"[Combat] Found monster by EntityId: {clientTargetId}");
                return monster;
            }

            if (_componentToEntityMap.TryGetValue(clientTargetId, out uint entityId))
            {
                monster = GetMonster(entityId);
                if (monster != null)
                {
                    if (!IsMonsterCombatSelectable(monster))
                    {
                        Debug.LogError($"[Combat] Target component {clientTargetId} resolved to inactive monster {monster.Name} alive={monster.IsAlive} HP={monster.CurrentHPWire / 256}/{monster.MaxHPWire / 256}");
                        return null;
                    }
                    Debug.LogError($"[Combat] Found monster by ComponentId: {clientTargetId} -> EntityId: {entityId}");
                    return monster;
                }
            }

            if (_clientToServerIdMap.TryGetValue(clientTargetId, out uint serverId))
            {
                monster = GetMonster(serverId);
                if (monster != null)
                {
                    if (!IsMonsterCombatSelectable(monster))
                    {
                        Debug.LogError($"[Combat] Learned target {clientTargetId} resolved to inactive monster {monster.Name} alive={monster.IsAlive} HP={monster.CurrentHPWire / 256}/{monster.MaxHPWire / 256}");
                        return null;
                    }
                    Debug.LogError($"[Combat] Found monster by learned ClientId: {clientTargetId} -> EntityId: {serverId}");
                    return monster;
                }
            }

            Debug.LogError($"[Combat] Target {clientTargetId} not found by any ID, checking nearby monster only");
            monster = GetNearestMonster(playerPosX, playerPosY, 30f, instanceKey);

            if (monster != null)
            {
                _clientToServerIdMap[clientTargetId] = monster.EntityId;
                Debug.LogError($"[Combat] LEARNED MAPPING: ClientId {clientTargetId} -> EntityId {monster.EntityId} ({monster.Name})");
            }

            return monster;
        }

        public DamageResult ApplyDamage(uint attackerId, uint defenderId, int damageAmount)
        {
            var monster = GetMonster(defenderId);
            uint damageWire = (uint)Math.Max(1, damageAmount) * 256u;
            bool applied = ApplyNativePlayerDamageToMonsterWire(monster, damageWire, "ApplyDamage", out uint oldHPWire, out uint newHPWire, out bool died);
            if (!applied)
                return new DamageResult { Success = false };
            int appliedDamage = (int)(((oldHPWire > newHPWire ? oldHPWire - newHPWire : 0u) + 255u) / 256u);

            return new DamageResult
            {
                Success = true,
                DamageDealt = appliedDamage,
                IsCritical = false,
                DefenderDied = died,
                NewHPWire = newHPWire
            };
        }

        private float Distance2D(float x1, float y1, float x2, float y2)
        {
            float dx = x2 - x1;
            float dy = y2 - y1;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        private float ResolveMonsterEffectiveAttackRange(Monster monster)
        {
            if (monster == null || monster.AttackRange <= 0f) return 0f;
            return monster.AttackRange + Mathf.Max(0f, monster.CollisionRadius) + ResolveAvatarCombatRadius();
        }

        public float GetMonsterEffectiveAttackRange(Monster monster)
        {
            return ResolveMonsterEffectiveAttackRange(monster);
        }

        public float ResolvePlayerMeleeRange(PlayerState state, Monster monster)
        {
            float weaponRange = state != null && state.WeaponRange > 0 ? state.WeaponRange : monster != null ? monster.AttackRange : 0f;
            float monsterRadius = monster != null ? Mathf.Max(0f, monster.CollisionRadius) : 0f;
            return Mathf.Max(1f, weaponRange) + ResolveAvatarCombatRadius() + monsterRadius;
        }

        public float ResolvePlayerMeleeNativeContactRange(PlayerState state, Monster monster)
        {
            return ResolvePlayerMeleeRange(state, monster);
        }

        public float ResolvePlayerRangedProjectileRange(PlayerState state, Monster monster)
        {
            float range = ResolvePlayerMeleeRange(state, monster);
            if (state == null || !DamageComputer.IsNativeProjectileWeapon(state))
                return range;

            float projectileSize = Mathf.Max(0f, state.WeaponProjectileSize);
            float firstTickTravel = state.WeaponProjectileSpeed > 0f ? Mathf.Max(0f, state.WeaponProjectileSpeed) * NATIVE_UNIT_TICK_INTERVAL : 0f;
            return range + projectileSize + firstTickTravel;
        }

        public float ResolvePlayerRangedWeaponUseRange(PlayerState state)
        {
            if (state != null && state.WeaponRange > 0f)
                return state.WeaponRange;
            return 1f;
        }

        public const float NativeDefaultManipulatorInitUseRange = 250f;

        public float ResolveNativeUseTargetInitUseRange(PlayerState state, Monster monster, out float clientSyncTolerance, out string source)
        {
            clientSyncTolerance = 0f;
            if (state != null && DamageComputer.IsNativeProjectileWeapon(state))
            {
                source = "ManipulatorDesc+0x6c init-use reach reader=UseTarget::CheckInitUse@0x00548980";
                return NativeDefaultManipulatorInitUseRange;
            }

            source = "native-contact-range";
            return ResolvePlayerMeleeNativeContactRange(state, monster);
        }

        public bool EvaluateNativeUseTargetInitUse(float actorX, float actorY, float targetX, float targetY,
            float initUseRange, float clientSyncTolerance, out float distance, out long distanceSqFixed8, out long thresholdSqFixed8)
        {
            float dx = targetX - actorX;
            float dy = targetY - actorY;
            distance = Mathf.Sqrt((dx * dx) + (dy * dy));

            int dxFixed = Mathf.RoundToInt(dx * 256f);
            int dyFixed = Mathf.RoundToInt(dy * 256f);
            distanceSqFixed8 = (((long)dxFixed * dxFixed) >> 8) + (((long)dyFixed * dyFixed) >> 8);

            float effectiveRange = Mathf.Max(0f, initUseRange + clientSyncTolerance);
            int rangeFixed = Mathf.RoundToInt(effectiveRange * 256f);
            thresholdSqFixed8 = ((long)rangeFixed * rangeFixed) >> 8;
            return thresholdSqFixed8 > 0 && distanceSqFixed8 <= thresholdSqFixed8;
        }

        private float ResolveNativeClientContactRange(Monster monster, CombatPlayer player, float allowedRange)
        {
            if (monster == null || player == null) return 0f;
            return allowedRange;
        }

        private static string ResolveMonsterPathMapKey(Monster monster)
        {
            if (!string.IsNullOrWhiteSpace(monster?.InstanceKey))
                return monster.InstanceKey;
            return monster?.ZoneName;
        }

        private bool IsMonsterAttackPathClear(Monster monster, CombatPlayer target, string source)
        {
            if (monster == null || target == null) return false;
            string pathMapKey = ResolveMonsterPathMapKey(monster);
            if (string.IsNullOrWhiteSpace(pathMapKey)) return true;
            PathMap pathMap = PathMapManager.Instance.GetPathMap(pathMapKey);
            if (pathMap == null) return true;
            if (!pathMap.TryCanReachPoint(monster.PosX, monster.PosY, target.PosX, target.PosY, out bool clear))
            {
                if (!string.IsNullOrEmpty(source))
                    Debug.LogError($"[MON-LOS] {monster.Name}#{monster.EntityId}->{target.Name} pathCoverage=False source={source} pathKey='{pathMapKey}' path=({monster.PosX:F1},{monster.PosY:F1})->({target.PosX:F1},{target.PosY:F1}) action=native-unblocked");
                return true;
            }
            if (!clear && !string.IsNullOrEmpty(source))
                Debug.LogError($"[MON-LOS] {monster.Name}#{monster.EntityId}->{target.Name} worldBlocked=True source={source} pathKey='{pathMapKey}' path=({monster.PosX:F1},{monster.PosY:F1})->({target.PosX:F1},{target.PosY:F1})");
            return clear;
        }

        private void ClearMonsterCombatContact(Monster monster, CombatPlayer target)
        {
            if (monster == null || target == null) return;
            if (monster.CombatContactTargetId != target.EntityId) return;
            monster.CombatContactTargetId = 0;
            monster.CombatContactUntil = 0f;
        }

        private bool IsNativeClientCombatContact(Monster monster, CombatPlayer target, float dist, float allowedRange)
        {
            if (!IsMonsterAttackPathClear(monster, target, null))
            {
                ClearMonsterCombatContact(monster, target);
                return false;
            }
            if (HasCombatContact(monster, target)) return true;
            if (monster == null || target == null) return false;
            float contactRange = ResolveNativeClientContactRange(monster, target, allowedRange);
            return contactRange > 0f && dist <= contactRange + NATIVE_CONTACT_RANGE_EPSILON;
        }

        private bool HasNativeMonsterTargetAction(Monster monster, CombatPlayer target, float dist)
        {
            if (monster == null || target == null) return false;
            if (!monster.AggroTriggered || monster.TargetId != target.EntityId) return false;
            float targetRange = ResolveMonsterTargetSearchRange(monster, true);
            return targetRange <= 0f || dist <= targetRange + NATIVE_CONTACT_RANGE_EPSILON;
        }

        private bool HasMonsterWeaponRuntimeReach(Monster monster, CombatPlayer target, float dist, float allowedRange, bool nativeClientContact)
        {
            if (monster == null || target == null || allowedRange <= 0f) return false;
            return dist <= allowedRange + NATIVE_CONTACT_RANGE_EPSILON;
        }

        private bool HasMonsterInitUseGateway(Monster monster, CombatPlayer target)
        {
            if (monster == null || target == null) return false;
            return EvaluateNativeUseTargetInitUse(monster.PosX, monster.PosY, target.PosX, target.PosY,
                NativeDefaultManipulatorInitUseRange, 0f, out _, out _, out _);
        }

        private float ResolveAvatarCombatRadius()
        {
            if (_avatarCombatRadius.HasValue) return _avatarCombatRadius.Value;
            float radius = 3f;
            var avatar = GCDatabase.Instance?.ResolveWithInheritance("avatar.base.avatar");
            var desc = avatar?.GetChild("Description") ?? avatar;
            radius = GetAuthoredFloat(desc, "CollisionRadius", radius);
            var bounds = avatar?.GetChild("Object")?.GetChild("Description") ?? avatar?.GetChild("Object");
            if (bounds != null)
            {
                float boundsRadius = Mathf.Max(
                    Mathf.Abs(GetAuthoredFloat(bounds, "MinX", 0f)),
                    Mathf.Abs(GetAuthoredFloat(bounds, "MaxX", 0f)),
                    Mathf.Abs(GetAuthoredFloat(bounds, "MinY", 0f)),
                    Mathf.Abs(GetAuthoredFloat(bounds, "MaxY", 0f)));
                if (boundsRadius > 0f) radius = boundsRadius;
            }
            _avatarCombatRadius = Mathf.Max(0f, radius);
            return _avatarCombatRadius.Value;
        }

        private float ResolveMonsterAttackWindup(Monster monster)
        {
            ResolveMonsterAttackAnimationFrames(monster, out int totalFrames, out int hitFrame, out int soundFrame);
            return Mathf.Max(1f / 30f, ResolveMonsterAttackFrameSeconds(monster, hitFrame));
        }

        private float ResolveMonsterAttackSoundDelay(Monster monster, float windup)
        {
            ResolveMonsterAttackAnimationFrames(monster, out int totalFrames, out int hitFrame, out int soundFrame);
            float soundDelay = ResolveMonsterAttackFrameSeconds(monster, soundFrame);
            return Mathf.Clamp(soundDelay, 1f / 30f, Mathf.Max(1f / 30f, windup));
        }

        private void ResolveMonsterAttackAnimationFrames(Monster monster, out int totalFrames, out int hitFrame, out int soundFrame)
        {
            int attackIndex = monster != null ? Mathf.Clamp(monster.AttackAnimationIndex, 0, 2) : 0;
            bool useProjectile = monster != null && monster.WeaponUsesProjectile;
            ResolveNativeAttackDefaultFrames(useProjectile, out int defaultTotalFrames, out int defaultHitFrame, out int defaultSoundFrame);
            totalFrames = monster?.AttackTotalFrames != null && monster.AttackTotalFrames.Length > attackIndex ? monster.AttackTotalFrames[attackIndex] : defaultTotalFrames;
            hitFrame = monster?.AttackHitFrames != null && monster.AttackHitFrames.Length > attackIndex ? monster.AttackHitFrames[attackIndex] : defaultHitFrame;
            soundFrame = monster?.AttackSoundFrames != null && monster.AttackSoundFrames.Length > attackIndex ? monster.AttackSoundFrames[attackIndex] : defaultSoundFrame;
            bool resolved = monster?.AttackFrameResolved != null &&
                monster.AttackFrameResolved.Length > attackIndex &&
                monster.AttackFrameResolved[attackIndex] &&
                totalFrames > 0 && hitFrame > 0 && soundFrame > 0;
            if (!resolved)
            {
                totalFrames = defaultTotalFrames;
                hitFrame = defaultHitFrame;
                soundFrame = defaultSoundFrame;
                Debug.LogError($"[ATTACK-TIMING] monster={monster?.Name ?? "<null>"} gc={monster?.SpawnGCType ?? monster?.GCType ?? "<null>"} variant={attackIndex} sourceKind={NativeResolutionSource.Native} reason={monster?.AttackTimingReason ?? "unresolved"} frames={totalFrames}/{hitFrame}/{soundFrame} native={(useProjectile ? "RangedWeapon::update projectile" : "MeleeWeapon::update")}");
                return;
            }
        }

        private float ResolveMonsterAttackFrameSeconds(Monster monster, int frame)
        {
            float authoredSpeed = GCDatabase.Instance.GetKnob("MonsterAttackSpeed", 100f);
            float attackSpeed = authoredSpeed;
            if (monster != null)
                attackSpeed *= Mathf.Max(0.01f, monster.AttackSpeed);
            int speedField = Mathf.Max(1, Mathf.RoundToInt(attackSpeed));
            int ticks = Mathf.Max(1, (frame * 100) / speedField);
            return ticks / 30f;
        }

        private float ResolveMonsterAttackCooldownSeconds(Monster monster)
        {
            float cooldown = monster != null ? monster.AttackCooldown : 1.75f;
            int cooldownFixed256 = Mathf.Max(0, Mathf.RoundToInt(Mathf.Max(0.01f, cooldown) * 256f));
            int ticks = Mathf.Max(1, (int)(((long)cooldownFixed256 * 0x1e00L) >> 16));
            return ticks / 30f;
        }

        private void AdvanceMonsterAttackAnimation(Monster monster)
        {
            MersenneTwister rng = GetRoomRngForMonster(monster);
            if (monster == null || rng == null) return;
            uint useRaw = NativeRngLedger.Generate(rng, "room", "monster-attack:use-animation", monster.InstanceKey ?? monster.ZoneName);
            uint previous = monster.AttackAnimationIndex;
            monster.AttackUseRaw = useRaw;
            monster.AttackAnimationIndex = (byte)(((useRaw & 1u) + previous + 1u) % 3u);
        }

        private void ConsumeMonsterAttackSearchRng(Monster monster, CombatPlayer target, string reason)
        {
            if (monster == null || target == null) return;
            if (monster.AttackSearchTargetId == target.EntityId) return;
            monster.AttackSearchTargetId = target.EntityId;
            monster.AttackSearchRaw = 0;
            monster.AttackSearchTieRaw = 0;
            var rng = GetRoomRngForMonster(monster);
            if (rng == null) return;
            uint searchRaw = NativeRngLedger.Generate(rng, "room", "SearchForAttack::GetRandomAttackDistance", monster.InstanceKey ?? monster.ZoneName);
            monster.AttackSearchRaw = searchRaw;
            Debug.LogError($"[MON-AI-RNG] {monster.Name}#{monster.EntityId}->{target.Name} searchRaw=0x{searchRaw:X8} tie=none reason={reason} rngPos={rng.CallsSinceReseed}");
        }

        private void ConsumeMonsterAttackSoundRng(Monster monster)
        {
            if (monster == null || !monster.AttackSoundPending) return;
            monster.AttackSoundPending = false;
            uint soundRaw = NativeRandomStreams.GenerateGlobalSound("monster-attack:sound", $"{monster.Name}#{monster.EntityId}");
            monster.AttackSoundRaw = soundRaw;
            monster.AttackSoundGateRaw = soundRaw;
            monster.AttackSoundRepeatRaw = (soundRaw & 3u) == 0 ? soundRaw : 0;
            var rng = GetRoomRngForMonster(monster);
            string roomRngPos = rng != null ? rng.CallsSinceReseed.ToString() : "n/a";
            Debug.LogError($"[MON-ATTACK] {monster.Name} sound nativeGlobalSoundRng=True raw=0x{soundRaw:X8} repeat={(monster.AttackSoundRepeatRaw != 0)} weaponSounds={monster.AttackWeaponSoundCount} attackSounds={monster.AttackRepeatSoundCount} globalSoundRngPos={NativeRandomStreams.GlobalSoundCalls} roomRngPos={roomRngPos}");
        }

        public uint ConsumeNativeOnApplyDamageEffectRng(MersenneTwister rng, string actor, uint targetId, string targetName, uint oldHPWire, uint newHPWire, uint targetMaxHPWire, uint damageWire, string source)
        {
            if (rng == null) return 0;
            if (damageWire == 0 || oldHPWire <= newHPWire || newHPWire == 0)
                return 0;

            uint gateRaw = NativeRngLedger.Generate(rng, "room", $"{source ?? "Damage::apply"}:Unit::onApplyDamage:effect-gate", actor);
            int gateRoll = (int)(gateRaw % 100u);
            uint appliedWire = oldHPWire - newHPWire;
            int severity = targetMaxHPWire > 0
                ? (int)Math.Min(int.MaxValue, ((long)appliedWire * 100L) / targetMaxHPWire)
                : 0;
            uint resistRaw = 0;
            if (gateRoll == 0 && severity >= 10)
                resistRaw = NativeRngLedger.Generate(rng, "room", $"{source ?? "Damage::apply"}:Unit::onApplyDamage:resist", actor);
            Debug.LogError($"[RNG-COMBAT] Unit::onApplyDamage effect actor={actor} target={targetName}#{targetId} source={source} gateRaw=0x{gateRaw:X8} gateRoll={gateRoll} severity={severity} resistRaw=0x{resistRaw:X8} hp={oldHPWire}->{newHPWire}/{targetMaxHPWire} appliedWire={appliedWire} damageWire={damageWire} rngAfter={rng.CallsSinceReseed}");
            return gateRaw;
        }

        public void FlushPlayerAttackCommitsBeforeSync(uint playerEntityId)
        {
            if (playerEntityId == 0) return;
            ProcessMonsterAttacks(0f, playerEntityId, false, null, GetNativeCombatTime());
        }

        public void CancelMonsterPendingAttack(Monster monster, string reason)
        {
            if (monster == null) return;
            bool hadPending = monster.AttackPending || monster.AttackSoundPending || monster.AttackClientVisible;
            monster.AttackPending = false;
            monster.AttackSoundPending = false;
            monster.AttackClientVisible = false;
            monster.AttackNativeContactOnly = false;
            monster.AttackHitResolved = false;
            monster.AttackStartedTime = 0f;
            monster.AttackCommitTime = 0f;
            monster.AttackSoundTime = 0f;
            monster.AttackEndTime = 0f;
            monster.AttackUseRaw = 0;
            monster.AttackCommitTargetX = 0f;
            monster.AttackCommitTargetY = 0f;
            monster.CombatContactTargetId = 0;
            monster.CombatContactUntil = 0f;
            if (monster.State == MonsterState.Attacking)
                monster.State = MonsterState.Combat;
            if (hadPending)
                Debug.LogError($"[MON-ATTACK] canceled pending attack {monster.Name}#{monster.EntityId} reason={reason}");
            TraceMonsterState(monster, "attack-cancel", null, -1f, -1f, reason);
        }

        public void DelayMonsterAttackRetry(Monster monster, string reason)
        {
            if (monster == null) return;
            float cooldown = ResolveMonsterAttackCooldownSeconds(monster);
            monster.LastAttackTime = GetNativeCombatTime() + cooldown;
            Debug.LogError($"[MON-ATTACK] retry delayed {monster.Name}#{monster.EntityId} reason={reason} cooldown={cooldown:F2}");
        }

        private bool ShouldLogFarTargetAction(Monster monster, float now)
        {
            if (monster == null) return false;
            if (!_monsterFarTargetActionLogTime.TryGetValue(monster.EntityId, out float last) || now - last >= 1f)
            {
                _monsterFarTargetActionLogTime[monster.EntityId] = now;
                return true;
            }
            return false;
        }

        private void TraceFarTargetAction(Monster monster, CombatPlayer target, float dist, float allowedRange, string source)
        {
            if (!ShouldLogFarTargetAction(monster, GetNativeCombatTime())) return;
            float targetRange = ResolveMonsterTargetSearchRange(monster, true);
            Debug.LogError($"[MON-ATTACK-REACH] target-action-only {monster.Name}#{monster.EntityId}->{target?.Name ?? "player"} source={source ?? "unknown"} dist={dist:F1} meleeRange={allowedRange:F1} targetRange={targetRange:F1} action=target-only-no-damage");
            TraceMonsterState(monster, "target-action-only", target, dist, allowedRange, source ?? "reach");
        }

        public bool HasPendingClientVisibleMonsterAttack(uint playerEntityId)
        {
            if (playerEntityId == 0) return false;
            float nativeNow = GetNativeCombatTime();
            foreach (var monster in _activeMonsters.Values)
            {
                if (monster == null) continue;
                if (!monster.IsAlive) continue;
                bool targetsPlayer = monster.TargetId == playerEntityId;
                bool contactsPlayer = monster.CombatContactTargetId == playerEntityId && nativeNow <= monster.CombatContactUntil;
                if (!_players.TryGetValue(playerEntityId, out var player) || player == null || !player.IsAlive || player.PlayerState == null) continue;
                float dist = Distance2D(monster.PosX, monster.PosY, player.PosX, player.PosY);
                bool attackPathClear = IsMonsterAttackPathClear(monster, player, null);
                if (!attackPathClear) continue;
                float allowedRange = ResolveMonsterEffectiveAttackRange(monster);
                if (allowedRange <= 0f) continue;
                bool nativeClientContact = IsNativeClientCombatContact(monster, player, dist, allowedRange);
                bool weaponRuntimeReach = HasMonsterWeaponRuntimeReach(monster, player, dist, allowedRange, nativeClientContact);
                bool visibleCommitTolerance = monster.AttackClientVisible && AttackCommitTargetStillValid(monster, player);
                if (monster.AttackPending && !monster.AttackHitResolved && (targetsPlayer || contactsPlayer) && (weaponRuntimeReach || monster.UsePrimaryActiveSkillThisAttack || visibleCommitTolerance))
                    return true;
                if (contactsPlayer)
                    return true;
                if (!targetsPlayer || !monster.AggroTriggered) continue;
                if (weaponRuntimeReach)
                    return true;
            }
            return false;
        }

        public bool FlushPlayerHPRuntimeBeforeSync(uint playerEntityId, string source, out uint hpWire, out bool unsafeAttack, float nativeNowOverride = -1f)
        {
            hpWire = 0;
            unsafeAttack = false;
            if (playerEntityId == 0) return true;
            if (!_players.TryGetValue(playerEntityId, out var target) || target == null || target.PlayerState == null) return true;
            float nativeNow = nativeNowOverride >= 0f ? nativeNowOverride : GetNativeCombatTime();
            target.PlayerState.AdvanceClientSyncHP(nativeNow, source ?? "FlushPlayerHPRuntimeBeforeSync");
            hpWire = target.PlayerState.SynchHP;

            foreach (var monster in _activeMonsters.Values)
            {
                if (monster == null) continue;
                bool targetsPlayer = monster.TargetId == playerEntityId;
                bool contactsPlayer = monster.CombatContactTargetId == playerEntityId && nativeNow <= monster.CombatContactUntil;
                if (!monster.AttackPending || monster.AttackHitResolved || (!targetsPlayer && !contactsPlayer)) continue;
                if (target.PlayerState.IsZoneSpawnDamageImmune)
                {
                    CancelMonsterPendingAttack(monster, $"{source}-target_zone_spawn_invulnerability");
                    continue;
                }
                if (target.PlayerState.CurrentHPWire == 0 && target.PlayerState.SynchHP == 0)
                {
                    target.IsAlive = false;
                    CancelMonsterPendingAttack(monster, $"{source}-target_dead");
                    continue;
                }
                if (monster.DeathPendingClientConfirmation
                    && !(monster.AttackNativeContactOnly && monster.AttackPending && !monster.AttackHitResolved))
                {
                    CancelMonsterPendingAttack(monster, $"{source}-monster_death_pending_client_confirmation");
                    continue;
                }

                float allowedRange = ResolveMonsterEffectiveAttackRange(monster);
                if (allowedRange <= 0f)
                {
                    unsafeAttack = true;
                    continue;
                }
                float dist = Distance2D(monster.PosX, monster.PosY, target.PosX, target.PosY);
                bool attackPathClear = IsMonsterAttackPathClear(monster, target, $"{source}-target-clear");
                if (monster.AttackCommitTime <= 0f)
                {
                    if (!attackPathClear)
                    {
                        CancelMonsterPendingAttack(monster, $"{source}-world_blocked");
                        if (monster.IsAlive)
                            monster.State = MonsterState.Chase;
                        Debug.LogError($"[MON-DAMAGE-SYNC] {monster.Name}#{monster.EntityId}->{target.Name} cancel worldBlocked source={source} dist={dist:F1} range={allowedRange:F1}");
                        OnMonsterAttackResolved?.Invoke(monster, target, false, target.PlayerState.CurrentHPWire);
                        hpWire = target.PlayerState.SynchHP;
                        continue;
                    }
                    bool initUseGateway = HasMonsterInitUseGateway(monster, target);
                    if (!initUseGateway && !monster.UsePrimaryActiveSkillThisAttack)
                    {
                        CancelMonsterPendingAttack(monster, $"{source}-init_use_gateway_pending dist={dist:F1} range={allowedRange:F1} targetRange={ResolveMonsterTargetSearchRange(monster, true):F1}");
                        if (monster.IsAlive)
                            monster.State = MonsterState.Chase;
                        continue;
                    }
                    if (GetRoomRngForMonster(monster) == null)
                    {
                        unsafeAttack = true;
                        continue;
                    }
                    ArmMonsterRuntimeAttack(monster, target, "HP-SYNC-ARM", nativeNow);
                }
                if (GetRoomRngForMonster(monster) == null)
                {
                    unsafeAttack = true;
                    continue;
                }
                if (monster.AttackSoundPending && nativeNow >= monster.AttackSoundTime)
                    ConsumeMonsterAttackSoundRng(monster);
                if (nativeNow < monster.AttackCommitTime)
                {
                    hpWire = target.PlayerState.SynchHP;
                    unsafeAttack = true;
                    Debug.LogError($"[PLAYER-HP-NATIVE-PENDING] {source} keeping current HP before native hit frame {monster.Name}#{monster.EntityId}->{target.Name} now={nativeNow:F3} commit={monster.AttackCommitTime:F3} hp={hpWire / 256f:F2}");
                    continue;
                }
                if (monster.AttackClientVisible && LastCompletedNativeEntityUpdateTime < monster.AttackClientVisibleTime)
                {
                    hpWire = target.PlayerState.SynchHP;
                    unsafeAttack = true;
                    Debug.LogError($"[PLAYER-HP-CLIENT-PENDING] {source} holding damage commit until client processes attack {monster.Name}#{monster.EntityId}->{target.Name} now={nativeNow:F3} visibleAt={monster.AttackClientVisibleTime:F3} lastEntityUpdate={LastCompletedNativeEntityUpdateTime:F3} hp={hpWire / 256f:F2}");
                    continue;
                }
                if (monster.AttackSoundPending)
                    ConsumeMonsterAttackSoundRng(monster);
                monster.AttackHitResolved = true;
                if (monster.AttackEndTime < nativeNow)
                    monster.AttackEndTime = nativeNow;

                if (!TryDeferMonsterProjectileImpact(monster, target, dist, "MON-DAMAGE-SYNC", source, nativeNow))
                    ResolveMonsterAttackDamage(monster, target, dist, "MON-DAMAGE-SYNC", source);
                monster.AttackPending = false;
                monster.AttackClientVisible = false;
                monster.AttackNativeContactOnly = false;
                monster.AttackSoundPending = false;
                monster.AttackHitResolved = false;
                monster.AttackStartedTime = 0f;
                monster.AttackEndTime = 0f;
                monster.AttackCommitTime = 0f;
                monster.AttackSoundTime = 0f;
                if (monster.IsAlive)
                    monster.State = MonsterState.Combat;
                hpWire = target.PlayerState.SynchHP;
            }

            return !unsafeAttack;
        }

        private bool AttackCommitTargetStillValid(Monster monster, CombatPlayer target)
        {
            if (monster == null || target == null) return false;
            float tolerance = monster.ClientSyncTolerance > 0f ? monster.ClientSyncTolerance : 10f;
            return Distance2D(monster.AttackCommitTargetX, monster.AttackCommitTargetY, target.PosX, target.PosY) <= tolerance;
        }

        private bool HasCombatContact(Monster monster, CombatPlayer target)
        {
            return monster != null && target != null && monster.CombatContactTargetId == target.EntityId && GetNativeCombatTime() <= monster.CombatContactUntil;
        }

        private float ResolveMonsterDamageTable(byte level)
        {
            return GCDatabase.Instance.RequireCurveValue("MonsterDamage", Mathf.Clamp(level, 1, 110));
        }

        private int ResolveMonsterAttackRating(Monster monster)
        {
            return DamageComputer.ResolveNativeMonsterAttackRating(monster);
        }

        private static uint ApplyDamageTakenModWire(uint damageWire, float damageTakenMod)
        {
            if (damageWire == 0) return 0;
            if (damageTakenMod < 1f) return 0;
            double scaled = damageWire * (double)damageTakenMod / 100.0;
            if (scaled >= uint.MaxValue) return uint.MaxValue;
            return (uint)scaled;
        }

        private static float ResolveNativeMonsterDamageTypeTakenMod(Monster monster, int damageTypeId)
        {
            if (monster == null) return 100f;
            return damageTypeId switch
            {
                3 => monster.FireDamageTakenMod,
                4 => monster.IceDamageTakenMod,
                5 => monster.PoisonDamageTakenMod,
                6 => monster.ShadowDamageTakenMod,
                7 => monster.DivineDamageTakenMod,
                _ => 100f
            };
        }

        private sealed class NativeDamageQueryResult
        {
            public uint AdjustedDamageWire;
            public float DamageTakenMod = 100f;
            public float DamageTypeMod = 100f;
            public string ResultName = "NONE";
            public uint ResistRaw;
            public int ResistChanceWire;
        }

        private enum NativeDamageResistResult
        {
            None = 0,
            Immune = 1,
            Resisted = 2,
            Vulnerable = 3
        }

        private static NativeDamageQueryResult ApplyNativeDamageQueryWire(uint damageWire, Monster monster, int damageTypeId, byte damageKind, float nativeDamageTime, string source, int attackerLevel)
        {
            var result = new NativeDamageQueryResult
            {
                AdjustedDamageWire = damageWire,
                DamageTakenMod = monster != null ? monster.DamageTakenMod : 100f,
                DamageTypeMod = ResolveNativeMonsterDamageTypeTakenMod(monster, damageTypeId),
                ResultName = "NONE"
            };

            result.AdjustedDamageWire = ApplyDamageTakenModWire(result.AdjustedDamageWire, result.DamageTakenMod);
            if (damageTypeId >= 3 && damageTypeId <= 7 && result.DamageTypeMod != 100f)
            {
                if (result.DamageTypeMod < 1f)
                {
                    result.AdjustedDamageWire = 0;
                    result.ResultName = "TYPE_ABSORB";
                    return result;
                }
                result.AdjustedDamageWire = ApplyDamageTakenModWire(result.AdjustedDamageWire, result.DamageTypeMod);
            }

            if (result.AdjustedDamageWire == 0 || monster == null)
                return result;

            NativeDamageResistResult resist = CheckNativeMonsterDamageResist(monster, damageKind, damageTypeId, 0x100, attackerLevel, out int resistChanceWire, out uint resistRaw, source);
            result.ResistChanceWire = resistChanceWire;
            result.ResistRaw = resistRaw;
            result.ResultName = resist.ToString().ToUpperInvariant();
            if (resist == NativeDamageResistResult.Immune || resist == NativeDamageResistResult.Resisted)
            {
                result.AdjustedDamageWire = 0;
                return result;
            }
            if (resist == NativeDamageResistResult.Vulnerable)
                result.AdjustedDamageWire = ClampWireAdd(result.AdjustedDamageWire, result.AdjustedDamageWire);
            return result;
        }

        private static NativeDamageResistResult CheckNativeMonsterDamageResist(Monster monster, byte damageKind, int damageTypeId, int fixedScale, int attackerLevel, out int resistChanceWire, out uint resistRaw, string source)
        {
            resistChanceWire = 0;
            resistRaw = 0;
            if (monster?.NativeSlots == null)
                return NativeDamageResistResult.None;

            if (monster.NativeSlots.Get(NativeUnitSlot.DamageImmunity) > 0)
            {
                resistChanceWire = 0x6400;
                Debug.LogError($"[DAMAGE-RESIST] target={monster.Name}#{monster.EntityId} result=IMMUNE chance=100.00 raw=0x00000000 damageKind={damageKind} damageTypeId={damageTypeId} source={source ?? "unknown"} native=Unit::CheckDamageResist@0x0050B660");
                return NativeDamageResistResult.Immune;
            }

            int resistWire = monster.NativeSlots.Get(NativeUnitSlot.DamageResist) * 0x100;
            if (damageKind == 3)
                resistWire += monster.NativeSlots.Get(NativeUnitSlot.MagicResist) * 0x100;
            resistWire += ResolveNativeMonsterDamageTypeResist(monster, damageTypeId) * 0x100;

            long scaledResist = ((long)resistWire * fixedScale) >> 8;
            attackerLevel = Math.Max(1, attackerLevel);
            int denom = Math.Max(1, attackerLevel * 0x0D00);
            long q = (scaledResist << 8) / denom;
            resistChanceWire = (int)((q * 0x4B00L) >> 8);
            if (resistChanceWire > 0x6400) resistChanceWire = 0x6400;
            if (resistChanceWire < -0x6400) resistChanceWire = -0x6400;
            if (resistChanceWire >= 0x6400)
                return NativeDamageResistResult.Immune;

            MersenneTwister rng = monster.Rng;
            if (rng == null)
            {
                Debug.LogError($"[DAMAGE-RESIST] target={monster.Name}#{monster.EntityId} result=BLOCKED chance={resistChanceWire / 256f:F2} raw=0x00000000 damageKind={damageKind} damageTypeId={damageTypeId} source={source ?? "unknown"} reason=missing-unit-owned-rng native=Unit::CheckDamageResist@0x0050B660");
                return NativeDamageResistResult.None;
            }

            resistRaw = NativeRngLedger.Generate(rng, "unitOwnedCombat", "Unit::CheckDamageResist", $"{monster.Name}#{monster.EntityId}");
            int roll = (int)(resistRaw % 0x6400);
            NativeDamageResistResult result;
            if (resistChanceWire < 0)
                result = roll < -resistChanceWire ? NativeDamageResistResult.Vulnerable : NativeDamageResistResult.None;
            else
                result = roll < resistChanceWire ? NativeDamageResistResult.Resisted : NativeDamageResistResult.None;
            Debug.LogError($"[DAMAGE-RESIST] target={monster.Name}#{monster.EntityId} result={result.ToString().ToUpperInvariant()} chance={resistChanceWire / 256f:F2} roll={roll / 256f:F2} raw=0x{resistRaw:X8} damageKind={damageKind} damageTypeId={damageTypeId} source={source ?? "unknown"} rngPos={rng.CallsSinceReseed} native=Unit::CheckDamageResist@0x0050B660");
            return result;
        }

        private static int ResolveNativeMonsterDamageTypeResist(Monster monster, int damageTypeId)
        {
            if (monster?.NativeSlots == null) return 0;
            return damageTypeId switch
            {
                0 => monster.NativeSlots.Get(NativeUnitSlot.CrushingResist),
                1 => monster.NativeSlots.Get(NativeUnitSlot.PiercingResist),
                2 => monster.NativeSlots.Get(NativeUnitSlot.SlashingResist),
                3 => monster.NativeSlots.Get(NativeUnitSlot.FireResist),
                4 => monster.NativeSlots.Get(NativeUnitSlot.IceResist),
                5 => monster.NativeSlots.Get(NativeUnitSlot.PoisonResist),
                6 => monster.NativeSlots.Get(NativeUnitSlot.ShadowResist),
                7 => monster.NativeSlots.Get(NativeUnitSlot.DivineResist),
                _ => 0
            };
        }


        private static NativeDamageQueryResult ApplyNativePlayerDamageQueryWire(uint damageWire, CombatPlayer target, Monster attacker, int damageTypeId, byte damageKind, float nativeDamageTime, string source, int attackerLevel, MersenneTwister rng)
        {
            PlayerState state = target?.PlayerState;
            var result = new NativeDamageQueryResult
            {
                AdjustedDamageWire = damageWire,
                DamageTakenMod = state != null ? state.DamageTakenMod : 100f,
                DamageTypeMod = ResolveNativePlayerDamageTypeTakenMod(state, damageTypeId),
                ResultName = "NONE"
            };

            if (state != null && state.HasAnyDamageImmunity)
            {
                result.AdjustedDamageWire = 0;
                result.ResultName = "IMMUNE";
                result.ResistChanceWire = 0x6400;
                Debug.LogError($"[DAMAGE-RESIST] target={target.Name}#{target.EntityId} result=IMMUNE chance=100.00 raw=0x00000000 damageKind={damageKind} damageTypeId={damageTypeId} source={source ?? "unknown"} native=Unit::CheckDamageResist@0x0050B660 target=Avatar");
                return result;
            }

            result.AdjustedDamageWire = ApplyDamageTakenModWire(result.AdjustedDamageWire, result.DamageTakenMod);
            if (damageTypeId >= 3 && damageTypeId <= 7 && result.DamageTypeMod != 100f)
            {
                if (result.DamageTypeMod < 1f)
                {
                    result.AdjustedDamageWire = 0;
                    result.ResultName = "TYPE_ABSORB";
                    return result;
                }
                result.AdjustedDamageWire = ApplyDamageTakenModWire(result.AdjustedDamageWire, result.DamageTypeMod);
            }

            if (result.AdjustedDamageWire == 0 || target == null)
                return result;

            NativeDamageResistResult resist = CheckNativePlayerDamageResist(target, attacker, damageKind, damageTypeId, 0x100, attackerLevel, rng, out int resistChanceWire, out uint resistRaw, source);
            result.ResistChanceWire = resistChanceWire;
            result.ResistRaw = resistRaw;
            result.ResultName = resist.ToString().ToUpperInvariant();
            if (resist == NativeDamageResistResult.Immune || resist == NativeDamageResistResult.Resisted)
            {
                result.AdjustedDamageWire = 0;
                return result;
            }
            if (resist == NativeDamageResistResult.Vulnerable)
                result.AdjustedDamageWire = ClampWireAdd(result.AdjustedDamageWire, result.AdjustedDamageWire);
            return result;
        }

        private static NativeDamageResistResult CheckNativePlayerDamageResist(CombatPlayer target, Monster attacker, byte damageKind, int damageTypeId, int fixedScale, int attackerLevel, MersenneTwister rng, out int resistChanceWire, out uint resistRaw, string source)
        {
            resistChanceWire = 0;
            resistRaw = 0;
            PlayerState state = target?.PlayerState;
            if (state == null)
                return NativeDamageResistResult.None;

            int resistWire = GetEquipmentStatSum(state, "DAMAGE_RESIST", "DAMAGERESIST") * 0x100;
            if (damageKind == 3)
                resistWire += GetEquipmentStatSum(state, "MAGIC_DAMAGE_RESIST", "MAGIC_RESIST", "MAGICDAMAGERESIST", "MAGICRESIST") * 0x100;
            resistWire += ResolveNativePlayerDamageTypeResist(state, damageTypeId) * 0x100;

            long scaledResist = ((long)resistWire * fixedScale) >> 8;
            attackerLevel = Math.Max(1, attackerLevel);
            int denom = Math.Max(1, attackerLevel * 0x0D00);
            long q = (scaledResist << 8) / denom;
            resistChanceWire = (int)((q * 0x4B00L) >> 8);
            if (resistChanceWire > 0x5A00) resistChanceWire = 0x5A00;
            if (resistChanceWire < -0x6400) resistChanceWire = -0x6400;

            if (rng == null)
            {
                Debug.LogError($"[DAMAGE-RESIST] target={target.Name}#{target.EntityId} result=BLOCKED chance={resistChanceWire / 256f:F2} raw=0x00000000 damageKind={damageKind} damageTypeId={damageTypeId} source={source ?? "unknown"} reason=missing-world-rng native=Unit::CheckDamageResist@0x0050B660 target=Avatar attacker={attacker?.Name ?? "unknown"}#{attacker?.EntityId ?? 0}");
                return NativeDamageResistResult.None;
            }

            resistRaw = NativeRngLedger.Generate(rng, "unitOwnedCombat", "Unit::CheckDamageResist", $"{target.Name}#{target.EntityId}");
            int roll = (int)(resistRaw % 0x6400);
            NativeDamageResistResult result;
            if (resistChanceWire < 0)
                result = roll < -resistChanceWire ? NativeDamageResistResult.Vulnerable : NativeDamageResistResult.None;
            else
                result = roll < resistChanceWire ? NativeDamageResistResult.Resisted : NativeDamageResistResult.None;
            Debug.LogError($"[DAMAGE-RESIST] target={target.Name}#{target.EntityId} result={result.ToString().ToUpperInvariant()} chance={resistChanceWire / 256f:F2} roll={roll / 256f:F2} raw=0x{resistRaw:X8} damageKind={damageKind} damageTypeId={damageTypeId} source={source ?? "unknown"} rngPos={rng.CallsSinceReseed} native=Unit::CheckDamageResist@0x0050B660 target=Avatar attacker={attacker?.Name ?? "unknown"}#{attacker?.EntityId ?? 0}");
            return result;
        }

        private static int ResolveNativePlayerDamageTypeResist(PlayerState state, int damageTypeId)
        {
            return damageTypeId switch
            {
                0 => GetEquipmentStatSum(state, "CRUSHING_DAMAGE_RESIST", "CRUSHINGDAMAGERESIST"),
                1 => GetEquipmentStatSum(state, "PIERCING_DAMAGE_RESIST", "PIERCINGDAMAGERESIST"),
                2 => GetEquipmentStatSum(state, "SLASHING_DAMAGE_RESIST", "SLASHINGDAMAGERESIST"),
                3 => GetEquipmentStatSum(state, "FIRE_DAMAGE_RESIST", "FIREDAMAGERESIST"),
                4 => GetEquipmentStatSum(state, "ICE_DAMAGE_RESIST", "ICEDAMAGERESIST", "COLD_DAMAGE_RESIST", "COLDDAMAGERESIST"),
                5 => GetEquipmentStatSum(state, "POISON_DAMAGE_RESIST", "POISONDAMAGERESIST"),
                6 => GetEquipmentStatSum(state, "SHADOW_DAMAGE_RESIST", "SHADOWDAMAGERESIST"),
                7 => GetEquipmentStatSum(state, "DIVINE_DAMAGE_RESIST", "DIVINEDAMAGERESIST"),
                _ => 0
            };
        }

        private static float ResolveNativePlayerDamageTypeTakenMod(PlayerState state, int damageTypeId)
        {
            int mod = damageTypeId switch
            {
                3 => GetEquipmentStatSum(state, "FIRE_DAMAGE_TAKEN_MOD", "FIREDAMAGETAKENMOD"),
                4 => GetEquipmentStatSum(state, "ICE_DAMAGE_TAKEN_MOD", "ICEDAMAGETAKENMOD", "COLD_DAMAGE_TAKEN_MOD", "COLDDAMAGETAKENMOD"),
                5 => GetEquipmentStatSum(state, "POISON_DAMAGE_TAKEN_MOD", "POISONDAMAGETAKENMOD"),
                6 => GetEquipmentStatSum(state, "SHADOW_DAMAGE_TAKEN_MOD", "SHADOWDAMAGETAKENMOD"),
                7 => GetEquipmentStatSum(state, "DIVINE_DAMAGE_TAKEN_MOD", "DIVINEDAMAGETAKENMOD"),
                _ => 0
            };
            return 100f + mod;
        }

        private static int NativeDamageResistLogCode(NativeDamageQueryResult query)
        {
            if (query == null || query.ResultName == "NONE") return 0;
            return query.ResultName == "VULNERABLE" ? 3 : 1;
        }

        private static int GetEquipmentStatSum(PlayerState state, params string[] keys)
        {
            if (state?.EquipmentStats == null || keys == null || keys.Length == 0) return 0;
            int total = 0;
            foreach (var kvp in state.EquipmentStats)
            {
                string key = CanonicalStatName(kvp.Key);
                for (int i = 0; i < keys.Length; i++)
                {
                    if (key == CanonicalStatName(keys[i]))
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

        private int ResolveAvatarDefenseRating(PlayerState state, Monster attacker)
        {
            if (state == null) return 0;
            float defensePerStrength = GCDatabase.Instance.GetKnob("DefenseRatingPerStrength", 14f);
            int rating = Mathf.RoundToInt(state.Strength * defensePerStrength) + state.ArmorDefenseRating;
            int modifier = GetEquipmentStat(state, "DEFENSE_RATING_MOD");
            string weaponClass = ResolveMonsterWeaponClass(attacker);
            if (IsRangedWeaponClass(weaponClass))
            {
                rating += GetEquipmentStat(state, "RANGE_DEFENSE_RATING");
                modifier += GetEquipmentStat(state, "RANGE_DEFENSE_RATING_MOD");
            }
            else
            {
                rating += GetEquipmentStat(state, "MELEE_DEFENSE_RATING");
                modifier += GetEquipmentStat(state, "MELEE_DEFENSE_RATING_MOD");
            }
            return Mathf.Max(0, (int)(((long)Mathf.Max(0, rating) * (modifier + 100)) / 100));
        }

        private static int GetEquipmentStat(PlayerState state, string key)
        {
            if (state?.EquipmentStats == null || string.IsNullOrEmpty(key)) return 0;
            return state.EquipmentStats.TryGetValue(key, out int value) ? value : 0;
        }

        private static string ResolveMonsterWeaponClass(Monster monster)
        {
            if (monster?.Manipulators != null &&
                monster.Manipulators.TryGetValue("primaryweapon", out var weapon) &&
                weapon?.properties != null &&
                weapon.properties.TryGetValue("WeaponClass", out var weaponClass) &&
                !string.IsNullOrWhiteSpace(weaponClass))
                return weaponClass;

            if (!string.IsNullOrWhiteSpace(monster?.WeaponClass))
                return monster.WeaponClass;

            return monster?.BehaviourType?.IndexOf("ranged", StringComparison.OrdinalIgnoreCase) >= 0 ? "1HRANGED" : "HTH";
        }

        private static bool IsRangedWeaponClass(string weaponClass)
        {
            if (string.IsNullOrEmpty(weaponClass)) return false;
            return weaponClass.Equals("1HRANGED", StringComparison.OrdinalIgnoreCase)
                || weaponClass.Equals("2HRANGED", StringComparison.OrdinalIgnoreCase)
                || weaponClass.Equals("2HCANNON", StringComparison.OrdinalIgnoreCase);
        }

        private int ResolveAvatarBlockChance(PlayerState state)
        {
            if (state == null || state.EquipmentStats == null) return 0;
            return state.EquipmentStats.TryGetValue("BLOCK", out int block) ? Mathf.Clamp(block, 0, 100) : 0;
        }

        private int ResolveHitThreshold(int attackRating, int defenseRating, int attackerLevel, int defenderLevel)
        {
            return DamageComputer.ResolveNativeHitThreshold(attackRating, defenseRating, attackerLevel, defenderLevel);
        }

        private uint ResolveMonsterDamageWire(Monster monster, uint damageRaw, out int minDamage, out int maxDamage, out int averageDamage)
        {
            float baseDamage = ResolveMonsterDamageTable(monster.Level);
            float damageMod = ResolveMonsterDamageModifier(monster);
            float weaponScale = monster.WeaponDamage > 0f ? monster.WeaponDamage : 1f;
            float damage = baseDamage * damageMod * weaponScale;
            int normalized = Mathf.RoundToInt(Mathf.Max(1f, damage) * 256f);
            if (normalized < 0x100) normalized = 0x100;

            int volatility = DamageComputer.FromFloat(Mathf.Clamp(monster.DamageVolatility, 0f, 0.95f));
            int spread = DamageComputer.FixedMul(normalized, volatility);
            minDamage = DamageComputer.RoundFixed32(normalized - spread);
            maxDamage = DamageComputer.RoundFixed32(normalized + spread);
            if (minDamage < 0x100) minDamage = 0x100;
            if (maxDamage < 0x100) maxDamage = 0x100;
            if (maxDamage < minDamage) maxDamage = minDamage;

            averageDamage = normalized;
            return (uint)DamageComputer.RollDamageRange(minDamage, maxDamage, damageRaw);
        }

        private NativeWeaponDamageInput CreateMonsterNativeWeaponDamageInput(Monster monster, CombatPlayer target, MersenneTwister rng, string source)
        {
            int attackerLevel = monster != null ? Mathf.Clamp(monster.Level, 0, 110) : 1;
            int defenderLevel = target?.PlayerState != null ? Mathf.Clamp(target.PlayerState.Level, 0, 110) : attackerLevel;
            float baseDamage = monster != null ? ResolveMonsterDamageTable(monster.Level) : 1f;
            float damageMod = monster != null ? ResolveMonsterDamageModifier(monster) : 1f;
            float weaponScale = monster != null && monster.WeaponDamage > 0f ? monster.WeaponDamage : 1f;
            float volatility = monster != null ? Mathf.Clamp(monster.DamageVolatility, 0f, 0.95f) : 0.5f;

            return new NativeWeaponDamageInput
            {
                Rng = rng,
                Source = source,
                AttackerLevel = attackerLevel,
                DefenderLevel = defenderLevel,
                AttackRating = ResolveMonsterAttackRating(monster),
                DefenseRating = ResolveAvatarDefenseRating(target?.PlayerState, monster),
                BlockChance = ResolveAvatarBlockChance(target?.PlayerState),
                DamageLevel = Mathf.Max(1, Mathf.RoundToInt(baseDamage)),
                DamageBonus = 0,
                DamageMod = Mathf.Max(0, Mathf.RoundToInt(damageMod * 100f)),
                WeaponClassId = monster != null ? monster.NativeWeaponClassId : 1,
                DamageTypeId = monster != null ? monster.NativeDamageTypeId : 0,
                WeaponDamageF32 = DamageComputer.NativeFixed32FromAuthoredDecimal(weaponScale),
                WeaponVolatilityF32 = DamageComputer.NativeFixed32FromAuthoredDecimal(volatility),
                CritThreshold = DamageComputer.ResolveNativeMonsterCriticalThreshold(monster, target?.PlayerState),
                CritDamagePercent = 200
            };
        }

        private bool MonsterUsesProjectileWeapon(Monster monster)
        {
            return monster != null && monster.WeaponUsesProjectile && monster.WeaponProjectileSpeed > 0f;
        }

        private static int NativeProjectileFlightTicksFixed(float rangeUnits, float projectileSpeed)
        {
            int rangeFixed16 = Mathf.Max(0, Mathf.RoundToInt(Mathf.Max(0f, rangeUnits) * 65536f));
            int speedByte = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(0f, projectileSpeed)), 1, 255);
            long stepDenominator = ((long)speedByte << 16) / NATIVE_PROJECTILE_SPEED_FIXED_DENOMINATOR;
            if (stepDenominator <= 0) stepDenominator = 1;
            long flight = ((long)rangeFixed16) / stepDenominator;
            int flightTicks = (int)(flight >> 8);
            return Math.Max(1, flightTicks);
        }

        private bool TryDeferMonsterProjectileImpact(Monster monster, CombatPlayer target, float dist, string marker, string source, float nativeNow)
        {
            if (!MonsterUsesProjectileWeapon(monster) || target?.PlayerState == null)
                return false;

            TryGetMonsterClientVisiblePosition(monster, nativeNow, out float startX, out float startY);
            float targetX = target.PosX;
            float targetY = target.PosY;
            float pathDistance = Distance2D(startX, startY, targetX, targetY);
            float weaponRange = monster.WeaponRange > 0f ? monster.WeaponRange : Mathf.Max(0f, ResolveMonsterEffectiveAttackRange(monster));
            float flightDistance = Mathf.Max(pathDistance, 0f);
            if (weaponRange > 0f)
                flightDistance = Mathf.Min(flightDistance, weaponRange);

            int fireTick = (int)ResolveNativeTickForTime(nativeNow);
            int flightTicks = NativeProjectileFlightTicksFixed(flightDistance, monster.WeaponProjectileSpeed);
            int firstCollisionTick = fireTick + 1;
            int dueTick = Math.Max(firstCollisionTick, fireTick + flightTicks);
            float dueTime = dueTick * NATIVE_UNIT_TICK_INTERVAL;

            var pending = new PendingMonsterProjectileImpact
            {
                Sequence = ++_nextMonsterProjectileSequence,
                MonsterEntityId = monster.EntityId,
                TargetEntityId = target.EntityId,
                Marker = marker,
                Source = source,
                Dist = dist,
                FireTick = fireTick,
                FlightTicks = flightTicks,
                DueTick = dueTick,
                DueTime = dueTime
            };
            _pendingMonsterProjectiles.Add(pending);

            Debug.LogError($"[MON-PROJECTILE] {monster.Name}#{monster.EntityId}->{target.Name} schedule seq={pending.Sequence} marker={marker} source={source} fireTick={fireTick} firstCollisionTick={firstCollisionTick} flightTicks={flightTicks} dueTick={dueTick} dueTime={dueTime:F3} dist={dist:F1} pathDist={pathDistance:F1} flightDist={flightDistance:F1} speed={monster.WeaponProjectileSpeed:F1} size={monster.WeaponProjectileSize:F1} range={weaponRange:F1} native=RangedWeapon::doHit@0x00595DD0->Projectile::init@0x005934D0 commitAtImpact=Projectile::doImpact@0x00594430->Weapon::applyDamage@0x00597E50");
            return true;
        }

        private void DrainDueMonsterProjectileImpacts(float nativeNow)
        {
            if (_pendingMonsterProjectiles.Count == 0)
                return;

            int nowTick = (int)ResolveNativeTickForTime(nativeNow);
            _pendingMonsterProjectiles.Sort((left, right) =>
            {
                int tick = left.DueTick.CompareTo(right.DueTick);
                if (tick != 0) return tick;
                return left.Sequence.CompareTo(right.Sequence);
            });

            for (int i = 0; i < _pendingMonsterProjectiles.Count;)
            {
                PendingMonsterProjectileImpact pending = _pendingMonsterProjectiles[i];
                if (pending == null)
                {
                    _pendingMonsterProjectiles.RemoveAt(i);
                    continue;
                }
                if (pending.DueTick > nowTick)
                {
                    i++;
                    continue;
                }

                _pendingMonsterProjectiles.RemoveAt(i);

                Monster monster = GetMonster(pending.MonsterEntityId);
                CombatPlayer target = GetPlayer(pending.TargetEntityId);
                if (monster == null || !monster.IsAlive || target == null || !target.IsAlive || target.PlayerState == null)
                {
                    Debug.LogError($"[MON-PROJECTILE] impact-discard seq={pending.Sequence} monster={pending.MonsterEntityId} target={pending.TargetEntityId} marker={pending.Marker} source={pending.Source} dueTick={pending.DueTick} nowTick={nowTick} reason=actor-invalid");
                    continue;
                }

                float impactTime = Mathf.Max(nativeNow, pending.DueTime);
                Debug.LogError($"[MON-PROJECTILE] {monster.Name}#{monster.EntityId}->{target.Name} impact seq={pending.Sequence} marker={pending.Marker} source={pending.Source} fireTick={pending.FireTick} flightTicks={pending.FlightTicks} dueTick={pending.DueTick} nowTick={nowTick} impactTime={impactTime:F3} native=Projectile::doImpact@0x00594430");
                ResolveMonsterAttackDamage(monster, target, pending.Dist, pending.Marker, $"{pending.Source}-projectile-impact");
            }
        }

        public void ClearMonsterProjectilesForMonster(uint monsterEntityId)
        {
            if (monsterEntityId == 0 || _pendingMonsterProjectiles.Count == 0)
                return;
            _pendingMonsterProjectiles.RemoveAll(pending => pending != null && pending.MonsterEntityId == monsterEntityId);
        }

        private void ResolveMonsterAttackDamage(Monster monster, CombatPlayer target, float dist, string marker, string source)
        {
            float nativeNow = GetNativeCombatTime();
            target.PlayerState.AdvanceClientSyncHP(nativeNow, $"{marker}-pre-damage");
            if (TryApplyMonsterActiveSkillEffect(monster, target, nativeNow, dist, marker, source, out bool activeSkillHandled, out bool activeSkillHPShifted))
            {
                target.IsAlive = target.PlayerState.CurrentHPWire > 0;
                OnMonsterAttackResolved?.Invoke(monster, target, activeSkillHPShifted, target.PlayerState.CurrentHPWire);
                return;
            }

            NativeRoomRuntime runtime = GetRoomRuntimeForMonster(monster);
            if (runtime == null)
            {
                Debug.LogError($"[MON-DAMAGE-SYNC] {monster?.Name ?? "monster"}#{monster?.EntityId ?? 0} missing room runtime source={source ?? "unknown"}");
                return;
            }
            MersenneTwister rng = runtime.RoomRng;
            if (rng == null)
            {
                Debug.LogError($"[MON-DAMAGE-SYNC] {monster?.Name ?? "monster"}#{monster?.EntityId ?? 0} missing room RNG instance='{runtime.InstanceKey}' source={source ?? "unknown"}");
                return;
            }

            NativeWeaponDamageInput damageInput = CreateMonsterNativeWeaponDamageInput(monster, target, rng, source);
            NativeWeaponDamageResult damageResult = DamageComputer.ResolveNativeWeaponDamage(damageInput);
            int attackRating = damageResult.AttackRating;
            int defenseRating = damageResult.DefenseRating;
            int defenderLevel = damageResult.DefenderLevel;
            int attackerLevel = damageResult.AttackerLevel;
            int hitThreshold = damageResult.HitThreshold;
            float hitChance = hitThreshold / 256f;
            uint hitRaw = damageResult.HitRaw;
            uint blockRaw = damageResult.BlockRaw;
            int hitRoll = damageResult.HitRoll;
            int blockRoll = damageResult.BlockRoll;
            int blockChance = damageResult.BlockChance;
            bool hit = damageResult.IsHit;
            bool blocked = damageResult.IsBlocked;
            if (hit && !blocked)
            {
                uint damageRaw = damageResult.DamageRaw;
                uint damageWire = damageResult.DamageWire;
                int minDamage = damageResult.MinDamageF32;
                int maxDamage = damageResult.MaxDamageF32;
                int averageDamage = (minDamage + maxDamage) / 2;
                uint currentHPWire = target.PlayerState.CurrentHPWire;
                NativeDamageQueryResult query = ApplyNativePlayerDamageQueryWire(damageWire, target, monster, damageResult.DamageTypeId, 0, nativeNow, source ?? marker, attackerLevel, rng);
                uint adjustedDamageWire = query.AdjustedDamageWire;
                if (adjustedDamageWire == 0)
                {
                    uint sameHPWire = target.PlayerState.CurrentHPWire;
                    target.IsAlive = sameHPWire > 0;
                    Debug.LogError($"[{marker}] {monster.Name}#{monster.EntityId}->{target.Name} no-damage source={source} dmg={damageWire / 256f:F2} adjusted=0 hp={sameHPWire / 256f:F2}/{target.PlayerState.MaxHPWire / 256f:F2} range=[{minDamage / 256f:F2},{maxDamage / 256f:F2}] avg={averageDamage / 256f:F2} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{defenderLevel} chance={hitChance:F1} rngPos={rng.CallsSinceReseed} anim={monster.AttackAnimationIndex} use=0x{monster.AttackUseRaw:X8} sound=0x{monster.AttackSoundRaw:X8} soundGate=0x{monster.AttackSoundGateRaw:X8} soundRepeat=0x{monster.AttackSoundRepeatRaw:X8} hit=0x{hitRaw:X8}/{hitRoll} block=0x{blockRaw:X8}/{blockRoll}/{blockChance} dmgRaw=0x{damageRaw:X8} dist={dist:F1} result={query.ResultName}");
                    Debug.LogError($"[NATIVE-DAMAGE-CONTRACT] source={source ?? marker ?? "monster"} target={target.Name}#{target.EntityId} weaponClassId={damageResult.WeaponClassId} damageTypeId={damageResult.DamageTypeId} rawRollWire={damageWire} preQueryWire={damageWire} damageTakenMod={query.DamageTakenMod:F2} typeMod={query.DamageTypeMod:F2} postQueryWire=0 hp={currentHPWire}->{sameHPWire}/{target.PlayerState.MaxHPWire} resist={query.ResultName} resistRaw=0x{query.ResistRaw:X8} resistChance={query.ResistChanceWire / 256f:F2} nativeDamageTime={nativeNow:F3} nativeTick=0 target=Avatar");
                    Debug.LogError($"[PLAYER-DAMAGE] source=monster attacker={monster.Name}#{monster.EntityId} target={target.Name}#{target.EntityId} result={query.ResultName} damageWire=0 preQueryWire={damageWire} hp={sameHPWire}->{sameHPWire}/{target.PlayerState.MaxHPWire} marker={marker} source={source} hitRaw=0x{hitRaw:X8} blockRaw=0x{blockRaw:X8} damageRaw=0x{damageRaw:X8} rngSeed=0x{runtime.Seed:X8} rngPos={rng.CallsSinceReseed}");
                    Debug.LogError($"[COMBAT-EVENT] actor=monster actorId={monster.EntityId} target=player targetId={target.EntityId} result={query.ResultName} damageWire=0 hp={sameHPWire}->{sameHPWire} hitRaw=0x{hitRaw:X8} hitRoll={hitRoll} threshold={hitThreshold} blockRaw=0x{blockRaw:X8} blockRoll={blockRoll} blockChance={blockChance} damageRaw=0x{damageRaw:X8} resist={NativeDamageResistLogCode(query)} rngAfter={rng.CallsSinceReseed} marker={marker} source={source}");
                    OnMonsterAttackResolved?.Invoke(monster, target, false, sameHPWire);
                }
                else
                {
                    target.PlayerState.TakeQueriedDamage(adjustedDamageWire, nativeNow);
                    uint newHPWire = target.PlayerState.CurrentHPWire;
                    target.IsAlive = newHPWire > 0;
                    uint effectRaw = ConsumeNativeOnApplyDamageEffectRng(rng, "monster", target.EntityId, target.Name, currentHPWire, newHPWire, target.PlayerState.MaxHPWire, adjustedDamageWire, source ?? marker ?? "Weapon::applyDamage");
                    if (currentHPWire > newHPWire)
                        ApplyNativeMonsterOnDamageCallback(monster, currentHPWire - newHPWire, nativeNow, source ?? marker);
                    if (newHPWire == 0)
                        RemovePlayerDamageModifiersForTarget(target.EntityId, source ?? marker ?? "monster-damage-death");
                    Debug.LogError($"[{marker}] {monster.Name}#{monster.EntityId}->{target.Name} HIT source={source} dmg={adjustedDamageWire / 256f:F2} preQuery={damageWire / 256f:F2} hp={currentHPWire / 256f:F2}->{newHPWire / 256f:F2}/{target.PlayerState.MaxHPWire / 256f:F2} range=[{minDamage / 256f:F2},{maxDamage / 256f:F2}] avg={averageDamage / 256f:F2} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{defenderLevel} chance={hitChance:F1} rngPos={rng.CallsSinceReseed} anim={monster.AttackAnimationIndex} use=0x{monster.AttackUseRaw:X8} sound=0x{monster.AttackSoundRaw:X8} soundGate=0x{monster.AttackSoundGateRaw:X8} soundRepeat=0x{monster.AttackSoundRepeatRaw:X8} hit=0x{hitRaw:X8}/{hitRoll} block=0x{blockRaw:X8}/{blockRoll}/{blockChance} dmgRaw=0x{damageRaw:X8} dist={dist:F1} result={query.ResultName}");
                    Debug.LogError($"[NATIVE-DAMAGE-CONTRACT] source={source ?? marker ?? "monster"} target={target.Name}#{target.EntityId} weaponClassId={damageResult.WeaponClassId} damageTypeId={damageResult.DamageTypeId} rawRollWire={damageWire} preQueryWire={damageWire} damageTakenMod={query.DamageTakenMod:F2} typeMod={query.DamageTypeMod:F2} postQueryWire={adjustedDamageWire} hp={currentHPWire}->{newHPWire}/{target.PlayerState.MaxHPWire} resist={query.ResultName} resistRaw=0x{query.ResistRaw:X8} resistChance={query.ResistChanceWire / 256f:F2} nativeDamageTime={nativeNow:F3} nativeTick=0 target=Avatar");
                    Debug.LogError($"[PLAYER-DAMAGE] source=monster attacker={monster.Name}#{monster.EntityId} target={target.Name}#{target.EntityId} result=HIT damageWire={adjustedDamageWire} preQueryWire={damageWire} hp={currentHPWire}->{newHPWire}/{target.PlayerState.MaxHPWire} marker={marker} source={source} hitRaw=0x{hitRaw:X8} blockRaw=0x{blockRaw:X8} damageRaw=0x{damageRaw:X8} effectRaw=0x{effectRaw:X8} rngSeed=0x{runtime.Seed:X8} rngPos={rng.CallsSinceReseed}");
                    Debug.LogError($"[COMBAT-EVENT] actor=monster actorId={monster.EntityId} target=player targetId={target.EntityId} result=HIT damageWire={adjustedDamageWire} hp={currentHPWire}->{newHPWire} hitRaw=0x{hitRaw:X8} hitRoll={hitRoll} threshold={hitThreshold} blockRaw=0x{blockRaw:X8} blockRoll={blockRoll} blockChance={blockChance} damageRaw=0x{damageRaw:X8} effectRaw=0x{effectRaw:X8} resist={NativeDamageResistLogCode(query)} rngAfter={rng.CallsSinceReseed} marker={marker} source={source}");
                    OnMonsterAttackResolved?.Invoke(monster, target, true, newHPWire);
                }
            }
            else if (blocked)
            {
                Debug.LogError($"[{marker}] {monster.Name}#{monster.EntityId}->{target.Name} block source={source} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{defenderLevel} chance={hitChance:F1} rngPos={rng.CallsSinceReseed} anim={monster.AttackAnimationIndex} use=0x{monster.AttackUseRaw:X8} sound=0x{monster.AttackSoundRaw:X8} soundGate=0x{monster.AttackSoundGateRaw:X8} soundRepeat=0x{monster.AttackSoundRepeatRaw:X8} hit=0x{hitRaw:X8}/{hitRoll} block=0x{blockRaw:X8}/{blockRoll}/{blockChance} dist={dist:F1}");
                Debug.LogError($"[PLAYER-DAMAGE] source=monster attacker={monster.Name}#{monster.EntityId} target={target.Name}#{target.EntityId} result=BLOCK damageWire=0 hp={target.PlayerState.CurrentHPWire}->{target.PlayerState.CurrentHPWire}/{target.PlayerState.MaxHPWire} marker={marker} source={source} hitRaw=0x{hitRaw:X8} blockRaw=0x{blockRaw:X8} rngSeed=0x{runtime.Seed:X8} rngPos={rng.CallsSinceReseed}");
                Debug.LogError($"[COMBAT-EVENT] actor=monster actorId={monster.EntityId} target=player targetId={target.EntityId} result=BLOCK damageWire=0 hp={target.PlayerState.CurrentHPWire}->{target.PlayerState.CurrentHPWire} hitRaw=0x{hitRaw:X8} hitRoll={hitRoll} threshold={hitThreshold} blockRaw=0x{blockRaw:X8} blockRoll={blockRoll} blockChance={blockChance} resist=0 rngAfter={rng.CallsSinceReseed} marker={marker} source={source}");
                OnMonsterAttackResolved?.Invoke(monster, target, false, target.PlayerState.CurrentHPWire);
            }
            else
            {
                Debug.LogError($"[{marker}] {monster.Name}#{monster.EntityId}->{target.Name} miss source={source} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{defenderLevel} chance={hitChance:F1} rngPos={rng.CallsSinceReseed} anim={monster.AttackAnimationIndex} use=0x{monster.AttackUseRaw:X8} sound=0x{monster.AttackSoundRaw:X8} soundGate=0x{monster.AttackSoundGateRaw:X8} soundRepeat=0x{monster.AttackSoundRepeatRaw:X8} hit=0x{hitRaw:X8}/{hitRoll} threshold={hitThreshold} block=0x{blockRaw:X8}/{blockRoll}/{blockChance} dist={dist:F1}");
                Debug.LogError($"[PLAYER-DAMAGE] source=monster attacker={monster.Name}#{monster.EntityId} target={target.Name}#{target.EntityId} result=MISS damageWire=0 hp={target.PlayerState.CurrentHPWire}->{target.PlayerState.CurrentHPWire}/{target.PlayerState.MaxHPWire} marker={marker} source={source} hitRaw=0x{hitRaw:X8} blockRaw=0x{blockRaw:X8} rngSeed=0x{runtime.Seed:X8} rngPos={rng.CallsSinceReseed}");
                Debug.LogError($"[COMBAT-EVENT] actor=monster actorId={monster.EntityId} target=player targetId={target.EntityId} result=MISS damageWire=0 hp={target.PlayerState.CurrentHPWire}->{target.PlayerState.CurrentHPWire} hitRaw=0x{hitRaw:X8} hitRoll={hitRoll} threshold={hitThreshold} blockRaw=0x{blockRaw:X8} blockRoll={blockRoll} blockChance={blockChance} resist=0 rngAfter={rng.CallsSinceReseed} marker={marker} source={source}");
                OnMonsterAttackResolved?.Invoke(monster, target, false, target.PlayerState.CurrentHPWire);
            }
        }

        private bool TryApplyMonsterActiveSkillEffect(Monster monster, CombatPlayer target, float nativeNow, float dist, string marker, string source, out bool handled, out bool hpShifted)
        {
            handled = false;
            hpShifted = false;
            if (monster == null || target?.PlayerState == null || !monster.UsePrimaryActiveSkillThisAttack)
                return false;

            if (!TryResolveMonsterHitPointRegenSkillEffect(monster, out MonsterHitPointRegenSkillEffect effect))
            {
                if (TryResolveMonsterWeaponDamageSkillEffect(monster, out MonsterWeaponDamageSkillEffect weaponEffect))
                {
                    handled = true;
                    CommitMonsterPrimarySkillUse(monster, $"{marker ?? "MON-SKILL"}:ActiveSkill::use@0x00538DD0");
                    hpShifted = ApplyMonsterWeaponDamageSkillEffect(monster, target, weaponEffect, nativeNow, dist, marker, source);
                    return true;
                }

                if (TryResolveMonsterStunActionSkillEffect(monster, out MonsterStunActionSkillEffect stunEffect))
                {
                    handled = true;
                    CommitMonsterPrimarySkillUse(monster, $"{marker ?? "MON-SKILL"}:ActiveSkill::use@0x00538DD0");
                    ApplyMonsterStunActionSkillEffect(monster, target, stunEffect, marker, source);
                    return true;
                }

                handled = true;
                CommitMonsterPrimarySkillUse(monster, $"{marker ?? "MON-SKILL"}:ActiveSkill::use@0x00538DD0");
                MonsterSkillEffectSupport support = ResolveMonsterSkillEffectSupport(monster.PrimaryActiveSkillPath, monster.PrimaryActiveSkillEffect);
                string families = support != null && support.Families.Count > 0 ? string.Join(",", support.Families) : "none";
                string unsupported = support != null && support.UnsupportedFamilies.Count > 0 ? string.Join(",", support.UnsupportedFamilies) : "none";
                Debug.LogError($"[MON-SKILL-EFFECT] partial {monster.Name}#{monster.EntityId}->{target.Name} skill={monster.PrimaryActiveSkillPath ?? "none"} effect={monster.PrimaryActiveSkillEffect ?? "none"} families={families} status={support?.Status ?? "UNKNOWN"} unsupported={unsupported} source={source ?? marker ?? "unknown"} reason=unsupported-effect basicFallback=False native=ActiveSkill::doSkillEffect@0x00539630");
                return true;
            }

            handled = true;
            CommitMonsterPrimarySkillUse(monster, $"{marker ?? "MON-SKILL"}:ActiveSkill::use@0x00538DD0");
            uint beforeHP = target.PlayerState.CurrentHPWire;
            uint powerLevel = ResolveMonsterSkillPowerLevelWire(monster, effect.SkillPath);
            string modifierKey = BuildPlayerRuntimeModifierKey(target.EntityId, monster.EntityId, effect.ModifierPath, effect.StackRule, NATIVE_RUNTIME_MODIFIER_SOURCE_IS_SELF);
            bool replaceModifier = _playerModifierNetworkIds.ContainsKey(modifierKey);
            bool stackAccepted = target.PlayerState.ShouldAcceptNativeAttributeModifier(modifierKey, effect.ModifierPath, effect.StackRule, powerLevel, effect.DurationTicks);
            if (!stackAccepted)
            {
                RaisePlayerModifierAdd(monster, target, modifierKey, effect.ModifierPath, 1, powerLevel, effect.DurationTicks, NATIVE_RUNTIME_MODIFIER_SOURCE_IS_SELF, replaceModifier, effect.SkillPath, effect.EffectPath, source ?? marker ?? "unknown", "SpellModEffect::doEffect@0x00554460 Modifiers::processAddModifier@0x00502280", false);
                uint rejectedHP = target.PlayerState.CurrentHPWire;
                Debug.LogError($"[PLAYER-MODIFIER-STACK] result=reject target={target.Name}#{target.EntityId} source={monster.Name}#{monster.EntityId} skill={effect.SkillPath} modifier={effect.ModifierPath} stack={effect.StackRule ?? "UNKNOWN"} key={modifierKey} incomingPower={powerLevel} incomingDuration={effect.DurationTicks} sourceIsSelf={NATIVE_RUNTIME_MODIFIER_SOURCE_IS_SELF} native=Modifiers::addModifierLocal@0x00501770");
                Debug.LogError($"[SPELL-MOD] skill={effect.SkillPath} effect={effect.EffectPath} modifier={effect.ModifierPath} attr=HIT_POINT_REGEN_BONUS value={effect.HitPointRegenBonus} durationSeconds={effect.DurationSeconds:F2} durationTicks={effect.DurationTicks} applied=False hp={beforeHP}->{rejectedHP}/{target.PlayerState.MaxHPWire} source={source ?? marker ?? "unknown"} native=SpellModEffect::doEffect@0x00554460 chanceDraw=false stack={effect.StackRule ?? ""} stackAccepted=False");
                return true;
            }
            bool applied = target.PlayerState.ApplyNativeHitPointRegenBonusModifier(
                effect.ModifierPath,
                effect.HitPointRegenBonus,
                effect.DurationTicks,
                effect.RemoveOnDeath,
                nativeNow,
                $"{marker ?? "MON-SKILL"}:{effect.SkillPath}",
                modifierKey,
                monster.EntityId,
                effect.SkillPath,
                effect.EffectPath,
                powerLevel,
                effect.StackRule);
            uint afterHP = target.PlayerState.CurrentHPWire;
            hpShifted = afterHP != beforeHP;
            if (applied)
                RaisePlayerModifierAdd(monster, target, modifierKey, effect.ModifierPath, 1, powerLevel, effect.DurationTicks, NATIVE_RUNTIME_MODIFIER_SOURCE_IS_SELF, replaceModifier, effect.SkillPath, effect.EffectPath, source ?? marker ?? "unknown", "SpellModEffect::doEffect@0x00554460 Modifiers::processAddModifier@0x00502280");
            Debug.LogError($"[SPELL-MOD] skill={effect.SkillPath} effect={effect.EffectPath} modifier={effect.ModifierPath} attr=HIT_POINT_REGEN_BONUS value={effect.HitPointRegenBonus} durationSeconds={effect.DurationSeconds:F2} durationTicks={effect.DurationTicks} power={powerLevel} applied={applied} hp={beforeHP}->{afterHP}/{target.PlayerState.MaxHPWire} source={source ?? marker ?? "unknown"} native=SpellModEffect::doEffect@0x00554460 chanceDraw=false stack={effect.StackRule ?? ""} stackAccepted=True");
            return true;
        }

        private void LogMonsterActiveSkillEffectSupport(Monster monster, MonsterActiveSkillRuntime skill)
        {
            if (monster == null || skill == null || string.IsNullOrWhiteSpace(skill.Path))
                return;
            MonsterSkillEffectSupport support = ResolveMonsterSkillEffectSupport(skill.Path, skill.Effect);
            string families = support.Families.Count > 0 ? string.Join(",", support.Families) : "none";
            string unsupported = support.UnsupportedFamilies.Count > 0 ? string.Join(",", support.UnsupportedFamilies) : "none";
            Debug.LogError($"[MON-SKILL-EFFECT-SUPPORT] monster={monster.Name}#{monster.EntityId} skill={skill.Path} effect={support.EffectPath ?? skill.Effect ?? "none"} families={families} status={support.Status} unsupported={unsupported} modifier={support.ModifierPath ?? ""} attr={support.Attribute ?? ""} reason={support.Reason ?? ""} native=ActiveSkill::doSkillEffect@0x00539630 SpellModEffect::doEffect@0x00554460 SpellWeaponDamageEffect::doEffect@0x0055E460 SpellKnockBackEffect::doEffect@0x00552C80 SpellKnockDownEffect::doEffect@0x005534F0");
        }

        private MonsterSkillEffectSupport ResolveMonsterSkillEffectSupport(string skillPath, string fallbackEffectPath)
        {
            var support = new MonsterSkillEffectSupport
            {
                SkillPath = skillPath,
                EffectPath = fallbackEffectPath
            };

            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded)
            {
                support.Status = "BLOCKED";
                support.Reason = "missing-gc-database";
                return support;
            }

            GCNode skill = gc.ResolveWithInheritance(skillPath);
            GCNode skillDesc = skill?.GetChild("Description") ?? skill;
            string effectPath = skillDesc?.GetString("Effect", fallbackEffectPath) ?? fallbackEffectPath;
            support.EffectPath = effectPath;
            if (string.IsNullOrWhiteSpace(effectPath))
            {
                support.Status = "NO_EFFECT";
                support.Reason = "skill-has-no-effect-property";
                return support;
            }

            GCNode effectNode = ResolveAuthoredNodeReference(effectPath, skill);
            if (effectNode == null)
            {
                support.Status = "BLOCKED";
                support.Reason = "effect-path-unresolved";
                return support;
            }

            foreach (GCNode effectChild in EnumerateNativeEffectChildren(effectNode))
            {
                string family = NormalizeNativeEffectFamily(effectChild.Extends);
                if (string.IsNullOrWhiteSpace(family) || support.Families.Contains(family))
                    continue;
                support.Families.Add(family);
            }

            if (support.Families.Count == 0)
            {
                support.Status = "NO_EFFECT_CHILDREN";
                support.Reason = "effect-node-has-no-native-effect-children";
                return support;
            }

            bool hasSupportedHpRegenMod = TryClassifyHitPointRegenBonusModEffect(effectNode, skill, out string modifierPath, out string attribute);
            support.ModifierPath = modifierPath;
            support.Attribute = attribute;

            foreach (string family in support.Families)
            {
                if (family == "SpellSoundEffect")
                    continue;
                if (family == "SpellModEffect" && hasSupportedHpRegenMod)
                    continue;
                if (family == "SpellWeaponDamageEffect" || family == "SpellKnockBackEffect" || family == "SpellKnockDownEffect" || family == "SpellDamageEffect")
                    continue;
                support.UnsupportedFamilies.Add(family);
            }

            if (support.UnsupportedFamilies.Count > 0)
            {
                support.Status = "BLOCKED";
                support.Reason = "unsupported-native-effect-family";
            }
            else if (support.Families.Contains("SpellModEffect") && hasSupportedHpRegenMod && support.Families.All(f => f == "SpellModEffect" || f == "SpellSoundEffect"))
            {
                support.Status = "SUPPORTED_HP_REGEN_BONUS";
                support.Reason = "server-mirrors-SpellModEffect-HIT_POINT_REGEN_BONUS-runtime";
            }
            else if (support.Families.Contains("SpellWeaponDamageEffect"))
            {
                bool hasStunAction = support.Families.Contains("SpellKnockBackEffect") || support.Families.Contains("SpellKnockDownEffect");
                support.Status = hasStunAction ? "PARTIAL_WEAPON_DAMAGE_STUN_ACTION" : "PARTIAL_WEAPON_DAMAGE";
                support.Reason = hasStunAction ? "weapon-damage-hp-mirror-and-stun-action-rng-exist-but-UnitAction-replication-is-partial" : "weapon-damage-hp-mirror-exists";
            }
            else if (support.Families.Contains("SpellDamageEffect"))
            {
                support.Status = "PARTIAL_SPELL_DAMAGE";
                support.Reason = "SpellDamageEffect/DOT/native-modifier-update-timing-not-closed";
            }
            else if (support.Families.Contains("SpellKnockBackEffect") || support.Families.Contains("SpellKnockDownEffect"))
            {
                support.Status = "PARTIAL_STUN_ACTION";
                support.Reason = "server-mirrors-SpellEffect-CheckChance-and-Unit-CheckStunResist-rng-but-UnitAction-replication-is-partial";
            }
            else
            {
                support.Status = "PARTIAL";
                support.Reason = "native-effect-family-present-without-runtime-mirror";
            }

            return support;
        }

        private static IEnumerable<GCNode> EnumerateNativeEffectChildren(GCNode effectNode)
        {
            if (effectNode == null)
                yield break;
            if (!string.IsNullOrWhiteSpace(NormalizeNativeEffectFamily(effectNode.Extends)))
                yield return effectNode;
            foreach (var child in effectNode.AnonymousChildren)
                if (!string.IsNullOrWhiteSpace(NormalizeNativeEffectFamily(child.Extends)))
                    yield return child;
            foreach (var child in effectNode.Children.Values)
                if (!string.IsNullOrWhiteSpace(NormalizeNativeEffectFamily(child.Extends)))
                    yield return child;
        }

        private static string NormalizeNativeEffectFamily(string extendsRaw)
        {
            if (string.IsNullOrWhiteSpace(extendsRaw))
                return "";
            string value = extendsRaw.Replace('\\', '.').Replace('/', '.');
            int dot = value.LastIndexOf('.');
            if (dot >= 0 && dot + 1 < value.Length)
                value = value.Substring(dot + 1);
            return value.EndsWith("Effect", StringComparison.OrdinalIgnoreCase) ? value : "";
        }

        private bool TryClassifyHitPointRegenBonusModEffect(GCNode effectNode, GCNode skillContext, out string modifierPath, out string attribute)
        {
            modifierPath = null;
            attribute = null;
            GCNode modEffect = FindEffectChild(effectNode, "SpellModEffect");
            if (modEffect == null)
                return false;

            modifierPath = modEffect.GetString("Modifier", null);
            if (string.IsNullOrWhiteSpace(modifierPath))
                return false;

            GCNode modifier = ResolveAuthoredNodeReference(modifierPath, skillContext);
            GCNode modifierDesc = modifier?.GetChild("Description") ?? modifier;
            GCNode attributeNode = FindEffectChild(modifierDesc, "Attribute");
            attribute = attributeNode?.GetString("Attribute", null);
            return string.Equals(attribute, "HIT_POINT_REGEN_BONUS", StringComparison.OrdinalIgnoreCase);
        }

        private bool ApplyMonsterWeaponDamageSkillEffect(Monster monster, CombatPlayer target, MonsterWeaponDamageSkillEffect effect, float nativeNow, float dist, string marker, string source)
        {
            NativeRoomRuntime runtime = GetRoomRuntimeForMonster(monster);
            MersenneTwister rng = runtime?.RoomRng;
            if (rng == null)
            {
                Debug.LogError($"[SPELL-WEAPON-DAMAGE] partial {monster?.Name ?? "monster"}#{monster?.EntityId ?? 0}->{target?.Name ?? "unknown"} skill={effect?.SkillPath ?? "none"} source={source ?? marker ?? "unknown"} reason=missing-room-rng native=SpellWeaponDamageEffect::doEffect@0x0055E460");
                return false;
            }

            NativeWeaponDamageInput damageInput = CreateMonsterNativeWeaponDamageInput(monster, target, rng, $"{marker ?? "MON-SKILL"}:SpellWeaponDamageEffect");
            int baseAttackRating = damageInput.AttackRating;
            int baseDamageMod = damageInput.DamageMod;
            if (effect.AttackRatingMod != 0)
                damageInput.AttackRating = Math.Max(0, (damageInput.AttackRating * (100 + effect.AttackRatingMod)) / 100);
            if (effect.DamageMod != 0)
                damageInput.DamageMod = Math.Max(0, damageInput.DamageMod + effect.DamageMod);

            NativeWeaponDamageResult damageResult = DamageComputer.ResolveNativeWeaponDamage(damageInput);
            bool hpShifted = false;
            uint beforeHP = target.PlayerState.CurrentHPWire;
            if (damageResult.IsHit && !damageResult.IsBlocked)
            {
                uint damageWire = damageResult.DamageWire;
                NativeDamageQueryResult query = ApplyNativePlayerDamageQueryWire(damageWire, target, monster, damageResult.DamageTypeId, 0, nativeNow, source ?? marker ?? "SpellWeaponDamageEffect", damageResult.AttackerLevel, rng);
                uint adjustedDamageWire = query.AdjustedDamageWire;
                if (adjustedDamageWire == 0)
                {
                    uint sameHP = target.PlayerState.CurrentHPWire;
                    target.IsAlive = sameHP > 0;
                    Debug.LogError($"[SPELL-WEAPON-DAMAGE] {monster.Name}#{monster.EntityId}->{target.Name} no-damage skill={effect.SkillPath} effect={effect.WeaponEffectPath} dmg={damageWire / 256f:F2} adjusted=0 hp={sameHP}->{sameHP}/{target.PlayerState.MaxHPWire} arMod={effect.AttackRatingMod} ar={baseAttackRating}->{damageInput.AttackRating} dmgMod={baseDamageMod}->{damageInput.DamageMod} hit=0x{damageResult.HitRaw:X8}/{damageResult.HitRoll} block=0x{damageResult.BlockRaw:X8}/{damageResult.BlockRoll}/{damageResult.BlockChance} dmgRaw=0x{damageResult.DamageRaw:X8} rngPos={rng.CallsSinceReseed} result={query.ResultName} native=SpellWeaponDamageEffect::doEffect@0x0055E460");
                    Debug.LogError($"[NATIVE-DAMAGE-CONTRACT] source={source ?? marker ?? "SpellWeaponDamageEffect"} target={target.Name}#{target.EntityId} weaponClassId={damageResult.WeaponClassId} damageTypeId={damageResult.DamageTypeId} rawRollWire={damageWire} preQueryWire={damageWire} damageTakenMod={query.DamageTakenMod:F2} typeMod={query.DamageTypeMod:F2} postQueryWire=0 hp={beforeHP}->{sameHP}/{target.PlayerState.MaxHPWire} resist={query.ResultName} resistRaw=0x{query.ResistRaw:X8} resistChance={query.ResistChanceWire / 256f:F2} nativeDamageTime={nativeNow:F3} nativeTick=0 target=Avatar");
                    Debug.LogError($"[PLAYER-DAMAGE] source=monster-skill attacker={monster.Name}#{monster.EntityId} target={target.Name}#{target.EntityId} result={query.ResultName} skill={effect.SkillPath} damageWire=0 preQueryWire={damageWire} hp={sameHP}->{sameHP}/{target.PlayerState.MaxHPWire} marker={marker} source={source} hitRaw=0x{damageResult.HitRaw:X8} blockRaw=0x{damageResult.BlockRaw:X8} damageRaw=0x{damageResult.DamageRaw:X8} rngSeed=0x{(runtime?.Seed ?? 0u):X8} rngPos={rng.CallsSinceReseed}");
                    Debug.LogError($"[COMBAT-EVENT] actor=monster actorId={monster.EntityId} target=player targetId={target.EntityId} result={query.ResultName} skill={effect.SkillPath} damageWire=0 hp={sameHP}->{sameHP} hitRaw=0x{damageResult.HitRaw:X8} hitRoll={damageResult.HitRoll} threshold={damageResult.HitThreshold} blockRaw=0x{damageResult.BlockRaw:X8} blockRoll={damageResult.BlockRoll} blockChance={damageResult.BlockChance} damageRaw=0x{damageResult.DamageRaw:X8} resist={NativeDamageResistLogCode(query)} rngAfter={rng.CallsSinceReseed} marker={marker} source={source}");
                }
                else
                {
                    target.PlayerState.TakeQueriedDamage(adjustedDamageWire, nativeNow);
                    uint afterHP = target.PlayerState.CurrentHPWire;
                    target.IsAlive = afterHP > 0;
                    hpShifted = afterHP != beforeHP;
                    uint effectRaw = ConsumeNativeOnApplyDamageEffectRng(rng, "monster-skill", target.EntityId, target.Name, beforeHP, afterHP, target.PlayerState.MaxHPWire, adjustedDamageWire, source ?? marker ?? "SpellWeaponDamageEffect");
                    if (beforeHP > afterHP)
                        ApplyNativeMonsterOnDamageCallback(monster, beforeHP - afterHP, nativeNow, source ?? marker ?? "SpellWeaponDamageEffect");
                    if (afterHP == 0)
                        RemovePlayerDamageModifiersForTarget(target.EntityId, source ?? marker ?? "SpellWeaponDamageEffect");
                    bool modifierApplied = effect.DamageModifier != null && afterHP > 0 && ApplyPlayerDamageModifierFromMonster(monster, target, effect.DamageModifier, nativeNow, source ?? marker ?? "SpellWeaponDamageEffect");
                    Debug.LogError($"[SPELL-WEAPON-DAMAGE] {monster.Name}#{monster.EntityId}->{target.Name} HIT skill={effect.SkillPath} effect={effect.WeaponEffectPath} dmg={adjustedDamageWire / 256f:F2} preQuery={damageWire / 256f:F2} hp={beforeHP / 256f:F2}->{afterHP / 256f:F2}/{target.PlayerState.MaxHPWire / 256f:F2} arMod={effect.AttackRatingMod} ar={baseAttackRating}->{damageInput.AttackRating} dmgMod={baseDamageMod}->{damageInput.DamageMod} hit=0x{damageResult.HitRaw:X8}/{damageResult.HitRoll} block=0x{damageResult.BlockRaw:X8}/{damageResult.BlockRoll}/{damageResult.BlockChance} dmgRaw=0x{damageResult.DamageRaw:X8} effectRaw=0x{effectRaw:X8} modifierApplied={modifierApplied} dist={dist:F1} rngPos={rng.CallsSinceReseed} result={query.ResultName} native=Weapon::applyDamage@0x00597E50");
                    Debug.LogError($"[NATIVE-DAMAGE-CONTRACT] source={source ?? marker ?? "SpellWeaponDamageEffect"} target={target.Name}#{target.EntityId} weaponClassId={damageResult.WeaponClassId} damageTypeId={damageResult.DamageTypeId} rawRollWire={damageWire} preQueryWire={damageWire} damageTakenMod={query.DamageTakenMod:F2} typeMod={query.DamageTypeMod:F2} postQueryWire={adjustedDamageWire} hp={beforeHP}->{afterHP}/{target.PlayerState.MaxHPWire} resist={query.ResultName} resistRaw=0x{query.ResistRaw:X8} resistChance={query.ResistChanceWire / 256f:F2} nativeDamageTime={nativeNow:F3} nativeTick=0 target=Avatar");
                    Debug.LogError($"[PLAYER-DAMAGE] source=monster-skill attacker={monster.Name}#{monster.EntityId} target={target.Name}#{target.EntityId} result=HIT skill={effect.SkillPath} damageWire={adjustedDamageWire} preQueryWire={damageWire} hp={beforeHP}->{afterHP}/{target.PlayerState.MaxHPWire} marker={marker} source={source} hitRaw=0x{damageResult.HitRaw:X8} blockRaw=0x{damageResult.BlockRaw:X8} damageRaw=0x{damageResult.DamageRaw:X8} effectRaw=0x{effectRaw:X8} rngSeed=0x{(runtime?.Seed ?? 0u):X8} rngPos={rng.CallsSinceReseed}");
                    Debug.LogError($"[COMBAT-EVENT] actor=monster actorId={monster.EntityId} target=player targetId={target.EntityId} result=HIT skill={effect.SkillPath} damageWire={adjustedDamageWire} hp={beforeHP}->{afterHP} hitRaw=0x{damageResult.HitRaw:X8} hitRoll={damageResult.HitRoll} threshold={damageResult.HitThreshold} blockRaw=0x{damageResult.BlockRaw:X8} blockRoll={damageResult.BlockRoll} blockChance={damageResult.BlockChance} damageRaw=0x{damageResult.DamageRaw:X8} effectRaw=0x{effectRaw:X8} resist={NativeDamageResistLogCode(query)} rngAfter={rng.CallsSinceReseed} marker={marker} source={source}");
                }
            }
            else if (damageResult.IsBlocked)
            {
                Debug.LogError($"[SPELL-WEAPON-DAMAGE] {monster.Name}#{monster.EntityId}->{target.Name} BLOCK skill={effect.SkillPath} effect={effect.WeaponEffectPath} hp={beforeHP}->{beforeHP}/{target.PlayerState.MaxHPWire} arMod={effect.AttackRatingMod} ar={baseAttackRating}->{damageInput.AttackRating} dmgMod={baseDamageMod}->{damageInput.DamageMod} hit=0x{damageResult.HitRaw:X8}/{damageResult.HitRoll} block=0x{damageResult.BlockRaw:X8}/{damageResult.BlockRoll}/{damageResult.BlockChance} dist={dist:F1} rngPos={rng.CallsSinceReseed} native=Weapon::applyDamage@0x00597E50");
                Debug.LogError($"[PLAYER-DAMAGE] source=monster-skill attacker={monster.Name}#{monster.EntityId} target={target.Name}#{target.EntityId} result=BLOCK skill={effect.SkillPath} damageWire=0 hp={beforeHP}->{beforeHP}/{target.PlayerState.MaxHPWire} marker={marker} source={source} hitRaw=0x{damageResult.HitRaw:X8} blockRaw=0x{damageResult.BlockRaw:X8} rngSeed=0x{(runtime?.Seed ?? 0u):X8} rngPos={rng.CallsSinceReseed}");
            }
            else
            {
                Debug.LogError($"[SPELL-WEAPON-DAMAGE] {monster.Name}#{monster.EntityId}->{target.Name} MISS skill={effect.SkillPath} effect={effect.WeaponEffectPath} hp={beforeHP}->{beforeHP}/{target.PlayerState.MaxHPWire} arMod={effect.AttackRatingMod} ar={baseAttackRating}->{damageInput.AttackRating} dmgMod={baseDamageMod}->{damageInput.DamageMod} hit=0x{damageResult.HitRaw:X8}/{damageResult.HitRoll} threshold={damageResult.HitThreshold} block=0x{damageResult.BlockRaw:X8}/{damageResult.BlockRoll}/{damageResult.BlockChance} dist={dist:F1} rngPos={rng.CallsSinceReseed} native=Weapon::applyDamage@0x00597E50");
                Debug.LogError($"[PLAYER-DAMAGE] source=monster-skill attacker={monster.Name}#{monster.EntityId} target={target.Name}#{target.EntityId} result=MISS skill={effect.SkillPath} damageWire=0 hp={beforeHP}->{beforeHP}/{target.PlayerState.MaxHPWire} marker={marker} source={source} hitRaw=0x{damageResult.HitRaw:X8} blockRaw=0x{damageResult.BlockRaw:X8} rngSeed=0x{(runtime?.Seed ?? 0u):X8} rngPos={rng.CallsSinceReseed}");
            }

            ConsumeNativeWeaponStunActionRng(rng, monster, target, effect, marker, source);
            return hpShifted;
        }

        private bool TryResolveMonsterHitPointRegenSkillEffect(Monster monster, out MonsterHitPointRegenSkillEffect effect)
        {
            effect = null;
            if (monster == null || string.IsNullOrWhiteSpace(monster.PrimaryActiveSkillPath))
                return false;

            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded)
                return false;

            GCNode skill = gc.ResolveWithInheritance(monster.PrimaryActiveSkillPath);
            GCNode skillDesc = skill?.GetChild("Description") ?? skill;
            string effectPath = skillDesc?.GetString("Effect", monster.PrimaryActiveSkillEffect) ?? monster.PrimaryActiveSkillEffect;
            if (string.IsNullOrWhiteSpace(effectPath))
                return false;

            GCNode effectNode = ResolveAuthoredNodeReference(effectPath, skill);
            if (effectNode == null)
                return false;

            GCNode modEffect = FindEffectChild(effectNode, "SpellModEffect");
            if (modEffect == null)
                return false;

            string modifierPath = modEffect.GetString("Modifier", null);
            if (string.IsNullOrWhiteSpace(modifierPath))
                return false;

            GCNode modifier = ResolveAuthoredNodeReference(modifierPath, skill);
            GCNode modifierDesc = modifier?.GetChild("Description") ?? modifier;
            if (modifierDesc == null)
                return false;

            GCNode attributeNode = FindEffectChild(modifierDesc, "Attribute");
            if (attributeNode == null)
                return false;

            string attribute = attributeNode.GetString("Attribute", null);
            if (!string.Equals(attribute, "HIT_POINT_REGEN_BONUS", StringComparison.OrdinalIgnoreCase))
                return false;

            int value = attributeNode.GetInt("Value", 0);
            if (value == 0)
                return false;

            float durationSeconds = modEffect.GetFloat("Duration", 0f);
            effect = new MonsterHitPointRegenSkillEffect
            {
                SkillPath = monster.PrimaryActiveSkillPath,
                EffectPath = effectPath,
                ModifierPath = modifierPath,
                HitPointRegenBonus = value,
                DurationSeconds = durationSeconds,
                DurationTicks = ComputeNativeSpellModDurationTicks(durationSeconds),
                RemoveOnDeath = modifierDesc.GetBool("RemoveOnDeath", false),
                StackRule = modifierDesc.GetString("StackRule", "")
            };
            return true;
        }

        private bool TryResolveMonsterWeaponDamageSkillEffect(Monster monster, out MonsterWeaponDamageSkillEffect effect)
        {
            effect = null;
            if (monster == null || string.IsNullOrWhiteSpace(monster.PrimaryActiveSkillPath))
                return false;

            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded)
                return false;

            GCNode skill = gc.ResolveWithInheritance(monster.PrimaryActiveSkillPath);
            GCNode skillDesc = skill?.GetChild("Description") ?? skill;
            string effectPath = skillDesc?.GetString("Effect", monster.PrimaryActiveSkillEffect) ?? monster.PrimaryActiveSkillEffect;
            if (string.IsNullOrWhiteSpace(effectPath))
                return false;

            GCNode effectNode = ResolveAuthoredNodeReference(effectPath, skill);
            if (effectNode == null)
                return false;

            GCNode weaponDamage = FindEffectChild(effectNode, "SpellWeaponDamageEffect");
            if (weaponDamage == null)
                return false;

            GCNode knockBack = FindEffectChildByExtends(effectNode, "SpellKnockBackEffect");
            GCNode knockDown = FindEffectChildByExtends(effectNode, "SpellKnockDownEffect");
            TryResolveMonsterDamageModifierSkillEffect(effectNode, skill, monster.PrimaryActiveSkillPath, effectPath, out MonsterDamageModifierSkillEffect damageModifier);
            int skillLevel = 1;
            effect = new MonsterWeaponDamageSkillEffect
            {
                SkillPath = monster.PrimaryActiveSkillPath,
                EffectPath = effectPath,
                WeaponEffectPath = BuildAuthoredEffectPath(effectPath, weaponDamage),
                AttackRatingMod = ResolveNativeSkillLinearMod(weaponDamage, "ARMod", skillLevel),
                DamageMod = ResolveNativeSkillLinearMod(weaponDamage, "DamageMod", skillLevel),
                HasKnockBack = knockBack != null,
                KnockBackEffectPath = knockBack != null ? BuildAuthoredEffectPath(effectPath, knockBack) : null,
                KnockBackStrength = knockBack != null ? ResolveNativeSkillLinearMod(knockBack, "Strength", skillLevel) : 0,
                KnockBackChanceWire = ResolveNativeSpellEffectChanceWire(knockBack),
                HasKnockDown = knockDown != null,
                KnockDownEffectPath = knockDown != null ? BuildAuthoredEffectPath(effectPath, knockDown) : null,
                KnockDownStrength = knockDown != null ? ResolveNativeSkillLinearMod(knockDown, "Strength", skillLevel) : 0,
                KnockDownChanceWire = ResolveNativeSpellEffectChanceWire(knockDown),
                DamageModifier = damageModifier
            };
            return true;
        }

        private bool TryResolveMonsterStunActionSkillEffect(Monster monster, out MonsterStunActionSkillEffect effect)
        {
            effect = null;
            if (monster == null || string.IsNullOrWhiteSpace(monster.PrimaryActiveSkillPath))
                return false;

            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded)
                return false;

            GCNode skill = gc.ResolveWithInheritance(monster.PrimaryActiveSkillPath);
            GCNode skillDesc = skill?.GetChild("Description") ?? skill;
            string effectPath = skillDesc?.GetString("Effect", monster.PrimaryActiveSkillEffect) ?? monster.PrimaryActiveSkillEffect;
            if (string.IsNullOrWhiteSpace(effectPath))
                return false;

            GCNode effectNode = ResolveAuthoredNodeReference(effectPath, skill);
            if (effectNode == null)
                return false;
            if (FindEffectChildByExtends(effectNode, "SpellWeaponDamageEffect") != null || FindEffectChildByExtends(effectNode, "SpellDamageEffect") != null)
                return false;

            GCNode knockDown = FindEffectChildByExtends(effectNode, "SpellKnockDownEffect");
            GCNode knockBack = FindEffectChildByExtends(effectNode, "SpellKnockBackEffect");
            GCNode action = knockDown ?? knockBack;
            if (action == null)
                return false;

            int skillLevel = 1;
            bool isKnockDown = knockDown != null;
            string family = isKnockDown ? "SpellKnockDownEffect" : "SpellKnockBackEffect";
            effect = new MonsterStunActionSkillEffect
            {
                SkillPath = monster.PrimaryActiveSkillPath,
                EffectPath = effectPath,
                ActionEffectPath = BuildAuthoredEffectPath(effectPath, action),
                ActionFamily = family,
                Strength = ResolveNativeSkillLinearMod(action, "Strength", skillLevel),
                ChanceWire = ResolveNativeSpellEffectChanceWire(action),
                IsKnockDown = isKnockDown
            };
            return true;
        }

        private bool TryResolveMonsterDamageModifierSkillEffect(GCNode effectNode, GCNode skill, string skillPath, string effectPath, out MonsterDamageModifierSkillEffect effect)
        {
            effect = null;
            if (effectNode == null)
                return false;

            GCNode modifierEffectRoot = effectNode;
            GCNode weaponDamage = FindEffectChild(effectNode, "SpellWeaponDamageEffect");
            string nestedEffectPath = weaponDamage?.GetString("Effect", null);
            if (!string.IsNullOrWhiteSpace(nestedEffectPath))
            {
                GCNode nestedEffectNode = ResolveAuthoredNodeReference(nestedEffectPath, skill);
                if (nestedEffectNode != null)
                    modifierEffectRoot = nestedEffectNode;
            }

            GCNode modEffect = FindEffectChild(modifierEffectRoot, "SpellModEffect");
            if (modEffect == null && modifierEffectRoot != effectNode)
                modEffect = FindEffectChild(effectNode, "SpellModEffect");
            if (modEffect == null)
                return false;

            string modifierPath = modEffect.GetString("Modifier", null);
            if (string.IsNullOrWhiteSpace(modifierPath))
                return false;

            GCNode modifier = ResolveAuthoredNodeReference(modifierPath, skill);
            GCNode modifierDesc = modifier?.GetChild("Description") ?? modifier;
            if (modifierDesc == null)
                return false;

            string modifierEffectPath = modifierDesc.GetString("Effect", null);
            if (string.IsNullOrWhiteSpace(modifierEffectPath))
                return false;

            GCNode modifierEffect = ResolveAuthoredNodeReference(modifierEffectPath, modifier ?? skill);
            if (modifierEffect == null)
                return false;

            GCNode damageEffect = FindEffectChild(modifierEffect, "SpellDamageEffect");
            if (damageEffect == null)
                return false;

            string attackType = damageEffect.GetString("AttackType", "MAGIC");
            string damageType = damageEffect.GetString("DamageType", "POISON");
            if (!DamageComputer.TryResolveNativeDamageTypeId(damageType, out int damageTypeId))
                return false;

            float damageMod = damageEffect.GetFloat("DamageMod", 0f);
            if (damageMod <= 0f)
                return false;

            float durationSeconds = modEffect.GetFloat("Duration", 0f);
            if (durationSeconds <= 0f)
                durationSeconds = modifierDesc.GetFloat("Duration", 0f);
            float frequencySeconds = modifierDesc.GetFloat("Frequency", 0f);
            if (frequencySeconds <= 0f)
                frequencySeconds = NATIVE_UNIT_TICK_INTERVAL;

            effect = new MonsterDamageModifierSkillEffect
            {
                SkillPath = skillPath,
                EffectPath = nestedEffectPath ?? effectPath,
                ModifierPath = modifierPath,
                ModifierEffectPath = modifierEffectPath,
                AttackType = attackType,
                DamageType = damageType,
                DamageTypeId = damageTypeId,
                DamageKind = string.Equals(attackType, "MAGIC", StringComparison.OrdinalIgnoreCase) ? (byte)3 : (byte)0,
                DamageMod = damageMod,
                DamageVolatility = damageEffect.GetFloat("DamageVolatility", 0f),
                DurationSeconds = durationSeconds,
                DurationTicks = ComputeNativeSpellModDurationTicks(durationSeconds),
                FrequencySeconds = frequencySeconds,
                FrequencyTicks = ComputeNativeEffectModFrequencyTicks(frequencySeconds),
                RemoveOnDeath = modifierDesc.GetBool("RemoveOnDeath", false),
                StackRule = modifierDesc.GetString("StackRule", "")
            };
            return true;
        }

        private static int ResolveNativeSkillLinearMod(GCNode node, string prefix, int skillLevel)
        {
            if (node == null || string.IsNullOrWhiteSpace(prefix))
                return 0;
            int level = Mathf.Max(0, skillLevel);
            int min = node.GetInt(prefix + "Min", 0);
            int inc = node.GetInt(prefix + "Inc", 0);
            int value = min + inc * level;
            if (node.HasProperty(prefix + "Max"))
            {
                int max = node.GetInt(prefix + "Max", value);
                if (value > max) value = max;
            }
            return value;
        }

        private static string BuildAuthoredEffectPath(string effectPath, GCNode child)
        {
            if (string.IsNullOrWhiteSpace(effectPath))
                return child?.Extends ?? "unknown";
            if (child == null)
                return effectPath;
            if (!string.IsNullOrWhiteSpace(child.Name) && !child.IsAnonymous)
                return $"{effectPath}.{child.Name}";
            return $"{effectPath}.{child.Extends ?? "anonymous"}";
        }

        private void ApplyMonsterStunActionSkillEffect(Monster monster, CombatPlayer target, MonsterStunActionSkillEffect effect, string marker, string source)
        {
            if (monster == null || target == null || effect == null)
                return;
            NativeRoomRuntime runtime = GetRoomRuntimeForMonster(monster);
            MersenneTwister rng = runtime?.RoomRng;
            if (rng == null)
            {
                Debug.LogError($"[SPELL-STUN-ACTION] partial attacker={monster.Name}#{monster.EntityId} target={target.Name}#{target.EntityId} skill={effect.SkillPath} effect={effect.ActionEffectPath} family={effect.ActionFamily} source={source ?? marker ?? "unknown"} result=BLOCKED reason=missing-room-rng native=SpellEffect::CheckChance@0x00545FF0 Unit::CheckStunResist@0x0050C630");
                return;
            }
            ConsumeNativeStunActionEffectRng(rng, monster, target, effect.SkillPath, effect.ActionEffectPath, effect.ActionFamily, effect.Strength, effect.ChanceWire, marker, source);
        }

        private void ConsumeNativeWeaponStunActionRng(MersenneTwister rng, Monster monster, CombatPlayer target, MonsterWeaponDamageSkillEffect effect, string marker, string source)
        {
            if (effect == null)
                return;
            if (effect.HasKnockBack)
                ConsumeNativeStunActionEffectRng(rng, monster, target, effect.SkillPath, effect.KnockBackEffectPath, "SpellKnockBackEffect", effect.KnockBackStrength, effect.KnockBackChanceWire, marker, source);
            if (effect.HasKnockDown)
                ConsumeNativeStunActionEffectRng(rng, monster, target, effect.SkillPath, effect.KnockDownEffectPath, "SpellKnockDownEffect", effect.KnockDownStrength, effect.KnockDownChanceWire, marker, source);
        }

        private bool ConsumeNativeStunActionEffectRng(MersenneTwister rng, Monster monster, CombatPlayer target, string skillPath, string effectPath, string effectFamily, int strength, int chanceWire, string marker, string source)
        {
            if (rng == null)
                return false;
            string family = string.IsNullOrWhiteSpace(effectFamily) ? "SpellKnockBackEffect" : effectFamily;
            string native = family == "SpellKnockDownEffect" ? "SpellKnockDownEffect::doEffect@0x00553360" : "SpellKnockBackEffect::doEffect@0x00552C80";
            if (!ConsumeNativeSpellEffectChanceRng(rng, monster, target, skillPath, effectPath, family, chanceWire, marker, source, out uint chanceRaw, out uint chanceRoll))
            {
                Debug.LogError($"[SPELL-STUN-ACTION] attacker={monster?.Name ?? "monster"}#{monster?.EntityId ?? 0} target={target?.Name ?? "unknown"}#{target?.EntityId ?? 0} skill={skillPath ?? "none"} effect={effectPath ?? "none"} family={family} result=CHANCE_FAIL strength={strength} chanceWire={chanceWire} chance={chanceWire / 256f:F2} chanceRaw=0x{chanceRaw:X8} chanceRoll={chanceRoll} rngPos={rng.CallsSinceReseed} packet=NOT_SENT source={source ?? marker ?? "unknown"} native=SpellEffect::CheckChance@0x00545FF0 {native}");
                return false;
            }

            int resistWire = ResolveNativePlayerStunResistChanceWire(target, monster?.Level ?? 0);
            uint stunRaw = NativeRngLedger.Generate(rng, "unitOwnedCombat", $"{marker ?? "MON-SKILL"}:{family}::CheckStunResist", "Unit::CheckStunResist");
            uint stunRoll = stunRaw % 0x6400u;
            bool resisted = stunRoll < (uint)Mathf.Max(0, resistWire);
            if (resisted)
            {
                Debug.LogError($"[SPELL-STUN-ACTION] attacker={monster?.Name ?? "monster"}#{monster?.EntityId ?? 0} target={target?.Name ?? "unknown"}#{target?.EntityId ?? 0} skill={skillPath ?? "none"} effect={effectPath ?? "none"} family={family} result=RESIST strength={strength} chanceWire={chanceWire} chanceRaw=0x{chanceRaw:X8} chanceRoll={chanceRoll} stunResistWire={resistWire} stunRaw=0x{stunRaw:X8} stunRoll={stunRoll} rngPos={rng.CallsSinceReseed} packet=NOT_SENT source={source ?? marker ?? "unknown"} native={native} SpellEffect::CheckChance@0x00545FF0 Unit::CheckStunResist@0x0050C630");
                return false;
            }

            PlayerStunActionResolved action = BuildNativePlayerStunAction(monster, target, skillPath, effectPath, family, strength, chanceWire, chanceRaw, chanceRoll, resistWire, stunRaw, stunRoll, source ?? marker ?? "unknown");
            OnPlayerStunActionResolved?.Invoke(monster, target, action);
            Debug.LogError($"[SPELL-STUN-ACTION] attacker={monster?.Name ?? "monster"}#{monster?.EntityId ?? 0} target={target?.Name ?? "unknown"}#{target?.EntityId ?? 0} skill={skillPath ?? "none"} effect={effectPath ?? "none"} family={family} result=ACTION_QUEUED action={action.ActionClassName} actionId=0x{action.ActionClassId:X2} heading={action.HeadingWire} strengthWire={action.StrengthWire} authoredStrength={action.AuthoredStrength} knockDownPlayerBranch={action.KnockDownPlayerBranch} chanceWire={chanceWire} chanceRaw=0x{chanceRaw:X8} chanceRoll={chanceRoll} stunResistWire={resistWire} stunRaw=0x{stunRaw:X8} stunRoll={stunRoll} rngPos={rng.CallsSinceReseed} source={source ?? marker ?? "unknown"} native={native} SpellEffect::CheckChance@0x00545FF0 Unit::CheckStunResist@0x0050C630 KnockBack::writeData@0x0052A320");
            return true;
        }

        private PlayerStunActionResolved BuildNativePlayerStunAction(Monster monster, CombatPlayer target, string skillPath, string effectPath, string family, int strength, int chanceWire, uint chanceRaw, uint chanceRoll, int resistWire, uint stunRaw, uint stunRoll, string source)
        {
            float sourceX = monster != null ? monster.PosX : 0f;
            float sourceY = monster != null ? monster.PosY : 0f;
            if (monster != null && TryGetMonsterClientVisiblePosition(monster, GetNativeCombatTime(), out float visibleX, out float visibleY))
            {
                sourceX = visibleX;
                sourceY = visibleY;
            }
            float targetX = target != null ? target.PosX : sourceX;
            float targetY = target != null ? target.PosY : sourceY;
            bool knockDownPlayerBranch;
            ushort strengthWire = ResolveNativePlayerStunActionStrengthWire(family, strength, out knockDownPlayerBranch);
            return new PlayerStunActionResolved
            {
                SkillPath = skillPath,
                EffectPath = effectPath,
                EffectFamily = family,
                ActionClassName = "KnockBack",
                ActionClassId = NATIVE_PLAYER_STUN_ACTION_KNOCKBACK_ID,
                HeadingWire = ResolveNativeDestHeadingWire(sourceX, sourceY, targetX, targetY),
                StrengthWire = strengthWire,
                AuthoredStrength = strength,
                ChanceWire = chanceWire,
                ChanceRaw = chanceRaw,
                ChanceRoll = chanceRoll,
                StunResistWire = resistWire,
                StunRaw = stunRaw,
                StunRoll = stunRoll,
                Source = source,
                KnockDownPlayerBranch = knockDownPlayerBranch
            };
        }

        private static ushort ResolveNativePlayerStunActionStrengthWire(string family, int strength, out bool knockDownPlayerBranch)
        {
            knockDownPlayerBranch = string.Equals(family, "SpellKnockDownEffect", StringComparison.OrdinalIgnoreCase);
            long value = knockDownPlayerBranch ? (long)strength * 2L : strength;
            if (value < 0) value = 0;
            if (value > ushort.MaxValue) value = ushort.MaxValue;
            return (ushort)value;
        }

        private static ushort ResolveNativeDestHeadingWire(float sourceX, float sourceY, float targetX, float targetY)
        {
            float dx = targetX - sourceX;
            float dy = targetY - sourceY;
            float distance = Mathf.Sqrt(dx * dx + dy * dy);
            if (distance <= 0.0001f)
                return 0;
            float absX = Mathf.Abs(dx);
            float absY = Mathf.Abs(dy);
            float heading;
            if (absX <= absY)
            {
                float angle = Mathf.Acos(Mathf.Clamp(absX / distance, -1f, 1f)) * Mathf.Rad2Deg;
                heading = dy < 0f ? 360f - (angle + 90f) : 360f - (90f - angle);
            }
            else
            {
                float angle = Mathf.Asin(Mathf.Clamp(absY / distance, -1f, 1f)) * Mathf.Rad2Deg;
                heading = dx < 0f ? 360f - (angle + 270f) : 360f - (90f - angle);
            }
            while (heading < 0f) heading += 360f;
            while (heading > 360f) heading -= 360f;
            return (ushort)Mathf.Clamp(Mathf.RoundToInt(heading), 0, 360);
        }

        private bool ConsumeNativeSpellEffectChanceRng(MersenneTwister rng, Monster monster, CombatPlayer target, string skillPath, string effectPath, string effectFamily, int chanceWire, string marker, string source, out uint raw, out uint roll)
        {
            raw = 0;
            roll = 0;
            int normalizedChance = Mathf.Clamp(chanceWire, 0, 0x6400);
            if (normalizedChance >= 0x6400)
                return true;
            raw = NativeRngLedger.Generate(rng, "unitOwnedCombat", $"{marker ?? "MON-SKILL"}:{effectFamily}::CheckChance", "SpellEffect::CheckChance");
            roll = raw % 0x6464u;
            return roll < (uint)normalizedChance;
        }

        private static int ResolveNativeSpellEffectChanceWire(GCNode node)
        {
            if (node == null)
                return 0x6400;
            float chance = node.GetFloat("Chance", 100f);
            if (float.IsNaN(chance) || float.IsInfinity(chance))
                chance = 100f;
            return Mathf.Clamp(Mathf.RoundToInt(chance * 256f), 0, 0x6400);
        }

        private static int ResolveNativePlayerStunResistChanceWire(CombatPlayer target, int attackerLevel)
        {
            int resist = GetEquipmentStatSum(target?.PlayerState, "STUN_RESIST", "STUNRESIST") * 0x100;
            int targetLevel = target?.PlayerState?.Level ?? 0;
            int diff = (targetLevel * 0x100) - (attackerLevel * 0x100);
            if (diff > 0x500)
                resist += (int)(((long)(diff - 0x500) * 0x500) >> 8);
            if (resist > 0x5A00) resist = 0x5A00;
            if (resist < 0x500) resist = 0x500;
            return resist;
        }

        private static GCNode ResolveAuthoredNodeReference(string path, GCNode contextRoot)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var gc = GCDatabase.Instance;
            GCNode node = gc?.ResolveWithInheritance(path);
            if (node != null)
                return node;

            if (contextRoot == null)
                return null;

            string rootName = contextRoot.Name;
            if (!string.IsNullOrWhiteSpace(rootName) &&
                path.StartsWith(rootName + ".", StringComparison.OrdinalIgnoreCase))
            {
                string subPath = path.Substring(rootName.Length + 1);
                return ResolveChildPath(contextRoot, subPath);
            }

            int dot = path.IndexOf('.');
            while (dot >= 0 && dot + 1 < path.Length)
            {
                string suffix = path.Substring(dot + 1);
                GCNode child = ResolveChildPath(contextRoot, suffix);
                if (child != null)
                    return child;
                dot = path.IndexOf('.', dot + 1);
            }

            return null;
        }

        private static GCNode ResolveChildPath(GCNode root, string dottedPath)
        {
            if (root == null || string.IsNullOrWhiteSpace(dottedPath))
                return root;

            GCNode current = root;
            foreach (string part in dottedPath.Split('.'))
            {
                if (current == null || string.IsNullOrWhiteSpace(part))
                    return null;
                current = current.GetChild(part);
            }
            return current;
        }

        private static GCNode FindEffectChild(GCNode node, string nativeExtends)
        {
            if (node == null)
                return null;
            if (AuthoredExtends(node, nativeExtends) || node.HasProperty("Attribute"))
                return node;
            foreach (var child in node.AnonymousChildren)
            {
                if (AuthoredExtends(child, nativeExtends) || child.HasProperty("Attribute"))
                    return child;
            }
            foreach (var child in node.Children.Values)
            {
                if (AuthoredExtends(child, nativeExtends) || child.HasProperty("Attribute"))
                    return child;
            }
            return null;
        }

        private static GCNode FindEffectChildByExtends(GCNode node, string nativeExtends)
        {
            if (node == null)
                return null;
            if (AuthoredExtends(node, nativeExtends))
                return node;
            foreach (var child in node.AnonymousChildren)
            {
                if (AuthoredExtends(child, nativeExtends))
                    return child;
            }
            foreach (var child in node.Children.Values)
            {
                if (AuthoredExtends(child, nativeExtends))
                    return child;
            }
            return null;
        }

        private static bool AuthoredExtends(GCNode node, string nativeExtends)
        {
            if (node == null || string.IsNullOrWhiteSpace(nativeExtends))
                return false;
            string ext = node.Extends ?? string.Empty;
            if (string.Equals(ext, nativeExtends, StringComparison.OrdinalIgnoreCase))
                return true;
            return ext.EndsWith("." + nativeExtends, StringComparison.OrdinalIgnoreCase);
        }

        private static ushort ComputeNativeSpellModDurationTicks(float durationSeconds)
        {
            if (durationSeconds <= 0f || float.IsNaN(durationSeconds) || float.IsInfinity(durationSeconds))
                return 0;
            long fixed8Seconds = Mathf.RoundToInt(durationSeconds * 256f);
            long ticks = ((fixed8Seconds * 30L) + 0x100L) >> 8;
            if (ticks <= 0) return 0;
            if (ticks > ushort.MaxValue) return ushort.MaxValue;
            return (ushort)ticks;
        }

        private static ushort ComputeNativeEffectModFrequencyTicks(float frequencySeconds)
        {
            if (frequencySeconds <= 0f || float.IsNaN(frequencySeconds) || float.IsInfinity(frequencySeconds))
                return 0;
            long fixed8Seconds = Mathf.RoundToInt(frequencySeconds * 256f);
            long ticks = (fixed8Seconds * 30L) >> 8;
            if (ticks <= 0) return 0;
            if (ticks > ushort.MaxValue) return ushort.MaxValue;
            return (ushort)ticks;
        }

        private static int ComputeNativeEffectModApplyTickBudget(ushort durationTicks, ushort frequencyTicks)
        {
            int period = Math.Max(1, (int)frequencyTicks);
            if (durationTicks == 0)
                return int.MaxValue;
            if (durationTicks <= period)
                return 0;
            return (durationTicks - 1) / period;
        }

        private static float ComputeNativeEffectModIntervalSeconds(ushort frequencyTicks)
        {
            return Math.Max(1, (int)frequencyTicks) / 30f;
        }

        private static float ComputeNativeEffectModExpireTime(float applyTime, ushort durationTicks)
        {
            if (durationTicks == 0)
                return float.PositiveInfinity;
            return applyTime + durationTicks / 30f;
        }

        private static bool IsNativeEffectModExpired(ushort durationTicks, float expireTime, float now)
        {
            return durationTicks > 0 && now + 0.0001f >= expireTime;
        }

        private float ResolveMonsterDamageModifier(Monster monster)
        {
            if (monster == null) return 1f;
            float damageMod = monster.DamageMod > 0f ? monster.DamageMod : 1f;
            return damageMod;
        }

        private IEnumerable<Monster> SelectMonsters(Monster onlyMonster)
        {
            if (onlyMonster != null)
            {
                yield return onlyMonster;
                yield break;
            }

            foreach (var monster in _activeMonsters.Values)
                yield return monster;
        }

        private uint PeekMonsterHPWireForTrace(Monster monster)
        {
            if (monster == null) return 0;
            if (_monsterHPAuthority.TryGetValue(monster.EntityId, out var authority) && authority.RuntimeInitialized)
                return authority.RuntimeHPWire;
            if (_monsterRuntimeHPWire.TryGetValue(monster.EntityId, out uint runtimeHP))
                return runtimeHP;
            return monster.CurrentHPWire;
        }

        private void TraceMonsterState(Monster monster, string phase, CombatPlayer target = null, float dist = -1f, float range = -1f, string reason = null)
        {
            if (monster == null) return;

            SyncNativeEncounterMirror(monster);
            uint hp = PeekMonsterHPWireForTrace(monster);
            string targetText = target != null ? $"{target.Name}#{target.EntityId}" : (monster.TargetId != 0 ? monster.TargetId.ToString() : "none");
            string signature = $"{monster.State}|{monster.IsAlive}|{monster.AggroTriggered}|{monster.TargetId}|{monster.AlertSourceEntityId}|{monster.AttackPending}|{monster.AttackClientVisible}|{monster.AttackNativeContactOnly}|{monster.AttackHitResolved}|{monster.UsePrimaryActiveSkillThisAttack}|{monster.DeathPendingClientConfirmation}|{monster.NativeDeathState}|{hp}|{monster.CurrentManaWire}|{Mathf.RoundToInt(monster.PosX * 10f)}|{Mathf.RoundToInt(monster.PosY * 10f)}|{monster.CombatContactTargetId}|{Mathf.RoundToInt(monster.AttackCommitTime * 1000f)}|{Mathf.RoundToInt(monster.AttackEndTime * 1000f)}|{monster.PrimaryActiveSkillCooldownRemainingTicks}";
            if (_monsterStateTraceSignatures.TryGetValue(monster.EntityId, out var previous) && previous == signature)
                return;

            _monsterStateTraceSignatures[monster.EntityId] = signature;
            string distText = dist >= 0f ? $"{dist:F1}" : "n/a";
            string rangeText = range >= 0f ? $"{range:F1}" : "n/a";
            NativeRoomRuntime runtime = GetRoomRuntimeForMonster(monster);
            string instance = runtime?.InstanceKey ?? monster.InstanceKey ?? "<missing>";
            uint seed = runtime?.Seed ?? 0u;
            int rngPos = runtime?.RngCallsSinceReseed ?? -1;
            Debug.LogError($"[MON-STATE] phase={phase ?? "unknown"} monster={monster.Name}#{monster.EntityId} behavior={monster.BehaviorId} unit={monster.UnitId} gc='{monster.GCType}' spawnGc='{monster.SpawnGCType}' zone='{monster.ZoneName}' instance='{instance}' state={monster.State} nativeState={monster.NativeAi?.StateId ?? 0} nativeMsg={monster.NativeAi?.LastMessageId ?? 0} encounterState={monster.NativeEncounterObjectState} encounterLive={monster.NativeEncounterLiveUnitCount} encounterReturning={monster.NativeEncounterReturningUnitCount} alive={monster.IsAlive} aggro={monster.AggroTriggered} target={targetText} alertSource={monster.AlertSourceEntityId} deathPending={monster.DeathPendingClientConfirmation} nativeDeath={monster.NativeDeathState} hp={hp}/{monster.MaxHPWire} mana={monster.CurrentManaWire}/{monster.MaxManaWire} pos=({monster.PosX:F1},{monster.PosY:F1},{monster.PosZ:F1}) dist={distText} range={rangeText} pending={monster.AttackPending} clientVisible={monster.AttackClientVisible} nativeContactOnly={monster.AttackNativeContactOnly} hitResolved={monster.AttackHitResolved} session={monster.AttackSessionId} commit={monster.AttackCommitTime:F3} end={monster.AttackEndTime:F3} contactTarget={monster.CombatContactTargetId} contactUntil={monster.CombatContactUntil:F3} skill={monster.PrimaryActiveSkillPath ?? "none"} useSkill={monster.UsePrimaryActiveSkillThisAttack} skillCd={monster.PrimaryActiveSkillCooldownRemainingTicks}/{monster.PrimaryActiveSkillCooldownTicks} rngSeed=0x{seed:X8} rngPos={rngPos} reason={reason ?? "state-change"}");
        }

        private void TraceCombatTick(string phase, float deltaTime, bool allowNewAttacks, Monster onlyMonster = null)
        {
            float now = GetNativeCombatTime();
            if (now - _lastCombatTraceSummaryTime < 1f)
                return;

            _lastCombatTraceSummaryTime = now;
            int alive = 0;
            int aggro = 0;
            int pending = 0;
            int attacking = 0;
            int deathPending = 0;
            foreach (var monster in SelectMonsters(onlyMonster))
            {
                if (monster == null) continue;
                if (monster.IsAlive) alive++;
                if (monster.AggroTriggered) aggro++;
                if (monster.AttackPending) pending++;
                if (monster.State == MonsterState.Attacking) attacking++;
                if (monster.DeathPendingClientConfirmation || monster.NativeDeathLifecycleActive) deathPending++;
            }

            string scope = onlyMonster != null ? onlyMonster.EntityId.ToString() : "all";
            NativeRoomRuntime tickRuntime = onlyMonster != null ? GetRoomRuntimeForMonster(onlyMonster) : CurrentRoomRuntime;
            Debug.LogError($"[COMBAT-TICK] phase={phase ?? "update"} dt={deltaTime:F3} scope={scope} players={_players.Count} monsters={_activeMonsters.Count} alive={alive} aggro={aggro} pending={pending} attacking={attacking} deathPending={deathPending} allowNew={allowNewAttacks} instance='{tickRuntime?.InstanceKey ?? "<missing>"}' rngReady={tickRuntime != null && tickRuntime.Initialized} roomSeed=0x{(tickRuntime?.Seed ?? 0u):X8} rngPos={tickRuntime?.RngCallsSinceReseed ?? -1}");
        }

        private bool ShouldRunNativeProximityScanThisTick(Monster monster)
        {
            if (monster == null) return false;
            if (!monster.IsAlive || monster.AggroTriggered || monster.TargetId != 0)
                return false;
            if (monster.ProximityScanCountdownTicks > 0)
            {
                monster.ProximityScanCountdownTicks--;
            }
            if (monster.ProximityScanCountdownTicks > 0)
                return false;
            int scanFixed256 = Mathf.Max(0, Mathf.RoundToInt(Mathf.Max(0f, monster.ScanFrequency) * 256f));
            int periodTicks = Mathf.Clamp((int)(((long)scanFixed256 * 0x1e00L) >> 16), 1, short.MaxValue);
            monster.ProximityScanCountdownTicks = (short)periodTicks;
            return true;
        }

        private static readonly bool ProximityAggroEnabled =
            !(Environment.GetEnvironmentVariable("DR_SERVER_PROXIMITY_AGGRO") is string proxAggroEnv
              && (proxAggroEnv.Trim() == "0" || proxAggroEnv.Trim().Equals("false", StringComparison.OrdinalIgnoreCase)));

        private void ProcessProximityAggro(uint playerEntityId = 0, Monster onlyMonster = null)
        {
            if (!ProximityAggroEnabled) return;
            if (_players.Count == 0) return;
            var pathMaps = new Dictionary<string, PathMap>(StringComparer.OrdinalIgnoreCase);
            foreach (var monster in SelectMonsters(onlyMonster))
            {
                if (!monster.IsAlive || monster.AggroTriggered || monster.TargetId != 0) continue;
                TryGetMonsterWanderClientVisiblePosition(monster, out float monsterX, out float monsterY);
                PathMap pathMap = null;
                string pathMapKey = ResolveMonsterPathMapKey(monster);
                if (!string.IsNullOrWhiteSpace(pathMapKey))
                {
                    if (!pathMaps.TryGetValue(pathMapKey, out pathMap))
                    {
                        pathMap = PathMapManager.Instance.GetPathMap(pathMapKey);
                        pathMaps[pathMapKey] = pathMap;
                    }
                }
                float range = ResolveMonsterAggroAdmissionRange(monster);
                if (range <= 0f) continue;

                CombatPlayer nearest = null;
                float nearestSq = float.MaxValue;
                float rangeSq = range * range;
                foreach (var player in _players.Values)
                {
                    if (playerEntityId != 0 && player.EntityId != playerEntityId) continue;
                    if (player == null || !player.IsAlive || player.PlayerState == null) continue;
                    if (player.PlayerState.IsZoneSpawnDamageImmune) continue;
                    if (player.PlayerState.CurrentHPWire == 0 && player.PlayerState.SynchHP == 0) continue;
                    float dx = player.PosX - monsterX;
                    float dy = player.PosY - monsterY;
                    float distSq = dx * dx + dy * dy;
                    if (distSq > rangeSq || distSq >= nearestSq) continue;
                    if (pathMap != null
                        && pathMap.TryCanReachPoint(monsterX, monsterY, player.PosX, player.PosY, out bool canAggroReach)
                        && !canAggroReach) continue;
                    nearest = player;
                    nearestSq = distSq;
                }

                if (nearest != null)
                {
                    SyncMonsterWanderClientVisiblePosition(monster, "proximity-acquire");
                    Debug.LogError($"[AGGRO-OBSERVE] proximity admit {monster.Name}#{monster.EntityId}->{nearest.Name} dist={Mathf.Sqrt(nearestSq):F1} aggro={range:F1} targetSearch={ResolveMonsterTargetSearchRange(monster, false):F1} native=AgroRange-admission");
                    AggroMonster(monster, nearest, "proximity", false);
                }
            }
        }

        private float ResolveMonsterAggroAdmissionRange(Monster monster)
        {
            if (monster == null) return 0f;
            return monster.AggroRange > 0f ? monster.AggroRange : 0f;
        }

        private float ResolveMonsterTargetSearchRange(Monster monster, bool hasPathMap)
        {
            if (monster == null) return 0f;
            if (HasNativeEncounterObjectActiveSearch(monster) && monster.PerceptionRange > 0f) return monster.PerceptionRange;
            return monster.AggroRange;
        }

        private void ProcessMonsterMovement(float deltaTime)
        {
            ProcessMonsterMovement(deltaTime, 0);
        }

        private void ProcessMonsterMovement(float deltaTime, uint playerEntityId)
        {
            ProcessMonsterMovement(deltaTime, playerEntityId, null, true);
        }

        private void ProcessMonsterMovement(float deltaTime, uint playerEntityId, Monster onlyMonster)
        {
            ProcessMonsterMovement(deltaTime, playerEntityId, onlyMonster, true);
        }

        private void ProcessMonsterMovement(float deltaTime, uint playerEntityId, Monster onlyMonster, bool emitPositionChanged)
        {
            if (deltaTime <= 0f) return;
            float nativeNow = GetNativeCombatTime();
            var pathMaps = new Dictionary<string, PathMap>(StringComparer.OrdinalIgnoreCase);
            foreach (var monster in SelectMonsters(onlyMonster))
            {
                if (!monster.IsAlive || !monster.AggroTriggered || monster.TargetId == 0) continue;
                if (playerEntityId != 0 && monster.TargetId != playerEntityId) continue;
                if (!_players.TryGetValue(monster.TargetId, out var target) || target == null || !target.IsAlive || target.PlayerState == null) continue;
                if (target.PlayerState.CurrentHPWire == 0 && target.PlayerState.SynchHP == 0) continue;
                if (monster.AttackPending) continue;

                float allowedRange = ResolveMonsterEffectiveAttackRange(monster);
                if (allowedRange <= 0f) continue;

                float dx = target.PosX - monster.PosX;
                float dy = target.PosY - monster.PosY;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                PathMap pathMap = null;
                string pathMapKey = ResolveMonsterPathMapKey(monster);
                if (!string.IsNullOrWhiteSpace(pathMapKey))
                {
                    if (!pathMaps.TryGetValue(pathMapKey, out pathMap))
                    {
                        pathMap = PathMapManager.Instance.GetPathMap(pathMapKey);
                        pathMaps[pathMapKey] = pathMap;
                    }
                }
                if (pathMap != null
                    && pathMap.TryCanReachPoint(monster.PosX, monster.PosY, target.PosX, target.PosY, out bool canMoveReach)
                    && !canMoveReach)
                {
                    ClearMonsterCombatContact(monster, target);
                    if (monster.State == MonsterState.Combat)
                        monster.State = MonsterState.Chase;
                    TraceMonsterState(monster, "movement", target, dist, allowedRange, "path-blocked");
                    continue;
                }
                if (dist <= allowedRange + NATIVE_CONTACT_RANGE_EPSILON || dist <= 0.001f)
                {
                    if (monster.State == MonsterState.Chase)
                        monster.State = MonsterState.Combat;
                    monster.CombatContactTargetId = target.EntityId;
                    monster.CombatContactUntil = nativeNow + Mathf.Max(0.25f, ResolveMonsterAttackWindup(monster) + 0.25f);
                    TraceMonsterState(monster, "movement", target, dist, allowedRange, "contact");
                    continue;
                }

                float speed = ResolveMonsterMovementSpeed(monster);
                if (speed <= 0f)
                {
                    monster.State = MonsterState.Chase;
                    TraceMonsterState(monster, "movement", target, dist, allowedRange, "no-speed");
                    continue;
                }

                float step = Mathf.Min(Mathf.Max(0f, dist - allowedRange), speed * deltaTime);
                if (step <= 0f) continue;

                monster.PosX += dx / dist * step;
                monster.PosY += dy / dist * step;
                monster.Heading = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
                monster.State = MonsterState.Chase;

                float remaining = dist - step;
                if (remaining <= allowedRange + NATIVE_CONTACT_RANGE_EPSILON)
                {
                    monster.State = MonsterState.Combat;
                    monster.CombatContactTargetId = target.EntityId;
                    monster.CombatContactUntil = nativeNow + Mathf.Max(0.25f, ResolveMonsterAttackWindup(monster) + 0.25f);
                }
                if (emitPositionChanged)
                    OnMonsterPositionChanged?.Invoke(monster);
                TraceMonsterState(monster, "movement", target, remaining, allowedRange, "move");
            }
        }

        private static float ResolveMonsterMovementSpeed(Monster monster)
        {
            if (monster == null) return 0f;
            if (monster.MoveSpeed > 0f) return monster.MoveSpeed;
            if (monster.WalkSpeed > 0f) return monster.WalkSpeed;
            return 0f;
        }

        public float GetMonsterMovementSpeed(Monster monster)
        {
            return ResolveMonsterMovementSpeed(monster);
        }

        private void AdvanceMonsterPrimarySkillCooldown(Monster monster, float now)
        {
            if (monster == null)
                return;

            if (monster.ActiveSkills != null && monster.ActiveSkills.Count > 0)
            {
                foreach (var skill in monster.ActiveSkills)
                    AdvanceMonsterSkillCooldown(monster, skill, now);
                if (monster.SelectedActiveSkill != null)
                    ApplyMonsterActiveSkillSelection(monster, monster.SelectedActiveSkill);
                return;
            }

            if (monster.PrimaryActiveSkillCooldownRemainingTicks == 0)
            {
                if (monster.PrimaryActiveSkillCooldownLastTime <= 0f)
                    monster.PrimaryActiveSkillCooldownLastTime = now;
                return;
            }
            if (monster.PrimaryActiveSkillCooldownLastTime <= 0f)
            {
                monster.PrimaryActiveSkillCooldownLastTime = now;
                return;
            }

            int ticks = Mathf.FloorToInt((now - monster.PrimaryActiveSkillCooldownLastTime) / NATIVE_UNIT_TICK_INTERVAL);
            if (ticks <= 0) return;
            ushort oldTicks = monster.PrimaryActiveSkillCooldownRemainingTicks;
            monster.PrimaryActiveSkillCooldownRemainingTicks = ticks >= oldTicks ? (ushort)0 : (ushort)(oldTicks - ticks);
            monster.PrimaryActiveSkillCooldownLastTime += ticks * NATIVE_UNIT_TICK_INTERVAL;
            if (oldTicks != monster.PrimaryActiveSkillCooldownRemainingTicks)
                Debug.LogError($"[MON-SKILL-CD] advance {monster.Name}#{monster.EntityId} skill={monster.PrimaryActiveSkillPath ?? "none"} ticks={oldTicks}->{monster.PrimaryActiveSkillCooldownRemainingTicks} elapsedTicks={ticks}");
        }

        private void AdvanceMonsterSkillCooldown(Monster monster, MonsterActiveSkillRuntime skill, float now)
        {
            if (monster == null || skill == null)
                return;
            if (skill.CooldownRemainingTicks == 0)
            {
                if (skill.CooldownLastTime <= 0f)
                    skill.CooldownLastTime = now;
                return;
            }
            if (skill.CooldownLastTime <= 0f)
            {
                skill.CooldownLastTime = now;
                return;
            }

            int ticks = Mathf.FloorToInt((now - skill.CooldownLastTime) / NATIVE_UNIT_TICK_INTERVAL);
            if (ticks <= 0) return;
            ushort oldTicks = skill.CooldownRemainingTicks;
            skill.CooldownRemainingTicks = ticks >= oldTicks ? (ushort)0 : (ushort)(oldTicks - ticks);
            skill.CooldownLastTime += ticks * NATIVE_UNIT_TICK_INTERVAL;
            if (oldTicks != skill.CooldownRemainingTicks)
                Debug.LogError($"[MON-SKILL-CD] advance {monster.Name}#{monster.EntityId} skill={skill.Path ?? "none"} ticks={oldTicks}->{skill.CooldownRemainingTicks} elapsedTicks={ticks}");
        }

        private void SelectMonsterPrimarySkillForAttack(Monster monster, CombatPlayer target, float dist, float fallbackRange, string source)
        {
            if (monster == null) return;
            monster.UsePrimaryActiveSkillThisAttack = false;
            IReadOnlyList<MonsterActiveSkillRuntime> skillList = monster.ActiveSkills != null && monster.ActiveSkills.Count > 0
                ? monster.ActiveSkills
                : null;
            if ((skillList == null || skillList.Count == 0) && string.IsNullOrWhiteSpace(monster.PrimaryActiveSkillPath))
                return;

            float now = GetNativeCombatTime();
            if (skillList != null && skillList.Count > 0)
            {
                foreach (var skill in skillList)
                    AdvanceMonsterSkillCooldown(monster, skill, now);
            }
            else
                AdvanceMonsterPrimarySkillCooldown(monster, now);

            var skillRng = GetRoomRngForMonster(monster);
            if (monster.State == MonsterState.Combat && skillRng != null)
            {
                uint gateRaw = NativeRngLedger.Generate(skillRng, "room", "MonsterBehavior2::UpdateSkills:combat-gate", $"{monster.Name}#{monster.EntityId}");
                uint gateRoll = gateRaw % 100u;
                if (gateRoll > 30u)
                {
                    Debug.LogError($"[MON-SKILL] validate skip=native-gate {monster.Name}#{monster.EntityId}->{target?.Name ?? "unknown"} skill={monster.PrimaryActiveSkillPath} gateRaw=0x{gateRaw:X8} gateRoll={gateRoll} source={source} native=MonsterBehavior2::UpdateSkills");
                    return;
                }
            }
            if (skillList != null && skillList.Count > 0)
            {
                int cooldownBlocked = 0;
                int rangeBlocked = 0;
                int minimumRangeBlocked = 0;
                int targetTypeBlocked = 0;
                int spellUseBlocked = 0;
                int selfHealthBlocked = 0;
                int targetHealthBlocked = 0;
                MonsterActiveSkillRuntime selected = null;
                foreach (var skill in skillList)
                {
                    if (!IsMonsterActiveSkillCombatUse(skill))
                    {
                        spellUseBlocked++;
                        continue;
                    }
                    if (!IsMonsterActiveSkillEnemyTarget(skill))
                    {
                        targetTypeBlocked++;
                        continue;
                    }
                    if (!PassesMonsterActiveSkillHealthPct(monster.CurrentHPWire, monster.MaxHPWire, skill.HasSelfHealthPct, skill.SelfHealthPct))
                    {
                        selfHealthBlocked++;
                        continue;
                    }
                    if (target?.PlayerState != null && !PassesMonsterActiveSkillHealthPct(target.PlayerState.CurrentHPWire, target.PlayerState.MaxHPWire, skill.HasTargetHealthPct, skill.TargetHealthPct))
                    {
                        targetHealthBlocked++;
                        continue;
                    }
                    float candidateRange = ResolveMonsterActiveSkillUseRange(skill, fallbackRange);
                    float minimumRange = ResolveMonsterActiveSkillMinimumRange(skill);
                    if (skill.CooldownRemainingTicks > 0)
                    {
                        cooldownBlocked++;
                        continue;
                    }
                    if (minimumRange >= 0f && dist + NATIVE_CONTACT_RANGE_EPSILON < minimumRange)
                    {
                        minimumRangeBlocked++;
                        continue;
                    }
                    if (dist > candidateRange + NATIVE_CONTACT_RANGE_EPSILON)
                    {
                        rangeBlocked++;
                        continue;
                    }
                    selected = skill;
                    break;
                }

                if (selected == null)
                {
                    Debug.LogError($"[MON-SKILL] validate skip=no-candidate {monster.Name}#{monster.EntityId}->{target?.Name ?? "unknown"} active={skillList.Count} cooldownBlocked={cooldownBlocked} rangeBlocked={rangeBlocked} minimumRangeBlocked={minimumRangeBlocked} targetTypeBlocked={targetTypeBlocked} spellUseBlocked={spellUseBlocked} selfHealthBlocked={selfHealthBlocked} targetHealthBlocked={targetHealthBlocked} dist={dist:F1} source={source} native=MonsterBehavior2::UpdateSkills");
                    return;
                }

                uint targetRaw = skillRng != null
                    ? NativeRngLedger.Generate(skillRng, "room", "MonsterBehavior2::UpdateSkills:target-candidate-select", $"{monster.Name}#{monster.EntityId}")
                    : 0u;
                ApplyMonsterActiveSkillSelection(monster, selected);
                monster.UsePrimaryActiveSkillThisAttack = true;
                float selectedRange = ResolveMonsterActiveSkillUseRange(selected, fallbackRange);
                float selectedMinimumRange = ResolveMonsterActiveSkillMinimumRange(selected);
                Debug.LogError($"[MON-SKILL] validate use {monster.Name}#{monster.EntityId}->{target?.Name ?? "unknown"} skill={selected.Path} id={selected.Id} skillOrder=native-first targetCandidate=0/1 targetRaw=0x{targetRaw:X8} dist={dist:F1} useRange={selectedRange:F1} minRange={selectedMinimumRange:F1} authoredRange={selected.Range:F1} targetType={selected.TargetType ?? ""} spellUse={selected.SpellUse ?? ""} cooldownTicks={selected.CooldownTicks} source={source} native=MonsterBehavior2::UpdateSkills");
                return;
            }

            float skillRange = monster.PrimaryActiveSkillSpellUseRange > 0f
                ? monster.PrimaryActiveSkillSpellUseRange
                : (monster.PrimaryActiveSkillRange > 0f ? monster.PrimaryActiveSkillRange : fallbackRange);
            float skillMinimumRange = monster.PrimaryActiveSkillMinimumRange;
            if (!IsCombatSpellUse(monster.PrimaryActiveSkillSpellUse))
            {
                Debug.LogError($"[MON-SKILL] validate skip=spell-use {monster.Name}#{monster.EntityId}->{target?.Name ?? "unknown"} skill={monster.PrimaryActiveSkillPath} spellUse={monster.PrimaryActiveSkillSpellUse ?? ""} dist={dist:F1} range={skillRange:F1} source={source} native=MonsterBehavior2::UpdateSkills");
                return;
            }
            if (!IsEnemyTargetType(monster.PrimaryActiveSkillTargetType))
            {
                Debug.LogError($"[MON-SKILL] validate skip=target-type {monster.Name}#{monster.EntityId}->{target?.Name ?? "unknown"} skill={monster.PrimaryActiveSkillPath} targetType={monster.PrimaryActiveSkillTargetType ?? ""} dist={dist:F1} range={skillRange:F1} source={source} native=MonsterBehavior2::UpdateSkills");
                return;
            }
            if (!PassesMonsterActiveSkillHealthPct(monster.CurrentHPWire, monster.MaxHPWire, monster.PrimaryActiveSkillHasSelfHealthPct, monster.PrimaryActiveSkillSelfHealthPct))
            {
                Debug.LogError($"[MON-SKILL] validate skip=self-hp {monster.Name}#{monster.EntityId}->{target?.Name ?? "unknown"} skill={monster.PrimaryActiveSkillPath} hp={monster.CurrentHPWire}/{monster.MaxHPWire} pctLimit={monster.PrimaryActiveSkillSelfHealthPct:F1} dist={dist:F1} range={skillRange:F1} source={source} native=MonsterBehavior2::UpdateSkills");
                return;
            }
            if (target?.PlayerState != null && !PassesMonsterActiveSkillHealthPct(target.PlayerState.CurrentHPWire, target.PlayerState.MaxHPWire, monster.PrimaryActiveSkillHasTargetHealthPct, monster.PrimaryActiveSkillTargetHealthPct))
            {
                Debug.LogError($"[MON-SKILL] validate skip=target-hp {monster.Name}#{monster.EntityId}->{target?.Name ?? "unknown"} skill={monster.PrimaryActiveSkillPath} hp={target.PlayerState.CurrentHPWire}/{target.PlayerState.MaxHPWire} pctLimit={monster.PrimaryActiveSkillTargetHealthPct:F1} dist={dist:F1} range={skillRange:F1} source={source} native=MonsterBehavior2::UpdateSkills");
                return;
            }
            if (monster.PrimaryActiveSkillCooldownRemainingTicks > 0)
            {
                Debug.LogError($"[MON-SKILL] validate skip=cooldown {monster.Name}#{monster.EntityId}->{target?.Name ?? "unknown"} skill={monster.PrimaryActiveSkillPath} remaining={monster.PrimaryActiveSkillCooldownRemainingTicks}/{monster.PrimaryActiveSkillCooldownTicks} dist={dist:F1} range={skillRange:F1} source={source}");
                return;
            }
            if (skillMinimumRange >= 0f && dist + NATIVE_CONTACT_RANGE_EPSILON < skillMinimumRange)
            {
                Debug.LogError($"[MON-SKILL] validate skip=min-range {monster.Name}#{monster.EntityId}->{target?.Name ?? "unknown"} skill={monster.PrimaryActiveSkillPath} dist={dist:F1} minRange={skillMinimumRange:F1} range={skillRange:F1} source={source}");
                return;
            }
            if (dist > skillRange + NATIVE_CONTACT_RANGE_EPSILON)
            {
                Debug.LogError($"[MON-SKILL] validate skip=range {monster.Name}#{monster.EntityId}->{target?.Name ?? "unknown"} skill={monster.PrimaryActiveSkillPath} dist={dist:F1} range={skillRange:F1} source={source}");
                return;
            }

            monster.UsePrimaryActiveSkillThisAttack = true;
            uint fallbackTargetRaw = skillRng != null
                ? NativeRngLedger.Generate(skillRng, "room", "MonsterBehavior2::UpdateSkills:target-candidate-select", $"{monster.Name}#{monster.EntityId}")
                : 0u;
            Debug.LogError($"[MON-SKILL] validate use {monster.Name}#{monster.EntityId}->{target?.Name ?? "unknown"} skill={monster.PrimaryActiveSkillPath} id={monster.PrimaryActiveSkillId} targetCandidate=0/1 targetRaw=0x{fallbackTargetRaw:X8} dist={dist:F1} useRange={skillRange:F1} minRange={skillMinimumRange:F1} authoredRange={monster.PrimaryActiveSkillRange:F1} targetType={monster.PrimaryActiveSkillTargetType ?? ""} spellUse={monster.PrimaryActiveSkillSpellUse ?? ""} cooldownTicks={monster.PrimaryActiveSkillCooldownTicks} source={source} native=MonsterBehavior2::UpdateSkills");
        }

        private static float ResolveMonsterActiveSkillUseRange(MonsterActiveSkillRuntime skill, float fallbackRange)
        {
            if (skill == null) return fallbackRange;
            if (skill.SpellUseRange > 0f) return skill.SpellUseRange;
            if (skill.Range > 0f) return skill.Range;
            return fallbackRange;
        }

        private static float ResolveMonsterActiveSkillMinimumRange(MonsterActiveSkillRuntime skill)
        {
            return skill != null ? skill.SpellUseMinimumRange : -1f;
        }

        private static bool IsMonsterActiveSkillCombatUse(MonsterActiveSkillRuntime skill)
        {
            return IsCombatSpellUse(skill?.SpellUse);
        }

        private static bool IsMonsterActiveSkillEnemyTarget(MonsterActiveSkillRuntime skill)
        {
            return IsEnemyTargetType(skill?.TargetType);
        }

        private static bool IsCombatSpellUse(string spellUse)
        {
            return string.IsNullOrWhiteSpace(spellUse)
                || spellUse.Equals("COMBAT", StringComparison.OrdinalIgnoreCase)
                || spellUse.Equals("1", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsEnemyTargetType(string targetType)
        {
            return string.IsNullOrWhiteSpace(targetType)
                || targetType.Equals("ENEMY", StringComparison.OrdinalIgnoreCase)
                || targetType.Equals("2", StringComparison.OrdinalIgnoreCase);
        }

        private static bool PassesMonsterActiveSkillHealthPct(uint currentHPWire, uint maxHPWire, bool hasLimit, float limit)
        {
            if (!hasLimit)
                return true;
            if (maxHPWire == 0)
                return false;
            float pct = currentHPWire * 100f / maxHPWire;
            return pct <= limit + 0.0001f;
        }

        public void CommitMonsterPrimarySkillUse(Monster monster, string source)
        {
            if (monster == null || !monster.UsePrimaryActiveSkillThisAttack || string.IsNullOrWhiteSpace(monster.PrimaryActiveSkillPath))
                return;
            var selected = monster.SelectedActiveSkill;
            if (selected != null && selected.CooldownTicks > 0)
            {
                selected.CooldownRemainingTicks = selected.CooldownTicks;
                selected.CooldownLastTime = GetNativeCombatTime();
                ApplyMonsterActiveSkillSelection(monster, selected);
            }
            else if (monster.PrimaryActiveSkillCooldownTicks > 0)
            {
                monster.PrimaryActiveSkillCooldownRemainingTicks = monster.PrimaryActiveSkillCooldownTicks;
                monster.PrimaryActiveSkillCooldownLastTime = GetNativeCombatTime();
            }
            Debug.LogError($"[MON-SKILL-CD] set {monster.Name}#{monster.EntityId} skill={monster.PrimaryActiveSkillPath} ticks={monster.PrimaryActiveSkillCooldownRemainingTicks} source={source ?? "unknown"}");
        }

        private void ProcessMonsterAttacks(float deltaTime)
        {
            ProcessMonsterAttacks(deltaTime, 0);
        }

        private void ProcessMonsterAttacks(float deltaTime, uint playerEntityId, bool allowNewAttacks = true)
        {
            ProcessMonsterAttacks(deltaTime, playerEntityId, allowNewAttacks, null);
        }

        private void ProcessMonsterAttacks(float deltaTime, uint playerEntityId, bool allowNewAttacks, Monster onlyMonster, float nativeNow = -1f)
        {
            float now = nativeNow >= 0f ? nativeNow : GetNativeCombatTime();

            DrainDueMonsterProjectileImpacts(now);

            foreach (var monster in SelectMonsters(onlyMonster))
            {
                if (GetRoomRngForMonster(monster) == null)
                    continue;
                AdvanceMonsterPrimarySkillCooldown(monster, now);
                TryAssistFromAlertSource(monster, "attack-loop");
                bool pendingClientVisibleAttack = monster.AttackPending && monster.AttackClientVisible;
                bool pendingRuntimeAttack = monster.AttackPending && monster.AttackCommitTime > 0f;
                if (!monster.AggroTriggered) continue;
                if (monster.DeathPendingClientConfirmation
                    && !(pendingRuntimeAttack && monster.AttackNativeContactOnly && !monster.AttackHitResolved))
                {
                    CancelMonsterPendingAttack(monster, "monster_death_pending_client_confirmation");
                    continue;
                }
                if (!monster.IsAlive && !pendingClientVisibleAttack && !pendingRuntimeAttack)
                {
                    monster.AttackPending = false;
                    monster.AttackSoundPending = false;
                    monster.AttackHitResolved = false;
                    monster.AttackStartedTime = 0f;
                    monster.AttackEndTime = 0f;
                    monster.AttackCommitTime = 0f;
                    monster.AttackSoundTime = 0f;
                    continue;
                }
                if (playerEntityId != 0 && monster.TargetId != playerEntityId) continue;
                if (!_players.TryGetValue(monster.TargetId, out var target) || !target.IsAlive || target.PlayerState == null) continue;
                if (target.PlayerState.IsZoneSpawnDamageImmune)
                {
                    CancelMonsterPendingAttack(monster, "target_zone_spawn_invulnerability");
                    continue;
                }
                if (target.PlayerState.CurrentHPWire == 0 && target.PlayerState.SynchHP == 0)
                {
                    target.IsAlive = false;
                    CancelMonsterPendingAttack(monster, "target_dead");
                    continue;
                }
                float allowedRange = ResolveMonsterEffectiveAttackRange(monster);
                if (allowedRange <= 0f) continue;
                float dist = Distance2D(monster.PosX, monster.PosY, target.PosX, target.PosY);
                bool attackPathClear = IsMonsterAttackPathClear(monster, target, null);
                bool nativeTargetAction = HasNativeMonsterTargetAction(monster, target, dist);
                bool initUseGateway = HasMonsterInitUseGateway(monster, target);
                TraceMonsterState(monster, "attack-loop", target, dist, allowedRange, monster.AttackPending ? "pending" : "ready");

                if (monster.AttackPending)
                {
                    if (monster.AttackCommitTime <= 0f)
                    {
                        if (!attackPathClear)
                        {
                            CancelMonsterPendingAttack(monster, $"world_blocked dist={dist:F1} range={allowedRange:F1}");
                            if (monster.IsAlive)
                                monster.State = MonsterState.Chase;
                            Debug.LogError($"[MON-DAMAGE] {monster.Name}#{monster.EntityId}->{target.Name} cancel worldBlocked dist={dist:F1} monsterRange={allowedRange:F1}");
                            OnMonsterAttackResolved?.Invoke(monster, target, false, target.PlayerState.CurrentHPWire);
                            continue;
                        }
                        if (!monster.IsAlive)
                        {
                            CancelMonsterPendingAttack(monster, "dead_unarmed");
                            continue;
                        }
                        SelectMonsterPrimarySkillForAttack(monster, target, dist, allowedRange, "pending-start");
                        if (!initUseGateway && !monster.UsePrimaryActiveSkillThisAttack)
                        {
                            if (nativeTargetAction)
                                TraceFarTargetAction(monster, target, dist, allowedRange, "pending-start");
                            DelayMonsterAttackRetry(monster, "init_use_gateway_pending_start");
                            CancelMonsterPendingAttack(monster, $"init_use_gateway_pending_start dist={dist:F1} range={allowedRange:F1} targetRange={ResolveMonsterTargetSearchRange(monster, true):F1}");
                            if (monster.IsAlive)
                                monster.State = MonsterState.Chase;
                            continue;
                        }
                        if (!monster.AttackClientVisible)
                        {
                            int handlerCount = OnMonsterAttackStarted?.GetInvocationList().Length ?? 0;
                            Debug.LogError($"[MON-ATTACK] dispatch start {monster.Name}->{target.Name} session={monster.AttackSessionId} handlers={handlerCount} dist={dist:F1} range={allowedRange:F1}");
                            OnMonsterAttackStarted?.Invoke(monster, target, monster.AttackSessionId);
                            if (!monster.AttackPending)
                            {
                                Debug.LogError($"[MON-ATTACK] {monster.Name}->{target.Name} START canceled session={monster.AttackSessionId}");
                                continue;
                            }
                        }
                        ArmMonsterRuntimeAttack(monster, target, "START", now);
                        continue;
                    }
                    if (monster.AttackHitResolved)
                    {
                        if (now < monster.AttackEndTime) continue;
                        monster.AttackPending = false;
                        monster.AttackClientVisible = false;
                        monster.AttackNativeContactOnly = false;
                        monster.AttackSoundPending = false;
                        monster.AttackHitResolved = false;
                        monster.AttackStartedTime = 0f;
                        monster.AttackEndTime = 0f;
                        monster.AttackCommitTime = 0f;
                        monster.AttackSoundTime = 0f;
                        if (monster.IsAlive)
                            monster.State = MonsterState.Combat;
                        TraceMonsterState(monster, "attack-complete", target, dist, allowedRange, "end");
                        continue;
                    }
                    if (monster.AttackSoundPending && now >= monster.AttackSoundTime)
                        ConsumeMonsterAttackSoundRng(monster);
                    if (now < monster.AttackCommitTime) continue;
                    if (monster.AttackSoundPending)
                        ConsumeMonsterAttackSoundRng(monster);
                    monster.AttackHitResolved = true;
                    if (monster.AttackEndTime < now)
                        monster.AttackEndTime = now;
                    if (!TryDeferMonsterProjectileImpact(monster, target, dist, "MON-DAMAGE", "ProcessMonsterAttacks", now))
                        ResolveMonsterAttackDamage(monster, target, dist, "MON-DAMAGE", "ProcessMonsterAttacks");
                    continue;
                }
                else
                {
                    if (!monster.IsAlive) continue;
                    if (!allowNewAttacks) continue;
                    if (!attackPathClear)
                    {
                        ClearMonsterCombatContact(monster, target);
                        continue;
                    }
                    SelectMonsterPrimarySkillForAttack(monster, target, dist, allowedRange, "new-start");
                    if (!initUseGateway && !monster.UsePrimaryActiveSkillThisAttack)
                    {
                        if (nativeTargetAction)
                            TraceFarTargetAction(monster, target, dist, allowedRange, "new-start");
                        if (monster.State == MonsterState.Combat)
                            monster.State = MonsterState.Chase;
                        continue;
                    }
                    if (now < monster.LastAttackTime) continue;
                    monster.AttackPending = true;
                    monster.AttackClientVisible = false;
                    monster.AttackNativeContactOnly = false;
                    monster.AttackHitResolved = false;
                    monster.AttackSoundRaw = 0;
                    monster.AttackSoundGateRaw = 0;
                    monster.AttackSoundRepeatRaw = 0;
                    monster.AttackUseRaw = 0;
                    monster.AttackStartedTime = now;
                    monster.AttackCommitTime = 0f;
                    monster.AttackSoundTime = 0f;
                    monster.AttackEndTime = 0f;
                    monster.AttackSoundPending = false;
                    monster.AttackCommitTargetX = target.PosX;
                    monster.AttackCommitTargetY = target.PosY;
                    monster.State = MonsterState.Attacking;
                    monster.NativeAi?.SetState(NativeMonsterStateId.Attack, "DoAttackAction");
                    if (monster.NativeAi != null)
                        monster.NativeAi.SkillDelayTimer = 300;
                    NativeEncounterMarkActive(monster, "DoAttackAction");
                    monster.AttackSessionId++;
                    if (monster.AttackSessionId == 0) monster.AttackSessionId = 1;
                    int handlerCount = OnMonsterAttackStarted?.GetInvocationList().Length ?? 0;
                    Debug.LogError($"[MON-ATTACK] dispatch start {monster.Name}->{target.Name} session={monster.AttackSessionId} handlers={handlerCount} dist={dist:F1} range={allowedRange:F1}");
                    TraceMonsterState(monster, "attack-start", target, dist, allowedRange, "dispatch");
                    OnMonsterAttackStarted?.Invoke(monster, target, monster.AttackSessionId);
                    if (monster.AttackPending)
                        ArmMonsterRuntimeAttack(monster, target, "START", now);
                    else
                        Debug.LogError($"[MON-ATTACK] {monster.Name}->{target.Name} START canceled session={monster.AttackSessionId}");
                    continue;
                }
            }
        }

        private void ArmMonsterClientVisibleAttack(Monster monster, CombatPlayer target, float nativeNow = -1f)
        {
            ArmMonsterRuntimeAttack(monster, target, "START", nativeNow);
        }

        private void ArmMonsterRuntimeAttack(Monster monster, CombatPlayer target, string marker, float nativeNow = -1f)
        {
            if (monster == null || target == null) return;
            float now = nativeNow >= 0f ? nativeNow : GetNativeCombatTime();
            if (monster.AttackStartedTime <= 0f)
                monster.AttackStartedTime = now;
            if (monster.AttackCommitTime > 0f)
                return;
            ConsumeMonsterAttackSearchRng(monster, target, marker);
            AdvanceMonsterAttackAnimation(monster);
            float windup = ResolveMonsterAttackWindup(monster);
            ResolveMonsterAttackAnimationFrames(monster, out int totalFrames, out int hitFrame, out int soundFrame);
            float startTime = monster.AttackStartedTime > 0f ? monster.AttackStartedTime : now;
            monster.AttackCommitTime = startTime + windup;
            monster.AttackSoundTime = startTime + ResolveMonsterAttackSoundDelay(monster, windup);
            monster.AttackEndTime = startTime + Mathf.Max(windup, ResolveMonsterAttackFrameSeconds(monster, totalFrames));
            monster.LastAttackTime = startTime + ResolveMonsterAttackCooldownSeconds(monster);
            monster.AttackHitResolved = false;
            monster.AttackSoundPending = monster.HasAttackSound || monster.AttackWeaponSoundCount > 0 || monster.AttackRepeatSoundCount > 0;
            Debug.LogError($"[MON-ATTACK] {monster.Name}->{target.Name} {marker} anim={monster.AttackAnimationIndex} session={monster.AttackSessionId} use=0x{monster.AttackUseRaw:X8} frames={totalFrames}/{hitFrame}/{soundFrame} soundAt={monster.AttackSoundTime:F3} hitAt={monster.AttackCommitTime:F3} endAt={monster.AttackEndTime:F3} weaponSounds={monster.AttackWeaponSoundCount} attackSounds={monster.AttackRepeatSoundCount}");
            TraceMonsterState(monster, "attack-arm", target, -1f, ResolveMonsterEffectiveAttackRange(monster), marker);
        }

        public void Update(float deltaTime)
        {
            Update(deltaTime, true);
        }

        public void Update(float deltaTime, bool allowNewMonsterAttacks)
        {
            TraceCombatTick("Update", deltaTime, allowNewMonsterAttacks);
            ProcessProximityAggro();
            ProcessMonsterAssistAlerts();
            float nativeNow = GetNativeCombatTime();
            ProcessMonsterAttacks(deltaTime, 0, allowNewMonsterAttacks, null, nativeNow);
            AdvancePlayerDamageModifierRuntime(0, nativeNow, "Update");
            AdvanceMonsterModifierRuntime(null, nativeNow, "Update");
            ProcessMonsterMovement(deltaTime);
            UpdateNativeMaintenance(deltaTime);
        }

        public void UpdateNativeMonsterEntity(uint entityId, float deltaTime, bool allowNewMonsterAttacks)
        {
            UpdateNativeMonsterEntity(entityId, deltaTime, allowNewMonsterAttacks, GetNativeCombatTime());
        }

        public void UpdateNativeMonsterEntity(uint entityId, float deltaTime, bool allowNewMonsterAttacks, float nativeNow)
        {
            if (!_activeMonsters.TryGetValue(entityId, out var monster))
                return;
            TraceCombatTick("UpdateNativeMonsterEntity", deltaTime, allowNewMonsterAttacks, monster);
            if (ShouldRunNativeProximityScanThisTick(monster))
                ProcessProximityAggro(0, monster);
            ProcessMonsterAssistAlerts(monster);
            ProcessMonsterAttacks(deltaTime, 0, allowNewMonsterAttacks, monster, nativeNow);
            AdvanceMonsterModifierRuntimeForTarget(entityId, GetRoomRngForMonster(monster), nativeNow, "UpdateNativeMonsterEntity");
            ProcessMonsterMovement(deltaTime, 0, monster);
        }

        public void UpdateNativeMaintenance(float deltaTime)
        {
            ProcessNativeDeathLifecycles(deltaTime);
            for (int i = _respawnQueue.Count - 1; i >= 0; i--)
            {
                if (Time.time >= _respawnQueue[i].RespawnTime)
                {
                    SpawnMonster(_respawnQueue[i].GCType, _respawnQueue[i].PosX, _respawnQueue[i].PosY, _respawnQueue[i].PosZ, _respawnQueue[i].Heading, _respawnQueue[i].ZoneName, _respawnQueue[i].EncounterGroupKey, _respawnQueue[i].EncounterDifficulty, null, _respawnQueue[i].InstanceKey);
                    _respawnQueue.RemoveAt(i);
                }
            }
        }

        public void ClearAll()
        {
            foreach (var id in new List<uint>(_activeMonsters.Keys))
                DespawnMonster(id, false);
            _respawnQueue.Clear();
            _players.Clear();
            _playerCombatAdvanceTime.Clear();
            _monsterRuntimeHPWire.Clear();
            _monsterHPAuthority.Clear();
            _monsterRuntimeDamageCommitted.Clear();
            _monsterHPRegenLastTime.Clear();
            _monsterHPRegenCarryWire.Clear();
            _monsterHPRegenCooldownTicks.Clear();
            _monsterManaRegenLastTime.Clear();
            _monsterManaRegenCooldownTicks.Clear();
            _monsterStateTraceSignatures.Clear();
            _nativeEncounterRuntimes.Clear();
            _activeMonsterModifiers.Clear();
            _activePlayerDamageModifiers.Clear();
            _playerModifierNetworkIds.Clear();
            _pendingModifierKills.Clear();
            _monsterFarTargetActionLogTime.Clear();
            _pendingMonsterProjectiles.Clear();
            _nextMonsterProjectileSequence = 0;
            _nativeEntityOrder.Clear();
            _nativeEntityOrderSet.Clear();
            _hasCompletedNativeEntityUpdate = false;
            _lastCompletedNativeEntityUpdateTick = 0;
            _lastCompletedNativeEntityUpdateTime = -1f;
            _hasCompletedNativeSubEntityUpdate = false;
            _lastCompletedNativeSubEntityUpdateTick = 0;
            _lastCompletedNativeSubEntityUpdateTime = -1f;
        }

        private float GetAggroRangeForTier(string tier)
        {
            return 30f;
        }

        private byte GetLevelForTier(string tier)
        {
            if (string.IsNullOrEmpty(tier))
                return 1;

            return tier.ToUpper() switch
            {
                "FODDER" => 0,
                "RECRUIT" => 1,
                "VETERAN" => 2,
                "CHAMPION" => 4,
                "HERO" => 6,
                "WARMONGER" => 8,
                _ => 1
            };
        }

        private byte GetZoneBaseLevel(string zoneName)
        {
            if (string.IsNullOrEmpty(zoneName)) return 1;

            string lower = zoneName.ToLower();

            if (lower.Contains("tutorial")) return 1;

            if (lower.StartsWith("dungeon") && lower.Length >= 9)
            {
                if (int.TryParse(lower.Substring(7, 2), out int dungeonNum))
                {
                    // dungeon00 = 1, dungeon01 = 5, dungeon02 = 9, etc.
                    return (byte)(dungeonNum * 4 + 1);
                }
            }

            return 1;
        }

        private float GetLeashRangeForTier(string tier)
        {
            return tier?.ToLower() switch
            {
                "grunt" => 25f,
                "champion" => 30f,
                "hero" => 35f,
                "boss" => 50f,
                _ => 30f
            };
        }

        public Monster GetMonsterByBehaviorId(uint behaviorId)
        {
            foreach (var monster in _activeMonsters.Values)
            {
                if (monster.BehaviorId == behaviorId)
                    return monster;
            }
            return null;
        }

        public Monster GetMonsterByManipulatorsId(uint manipulatorsId)
        {
            foreach (var monster in _activeMonsters.Values)
            {
                if (monster.ManipulatorsId == manipulatorsId)
                    return monster;
            }
            return null;
        }

        public Monster GetMonsterBySkillsId(uint skillsId)
        {
            foreach (var monster in _activeMonsters.Values)
            {
                if (monster.SkillsId == skillsId)
                    return monster;
            }
            return null;
        }

        private float GetMoveSpeedFromCreature(CreatureData creature)
        {
            if (!string.IsNullOrEmpty(creature.speed) &&
                float.TryParse(creature.speed, out float speed))
            {
                float serverSpeed = speed;
                Debug.LogError($"[CombatManager] {creature.gcType} MoveSpeed from DB: {speed} -> {serverSpeed}");
                return serverSpeed;
            }

            return 30f;
        }
        public List<Monster> GetMonstersInRange(float x, float y, float range)
        {
            return GetMonstersInRange(x, y, range, null);
        }

        public List<Monster> GetMonstersInRange(float x, float y, float range, string instanceKey)
        {
            var result = new List<Monster>();
            float rangeSq = range * range;
            string normalizedInstanceKey = string.IsNullOrWhiteSpace(instanceKey)
                ? null
                : NativeRoomRuntime.NormalizeInstanceKey(instanceKey);
            foreach (var monster in GetAllMonsters())
            {
                if (!monster.IsAlive) continue;
                if (!MatchesInstance(monster, normalizedInstanceKey)) continue;
                float dx = monster.PosX - x;
                float dy = monster.PosY - y;
                if (dx * dx + dy * dy <= rangeSq)
                    result.Add(monster);
            }
            return result;
        }

        public List<Monster> GetMonstersInClientVisibleRange(float x, float y, float range, string instanceKey, float nativeNow)
        {
            var result = new List<Monster>();
            float rangeSq = range * range;
            string normalizedInstanceKey = string.IsNullOrWhiteSpace(instanceKey)
                ? null
                : NativeRoomRuntime.NormalizeInstanceKey(instanceKey);
            foreach (var monster in GetAllMonsters())
            {
                if (!monster.IsAlive) continue;
                if (!MatchesInstance(monster, normalizedInstanceKey)) continue;

                float rawDx = monster.PosX - x;
                float rawDy = monster.PosY - y;
                if (rawDx * rawDx + rawDy * rawDy <= rangeSq)
                {
                    result.Add(monster);
                    continue;
                }

                if (!TryGetMonsterClientVisiblePosition(monster, nativeNow, out float visibleX, out float visibleY))
                    continue;
                float visibleDx = visibleX - x;
                float visibleDy = visibleY - y;
                if (visibleDx * visibleDx + visibleDy * visibleDy <= rangeSq)
                    result.Add(monster);
            }
            return result;
        }
        private class RespawnEntry
        {
            public string GCType;
            public string ZoneName;
            public string InstanceKey;
            public float PosX, PosY, PosZ;
            public float Heading;
            public string EncounterGroupKey;
            public float EncounterDifficulty;
            public float RespawnTime;
        }
    }

    public class PendingMonsterProjectileImpact
    {
        public long Sequence;
        public uint MonsterEntityId;
        public uint TargetEntityId;
        public string Marker;
        public string Source;
        public float Dist;
        public int FireTick;
        public int FlightTicks;
        public int DueTick;
        public float DueTime;
    }

    public class CombatPlayer
    {
        public uint EntityId;
        public string Name;
        public PlayerState PlayerState;
        public float PosX, PosY;
        public bool IsAlive = true;
        public bool HasActiveClientAttack;
        public uint ActiveClientAttackTargetId;
        public string InstanceKey;
    }

    public class DamageEvent
    {
        public uint AttackerId;
        public uint DefenderId;
        public int DamageAmount;
        public uint DamageWire;
        public bool IsCritical;
        public float PosX, PosY, PosZ;
    }

    public class DamageResult
    {
        public bool Success;
        public int DamageDealt;
        public bool IsCritical;
        public bool DefenderDied;
        public uint NewHPWire;
    }
}
