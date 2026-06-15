using System;
using System.Collections.Generic;
using DungeonRunners.Core;
using DungeonRunners.Engine;

namespace DungeonRunners.Combat
{
    public class Monster
    {
        public uint EntityId;
        public uint BehaviorId;
        public uint SkillsId;
        public uint ManipulatorsId;
        public uint ModifiersId;
        public uint UnitId;
        public int UseTargetCount;
        public string GCType;
        public string BehaviourType;
        public string SpawnBehaviourType;
        public string Name;
        public string Faction;
        public string CreatureType;
        public string Element;
        public string Tier;
        public byte Level = 10;  // CRITICAL - must not be 0 or client crashes!
        public float Difficulty = 1.0f;  // From Tables.gc — XP multiplier (RECRUIT=1.0, VETERAN=2.0, etc)
        public float ExperienceDifficulty = 1.0f;
        public string SpawnGCType;  // dungeon-specific GC path for spawn packet
        public List<string> AuthoredArchetypeAncestry = new List<string>();
        public string InstanceKey;
        public uint MaxHPWire;
        public uint CurrentHPWire;
        public float LastClientHPReportTime;
        public uint LastClientHPReportWire;
        public bool DeathPendingClientConfirmation;
        public float DeathPendingSince;
        public byte NativeDeathState;
        public ushort NativeCorpseTicksRemaining;
        public ushort NativeFadeTicksRemaining;
        public bool NativeDeathLifecycleActive;
        public bool NativeDeathRemoveSent;
        public uint MaxManaWire;
        public uint CurrentManaWire;
        public int BaseDamage;
        public float AttackRating = 1.0f;
        public float DamageMod = 1.0f;
        public float DamageTakenMod = 100f;
        public float DamageVolatility = 0.5f;
        public float WeaponDamage = 1.0f;
        public string WeaponClass = "HTH";
        public string WeaponDamageType = "CRUSHING";
        public bool WeaponUsesProjectile = false;
        public float WeaponProjectileSpeed = 0f;
        public float WeaponProjectileSize = 0f;
        public float WeaponRange = 0f;
        public int NativeWeaponClassId = 1;
        public int NativeDamageTypeId = 0;
        public float HealthRegen = 0f;
        public bool HasAuthoredHealthRegen;
        public float ManaRegen = 0f;
        public bool HasAuthoredManaRegen;
        public float CritChance = 0f;
        public float DefenseRating = 1.0f;
        public float DamageImmunity = 0f;
        public float DamageResist = 0f;
        public float CrushingResist = 0f;
        public float PiercingResist = 0f;
        public float SlashingResist = 0f;
        public float DivineResist = 0f;
        public float FireResist = 0f;
        public float IceResist = 0f;
        public float PoisonResist = 0f;
        public float ShadowResist = 0f;
        public float MagicDamageResist = 0f;
        public float DivineDamageTakenMod = 100f;
        public float FireDamageTakenMod = 100f;
        public float IceDamageTakenMod = 100f;
        public float PoisonDamageTakenMod = 100f;
        public float ShadowDamageTakenMod = 100f;
        public float PosX, PosY, PosZ;
        public float SpawnPosX, SpawnPosY, SpawnPosZ;
        public bool ClientVisibleMoveInitialized;
        public bool ClientVisibleMoveActive;
        public float ClientVisiblePosX, ClientVisiblePosY;
        public float ClientVisibleMoveTargetX, ClientVisibleMoveTargetY;
        public float ClientVisibleMoveLastTime;
        public byte SessionId;
        public float Heading;

        public float AggroRange = 50f;
        public float PerceptionRange = 0f;
        public float ShoutRange = 0f;
        public float LeashRange = 30f;
        public float AttackRange = 2.5f;
        public float ClientSyncTolerance = 10f;
        public float CollisionRadius = 5f;
        public string AttackType;
        public string IdleAction;
        public string LogicType;
        public string AttackStyle;
        public bool Retreatable;
        public bool Leashed;
        public bool UseIdleTime;
        public bool AutoScan;
        public bool AvoidUnits;
        public bool TurnBeforeMoving;
        public bool PlayerControlled;
        public int CollisionBand;
        public int CollisionPriority;
        public float ScanFrequency;
        public short ProximityScanCountdownTicks = 30;
        public float FleeRange;
        public float RetreatRangeSquared;
        public float TeleportFrequency;
        public float TeleportLimboTime;
        public float BaseTime;
        public float VariableTime;
        public ushort CorpseLingerTicks = 900;
        public bool AutoRespawn = false;
        public ushort RespawnRateTicks = 3600;
        public float AttackSpeed = 1.0f;
        public float AttackCooldown = 1.5f;
        public float AttackLeadDelay = 0.75f;
        public float MoveSpeed = 5f;
        public float WalkSpeed = 25f;
        public float WanderRange = 0f;
        public uint LastStateCounter;

        public uint RngSeed;
        public MersenneTwister Rng;
        public NativeUnitSlotState NativeSlots = new NativeUnitSlotState();
        public NativeMonsterAiRuntime NativeAi = new NativeMonsterAiRuntime();

        // Manipulators from creature database
        public Dictionary<string, ManipulatorData> Manipulators;
        public bool AggroSent { get; set; }

        // UpdateNumber tracking for HP synch - client counts every synch update
        // and expects this counter packed into low byte of HP value
        public byte UpdateNumber = 0;

        private volatile int _stateInt = (int)MonsterState.Idle;
        public MonsterState State
        {
            get => (MonsterState)_stateInt;
            set => _stateInt = (int)value;
        }
        public bool IsAlive = true;
        private volatile uint _targetId = 0;
        public uint TargetId
        {
            get => _targetId;
            set => _targetId = value;
        }
        public float LastAttackTime;
        public bool AttackPending;
        public float AttackCommitTime;
        public float AttackSoundTime;
        public float AttackSoundLeadDelay;
        public bool AttackSoundPending;
        public bool HasAttackSound;
        public uint AttackSoundRaw;
        public uint AttackSoundGateRaw;
        public uint AttackSoundRepeatRaw;
        public byte AttackSessionId;
        public byte AttackAnimationIndex;
        public uint AttackUseRaw;
        public uint AttackSearchTargetId;
        public uint AttackSearchRaw;
        public uint AttackSearchTieRaw;
        public bool AttackClientVisible;
        public float AttackClientVisibleTime;
        public bool AttackNativeContactOnly;
        public bool AttackHitResolved;
        public bool UsePrimaryActiveSkillThisAttack;
        public bool HasMultiAttackStateDraw;
        public ushort MultiAttackBaseCount;
        public ushort MultiAttackCountRange;
        public List<MonsterActiveSkillRuntime> ActiveSkills = new List<MonsterActiveSkillRuntime>();
        public MonsterActiveSkillRuntime SelectedActiveSkill;
        public string PrimaryActiveSkillPath;
        public byte PrimaryActiveSkillId = 10;
        public float PrimaryActiveSkillRange;
        public float PrimaryActiveSkillSpellUseRange;
        public float PrimaryActiveSkillMinimumRange = -1f;
        public string PrimaryActiveSkillTargetType;
        public string PrimaryActiveSkillSpellUse;
        public bool PrimaryActiveSkillHasSelfHealthPct;
        public float PrimaryActiveSkillSelfHealthPct;
        public bool PrimaryActiveSkillHasTargetHealthPct;
        public float PrimaryActiveSkillTargetHealthPct;
        public float PrimaryActiveSkillCooldownSeconds;
        public ushort PrimaryActiveSkillCooldownTicks;
        public ushort PrimaryActiveSkillCooldownRemainingTicks;
        public float PrimaryActiveSkillCooldownLastTime;
        public int PrimaryActiveSkillAnimationId;
        public string PrimaryActiveSkillEffect;
        public string PrimaryActiveSkillCastModifier;
        public float AttackStartedTime;
        public float AttackEndTime;
        public int AttackWeaponSoundCount;
        public int AttackRepeatSoundCount;
        public int[] AttackTotalFrames = new int[] { 0, 0, 0 };
        public int[] AttackHitFrames = new int[] { 0, 0, 0 };
        public int[] AttackSoundFrames = new int[] { 0, 0, 0 };
        public bool[] AttackFrameResolved = new bool[] { false, false, false };
        public NativeResolutionSource AttackTimingSource = NativeResolutionSource.Blocked;
        public string AttackTimingReason = "unresolved";
        public float AttackCommitTargetX;
        public float AttackCommitTargetY;
        public uint CombatContactTargetId;
        public float CombatContactUntil;
        public uint AlertSourceEntityId;
        public float SpawnTime;  // Time.time when spawned - skip first move packet
        private volatile bool _aggroTriggered = false;
        public bool AggroTriggered
        {
            get => _aggroTriggered;
            set => _aggroTriggered = value;
        }
        private Dictionary<uint, int> _threatTable = new Dictionary<uint, int>();
        public string ZoneName;
        public string EncounterGroupKey;
        public NativeEncounterRuntime NativeEncounter;
        public byte NativeEncounterObjectState;
        public byte NativeEncounterLiveUnitCount;
        public byte NativeEncounterReturningUnitCount;
        public ushort NativeEncounterActiveTimer;
        public ushort NativeEncounterScanTimer = 0x1E;
        public bool NativeEncounterScanEnabled = true;
        public int HP => (int)(CurrentHPWire / 256);
        public int MaxHP => (int)(MaxHPWire / 256);
        public void AddThreat(uint playerId, int amount)
        {
            if (_threatTable.ContainsKey(playerId))
                _threatTable[playerId] += amount;
            else
                _threatTable[playerId] = amount;
        }

        public void ClearTarget()
        {
            TargetId = 0;
            AlertSourceEntityId = 0;
            _threatTable.Clear();
        }
    }

    public enum MonsterState
    {
        Idle,       // Binary state 0 — wandering, fidgeting
        Chase,      // Moving toward target
        Combat,     // Binary state 5 — engaged in combat
        Attacking,  // Binary state 6 — actively executing attack
        Return,     // Binary state 7 — returning to spawn
        Dead        // Monster died
    }

    public class MonsterActiveSkillRuntime
    {
        public string Path;
        public byte Id = 10;
        public float Range;
        public float SpellUseRange;
        public float SpellUseMinimumRange = -1f;
        public string TargetType;
        public string SpellUse;
        public bool HasSelfHealthPct;
        public float SelfHealthPct;
        public bool HasTargetHealthPct;
        public float TargetHealthPct;
        public float CooldownSeconds;
        public ushort CooldownTicks;
        public ushort CooldownRemainingTicks;
        public float CooldownLastTime;
        public int AnimationId;
        public string Effect;
        public string CastModifier;
        public bool IsPrimaryAttack;
    }
}
