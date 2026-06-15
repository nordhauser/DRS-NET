using System;
using System.Collections.Generic;
using System.Globalization;
using DungeonRunners.Engine;
using DungeonRunners.Data;
using DungeonRunners.Networking;
using DungeonRunners.Core;

namespace DungeonRunners.Combat
{
    public class WeaponCycleTracker
    {
        private static WeaponCycleTracker _instance;
        public static WeaponCycleTracker Instance => _instance ??= new WeaponCycleTracker();

        private Dictionary<string, WeaponCycle> _activeCycles = new Dictionary<string, WeaponCycle>();
        private readonly Dictionary<string, float> _nextUseReadyByConnection = new Dictionary<string, float>(StringComparer.Ordinal);
        private Queue<CompletedAttack> _completedAttacks = new Queue<CompletedAttack>();
        private readonly List<PendingProjectileHit> _activeProjectiles = new List<PendingProjectileHit>();
        private long _nextProjectileSequence;

        private const int DEFAULT_TOTAL_TICKS = 30;
        private const int DEFAULT_SOUND_POSITION = 10;
        private const int DEFAULT_HIT_POSITION = 15;
        private const float NATIVE_UPDATE_TICK = 1f / 30f;
        public const float NativeUpdateTickSeconds = NATIVE_UPDATE_TICK;
        private const float NATIVE_CONTACT_RANGE_EPSILON = 1f / 16f;
        private const float NATIVE_PROJECTILE_BROAD_SCAN_PADDING = 30f;
        private const int MAX_PENDING_REPEAT_USES = int.MaxValue;
        private static readonly string[] PLAYER_ANIMATION_LIST_PATHS =
        {
            "avatar.races.humanmale.HumanMaleAnimations",
            "avatar.races.humanfemale.HumanFemaleAnimations",
            "HumanMaleAnimations",
            "HumanFemaleAnimations"
        };
        private float Now => CombatManager.Instance.NativeCombatTime;
        public int PendingProjectileEventCount => _activeProjectiles.Count;

        public static int NativeTickIndexFromTime(float time)
        {
            if (time <= 0f) return 0;
            return Mathf.Max(0, Mathf.FloorToInt((time / NATIVE_UPDATE_TICK) + 0.0001f));
        }

        public static int NativeProjectileFlightTicks(float distance, float speed)
        {
            int speedByte = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(0f, speed)), 1, 255);
            long stepDenominator = ((long)speedByte << 16) / 7680L;
            if (stepDenominator < 1L) stepDenominator = 1L;
            long rangeFixed16 = Mathf.RoundToInt(Mathf.Max(0f, distance) * 65536f);
            long flight = rangeFixed16 / stepDenominator;
            return Math.Max(1, (int)(flight >> 8));
        }

        public static float NativeProjectileFlightSeconds(float distance, float speed)
        {
            return NativeProjectileFlightTicks(distance, speed) * NATIVE_UPDATE_TICK;
        }

        public static int NativeProjectileImpactDelayTicks(float distance, float speed)
        {
            return NativeProjectileFlightTicks(distance, speed);
        }

        public static float NativeProjectileImpactDelaySeconds(float distance, float speed)
        {
            return NativeProjectileImpactDelayTicks(distance, speed) * NATIVE_UPDATE_TICK;
        }

        private static int NativeDrainTickFromTime(float time)
        {
            if (time <= 0f) return 0;
            return Mathf.Max(0, Mathf.FloorToInt((time / NATIVE_UPDATE_TICK) + 0.0001f));
        }

        public static float NativeProjectileStepDistance(float speed)
        {
            int stepFixed8 = Mathf.Max(1, Mathf.FloorToInt(Mathf.Max(1f, speed) * 256f / 30f));
            return stepFixed8 / 256f;
        }

        public static float NativeProjectileRadiusFromAuthoredSize(float projectileSize)
        {
            return Mathf.Max(0f, projectileSize);
        }

        public static float NativeProjectileCollisionRadius(float targetCollisionRadius, float projectileSize)
        {
            return Mathf.Max(0f, targetCollisionRadius) + NativeProjectileRadiusFromAuthoredSize(projectileSize);
        }

        public static float NativeProjectileInitialDistance(float speed, float maxDistance)
        {
            if (maxDistance <= 0f) return 0f;
            return Mathf.Min(maxDistance, NativeProjectileStepDistance(speed));
        }

        public static int NativeProjectileLifetimeTicks(float range, float speed)
        {
            float safeSpeed = Mathf.Max(1f, speed);
            return Math.Max(1, Mathf.FloorToInt((Mathf.Max(0f, range) * 30f) / safeSpeed));
        }

        private static NativeWeaponDamageInput CreatePlayerNativeWeaponDamageInput(MersenneTwister rng, PlayerState state, Monster monster, string source)
        {
            int attackerLevel = Math.Max(0, state?.Level ?? 1);
            int defenderLevel = Math.Max(0, monster?.Level ?? attackerLevel);
            return new NativeWeaponDamageInput
            {
                Rng = rng,
                Source = source,
                AttackerLevel = attackerLevel,
                DefenderLevel = defenderLevel,
                AttackRating = DamageComputer.ResolveNativeAvatarAttackRating(state),
                DefenseRating = DamageComputer.ResolveNativeMonsterDefenseRating(monster),
                BlockChance = 0,
                DamageLevel = DamageComputer.ResolveNativeWeaponDamageLevel(state),
                DamageBonus = DamageComputer.ResolveNativeWeaponDamageBonus(state),
                DamageMod = DamageComputer.ResolveNativeDamageMod(state),
                WeaponClassId = DamageComputer.ResolveNativeWeaponClassId(state),
                DamageTypeId = DamageComputer.ResolveNativeDamageTypeId(state),
                WeaponDamageF32 = DamageComputer.GetWeaponBaseDamageF32(state),
                WeaponVolatilityF32 = DamageComputer.GetWeaponVolatilityF32(state),
                CritThreshold = DamageComputer.ResolveNativeCriticalThreshold(state, monster),
                CritDamagePercent = DamageComputer.ResolveNativeCriticalDamagePercent(state),
                AttackerState = state,
                IncludeWeaponDamageAdds = true
            };
        }

        private static MersenneTwister ResolveRoomRng(WeaponCycle cycle)
        {
            if (!string.IsNullOrWhiteSpace(cycle?.InstanceKey))
                return CombatManager.Instance.GetRoomRngForInstance(cycle.InstanceKey);
            RuntimeEvidenceManager.LogFallbackHit("rng-instance", "weapon-cycle-missing-owner", "source=WeaponCycleTracker.ResolveRoomRng", 64);
            Debug.LogError("[RNG-INSTANCE] source=WeaponCycleTracker.ResolveRoomRng reason=missing-cycle-owner rng=null");
            return null;
        }

        private static string ResolveCycleInstanceKey(WeaponCycle cycle, Monster monster = null, RRConnection conn = null, string source = null)
        {
            if (!string.IsNullOrWhiteSpace(cycle?.InstanceKey))
                return cycle.InstanceKey;
            if (!string.IsNullOrWhiteSpace(monster?.InstanceKey))
                return monster.InstanceKey;
            if (!string.IsNullOrWhiteSpace(cycle?.Monster?.InstanceKey))
                return cycle.Monster.InstanceKey;
            RRConnection ownerConn = conn ?? cycle?.Connection;
            if (!string.IsNullOrWhiteSpace(ownerConn?.RuntimeInstanceKey))
                return ownerConn.RuntimeInstanceKey;

            RuntimeEvidenceManager.LogFallbackHit(
                "rng-instance",
                "cycle-missing-instance",
                $"source={source ?? "unknown"} connZone='{ownerConn?.CurrentZoneName ?? ""}' connInstance={ownerConn?.InstanceId ?? 0u}",
                64);
            Debug.LogError($"[RNG-INSTANCE] source={source ?? "unknown"} reason=cycle-missing-instance rng=null connZone='{ownerConn?.CurrentZoneName ?? ""}' connInstance={ownerConn?.InstanceId ?? 0u}");
            return null;
        }

        private static string ResolvePathMapKey(string instanceKey, string zoneName)
        {
            if (!string.IsNullOrWhiteSpace(instanceKey))
                return instanceKey;
            return zoneName;
        }

        private static NativeWeaponDamageInput CloneNativeWeaponDamageInput(NativeWeaponDamageInput input, MersenneTwister rng, string source)
        {
            if (input == null) return null;
            return new NativeWeaponDamageInput
            {
                Rng = rng,
                Source = source,
                AttackerLevel = input.AttackerLevel,
                DefenderLevel = input.DefenderLevel,
                AttackRating = input.AttackRating,
                DefenseRating = input.DefenseRating,
                BlockChance = input.BlockChance,
                DamageLevel = input.DamageLevel,
                DamageBonus = input.DamageBonus,
                DamageMod = input.DamageMod,
                WeaponClassId = input.WeaponClassId,
                DamageTypeId = input.DamageTypeId,
                WeaponDamageF32 = input.WeaponDamageF32,
                WeaponVolatilityF32 = input.WeaponVolatilityF32,
                CritThreshold = input.CritThreshold,
                CritDamagePercent = input.CritDamagePercent,
                AttackerState = input.AttackerState,
                IncludeWeaponDamageAdds = input.IncludeWeaponDamageAdds
            };
        }

        public void RegisterAttack(string connKey, ushort targetId, Monster monster,
            PlayerState playerState, RRConnection conn, bool canStartNow = true, float distance = 0f, float allowedRange = 0f)
        {
            bool nativeProjectileRequest = DamageComputer.IsNativeProjectileWeapon(playerState);
            if (nativeProjectileRequest && canStartNow &&
                (conn == null || !conn.HasActiveUseTarget || conn.ActiveUseTargetId != targetId || !conn.ActiveUseTargetInitUsePassed))
            {
                canStartNow = false;
                Debug.LogError($"[WEAPON-CYCLE] {connKey} ranged UseTarget awaiting native init-use target={targetId} dist={distance:F1} range={allowedRange:F1}");
            }

            if (!_activeCycles.TryGetValue(connKey, out var cycle))
            {
                cycle = new WeaponCycle();
                _activeCycles[connKey] = cycle;
            }

            bool sameMonster = cycle.Monster != null && monster != null && cycle.Monster.EntityId == monster.EntityId;
            bool sameTarget = cycle.TargetId == targetId && sameMonster;
            if ((cycle.IsActive || cycle.AwaitingContact) && sameTarget)
            {
                cycle.Monster = monster;
                cycle.PlayerState = playerState;
                cycle.Connection = conn;
                cycle.InstanceKey = ResolveCycleInstanceKey(cycle, monster, conn, "RegisterAttack-update");
                cycle.Distance = distance;
                cycle.ContactRange = allowedRange;
                CaptureNativeUseTargetState(cycle, conn, distance, allowedRange);
                ApplyNativeUseCooldownLedger(connKey, cycle, Now);
                if (cycle.IsActive && canStartNow)
                {
                    if (IsNativeRangedCycle(cycle))
                    {
                        QueueNativeRepeatUse(connKey, cycle, monster);
                        return;
                    }
                    QueueNativeRepeatUse(connKey, cycle, monster);
                }
                if (canStartNow && cycle.AwaitingContact)
                {
                    cycle.ServerApproachOnly = false;
                    float now = Now;
                    if (!IsNativeUseReady(cycle, now))
                    {
                        if (IsNativeRangedCycle(cycle))
                        {
                            QueueNativeRepeatUse(connKey, cycle, monster);
                            Debug.LogError($"[WEAPON-CYCLE] {connKey} redundant ranged UseTarget while awaiting contact on {monster.Name} nextIn={cycle.NextUseTime - now:F2} native=UseTarget::IsRedundant pendingRepeat={cycle.PendingRepeatUses}");
                        }
                        else
                        {
                            QueueNativeRepeatUse(connKey, cycle, monster);
                        }
                        cycle.IsActive = false;
                        cycle.AwaitingContact = true;
                        cycle.LastTickTime = now;
                        Debug.LogError($"[WEAPON-CYCLE] {connKey} → cooldown hold on {monster.Name} nextIn={cycle.NextUseTime - now:F2}");
                        return;
                    }
                    BeginCycle(connKey, cycle, monster, targetId, now);
                    Debug.LogError($"[WEAPON-CYCLE] {connKey} → CONTACT cycle on {monster.Name} dist={distance:F1} range={allowedRange:F1}");
                }
                else
                {
                    string mode = cycle.AwaitingContact ? "approach" : "continuation swing";
                    Debug.LogError($"[WEAPON-CYCLE] {connKey} → {mode} on {monster.Name} pendingRepeat={cycle.PendingRepeatUses}");
                }
                return;
            }

            cycle.TargetId = targetId;
            cycle.Monster = monster;
            cycle.PlayerState = playerState;
            cycle.Connection = conn;
            cycle.InstanceKey = ResolveCycleInstanceKey(cycle, monster, conn, "RegisterAttack-new");
            cycle.TickCounter = 0;
            ResetSwingRngState(cycle);
            cycle.SwingCount = 0;
            cycle.PendingRepeatUses = 0;
            cycle.Distance = distance;
            cycle.ContactRange = allowedRange;
            CaptureNativeUseTargetState(cycle, conn, distance, allowedRange);
            cycle.ServerApproachOnly = !canStartNow;
            cycle.ContactHoldLogged = false;
            cycle.LastTickTime = Now;
            cycle.CycleStartTime = 0f;
            ApplyNativeUseCooldownLedger(connKey, cycle, cycle.LastTickTime);

            if (canStartNow)
            {
                float now = Now;
                if (!IsNativeUseReady(cycle, now))
                {
                    cycle.IsActive = false;
                    cycle.AwaitingContact = true;
                    cycle.ServerApproachOnly = false;
                    cycle.LastTickTime = now;
                    Debug.LogError($"[WEAPON-CYCLE] {connKey} → cooldown hold on {monster.Name} (ID:{targetId}) nextIn={cycle.NextUseTime - now:F2}");
                    return;
                }
                BeginCycle(connKey, cycle, monster, targetId, now);
                Debug.LogError($"[WEAPON-CYCLE] {connKey} → NEW cycle on {monster.Name} (ID:{targetId})");
            }
            else
            {
                cycle.IsActive = false;
                cycle.AwaitingContact = true;
                Debug.LogError($"[WEAPON-CYCLE] {connKey} → APPROACH intent on {monster.Name} (ID:{targetId}) dist={distance:F1} range={allowedRange:F1}");
            }
        }

        private static void CaptureNativeUseTargetState(WeaponCycle cycle, RRConnection conn, float distance, float allowedRange)
        {
            if (cycle == null) return;
            cycle.InitUsePassed = conn != null && conn.ActiveUseTargetInitUsePassed;
            cycle.InitUseRange = conn != null && conn.ActiveUseTargetInitUseRange > 0f ? conn.ActiveUseTargetInitUseRange : allowedRange;
            cycle.InitUseDistance = conn != null && conn.ActiveUseTargetInitUseDistance > 0f ? conn.ActiveUseTargetInitUseDistance : distance;
            cycle.InitUseTolerance = conn != null ? conn.ActiveUseTargetClientSyncTolerance : 0f;
            cycle.UseTargetComponentId = conn != null ? conn.ActiveUseTargetComponentId : (ushort)0;
            cycle.UseTargetSessionId = conn != null ? conn.ActiveUseTargetSessionId : (byte)0;
        }

        private void QueueNativeRepeatUse(string connKey, WeaponCycle cycle, Monster monster)
        {
            if (cycle == null || monster == null || !monster.IsAlive) return;
            int before = cycle.PendingRepeatUses;
            cycle.PendingRepeatUses = cycle.PendingRepeatUses >= MAX_PENDING_REPEAT_USES ? MAX_PENDING_REPEAT_USES : cycle.PendingRepeatUses + 1;
            bool projectile = IsNativeProjectileRangedCycle(cycle);
            if (cycle.PendingRepeatUses != before)
                Debug.LogError($"[WEAPON-CYCLE] {connKey} queued repeat UseTarget on {monster.Name} pending={cycle.PendingRepeatUses} projectile={projectile} native=UseTarget::IsRedundant nativePendingSlot=Behavior+0x78 coalesced=next-native-use");
            else
                Debug.LogError($"[WEAPON-CYCLE] {connKey} repeat UseTarget coalesced on {monster.Name} pending={cycle.PendingRepeatUses} projectile={projectile} native=UseTarget::IsRedundant nativePendingSlot=Behavior+0x78 coalesced=existing-pending-use");
        }

        public Monster GetActiveTarget(string playerKey)
        {
            if (_activeCycles.TryGetValue(playerKey, out var cycle) && (cycle.IsActive || cycle.AwaitingContact))
                return cycle.Monster;
            return null;
        }

        public void TickAll(MersenneTwister rng)
        {
            TickAll(rng, Now);
        }

        public void TickAll(MersenneTwister rng, float tickNow)
        {
            foreach (var kvp in _activeCycles)
            {
                AdvanceCycleToNow(kvp.Key, kvp.Value, ResolveRoomRng(kvp.Value), tickNow);
            }
        }

        public void TickPlayerEntity(uint playerEntityId, MersenneTwister rng, float tickNow)
        {
            foreach (var kvp in _activeCycles)
            {
                var cycle = kvp.Value;
                if (cycle?.Connection?.Avatar == null || cycle.Connection.Avatar.Id != playerEntityId)
                    continue;
                AdvanceCycleToNow(kvp.Key, cycle, ResolveRoomRng(cycle), tickNow);
                return;
            }
        }

        private void AdvanceCycleToNow(string connKey, WeaponCycle cycle, MersenneTwister rng, float now)
        {
            if (cycle == null) return;
            if (!cycle.IsActive && !cycle.AwaitingContact) return;
            float interval = GetCycleTickInterval(cycle);
            if (cycle.LastTickTime > 0f && now - cycle.LastTickTime + 0.0001f < interval) return;
            float before = cycle.LastTickTime;
            bool wasActive = cycle.IsActive;
            bool wasAwaiting = cycle.AwaitingContact;
            TickCycle(connKey, cycle, rng, now);
            if (before > 0f && cycle.LastTickTime > 0f && now - before > interval + 0.0001f)
            {
                Debug.LogError($"[NATIVE-COMBAT-CLOCK] playerWeaponCycle skippedCatchUp conn={connKey} active={wasActive} awaiting={wasAwaiting} last={before:F3}->{cycle.LastTickTime:F3} now={now:F3} interval={interval:F3}");
            }
        }

        public void FlushPlayerEntityBeforeSynch(uint playerEntityId, MersenneTwister rng, float now, string source)
        {
            if (playerEntityId == 0) return;
            var projectileSummary = DrainDueProjectileImpactsForPlayer(playerEntityId, rng, now, source ?? "FlushPlayerEntityBeforeSynch");
            bool loggedCycle = false;
            foreach (var kvp in _activeCycles)
            {
                var cycle = kvp.Value;
                if (cycle?.Connection?.Avatar == null || cycle.Connection.Avatar.Id != playerEntityId)
                    continue;
                int beforeTick = cycle.TickCounter;
                uint beforeHP = cycle.Monster != null ? CombatManager.Instance.PeekMonsterCurrentHPWire(cycle.Monster) : 0u;
                AdvanceCycleToNow(kvp.Key, cycle, ResolveRoomRng(cycle), now);
                uint afterHP = cycle.Monster != null ? CombatManager.Instance.PeekMonsterCurrentHPWire(cycle.Monster) : beforeHP;
                loggedCycle = true;
                Debug.LogError($"[PRE-SUFFIX-DUE-DRAIN] source={source ?? "unknown"} player={playerEntityId} weaponCycle active={cycle.IsActive} awaiting={cycle.AwaitingContact} tick={beforeTick}->{cycle.TickCounter} target={cycle.Monster?.EntityId ?? 0} hp={beforeHP}->{afterHP} nativeNow={now:F3} projectileDrain={projectileSummary.MatchingDrained}/{projectileSummary.Drained} globalPending={projectileSummary.PendingBefore}->{projectileSummary.PendingAfter} phase=native-advance");
                return;
            }
            if (!loggedCycle && (projectileSummary.Drained > 0 || projectileSummary.Stopped))
                Debug.LogError($"[PRE-SUFFIX-DUE-DRAIN] source={source ?? "unknown"} player={playerEntityId} weaponCycle=False nativeNow={now:F3} projectileDrain={projectileSummary.MatchingDrained}/{projectileSummary.Drained} globalPending={projectileSummary.PendingBefore}->{projectileSummary.PendingAfter} stopped={projectileSummary.Stopped} nextDueTick={projectileSummary.NextDueTick} nextDue={projectileSummary.NextDueTime:F3} phase=native-advance");
        }

        public WeaponCycleFlushResult FlushMonsterEntityBeforeSynch(uint monsterEntityId, MersenneTwister rng, float now, string source)
        {
            var result = new WeaponCycleFlushResult
            {
                TargetEntityId = monsterEntityId,
                PendingBefore = _activeProjectiles.Count
            };
            if (monsterEntityId == 0)
            {
                result.PendingAfter = _activeProjectiles.Count;
                return result;
            }

            Monster monster = CombatManager.Instance.GetMonster(monsterEntityId);
            result.BeforeHPWire = monster != null ? CombatManager.Instance.PeekMonsterCurrentHPWire(monster) : 0u;

            var projectileSummary = DrainDueProjectileImpactsForMonster(monsterEntityId, rng, now, source ?? "FlushMonsterEntityBeforeSynch");
            result.ProjectilesResolved = projectileSummary.MatchingDrained;

            foreach (var kvp in _activeCycles)
            {
                var cycle = kvp.Value;
                if (cycle?.Monster == null || cycle.Monster.EntityId != monsterEntityId)
                    continue;

                result.HadTargetCycle = true;
                int beforeTick = cycle.TickCounter;
                uint beforeHP = CombatManager.Instance.PeekMonsterCurrentHPWire(cycle.Monster);
                AdvanceCycleToNow(kvp.Key, cycle, ResolveRoomRng(cycle), now);
                uint afterHP = CombatManager.Instance.PeekMonsterCurrentHPWire(cycle.Monster);
                result.CycleTicks += Math.Max(0, cycle.TickCounter - beforeTick);
                Debug.LogError($"[PRE-SUFFIX-DUE-DRAIN] source={source ?? "unknown"} monster={monsterEntityId} weaponCycle player={(cycle.Connection?.Avatar != null ? cycle.Connection.Avatar.Id : 0)} active={cycle.IsActive} awaiting={cycle.AwaitingContact} tick={beforeTick}->{cycle.TickCounter} hp={beforeHP}->{afterHP} nativeNow={now:F3} phase=native-advance");
            }

            result.PendingAfter = _activeProjectiles.Count;
            monster = CombatManager.Instance.GetMonster(monsterEntityId);
            result.AfterHPWire = monster != null ? CombatManager.Instance.PeekMonsterCurrentHPWire(monster) : result.BeforeHPWire;
            if (projectileSummary.Drained > 0 || projectileSummary.Stopped)
            {
                Debug.LogError($"[PRE-SUFFIX-DUE-DRAIN] source={source ?? "unknown"} monster={monsterEntityId} projectilesResolved={projectileSummary.MatchingDrained}/{projectileSummary.Drained} pending={result.PendingBefore}->{result.PendingAfter} hp={result.BeforeHPWire}->{result.AfterHPWire} nativeNow={now:F3} nextDueTick={projectileSummary.NextDueTick} nextDue={projectileSummary.NextDueTime:F3}");
            }
            return result;
        }

        public NativeDueDrainSummary TickProjectileEntityPhase(MersenneTwister rng, float tickNow, string source = null)
        {
            var summary = UpdateActiveProjectileSubEntities(rng, tickNow, source ?? "ProjectileEntityUpdate", null);
            if (summary.Drained > 0 || summary.Stopped)
            {
                int nowTick = NativeDrainTickFromTime(tickNow);
                Debug.LogError($"[PROJECTILE-ENTITY] source={source ?? "unknown"} now={tickNow:F3} nowTick={nowTick} drained={summary.Drained} pending={summary.PendingBefore}->{summary.PendingAfter} stopped={summary.Stopped} nextDueTick={summary.NextDueTick} nextDue={summary.NextDueTime:F3}");
            }
            return summary;
        }

        private float GetCycleTickInterval(WeaponCycle cycle)
        {
            return NATIVE_UPDATE_TICK;
        }

        private int GetNativeSpeedField(WeaponCycle cycle)
        {
            float speed = cycle?.PlayerState != null && cycle.PlayerState.WeaponSpeed > 0f ? cycle.PlayerState.WeaponSpeed : 105f;
            float speedPct = DamageComputer.ResolveNativeWeaponAttackSpeedPct(cycle?.PlayerState);
            float scale = 1f + (speedPct / 100f);
            if (scale < 0.05f) scale = 0.05f;
            speed *= scale;
            if (speed <= 1f) speed = 105f;
            int field = Mathf.RoundToInt(speed);
            return Math.Max(1, field);
        }

        private int GetNativeTickPosition(WeaponCycle cycle, int defaultPosition)
        {
            return Math.Max(1, ((defaultPosition * 100) / GetNativeSpeedField(cycle)) + 1);
        }

        private int GetNativeCycleTickCount(WeaponCycle cycle, int defaultPosition)
        {
            return Math.Max(1, (defaultPosition * 100) / GetNativeSpeedField(cycle));
        }

        private int GetNativeCycleTicks(WeaponCycle cycle)
        {
            int totalFrames = cycle != null && cycle.AttackTotalFrames > 0 ? cycle.AttackTotalFrames : DEFAULT_TOTAL_TICKS;
            return GetNativeCycleTickCount(cycle, totalFrames);
        }

        private int GetNativeHitTick(WeaponCycle cycle)
        {
            int hitFrame = cycle != null && cycle.AttackHitFrame > 0 ? cycle.AttackHitFrame : DEFAULT_HIT_POSITION;
            return GetNativeTickPosition(cycle, hitFrame);
        }

        private int GetNativeSoundTick(WeaponCycle cycle)
        {
            int soundFrame = cycle != null && cycle.AttackSoundFrame > 0 ? cycle.AttackSoundFrame : DEFAULT_SOUND_POSITION;
            return GetNativeTickPosition(cycle, soundFrame);
        }

        private int GetNativeHitEventTick(WeaponCycle cycle)
        {
            return GetNativeHitTick(cycle);
        }

        private int GetNativeSoundEventTick(WeaponCycle cycle)
        {
            return GetNativeSoundTick(cycle);
        }

        private float GetNativeCooldownSeconds(WeaponCycle cycle)
        {
            int ticks = GetNativeCooldownTicks(cycle);
            return ticks * NATIVE_UPDATE_TICK;
        }

        private int GetNativeCooldownTicks(WeaponCycle cycle)
        {
            return DamageComputer.ResolveNativeBasicAttackCooldownTicks(cycle?.PlayerState);
        }

        private bool IsNativeUseReady(WeaponCycle cycle, float now)
        {
            return cycle == null || cycle.NextUseTime <= 0f || now + 0.0001f >= cycle.NextUseTime;
        }

        private void ApplyNativeUseCooldownLedger(string connKey, WeaponCycle cycle, float now)
        {
            if (string.IsNullOrEmpty(connKey) || cycle == null)
                return;
            if (!_nextUseReadyByConnection.TryGetValue(connKey, out float readyAt))
                return;
            if (readyAt <= now + 0.0001f)
            {
                _nextUseReadyByConnection.Remove(connKey);
                return;
            }
            if (readyAt > cycle.NextUseTime)
                cycle.NextUseTime = readyAt;
        }

        private void RememberNativeUseCooldown(string connKey, float readyAt)
        {
            if (string.IsNullOrEmpty(connKey) || readyAt <= 0f)
                return;
            if (!_nextUseReadyByConnection.TryGetValue(connKey, out float current) || readyAt > current)
                _nextUseReadyByConnection[connKey] = readyAt;
        }

        private void BeginCycle(string connKey, WeaponCycle cycle, Monster monster, ushort targetId)
        {
            BeginCycle(connKey, cycle, monster, targetId, Now);
        }

        private void BeginCycle(string connKey, WeaponCycle cycle, Monster monster, ushort targetId, float now)
        {
            cycle.IsActive = true;
            cycle.AwaitingContact = false;
            cycle.ServerApproachOnly = false;
            cycle.ContactHoldLogged = false;
            cycle.TargetId = targetId;
            cycle.Monster = monster;
            cycle.InstanceKey = ResolveCycleInstanceKey(cycle, monster, cycle.Connection, "BeginCycle");
            cycle.TickCounter = 0;
            ResetSwingRngState(cycle);
            cycle.LastTickTime = now;
            cycle.CycleStartTime = now;
            ConsumeNativeUseRng(connKey, cycle, ResolveRoomRng(cycle));
            ResolveNativeAttackFrames(cycle);
            float cooldownSeconds = GetNativeCooldownSeconds(cycle);
            cycle.NextUseTime = now + cooldownSeconds;
            RememberNativeUseCooldown(connKey, cycle.NextUseTime);
            if (IsNativeProjectileRangedCycle(cycle))
            {
                cycle.InitUsePassed = true;
                if (cycle.Connection != null)
                {
                    cycle.Connection.ActiveUseTargetStartedWeaponUse = true;
                    cycle.Connection.ActiveUseTargetInitUsePassed = true;
                }
                Debug.LogError($"[RANGED-USE-START] conn={connKey} target={monster?.EntityId ?? 0} component={cycle.UseTargetComponentId} session={cycle.UseTargetSessionId} initUsePassed={cycle.InitUsePassed} initUseRange={cycle.InitUseRange:F1} initUseDist={cycle.InitUseDistance:F1} tolerance={cycle.InitUseTolerance:F1} tick={NativeTickIndexFromTime(now)} rngAdvanced=False");
            }
            Debug.LogError($"[WEAPON-CYCLE-RATE] {connKey} anim={cycle.AttackAnimationId} frames total={cycle.AttackTotalFrames} hit={cycle.AttackHitFrame} sound={cycle.AttackSoundFrame} speed={GetNativeSpeedField(cycle)} speedPct={DamageComputer.ResolveNativeWeaponAttackSpeedPct(cycle.PlayerState):F2} animationTicks={GetNativeCycleTicks(cycle)} useCooldownTicks={GetNativeCooldownTicks(cycle)} cooldown={cooldownSeconds:F3}s readyAt={cycle.NextUseTime:F3} class={cycle.PlayerState?.WeaponClass ?? "unknown"} category={cycle.PlayerState?.WeaponCategory ?? "unknown"} useProjectile={cycle.PlayerState?.WeaponUsesProjectile ?? false} burst={cycle.PlayerState?.WeaponBurstCount ?? 1} native=Weapon::use+0x86/RangedWeapon::update+0x8e");
        }

        private void ResetSwingRngState(WeaponCycle cycle)
        {
            if (cycle == null) return;
            cycle.ProcFired = false;
            cycle.HitFired = false;
            cycle.AttackSoundFired = false;
            cycle.UseRngConsumed = false;
            cycle.UseRaw = 0;
            cycle.AttackSoundSelectRaw = 0;
            cycle.AttackSoundGateRaw = 0;
            cycle.AttackSoundRepeatRaw = 0;
            cycle.ImpactSoundRaw = 0;
        }

        private void ResolveNativeAttackFrames(WeaponCycle cycle)
        {
            if (cycle == null)
                return;

            cycle.AttackTotalFrames = DEFAULT_TOTAL_TICKS;
            cycle.AttackHitFrame = DEFAULT_HIT_POSITION;
            cycle.AttackSoundFrame = DEFAULT_SOUND_POSITION;
            cycle.AttackAnimationId = ResolveNativeAttackAnimationId(cycle);
            cycle.AttackSourceOffsetX = 0f;
            cycle.AttackSourceOffsetY = 0f;
            cycle.AttackSourceOffsetZ = 12f;
            cycle.AttackSourceOffsetResolved = false;

            var animations = ResolvePlayerAnimationList();
            if (animations?.AnonymousChildren == null)
                return;

            int animationId = cycle.AttackAnimationId;
            foreach (var animation in animations.AnonymousChildren)
            {
                if (animation.GetInt("ID", 0) != animationId)
                    continue;

                cycle.AttackTotalFrames = Math.Max(1, animation.GetInt("NumFrames", DEFAULT_TOTAL_TICKS));
                cycle.AttackHitFrame = Math.Max(1, animation.GetInt("TriggerTime", DEFAULT_HIT_POSITION));
                cycle.AttackSoundFrame = Math.Max(1, animation.GetInt("SoundTriggerTime", DEFAULT_SOUND_POSITION));
                if (TryParseSourceOffset(animation, out Vector3 sourceOffset))
                {
                    cycle.AttackSourceOffsetX = sourceOffset.x;
                    cycle.AttackSourceOffsetY = sourceOffset.y;
                    cycle.AttackSourceOffsetZ = sourceOffset.z;
                    cycle.AttackSourceOffsetResolved = true;
                }
                return;
            }
        }

        private static bool TryParseSourceOffset(GCNode animation, out Vector3 sourceOffset)
        {
            sourceOffset = Vector3.zero;
            string raw = animation?.GetString("SourceOffset", null);
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            int comment = raw.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
                raw = raw.Substring(0, comment);

            string[] parts = raw.Split(',');
            if (parts.Length < 3)
                return false;

            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x))
                return false;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                return false;
            if (!float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                return false;

            sourceOffset = new Vector3(x, y, z);
            return true;
        }

        private GCNode ResolvePlayerAnimationList()
        {
            var db = GCDatabase.Instance;
            if (db == null) return null;

            foreach (string path in PLAYER_ANIMATION_LIST_PATHS)
            {
                var animations = db.ResolveWithInheritance(path);
                if (animations?.AnonymousChildren != null && animations.AnonymousChildren.Count > 0)
                    return animations;
            }

            return null;
        }

        private int ResolveNativeAttackAnimationId(WeaponCycle cycle)
        {
            string weaponClass = cycle?.PlayerState?.WeaponClass ?? string.Empty;
            if (cycle?.PlayerState != null && DamageComputer.IsNativeRangedWeapon(cycle.PlayerState))
            {
                string weaponCategory = cycle.PlayerState.WeaponCategory ?? string.Empty;
                int selector = 10;
                if (ContainsIgnoreCase(weaponCategory, "CANNON") || ContainsIgnoreCase(weaponClass, "CANNON"))
                    return 1310;
                if (ContainsIgnoreCase(weaponCategory, "1H") ||
                    ContainsIgnoreCase(weaponClass, "1HRANGED") ||
                    ContainsIgnoreCase(weaponClass, "1HCROSSBOW") ||
                    ContainsIgnoreCase(weaponClass, "1HGUN"))
                    return 900 + selector;
                return 300 + selector;
            }

            int baseId;
            if (weaponClass.Equals("HTH", StringComparison.OrdinalIgnoreCase))
                baseId = 110;
            else if (weaponClass.Equals("2HMELEE", StringComparison.OrdinalIgnoreCase))
                baseId = 610;
            else if (weaponClass.Equals("POLEARM", StringComparison.OrdinalIgnoreCase))
                baseId = 810;
            else
                baseId = 510;

            int variant = cycle?.AttackAnimationIndex ?? 0;
            return baseId + variant;
        }

        private static bool ContainsIgnoreCase(string value, string token)
        {
            return !string.IsNullOrEmpty(value) &&
                !string.IsNullOrEmpty(token) &&
                value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsNativeRangedCycle(WeaponCycle cycle)
        {
            return cycle?.PlayerState != null && DamageComputer.IsNativeRangedWeapon(cycle.PlayerState);
        }

        private static bool IsNativeProjectileRangedCycle(WeaponCycle cycle)
        {
            return IsNativeRangedCycle(cycle) &&
                DamageComputer.IsNativeProjectileWeapon(cycle.PlayerState) &&
                cycle.PlayerState.WeaponProjectileSpeed > 0f &&
                cycle.PlayerState.WeaponProjectileSize > 0f;
        }

        private void ConsumeNativeUseRng(string connKey, WeaponCycle cycle, MersenneTwister rng)
        {
            if (cycle == null || rng == null || cycle.UseRngConsumed) return;
            if (cycle.PlayerState != null && DamageComputer.IsNativeRangedWeapon(cycle.PlayerState))
            {
                cycle.UseRaw = 0;
                cycle.UseRngConsumed = true;
                Debug.LogError($"[RNG-COMBAT] {connKey} RangedWeapon::use no room RNG class={cycle.PlayerState.WeaponClass} rngPos={rng.CallsSinceReseed}");
                return;
            }
            Debug.LogError($"[RNG-AUDIT] before-player-use seed=0x{rng.LastSeed:X8} rngPos={rng.CallsSinceReseed} {WanderSimulator.Instance.DescribeSchedule()}");
            cycle.UseRaw = NativeRngLedger.Generate(rng, "room", "player-melee:MeleeWeapon::use", connKey);
            cycle.UseRngConsumed = true;
            uint previousAnim = cycle.AttackAnimationIndex;
            cycle.AttackAnimationIndex = (byte)(((cycle.UseRaw & 1u) + previousAnim + 1u) % 3u);
            Debug.LogError($"[RNG-COMBAT] {connKey} MeleeWeapon::use useRaw=0x{cycle.UseRaw:X8} animBit={cycle.UseRaw & 1u} anim={previousAnim}->{cycle.AttackAnimationIndex} rngPos={rng.CallsSinceReseed}");
        }

        private void TickCycle(string connKey, WeaponCycle cycle, MersenneTwister rng, float now)
        {
            if (cycle == null) return;
            if (cycle.Monster == null || !cycle.Monster.IsAlive)
            {
                cycle.IsActive = false;
                cycle.AwaitingContact = false;
                return;
            }

            if (cycle.AwaitingContact)
            {
                if (cycle.Connection != null && !cycle.Connection.HasActiveUseTarget)
                {
                    Debug.LogError($"[WEAPON-CYCLE] {connKey} stop awaiting contact on {cycle.Monster.Name} target={cycle.TargetId} source=no-active-UseTarget native=UseTarget::UpdateMoving owner=Behavior::doActionLocal");
                    cycle.IsActive = false;
                    cycle.AwaitingContact = false;
                    cycle.ServerApproachOnly = false;
                    cycle.PendingRepeatUses = 0;
                    return;
                }
                if (cycle.Connection != null && cycle.Connection.HasActiveUseTarget && cycle.Connection.ActiveUseTargetId != cycle.TargetId)
                {
                    cycle.IsActive = false;
                    cycle.AwaitingContact = false;
                    return;
                }
                if (!HasNativePlayerMeleeContact(cycle, out float dist, out float range))
                {
                    cycle.Distance = dist;
                    cycle.ContactRange = range;
                    cycle.LastTickTime = now;
                    return;
                }
                cycle.Distance = dist;
                cycle.ContactRange = range;
                if (!IsNativeUseReady(cycle, now))
                {
                    cycle.LastTickTime = now;
                    return;
                }
                bool wasServerApproachOnly = cycle.ServerApproachOnly;
                if (wasServerApproachOnly)
                {
                    uint avatarId = cycle.Connection?.Avatar != null ? (uint)cycle.Connection.Avatar.Id : 0u;
                    if (avatarId != 0)
                    {
                        bool nativeWeaponUseStarted = IsNativeProjectileRangedCycle(cycle);
                        CombatManager.Instance.SetPlayerActiveClientAttack(avatarId, true, cycle.Monster.EntityId);
                        CombatManager.Instance.EngageMonsterFromClientAction(cycle.Monster, avatarId, nativeWeaponUseStarted);
                    }
                }
                BeginCycle(connKey, cycle, cycle.Monster, cycle.TargetId, now);
                string contactMode = wasServerApproachOnly ? "CONTACT cycle from approach" : "CONTACT cycle";
                Debug.LogError($"[WEAPON-CYCLE] {connKey} → {contactMode} on {cycle.Monster.Name} dist={dist:F1} range={range:F1}");
            }

            if (!cycle.IsActive) return;

            float tickInterval = GetCycleTickInterval(cycle);
            if (cycle.LastTickTime > 0f && now - cycle.LastTickTime + 0.0001f < tickInterval) return;
            float tickNow = now;
            if (cycle.LastTickTime > 0f)
            {
                cycle.LastTickTime += tickInterval;
                tickNow = cycle.LastTickTime;
            }
            else
            {
                cycle.LastTickTime = tickNow;
            }
            cycle.TickCounter++;

                if (cycle.TickCounter == GetNativeSoundEventTick(cycle) && !cycle.ProcFired)
                {
                    cycle.ProcFired = true;
                    cycle.AttackSoundFired = true;
                    cycle.AttackSoundGateRaw = NativeRandomStreams.GenerateGlobalSound("player-weapon:sound", connKey);
                    cycle.AttackSoundSelectRaw = 0;
                    cycle.AttackSoundRepeatRaw = (cycle.AttackSoundGateRaw & 3u) == 0 ? cycle.AttackSoundGateRaw : 0;
                    string roomRngPos = rng != null ? rng.CallsSinceReseed.ToString() : "n/a";
                    Debug.LogError($"[WEAPON-CYCLE] {connKey} SOUND tick={cycle.TickCounter} nativeGlobalSoundRng=True soundGate=0x{cycle.AttackSoundGateRaw:X8} repeat={(cycle.AttackSoundRepeatRaw != 0)} globalSoundRngPos={NativeRandomStreams.GlobalSoundCalls} roomRngPos={roomRngPos}");
                }

                if (cycle.TickCounter == GetNativeHitEventTick(cycle) && !cycle.HitFired)
                {
                    cycle.HitFired = true;
                    cycle.SwingCount++;

                    ConsumeNativeUseRng(connKey, cycle, rng);
                    uint useRaw = cycle.UseRaw;

                    if (IsNativeProjectileRangedCycle(cycle))
                    {
                        QueueNativeProjectileHit(connKey, cycle, tickNow);
                    }
                    else
                    {
                    NativeWeaponDamageInput damageInput = CreatePlayerNativeWeaponDamageInput(rng, cycle.PlayerState, cycle.Monster, "WeaponCycle");
                    DamageComputer.LogNativeDamageSlots(cycle.PlayerState, damageInput, cycle.Monster, "WeaponCycle");
                    NativeWeaponDamageResult damageResult = DamageComputer.ResolveNativeWeaponDamage(damageInput);
                    uint hitRaw = damageResult.HitRaw;
                    int hitRoll = damageResult.HitRoll;
                    uint blockRaw = damageResult.BlockRaw;
                    int blockRoll = damageResult.BlockRoll;
                    int attackRating = damageResult.AttackRating;
                    int defenseRating = damageResult.DefenseRating;
                    int attackerLevel = damageResult.AttackerLevel;
                    int hitDefenderLevel = damageResult.DefenderLevel;
                    int hitChanceF32 = damageResult.HitThreshold;
                    bool isHit = damageResult.IsHit;
                    bool isBlocked = damageResult.IsBlocked;

                    Debug.LogError($"[RNG-COMBAT] swing#{cycle.SwingCount} seed=0x{rng.LastSeed:X8} rngPos={rng.CallsSinceReseed} useRaw=0x{useRaw:X8} soundSelect=0x{cycle.AttackSoundSelectRaw:X8} soundGate=0x{cycle.AttackSoundGateRaw:X8} soundRepeat=0x{cycle.AttackSoundRepeatRaw:X8} hitRaw=0x{hitRaw:X8} blockRaw=0x{blockRaw:X8} hitRoll={hitRoll} threshold={hitChanceF32} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{hitDefenderLevel} isHit={isHit}");

                    int damage = 0;
                    uint damageRaw = 0;
                    uint damageWire = 0;
                    int weaponDmg = 0;
                    int volatility = 0;
                    int levelDamageBonus = 0;
                    int damageBonus = 0;
                    int damageMod = 0;
                    int minDmg = 0;
                    int maxDmg = 0;
                    int critThreshold = 0;
                    int critPercent = 0;
                    string weaponClass = cycle.PlayerState?.WeaponClass ?? "unknown";
                    string statSource = DamageComputer.ResolveNativeWeaponStatSource(cycle.PlayerState);
                    bool isCritical = false;
                    uint oldHPWire = CombatManager.Instance.PeekMonsterCurrentHPWire(cycle.Monster);
                    uint newHPWire = oldHPWire;
                    bool applied = false;
                    bool killed = false;
                    int actualDamage = 0;
                    int appliedDamage = 0;
                    uint damageProcRaw = 0;
                    uint effectRaw = 0;
                    uint impactSoundRaw = 0;
                    weaponDmg = damageInput.WeaponDamageF32;
                    volatility = damageInput.WeaponVolatilityF32;
                    levelDamageBonus = damageInput.DamageLevel;
                    damageBonus = damageInput.DamageBonus;
                    damageMod = damageInput.DamageMod;
                    minDmg = damageResult.MinDamageF32;
                    maxDmg = damageResult.MaxDamageF32;
                    critThreshold = damageInput.CritThreshold;
                    critPercent = damageInput.CritDamagePercent;
                    isCritical = damageResult.IsCritical;
                    if (isHit && !isBlocked)
                    {
                        damageRaw = damageResult.DamageRaw;
                        damage = damageResult.DamageF32;

                        Debug.LogError($"[RNG-COMBAT] dmgRaw=0x{damageRaw:X8} weaponClass={weaponClass} statSource={statSource} dmg={damage / 256} range=[{minDmg / 256},{maxDmg / 256}] rangeWire=[{minDmg},{maxDmg}] rolledWire={damage} bonus={damageBonus} damageMod={damageMod} crit={isCritical} critThreshold={critThreshold} critPct={critPercent} str={cycle.PlayerState.Strength} agi={cycle.PlayerState.Agility} level={cycle.PlayerState.Level} weaponLevel={cycle.PlayerState.WeaponLevel} levelBonus={levelDamageBonus} nativeBaseDamage={cycle.PlayerState.NativeWeaponBaseDamage} nativeBaseSource={cycle.PlayerState.NativeWeaponBaseDamageSource} weapon={DamageComputer.ToFloat(weaponDmg):F2} vol={DamageComputer.ToFloat(volatility):F2} roomRng={rng.CallsSinceReseed}");

                        damageWire = damageResult.DamageWire;
                        uint totalDamageWire = damageResult.TotalDamageWire != 0 ? damageResult.TotalDamageWire : damageWire;
                        int addCount = damageResult.DamageAdds != null ? damageResult.DamageAdds.Count : 0;
                        actualDamage = (int)((totalDamageWire + 255) / 256);
                        uint playerEntityId = cycle.Connection?.Avatar != null ? (uint)cycle.Connection.Avatar.Id : 0u;

                        float nativeHitTime = cycle.CycleStartTime > 0f
                            ? cycle.CycleStartTime + (GetCycleTickInterval(cycle) * GetNativeHitEventTick(cycle))
                            : tickNow;
                        applied = CombatManager.Instance.ApplyNativePlayerWeaponDamageToMonsterWire(cycle.Monster, damageResult, $"WeaponCycle-HIT swing={cycle.SwingCount}", out oldHPWire, out newHPWire, out killed, out effectRaw, rng, "player-weapon", nativeHitTime, 0);
                        if (applied)
                            CombatManager.Instance.NotifyMonsterDamagedByPlayer(cycle.Monster, playerEntityId, "damage");
                        cycle.ImpactSoundRaw = impactSoundRaw;
                        appliedDamage = (int)((oldHPWire > newHPWire ? oldHPWire - newHPWire : 0) + 255) / 256;

                        Debug.LogError($"[NATIVE-DAMAGE-CONTRACT] source=WeaponCycle swing={cycle.SwingCount} target={cycle.Monster.Name}#{cycle.Monster.EntityId} damageLevel={levelDamageBonus} weaponClassId={damageInput.WeaponClassId} damageTypeId={damageInput.DamageTypeId} bonus={damageBonus} mod={damageMod} rangeWire=[{minDmg},{maxDmg}] rawRoll=0x{damageRaw:X8} preQueryWire={damageWire} addCount={addCount} totalRawWire={totalDamageWire} hp={oldHPWire}->{newHPWire}/{cycle.Monster.MaxHPWire} result={(isCritical ? "CRIT" : "HIT")} rngAfter={rng.CallsSinceReseed}");
                        Debug.LogError($"[WEAPON-CYCLE] {connKey} {(isCritical ? "CRIT" : "HIT")}: {actualDamage} dmgWire={damageWire} totalWire={totalDamageWire} addCount={addCount} applied={applied} appliedDamage={appliedDamage} on {cycle.Monster.Name} HP={oldHPWire}->{newHPWire} proc=0x{damageProcRaw:X8} impact=0x{impactSoundRaw:X8} effect=0x{effectRaw:X8} rngPos={rng.CallsSinceReseed} [swing #{cycle.SwingCount}]");
                        if (killed && cycle.Monster != null)
                        {
                            _completedAttacks.Enqueue(new CompletedAttack
                            {
                                ConnKey = connKey,
                                Connection = cycle.Connection,
                                Monster = cycle.Monster,
                                DamageDealt = appliedDamage,
                                Killed = true
                            });
                            cycle.IsActive = false;
                        }
                    }
                    else
                    {
                        string resultType = !isHit ? "MISS" : "BLOCK";
                        CombatManager.Instance.LogMonsterClientVisibleSwingNoDamage(cycle.Monster, $"WeaponCycle-{resultType} swing={cycle.SwingCount}");
                        Debug.LogError($"[WEAPON-CYCLE] {connKey} {resultType}: hitRoll={hitRoll} blockRoll={blockRoll} on {cycle.Monster.Name} [swing #{cycle.SwingCount}]");
                    }
                    string combatResult = isHit ? (isBlocked ? "BLOCK" : (isCritical ? "CRIT" : "HIT")) : "MISS";
                    Debug.LogError($"[PLAYER-HIT-DETAIL] player={connKey} target={cycle.Monster.EntityId}/{cycle.Monster.BehaviorId} seed=0x{rng.LastSeed:X8} swing={cycle.SwingCount} tick={cycle.TickCounter} rngAfter={rng.CallsSinceReseed} useRaw=0x{useRaw:X8} soundGate=0x{cycle.AttackSoundGateRaw:X8} soundSelect=0x{cycle.AttackSoundSelectRaw:X8} soundRepeat=0x{cycle.AttackSoundRepeatRaw:X8} hitRaw=0x{hitRaw:X8} blockRaw=0x{blockRaw:X8} dmgRaw=0x{damageRaw:X8} effectRaw=0x{effectRaw:X8} impact=0x{impactSoundRaw:X8} hitRoll={hitRoll} threshold={hitChanceF32} critThreshold={critThreshold} critPct={critPercent} blockRoll={blockRoll} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{hitDefenderLevel} result={combatResult} weaponClass={weaponClass} weaponClassId={damageInput.WeaponClassId} damageTypeId={damageInput.DamageTypeId} statSource={statSource} weaponDamage={DamageComputer.ToFloat(weaponDmg):F4} vol={DamageComputer.ToFloat(volatility):F4} weaponLevel={cycle.PlayerState.WeaponLevel} levelBonus={levelDamageBonus} nativeBaseDamage={cycle.PlayerState.NativeWeaponBaseDamage} nativeBaseSource={cycle.PlayerState.NativeWeaponBaseDamageSource} bonus={damageBonus} mod={damageMod} rangeWire=[{minDmg},{maxDmg}] rolledWire={damageWire} totalWire={damageResult.TotalDamageWire} addCount={(damageResult.DamageAdds != null ? damageResult.DamageAdds.Count : 0)} applied={applied} hp={oldHPWire}->{newHPWire} exact=weapon-stat-source");
                    Debug.LogError($"[COMBAT-EVENT] actor=player player={connKey} actorId={(cycle.Connection?.Avatar != null ? cycle.Connection.Avatar.Id : 0)} target=monster targetId={cycle.Monster.EntityId} behaviorId={cycle.Monster.BehaviorId} result={combatResult} damageWire={damageWire} totalWire={damageResult.TotalDamageWire} addCount={(damageResult.DamageAdds != null ? damageResult.DamageAdds.Count : 0)} appliedDamage={appliedDamage} hp={oldHPWire}->{newHPWire} hitRaw=0x{hitRaw:X8} hitRoll={hitRoll} threshold={hitChanceF32} blockRaw=0x{blockRaw:X8} blockRoll={blockRoll} blockChance=0 damageRaw=0x{damageRaw:X8} effectRaw=0x{effectRaw:X8} resist=0 critThreshold={critThreshold} critPct={critPercent} rngAfter={rng.CallsSinceReseed}");
                    }
                }

                if (cycle.TickCounter >= GetNativeCycleTicks(cycle))
                {
                    bool heldActionAlive = cycle.Connection != null && cycle.Connection.HasActiveUseTarget && cycle.Connection.ActiveUseTargetId == cycle.TargetId && cycle.Monster != null && cycle.Monster.IsAlive && cycle.PendingRepeatUses > 0;
                    bool consumedActiveUseTarget = false;
                    cycle.TickCounter = 0;
                    ResetSwingRngState(cycle);
                    cycle.CycleStartTime = 0f;
                    cycle.LastTickTime = tickNow;
                    cycle.IsActive = false;
                    cycle.ServerApproachOnly = false;
                    cycle.ContactHoldLogged = false;
                    if (cycle.PendingRepeatUses > 0)
                        cycle.PendingRepeatUses--;
                    if (heldActionAlive)
                    {
                        if (HasNativePlayerMeleeContact(cycle, out float repeatDist, out float repeatRange))
                        {
                            cycle.Distance = repeatDist;
                            cycle.ContactRange = repeatRange;
                            if (IsNativeUseReady(cycle, tickNow))
                            {
                                BeginCycle(connKey, cycle, cycle.Monster, cycle.TargetId, tickNow);
                                Debug.LogError($"[WEAPON-CYCLE] {connKey} → REPEAT cycle on {cycle.Monster.Name} dist={repeatDist:F1} range={repeatRange:F1} pending={cycle.PendingRepeatUses}");
                            }
                            else
                            {
                                cycle.AwaitingContact = true;
                                Debug.LogError($"[WEAPON-CYCLE] {connKey} → REPEAT cooldown hold on {cycle.Monster.Name} nextIn={cycle.NextUseTime - tickNow:F2} pending={cycle.PendingRepeatUses}");
                            }
                        }
                        else
                        {
                            cycle.Distance = repeatDist;
                            cycle.ContactRange = repeatRange;
                            cycle.AwaitingContact = true;
                            Debug.LogError($"[WEAPON-CYCLE] {connKey} → REPEAT awaiting contact on {cycle.Monster.Name} dist={repeatDist:F1} range={repeatRange:F1} pending={cycle.PendingRepeatUses}");
                        }
                        return;
                    }
                    cycle.AwaitingContact = false;
                    bool hasPendingProjectile = IsNativeProjectileRangedCycle(cycle) && HasPendingProjectileForCycle(cycle);
                    if (hasPendingProjectile)
                        Debug.LogError($"[WEAPON-CYCLE] {connKey} → STOP animation on {cycle.Monster?.Name ?? "monster"} pendingProjectile=True actionClear=weapon-stop native=RangedWeapon::update");
                    if (!hasPendingProjectile && cycle.Connection != null && cycle.Connection.HasActiveUseTarget && cycle.Connection.ActiveUseTargetId == cycle.TargetId)
                    {
                        EnqueueControlRelease(cycle.Connection);
                        consumedActiveUseTarget = true;
                    }
                    Debug.LogError($"[WEAPON-CYCLE] {connKey} → STOP cycle on {cycle.Monster?.Name ?? "monster"} consumedUseTarget={consumedActiveUseTarget} pendingProjectile={hasPendingProjectile} release=queued awaiting next UseTarget");
                }
        }


        private void QueueNativeProjectileHit(string connKey, WeaponCycle cycle, float fireTime)
        {
            if (cycle == null || cycle.Connection == null || cycle.PlayerState == null || cycle.Monster == null)
                return;

            float playerX = cycle.Connection.HasLivePlayerPosition ? cycle.Connection.LivePlayerPosX : cycle.Connection.PlayerPosX;
            float playerY = cycle.Connection.HasLivePlayerPosition ? cycle.Connection.LivePlayerPosY : cycle.Connection.PlayerPosY;
            ResolveProjectileSourcePosition(cycle, playerX, playerY, out float startX, out float startY);
            float targetX = cycle.Monster.PosX;
            float targetY = cycle.Monster.PosY;
            bool targetFromClientVisible = CombatManager.Instance.TryGetMonsterClientVisiblePosition(cycle.Monster, fireTime, out float visibleTargetX, out float visibleTargetY);
            if (targetFromClientVisible)
            {
                targetX = visibleTargetX;
                targetY = visibleTargetY;
            }
            float pathDistance = Distance2D(startX, startY, targetX, targetY);
            if (pathDistance <= 0.001f)
                pathDistance = 0.001f;

            float speed = Mathf.Max(1f, cycle.PlayerState.WeaponProjectileSpeed);
            int fireTick = NativeTickIndexFromTime(fireTime);
            int flightTicks = NativeProjectileFlightTicks(pathDistance, speed);
            int impactDelayTicks = NativeProjectileImpactDelayTicks(pathDistance, speed);
            int dueTick = fireTick + impactDelayTicks;
            float dueTime = dueTick * NATIVE_UPDATE_TICK;
            float delay = Mathf.Max(0f, dueTime - fireTime);
            float projectileSize = Mathf.Max(0f, cycle.PlayerState.WeaponProjectileSize);
            float maxRange = cycle.PlayerState.WeaponRange > 0f
                ? cycle.PlayerState.WeaponRange
                : pathDistance;
            float maxDistance = Mathf.Max(0.001f, maxRange);
            int maxLifetimeTicks = NativeProjectileLifetimeTicks(maxRange, speed);
            float stepDistance = NativeProjectileStepDistance(speed);
            float initialDistance = NativeProjectileInitialDistance(speed, maxDistance);
            string instanceKey = ResolveCycleInstanceKey(cycle, cycle.Monster, cycle.Connection, "QueueNativeProjectileHit");
            if (string.IsNullOrWhiteSpace(instanceKey))
            {
                RuntimeEvidenceManager.LogFallbackHit(
                    "rng-instance",
                    "projectile-missing-instance-stamp",
                    $"target={cycle.Monster.EntityId} conn={connKey}",
                    64);
                Debug.LogError($"[RNG-INSTANCE] source=QueueNativeProjectileHit reason=missing-instance target={cycle.Monster.EntityId} conn={connKey}");
                return;
            }
            string zoneName = !string.IsNullOrWhiteSpace(cycle.Monster.ZoneName)
                ? cycle.Monster.ZoneName
                : cycle.Connection.CurrentZoneName;
            float baseZ = ResolveProjectileBaseZ(cycle.Connection, cycle.Monster);
            float startGroundFallback = baseZ;
            bool startGroundResolved = ResolveProjectileGroundZ(
                zoneName,
                instanceKey,
                startX,
                startY,
                startGroundFallback,
                out float startGroundZ,
                out string startGroundSource);
            float startZ = (startGroundResolved ? startGroundZ : startGroundFallback) + cycle.AttackSourceOffsetZ;
            float groundOffsetZ = startZ - (startGroundResolved ? startGroundZ : startGroundFallback);
            var pending = new PendingProjectileHit
            {
                Sequence = ++_nextProjectileSequence,
                ConnKey = connKey,
                Connection = cycle.Connection,
                PlayerState = cycle.PlayerState,
                Monster = cycle.Monster,
                InstanceKey = instanceKey,
                RequestedTargetId = cycle.Monster.EntityId,
                TargetId = cycle.Monster.EntityId,
                BehaviorId = cycle.Monster.BehaviorId,
                Swing = cycle.SwingCount,
                Tick = cycle.TickCounter,
                FireTime = fireTime,
                DueTime = dueTime,
                FireNativeTick = fireTick,
                FlightTicks = flightTicks,
                ImpactDelayTicks = impactDelayTicks,
                DueNativeTick = dueTick,
                HitDistance = pathDistance,
                PathDistance = pathDistance,
                UseRaw = cycle.UseRaw,
                AttackSoundSelectRaw = cycle.AttackSoundSelectRaw,
                AttackSoundGateRaw = cycle.AttackSoundGateRaw,
                AttackSoundRepeatRaw = cycle.AttackSoundRepeatRaw,
                DamageInput = CreatePlayerNativeWeaponDamageInput(CombatManager.Instance.GetRoomRngForInstance(instanceKey), cycle.PlayerState, cycle.Monster, "RangedProjectileSnapshot"),
                StartX = startX,
                StartY = startY,
                StartZ = startZ,
                GroundOffsetZ = groundOffsetZ,
                GroundOffsetResolved = startGroundResolved,
                GroundSource = startGroundSource,
                SourceOffsetX = cycle.AttackSourceOffsetX,
                SourceOffsetY = cycle.AttackSourceOffsetY,
                SourceOffsetZ = cycle.AttackSourceOffsetZ,
                SourceOffsetResolved = cycle.AttackSourceOffsetResolved,
                TargetX = targetX,
                TargetY = targetY,
                WorldBlocked = false,
                ProjectileSpeed = speed,
                ProjectileSize = projectileSize,
                StepDistance = stepDistance,
                InitialDistance = initialDistance,
                CurrentDistance = initialDistance,
                MaxDistance = maxDistance,
                MaxLifetimeTicks = maxLifetimeTicks,
                LastUpdateNativeTick = fireTick,
                UpdatesCompleted = initialDistance > 0f ? 1 : 0,
                InitUsePassed = cycle.InitUsePassed,
                InitUseRange = cycle.InitUseRange,
                InitUseDistance = cycle.InitUseDistance,
                InitUseTolerance = cycle.InitUseTolerance,
                ImpactResolved = false
            };
            _activeProjectiles.Add(pending);
            if (IsConnectionUseTargetForProjectile(pending))
            {
                cycle.Connection.ActiveUseTargetLastProjectileSeq = pending.Sequence;
                cycle.Connection.ActiveUseTargetVisibleHit = false;
            }
            Debug.LogError($"[RANGED-PROJECTILE] {connKey} create-subentity target={cycle.Monster.Name}#{cycle.Monster.EntityId} requested={cycle.Monster.Name}#{cycle.Monster.EntityId} seq={pending.Sequence} swing={cycle.SwingCount} hitFrameTick={cycle.TickCounter} fireTick={fireTick} firstUpdateTick={fireTick + 1} flightTicks={flightTicks} impactDelayTicks={impactDelayTicks} dueTick={dueTick} startPos=({startX:F1},{startY:F1},{startZ:F1}) groundZ={startGroundZ:F1} groundOffsetZ={groundOffsetZ:F1} groundResolved={startGroundResolved} groundSource='{startGroundSource ?? "fallback"}' sourceOffset=({pending.SourceOffsetX:F1},{pending.SourceOffsetY:F1},{pending.SourceOffsetZ:F1}) sourceOffsetResolved={pending.SourceOffsetResolved} heading={cycle.Connection.PlayerHeading:F1} hitDist={pathDistance:F2} pathDist={pathDistance:F2} targetPos=({targetX:F1},{targetY:F1}) targetSource={(targetFromClientVisible ? "client-visible" : "monster-runtime")} speed={speed:F1} step={stepDistance:F3} initPreStep={initialDistance:F3} size={projectileSize:F1} maxDist={maxDistance:F2} maxLife={maxLifetimeTicks} delay={delay:F3}s due={dueTime:F3} initUsePassed={pending.InitUsePassed} initUseRange={pending.InitUseRange:F1} initUseDist={pending.InitUseDistance:F1} useProjectile=True collision=subentity-swept native=RangedWeapon::update+Projectile::init");
        }

        private static void ResolveProjectileSourcePosition(WeaponCycle cycle, float playerX, float playerY, out float startX, out float startY)
        {
            startX = playerX;
            startY = playerY;
            if (cycle == null || (!cycle.AttackSourceOffsetResolved && Mathf.Abs(cycle.AttackSourceOffsetX) <= 0.001f && Mathf.Abs(cycle.AttackSourceOffsetY) <= 0.001f))
                return;

            float heading = cycle.Connection != null ? cycle.Connection.PlayerHeading : 0f;
            float headingRad = heading * Mathf.Deg2Rad;
            float sin = Mathf.Sin(headingRad);
            float cos = Mathf.Cos(headingRad);
            float rightX = cos;
            float rightY = -sin;
            float forwardX = sin;
            float forwardY = cos;
            startX = playerX + (rightX * cycle.AttackSourceOffsetX) + (forwardX * cycle.AttackSourceOffsetY);
            startY = playerY + (rightY * cycle.AttackSourceOffsetX) + (forwardY * cycle.AttackSourceOffsetY);
        }

        private bool TryResolveProjectileTarget(WeaponCycle cycle, float startX, float startY, float targetX, float targetY, float pathDistance, out Monster hitMonster, out float hitDistance, out bool worldBlocked)
        {
            hitMonster = null;
            hitDistance = pathDistance;
            worldBlocked = false;
            if (cycle == null || cycle.Monster == null || cycle.PlayerState == null) return false;

            string zoneName = !string.IsNullOrWhiteSpace(cycle.Monster.ZoneName)
                ? cycle.Monster.ZoneName
                : cycle.Connection?.CurrentZoneName;

            float dx = targetX - startX;
            float dy = targetY - startY;
            float lenSq = dx * dx + dy * dy;
            if (lenSq <= 0.001f) return false;

            float projectileSize = Mathf.Max(0f, cycle.PlayerState.WeaponProjectileSize);
            float projectileRadius = NativeProjectileRadiusFromAuthoredSize(projectileSize);
            float scanRange = Mathf.Max(pathDistance + projectileRadius + NATIVE_PROJECTILE_BROAD_SCAN_PADDING, cycle.ContactRange + projectileRadius + NATIVE_PROJECTILE_BROAD_SCAN_PADDING);
            Monster best = null;
            float bestAlong = float.MaxValue;
            string instanceKey = ResolveCycleInstanceKey(cycle, cycle.Monster, cycle.Connection, "TryResolveProjectileTarget");
            if (string.IsNullOrWhiteSpace(instanceKey))
            {
                RuntimeEvidenceManager.LogFallbackHit("rng-instance", "projectile-scan-missing-instance", $"target={cycle.Monster?.EntityId ?? 0}", 64);
                return false;
            }
            foreach (var candidate in CombatManager.Instance.GetMonstersInClientVisibleRange(startX, startY, scanRange, instanceKey, Now))
            {
                if (candidate == null || !candidate.IsAlive) continue;
                if (!string.IsNullOrWhiteSpace(zoneName) &&
                    !string.IsNullOrWhiteSpace(candidate.ZoneName) &&
                    !string.Equals(candidate.ZoneName, zoneName, StringComparison.OrdinalIgnoreCase))
                    continue;

                CombatManager.Instance.TryGetMonsterClientVisiblePosition(candidate, Now, out float candidateX, out float candidateY);
                float cx = candidateX - startX;
                float cy = candidateY - startY;
                float t = Mathf.Clamp01((cx * dx + cy * dy) / lenSq);
                float along = t * pathDistance;
                if (along > pathDistance + projectileRadius) continue;
                float closestX = startX + dx * t;
                float closestY = startY + dy * t;
                float miss = Distance2D(candidateX, candidateY, closestX, closestY);
                float radius = NativeProjectileCollisionRadius(candidate.CollisionRadius, projectileSize);
                if (miss > radius) continue;

                if (along < bestAlong)
                {
                    best = candidate;
                    bestAlong = along;
                    worldBlocked = false;
                }
            }

            if (best == null)
                return false;

            hitMonster = best;
            hitDistance = Mathf.Max(0f, bestAlong);
            return true;
        }

        private NativeDueDrainSummary ResolveDueProjectileHits(MersenneTwister rng, float now, string source)
        {
            return UpdateActiveProjectileSubEntities(rng, now, source, null);
        }

        public NativeDueDrainSummary DrainDueProjectileImpacts(MersenneTwister rng, float now, string source = null)
        {
            return UpdateActiveProjectileSubEntities(rng, now, source ?? "DrainDueProjectileImpacts", null);
        }

        public NativeDueDrainSummary DrainDueProjectileImpactsForMonster(uint monsterEntityId, MersenneTwister rng, float now, string source = null)
        {
            return UpdateActiveProjectileSubEntities(rng, now, source ?? "DrainDueProjectileImpactsForMonster", pending =>
            {
                uint pendingMonsterId = 0u;
                if (pending?.Monster != null)
                    pendingMonsterId = pending.Monster.EntityId;
                else if (pending != null)
                    pendingMonsterId = pending.TargetId;
                return monsterEntityId != 0 && pendingMonsterId == monsterEntityId;
            });
        }

        public NativeDueDrainSummary DrainDueProjectileImpactsForPlayer(uint playerEntityId, MersenneTwister rng, float now, string source = null)
        {
            return UpdateActiveProjectileSubEntities(rng, now, source ?? "DrainDueProjectileImpactsForPlayer", pending =>
            {
                uint pendingPlayerId = pending?.Connection?.Avatar != null
                    ? (uint)pending.Connection.Avatar.Id
                    : 0u;
                return playerEntityId != 0 && pendingPlayerId == playerEntityId;
            });
        }

        private NativeDueDrainSummary UpdateActiveProjectileSubEntities(MersenneTwister rng, float now, string source, Predicate<PendingProjectileHit> countPredicate)
        {
            int nowTick = NativeDrainTickFromTime(now);
            int matched = 0;
            var summary = new NativeDueDrainSummary
            {
                PendingBefore = _activeProjectiles.Count
            };

            if (_activeProjectiles.Count == 0)
            {
                summary.PendingAfter = 0;
                return summary;
            }

            if (rng == null)
            {
                bool anyRuntimeRng = false;
                for (int r = 0; r < _activeProjectiles.Count; r++)
                {
                    var pending = _activeProjectiles[r];
                    if (pending != null &&
                        !string.IsNullOrWhiteSpace(pending.InstanceKey) &&
                        CombatManager.Instance.GetRoomRngForInstance(pending.InstanceKey) != null)
                    {
                        anyRuntimeRng = true;
                        break;
                    }
                }
                if (!anyRuntimeRng)
                {
                    summary.PendingAfter = _activeProjectiles.Count;
                    summary.Stopped = true;
                    var next = _activeProjectiles[0];
                    summary.NextDueTick = Math.Max(0, next.LastUpdateNativeTick + 1);
                    summary.NextDueTime = summary.NextDueTick * NATIVE_UPDATE_TICK;
                    Debug.LogError($"[RANGED-PROJECTILE] active projectile update missing room RNG source={source ?? "unknown"} pending={summary.PendingBefore}");
                    return summary;
                }
            }

            _activeProjectiles.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
            for (int i = 0; i < _activeProjectiles.Count;)
            {
                PendingProjectileHit pending = _activeProjectiles[i];
                if (pending == null)
                {
                    _activeProjectiles.RemoveAt(i);
                    continue;
                }

                MersenneTwister pendingRng = !string.IsNullOrWhiteSpace(pending.InstanceKey)
                    ? CombatManager.Instance.GetRoomRngForInstance(pending.InstanceKey)
                    : null;
                if (pendingRng == null)
                {
                    RuntimeEvidenceManager.LogFallbackHit(
                        "rng-instance",
                        "projectile-missing-instance-rng",
                        $"seq={pending.Sequence} target={pending.TargetId} instance={pending.InstanceKey ?? "<none>"}",
                        64);
                    Debug.LogError($"[RNG-INSTANCE] source=RangedProjectile reason=missing-instance-rng seq={pending.Sequence} target={pending.TargetId} instance={pending.InstanceKey ?? "<none>"}");
                    i++;
                    continue;
                }

                if (nowTick <= pending.LastUpdateNativeTick)
                {
                    i++;
                    continue;
                }

                bool removed = false;
                for (int updateTick = pending.LastUpdateNativeTick + 1; updateTick <= nowTick; updateTick++)
                {
                    float updateTime = updateTick * NATIVE_UPDATE_TICK;
                    if (pending.UpdatesCompleted >= pending.MaxLifetimeTicks)
                    {
                        Debug.LogError($"[RANGED-PROJECTILE] {pending.ConnKey} subentity expired no-hit requested={pending.Monster?.Name ?? "monster"}#{pending.TargetId} seq={pending.Sequence} swing={pending.Swing} current={pending.CurrentDistance:F2}/{pending.MaxDistance:F2} updates={pending.UpdatesCompleted}/{pending.MaxLifetimeTicks} source={source ?? "unknown"} native=Projectile::update lifetime-zero-before-unit-check");
                        if (pending.Monster != null)
                            CombatManager.Instance.LogMonsterClientVisibleSwingNoDamage(pending.Monster, $"RangedProjectile-expired-no-hit swing={pending.Swing}");
                        ClearConsumedUseTargetForProjectile(pending, "expired-lifetime");
                        _activeProjectiles.RemoveAt(i);
                        removed = true;
                        break;
                    }

                    float beforeDistance = pending.CurrentDistance;
                    float afterDistance = Mathf.Min(pending.MaxDistance, beforeDistance + Mathf.Max(0.001f, pending.StepDistance));
                    pending.UpdatesCompleted++;
                    pending.LastUpdateNativeTick = updateTick;

                    if (TryResolveProjectileTargetAlongSegment(pending, beforeDistance, afterDistance, out Monster impactMonster, out float impactDistance, out bool impactWorldBlocked))
                    {
                        if (impactMonster == null)
                        {
                            pending.HitDistance = impactDistance;
                            pending.CurrentDistance = impactDistance;
                            pending.DueNativeTick = updateTick;
                            pending.DueTime = updateTime;
                            pending.WorldBlocked = true;
                            pending.ImpactResolved = true;
                            if (countPredicate == null || countPredicate(pending))
                                matched++;
                            if (pending.Monster != null)
                                CombatManager.Instance.LogMonsterClientVisibleSwingNoDamage(pending.Monster, $"RangedProjectile-world-collision swing={pending.Swing}");
                            Debug.LogError($"[RANGED-PROJECTILE] {pending.ConnKey} NO-DAMAGE world-collision requested={pending.Monster?.Name ?? "monster"}#{pending.TargetId} seq={pending.Sequence} swing={pending.Swing} hitDist={impactDistance:F2} tick={updateTick} source={source ?? "unknown"} native=ProjectileChecker::testFirstTime");
                            ClearConsumedUseTargetForProjectile(pending, "world-collision");
                            _activeProjectiles.RemoveAt(i);
                            summary.Drained++;
                            removed = true;
                            break;
                        }

                        pending.Monster = impactMonster;
                        pending.TargetId = impactMonster.EntityId;
                        pending.BehaviorId = impactMonster.BehaviorId;
                        pending.HitDistance = impactDistance;
                        pending.CurrentDistance = impactDistance;
                        pending.DueNativeTick = updateTick;
                        pending.DueTime = updateTime;
                        pending.WorldBlocked = impactWorldBlocked;
                        pending.ImpactResolved = true;
                        if (countPredicate == null || countPredicate(pending))
                            matched++;
                        ResolveProjectileDamage(pending, pendingRng, updateTime);
                        _activeProjectiles.RemoveAt(i);
                        summary.Drained++;
                        removed = true;
                        break;
                    }

                    pending.CurrentDistance = afterDistance;
                    if (pending.CurrentDistance + 0.0001f >= pending.MaxDistance || pending.UpdatesCompleted >= pending.MaxLifetimeTicks)
                    {
                        Debug.LogError($"[RANGED-PROJECTILE] {pending.ConnKey} subentity expired no-hit requested={pending.Monster?.Name ?? "monster"}#{pending.TargetId} seq={pending.Sequence} swing={pending.Swing} current={pending.CurrentDistance:F2}/{pending.MaxDistance:F2} updates={pending.UpdatesCompleted}/{pending.MaxLifetimeTicks} tick={updateTick} source={source ?? "unknown"} native=Projectile::update range-end");
                        if (pending.Monster != null)
                            CombatManager.Instance.LogMonsterClientVisibleSwingNoDamage(pending.Monster, $"RangedProjectile-expired-no-hit swing={pending.Swing}");
                        ClearConsumedUseTargetForProjectile(pending, "expired-range");
                        _activeProjectiles.RemoveAt(i);
                        removed = true;
                        break;
                    }
                }

                if (!removed)
                    i++;
            }
            summary.MatchingDrained = matched;
            summary.PendingAfter = _activeProjectiles.Count;
            if (_activeProjectiles.Count > 0)
            {
                var next = _activeProjectiles[0];
                summary.NextDueTick = Math.Max(0, next.LastUpdateNativeTick + 1);
                summary.NextDueTime = summary.NextDueTick * NATIVE_UPDATE_TICK;
            }

            if (summary.Drained > 0 || summary.Stopped)
            {
                Debug.LogError($"[RANGED-PROJECTILE-DUE] source={source ?? "unknown"} now={now:F3} nowTick={nowTick} drained={summary.Drained} matching={summary.MatchingDrained} pending={summary.PendingBefore}->{summary.PendingAfter} stopped={summary.Stopped} nextDueTick={summary.NextDueTick} nextDue={summary.NextDueTime:F3} runtime=subentity-swept");
            }

            return summary;
        }

        private bool TryResolveProjectileTargetAlongSegment(PendingProjectileHit pending, float segmentStart, float segmentEnd, out Monster hitMonster, out float hitDistance, out bool worldBlocked)
        {
            hitMonster = null;
            hitDistance = segmentEnd;
            worldBlocked = pending != null && pending.WorldBlocked;
            if (pending == null || pending.PlayerState == null)
                return false;

            float dx = pending.TargetX - pending.StartX;
            float dy = pending.TargetY - pending.StartY;
            float lenSq = dx * dx + dy * dy;
            if (lenSq <= 0.001f)
                return false;

            float pathDistance = Mathf.Sqrt(lenSq);
            float dirX = dx / pathDistance;
            float dirY = dy / pathDistance;
            float projectileSize = Mathf.Max(0f, pending.ProjectileSize);
            float projectileRadius = NativeProjectileRadiusFromAuthoredSize(projectileSize);
            float scanRange = Mathf.Max(segmentEnd + projectileRadius + NATIVE_PROJECTILE_BROAD_SCAN_PADDING, pending.HitDistance + projectileRadius + NATIVE_PROJECTILE_BROAD_SCAN_PADDING);
            string zoneName = !string.IsNullOrWhiteSpace(pending.Monster?.ZoneName)
                ? pending.Monster.ZoneName
                : pending.Connection?.CurrentZoneName;
            string instanceKey = pending.InstanceKey;
            if (string.IsNullOrWhiteSpace(instanceKey))
            {
                RuntimeEvidenceManager.LogFallbackHit("rng-instance", "projectile-segment-missing-instance", $"seq={pending.Sequence} target={pending.TargetId}", 64);
                return false;
            }
            WorldCollisionHit worldHit = null;
            float worldHitAlong = float.MaxValue;
            float startZ = ResolveProjectileZAt(pending, zoneName, instanceKey, segmentStart, dirX, dirY, ResolveProjectileStartZ(pending));
            Vector3 segmentWorldStart = new Vector3(
                pending.StartX + (dirX * segmentStart),
                pending.StartY + (dirY * segmentStart),
                startZ);
            float endZ = ResolveProjectileZAt(pending, zoneName, instanceKey, segmentEnd, dirX, dirY, startZ);
            Vector3 segmentWorldEnd = new Vector3(
                pending.StartX + (dirX * segmentEnd),
                pending.StartY + (dirY * segmentEnd),
                endZ);
            if (WorldCollisionManager.Instance.TrySegmentHit(zoneName, instanceKey, segmentWorldStart, segmentWorldEnd, projectileRadius, out worldHit))
                worldHitAlong = Mathf.Min(segmentEnd, segmentStart + Mathf.Max(0f, worldHit.Distance));

            Monster best = null;
            float bestAlong = float.MaxValue;
            float bestDistSq = float.MaxValue;
            bool bestBlocked = worldBlocked;
            float clientVisibleNow = pending.LastUpdateNativeTick * NATIVE_UPDATE_TICK;
            foreach (var candidate in CombatManager.Instance.GetMonstersInClientVisibleRange(pending.StartX, pending.StartY, scanRange, instanceKey, clientVisibleNow))
            {
                if (candidate == null || !candidate.IsAlive) continue;
                if (CombatManager.Instance.PeekMonsterCurrentHPWire(candidate) == 0) continue;
                if (!string.IsNullOrWhiteSpace(zoneName)
                    && !string.IsNullOrWhiteSpace(candidate.ZoneName)
                    && !string.Equals(candidate.ZoneName, zoneName, StringComparison.OrdinalIgnoreCase))
                    continue;

                CombatManager.Instance.TryGetMonsterClientVisiblePosition(candidate, clientVisibleNow, out float candidateX, out float candidateY);
                float cx = candidateX - pending.StartX;
                float cy = candidateY - pending.StartY;
                float projected = (cx * dirX) + (cy * dirY);
                float radius = NativeProjectileCollisionRadius(candidate.CollisionRadius, projectileSize);
                if (projected + radius < segmentStart || projected - radius > segmentEnd)
                    continue;

                float closestAlong = Mathf.Clamp(projected, segmentStart, segmentEnd);
                float closestX = pending.StartX + (dirX * closestAlong);
                float closestY = pending.StartY + (dirY * closestAlong);
                float missX = candidateX - closestX;
                float missY = candidateY - closestY;
                float distSq = (missX * missX) + (missY * missY);
                float radiusSq = radius * radius;
                if (distSq > radiusSq)
                    continue;

                float entryOffset = Mathf.Sqrt(Mathf.Max(0f, radiusSq - distSq));
                float impactAlong = Mathf.Clamp(projected - entryOffset, segmentStart, segmentEnd);
                if (impactAlong < bestAlong || (Mathf.Abs(impactAlong - bestAlong) <= 0.0001f && distSq < bestDistSq))
                {
                    best = candidate;
                    bestAlong = impactAlong;
                    bestDistSq = distSq;
                    bestBlocked = false;
                }
            }

            if (best == null)
            {
                if (worldHit != null)
                {
                    Debug.LogError($"[PROJECTILE-WORLD-COLLISION] conn={pending.ConnKey} seq={pending.Sequence} target={pending.TargetId}/{pending.BehaviorId} zone='{zoneName ?? ""}' instance='{instanceKey ?? ""}' tile='{worldHit.TileType}' grid=({worldHit.GridX},{worldHit.GridY}) object='{worldHit.ObjectPath}' collision='{worldHit.CollisionObject}' local=({worldHit.LocalX:F1},{worldHit.LocalY:F1},{worldHit.LocalZ:F1}) world=({worldHit.WorldX:F1},{worldHit.WorldY:F1},{worldHit.WorldZ:F1}) segment={segmentStart:F2}->{segmentEnd:F2} hitDist={Mathf.Max(0f, worldHitAlong):F2} result=diagnostic-only reason=BLOCKED_NATIVE_PLACEMENT_TRANSFORM native=ProjectileChecker::testFirstTime->WorldCollisionManager");
                    worldBlocked = false;
                }
                return false;
            }

            if (worldHit != null && worldHitAlong <= bestAlong + 0.0001f)
                Debug.LogError($"[PROJECTILE-WORLD-COLLISION] conn={pending.ConnKey} seq={pending.Sequence} target={pending.TargetId}/{pending.BehaviorId} zone='{zoneName ?? ""}' instance='{instanceKey ?? ""}' tile='{worldHit.TileType}' grid=({worldHit.GridX},{worldHit.GridY}) object='{worldHit.ObjectPath}' collision='{worldHit.CollisionObject}' local=({worldHit.LocalX:F1},{worldHit.LocalY:F1},{worldHit.LocalZ:F1}) world=({worldHit.WorldX:F1},{worldHit.WorldY:F1},{worldHit.WorldZ:F1}) segment={segmentStart:F2}->{segmentEnd:F2} hitDist={worldHitAlong:F2} targetHitDist={bestAlong:F2} result=ignored-unit-first native=ProjectileChecker::testFirstTime->UnitFinder2::findHittableUnits");

            CombatManager.Instance.SyncMonsterWanderClientVisiblePosition(best, "ProjectileChecker-subentity-hit");
            hitMonster = best;
            hitDistance = Mathf.Max(0f, bestAlong);
            worldBlocked = bestBlocked;
            float hitRadius = NativeProjectileCollisionRadius(best.CollisionRadius, pending.ProjectileSize);
            Debug.LogError($"[RANGED-PROJECTILE] {pending.ConnKey} subentity impact seq={pending.Sequence} target={best.Name}#{best.EntityId} swing={pending.Swing} segment={segmentStart:F2}->{segmentEnd:F2} hitDist={hitDistance:F2} radius={hitRadius:F2} projectileRadius={NativeProjectileRadiusFromAuthoredSize(pending.ProjectileSize):F2} worldBlocked={worldBlocked}");
            Debug.LogError($"[PROJECTILE-COLLISION] conn={pending.ConnKey} seq={pending.Sequence} createTick={pending.FireNativeTick} firstUpdateTick={pending.FireNativeTick + 1} hitTick={pending.LastUpdateNativeTick} segment={segmentStart:F2}->{segmentEnd:F2} radius={hitRadius:F2} projectileRadius={NativeProjectileRadiusFromAuthoredSize(pending.ProjectileSize):F2} target={best.EntityId}/{best.BehaviorId} initUsePassed={pending.InitUsePassed}");
            return true;
        }

        private void ResolveProjectileDamage(PendingProjectileHit pending, MersenneTwister rng, float now)
        {
            if (pending == null || pending.Monster == null || pending.PlayerState == null)
                return;

            bool sameTarget;
            if (!pending.ImpactResolved)
            {
                if (!TryResolveProjectileTargetAtImpact(pending, out Monster impactMonster, out float impactDistance, out bool impactWorldBlocked))
                {
                    Debug.LogError($"[RANGED-PROJECTILE] {pending.ConnKey} impact no-hit requested={pending.Monster?.Name ?? "monster"}#{pending.TargetId} swing={pending.Swing} path=({pending.StartX:F1},{pending.StartY:F1})->({pending.TargetX:F1},{pending.TargetY:F1}) hitDist={pending.HitDistance:F2} dueTick={pending.DueNativeTick}");
                    CombatManager.Instance.LogMonsterClientVisibleSwingNoDamage(pending.Monster, $"RangedProjectile-impact-no-hit swing={pending.Swing}");
                    ClearConsumedUseTargetForProjectile(pending, "impact-no-hit");
                    return;
                }

                sameTarget = pending.RequestedTargetId == 0 || impactMonster.EntityId == pending.RequestedTargetId;
                pending.Monster = impactMonster;
                pending.TargetId = impactMonster.EntityId;
                pending.BehaviorId = impactMonster.BehaviorId;
                pending.HitDistance = impactDistance;
                pending.WorldBlocked = impactWorldBlocked;
            }
            else
            {
                sameTarget = pending.RequestedTargetId == 0 || pending.Monster.EntityId == pending.RequestedTargetId;
            }

            bool activeUseTargetForProjectile = IsConnectionUseTargetForProjectile(pending);
            if (activeUseTargetForProjectile)
            {
                pending.Connection.ActiveUseTargetVisibleHit = true;
                pending.Connection.ActiveUseTargetLastProjectileSeq = pending.Sequence;
                pending.Connection.ActiveUseTargetLastImpactTick = pending.DueNativeTick;
            }
            else if (pending.Connection != null)
            {
                Debug.LogError($"[PROJECTILE-COLLISION] conn={pending.ConnKey} seq={pending.Sequence} activeUseTarget={pending.Connection.ActiveUseTargetId} requested={pending.RequestedTargetId} target={pending.TargetId} telemetry=inactive-use-target-damage-allowed native=Projectile::doImpact");
            }

            NativeWeaponDamageInput damageInput = sameTarget
                ? CloneNativeWeaponDamageInput(pending.DamageInput, rng, "RangedProjectile")
                : null;
            if (damageInput == null)
                damageInput = CreatePlayerNativeWeaponDamageInput(rng, pending.PlayerState, pending.Monster, "RangedProjectile");
            DamageComputer.LogNativeDamageSlots(pending.PlayerState, damageInput, pending.Monster, "RangedProjectile");
            NativeWeaponDamageResult damageResult = DamageComputer.ResolveNativeWeaponDamage(damageInput);
            uint hitRaw = damageResult.HitRaw;
            int hitRoll = damageResult.HitRoll;
            uint blockRaw = damageResult.BlockRaw;
            int blockRoll = damageResult.BlockRoll;
            int attackRating = damageResult.AttackRating;
            int defenseRating = damageResult.DefenseRating;
            int attackerLevel = damageResult.AttackerLevel;
            int hitDefenderLevel = damageResult.DefenderLevel;
            int hitChanceF32 = damageResult.HitThreshold;
            bool isHit = damageResult.IsHit;
            bool isBlocked = damageResult.IsBlocked;

            Debug.LogError($"[RNG-COMBAT] projectile swing#{pending.Swing} seed=0x{rng.LastSeed:X8} rngPos={rng.CallsSinceReseed} useRaw=0x{pending.UseRaw:X8} soundSelect=0x{pending.AttackSoundSelectRaw:X8} soundGate=0x{pending.AttackSoundGateRaw:X8} soundRepeat=0x{pending.AttackSoundRepeatRaw:X8} hitRaw=0x{hitRaw:X8} blockRaw=0x{blockRaw:X8} hitRoll={hitRoll} threshold={hitChanceF32} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{hitDefenderLevel} isHit={isHit}");

            int damage = 0;
            uint damageRaw = 0;
            uint damageWire = 0;
            int weaponDmg = 0;
            int volatility = 0;
            int levelDamageBonus = 0;
            int damageBonus = 0;
            int damageMod = 0;
            int minDmg = 0;
            int maxDmg = 0;
            int critThreshold = 0;
            int critPercent = 0;
            string weaponClass = pending.PlayerState.WeaponClass ?? "unknown";
            string statSource = DamageComputer.ResolveNativeWeaponStatSource(pending.PlayerState);
            bool isCritical = false;
            uint oldHPWire = CombatManager.Instance.PeekMonsterCurrentHPWire(pending.Monster);
            uint newHPWire = oldHPWire;
            bool applied = false;
            bool killed = false;
            int actualDamage = 0;
            int appliedDamage = 0;
            uint effectRaw = 0;
            uint impactSoundRaw = 0;
            weaponDmg = damageInput.WeaponDamageF32;
            volatility = damageInput.WeaponVolatilityF32;
            levelDamageBonus = damageInput.DamageLevel;
            damageBonus = damageInput.DamageBonus;
            damageMod = damageInput.DamageMod;
            minDmg = damageResult.MinDamageF32;
            maxDmg = damageResult.MaxDamageF32;
            critThreshold = damageInput.CritThreshold;
            critPercent = damageInput.CritDamagePercent;
            isCritical = damageResult.IsCritical;

            if (isHit && !isBlocked)
            {
                damageRaw = damageResult.DamageRaw;
                damage = damageResult.DamageF32;

                Debug.LogError($"[RNG-COMBAT] projectile dmgRaw=0x{damageRaw:X8} weaponClass={weaponClass} statSource={statSource} dmg={damage / 256} range=[{minDmg / 256},{maxDmg / 256}] rangeWire=[{minDmg},{maxDmg}] rolledWire={damage} bonus={damageBonus} damageMod={damageMod} crit={isCritical} critThreshold={critThreshold} critPct={critPercent} str={pending.PlayerState.Strength} agi={pending.PlayerState.Agility} level={pending.PlayerState.Level} weaponLevel={pending.PlayerState.WeaponLevel} levelBonus={levelDamageBonus} nativeBaseDamage={pending.PlayerState.NativeWeaponBaseDamage} nativeBaseSource={pending.PlayerState.NativeWeaponBaseDamageSource} weapon={DamageComputer.ToFloat(weaponDmg):F2} vol={DamageComputer.ToFloat(volatility):F2} roomRng={rng.CallsSinceReseed}");

                damageWire = damageResult.DamageWire;
                uint totalDamageWire = damageResult.TotalDamageWire != 0 ? damageResult.TotalDamageWire : damageWire;
                int addCount = damageResult.DamageAdds != null ? damageResult.DamageAdds.Count : 0;
                actualDamage = (int)((totalDamageWire + 255) / 256);
                uint playerEntityId = pending.Connection?.Avatar != null ? (uint)pending.Connection.Avatar.Id : 0u;

                uint dueNativeTick = pending.DueNativeTick > 0 ? (uint)pending.DueNativeTick : 0u;
                applied = CombatManager.Instance.ApplyNativePlayerWeaponDamageToMonsterWire(pending.Monster, damageResult, $"RangedProjectile-HIT swing={pending.Swing}", out oldHPWire, out newHPWire, out killed, out effectRaw, rng, "player-projectile", now, dueNativeTick);
                if (applied)
                    CombatManager.Instance.NotifyMonsterDamagedByPlayer(pending.Monster, playerEntityId, "projectile-damage");
                appliedDamage = (int)((oldHPWire > newHPWire ? oldHPWire - newHPWire : 0) + 255) / 256;

                Debug.LogError($"[NATIVE-DAMAGE-CONTRACT] source=RangedProjectile swing={pending.Swing} target={pending.Monster.Name}#{pending.Monster.EntityId} damageLevel={levelDamageBonus} weaponClassId={damageInput.WeaponClassId} damageTypeId={damageInput.DamageTypeId} bonus={damageBonus} mod={damageMod} rangeWire=[{minDmg},{maxDmg}] rawRoll=0x{damageRaw:X8} preQueryWire={damageWire} addCount={addCount} totalRawWire={totalDamageWire} hp={oldHPWire}->{newHPWire}/{pending.Monster.MaxHPWire} result={(isCritical ? "CRIT" : "HIT")} rngAfter={rng.CallsSinceReseed}");
                Debug.LogError($"[RANGED-PROJECTILE] {pending.ConnKey} {(isCritical ? "CRIT" : "HIT")}: {actualDamage} dmgWire={damageWire} totalWire={totalDamageWire} addCount={addCount} applied={applied} appliedDamage={appliedDamage} target={pending.Monster.Name}#{pending.Monster.EntityId} HP={oldHPWire}->{newHPWire} impact=0x{impactSoundRaw:X8} effect=0x{effectRaw:X8} rngPos={rng.CallsSinceReseed} swing={pending.Swing} hitDist={pending.HitDistance:F2} flightTicks={pending.FlightTicks} dueTick={pending.DueNativeTick} delay={(pending.DueTime - pending.FireTime):F3}s worldBlocked={pending.WorldBlocked}");
                if (killed)
                {
                    _completedAttacks.Enqueue(new CompletedAttack
                    {
                        ConnKey = pending.ConnKey,
                        Connection = pending.Connection,
                        Monster = pending.Monster,
                        DamageDealt = appliedDamage,
                        Killed = true
                    });
                }
            }
            else
            {
                string resultType = !isHit ? "MISS" : "BLOCK";
                CombatManager.Instance.LogMonsterClientVisibleSwingNoDamage(pending.Monster, $"RangedProjectile-{resultType} swing={pending.Swing}");
                Debug.LogError($"[RANGED-PROJECTILE] {pending.ConnKey} {resultType}: hitRoll={hitRoll} blockRoll={blockRoll} target={pending.Monster.Name}#{pending.Monster.EntityId} swing={pending.Swing}");
            }

            string combatResult = isHit ? (isBlocked ? "BLOCK" : (isCritical ? "CRIT" : "HIT")) : "MISS";
            Debug.LogError($"[PLAYER-HIT-DETAIL] player={pending.ConnKey} target={pending.Monster.EntityId}/{pending.BehaviorId} seed=0x{rng.LastSeed:X8} projectile=True swing={pending.Swing} tick={pending.Tick} fireTick={pending.FireNativeTick} flightTicks={pending.FlightTicks} impactDelayTicks={pending.ImpactDelayTicks} dueTick={pending.DueNativeTick} rngAfter={rng.CallsSinceReseed} useRaw=0x{pending.UseRaw:X8} soundGate=0x{pending.AttackSoundGateRaw:X8} soundSelect=0x{pending.AttackSoundSelectRaw:X8} soundRepeat=0x{pending.AttackSoundRepeatRaw:X8} hitRaw=0x{hitRaw:X8} blockRaw=0x{blockRaw:X8} dmgRaw=0x{damageRaw:X8} effectRaw=0x{effectRaw:X8} impact=0x{impactSoundRaw:X8} hitRoll={hitRoll} threshold={hitChanceF32} critThreshold={critThreshold} critPct={critPercent} blockRoll={blockRoll} ar={attackRating} dr={defenseRating} levels={attackerLevel}->{hitDefenderLevel} result={combatResult} weaponClass={weaponClass} weaponClassId={damageInput.WeaponClassId} damageTypeId={damageInput.DamageTypeId} statSource={statSource} weaponDamage={DamageComputer.ToFloat(weaponDmg):F4} vol={DamageComputer.ToFloat(volatility):F4} weaponLevel={pending.PlayerState.WeaponLevel} levelBonus={levelDamageBonus} nativeBaseDamage={pending.PlayerState.NativeWeaponBaseDamage} nativeBaseSource={pending.PlayerState.NativeWeaponBaseDamageSource} bonus={damageBonus} mod={damageMod} rangeWire=[{minDmg},{maxDmg}] rolledWire={damageWire} totalWire={damageResult.TotalDamageWire} addCount={(damageResult.DamageAdds != null ? damageResult.DamageAdds.Count : 0)} applied={applied} hp={oldHPWire}->{newHPWire} exact=projectile-weapon-stat-source");
            Debug.LogError($"[COMBAT-EVENT] actor=player player={pending.ConnKey} actorId={(pending.Connection?.Avatar != null ? pending.Connection.Avatar.Id : 0)} target=monster targetId={pending.Monster.EntityId} behaviorId={pending.BehaviorId} result={combatResult} projectile=True damageWire={damageWire} totalWire={damageResult.TotalDamageWire} addCount={(damageResult.DamageAdds != null ? damageResult.DamageAdds.Count : 0)} appliedDamage={appliedDamage} hp={oldHPWire}->{newHPWire} hitRaw=0x{hitRaw:X8} hitRoll={hitRoll} threshold={hitChanceF32} blockRaw=0x{blockRaw:X8} blockRoll={blockRoll} blockChance=0 damageRaw=0x{damageRaw:X8} effectRaw=0x{effectRaw:X8} resist=0 critThreshold={critThreshold} critPct={critPercent} rngAfter={rng.CallsSinceReseed}");
            ClearConsumedUseTargetForProjectile(pending, "resolved");
        }

        private bool TryResolveProjectileTargetAtImpact(PendingProjectileHit pending, out Monster hitMonster, out float hitDistance, out bool worldBlocked)
        {
            hitMonster = null;
            hitDistance = pending != null ? pending.HitDistance : 0f;
            worldBlocked = pending != null && pending.WorldBlocked;
            if (pending == null || pending.PlayerState == null || pending.Monster == null)
                return false;

            string zoneName = !string.IsNullOrWhiteSpace(pending.Monster.ZoneName)
                ? pending.Monster.ZoneName
                : pending.Connection?.CurrentZoneName;
            string instanceKey = pending.InstanceKey;
            if (string.IsNullOrWhiteSpace(instanceKey))
            {
                RuntimeEvidenceManager.LogFallbackHit("rng-instance", "projectile-impact-missing-instance", $"seq={pending.Sequence} target={pending.TargetId}", 64);
                return false;
            }
            float dx = pending.TargetX - pending.StartX;
            float dy = pending.TargetY - pending.StartY;
            float lenSq = dx * dx + dy * dy;
            if (lenSq <= 0.001f)
                return false;

            float pathDistance = Mathf.Sqrt(lenSq);
            float projectileSize = Mathf.Max(0f, pending.PlayerState.WeaponProjectileSize);
            float projectileRadius = NativeProjectileRadiusFromAuthoredSize(projectileSize);
            float scanRange = Mathf.Max(pathDistance + projectileRadius + NATIVE_PROJECTILE_BROAD_SCAN_PADDING, pending.HitDistance + projectileRadius + NATIVE_PROJECTILE_BROAD_SCAN_PADDING);
            Monster best = null;
            float bestAlong = float.MaxValue;
            bool bestBlocked = worldBlocked;
            WorldCollisionHit staticHit = null;
            float startZ = ResolveProjectileStartZ(pending);
            float endZ = ResolveProjectileZAt(pending, zoneName, instanceKey, pending.PathDistance, dx / pathDistance, dy / pathDistance, startZ);
            bool staticBlocked = WorldCollisionManager.Instance.TrySegmentHit(
                zoneName,
                instanceKey,
                new Vector3(pending.StartX, pending.StartY, startZ),
                new Vector3(pending.TargetX, pending.TargetY, endZ),
                projectileRadius,
                out staticHit);

            foreach (var candidate in CombatManager.Instance.GetMonstersInClientVisibleRange(pending.StartX, pending.StartY, scanRange, instanceKey, pending.DueTime))
            {
                if (candidate == null || !candidate.IsAlive) continue;
                if (!string.IsNullOrWhiteSpace(zoneName)
                    && !string.IsNullOrWhiteSpace(candidate.ZoneName)
                    && !string.Equals(candidate.ZoneName, zoneName, StringComparison.OrdinalIgnoreCase))
                    continue;

                CombatManager.Instance.TryGetMonsterClientVisiblePosition(candidate, pending.DueTime, out float candidateX, out float candidateY);
                float cx = candidateX - pending.StartX;
                float cy = candidateY - pending.StartY;
                float t = Mathf.Clamp01((cx * dx + cy * dy) / lenSq);
                float along = t * pathDistance;
                if (along > pathDistance + projectileRadius) continue;
                float closestX = pending.StartX + dx * t;
                float closestY = pending.StartY + dy * t;
                float miss = Distance2D(candidateX, candidateY, closestX, closestY);
                float radius = NativeProjectileCollisionRadius(candidate.CollisionRadius, projectileSize);
                if (miss > radius) continue;

                if (along < bestAlong)
                {
                    best = candidate;
                    bestAlong = along;
                    bestBlocked = false;
                }
            }

            if (best == null)
                return false;

            if (staticBlocked && staticHit != null && staticHit.Distance <= bestAlong + 0.0001f)
            {
                Debug.LogError($"[PROJECTILE-WORLD-COLLISION] conn={pending.ConnKey} seq={pending.Sequence} target={pending.TargetId}/{pending.BehaviorId} zone='{zoneName ?? ""}' instance='{instanceKey ?? ""}' tile='{staticHit.TileType}' grid=({staticHit.GridX},{staticHit.GridY}) object='{staticHit.ObjectPath}' collision='{staticHit.CollisionObject}' local=({staticHit.LocalX:F1},{staticHit.LocalY:F1},{staticHit.LocalZ:F1}) world=({staticHit.WorldX:F1},{staticHit.WorldY:F1},{staticHit.WorldZ:F1}) hitDist={staticHit.Distance:F2} targetHitDist={bestAlong:F2} result=ignored-unit-first-late native=ProjectileChecker::testFirstTime->UnitFinder2::findHittableUnits");
            }

            hitMonster = best;
            hitDistance = Mathf.Max(0f, bestAlong);
            worldBlocked = bestBlocked;
            return true;
        }

        private static float Distance2D(float x1, float y1, float x2, float y2)
        {
            float dx = x2 - x1;
            float dy = y2 - y1;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        private static float ResolveProjectileStartZ(PendingProjectileHit pending)
        {
            if (pending != null && Mathf.Abs(pending.StartZ) > 0.001f)
                return pending.StartZ;
            if (pending?.Connection != null)
            {
                if (pending.Connection.HasLivePlayerPosition && Mathf.Abs(pending.Connection.LivePlayerPosZ) > 0.001f)
                    return pending.Connection.LivePlayerPosZ + pending.SourceOffsetZ;
                if (Mathf.Abs(pending.Connection.PlayerPosZ) > 0.001f)
                    return pending.Connection.PlayerPosZ + pending.SourceOffsetZ;
            }
            if (pending?.Monster != null && Mathf.Abs(pending.Monster.PosZ) > 0.001f)
                return pending.Monster.PosZ + pending.SourceOffsetZ;
            return 10f + (pending?.SourceOffsetZ ?? 0f);
        }

        private static float ResolveProjectileBaseZ(RRConnection conn, Monster monster)
        {
            if (conn != null)
            {
                if (conn.HasLivePlayerPosition && Mathf.Abs(conn.LivePlayerPosZ) > 0.001f)
                    return conn.LivePlayerPosZ;
                if (Mathf.Abs(conn.PlayerPosZ) > 0.001f)
                    return conn.PlayerPosZ;
            }
            if (monster != null && Mathf.Abs(monster.PosZ) > 0.001f)
                return monster.PosZ;
            return 10f;
        }

        private static bool ResolveProjectileGroundZ(string zoneName, string instanceKey, float x, float y, float fallback, out float groundZ, out string source)
        {
            groundZ = fallback;
            source = null;
            string pathMapKey = ResolvePathMapKey(instanceKey, zoneName);
            PathMap pathMap = !string.IsNullOrWhiteSpace(pathMapKey) ? PathMapManager.Instance.GetPathMap(pathMapKey) : null;
            if (pathMap != null)
            {
                groundZ = pathMap.GetHeightAt(x, y, fallback);
                source = $"PathMap:{pathMapKey}";
                return true;
            }
            if (WorldCollisionManager.Instance.TryGetTerrainHeight(zoneName, instanceKey, x, y, fallback, out groundZ, out source))
            {
                source = $"WorldCollisionHeight:{source}";
                return true;
            }
            return false;
        }

        private static float ResolveProjectileZAt(PendingProjectileHit pending, string zoneName, string instanceKey, float along, float dirX, float dirY, float fallback)
        {
            if (pending == null)
                return fallback;

            float x = pending.StartX + (dirX * along);
            float y = pending.StartY + (dirY * along);
            float groundFallback = fallback - pending.GroundOffsetZ;
            if (ResolveProjectileGroundZ(zoneName, instanceKey, x, y, groundFallback, out float groundZ, out _))
                return groundZ + pending.GroundOffsetZ;
            if (pending.GroundOffsetResolved)
                return groundFallback + pending.GroundOffsetZ;
            return fallback;
        }

        private static bool IsConnectionUseTargetForProjectile(PendingProjectileHit pending)
        {
            if (pending == null || pending.Connection == null || !pending.Connection.HasActiveUseTarget)
                return false;

            uint activeTarget = pending.Connection.ActiveUseTargetId;
            uint requestedTarget = pending.RequestedTargetId != 0 ? pending.RequestedTargetId : pending.TargetId;
            return activeTarget == requestedTarget || activeTarget == pending.TargetId;
        }

        private bool ClearConsumedUseTarget(WeaponCycle cycle)
        {
            if (cycle == null || cycle.Connection == null) return false;
            if (!cycle.Connection.HasActiveUseTarget || cycle.Connection.ActiveUseTargetId != cycle.TargetId) return false;

            ClearConnectionUseTargetFields(cycle.Connection);

            uint avatarId = cycle.Connection.Avatar != null ? (uint)cycle.Connection.Avatar.Id : 0u;
            if (avatarId != 0)
                CombatManager.Instance.SetPlayerActiveClientAttack(avatarId, false);
            return true;
        }

        private bool HasPendingProjectileForCycle(WeaponCycle cycle)
        {
            if (cycle == null || _activeProjectiles.Count == 0)
                return false;

            for (int i = 0; i < _activeProjectiles.Count; i++)
            {
                var pending = _activeProjectiles[i];
                if (pending == null) continue;
                if (cycle.Connection != null && pending.Connection != null && !ReferenceEquals(cycle.Connection, pending.Connection))
                    continue;
                uint requestedTarget = pending.RequestedTargetId != 0 ? pending.RequestedTargetId : pending.TargetId;
                if (requestedTarget == cycle.TargetId || pending.TargetId == cycle.TargetId)
                    return true;
            }
            return false;
        }

        private bool ClearConsumedUseTargetForProjectile(PendingProjectileHit pending, string source)
        {
            if (pending == null || pending.Connection == null)
                return false;

            uint requestedTarget = pending.RequestedTargetId != 0 ? pending.RequestedTargetId : pending.TargetId;
            if (!pending.Connection.HasActiveUseTarget ||
                (pending.Connection.ActiveUseTargetId != requestedTarget && pending.Connection.ActiveUseTargetId != pending.TargetId))
                return false;

            uint activeTarget = pending.Connection.ActiveUseTargetId;
            bool pendingProjectile = pending.Monster != null && pending.Monster.IsAlive && HasOtherPendingProjectileForConnectionTarget(pending);
            bool pendingUseTarget = pending.Monster != null && pending.Monster.IsAlive && HasPendingUseTargetForConnectionTarget(pending);
            if (!pendingProjectile && !pendingUseTarget)
            {
                EnqueueControlRelease(pending.Connection);
                Debug.LogError($"[RANGED-PROJECTILE] {pending.ConnKey} release UseTarget seq={pending.Sequence} swing={pending.Swing} source={source ?? "unknown"} activeTarget={activeTarget} requested={requestedTarget} target={pending.TargetId} pendingSameTarget=False pendingUseTarget=False native=Projectile::doImpact actionClear=release-queued owner=RangedWeapon::update");
                return true;
            }
            Debug.LogError($"[RANGED-PROJECTILE] {pending.ConnKey} preserve UseTarget seq={pending.Sequence} swing={pending.Swing} source={source ?? "unknown"} activeTarget={activeTarget} requested={requestedTarget} target={pending.TargetId} pendingSameTarget={pendingProjectile} pendingUseTarget={pendingUseTarget} native=Projectile::doImpact actionClear=False owner=RangedWeapon::update");
            return false;
        }

        private bool HasPendingUseTargetForConnectionTarget(PendingProjectileHit pending)
        {
            if (pending == null || pending.Connection == null || _activeCycles.Count == 0)
                return false;

            uint requestedTarget = pending.RequestedTargetId != 0 ? pending.RequestedTargetId : pending.TargetId;
            foreach (WeaponCycle cycle in _activeCycles.Values)
            {
                if (cycle == null || cycle.Connection == null || !ReferenceEquals(cycle.Connection, pending.Connection))
                    continue;
                if (cycle.PendingRepeatUses <= 0)
                    continue;
                if (cycle.TargetId == requestedTarget || cycle.TargetId == pending.TargetId)
                    return true;
            }
            return false;
        }

        private bool HasOtherPendingProjectileForConnectionTarget(PendingProjectileHit pending)
        {
            if (pending == null || pending.Connection == null || _activeProjectiles.Count == 0)
                return false;

            uint requestedTarget = pending.RequestedTargetId != 0 ? pending.RequestedTargetId : pending.TargetId;
            for (int i = 0; i < _activeProjectiles.Count; i++)
            {
                PendingProjectileHit other = _activeProjectiles[i];
                if (other == null || ReferenceEquals(other, pending) || other.Sequence == pending.Sequence)
                    continue;
                if (other.Connection == null || !ReferenceEquals(other.Connection, pending.Connection))
                    continue;

                uint otherRequested = other.RequestedTargetId != 0 ? other.RequestedTargetId : other.TargetId;
                if (otherRequested == requestedTarget || other.TargetId == pending.TargetId)
                    return true;
            }
            return false;
        }

        private static void ClearConnectionUseTargetFields(RRConnection conn)
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

        private bool HasNativePlayerMeleeContact(WeaponCycle cycle, out float distance, out float range)
        {
            distance = float.MaxValue;
            range = 0f;
            if (cycle == null || cycle.Connection == null || cycle.Monster == null) return false;
            float monsterX = cycle.Monster.PosX;
            float monsterY = cycle.Monster.PosY;
            CombatManager.Instance.TryGetMonsterClientVisiblePosition(cycle.Monster, Now, out monsterX, out monsterY);
            float dx = monsterX - cycle.Connection.PlayerPosX;
            float dy = monsterY - cycle.Connection.PlayerPosY;
            distance = Mathf.Sqrt(dx * dx + dy * dy);
            if (IsNativeProjectileRangedCycle(cycle))
            {
                if (!cycle.Connection.HasActiveUseTarget || cycle.Connection.ActiveUseTargetId != cycle.TargetId)
                    return false;

                bool wasPassed = cycle.InitUsePassed;
                float tolerance;
                string source;
                float initUseRange = CombatManager.Instance.ResolveNativeUseTargetInitUseRange(cycle.PlayerState, cycle.Monster, out tolerance, out source);
                bool initUsePassed = CombatManager.Instance.EvaluateNativeUseTargetInitUse(
                    cycle.Connection.PlayerPosX, cycle.Connection.PlayerPosY,
                    monsterX, monsterY,
                    initUseRange, tolerance, out distance,
                    out long distanceSqFixed8, out long thresholdSqFixed8);

                if (cycle.Connection.HasActiveUseTarget && cycle.Connection.ActiveUseTargetId == cycle.TargetId)
                {
                    cycle.Connection.ActiveUseTargetInitUsePassed = initUsePassed;
                    cycle.Connection.ActiveUseTargetInitUseRange = initUseRange;
                    cycle.Connection.ActiveUseTargetInitUseDistance = distance;
                    cycle.Connection.ActiveUseTargetClientSyncTolerance = tolerance;
                }
                cycle.InitUsePassed = initUsePassed;
                cycle.InitUseRange = initUseRange;
                cycle.InitUseDistance = distance;
                cycle.InitUseTolerance = tolerance;
                float weaponRange = CombatManager.Instance.ResolvePlayerRangedWeaponUseRange(cycle.PlayerState);
                if (initUsePassed && !wasPassed)
                    Debug.LogError($"[USETARGET-INIT] target={cycle.Monster.EntityId} behavior={cycle.Monster.BehaviorId} component={cycle.UseTargetComponentId} session={cycle.UseTargetSessionId} dist={distance:F2} distSqFixed8={distanceSqFixed8} initUseRange={initUseRange:F1} tolerance={tolerance:F1} thresholdSqFixed8={thresholdSqFixed8} source={source} weaponRange={weaponRange:F1} result={(initUsePassed ? "use" : "moving")} rngBefore=-1 rngAfter=-1");
                range = initUseRange;
                return initUsePassed;
            }

            range = CombatManager.Instance.ResolvePlayerMeleeNativeContactRange(cycle.PlayerState, cycle.Monster);
            return range > 0f && distance <= range + NATIVE_CONTACT_RANGE_EPSILON;
        }

        public CompletedAttack DequeueKill()
        {
            return _completedAttacks.Count > 0 ? _completedAttacks.Dequeue() : null;
        }

        public bool HasPendingKills => _completedAttacks.Count > 0;

        private readonly Queue<RRConnection> _pendingControlReleases = new Queue<RRConnection>();
        public bool HasPendingControlReleases => _pendingControlReleases.Count > 0;
        public RRConnection DequeueControlRelease() => _pendingControlReleases.Count > 0 ? _pendingControlReleases.Dequeue() : null;

        private void EnqueueControlRelease(RRConnection conn)
        {
            if (conn == null || !conn.HasActiveUseTarget) return;
            if (!_pendingControlReleases.Contains(conn))
                _pendingControlReleases.Enqueue(conn);
        }

        public void ClearConnection(string connKey)
        {
            if (!string.IsNullOrEmpty(connKey) && _activeCycles.TryGetValue(connKey, out var cycle))
                RememberNativeUseCooldown(connKey, cycle.NextUseTime);
            _activeCycles.Remove(connKey);
            if (!string.IsNullOrEmpty(connKey) && _activeProjectiles.Count > 0)
            {
                _activeProjectiles.RemoveAll(pending =>
                    pending != null && string.Equals(pending.ConnKey, connKey, StringComparison.Ordinal));
            }
        }

        public void CancelConnectionUseTargetIntent(string connKey, string source = null)
        {
            if (string.IsNullOrEmpty(connKey))
                return;
            float preservedReadyAt = 0f;
            bool removedCycle = false;
            if (_activeCycles.TryGetValue(connKey, out var cycle))
            {
                preservedReadyAt = cycle.NextUseTime;
                RememberNativeUseCooldown(connKey, preservedReadyAt);
                removedCycle = _activeCycles.Remove(connKey);
            }
            int preservedProjectiles = 0;
            for (int i = 0; i < _activeProjectiles.Count; i++)
            {
                if (_activeProjectiles[i] != null && string.Equals(_activeProjectiles[i].ConnKey, connKey, StringComparison.Ordinal))
                    preservedProjectiles++;
            }
            float now = Now;
            float preservedNextIn = preservedReadyAt > now ? preservedReadyAt - now : 0f;
            Debug.LogError($"[WEAPON-CYCLE] {connKey} cancel-use-target-intent source={source ?? "unknown"} removedCycle={removedCycle} preservedProjectiles={preservedProjectiles} preservedReadyAt={preservedReadyAt:F3} preservedNextIn={preservedNextIn:F3} native=Weapon::use+0x86");
        }

        public void Clear()
        {
            _activeCycles.Clear();
            _nextUseReadyByConnection.Clear();
            _completedAttacks.Clear();
            _activeProjectiles.Clear();
            _nextProjectileSequence = 0;
        }
    }

    public class WeaponCycle
    {
        public bool IsActive;
        public ushort TargetId;
        public Monster Monster;
        public PlayerState PlayerState;
        public RRConnection Connection;
        public string InstanceKey;
        public int TickCounter;
        public float CycleStartTime;
        public bool ProcFired;
        public bool HitFired;
        public bool AttackSoundFired;
        public bool UseRngConsumed;
        public uint UseRaw;
        public uint AttackSoundSelectRaw;
        public uint AttackSoundGateRaw;
        public uint AttackSoundRepeatRaw;
        public uint ImpactSoundRaw;
        public byte AttackAnimationIndex;
        public int SwingCount;
        public int PendingRepeatUses;
        public bool AwaitingContact;
        public bool ServerApproachOnly;
        public bool ContactHoldLogged;
        public float Distance;
        public float ContactRange;
        public bool InitUsePassed;
        public float InitUseRange;
        public float InitUseDistance;
        public float InitUseTolerance;
        public ushort UseTargetComponentId;
        public byte UseTargetSessionId;
        public float LastTickTime;
        public float NextUseTime;
        public int AttackTotalFrames;
        public int AttackHitFrame;
        public int AttackSoundFrame;
        public int AttackAnimationId;
        public bool AttackSourceOffsetResolved;
        public float AttackSourceOffsetX;
        public float AttackSourceOffsetY;
        public float AttackSourceOffsetZ;
    }

    public class CompletedAttack
    {
        public string ConnKey;
        public RRConnection Connection;
        public Monster Monster;
        public bool Killed;
        public int DamageDealt;
    }

    public class WeaponCycleFlushResult
    {
        public uint TargetEntityId;
        public uint BeforeHPWire;
        public uint AfterHPWire;
        public int PendingBefore;
        public int PendingAfter;
        public int ProjectilesResolved;
        public int CycleTicks;
        public bool HadTargetCycle;
    }

    public class PendingProjectileHit
    {
        public long Sequence;
        public string ConnKey;
        public RRConnection Connection;
        public PlayerState PlayerState;
        public Monster Monster;
        public string InstanceKey;
        public uint RequestedTargetId;
        public uint TargetId;
        public uint BehaviorId;
        public int Swing;
        public int Tick;
        public float FireTime;
        public float DueTime;
        public int FireNativeTick;
        public int FlightTicks;
        public int ImpactDelayTicks;
        public int DueNativeTick;
        public float HitDistance;
        public float PathDistance;
        public bool WorldBlocked;
        public uint UseRaw;
        public uint AttackSoundSelectRaw;
        public uint AttackSoundGateRaw;
        public uint AttackSoundRepeatRaw;
        public NativeWeaponDamageInput DamageInput;
        public float StartX;
        public float StartY;
        public float StartZ;
        public float GroundOffsetZ;
        public bool GroundOffsetResolved;
        public string GroundSource;
        public bool SourceOffsetResolved;
        public float SourceOffsetX;
        public float SourceOffsetY;
        public float SourceOffsetZ;
        public float TargetX;
        public float TargetY;
        public float ProjectileSpeed;
        public float ProjectileSize;
        public float StepDistance;
        public float InitialDistance;
        public float CurrentDistance;
        public float MaxDistance;
        public int MaxLifetimeTicks;
        public int LastUpdateNativeTick;
        public int UpdatesCompleted;
        public bool InitUsePassed;
        public float InitUseRange;
        public float InitUseDistance;
        public float InitUseTolerance;
        public bool ImpactResolved;
    }

    public enum NativeDueEventDisposition
    {
        Consumed,
        KeepAndStop
    }

    public class NativeDueDrainSummary
    {
        public int PendingBefore;
        public int PendingAfter;
        public int Drained;
        public int MatchingDrained;
        public bool Stopped;
        public int NextDueTick = -1;
        public float NextDueTime = -1f;
    }

    public class NativeDueEvent<T>
    {
        public int DueTick;
        public float DueTime;
        public long Sequence;
        public T Payload;
    }

    public class NativeDueEventScheduler<T>
    {
        private readonly List<NativeDueEvent<T>> _events = new List<NativeDueEvent<T>>();
        private long _nextSequence;

        public int Count => _events.Count;

        public long Schedule(int dueTick, float dueTime, T payload)
        {
            var scheduled = new NativeDueEvent<T>
            {
                DueTick = Math.Max(0, dueTick),
                DueTime = dueTime,
                Sequence = ++_nextSequence,
                Payload = payload
            };

            int insertAt = _events.Count;
            for (int i = 0; i < _events.Count; i++)
            {
                if (Compare(scheduled, _events[i]) < 0)
                {
                    insertAt = i;
                    break;
                }
            }
            _events.Insert(insertAt, scheduled);
            return scheduled.Sequence;
        }

        public NativeDueDrainSummary DrainDue(int upToTick, float upToTime, Func<NativeDueEvent<T>, NativeDueEventDisposition> drain)
        {
            var summary = new NativeDueDrainSummary
            {
                PendingBefore = _events.Count
            };

            while (_events.Count > 0)
            {
                var scheduled = _events[0];
                if (!IsDue(scheduled, upToTick, upToTime))
                    break;

                _events.RemoveAt(0);
                NativeDueEventDisposition disposition = drain != null
                    ? drain(scheduled)
                    : NativeDueEventDisposition.Consumed;

                if (disposition == NativeDueEventDisposition.KeepAndStop)
                {
                    _events.Insert(0, scheduled);
                    summary.Stopped = true;
                    break;
                }

                summary.Drained++;
            }

            summary.PendingAfter = _events.Count;
            if (_events.Count > 0)
            {
                summary.NextDueTick = _events[0].DueTick;
                summary.NextDueTime = _events[0].DueTime;
            }
            return summary;
        }

        public int RemoveWhere(Predicate<T> predicate)
        {
            if (predicate == null || _events.Count == 0)
                return 0;

            int removed = 0;
            for (int i = _events.Count - 1; i >= 0; i--)
            {
                if (!predicate(_events[i].Payload))
                    continue;

                _events.RemoveAt(i);
                removed++;
            }
            return removed;
        }

        public void Clear()
        {
            _events.Clear();
        }

        private static bool IsDue(NativeDueEvent<T> scheduled, int upToTick, float upToTime)
        {
            if (scheduled.DueTick < upToTick)
                return true;
            if (scheduled.DueTick > upToTick)
                return false;
            return scheduled.DueTime <= upToTime + 0.0001f;
        }

        private static int Compare(NativeDueEvent<T> left, NativeDueEvent<T> right)
        {
            int tick = left.DueTick.CompareTo(right.DueTick);
            if (tick != 0) return tick;
            int time = left.DueTime.CompareTo(right.DueTime);
            if (time != 0) return time;
            return left.Sequence.CompareTo(right.Sequence);
        }
    }
}
