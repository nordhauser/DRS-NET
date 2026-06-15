using System;
using System.Collections.Generic;
using System.Linq;
using DungeonRunners.Networking;
using DungeonRunners.Networking.Sync;
using DungeonRunners.Utilities;

namespace DungeonRunners.Combat
{
    public static class NativeDamageReplaySelfTest
    {
        public const uint LatestPup50024Seed = 0x8D801C2B;
        public const int LatestPup50024PreHitConsumes = 156;
        public const uint LatestPup50024ServerDamageWire = 3180;
        public const uint LatestPup50024NativeObservedDamageWire = 4399;
        public const uint LatestPup50024StartHPWire = 29184;
        public const uint LatestRatling50000ClockSeed = 0xE8BD592F;
        public const uint LatestRatling50000StartHPWire = 29184;
        public const uint LatestRatling50000FirstSuffixHPWire = 24002;
        public const uint LatestRatling50000ClientCrashHPWire = 24132;
        public const uint LatestRatling50000DamageRaw = 0x30FF6AD4;
        public const int NativeCombatHz = 30;
        public const int LatestRatling50000SuffixFlushAttempts = 12;
        public const uint SchedulerSelfTestTargetEntityId = 50000;
        public const uint SchedulerSelfTestOtherEntityId = 50024;
        public const uint SchedulerSelfTestStartHPWire = 29184;
        public const uint LatestPup50000StaleRemoteHPWire = 29184;
        public const uint LatestPup50000ClientLocalHPWire = 21882;
        public const uint LatestPup50000SameTickStartHPWire = 29184;
        public const uint LatestPup50000SameTickRemoteHPWire = 24188;
        public const uint LatestPup50000SameTickDamageWire = 4996;
        public const int LatestPup50000SameTickImpactTick = 2718;
        public const uint LatestPup50000Cb024Seed = 0x1972493E;
        public const int LatestPup50000Cb024PreHitConsumes = 26;
        public const uint LatestPup50000Cb024ClientLocalHPWire = 21882;
        public const uint LatestPup50000Cb024BadRemoteHPWire = 29184;
        public const uint LatestPup50000Cb024FirstStartHPWire = 29184;
        public const uint LatestPup50000Cb024FirstDamageWire = 4996;
        public const uint LatestPup50000Cb024FirstPostHPWire = 24188;
        public const int LatestPup50000Cb024FirstImpactTick = 2718;
        public const uint LatestPup50000Cb024LateStartHPWire = 24188;
        public const uint LatestPup50000Cb024LateDamageWire = 5714;
        public const uint LatestPup50000Cb024LatePostHPWire = 18474;
        public const int LatestPup50000Cb024LateImpactTick = 2735;
        public const uint LatestPup50018HeldSeed = 0x5B5C8C1D;
        public const uint LatestPup50018EntityId = 50018;
        public const uint LatestPup50019BehaviorId = 50019;
        public const uint LatestPup50018StartHPWire = 29184;
        public const uint LatestPup50018BadRemoteHPWire = 23616;
        public const uint LatestPup50018ClientHPWire = 14752;
        public const uint LatestPup50018FirstDamageWire = 5568;
        public const uint LatestPup50018SecondDamageWire = 4385;
        public const uint LatestPup50018ThirdDamageWire = 4479;
        public const int LatestPup50018FirstProjectileDueTick = 600;
        public const int LatestPup50018SecondProjectileDueTick = 613;
        public const int LatestPup50018StaleSuffixCutoffTick = 612;
        public const uint LatestPoisonHeldRatlingStartHPWire = 29184;
        public const uint LatestPoisonHeldRatlingRemoteHPWire = 29184;
        public const uint LatestPoisonHeldRatlingClientHPWire = 21672;
        public const uint LatestPoisonHeldRatlingFirstWeaponDamageWire = 3868;
        public const uint LatestPoisonHeldRatlingSecondWeaponDamageWire = 3644;
        public const uint LatestWhiskerRatling50000StartHPWire = 29184;
        public const uint LatestWhiskerRatling50000ClientFirstHPWire = 24294;
        public const uint LatestWhiskerRatling50000NativeFirstDamageWire = 4890;
        public const uint LatestWhiskerRatling50000BadServerDamageWire = 5797;
        public const uint LatestWhiskerRatling50000BadServerRemoteHPWire = 23387;
        public const ushort LatestPup50006AggroBehaviorComponentId = 50007;
        public const ushort LatestPup50006AggroTargetEntityId = 510;
        public const uint LatestPup50006AggroHPWire = 29184;
        public const byte LatestPup50006MalformedAggroRemoteFlags = 0x09;
        public const uint LatestRatling50006RuntimeSeed = 0x48BE5F24;
        public const uint LatestRatling50006EntityId = 50006;
        public const uint LatestRatling50007BehaviorId = 50007;
        public const uint LatestRatling50006ClientHPWire = 22197;
        public const uint LatestRatling50006BadServerSuffixHPWire = 29184;
        public const int LatestRatling50006ProjectileTickStart = 532;
        public const int LatestRatling50006ProjectileTickEnd = 535;
        public const uint LatestRatling50024SamePacketEntityId = 50024;
        public const uint LatestRatling50025SamePacketBehaviorId = 50025;
        public const uint LatestRatling50024SamePacketStartHPWire = 29184;
        public const uint LatestRatling50024SamePacketBadRemoteHPWire = 23880;
        public const uint LatestRatling50024SamePacketDamageWire = 5304;
        public const int LatestRatling50024SamePacketImpactTick = 3670;
        public const uint LatestWhiskerRatling50000Cb030StartHPWire = 29184;
        public const uint LatestWhiskerRatling50000Cb030DamageWire = 4048;
        public const uint LatestWhiskerRatling50000Cb030RuntimeHPWire = 25136;
        public const int LatestWhiskerRatling50000Cb030ImpactTick = 631;
        public const uint LatestAvatar1510EntityId = 1510;
        public const uint LatestAvatar1510SavedHPWire = 150714;
        public const uint LatestAvatar1510ClientLocalHPWire = 150755;
        public const uint LatestAvatar1510BadRemoteHPWire = 152576;
        public const uint LatestAvatar1510MaxHPWire = 152576;
        public const int LatestAvatar1510ObservedNativeRegenTicks = 41;
        public const int LatestAvatar1510AuthoredHealthRegenFactor = 0;
        public const int LatestAvatar1510BadGlobalOnlyHealthRegenFactor = 2;
        public const uint LatestPup50036RuntimeSeed = 0x36F47186;
        public const int LatestPup50036PreHitConsumes = 32;
        public const uint LatestPup50036StartHPWire = 29184;
        public const uint LatestPup50036BadServerSuffixHPWire = 24227;
        public const uint LatestPup50036ClientLocalHPWire = 22921;
        public const uint LatestPup50036BadServerDamageWire = 4957;
        public const uint LatestPup50036NativeBaseDamageReplayDamageWire = 6346;
        public const uint LatestPup50036NativeBaseDamageReplayHPWire = 22838;
        public const uint LatestPup50036HitRaw = 0x75FAC60E;
        public const uint LatestPup50036BlockRaw = 0x28EC2003;
        public const uint LatestPup50036DamageRaw = 0x3C7C808B;
        public const uint LatestPup50030EntityId = 50030;
        public const uint LatestPup50031BehaviorId = 50031;
        public const uint LatestPup50030StartHPWire = 29184;
        public const uint LatestPup50030FirstDamageWire = 6229;
        public const uint LatestPup50030QueuedMoveSuffixHPWire = 22955;
        public const uint LatestPup50030LaterDamageToZeroWire = 22955;
        public const uint LatestPup50030ClientLocalHPWire = 0;
        public const uint LatestRatling50024FirstHitSeed = 0x6B2F7F4D;
        public const uint LatestRatling50024FirstHitEntityId = 50024;
        public const uint LatestRatling50025FirstHitBehaviorId = 50025;
        public const int LatestRatling50024FirstHitPreConsumes = 22;
        public const int LatestRatling50024FirstHitSwing = 1;
        public const uint LatestRatling50024FirstHitRaw = 0x2104A9C4;
        public const uint LatestRatling50024FirstBlockRaw = 0x1BFC8117;
        public const uint LatestRatling50024FirstDamageRaw = 0x150AEBEB;
        public const int LatestRatling50024FirstHitRngAfter = 25;
        public const uint LatestRatling50024FirstHitStartHPWire = 29184;
        public const uint LatestRatling50024FirstHitServerPrimaryWire = 3242;
        public const uint LatestRatling50024FirstHitBadSuffixHPWire = 25942;
        public const uint LatestRatling50024FirstHitExpectedTotalWire = 4156;
        public const uint LatestRatling50024FirstHitExpectedHPWire = 25028;
        public const uint LatestPup50024Cb037Seed = 0x4FC90A7C;
        public const uint LatestPup50024Cb037EntityId = 50024;
        public const uint LatestPup50025Cb037BehaviorId = 50025;
        public const ushort LatestPup50024Cb037UseTargetComponentId = 535;
        public const int LatestPup50024Cb037UseTargetFrame = 790;
        public const int LatestPup50024Cb037BadSuffixFrame = 818;
        public const int LatestPup50024Cb037PreDamageRngPos = 26;
        public const uint LatestPup50024Cb037HitRaw = 0x548AD439;
        public const uint LatestPup50024Cb037BlockRaw = 0x24DA2EAB;
        public const uint LatestPup50024Cb037DamageRaw = 0xD9A445A0;
        public const int LatestPup50024Cb037RngAfter = 29;
        public const uint LatestPup50024Cb037StartHPWire = 29184;
        public const uint LatestPup50024Cb037CritDamageWire = 6714;
        public const uint LatestPup50024Cb037BadSuffixHPWire = 22470;
        public const uint LatestPup50024Cb037ExpectedPreVisibleHitHPWire = 29184;
        public const float LatestPup50024Cb037Distance = 143.9f;
        public const float LatestPup50024Cb037InitUseRange = 250.0f;
        public const float LatestPup50024Cb037AuthoredWeaponRange = 176.0f;
        public const float LatestPup50024Cb037ProjectileSpeed = 180.0f;
        public const float LatestPup50024Cb037ProjectileSize = 10.0f;
        public const float LatestPup50024Cb037PupRadius = 5.0f;
        public const int LatestPup50024Cb037ProjectileFirstStep = 6;
        public const int LatestPup50024Cb037ProjectileLifetimeTicks = 29;
        public const uint LatestLevelUpPreserveStartHPWire = 84992;
        public const uint LatestLevelUpPreserveNewMaxHPWire = 89088;
        public const uint LatestLevelUpPreserveManaSentinelWire = 32123;
        public const uint LatestLevelUpPreserveOldMaxManaWire = 44800;
        public const uint LatestLevelUpPreserveNewMaxManaWire = 46080;

        public static NativeDamageReplayResult RunLatestPup50024Replay()
        {
            var rng = new MersenneTwister(LatestPup50024Seed);
            for (int i = 0; i < LatestPup50024PreHitConsumes; i++)
                rng.Generate();

            var input = new NativeWeaponDamageInput
            {
                Rng = rng,
                Source = "latest-pup-50024-replay",
                AttackerLevel = 3,
                DefenderLevel = 2,
                AttackRating = 210,
                DefenseRating = 52,
                BlockChance = 0,
                DamageLevel = 3,
                DamageBonus = 31,
                DamageMod = 100,
                WeaponDamageF32 = 139,
                WeaponVolatilityF32 = 85,
                CritThreshold = 2048,
                CritDamagePercent = 200
            };

            NativeWeaponDamageResult result = DamageComputer.ResolveNativeWeaponDamage(input);
            return new NativeDamageReplayResult
            {
                Seed = LatestPup50024Seed,
                PreHitConsumes = LatestPup50024PreHitConsumes,
                HitRaw = result.HitRaw,
                BlockRaw = result.BlockRaw,
                DamageRaw = result.DamageRaw,
                HitRoll = result.HitRoll,
                BlockRoll = result.BlockRoll,
                HitThreshold = result.HitThreshold,
                MinDamageWire = result.MinDamageF32,
                MaxDamageWire = result.MaxDamageF32,
                DamageWire = result.DamageWire,
                ServerLoggedDamageWire = LatestPup50024ServerDamageWire,
                NativeObservedDamageWire = LatestPup50024NativeObservedDamageWire,
                ServerLoggedHPAfter = LatestPup50024StartHPWire - LatestPup50024ServerDamageWire,
                NativeObservedHPAfter = LatestPup50024StartHPWire - LatestPup50024NativeObservedDamageWire,
                ReplayHPAfter = LatestPup50024StartHPWire - result.DamageWire,
                RoomRngAfter = result.RoomRngAfter
            };
        }

        public static LatestPup50036NativeBaseDamageReplayResult RunLatestPup50036NativeBaseDamageReplay()
        {
            var rng = new MersenneTwister(LatestPup50036RuntimeSeed);
            for (int i = 0; i < LatestPup50036PreHitConsumes; i++)
                rng.Generate();

            int runtimeBaseDamage = DamageComputer.ResolveNativeWeaponRuntimeBaseDamageLevel(
                playerLevel: 3,
                storedLevel: -1,
                fallbackItemLevel: 1,
                out bool tracksPlayerLevel,
                out string runtimeBaseSource);

            int explicitStoredBaseDamage = DamageComputer.ResolveNativeWeaponRuntimeBaseDamageLevel(
                playerLevel: 3,
                storedLevel: 1,
                fallbackItemLevel: 1,
                out bool explicitTracksPlayerLevel,
                out string explicitBaseSource);

            var input = new NativeWeaponDamageInput
            {
                Rng = rng,
                Source = "latest-pup-50036-native-base-damage-replay",
                AttackerLevel = 3,
                DefenderLevel = 2,
                AttackRating = 210,
                DefenseRating = 52,
                BlockChance = 0,
                DamageLevel = runtimeBaseDamage,
                DamageBonus = 31,
                DamageMod = 100,
                WeaponDamageF32 = 139,
                WeaponVolatilityF32 = 85,
                CritThreshold = 2048,
                CritDamagePercent = 200
            };

            NativeWeaponDamageResult result = DamageComputer.ResolveNativeWeaponDamage(input);
            uint totalDamageWire = result.TotalDamageWire != 0 ? result.TotalDamageWire : result.DamageWire;
            return new LatestPup50036NativeBaseDamageReplayResult
            {
                Seed = LatestPup50036RuntimeSeed,
                PreHitConsumes = LatestPup50036PreHitConsumes,
                RuntimeBaseDamage = runtimeBaseDamage,
                RuntimeBaseTracksPlayerLevel = tracksPlayerLevel,
                RuntimeBaseSource = runtimeBaseSource,
                ExplicitStoredBaseDamage = explicitStoredBaseDamage,
                ExplicitStoredTracksPlayerLevel = explicitTracksPlayerLevel,
                ExplicitStoredBaseSource = explicitBaseSource,
                HitRaw = result.HitRaw,
                BlockRaw = result.BlockRaw,
                DamageRaw = result.DamageRaw,
                HitRoll = result.HitRoll,
                BlockRoll = result.BlockRoll,
                HitThreshold = result.HitThreshold,
                MinDamageWire = result.MinDamageF32,
                MaxDamageWire = result.MaxDamageF32,
                DamageWire = result.DamageWire,
                TotalDamageWire = totalDamageWire,
                BadServerDamageWire = LatestPup50036BadServerDamageWire,
                StartHPWire = LatestPup50036StartHPWire,
                ReplayHPAfter = ApplyDamageWire(LatestPup50036StartHPWire, totalDamageWire),
                BadServerSuffixHPWire = LatestPup50036BadServerSuffixHPWire,
                ClientLocalHPWire = LatestPup50036ClientLocalHPWire,
                RoomRngAfter = result.RoomRngAfter,
                SuffixWritesAlreadyComputedHP = true,
                PupHasAuthoredNegativeHPRegen = false
            };
        }

        public static NativeFixed32ReplayResult RunStarterCrossbowFixed32Replay()
        {
            return new NativeFixed32ReplayResult
            {
                DamageText = "0.54",
                VolatilityText = "0.33",
                DamageF32 = DamageComputer.NativeFixed32FromAuthoredDecimal(0.54f),
                VolatilityF32 = DamageComputer.NativeFixed32FromAuthoredDecimal(0.33f)
            };
        }

        public static NativeWeaponApplyDamageRngOrderReplayResult RunWeaponApplyDamageRngOrderReplay()
        {
            NativeWeaponDamageResult miss = FindDamageReplay(AttackResultType.Miss, 0, 10000, 0, 0);
            NativeWeaponDamageResult block = FindDamageReplay(AttackResultType.Block, 10000, 0, 101, 0);
            NativeWeaponDamageResult hit = FindDamageReplay(AttackResultType.Hit, 10000, 0, 0, 0);
            NativeWeaponDamageResult crit = FindDamageReplay(AttackResultType.Critical, 10000, 0, 0, 0x6400);

            return new NativeWeaponApplyDamageRngOrderReplayResult
            {
                MissRngAfter = miss.RoomRngAfter,
                BlockRngAfter = block.RoomRngAfter,
                HitRngAfter = hit.RoomRngAfter,
                CritRngAfter = crit.RoomRngAfter,
                CritConsumedExtraWeaponDraw = crit.RoomRngAfter != hit.RoomRngAfter,
                BlockCompareIsStrict = block.IsBlocked && block.BlockRoll < block.BlockChance,
                DamageDrawOnlyForLandedHit =
                    miss.DamageRaw == 0 &&
                    block.DamageRaw == 0 &&
                    hit.DamageRaw != 0 &&
                    crit.DamageRaw != 0
            };
        }

        public static NativeWeaponDamageAddsReplayResult RunWeaponDamageAddsReplay()
        {
            var state = new PlayerState();
            state.EquipmentStats["SHADOW_DAMAGE_WEAPON_ADD"] = 2;
            state.EquipmentStats["SHADOW_DAMAGE_BONUS"] = 1;
            state.EquipmentStats["SHADOW_DAMAGE_MOD"] = 50;
            state.EquipmentStats["FIRE_DAMAGE_WEAPON_ADD"] = 1;
            state.EquipmentStats["POISON_DAMAGE_BONUS"] = 99;

            const int weaponDamageF32 = 0x180;
            List<NativeWeaponDamageEvent> adds = DamageComputer.ResolveNativeWeaponDamageAdds(state, weaponDamageF32);
            NativeWeaponDamageEvent shadow = adds.FirstOrDefault(e => string.Equals(e.Element, "Shadow", StringComparison.OrdinalIgnoreCase));
            NativeWeaponDamageEvent fire = adds.FirstOrDefault(e => string.Equals(e.Element, "Fire", StringComparison.OrdinalIgnoreCase));
            NativeWeaponDamageEvent poison = adds.FirstOrDefault(e => string.Equals(e.Element, "Poison", StringComparison.OrdinalIgnoreCase));

            return new NativeWeaponDamageAddsReplayResult
            {
                SlotCount = DamageComputer.NativeWeaponDamageAddSlots.Length,
                AddCount = adds.Count,
                ShadowDamageTypeId = shadow != null ? shadow.DamageTypeId : -1,
                ShadowDamageWire = shadow != null ? shadow.DamageWire : 0,
                FireDamageTypeId = fire != null ? fire.DamageTypeId : -1,
                FireDamageWire = fire != null ? fire.DamageWire : 0,
                PoisonSkippedWithoutWeaponAdd = poison == null,
                WeaponDamageF32 = weaponDamageF32
            };
        }

        public static LatestRatling50006ClosureReplayResult RunLatestRatling50006ClosureReplay()
        {
            return new LatestRatling50006ClosureReplayResult
            {
                Seed = LatestRatling50006RuntimeSeed,
                EntityId = LatestRatling50006EntityId,
                BehaviorId = LatestRatling50007BehaviorId,
                ClientVisibleHPWire = LatestRatling50006ClientHPWire,
                BadServerSuffixHPWire = LatestRatling50006BadServerSuffixHPWire,
                DeltaWire = LatestRatling50006BadServerSuffixHPWire - LatestRatling50006ClientHPWire,
                ProjectileTickStart = LatestRatling50006ProjectileTickStart,
                ProjectileTickEnd = LatestRatling50006ProjectileTickEnd,
                RuntimeInstanceKeyRequired = true,
                SuffixPayloadShapeIsNative = EntitySynchInfoPayload.FromHP(LatestRatling50006ClientHPWire).Flags == 0x02,
                SamePacketProjectileImpactBeforeValidate = false
            };
        }

        public static LatestRatling50024SamePacketCutoffReplayResult RunLatestRatling50024SamePacketCutoffReplay()
        {
            uint runtimeHPAfterProjectile = ApplyDamageWire(LatestRatling50024SamePacketStartHPWire, LatestRatling50024SamePacketDamageWire);
            return new LatestRatling50024SamePacketCutoffReplayResult
            {
                EntityId = LatestRatling50024SamePacketEntityId,
                BehaviorId = LatestRatling50025SamePacketBehaviorId,
                ClientValidateHPWire = LatestRatling50024SamePacketStartHPWire,
                BadRemoteHPWire = LatestRatling50024SamePacketBadRemoteHPWire,
                DamageWire = LatestRatling50024SamePacketDamageWire,
                RuntimeHPAfterProjectile = runtimeHPAfterProjectile,
                ComponentSuffixHPWire = LatestRatling50024SamePacketStartHPWire,
                NextEntityBoundarySuffixHPWire = runtimeHPAfterProjectile,
                ImpactTick = LatestRatling50024SamePacketImpactTick,
                SamePacketSubentityDamageBeforeValidate = false
            };
        }

        public static LatestAvatar1510HeroRegenReplayResult RunLatestAvatar1510HeroRegenReplay()
        {
            int nativeFloorDelta = CombatManager.ComputeNativeUnitRegenDeltaWire(
                LatestAvatar1510MaxHPWire,
                LatestAvatar1510AuthoredHealthRegenFactor,
                0,
                0,
                cooldownActive: false);
            int badGlobalOnlyDelta = CombatManager.ComputeNativeUnitRegenDeltaWire(
                LatestAvatar1510MaxHPWire,
                LatestAvatar1510BadGlobalOnlyHealthRegenFactor,
                0,
                0,
                cooldownActive: false);

            uint replayHP = LatestAvatar1510SavedHPWire;
            uint badGlobalOnlyHP = LatestAvatar1510SavedHPWire;
            for (int i = 0; i < LatestAvatar1510ObservedNativeRegenTicks; i++)
            {
                replayHP = CombatManager.ApplyNativeUnitHPShiftWire(replayHP, LatestAvatar1510MaxHPWire, nativeFloorDelta);
                badGlobalOnlyHP = CombatManager.ApplyNativeUnitHPShiftWire(badGlobalOnlyHP, LatestAvatar1510MaxHPWire, badGlobalOnlyDelta);
            }

            return new LatestAvatar1510HeroRegenReplayResult
            {
                EntityId = LatestAvatar1510EntityId,
                SavedHPWire = LatestAvatar1510SavedHPWire,
                ClientLocalHPWire = LatestAvatar1510ClientLocalHPWire,
                BadRemoteHPWire = LatestAvatar1510BadRemoteHPWire,
                MaxHPWire = LatestAvatar1510MaxHPWire,
                AuthoredHealthRegenFactor = LatestAvatar1510AuthoredHealthRegenFactor,
                BadGlobalOnlyHealthRegenFactor = LatestAvatar1510BadGlobalOnlyHealthRegenFactor,
                NativeFloorDeltaWire = nativeFloorDelta,
                BadGlobalOnlyDeltaWire = badGlobalOnlyDelta,
                ObservedNativeRegenTicks = LatestAvatar1510ObservedNativeRegenTicks,
                ReplayHPWire = replayHP,
                BadGlobalOnlyHPWire = badGlobalOnlyHP
            };
        }

        public static NativeRoomRuntimeLateJoinReplayResult RunNativeRoomRuntimeLateJoinReplay()
        {
            var runtime = new NativeRoomRuntime("dungeon00_level01_inst153");
            runtime.Initialize(LatestRatling50006RuntimeSeed, "selftest-fresh-spawn");
            runtime.RoomRng.Generate();
            runtime.RoomRng.Generate();
            int rngPosBeforeLateJoin = runtime.RngCallsSinceReseed;
            bool sameSeedInitialized = runtime.EnsureInitialized(LatestRatling50006RuntimeSeed, "selftest-late-join-same-seed");
            int rngPosAfterSameSeedLateJoin = runtime.RngCallsSinceReseed;
            bool differentSeedInitialized = runtime.EnsureInitialized(0x12345678, "selftest-late-join-different-seed");
            int rngPosAfterDifferentSeedLateJoin = runtime.RngCallsSinceReseed;

            return new NativeRoomRuntimeLateJoinReplayResult
            {
                InstanceKey = runtime.InstanceKey,
                Seed = runtime.Seed,
                SameSeedInitialized = sameSeedInitialized,
                DifferentSeedInitialized = differentSeedInitialized,
                RngPosBeforeLateJoin = rngPosBeforeLateJoin,
                RngPosAfterSameSeedLateJoin = rngPosAfterSameSeedLateJoin,
                RngPosAfterDifferentSeedLateJoin = rngPosAfterDifferentSeedLateJoin
            };
        }

        public static NativeStarterCrossbowCycleReplayResult RunStarterCrossbowCycleReplay()
        {
            const int selector = 0;
            const int animationId = 310;
            const int numFrames = 17;
            const int triggerTime = 2;
            const int soundTriggerTime = 2;
            const float weaponSpeed = 95f;

            int cycleTicks = Math.Max(1, (int)Math.Floor(numFrames * 100f / weaponSpeed));
            int triggerTick = Math.Max(1, (int)Math.Floor(triggerTime * 100f / weaponSpeed) + 1);
            int soundTick = Math.Max(1, (int)Math.Floor(soundTriggerTime * 100f / weaponSpeed) + 1);
            var starterState = new PlayerState
            {
                WeaponClass = "2HRANGED",
                WeaponSpeed = weaponSpeed,
                WeaponCooldown = 0f,
                WeaponUsesProjectile = true
            };

            return new NativeStarterCrossbowCycleReplayResult
            {
                Selector = selector,
                AnimationId = animationId,
                NumFrames = numFrames,
                TriggerTime = triggerTime,
                SoundTriggerTime = soundTriggerTime,
                WeaponSpeed = weaponSpeed,
                CycleTicks = cycleTicks,
                ProjectileEventTick = triggerTick,
                SoundEventTick = soundTick,
                UseCooldownTicks = DamageComputer.ResolveNativeBasicAttackCooldownTicks(starterState),
                UseProjectileCreatesProjectileOnly = true
            };
        }

        public static NativeProjectilePrestepReplayResult RunProjectilePrestepReplay()
        {
            const float crossbowSpeed = 180f;
            const float poisonSpeed = 200f;
            const float targetCollisionRadius = 5f;
            const float crossbowProjectileSize = 10f;
            const float poisonProjectileSize = 8f;
            const float poisonProjectileLifespanTicks = 23f;
            const float poisonTargetProjectedDistance = 156.9f;
            const float poisonAimDistance = 135.3f;

            float crossbowStep = WeaponCycleTracker.NativeProjectileStepDistance(crossbowSpeed);
            float poisonStep = WeaponCycleTracker.NativeProjectileStepDistance(poisonSpeed);
            float poisonMaxDistance = poisonSpeed * poisonProjectileLifespanTicks / NativeCombatHz;
            float poisonRadius = WeaponCycleTracker.NativeProjectileCollisionRadius(targetCollisionRadius, poisonProjectileSize);
            float poisonEntryDistance = Math.Max(0f, poisonTargetProjectedDistance - poisonRadius);

            return new NativeProjectilePrestepReplayResult
            {
                CrossbowSpeed = crossbowSpeed,
                CrossbowStepDistance = crossbowStep,
                CrossbowInitialDistance = WeaponCycleTracker.NativeProjectileInitialDistance(crossbowSpeed, 200f),
                CrossbowTargetCollisionRadius = targetCollisionRadius,
                CrossbowProjectileSize = crossbowProjectileSize,
                CrossbowHitRadius = WeaponCycleTracker.NativeProjectileCollisionRadius(targetCollisionRadius, crossbowProjectileSize),
                PoisonSpeed = poisonSpeed,
                PoisonStepDistance = poisonStep,
                PoisonInitialDistance = WeaponCycleTracker.NativeProjectileInitialDistance(poisonSpeed, poisonMaxDistance),
                PoisonProjectileSize = poisonProjectileSize,
                PoisonHitRadius = poisonRadius,
                PoisonAimDistance = poisonAimDistance,
                PoisonProjectedDistance = poisonTargetProjectedDistance,
                PoisonMaxDistance = poisonMaxDistance,
                PoisonEntryDistance = poisonEntryDistance
            };
        }

        public static NativeHpRuntimeReplayResult RunLatestRatling50000HpRuntimeReplay()
        {
            int damageF32 = DamageComputer.NativeFixed32FromAuthoredDecimal(0.54f);
            int volatilityF32 = DamageComputer.NativeFixed32FromAuthoredDecimal(0.33f);
            DamageComputer.ComputeNativeWeaponDamageRange(3, 31, 100, damageF32, volatilityF32, out int minDamage, out int maxDamage);
            int damageWire = DamageComputer.RollDamageRange(minDamage, maxDamage, LatestRatling50000DamageRaw);
            uint firstSuffixHP = ApplyDamageWire(LatestRatling50000StartHPWire, (uint)damageWire);

            const int unitRuntimeTicksBeforeSecondSuffix = 0;
            const int baseRegen = 0;
            int perTickRegen = CombatManager.ComputeNativeUnitRegenDeltaWire(LatestRatling50000StartHPWire, baseRegen, 0, 0, false);
            const int defaultWorldSettingsField = 15;
            bool stockUnitDamageRegenDelayClears = (defaultWorldSettingsField & 0x00000800) != 0;
            uint secondSuffixHP = firstSuffixHP;
            for (int i = 0; i < unitRuntimeTicksBeforeSecondSuffix; i++)
                secondSuffixHP = CombatManager.ApplyNativeUnitHPShiftWire(secondSuffixHP, LatestRatling50000StartHPWire, perTickRegen);

            return new NativeHpRuntimeReplayResult
            {
                StartHPWire = LatestRatling50000StartHPWire,
                FirstSuffixHPWire = firstSuffixHP,
                ClientCrashHPWire = LatestRatling50000ClientCrashHPWire,
                DamageRaw = LatestRatling50000DamageRaw,
                DamageF32 = damageF32,
                VolatilityF32 = volatilityF32,
                MinDamageWire = minDamage,
                MaxDamageWire = maxDamage,
                DamageWire = (uint)damageWire,
                UnitRuntimeTicksBeforeSecondSuffix = unitRuntimeTicksBeforeSecondSuffix,
                BaseRegen = baseRegen,
                RegenMod = 0,
                AdditiveRegen = 0,
                PerTickRegenWire = perTickRegen,
                DefaultWorldSettingsField = defaultWorldSettingsField,
                StockUnitDamageRegenDelayClears = stockUnitDamageRegenDelayClears,
                SecondSuffixHPWire = secondSuffixHP,
                PlainWeaponPostApplyEffectRaw = 0
            };
        }

        public static NativeCombatClockReplayResult RunLatestRatling50000ClockReplay()
        {
            const int nativeTicksObserved = 30;
            const int poisonDurationSeconds = 4;
            const int poisonFrequencySeconds = 1;
            const int rangedRepeatQueueCount = 0;
            const int rangedHeldContinuationCount = 1;
            const int rangedImmediateProjectileFromRedundantUse = 0;

            int actorAdvances = 0;
            for (int tick = 0; tick < nativeTicksObserved; tick++)
                actorAdvances++;

            return new NativeCombatClockReplayResult
            {
                Seed = LatestRatling50000ClockSeed,
                NativeHz = NativeCombatHz,
                NativeTicksObserved = nativeTicksObserved,
                ActorAdvances = actorAdvances,
                SuffixFlushAttempts = LatestRatling50000SuffixFlushAttempts,
                SuffixAdvancedTicks = 0,
                PoisonDurationTicks = poisonDurationSeconds * NativeCombatHz,
                PoisonFrequencyTicks = poisonFrequencySeconds * NativeCombatHz,
                PoisonMaxTicks = poisonDurationSeconds / poisonFrequencySeconds,
                RangedRepeatQueueCount = rangedRepeatQueueCount,
                RangedHeldContinuationCount = rangedHeldContinuationCount,
                RangedImmediateProjectileFromRedundantUse = rangedImmediateProjectileFromRedundantUse,
                RangedRepeatResolvedFromDueTick = 0
            };
        }

        public static LatestPup50018HeldRangedContinuationReplayResult RunLatestPup50018HeldRangedContinuationReplay()
        {
            uint hpAfterFirst = ApplyDamageWire(LatestPup50018StartHPWire, LatestPup50018FirstDamageWire);
            uint hpAfterSecond = ApplyDamageWire(hpAfterFirst, LatestPup50018SecondDamageWire);
            uint hpAfterThird = ApplyDamageWire(hpAfterSecond, LatestPup50018ThirdDamageWire);
            return new LatestPup50018HeldRangedContinuationReplayResult
            {
                Seed = LatestPup50018HeldSeed,
                EntityId = LatestPup50018EntityId,
                BehaviorId = LatestPup50019BehaviorId,
                StartHPWire = LatestPup50018StartHPWire,
                BadRemoteHPWire = LatestPup50018BadRemoteHPWire,
                ClientCrashHPWire = LatestPup50018ClientHPWire,
                FirstDamageWire = LatestPup50018FirstDamageWire,
                SecondDamageWire = LatestPup50018SecondDamageWire,
                ThirdDamageWire = LatestPup50018ThirdDamageWire,
                HPAfterFirstProjectile = hpAfterFirst,
                HPAfterSecondProjectile = hpAfterSecond,
                HPAfterThirdProjectile = hpAfterThird,
                FirstProjectileDueTick = LatestPup50018FirstProjectileDueTick,
                SecondProjectileDueTick = LatestPup50018SecondProjectileDueTick,
                StaleSuffixCutoffTick = LatestPup50018StaleSuffixCutoffTick,
                RedundantRangedUseImmediateProjectiles = 0,
                RedundantRangedHeldContinuations = 2
            };
        }

        public static LatestPup50030NativeBehaviorWriterReplayResult RunLatestPup50030NativeBehaviorWriterReplay()
        {
            uint hpAfterFirst = ApplyDamageWire(LatestPup50030StartHPWire, LatestPup50030FirstDamageWire);
            uint hpAtWriter = ApplyDamageWire(hpAfterFirst, LatestPup50030LaterDamageToZeroWire);
            var suffixWriter = new LEWriter();
            EntitySynchInfoPayload.FromHP(hpAtWriter).Write(suffixWriter);
            byte[] suffixPayload = suffixWriter.ToArray();

            return new LatestPup50030NativeBehaviorWriterReplayResult
            {
                EntityId = LatestPup50030EntityId,
                BehaviorId = LatestPup50031BehaviorId,
                StartHPWire = LatestPup50030StartHPWire,
                FirstDamageWire = LatestPup50030FirstDamageWire,
                HPAfterFirstProjectile = hpAfterFirst,
                QueuedMoveSuffixHPWire = LatestPup50030QueuedMoveSuffixHPWire,
                LaterDamageToZeroWire = LatestPup50030LaterDamageToZeroWire,
                ClientLocalHPWire = LatestPup50030ClientLocalHPWire,
                NativeWriterCurrentHPWire = hpAtWriter,
                NativeWriterSuffixFlag = suffixPayload.Length > 0 ? suffixPayload[0] : (byte)0,
                NativeWriterSuffixHPWire = suffixPayload.Length >= 5 ? BitConverter.ToUInt32(suffixPayload, 1) : 0u,
                PostZeroNonZeroMonsterSuffixEmitted = false
            };
        }

        public static LatestPoisonShotHeldLeftCrashReplayResult RunLatestPoisonShotHeldLeftCrashReplay()
        {
            uint hpAfterFirst = ApplyDamageWire(LatestPoisonHeldRatlingStartHPWire, LatestPoisonHeldRatlingFirstWeaponDamageWire);
            uint hpAfterSecond = ApplyDamageWire(hpAfterFirst, LatestPoisonHeldRatlingSecondWeaponDamageWire);
            return new LatestPoisonShotHeldLeftCrashReplayResult
            {
                StartHPWire = LatestPoisonHeldRatlingStartHPWire,
                RemoteSuffixHPWire = LatestPoisonHeldRatlingRemoteHPWire,
                ClientCrashHPWire = LatestPoisonHeldRatlingClientHPWire,
                FirstWeaponDamageWire = LatestPoisonHeldRatlingFirstWeaponDamageWire,
                SecondWeaponDamageWire = LatestPoisonHeldRatlingSecondWeaponDamageWire,
                HPAfterFirstWeaponDamage = hpAfterFirst,
                HPAfterSecondWeaponDamage = hpAfterSecond,
                PoisonImpactCountBeforeSuffix = 2,
                PoisonDotTicksBeforeSuffix = 0,
                RedundantRangedUseImmediateProjectiles = 0,
                RedundantRangedHeldContinuations = 1
            };
        }

        public static LatestWhiskerRatlingFirstProjectileReplayResult RunLatestWhiskerRatling50000FirstProjectileReplay()
        {
            uint nativeHPAfterFirst = ApplyDamageWire(LatestWhiskerRatling50000StartHPWire, LatestWhiskerRatling50000NativeFirstDamageWire);
            uint badServerHPAfterFirst = ApplyDamageWire(LatestWhiskerRatling50000StartHPWire, LatestWhiskerRatling50000BadServerDamageWire);
            return new LatestWhiskerRatlingFirstProjectileReplayResult
            {
                StartHPWire = LatestWhiskerRatling50000StartHPWire,
                ExpectedClientHPWire = LatestWhiskerRatling50000ClientFirstHPWire,
                NativeFirstDamageWire = LatestWhiskerRatling50000NativeFirstDamageWire,
                NativeHPAfterFirstProjectile = nativeHPAfterFirst,
                BadServerFirstDamageWire = LatestWhiskerRatling50000BadServerDamageWire,
                BadServerRemoteHPWire = LatestWhiskerRatling50000BadServerRemoteHPWire,
                BadServerHPAfterFirstProjectile = badServerHPAfterFirst,
                RedundantRangedUseImmediateProjectiles = 0,
                RedundantRangedHeldContinuations = 0,
                SuffixReadsAlreadyAppliedHP = true
            };
        }

        public static LatestRatling50024FirstHitDamageGapReplayResult RunLatestRatling50024FirstHitDamageGapReplay()
        {
            var rng = new MersenneTwister(LatestRatling50024FirstHitSeed);
            for (int i = 0; i < LatestRatling50024FirstHitPreConsumes; i++)
                rng.Generate();

            var input = new NativeWeaponDamageInput
            {
                Rng = rng,
                Source = "latest-ratling-50024-first-hit-gap",
                AttackerLevel = 3,
                DefenderLevel = 2,
                AttackRating = 210,
                DefenseRating = 52,
                BlockChance = 0,
                DamageLevel = 3,
                DamageBonus = 31,
                DamageMod = 100,
                WeaponClassId = DamageComputer.ResolveNativeWeaponClassId("2HRANGED"),
                DamageTypeId = DamageComputer.ResolveNativeDamageTypeId("PIERCING"),
                WeaponDamageF32 = 139,
                WeaponVolatilityF32 = 85,
                CritThreshold = 2048,
                CritDamagePercent = 200,
                IncludeWeaponDamageAdds = true
            };

            NativeWeaponDamageResult result = DamageComputer.ResolveNativeWeaponDamage(input);
            uint primaryHPAfter = ApplyDamageWire(LatestRatling50024FirstHitStartHPWire, result.DamageWire);
            return new LatestRatling50024FirstHitDamageGapReplayResult
            {
                Seed = LatestRatling50024FirstHitSeed,
                EntityId = LatestRatling50024FirstHitEntityId,
                BehaviorId = LatestRatling50025FirstHitBehaviorId,
                Swing = LatestRatling50024FirstHitSwing,
                PreHitConsumes = LatestRatling50024FirstHitPreConsumes,
                HitRaw = result.HitRaw,
                BlockRaw = result.BlockRaw,
                DamageRaw = result.DamageRaw,
                RoomRngAfter = result.RoomRngAfter,
                WeaponClassId = result.WeaponClassId,
                DamageTypeId = result.DamageTypeId,
                DamageBonus = result.DamageBonus,
                DamageMod = result.DamageMod,
                MinDamageWire = result.MinDamageF32,
                MaxDamageWire = result.MaxDamageF32,
                PrimaryDamageWire = result.DamageWire,
                AddCount = result.DamageAdds != null ? result.DamageAdds.Count : 0,
                TotalDamageWire = result.TotalDamageWire,
                StartHPWire = LatestRatling50024FirstHitStartHPWire,
                PrimaryHPAfter = primaryHPAfter,
                BadFirstMoveSuffixHPWire = LatestRatling50024FirstHitBadSuffixHPWire,
                ExpectedTotalDamageWire = LatestRatling50024FirstHitExpectedTotalWire,
                ExpectedPostImpactHPWire = LatestRatling50024FirstHitExpectedHPWire,
                MissingSameImpactWire = primaryHPAfter > LatestRatling50024FirstHitExpectedHPWire
                    ? primaryHPAfter - LatestRatling50024FirstHitExpectedHPWire
                    : 0,
                NativeApplyBoundaryIsSingle = true,
                PacketDelayOrSuppressionUsed = false
            };
        }

        public static LatestPup50024Cb037UseTargetTimingReplayResult RunLatestPup50024Cb037UseTargetTimingReplay()
        {
            var rng = new MersenneTwister(LatestPup50024Cb037Seed);
            for (int i = 0; i < LatestPup50024Cb037PreDamageRngPos; i++)
                rng.Generate();

            int preInitUseRngPos = rng.CallsSinceReseed;
            uint hitRaw = rng.Generate();
            uint blockRaw = rng.Generate();
            uint damageRaw = rng.Generate();
            uint postImpactHP = ApplyDamageWire(LatestPup50024Cb037StartHPWire, LatestPup50024Cb037CritDamageWire);

            return new LatestPup50024Cb037UseTargetTimingReplayResult
            {
                Seed = LatestPup50024Cb037Seed,
                EntityId = LatestPup50024Cb037EntityId,
                BehaviorId = LatestPup50025Cb037BehaviorId,
                UseTargetComponentId = LatestPup50024Cb037UseTargetComponentId,
                UseTargetFrame = LatestPup50024Cb037UseTargetFrame,
                BadSuffixFrame = LatestPup50024Cb037BadSuffixFrame,
                PreInitUseRngPos = preInitUseRngPos,
                HitRaw = hitRaw,
                BlockRaw = blockRaw,
                DamageRaw = damageRaw,
                RngAfterImpact = rng.CallsSinceReseed,
                StartHPWire = LatestPup50024Cb037StartHPWire,
                PreVisibleHitHPWire = LatestPup50024Cb037ExpectedPreVisibleHitHPWire,
                DamageWire = LatestPup50024Cb037CritDamageWire,
                BadPrematureSuffixHPWire = LatestPup50024Cb037BadSuffixHPWire,
                PostImpactHPWire = postImpactHP,
                Distance = LatestPup50024Cb037Distance,
                InitUseRange = LatestPup50024Cb037InitUseRange,
                AuthoredWeaponRange = LatestPup50024Cb037AuthoredWeaponRange,
                ProjectileSize = LatestPup50024Cb037ProjectileSize,
                ProjectileNarrowRadius = WeaponCycleTracker.NativeProjectileCollisionRadius(LatestPup50024Cb037PupRadius, LatestPup50024Cb037ProjectileSize),
                ProjectileFirstStep = LatestPup50024Cb037ProjectileFirstStep,
                ProjectileLifetimeTicks = LatestPup50024Cb037ProjectileLifetimeTicks,
                ProjectileCreatedBeforeInitUse = false,
                DamageRngAdvancedBeforeInitUse = false,
                SameTickCreateDamageResolved = false,
                PacketDelayOrSuppressionUsed = false
            };
        }

        public static LatestLevelUpPreserveReplayResult RunLatestLevelUpPreserveReplay()
        {
            uint preservedHP = Math.Min(LatestLevelUpPreserveStartHPWire, LatestLevelUpPreserveNewMaxHPWire);
            uint preservedMana = Math.Min(LatestLevelUpPreserveManaSentinelWire, LatestLevelUpPreserveNewMaxManaWire);
            return new LatestLevelUpPreserveReplayResult
            {
                OldHPWire = LatestLevelUpPreserveStartHPWire,
                NewMaxHPWire = LatestLevelUpPreserveNewMaxHPWire,
                PreservedHPWire = preservedHP,
                OldManaWire = LatestLevelUpPreserveManaSentinelWire,
                OldMaxManaWire = LatestLevelUpPreserveOldMaxManaWire,
                NewMaxManaWire = LatestLevelUpPreserveNewMaxManaWire,
                PreservedManaWire = preservedMana
            };
        }

        public static NativeMalformedAggroPacketReplayResult RunLatestPup50006MalformedAggroPacketReplay()
        {
            byte[] malformedPacket =
            {
                0x07, 0x35, 0x57, 0xC3, 0x64, 0x03, 0x09, 0x00,
                0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFE, 0x01,
                0x02, 0x00, 0x72, 0x00, 0x00, 0x06
            };

            const int unitBehaviorPayloadOffset = 5;
            const int entitySynchInfoOffset = unitBehaviorPayloadOffset + 1;
            const int targetEntityOffset = 14;
            const int deferredHpSuffixOffset = 16;
            byte remoteFlags = malformedPacket[entitySynchInfoOffset];
            bool remoteReadsHP = (remoteFlags & 0x02) != 0;
            uint remoteHPWire = remoteReadsHP && entitySynchInfoOffset + 4 < malformedPacket.Length
                ? BitConverter.ToUInt32(malformedPacket, entitySynchInfoOffset + 1)
                : 0u;

            var moveSuffixWriter = new LEWriter();
            EntitySynchInfoPayload.FromHP(LatestPup50006AggroHPWire).Write(moveSuffixWriter);
            byte[] moveSuffix = moveSuffixWriter.ToArray();

            var attackSuffixWriter = new LEWriter();
            EntitySynchInfoPayload.FromHP(LatestPup50006AggroHPWire).Write(attackSuffixWriter);
            byte[] attackSuffix = attackSuffixWriter.ToArray();

            return new NativeMalformedAggroPacketReplayResult
            {
                BeginStream = malformedPacket[0],
                Opcode = malformedPacket[1],
                ComponentId = BitConverter.ToUInt16(malformedPacket, 2),
                ComponentUpdateSubtype = malformedPacket[4],
                UnitBehaviorPayloadOffset = unitBehaviorPayloadOffset,
                UnitBehaviorPayloadBytes = 1,
                UnitBehaviorPayloadByte = malformedPacket[unitBehaviorPayloadOffset],
                EntitySynchInfoOffset = entitySynchInfoOffset,
                RemoteFlags = remoteFlags,
                RemoteReadsHP = remoteReadsHP,
                RemoteHPWire = remoteHPWire,
                TargetEntityId = BitConverter.ToUInt16(malformedPacket, targetEntityOffset),
                DeferredHPFlagOffset = deferredHpSuffixOffset,
                DeferredHPFlag = malformedPacket[deferredHpSuffixOffset],
                DeferredHPWire = BitConverter.ToUInt32(malformedPacket, deferredHpSuffixOffset + 1),
                EndStream = malformedPacket[malformedPacket.Length - 1],
                AggroPathShouldEmitPacket = false,
                MonsterMoveSubtype = 0x04,
                MonsterAttackSubtype = 0x04,
                MonsterMoveSuffixFlag = moveSuffix[0],
                MonsterMoveSuffixHPWire = BitConverter.ToUInt32(moveSuffix, 1),
                MonsterAttackSuffixFlag = attackSuffix[0],
                MonsterAttackSuffixHPWire = BitConverter.ToUInt32(attackSuffix, 1)
            };
        }

        public static NativeRangedSeed2848ReplayResult RunLatestPupSeed2848RangedReplay()
        {
            var rng = new MersenneTwister(LatestPup50000Cb024Seed);
            for (int i = 0; i < LatestPup50000Cb024PreHitConsumes; i++)
                rng.Generate();

            var damages = new List<uint>();
            var hpAfter = new List<uint>();
            var rngAfter = new List<int>();
            var hitRaw = new List<uint>();
            var blockRaw = new List<uint>();
            var damageRaw = new List<uint>();
            var critical = new List<bool>();
            uint hp = LatestPup50000Cb024FirstStartHPWire;

            for (int i = 0; i < 4; i++)
            {
                var input = new NativeWeaponDamageInput
                {
                    Rng = rng,
                    Source = "latest-pup-50000-cb024-ranged-replay",
                    AttackerLevel = 3,
                    DefenderLevel = 2,
                    AttackRating = 210,
                    DefenseRating = 52,
                    BlockChance = 0,
                    DamageLevel = 3,
                    DamageBonus = 31,
                    DamageMod = 100,
                    WeaponDamageF32 = 139,
                    WeaponVolatilityF32 = 85,
                    CritThreshold = 2048,
                    CritDamagePercent = 200
                };

                NativeWeaponDamageResult result = DamageComputer.ResolveNativeWeaponDamage(input);
                damages.Add(result.DamageWire);
                hp = ApplyDamageWire(hp, result.DamageWire);
                hpAfter.Add(hp);
                rngAfter.Add(result.RoomRngAfter);
                hitRaw.Add(result.HitRaw);
                blockRaw.Add(result.BlockRaw);
                damageRaw.Add(result.DamageRaw);
                critical.Add(result.IsCritical);
            }

            return new NativeRangedSeed2848ReplayResult
            {
                Seed = LatestPup50000Cb024Seed,
                PreHitConsumes = LatestPup50000Cb024PreHitConsumes,
                DamageWire = damages.ToArray(),
                HPAfter = hpAfter.ToArray(),
                RoomRngAfter = rngAfter.ToArray(),
                HitRaw = hitRaw.ToArray(),
                BlockRaw = blockRaw.ToArray(),
                DamageRaw = damageRaw.ToArray(),
                Critical = critical.ToArray(),
                GlobalAttackSoundRngSeparate = true
            };
        }

        public static NativeSchedulerOrderReplayResult RunNativeSchedulerOrderReplay()
        {
            const float suffixNow = 1.000f;
            const int validationTick = NativeCombatHz;
            const int projectileHitFrameTick = 15;
            const float projectileDistance = 90f;
            const float projectileSpeed = 180f;
            const float poisonDurationSeconds = 4f;
            const float poisonFrequencySeconds = 1f;

            var pending = new List<NativeSchedulerEvent>
            {
                new NativeSchedulerEvent(1, SchedulerSelfTestTargetEntityId, 0.900f, 1280),
                new NativeSchedulerEvent(2, SchedulerSelfTestOtherEntityId, 0.800f, 2048),
                new NativeSchedulerEvent(3, SchedulerSelfTestTargetEntityId, 1.100f, 512),
                new NativeSchedulerEvent(4, SchedulerSelfTestTargetEntityId, 1.000f, 768),
                new NativeSchedulerEvent(5, SchedulerSelfTestOtherEntityId, 1.200f, 1024)
            };
            int[] originalOrder = pending.Select(e => e.Order).ToArray();

            NativeSchedulerDrainReplay firstDrain = DrainDueSchedulerEvents(pending, SchedulerSelfTestTargetEntityId, suffixNow, includeEqualTime: false);
            uint hpAfterFirstDrain = ApplyDamageWire(SchedulerSelfTestStartHPWire, firstDrain.ResolvedDamageWire);

            uint pureRead1 = hpAfterFirstDrain;
            uint pureRead2 = pureRead1;
            int pureReadRuntimeAdvances = 0;
            int pureReadResolvedEvents = 0;

            NativeSchedulerDrainReplay secondDrain = DrainDueSchedulerEvents(pending, SchedulerSelfTestTargetEntityId, suffixNow, includeEqualTime: false);
            uint hpAfterSecondDrain = ApplyDamageWire(hpAfterFirstDrain, secondDrain.ResolvedDamageWire);
            NativeSchedulerDrainReplay entityPhaseDrain = DrainDueSchedulerEvents(pending, SchedulerSelfTestTargetEntityId, suffixNow, includeEqualTime: true);
            uint hpAfterEntityPhase = ApplyDamageWire(hpAfterSecondDrain, entityPhaseDrain.ResolvedDamageWire);

            float projectileFireTime = projectileHitFrameTick / (float)NativeCombatHz;
            int projectileFlightTicks = WeaponCycleTracker.NativeProjectileFlightTicks(projectileDistance, projectileSpeed);
            int projectileImpactDelayTicks = WeaponCycleTracker.NativeProjectileImpactDelayTicks(projectileDistance, projectileSpeed);
            float projectileFlightTime = projectileFlightTicks / (float)NativeCombatHz;
            float projectileImpactDelay = projectileImpactDelayTicks / (float)NativeCombatHz;
            float projectileImpactTime = projectileFireTime + projectileImpactDelay;
            int projectileImpactTick = projectileHitFrameTick + projectileImpactDelayTicks;
            bool projectileResolvesBeforeImpact = IsDue(projectileImpactTime, projectileImpactTime - (1f / (NativeCombatHz * 2f)));
            bool projectileResolvesAtImpact = IsDue(projectileImpactTime, projectileImpactTime);

            float poisonImpactTime = projectileImpactTime;
            float poisonFirstTickTime = poisonImpactTime + poisonFrequencySeconds;
            int poisonTicksAtImpact = CountPoisonTicksDue(poisonImpactTime, poisonFirstTickTime, poisonFrequencySeconds, poisonDurationSeconds);
            int poisonTicksBeforeFirst = CountPoisonTicksDue(poisonFirstTickTime - (1f / (NativeCombatHz * 2f)), poisonFirstTickTime, poisonFrequencySeconds, poisonDurationSeconds);
            int poisonTicksAtFirst = CountPoisonTicksDue(poisonFirstTickTime, poisonFirstTickTime, poisonFrequencySeconds, poisonDurationSeconds);
            var hpSuffixWriter = new LEWriter();
            EntitySynchInfoPayload.FromHP(LatestPup50000Cb024FirstPostHPWire).Write(hpSuffixWriter);
            byte[] hpSuffixPayload = hpSuffixWriter.ToArray();
            var emptySuffixWriter = new LEWriter();
            EntitySynchInfoPayload.Empty.Write(emptySuffixWriter);
            byte[] emptySuffixPayload = emptySuffixWriter.ToArray();
            LatestPup50030NativeBehaviorWriterReplayResult latestPup50030 = RunLatestPup50030NativeBehaviorWriterReplay();

            return new NativeSchedulerOrderReplayResult
            {
                NativeHz = NativeCombatHz,
                TargetEntityId = SchedulerSelfTestTargetEntityId,
                OtherEntityId = SchedulerSelfTestOtherEntityId,
                SuffixNow = suffixNow,
                ValidationCutoffTick = validationTick,
                ValidationCutoffTime = suffixNow,
                OriginalQueueOrder = originalOrder,
                FirstDrainResolvedOrder = firstDrain.ResolvedOrder,
                FirstDrainRemainingOrder = firstDrain.RemainingOrder,
                SecondDrainResolvedOrder = secondDrain.ResolvedOrder,
                SecondDrainRemainingOrder = secondDrain.RemainingOrder,
                EntityPhaseResolvedOrder = entityPhaseDrain.ResolvedOrder,
                EntityPhaseRemainingOrder = entityPhaseDrain.RemainingOrder,
                FirstDrainDamageWire = firstDrain.ResolvedDamageWire,
                SecondDrainDamageWire = secondDrain.ResolvedDamageWire,
                EntityPhaseDamageWire = entityPhaseDrain.ResolvedDamageWire,
                StartHPWire = SchedulerSelfTestStartHPWire,
                HPAfterFirstDrain = hpAfterFirstDrain,
                HPAfterPureRead1 = pureRead1,
                HPAfterPureRead2 = pureRead2,
                HPAfterSecondDrain = hpAfterSecondDrain,
                HPAfterEntityPhase = hpAfterEntityPhase,
                PureReadRuntimeAdvances = pureReadRuntimeAdvances,
                PureReadResolvedEvents = pureReadResolvedEvents,
                ProjectileHitFrameTick = projectileHitFrameTick,
                ProjectileFlightTicks = projectileFlightTicks,
                ProjectileImpactDelayTicks = projectileImpactDelayTicks,
                ProjectileImpactTick = projectileImpactTick,
                ProjectileFireTime = projectileFireTime,
                ProjectileDistance = projectileDistance,
                ProjectileSpeed = projectileSpeed,
                ProjectileFlightTime = projectileFlightTime,
                ProjectileImpactDelay = projectileImpactDelay,
                ProjectileImpactTime = projectileImpactTime,
                ProjectileSamePassFirstUpdate = false,
                ProjectileResolvesBeforeImpact = projectileResolvesBeforeImpact,
                ProjectileResolvesAtImpact = projectileResolvesAtImpact,
                PoisonImpactTime = poisonImpactTime,
                PoisonFrequencySeconds = poisonFrequencySeconds,
                PoisonDurationSeconds = poisonDurationSeconds,
                PoisonFirstTickTime = poisonFirstTickTime,
                PoisonFirstTickDelayTicks = SecondsToNativeTicks(poisonFirstTickTime - poisonImpactTime),
                PoisonMaxTicks = (int)Math.Ceiling(poisonDurationSeconds / poisonFrequencySeconds),
                PoisonTicksAtImpact = poisonTicksAtImpact,
                PoisonTicksBeforeFirstTick = poisonTicksBeforeFirst,
                PoisonTicksAtFirstTick = poisonTicksAtFirst,
                EntitySynchInfoHpPayloadBytes = hpSuffixPayload.Length,
                EntitySynchInfoHpPayloadFlag = hpSuffixPayload.Length > 0 ? hpSuffixPayload[0] : (byte)0,
                EntitySynchInfoHpPayloadValueWire = hpSuffixPayload.Length >= 5 ? BitConverter.ToUInt32(hpSuffixPayload, 1) : 0u,
                EntitySynchInfoEmptyPayloadBytes = emptySuffixPayload.Length,
                HpSyncRuntimeBeforeRegister = 17893u,
                HpSyncStaleDomainHP = 24278u,
                HpSyncAfterRegister = Math.Min(17893u, 24278u),
                LatestPupStaleRemoteHPWire = LatestPup50000StaleRemoteHPWire,
                LatestPupClientLocalHPWire = LatestPup50000ClientLocalHPWire,
                LatestPupSameTickStartHPWire = LatestPup50000SameTickStartHPWire,
                LatestPupSameTickRuntimeHPWire = LatestPup50000SameTickRemoteHPWire,
                LatestPupSameTickDamageWire = LatestPup50000SameTickDamageWire,
                LatestPupSameTickImpactTick = LatestPup50000SameTickImpactTick,
                LatestPupSameTickComponentSuffixHPWire = LatestPup50000SameTickRemoteHPWire,
                LatestPupNextVisibleSuffixHPWire = LatestPup50000SameTickRemoteHPWire,
                LatestPupCb024BadRemoteHPWire = LatestPup50000Cb024BadRemoteHPWire,
                LatestPupCb024ClientLocalHPWire = LatestPup50000Cb024ClientLocalHPWire,
                LatestPupCb024FirstStartHPWire = LatestPup50000Cb024FirstStartHPWire,
                LatestPupCb024FirstDamageWire = LatestPup50000Cb024FirstDamageWire,
                LatestPupCb024PostSubentityMoveSuffixHPWire = LatestPup50000Cb024FirstPostHPWire,
                LatestPupCb024PreSubentitySuffixHPWire = LatestPup50000Cb024FirstStartHPWire,
                LatestPupCb024FirstImpactTick = LatestPup50000Cb024FirstImpactTick,
                LatestPupCb024LateStartHPWire = LatestPup50000Cb024LateStartHPWire,
                LatestPupCb024LateDamageWire = LatestPup50000Cb024LateDamageWire,
                LatestPupCb024LatePostSubentityMoveSuffixHPWire = LatestPup50000Cb024LatePostHPWire,
                LatestPupCb024LatePreSubentitySuffixHPWire = LatestPup50000Cb024LateStartHPWire,
                LatestPupCb024LateImpactTick = LatestPup50000Cb024LateImpactTick,
                LatestWhiskerRatlingCb030StartHPWire = LatestWhiskerRatling50000Cb030StartHPWire,
                LatestWhiskerRatlingCb030DamageWire = LatestWhiskerRatling50000Cb030DamageWire,
                LatestWhiskerRatlingCb030RuntimeHPWire = LatestWhiskerRatling50000Cb030RuntimeHPWire,
                LatestWhiskerRatlingCb030PreEntitySuffixHPWire = LatestWhiskerRatling50000Cb030RuntimeHPWire,
                LatestWhiskerRatlingCb030PostEntitySuffixHPWire = LatestWhiskerRatling50000Cb030RuntimeHPWire,
                LatestWhiskerRatlingCb030ImpactTick = LatestWhiskerRatling50000Cb030ImpactTick,
                LatestPup50030StartHPWire = latestPup50030.StartHPWire,
                LatestPup50030FirstDamageWire = latestPup50030.FirstDamageWire,
                LatestPup50030QueuedMoveSuffixHPWire = latestPup50030.QueuedMoveSuffixHPWire,
                LatestPup50030ClientLocalHPWire = latestPup50030.ClientLocalHPWire,
                LatestPup50030NativeWriterCurrentHPWire = latestPup50030.NativeWriterCurrentHPWire,
                LatestPup50030NativeWriterSuffixHPWire = latestPup50030.NativeWriterSuffixHPWire,
                LatestPup50030PostZeroNonZeroMonsterSuffixEmitted = latestPup50030.PostZeroNonZeroMonsterSuffixEmitted,
                SubentityPhaseRunsBeforeEntityPhase = true,
                ProjectileCreatedDuringEntityUpdatesNextSubentityPhase = true
            };
        }

        public static NativeAuthoredSpellSplitReplayResult RunFireBoltPoisonAuthoredSplitReplay()
        {
            SpellData fireBolt = SpellDatabase.GetSpell("FireBolt");
            SpellData poisonShot = SpellDatabase.GetSpell("PoisonShot");

            return new NativeAuthoredSpellSplitReplayResult
            {
                FireBoltFound = fireBolt != null,
                PoisonShotFound = poisonShot != null,
                FireBoltSkillId = fireBolt?.SkillId,
                PoisonShotSkillId = poisonShot?.SkillId,
                FireBoltAttackType = fireBolt != null ? fireBolt.AttackType : default,
                PoisonShotAttackType = poisonShot != null ? poisonShot.AttackType : default,
                FireBoltDamageType = fireBolt != null ? fireBolt.DamageType : default,
                PoisonShotDamageType = poisonShot != null ? poisonShot.DamageType : default,
                FireBoltRange = fireBolt?.Range ?? 0,
                PoisonShotRange = poisonShot?.Range ?? 0,
                FireBoltCooldown = fireBolt?.Cooldown ?? 0f,
                PoisonShotCooldown = poisonShot?.Cooldown ?? 0f,
                FireBoltProjectileSpeed = fireBolt?.ProjectileSpeed ?? 0f,
                PoisonShotProjectileSpeed = poisonShot?.ProjectileSpeed ?? 0f,
                FireBoltProjectileSize = fireBolt?.ProjectileSize ?? 0f,
                PoisonShotProjectileSize = poisonShot?.ProjectileSize ?? 0f,
                FireBoltProjectileLifespan = fireBolt?.ProjectileLifespan ?? 0f,
                PoisonShotProjectileLifespan = poisonShot?.ProjectileLifespan ?? 0f,
                FireBoltDamageMod = fireBolt?.DamageMod ?? 0f,
                PoisonShotDamageMod = poisonShot?.DamageMod ?? 0f,
                FireBoltDamageVolatility = fireBolt?.DamageVolatility ?? 0f,
                PoisonShotDamageVolatility = poisonShot?.DamageVolatility ?? 0f,
                FireBoltHasDirectDamageEffect = fireBolt?.HasDirectDamageEffect ?? false,
                PoisonShotHasDirectDamageEffect = poisonShot?.HasDirectDamageEffect ?? false,
                PoisonShotHasImmediateWeaponDamageEffect = poisonShot?.HasImmediateWeaponDamageEffect ?? false,
                PoisonShotARModMin = poisonShot?.ARModMin ?? 0,
                PoisonShotARModMax = poisonShot?.ARModMax ?? 0,
                PoisonShotWeaponEffectDamageModMin = poisonShot?.WeaponEffectDamageModMin ?? 0,
                PoisonShotWeaponEffectDamageModMax = poisonShot?.WeaponEffectDamageModMax ?? 0,
                FireBoltHasProjectileModifierDamage = fireBolt?.HasProjectileModifierDamage ?? false,
                PoisonShotHasProjectileModifierDamage = poisonShot?.HasProjectileModifierDamage ?? false,
                FireBoltProjectileEffectId = fireBolt?.ProjectileEffectId,
                PoisonShotProjectileEffectId = poisonShot?.ProjectileEffectId,
                FireBoltProjectileModifierId = fireBolt?.ProjectileModifierId,
                PoisonShotProjectileModifierId = poisonShot?.ProjectileModifierId,
                PoisonShotProjectileModifierEffectId = poisonShot?.ProjectileModifierEffectId,
                PoisonShotProjectileModifierAttackType = poisonShot?.EffectiveProjectileModifierAttackType ?? default,
                PoisonShotProjectileModifierDamageType = poisonShot?.EffectiveProjectileModifierDamageType ?? default,
                PoisonShotProjectileModifierDuration = poisonShot?.ProjectileModifierDuration ?? 0f,
                PoisonShotProjectileModifierFrequency = poisonShot?.ProjectileModifierFrequency ?? 0f,
                PoisonShotProjectileModifierStackRule = poisonShot?.ProjectileModifierStackRule,
                PoisonShotProjectileModifierDamageMod = poisonShot?.ProjectileModifierDamageMod ?? 0f,
                PoisonShotProjectileModifierDamageVolatility = poisonShot?.ProjectileModifierDamageVolatility ?? 0f,
                PoisonShotProjectileModifierCriticalChance = poisonShot?.ProjectileModifierCriticalChance ?? 0f
            };
        }

        private static NativeSchedulerDrainReplay DrainDueSchedulerEvents(List<NativeSchedulerEvent> pending, uint targetEntityId, float now, bool includeEqualTime)
        {
            var resolvedOrder = new List<int>();
            var remaining = new List<NativeSchedulerEvent>();
            uint resolvedDamage = 0;

            foreach (NativeSchedulerEvent pendingEvent in pending.OrderBy(e => e.DueTime).ThenBy(e => e.Order))
            {
                if (includeEqualTime ? IsDue(pendingEvent.DueTime, now) : IsDueBefore(pendingEvent.DueTime, now))
                {
                    resolvedOrder.Add(pendingEvent.Order);
                    if (pendingEvent.TargetEntityId == targetEntityId)
                        resolvedDamage += pendingEvent.DamageWire;
                    continue;
                }
                remaining.Add(pendingEvent);
            }

            pending.Clear();
            pending.AddRange(remaining);

            return new NativeSchedulerDrainReplay
            {
                ResolvedOrder = resolvedOrder.ToArray(),
                RemainingOrder = pending.Select(e => e.Order).ToArray(),
                ResolvedDamageWire = resolvedDamage
            };
        }

        private static bool IsDue(float dueTime, float now)
        {
            return dueTime <= 0f || now + 0.0001f >= dueTime;
        }

        private static bool IsDueBefore(float dueTime, float now)
        {
            return dueTime <= 0f || dueTime + 0.0001f < now;
        }

        private static int SecondsToNativeTicks(float seconds)
        {
            return (int)Math.Ceiling(Math.Max(0f, seconds) * NativeCombatHz - 0.0001f);
        }

        private static int CountPoisonTicksDue(float now, float firstTickTime, float frequencySeconds, float durationSeconds)
        {
            int maxTicks = (int)Math.Ceiling(durationSeconds / frequencySeconds);
            int due = 0;
            float nextTick = firstTickTime;
            for (int tick = 0; tick < maxTicks; tick++)
            {
                if (!IsDue(nextTick, now))
                    break;
                due++;
                nextTick += frequencySeconds;
            }
            return due;
        }

        private static uint ApplyDamageWire(uint hpWire, uint damageWire)
        {
            return damageWire >= hpWire ? 0 : hpWire - damageWire;
        }

        private static NativeWeaponDamageResult FindDamageReplay(AttackResultType type, int attackRating, int defenseRating, int blockChance, int critThreshold)
        {
            for (uint seed = 1; seed < 100000; seed++)
            {
                var input = new NativeWeaponDamageInput
                {
                    Rng = new MersenneTwister(seed),
                    Source = $"rng-order-{type}",
                    AttackerLevel = 3,
                    DefenderLevel = 3,
                    AttackRating = attackRating,
                    DefenseRating = defenseRating,
                    BlockChance = blockChance,
                    DamageLevel = 3,
                    DamageBonus = 0,
                    DamageMod = 100,
                    WeaponDamageF32 = 0x100,
                    WeaponVolatilityF32 = 0x40,
                    CritThreshold = critThreshold,
                    CritDamagePercent = 200
                };
                NativeWeaponDamageResult result = DamageComputer.ResolveNativeWeaponDamage(input);
                if (result.Type == type)
                    return result;
            }

            return new NativeWeaponDamageResult { Type = type, ResultName = "NOT-FOUND" };
        }

        private sealed class NativeSchedulerEvent
        {
            public NativeSchedulerEvent(int order, uint targetEntityId, float dueTime, uint damageWire)
            {
                Order = order;
                TargetEntityId = targetEntityId;
                DueTime = dueTime;
                DamageWire = damageWire;
            }

            public int Order;
            public uint TargetEntityId;
            public float DueTime;
            public uint DamageWire;
        }

        private sealed class NativeSchedulerDrainReplay
        {
            public int[] ResolvedOrder;
            public int[] RemainingOrder;
            public uint ResolvedDamageWire;
        }
    }

    public class NativeDamageReplayResult
    {
        public uint Seed;
        public int PreHitConsumes;
        public uint HitRaw;
        public uint BlockRaw;
        public uint DamageRaw;
        public int HitRoll;
        public int BlockRoll;
        public int HitThreshold;
        public int MinDamageWire;
        public int MaxDamageWire;
        public uint DamageWire;
        public uint ServerLoggedDamageWire;
        public uint NativeObservedDamageWire;
        public uint ReplayHPAfter;
        public uint ServerLoggedHPAfter;
        public uint NativeObservedHPAfter;
        public int RoomRngAfter;

        public bool MatchesServerLoggedReplay =>
            HitRaw == 0x15C81148u &&
            BlockRaw == 0x0802FB68u &&
            DamageRaw == 0x74222EACu &&
            DamageWire == ServerLoggedDamageWire &&
            ReplayHPAfter == ServerLoggedHPAfter &&
            RoomRngAfter == 159;

        public bool MatchesNativeObservedHP => DamageWire == NativeObservedDamageWire && ReplayHPAfter == NativeObservedHPAfter;

        public override string ToString()
        {
            return $"seed=0x{Seed:X8} pre={PreHitConsumes} hit=0x{HitRaw:X8}/{HitRoll} block=0x{BlockRaw:X8}/{BlockRoll} dmgRaw=0x{DamageRaw:X8} threshold={HitThreshold} range=[{MinDamageWire},{MaxDamageWire}] damage={DamageWire} hp={ReplayHPAfter} serverMatch={MatchesServerLoggedReplay} nativeHpMatch={MatchesNativeObservedHP}";
        }
    }

    public class LatestPup50036NativeBaseDamageReplayResult
    {
        public uint Seed;
        public int PreHitConsumes;
        public int RuntimeBaseDamage;
        public bool RuntimeBaseTracksPlayerLevel;
        public string RuntimeBaseSource;
        public int ExplicitStoredBaseDamage;
        public bool ExplicitStoredTracksPlayerLevel;
        public string ExplicitStoredBaseSource;
        public uint HitRaw;
        public uint BlockRaw;
        public uint DamageRaw;
        public int HitRoll;
        public int BlockRoll;
        public int HitThreshold;
        public int MinDamageWire;
        public int MaxDamageWire;
        public uint DamageWire;
        public uint TotalDamageWire;
        public uint BadServerDamageWire;
        public uint StartHPWire;
        public uint ReplayHPAfter;
        public uint BadServerSuffixHPWire;
        public uint ClientLocalHPWire;
        public int RoomRngAfter;
        public bool SuffixWritesAlreadyComputedHP;
        public bool PupHasAuthoredNegativeHPRegen;

        public int RemainingClientDeltaWire => (int)ReplayHPAfter - (int)ClientLocalHPWire;

        public bool RuntimeBaseDamageContractMatchesNativePath =>
            RuntimeBaseDamage == 1 &&
            !RuntimeBaseTracksPlayerLevel &&
            RuntimeBaseSource == "authored-item-level" &&
            ExplicitStoredBaseDamage == 1 &&
            !ExplicitStoredTracksPlayerLevel &&
            ExplicitStoredBaseSource == "materialized-item-level";

        public bool ReplayConsumesLatestServerRngPrefix =>
            Seed == NativeDamageReplaySelfTest.LatestPup50036RuntimeSeed &&
            PreHitConsumes == NativeDamageReplaySelfTest.LatestPup50036PreHitConsumes &&
            HitRaw == NativeDamageReplaySelfTest.LatestPup50036HitRaw &&
            BlockRaw == NativeDamageReplaySelfTest.LatestPup50036BlockRaw &&
            DamageRaw == NativeDamageReplaySelfTest.LatestPup50036DamageRaw &&
            RoomRngAfter == 35;

        public bool FixesBadServerBaseDamageSuffix =>
            DamageWire == NativeDamageReplaySelfTest.LatestPup50036NativeBaseDamageReplayDamageWire &&
            ReplayHPAfter == NativeDamageReplaySelfTest.LatestPup50036NativeBaseDamageReplayHPWire &&
            DamageWire != BadServerDamageWire &&
            ReplayHPAfter != BadServerSuffixHPWire;

        public bool RequiresSecondDeltaRound => ReplayHPAfter != ClientLocalHPWire;

        public bool MatchesNativeBaseDamageClosure =>
            RuntimeBaseDamageContractMatchesNativePath &&
            ReplayConsumesLatestServerRngPrefix &&
            FixesBadServerBaseDamageSuffix &&
            SuffixWritesAlreadyComputedHP &&
            !PupHasAuthoredNegativeHPRegen;

        public override string ToString()
        {
            return $"seed=0x{Seed:X8} pre={PreHitConsumes} base={RuntimeBaseDamage}/{RuntimeBaseSource} storedBase={ExplicitStoredBaseDamage}/{ExplicitStoredBaseSource} raw=0x{DamageRaw:X8} damage={DamageWire} hp={ReplayHPAfter} bad={BadServerSuffixHPWire} client={ClientLocalHPWire} delta={RemainingClientDeltaWire} match={MatchesNativeBaseDamageClosure} secondRound={RequiresSecondDeltaRound}";
        }
    }

    public class NativeWeaponApplyDamageRngOrderReplayResult
    {
        public int MissRngAfter;
        public int BlockRngAfter;
        public int HitRngAfter;
        public int CritRngAfter;
        public bool CritConsumedExtraWeaponDraw;
        public bool BlockCompareIsStrict;
        public bool DamageDrawOnlyForLandedHit;

        public bool MatchesNativeWeaponApplyDamageRngOrder =>
            MissRngAfter == 2 &&
            BlockRngAfter == 2 &&
            HitRngAfter == 3 &&
            CritRngAfter == 3 &&
            !CritConsumedExtraWeaponDraw &&
            BlockCompareIsStrict &&
            DamageDrawOnlyForLandedHit;

        public override string ToString()
        {
            return $"miss={MissRngAfter} block={BlockRngAfter} hit={HitRngAfter} crit={CritRngAfter} critExtra={CritConsumedExtraWeaponDraw} strictBlock={BlockCompareIsStrict} damageDrawOnlyHit={DamageDrawOnlyForLandedHit} match={MatchesNativeWeaponApplyDamageRngOrder}";
        }
    }

    public class NativeWeaponDamageAddsReplayResult
    {
        public int SlotCount;
        public int AddCount;
        public int ShadowDamageTypeId;
        public uint ShadowDamageWire;
        public int FireDamageTypeId;
        public uint FireDamageWire;
        public bool PoisonSkippedWithoutWeaponAdd;
        public int WeaponDamageF32;

        public bool MatchesNativeWeaponDamageAddsContract =>
            SlotCount == 5 &&
            AddCount == 2 &&
            ShadowDamageTypeId == 6 &&
            ShadowDamageWire == 1536 &&
            FireDamageTypeId == 3 &&
            FireDamageWire == 256 &&
            PoisonSkippedWithoutWeaponAdd &&
            WeaponDamageF32 == 0x180;

        public override string ToString()
        {
            return $"slots={SlotCount} adds={AddCount} shadowType={ShadowDamageTypeId} shadow={ShadowDamageWire} fireType={FireDamageTypeId} fire={FireDamageWire} poisonSkipped={PoisonSkippedWithoutWeaponAdd} weaponDamage={WeaponDamageF32} match={MatchesNativeWeaponDamageAddsContract}";
        }
    }

    public class LatestRatling50006ClosureReplayResult
    {
        public uint Seed;
        public uint EntityId;
        public uint BehaviorId;
        public uint ClientVisibleHPWire;
        public uint BadServerSuffixHPWire;
        public uint DeltaWire;
        public int ProjectileTickStart;
        public int ProjectileTickEnd;
        public bool RuntimeInstanceKeyRequired;
        public bool SuffixPayloadShapeIsNative;
        public bool SamePacketProjectileImpactBeforeValidate;

        public bool MatchesLatestCrashContract =>
            Seed == NativeDamageReplaySelfTest.LatestRatling50006RuntimeSeed &&
            EntityId == NativeDamageReplaySelfTest.LatestRatling50006EntityId &&
            BehaviorId == NativeDamageReplaySelfTest.LatestRatling50007BehaviorId &&
            ClientVisibleHPWire == NativeDamageReplaySelfTest.LatestRatling50006ClientHPWire &&
            BadServerSuffixHPWire == NativeDamageReplaySelfTest.LatestRatling50006BadServerSuffixHPWire &&
            DeltaWire == 4487 &&
            RuntimeInstanceKeyRequired &&
            SuffixPayloadShapeIsNative &&
            !SamePacketProjectileImpactBeforeValidate;

        public override string ToString()
        {
            return $"entity={EntityId}/{BehaviorId} seed=0x{Seed:X8} hpClient={ClientVisibleHPWire} hpServerBad={BadServerSuffixHPWire} delta={DeltaWire} ticks={ProjectileTickStart}-{ProjectileTickEnd} instanceRuntime={RuntimeInstanceKeyRequired} samePacketImpactBeforeValidate={SamePacketProjectileImpactBeforeValidate} match={MatchesLatestCrashContract}";
        }
    }

    public class LatestRatling50024SamePacketCutoffReplayResult
    {
        public uint EntityId;
        public uint BehaviorId;
        public uint ClientValidateHPWire;
        public uint BadRemoteHPWire;
        public uint DamageWire;
        public uint RuntimeHPAfterProjectile;
        public uint ComponentSuffixHPWire;
        public uint NextEntityBoundarySuffixHPWire;
        public int ImpactTick;
        public bool SamePacketSubentityDamageBeforeValidate;

        public bool MatchesLatestCrashContract =>
            EntityId == NativeDamageReplaySelfTest.LatestRatling50024SamePacketEntityId &&
            BehaviorId == NativeDamageReplaySelfTest.LatestRatling50025SamePacketBehaviorId &&
            ClientValidateHPWire == NativeDamageReplaySelfTest.LatestRatling50024SamePacketStartHPWire &&
            BadRemoteHPWire == NativeDamageReplaySelfTest.LatestRatling50024SamePacketBadRemoteHPWire &&
            DamageWire == NativeDamageReplaySelfTest.LatestRatling50024SamePacketDamageWire &&
            RuntimeHPAfterProjectile == BadRemoteHPWire &&
            ComponentSuffixHPWire == ClientValidateHPWire &&
            NextEntityBoundarySuffixHPWire == RuntimeHPAfterProjectile &&
            ImpactTick == NativeDamageReplaySelfTest.LatestRatling50024SamePacketImpactTick &&
            !SamePacketSubentityDamageBeforeValidate;

        public override string ToString()
        {
            return $"entity={EntityId}/{BehaviorId} clientHP={ClientValidateHPWire} badRemote={BadRemoteHPWire} damage={DamageWire} runtimeAfter={RuntimeHPAfterProjectile} componentSuffix={ComponentSuffixHPWire} nextBoundary={NextEntityBoundarySuffixHPWire} impactTick={ImpactTick} samePacketDamageBeforeValidate={SamePacketSubentityDamageBeforeValidate} match={MatchesLatestCrashContract}";
        }
    }

    public class LatestAvatar1510HeroRegenReplayResult
    {
        public uint EntityId;
        public uint SavedHPWire;
        public uint ClientLocalHPWire;
        public uint BadRemoteHPWire;
        public uint MaxHPWire;
        public int AuthoredHealthRegenFactor;
        public int BadGlobalOnlyHealthRegenFactor;
        public int NativeFloorDeltaWire;
        public int BadGlobalOnlyDeltaWire;
        public int ObservedNativeRegenTicks;
        public uint ReplayHPWire;
        public uint BadGlobalOnlyHPWire;

        public bool MatchesLatestCrashContract =>
            EntityId == NativeDamageReplaySelfTest.LatestAvatar1510EntityId &&
            SavedHPWire == NativeDamageReplaySelfTest.LatestAvatar1510SavedHPWire &&
            ClientLocalHPWire == NativeDamageReplaySelfTest.LatestAvatar1510ClientLocalHPWire &&
            BadRemoteHPWire == NativeDamageReplaySelfTest.LatestAvatar1510BadRemoteHPWire &&
            MaxHPWire == NativeDamageReplaySelfTest.LatestAvatar1510MaxHPWire &&
            AuthoredHealthRegenFactor == 0 &&
            NativeFloorDeltaWire == 1 &&
            ReplayHPWire == ClientLocalHPWire &&
            BadGlobalOnlyHPWire == BadRemoteHPWire;

        public override string ToString()
        {
            return $"entity={EntityId} saved={SavedHPWire} client={ClientLocalHPWire} badRemote={BadRemoteHPWire} max={MaxHPWire} authoredRegen={AuthoredHealthRegenFactor} floorDelta={NativeFloorDeltaWire} ticks={ObservedNativeRegenTicks} replay={ReplayHPWire} badGlobalRegen={BadGlobalOnlyHealthRegenFactor}/{BadGlobalOnlyDeltaWire}->{BadGlobalOnlyHPWire} match={MatchesLatestCrashContract}";
        }
    }

    public class NativeRoomRuntimeLateJoinReplayResult
    {
        public string InstanceKey;
        public uint Seed;
        public bool SameSeedInitialized;
        public bool DifferentSeedInitialized;
        public int RngPosBeforeLateJoin;
        public int RngPosAfterSameSeedLateJoin;
        public int RngPosAfterDifferentSeedLateJoin;

        public bool PreservesExistingRuntime =>
            Seed == NativeDamageReplaySelfTest.LatestRatling50006RuntimeSeed &&
            !SameSeedInitialized &&
            !DifferentSeedInitialized &&
            RngPosBeforeLateJoin == 2 &&
            RngPosAfterSameSeedLateJoin == RngPosBeforeLateJoin &&
            RngPosAfterDifferentSeedLateJoin == RngPosBeforeLateJoin;

        public override string ToString()
        {
            return $"instance='{InstanceKey}' seed=0x{Seed:X8} pos={RngPosBeforeLateJoin}->{RngPosAfterSameSeedLateJoin}->{RngPosAfterDifferentSeedLateJoin} sameInit={SameSeedInitialized} diffInit={DifferentSeedInitialized} preserve={PreservesExistingRuntime}";
        }
    }

    public class NativeRangedSeed2848ReplayResult
    {
        public uint Seed;
        public int PreHitConsumes;
        public uint[] DamageWire;
        public uint[] HPAfter;
        public int[] RoomRngAfter;
        public uint[] HitRaw;
        public uint[] BlockRaw;
        public uint[] DamageRaw;
        public bool[] Critical;
        public bool GlobalAttackSoundRngSeparate;

        public bool RoomRngAdvancesThreePerProjectileImpact =>
            RoomRngAfter != null &&
            RoomRngAfter.SequenceEqual(new[] { 28, 31, 34, 37 });

        public bool FirstFourDamageStepsMatchLatestRuntime =>
            Seed == NativeDamageReplaySelfTest.LatestPup50000Cb024Seed &&
            PreHitConsumes == NativeDamageReplaySelfTest.LatestPup50000Cb024PreHitConsumes &&
            DamageWire != null && DamageWire.SequenceEqual(new uint[] { 3466u, 9446u, 6141u, 5466u }) &&
            HPAfter != null && HPAfter.SequenceEqual(new uint[] { 25718u, 16272u, 10131u, 4665u }) &&
            HitRaw != null && HitRaw.SequenceEqual(new uint[] { 0x4AEB931Fu, 0x8A723AF2u, 0xB6B165D5u, 0xA47A95C1u }) &&
            BlockRaw != null && BlockRaw.SequenceEqual(new uint[] { 0x63C950CBu, 0xDA672DD9u, 0xC2D6B072u, 0xDE678610u }) &&
            DamageRaw != null && DamageRaw.SequenceEqual(new uint[] { 0x93615DEDu, 0xB1215B81u, 0xF86448ECu, 0xBD6E8C94u }) &&
            Critical != null && Critical.SequenceEqual(new[] { false, true, false, false });

        public bool MatchesNativeRangedSeedContract =>
            GlobalAttackSoundRngSeparate &&
            RoomRngAdvancesThreePerProjectileImpact &&
            FirstFourDamageStepsMatchLatestRuntime;

        public override string ToString()
        {
            return $"seed=0x{Seed:X8} pre={PreHitConsumes} damage=[{Join(DamageWire)}] hp=[{Join(HPAfter)}] rngAfter=[{Join(RoomRngAfter)}] soundSeparate={GlobalAttackSoundRngSeparate} match={MatchesNativeRangedSeedContract}";
        }

        private static string Join<T>(T[] values)
        {
            return values == null ? "" : string.Join(",", values);
        }
    }

    public class NativeCombatClockReplayResult
    {
        public uint Seed;
        public int NativeHz;
        public int NativeTicksObserved;
        public int ActorAdvances;
        public int SuffixFlushAttempts;
        public int SuffixAdvancedTicks;
        public int PoisonDurationTicks;
        public int PoisonFrequencyTicks;
        public int PoisonMaxTicks;
        public int RangedRepeatQueueCount;
        public int RangedHeldContinuationCount;
        public int RangedImmediateProjectileFromRedundantUse;
        public int RangedRepeatResolvedFromDueTick;

        public bool OneActorAdvancePerNativeTick => ActorAdvances == NativeTicksObserved;
        public bool SuffixFlushDoesNotAdvanceCombat => SuffixAdvancedTicks == 0 && SuffixFlushAttempts > 0;
        public bool PoisonCadenceMatchesAuthored => NativeHz == 30 && PoisonDurationTicks == 120 && PoisonFrequencyTicks == 30 && PoisonMaxTicks == 4;
        public bool RangedRedundantUseIsDiscarded =>
            RangedRepeatQueueCount == 0 &&
            RangedHeldContinuationCount == 0 &&
            RangedImmediateProjectileFromRedundantUse == 0 &&
            RangedRepeatResolvedFromDueTick == 0;
        public bool RangedRedundantUseDoesNotSpawnImmediateProjectile =>
            RangedRepeatQueueCount == 0 &&
            RangedImmediateProjectileFromRedundantUse == 0 &&
            RangedRepeatResolvedFromDueTick == 0;
        public bool RangedRedundantUseCoalescesHeldContinuation =>
            RangedHeldContinuationCount > 0 &&
            RangedRedundantUseDoesNotSpawnImmediateProjectile;
        public bool RangedProjectileRepeatIsDueScheduled => RangedRedundantUseCoalescesHeldContinuation;
        public bool MatchesNativeClockContract => OneActorAdvancePerNativeTick && SuffixFlushDoesNotAdvanceCombat && PoisonCadenceMatchesAuthored && RangedRedundantUseCoalescesHeldContinuation;

        public override string ToString()
        {
            return $"seed=0x{Seed:X8} hz={NativeHz} ticks={NativeTicksObserved} actorAdvances={ActorAdvances} suffixFlushes={SuffixFlushAttempts} suffixAdvanced={SuffixAdvancedTicks} poisonFrequencyTicks={PoisonFrequencyTicks} poisonDurationTicks={PoisonDurationTicks} poisonMaxTicks={PoisonMaxTicks} repeatQueued={RangedRepeatQueueCount} heldContinuations={RangedHeldContinuationCount} immediateRepeatProjectiles={RangedImmediateProjectileFromRedundantUse} repeatDueResolved={RangedRepeatResolvedFromDueTick} clockMatch={MatchesNativeClockContract}";
        }
    }

    public class LatestPup50018HeldRangedContinuationReplayResult
    {
        public uint Seed;
        public uint EntityId;
        public uint BehaviorId;
        public uint StartHPWire;
        public uint BadRemoteHPWire;
        public uint ClientCrashHPWire;
        public uint FirstDamageWire;
        public uint SecondDamageWire;
        public uint ThirdDamageWire;
        public uint HPAfterFirstProjectile;
        public uint HPAfterSecondProjectile;
        public uint HPAfterThirdProjectile;
        public int FirstProjectileDueTick;
        public int SecondProjectileDueTick;
        public int StaleSuffixCutoffTick;
        public int RedundantRangedUseImmediateProjectiles;
        public int RedundantRangedHeldContinuations;

        public bool FirstTwoServerDamageStepsMatchLog =>
            HPAfterFirstProjectile == BadRemoteHPWire &&
            HPAfterSecondProjectile == StartHPWire - FirstDamageWire - SecondDamageWire &&
            FirstProjectileDueTick == NativeDamageReplaySelfTest.LatestPup50018FirstProjectileDueTick &&
            SecondProjectileDueTick == NativeDamageReplaySelfTest.LatestPup50018SecondProjectileDueTick;

        public bool ClientCrashRequiresThirdProjectile =>
            HPAfterThirdProjectile == ClientCrashHPWire &&
            HPAfterSecondProjectile - ClientCrashHPWire == ThirdDamageWire;

        public bool StaleSuffixWouldCrash =>
            StaleSuffixCutoffTick == NativeDamageReplaySelfTest.LatestPup50018StaleSuffixCutoffTick &&
            BadRemoteHPWire != ClientCrashHPWire;

        public bool RedundantRangedUseCoalescesHeldContinuation =>
            RedundantRangedUseImmediateProjectiles == 0 &&
            RedundantRangedHeldContinuations > 0;

        public bool MatchesLatestCrashContract =>
            Seed == NativeDamageReplaySelfTest.LatestPup50018HeldSeed &&
            EntityId == NativeDamageReplaySelfTest.LatestPup50018EntityId &&
            BehaviorId == NativeDamageReplaySelfTest.LatestPup50019BehaviorId &&
            FirstTwoServerDamageStepsMatchLog &&
            ClientCrashRequiresThirdProjectile &&
            StaleSuffixWouldCrash &&
            RedundantRangedUseCoalescesHeldContinuation;

        public override string ToString()
        {
            return $"entity={EntityId}/{BehaviorId} seed=0x{Seed:X8} hp={StartHPWire}->{HPAfterFirstProjectile}->{HPAfterSecondProjectile}->{HPAfterThirdProjectile} remote={BadRemoteHPWire} client={ClientCrashHPWire} damage={FirstDamageWire}+{SecondDamageWire}+{ThirdDamageWire} due={FirstProjectileDueTick}/{SecondProjectileDueTick} cutoff={StaleSuffixCutoffTick} held={RedundantRangedHeldContinuations} immediate={RedundantRangedUseImmediateProjectiles} match={MatchesLatestCrashContract}";
        }
    }

    public class LatestPup50030NativeBehaviorWriterReplayResult
    {
        public uint EntityId;
        public uint BehaviorId;
        public uint StartHPWire;
        public uint FirstDamageWire;
        public uint HPAfterFirstProjectile;
        public uint QueuedMoveSuffixHPWire;
        public uint LaterDamageToZeroWire;
        public uint ClientLocalHPWire;
        public uint NativeWriterCurrentHPWire;
        public byte NativeWriterSuffixFlag;
        public uint NativeWriterSuffixHPWire;
        public bool PostZeroNonZeroMonsterSuffixEmitted;

        public bool FirstRealHitMatchesRetest =>
            StartHPWire == NativeDamageReplaySelfTest.LatestPup50030StartHPWire &&
            FirstDamageWire == NativeDamageReplaySelfTest.LatestPup50030FirstDamageWire &&
            HPAfterFirstProjectile == NativeDamageReplaySelfTest.LatestPup50030QueuedMoveSuffixHPWire;

        public bool NativeWriterUsesCurrentHP =>
            QueuedMoveSuffixHPWire == NativeDamageReplaySelfTest.LatestPup50030QueuedMoveSuffixHPWire &&
            ClientLocalHPWire == NativeDamageReplaySelfTest.LatestPup50030ClientLocalHPWire &&
            NativeWriterCurrentHPWire == ClientLocalHPWire &&
            NativeWriterSuffixFlag == 0x02 &&
            NativeWriterSuffixHPWire == ClientLocalHPWire &&
            !PostZeroNonZeroMonsterSuffixEmitted;

        public bool MatchesLatestStaleMoveClosure =>
            EntityId == NativeDamageReplaySelfTest.LatestPup50030EntityId &&
            BehaviorId == NativeDamageReplaySelfTest.LatestPup50031BehaviorId &&
            FirstRealHitMatchesRetest &&
            NativeWriterUsesCurrentHP;

        public override string ToString()
        {
            return $"entity={EntityId}/{BehaviorId} hp={StartHPWire}->{HPAfterFirstProjectile}->{NativeWriterCurrentHPWire} staleMove={QueuedMoveSuffixHPWire} client={ClientLocalHPWire} suffix=0x{NativeWriterSuffixFlag:X2}:{NativeWriterSuffixHPWire} postZeroNonZero={PostZeroNonZeroMonsterSuffixEmitted} match={MatchesLatestStaleMoveClosure}";
        }
    }

    public class LatestPoisonShotHeldLeftCrashReplayResult
    {
        public uint StartHPWire;
        public uint RemoteSuffixHPWire;
        public uint ClientCrashHPWire;
        public uint FirstWeaponDamageWire;
        public uint SecondWeaponDamageWire;
        public uint HPAfterFirstWeaponDamage;
        public uint HPAfterSecondWeaponDamage;
        public int PoisonImpactCountBeforeSuffix;
        public int PoisonDotTicksBeforeSuffix;
        public int RedundantRangedUseImmediateProjectiles;
        public int RedundantRangedHeldContinuations;

        public bool CrashHpArithmeticMatchesTwoWeaponHits =>
            HPAfterSecondWeaponDamage == ClientCrashHPWire &&
            StartHPWire - ClientCrashHPWire == FirstWeaponDamageWire + SecondWeaponDamageWire;

        public bool PoisonDotDeferredBeforeSuffix => PoisonDotTicksBeforeSuffix == 0;
        public bool RedundantRangedUseDoesNotSpawnImmediateProjectile => RedundantRangedUseImmediateProjectiles == 0;
        public bool RedundantRangedUseCoalescesHeldContinuation =>
            RedundantRangedHeldContinuations > 0 &&
            RedundantRangedUseDoesNotSpawnImmediateProjectile;
        public bool StaleSuffixWouldCrash => RemoteSuffixHPWire != ClientCrashHPWire;
        public bool MatchesLatestCrashContract =>
            CrashHpArithmeticMatchesTwoWeaponHits &&
            PoisonDotDeferredBeforeSuffix &&
            RedundantRangedUseCoalescesHeldContinuation &&
            StaleSuffixWouldCrash;

        public override string ToString()
        {
            return $"hp={StartHPWire}->{HPAfterFirstWeaponDamage}->{HPAfterSecondWeaponDamage} remote={RemoteSuffixHPWire} client={ClientCrashHPWire} damage={FirstWeaponDamageWire}+{SecondWeaponDamageWire} poisonImpacts={PoisonImpactCountBeforeSuffix} poisonDotTicks={PoisonDotTicksBeforeSuffix} heldContinuations={RedundantRangedHeldContinuations} immediateRepeatProjectiles={RedundantRangedUseImmediateProjectiles} match={MatchesLatestCrashContract}";
        }
    }

    public class LatestWhiskerRatlingFirstProjectileReplayResult
    {
        public uint StartHPWire;
        public uint ExpectedClientHPWire;
        public uint NativeFirstDamageWire;
        public uint NativeHPAfterFirstProjectile;
        public uint BadServerFirstDamageWire;
        public uint BadServerRemoteHPWire;
        public uint BadServerHPAfterFirstProjectile;
        public int RedundantRangedUseImmediateProjectiles;
        public int RedundantRangedHeldContinuations;
        public bool SuffixReadsAlreadyAppliedHP;

        public bool NativeFirstProjectileMatchesClientHP =>
            NativeHPAfterFirstProjectile == ExpectedClientHPWire &&
            StartHPWire - ExpectedClientHPWire == NativeFirstDamageWire;

        public bool BadServerReplayMatchesLatestCrash =>
            BadServerHPAfterFirstProjectile == BadServerRemoteHPWire &&
            BadServerRemoteHPWire != ExpectedClientHPWire;

        public bool RedundantRangedUseDiscarded =>
            RedundantRangedUseImmediateProjectiles == 0 &&
            RedundantRangedHeldContinuations == 0;

        public bool MatchesLatestWhiskerFirstProjectileContract =>
            NativeFirstProjectileMatchesClientHP &&
            BadServerReplayMatchesLatestCrash &&
            RedundantRangedUseDiscarded &&
            SuffixReadsAlreadyAppliedHP;

        public override string ToString()
        {
            return $"hp={StartHPWire}->{NativeHPAfterFirstProjectile} expected={ExpectedClientHPWire} nativeDamage={NativeFirstDamageWire} bad={BadServerFirstDamageWire}->{BadServerRemoteHPWire} held={RedundantRangedHeldContinuations} immediate={RedundantRangedUseImmediateProjectiles} suffixReadOnly={SuffixReadsAlreadyAppliedHP} match={MatchesLatestWhiskerFirstProjectileContract}";
        }
    }

    public class LatestPup50024Cb037UseTargetTimingReplayResult
    {
        public uint Seed;
        public uint EntityId;
        public uint BehaviorId;
        public ushort UseTargetComponentId;
        public int UseTargetFrame;
        public int BadSuffixFrame;
        public int PreInitUseRngPos;
        public uint HitRaw;
        public uint BlockRaw;
        public uint DamageRaw;
        public int RngAfterImpact;
        public uint StartHPWire;
        public uint PreVisibleHitHPWire;
        public uint DamageWire;
        public uint BadPrematureSuffixHPWire;
        public uint PostImpactHPWire;
        public float Distance;
        public float InitUseRange;
        public float AuthoredWeaponRange;
        public float ProjectileSize;
        public float ProjectileNarrowRadius;
        public int ProjectileFirstStep;
        public int ProjectileLifetimeTicks;
        public bool ProjectileCreatedBeforeInitUse;
        public bool DamageRngAdvancedBeforeInitUse;
        public bool SameTickCreateDamageResolved;
        public bool PacketDelayOrSuppressionUsed;

        public bool RngVectorMatchesLatestCrash =>
            Seed == NativeDamageReplaySelfTest.LatestPup50024Cb037Seed &&
            PreInitUseRngPos == NativeDamageReplaySelfTest.LatestPup50024Cb037PreDamageRngPos &&
            HitRaw == NativeDamageReplaySelfTest.LatestPup50024Cb037HitRaw &&
            BlockRaw == NativeDamageReplaySelfTest.LatestPup50024Cb037BlockRaw &&
            DamageRaw == NativeDamageReplaySelfTest.LatestPup50024Cb037DamageRaw &&
            RngAfterImpact == NativeDamageReplaySelfTest.LatestPup50024Cb037RngAfter;

        public bool PreVisibleHitHPStaysFull =>
            PreVisibleHitHPWire == StartHPWire &&
            PreVisibleHitHPWire == NativeDamageReplaySelfTest.LatestPup50024Cb037ExpectedPreVisibleHitHPWire &&
            BadPrematureSuffixHPWire == NativeDamageReplaySelfTest.LatestPup50024Cb037BadSuffixHPWire &&
            BadPrematureSuffixHPWire != PreVisibleHitHPWire;

        public bool ProjectilePhaseMatchesNative =>
            !ProjectileCreatedBeforeInitUse &&
            !DamageRngAdvancedBeforeInitUse &&
            !SameTickCreateDamageResolved &&
            ProjectileNarrowRadius == WeaponCycleTracker.NativeProjectileCollisionRadius(NativeDamageReplaySelfTest.LatestPup50024Cb037PupRadius, NativeDamageReplaySelfTest.LatestPup50024Cb037ProjectileSize) &&
            ProjectileFirstStep == NativeDamageReplaySelfTest.LatestPup50024Cb037ProjectileFirstStep &&
            ProjectileLifetimeTicks == NativeDamageReplaySelfTest.LatestPup50024Cb037ProjectileLifetimeTicks;

        public bool MatchesCb037TimingContract =>
            EntityId == NativeDamageReplaySelfTest.LatestPup50024Cb037EntityId &&
            BehaviorId == NativeDamageReplaySelfTest.LatestPup50025Cb037BehaviorId &&
            UseTargetComponentId == NativeDamageReplaySelfTest.LatestPup50024Cb037UseTargetComponentId &&
            UseTargetFrame == NativeDamageReplaySelfTest.LatestPup50024Cb037UseTargetFrame &&
            BadSuffixFrame == NativeDamageReplaySelfTest.LatestPup50024Cb037BadSuffixFrame &&
            Distance < InitUseRange &&
            RngVectorMatchesLatestCrash &&
            PreVisibleHitHPStaysFull &&
            ProjectilePhaseMatchesNative &&
            !PacketDelayOrSuppressionUsed;

        public override string ToString()
        {
            return $"entity={EntityId}/{BehaviorId} component={UseTargetComponentId} frame={UseTargetFrame}->{BadSuffixFrame} seed=0x{Seed:X8} rng={PreInitUseRngPos}->{RngAfterImpact} raw=0x{HitRaw:X8}/0x{BlockRaw:X8}/0x{DamageRaw:X8} hpPre={PreVisibleHitHPWire} bad={BadPrematureSuffixHPWire} postImpact={PostImpactHPWire} dist={Distance:F1} initRange={InitUseRange:F1} weaponRange={AuthoredWeaponRange:F1} radius={ProjectileNarrowRadius:F1} firstStep={ProjectileFirstStep} lifetime={ProjectileLifetimeTicks} noPacketWorkaround={!PacketDelayOrSuppressionUsed} match={MatchesCb037TimingContract}";
        }
    }

    public class LatestLevelUpPreserveReplayResult
    {
        public uint OldHPWire;
        public uint NewMaxHPWire;
        public uint PreservedHPWire;
        public uint OldManaWire;
        public uint OldMaxManaWire;
        public uint NewMaxManaWire;
        public uint PreservedManaWire;

        public bool HPCurrentPreserved =>
            OldHPWire == NativeDamageReplaySelfTest.LatestLevelUpPreserveStartHPWire &&
            NewMaxHPWire == NativeDamageReplaySelfTest.LatestLevelUpPreserveNewMaxHPWire &&
            PreservedHPWire == OldHPWire;

        public bool ManaCurrentPreserved =>
            OldManaWire == NativeDamageReplaySelfTest.LatestLevelUpPreserveManaSentinelWire &&
            OldMaxManaWire == NativeDamageReplaySelfTest.LatestLevelUpPreserveOldMaxManaWire &&
            NewMaxManaWire == NativeDamageReplaySelfTest.LatestLevelUpPreserveNewMaxManaWire &&
            PreservedManaWire == OldManaWire;

        public bool MatchesLevelUpPreserveContract => HPCurrentPreserved && ManaCurrentPreserved;

        public override string ToString()
        {
            return $"hp={OldHPWire}->{PreservedHPWire}/{NewMaxHPWire} mana={OldManaWire}->{PreservedManaWire}/{OldMaxManaWire}->{NewMaxManaWire} match={MatchesLevelUpPreserveContract}";
        }
    }

    public class LatestRatling50024FirstHitDamageGapReplayResult
    {
        public uint Seed;
        public uint EntityId;
        public uint BehaviorId;
        public int Swing;
        public int PreHitConsumes;
        public uint HitRaw;
        public uint BlockRaw;
        public uint DamageRaw;
        public int RoomRngAfter;
        public int WeaponClassId;
        public int DamageTypeId;
        public int DamageBonus;
        public int DamageMod;
        public int MinDamageWire;
        public int MaxDamageWire;
        public uint PrimaryDamageWire;
        public int AddCount;
        public uint TotalDamageWire;
        public uint StartHPWire;
        public uint PrimaryHPAfter;
        public uint BadFirstMoveSuffixHPWire;
        public uint ExpectedTotalDamageWire;
        public uint ExpectedPostImpactHPWire;
        public uint MissingSameImpactWire;
        public bool NativeApplyBoundaryIsSingle;
        public bool PacketDelayOrSuppressionUsed;

        public bool RngVectorMatchesLatestCrash =>
            Seed == NativeDamageReplaySelfTest.LatestRatling50024FirstHitSeed &&
            Swing == NativeDamageReplaySelfTest.LatestRatling50024FirstHitSwing &&
            PreHitConsumes == NativeDamageReplaySelfTest.LatestRatling50024FirstHitPreConsumes &&
            HitRaw == NativeDamageReplaySelfTest.LatestRatling50024FirstHitRaw &&
            BlockRaw == NativeDamageReplaySelfTest.LatestRatling50024FirstBlockRaw &&
            DamageRaw == NativeDamageReplaySelfTest.LatestRatling50024FirstDamageRaw &&
            RoomRngAfter == NativeDamageReplaySelfTest.LatestRatling50024FirstHitRngAfter;

        public bool ServerPrimaryReplayMatchesLatestCrash =>
            PrimaryDamageWire == NativeDamageReplaySelfTest.LatestRatling50024FirstHitServerPrimaryWire &&
            PrimaryHPAfter == NativeDamageReplaySelfTest.LatestRatling50024FirstHitBadSuffixHPWire &&
            AddCount == 0;

        public bool FirstMoveSuffixIsKnownBad =>
            BadFirstMoveSuffixHPWire == NativeDamageReplaySelfTest.LatestRatling50024FirstHitBadSuffixHPWire &&
            BadFirstMoveSuffixHPWire != ExpectedPostImpactHPWire;

        public bool RatlingSameImpactGapRecorded =>
            ExpectedTotalDamageWire == NativeDamageReplaySelfTest.LatestRatling50024FirstHitExpectedTotalWire &&
            ExpectedPostImpactHPWire == NativeDamageReplaySelfTest.LatestRatling50024FirstHitExpectedHPWire &&
            MissingSameImpactWire == 914 &&
            NativeApplyBoundaryIsSingle &&
            !PacketDelayOrSuppressionUsed;

        public bool MatchesPartialEvidenceVector =>
            RngVectorMatchesLatestCrash &&
            ServerPrimaryReplayMatchesLatestCrash &&
            FirstMoveSuffixIsKnownBad &&
            RatlingSameImpactGapRecorded;

        public override string ToString()
        {
            return $"entity={EntityId}/{BehaviorId} seed=0x{Seed:X8} swing={Swing} pre={PreHitConsumes} raw=0x{HitRaw:X8}/0x{BlockRaw:X8}/0x{DamageRaw:X8} rngAfter={RoomRngAfter} class={WeaponClassId} type={DamageTypeId} range=[{MinDamageWire},{MaxDamageWire}] primary={PrimaryDamageWire} total={TotalDamageWire} addCount={AddCount} hp={StartHPWire}->{PrimaryHPAfter} expected={ExpectedPostImpactHPWire} missing={MissingSameImpactWire} badSuffix={BadFirstMoveSuffixHPWire} noPacketWorkaround={!PacketDelayOrSuppressionUsed} partialMatch={MatchesPartialEvidenceVector}";
        }
    }

    public class NativeMalformedAggroPacketReplayResult
    {
        public byte BeginStream;
        public byte Opcode;
        public ushort ComponentId;
        public byte ComponentUpdateSubtype;
        public int UnitBehaviorPayloadOffset;
        public int UnitBehaviorPayloadBytes;
        public byte UnitBehaviorPayloadByte;
        public int EntitySynchInfoOffset;
        public byte RemoteFlags;
        public bool RemoteReadsHP;
        public uint RemoteHPWire;
        public ushort TargetEntityId;
        public int DeferredHPFlagOffset;
        public byte DeferredHPFlag;
        public uint DeferredHPWire;
        public byte EndStream;
        public bool AggroPathShouldEmitPacket;
        public byte MonsterMoveSubtype;
        public byte MonsterAttackSubtype;
        public byte MonsterMoveSuffixFlag;
        public uint MonsterMoveSuffixHPWire;
        public byte MonsterAttackSuffixFlag;
        public uint MonsterAttackSuffixHPWire;

        public bool UnitBehavior64ConsumesOneByte =>
            ComponentUpdateSubtype == 0x64 &&
            UnitBehaviorPayloadBytes == 1 &&
            UnitBehaviorPayloadByte == 0x03 &&
            EntitySynchInfoOffset == UnitBehaviorPayloadOffset + 1;

        public bool MalformedAggroWouldCrash =>
            BeginStream == 0x07 &&
            Opcode == 0x35 &&
            ComponentId == NativeDamageReplaySelfTest.LatestPup50006AggroBehaviorComponentId &&
            TargetEntityId == NativeDamageReplaySelfTest.LatestPup50006AggroTargetEntityId &&
            RemoteFlags == NativeDamageReplaySelfTest.LatestPup50006MalformedAggroRemoteFlags &&
            !RemoteReadsHP &&
            RemoteHPWire == 0 &&
            DeferredHPFlag == 0x02 &&
            DeferredHPWire == NativeDamageReplaySelfTest.LatestPup50006AggroHPWire &&
            EndStream == 0x06;

        public bool NativeAggroHasNoStandalonePacket => !AggroPathShouldEmitPacket;

        public bool NativeActionLanesRemainHpSuffixed =>
            MonsterMoveSubtype == 0x04 &&
            MonsterAttackSubtype == 0x04 &&
            MonsterMoveSuffixFlag == 0x02 &&
            MonsterAttackSuffixFlag == 0x02 &&
            MonsterMoveSuffixHPWire == NativeDamageReplaySelfTest.LatestPup50006AggroHPWire &&
            MonsterAttackSuffixHPWire == NativeDamageReplaySelfTest.LatestPup50006AggroHPWire;

        public bool MatchesNativeAggroPacketShapeContract =>
            UnitBehavior64ConsumesOneByte &&
            MalformedAggroWouldCrash &&
            NativeAggroHasNoStandalonePacket &&
            NativeActionLanesRemainHpSuffixed;

        public override string ToString()
        {
            return $"component={ComponentId} sub=0x{ComponentUpdateSubtype:X2} unitPayload={UnitBehaviorPayloadByte}@{UnitBehaviorPayloadOffset} esiFlags=0x{RemoteFlags:X2}@{EntitySynchInfoOffset} remoteHp={RemoteHPWire} deferredHp=0x{DeferredHPFlag:X2}:{DeferredHPWire}@{DeferredHPFlagOffset} target={TargetEntityId} aggroPacket={AggroPathShouldEmitPacket} moveHp={MonsterMoveSuffixHPWire} attackHp={MonsterAttackSuffixHPWire} match={MatchesNativeAggroPacketShapeContract}";
        }
    }

    public class NativeStarterCrossbowCycleReplayResult
    {
        public int Selector;
        public int AnimationId;
        public int NumFrames;
        public int TriggerTime;
        public int SoundTriggerTime;
        public float WeaponSpeed;
        public int CycleTicks;
        public int ProjectileEventTick;
        public int SoundEventTick;
        public int UseCooldownTicks;
        public bool UseProjectileCreatesProjectileOnly;

        public bool MatchesNativeStarterCrossbowCycle =>
            Selector == 0 &&
            AnimationId == 310 &&
            NumFrames == 17 &&
            TriggerTime == 2 &&
            SoundTriggerTime == 2 &&
            Near(WeaponSpeed, 95f) &&
            CycleTicks == 17 &&
            ProjectileEventTick == 3 &&
            SoundEventTick == 3 &&
            UseCooldownTicks == 0 &&
            UseProjectileCreatesProjectileOnly;

        public override string ToString()
        {
            return $"selector={Selector} anim={AnimationId} frames={NumFrames} trigger={TriggerTime}->{ProjectileEventTick} sound={SoundTriggerTime}->{SoundEventTick} speed={WeaponSpeed:F1} cycleTicks={CycleTicks} useCooldownTicks={UseCooldownTicks} useProjectileCreatesOnly={UseProjectileCreatesProjectileOnly} match={MatchesNativeStarterCrossbowCycle}";
        }

        private static bool Near(float actual, float expected)
        {
            return Math.Abs(actual - expected) <= 0.0001f;
        }
    }

    public class NativeProjectilePrestepReplayResult
    {
        public float CrossbowSpeed;
        public float CrossbowStepDistance;
        public float CrossbowInitialDistance;
        public float CrossbowTargetCollisionRadius;
        public float CrossbowProjectileSize;
        public float CrossbowHitRadius;
        public float PoisonSpeed;
        public float PoisonStepDistance;
        public float PoisonInitialDistance;
        public float PoisonProjectileSize;
        public float PoisonHitRadius;
        public float PoisonAimDistance;
        public float PoisonProjectedDistance;
        public float PoisonMaxDistance;
        public float PoisonEntryDistance;

        public bool CrossbowCollisionMatchesNative =>
            Near(CrossbowStepDistance, 6f) &&
            Near(CrossbowInitialDistance, 6f) &&
            Near(CrossbowHitRadius, 15f);

        public bool PoisonShotSweptHitMatchesNative =>
            Near(PoisonStepDistance, 6.6640625f) &&
            Near(PoisonInitialDistance, PoisonStepDistance) &&
            Near(PoisonHitRadius, 13f) &&
            PoisonProjectedDistance > PoisonMaxDistance &&
            PoisonEntryDistance <= PoisonMaxDistance &&
            PoisonEntryDistance > PoisonAimDistance;

        public bool MatchesNativeProjectileRuntime =>
            CrossbowCollisionMatchesNative && PoisonShotSweptHitMatchesNative;

        public override string ToString()
        {
            return $"crossbow step={CrossbowStepDistance:F3} init={CrossbowInitialDistance:F3} radius={CrossbowHitRadius:F1}; poison step={PoisonStepDistance:F3} init={PoisonInitialDistance:F3} radius={PoisonHitRadius:F1} aim={PoisonAimDistance:F1} projected={PoisonProjectedDistance:F1} entry={PoisonEntryDistance:F1} max={PoisonMaxDistance:F1} sweptMatch={MatchesNativeProjectileRuntime}";
        }

        private static bool Near(float actual, float expected)
        {
            return Math.Abs(actual - expected) <= 0.0001f;
        }
    }

    public class NativeFixed32ReplayResult
    {
        public string DamageText;
        public string VolatilityText;
        public int DamageF32;
        public int VolatilityF32;

        public bool MatchesNativeFixed32 => DamageF32 == 139 && VolatilityF32 == 85;

        public override string ToString()
        {
            return $"damage={DamageText}->{DamageF32} volatility={VolatilityText}->{VolatilityF32} fixed32Match={MatchesNativeFixed32}";
        }
    }

    public class NativeHpRuntimeReplayResult
    {
        public uint StartHPWire;
        public uint FirstSuffixHPWire;
        public uint ClientCrashHPWire;
        public uint DamageRaw;
        public int DamageF32;
        public int VolatilityF32;
        public int MinDamageWire;
        public int MaxDamageWire;
        public uint DamageWire;
        public int UnitRuntimeTicksBeforeSecondSuffix;
        public int BaseRegen;
        public int RegenMod;
        public int AdditiveRegen;
        public int PerTickRegenWire;
        public int DefaultWorldSettingsField;
        public bool StockUnitDamageRegenDelayClears;
        public uint SecondSuffixHPWire;
        public uint PlainWeaponPostApplyEffectRaw;

        public bool FirstHitMatchesServerSuffix =>
            DamageF32 == 139 &&
            VolatilityF32 == 85 &&
            DamageWire == 5182 &&
            FirstSuffixHPWire == NativeDamageReplaySelfTest.LatestRatling50000FirstSuffixHPWire;

        public bool SecondSuffixAdvancesUnitRuntime =>
            SecondSuffixHPWire != FirstSuffixHPWire &&
            SecondSuffixHPWire > FirstSuffixHPWire &&
            SecondSuffixHPWire <= StartHPWire;

        public bool PlainWeaponDoesNotConsumePostApplyRng => PlainWeaponPostApplyEffectRaw == 0;
        public bool StockUnitDamageRegenCooldownUsesNativeFlag =>
            DefaultWorldSettingsField == 15 &&
            !StockUnitDamageRegenDelayClears;

        public override string ToString()
        {
            return $"hp={StartHPWire}->{FirstSuffixHPWire}->{SecondSuffixHPWire} clientLocal={ClientCrashHPWire} dmgRaw=0x{DamageRaw:X8} range=[{MinDamageWire},{MaxDamageWire}] damage={DamageWire} regenTicks={UnitRuntimeTicksBeforeSecondSuffix} perTick={PerTickRegenWire} stockUnitCooldownClear={StockUnitDamageRegenDelayClears} firstHitMatch={FirstHitMatchesServerSuffix} unitRuntimeAdvance={SecondSuffixAdvancesUnitRuntime} plainEffectRaw=0x{PlainWeaponPostApplyEffectRaw:X8}";
        }
    }

    public class NativeSchedulerOrderReplayResult
    {
        public int NativeHz;
        public uint TargetEntityId;
        public uint OtherEntityId;
        public float SuffixNow;
        public int ValidationCutoffTick;
        public float ValidationCutoffTime;
        public int[] OriginalQueueOrder;
        public int[] FirstDrainResolvedOrder;
        public int[] FirstDrainRemainingOrder;
        public int[] SecondDrainResolvedOrder;
        public int[] SecondDrainRemainingOrder;
        public int[] EntityPhaseResolvedOrder;
        public int[] EntityPhaseRemainingOrder;
        public uint FirstDrainDamageWire;
        public uint SecondDrainDamageWire;
        public uint EntityPhaseDamageWire;
        public uint StartHPWire;
        public uint HPAfterFirstDrain;
        public uint HPAfterPureRead1;
        public uint HPAfterPureRead2;
        public uint HPAfterSecondDrain;
        public uint HPAfterEntityPhase;
        public int PureReadRuntimeAdvances;
        public int PureReadResolvedEvents;
        public int ProjectileHitFrameTick;
        public int ProjectileFlightTicks;
        public int ProjectileImpactDelayTicks;
        public int ProjectileImpactTick;
        public float ProjectileFireTime;
        public float ProjectileDistance;
        public float ProjectileSpeed;
        public float ProjectileFlightTime;
        public float ProjectileImpactDelay;
        public float ProjectileImpactTime;
        public bool ProjectileSamePassFirstUpdate;
        public bool ProjectileResolvesBeforeImpact;
        public bool ProjectileResolvesAtImpact;
        public float PoisonImpactTime;
        public float PoisonFrequencySeconds;
        public float PoisonDurationSeconds;
        public float PoisonFirstTickTime;
        public int PoisonFirstTickDelayTicks;
        public int PoisonMaxTicks;
        public int PoisonTicksAtImpact;
        public int PoisonTicksBeforeFirstTick;
        public int PoisonTicksAtFirstTick;
        public int EntitySynchInfoHpPayloadBytes;
        public byte EntitySynchInfoHpPayloadFlag;
        public uint EntitySynchInfoHpPayloadValueWire;
        public int EntitySynchInfoEmptyPayloadBytes;
        public uint HpSyncRuntimeBeforeRegister;
        public uint HpSyncStaleDomainHP;
        public uint HpSyncAfterRegister;
        public uint LatestPupStaleRemoteHPWire;
        public uint LatestPupClientLocalHPWire;
        public uint LatestPupSameTickStartHPWire;
        public uint LatestPupSameTickRuntimeHPWire;
        public uint LatestPupSameTickDamageWire;
        public int LatestPupSameTickImpactTick;
        public uint LatestPupSameTickComponentSuffixHPWire;
        public uint LatestPupNextVisibleSuffixHPWire;
        public uint LatestPupCb024BadRemoteHPWire;
        public uint LatestPupCb024ClientLocalHPWire;
        public uint LatestPupCb024FirstStartHPWire;
        public uint LatestPupCb024FirstDamageWire;
        public uint LatestPupCb024PostSubentityMoveSuffixHPWire;
        public uint LatestPupCb024PreSubentitySuffixHPWire;
        public int LatestPupCb024FirstImpactTick;
        public uint LatestPupCb024LateStartHPWire;
        public uint LatestPupCb024LateDamageWire;
        public uint LatestPupCb024LatePostSubentityMoveSuffixHPWire;
        public uint LatestPupCb024LatePreSubentitySuffixHPWire;
        public int LatestPupCb024LateImpactTick;
        public uint LatestWhiskerRatlingCb030StartHPWire;
        public uint LatestWhiskerRatlingCb030DamageWire;
        public uint LatestWhiskerRatlingCb030RuntimeHPWire;
        public uint LatestWhiskerRatlingCb030PreEntitySuffixHPWire;
        public uint LatestWhiskerRatlingCb030PostEntitySuffixHPWire;
        public int LatestWhiskerRatlingCb030ImpactTick;
        public uint LatestPup50030StartHPWire;
        public uint LatestPup50030FirstDamageWire;
        public uint LatestPup50030QueuedMoveSuffixHPWire;
        public uint LatestPup50030ClientLocalHPWire;
        public uint LatestPup50030NativeWriterCurrentHPWire;
        public uint LatestPup50030NativeWriterSuffixHPWire;
        public bool LatestPup50030PostZeroNonZeroMonsterSuffixEmitted;
        public bool SubentityPhaseRunsBeforeEntityPhase;
        public bool ProjectileCreatedDuringEntityUpdatesNextSubentityPhase;

        public bool GlobalOrderPreserved =>
            Seq(OriginalQueueOrder, 1, 2, 3, 4, 5) &&
            Seq(FirstDrainResolvedOrder, 2, 1) &&
            Seq(FirstDrainRemainingOrder, 4, 3, 5) &&
            Seq(EntityPhaseResolvedOrder, 4) &&
            Seq(EntityPhaseRemainingOrder, 3, 5);

        public bool SuffixDrainIdempotent =>
            FirstDrainResolvedOrder != null &&
            FirstDrainResolvedOrder.Length == 2 &&
            Seq(SecondDrainResolvedOrder) &&
            Same(FirstDrainRemainingOrder, SecondDrainRemainingOrder) &&
            SecondDrainDamageWire == 0 &&
            HPAfterSecondDrain == HPAfterFirstDrain;

        public bool PureHPReadInvariant =>
            HPAfterPureRead1 == HPAfterFirstDrain &&
            HPAfterPureRead2 == HPAfterFirstDrain &&
            PureReadRuntimeAdvances == 0 &&
            PureReadResolvedEvents == 0;

        public bool RangedProjectileTimelineMatchesNative =>
            NativeHz == 30 &&
            ProjectileHitFrameTick == 15 &&
            ProjectileFlightTicks == 15 &&
            ProjectileImpactDelayTicks == 15 &&
            ProjectileImpactTick == 30 &&
            !ProjectileSamePassFirstUpdate &&
            !ProjectileResolvesBeforeImpact &&
            ProjectileResolvesAtImpact;

        public bool ValidationCutoffKeepsEntityPhaseSeparate =>
            NativeHz == 30 &&
            ValidationCutoffTick == 30 &&
            Near(ValidationCutoffTime, SuffixNow) &&
            Seq(SecondDrainResolvedOrder) &&
            Seq(EntityPhaseResolvedOrder, 4) &&
            EntityPhaseDamageWire == 768 &&
            HPAfterEntityPhase < HPAfterSecondDrain;

        public bool HpSyncRegisterIsIdempotent =>
            HpSyncAfterRegister == HpSyncRuntimeBeforeRegister &&
            HpSyncAfterRegister < HpSyncStaleDomainHP;

        public bool PoisonFirstTickDeferred =>
            NativeHz == 30 &&
            PoisonFirstTickDelayTicks == 30 &&
            PoisonMaxTicks == 4 &&
            PoisonTicksAtImpact == 0 &&
            PoisonTicksBeforeFirstTick == 0 &&
            PoisonTicksAtFirstTick == 1;

        public bool EntitySynchInfoWriterShapeMatchesNative =>
            EntitySynchInfoHpPayloadBytes == 5 &&
            EntitySynchInfoHpPayloadFlag == 0x02 &&
            EntitySynchInfoHpPayloadValueWire == NativeDamageReplaySelfTest.LatestPup50000Cb024FirstPostHPWire &&
            EntitySynchInfoEmptyPayloadBytes == 1;

        public bool LatestPupStaleSuffixWouldCrash =>
            LatestPupStaleRemoteHPWire == NativeDamageReplaySelfTest.LatestPup50000StaleRemoteHPWire &&
            LatestPupClientLocalHPWire == NativeDamageReplaySelfTest.LatestPup50000ClientLocalHPWire &&
            LatestPupStaleRemoteHPWire != LatestPupClientLocalHPWire;

        public bool LatestPupSameTickProjectileVisibility =>
            LatestPupSameTickStartHPWire == NativeDamageReplaySelfTest.LatestPup50000SameTickStartHPWire &&
            LatestPupSameTickDamageWire == NativeDamageReplaySelfTest.LatestPup50000SameTickDamageWire &&
            LatestPupSameTickRuntimeHPWire == LatestPupSameTickStartHPWire - LatestPupSameTickDamageWire &&
            LatestPupSameTickComponentSuffixHPWire == LatestPupSameTickRuntimeHPWire &&
            LatestPupNextVisibleSuffixHPWire == LatestPupSameTickRuntimeHPWire &&
            LatestPupSameTickImpactTick == NativeDamageReplaySelfTest.LatestPup50000SameTickImpactTick;

        public bool LatestPupCb024PreEntityMonsterMoveVisibility =>
            LatestPupCb024BadRemoteHPWire == NativeDamageReplaySelfTest.LatestPup50000Cb024BadRemoteHPWire &&
            LatestPupCb024ClientLocalHPWire == NativeDamageReplaySelfTest.LatestPup50000Cb024ClientLocalHPWire &&
            LatestPupCb024FirstStartHPWire == NativeDamageReplaySelfTest.LatestPup50000Cb024FirstStartHPWire &&
            LatestPupCb024FirstDamageWire == NativeDamageReplaySelfTest.LatestPup50000Cb024FirstDamageWire &&
            LatestPupCb024PreSubentitySuffixHPWire == LatestPupCb024FirstStartHPWire &&
            LatestPupCb024PostSubentityMoveSuffixHPWire == NativeDamageReplaySelfTest.LatestPup50000Cb024FirstPostHPWire &&
            LatestPupCb024FirstImpactTick == NativeDamageReplaySelfTest.LatestPup50000Cb024FirstImpactTick &&
            LatestPupCb024LateStartHPWire == NativeDamageReplaySelfTest.LatestPup50000Cb024LateStartHPWire &&
            LatestPupCb024LateDamageWire == NativeDamageReplaySelfTest.LatestPup50000Cb024LateDamageWire &&
            LatestPupCb024LatePreSubentitySuffixHPWire == LatestPupCb024LateStartHPWire &&
            LatestPupCb024LatePostSubentityMoveSuffixHPWire == NativeDamageReplaySelfTest.LatestPup50000Cb024LatePostHPWire &&
            LatestPupCb024LateImpactTick == NativeDamageReplaySelfTest.LatestPup50000Cb024LateImpactTick;

        public bool LatestWhiskerRatlingCb030PreEntityMonsterMoveVisibility =>
            LatestWhiskerRatlingCb030StartHPWire == NativeDamageReplaySelfTest.LatestWhiskerRatling50000Cb030StartHPWire &&
            LatestWhiskerRatlingCb030DamageWire == NativeDamageReplaySelfTest.LatestWhiskerRatling50000Cb030DamageWire &&
            LatestWhiskerRatlingCb030RuntimeHPWire == LatestWhiskerRatlingCb030StartHPWire - LatestWhiskerRatlingCb030DamageWire &&
            LatestWhiskerRatlingCb030PreEntitySuffixHPWire == LatestWhiskerRatlingCb030RuntimeHPWire &&
            LatestWhiskerRatlingCb030PostEntitySuffixHPWire == LatestWhiskerRatlingCb030RuntimeHPWire &&
            LatestWhiskerRatlingCb030ImpactTick == NativeDamageReplaySelfTest.LatestWhiskerRatling50000Cb030ImpactTick;

        public bool LatestPup50030NativeWriterStaleMoveClosure =>
            LatestPup50030StartHPWire == NativeDamageReplaySelfTest.LatestPup50030StartHPWire &&
            LatestPup50030FirstDamageWire == NativeDamageReplaySelfTest.LatestPup50030FirstDamageWire &&
            LatestPup50030QueuedMoveSuffixHPWire == NativeDamageReplaySelfTest.LatestPup50030QueuedMoveSuffixHPWire &&
            LatestPup50030ClientLocalHPWire == NativeDamageReplaySelfTest.LatestPup50030ClientLocalHPWire &&
            LatestPup50030NativeWriterCurrentHPWire == LatestPup50030ClientLocalHPWire &&
            LatestPup50030NativeWriterSuffixHPWire == LatestPup50030ClientLocalHPWire &&
            !LatestPup50030PostZeroNonZeroMonsterSuffixEmitted;

        public bool NativeSubentityPhaseContract =>
            SubentityPhaseRunsBeforeEntityPhase &&
            ProjectileCreatedDuringEntityUpdatesNextSubentityPhase &&
            !ProjectileSamePassFirstUpdate;

        public bool MatchesNativeSchedulerOrderContract =>
            GlobalOrderPreserved &&
            SuffixDrainIdempotent &&
            PureHPReadInvariant &&
            RangedProjectileTimelineMatchesNative &&
            ValidationCutoffKeepsEntityPhaseSeparate &&
            HpSyncRegisterIsIdempotent &&
            PoisonFirstTickDeferred &&
            EntitySynchInfoWriterShapeMatchesNative &&
            LatestPupStaleSuffixWouldCrash &&
            LatestPupSameTickProjectileVisibility &&
            LatestPupCb024PreEntityMonsterMoveVisibility &&
            LatestWhiskerRatlingCb030PreEntityMonsterMoveVisibility &&
            LatestPup50030NativeWriterStaleMoveClosure &&
            NativeSubentityPhaseContract;

        public override string ToString()
        {
            return $"target={TargetEntityId} other={OtherEntityId} cutoff={ValidationCutoffTick}@{ValidationCutoffTime:F3} queue=[{Join(OriginalQueueOrder)}] firstResolved=[{Join(FirstDrainResolvedOrder)}] firstRemaining=[{Join(FirstDrainRemainingOrder)}] secondResolved=[{Join(SecondDrainResolvedOrder)}] entityResolved=[{Join(EntityPhaseResolvedOrder)}] hp={StartHPWire}->{HPAfterFirstDrain}->{HPAfterSecondDrain}->{HPAfterEntityPhase} pureRead={PureHPReadInvariant} projectile=t{ProjectileHitFrameTick}+flight{ProjectileFlightTicks}/delay{ProjectileImpactDelayTicks}->t{ProjectileImpactTick} subentityFirst={NativeSubentityPhaseContract} stalePup={LatestPupStaleRemoteHPWire}!={LatestPupClientLocalHPWire} sameTickPup={LatestPupSameTickComponentSuffixHPWire}->{LatestPupNextVisibleSuffixHPWire} cb024={LatestPupCb024PreSubentitySuffixHPWire}->{LatestPupCb024PostSubentityMoveSuffixHPWire}/late={LatestPupCb024LatePreSubentitySuffixHPWire}->{LatestPupCb024LatePostSubentityMoveSuffixHPWire} cb030={LatestWhiskerRatlingCb030PreEntitySuffixHPWire}->{LatestWhiskerRatlingCb030PostEntitySuffixHPWire} pup50030={LatestPup50030QueuedMoveSuffixHPWire}->{LatestPup50030NativeWriterSuffixHPWire} esiBytes={EntitySynchInfoHpPayloadBytes}/{EntitySynchInfoEmptyPayloadBytes} runtime={LatestPupSameTickRuntimeHPWire} impactTick={LatestPupSameTickImpactTick} hpRegister={HpSyncAfterRegister} poisonFirstDelayTicks={PoisonFirstTickDelayTicks} schedulerMatch={MatchesNativeSchedulerOrderContract}";
        }

        private static bool Seq(int[] actual, params int[] expected)
        {
            return actual != null && actual.SequenceEqual(expected);
        }

        private static bool Same(int[] first, int[] second)
        {
            if (first == null || second == null) return false;
            return first.SequenceEqual(second);
        }

        private static string Join(int[] values)
        {
            return values == null ? "" : string.Join(",", values);
        }

        private static bool Near(float actual, float expected)
        {
            return Math.Abs(actual - expected) <= 0.0001f;
        }
    }

    public class NativeAuthoredSpellSplitReplayResult
    {
        public bool FireBoltFound;
        public bool PoisonShotFound;
        public string FireBoltSkillId;
        public string PoisonShotSkillId;
        public AttackType FireBoltAttackType;
        public AttackType PoisonShotAttackType;
        public DamageElement FireBoltDamageType;
        public DamageElement PoisonShotDamageType;
        public int FireBoltRange;
        public int PoisonShotRange;
        public float FireBoltCooldown;
        public float PoisonShotCooldown;
        public float FireBoltProjectileSpeed;
        public float PoisonShotProjectileSpeed;
        public float FireBoltProjectileSize;
        public float PoisonShotProjectileSize;
        public float FireBoltProjectileLifespan;
        public float PoisonShotProjectileLifespan;
        public float FireBoltDamageMod;
        public float PoisonShotDamageMod;
        public float FireBoltDamageVolatility;
        public float PoisonShotDamageVolatility;
        public bool FireBoltHasDirectDamageEffect;
        public bool PoisonShotHasDirectDamageEffect;
        public bool PoisonShotHasImmediateWeaponDamageEffect;
        public int PoisonShotARModMin;
        public int PoisonShotARModMax;
        public int PoisonShotWeaponEffectDamageModMin;
        public int PoisonShotWeaponEffectDamageModMax;
        public bool FireBoltHasProjectileModifierDamage;
        public bool PoisonShotHasProjectileModifierDamage;
        public string FireBoltProjectileEffectId;
        public string PoisonShotProjectileEffectId;
        public string FireBoltProjectileModifierId;
        public string PoisonShotProjectileModifierId;
        public string PoisonShotProjectileModifierEffectId;
        public AttackType PoisonShotProjectileModifierAttackType;
        public DamageElement PoisonShotProjectileModifierDamageType;
        public float PoisonShotProjectileModifierDuration;
        public float PoisonShotProjectileModifierFrequency;
        public string PoisonShotProjectileModifierStackRule;
        public float PoisonShotProjectileModifierDamageMod;
        public float PoisonShotProjectileModifierDamageVolatility;
        public float PoisonShotProjectileModifierCriticalChance;

        public bool FireBoltDirectProjectileDamage =>
            FireBoltFound &&
            FireBoltAttackType == AttackType.MAGIC &&
            FireBoltDamageType == DamageElement.FIRE &&
            FireBoltRange == 300 &&
            Near(FireBoltCooldown, 0f) &&
            Near(FireBoltProjectileSpeed, 200f) &&
            Near(FireBoltProjectileSize, 8f) &&
            Near(FireBoltProjectileLifespan, 30.5f) &&
            Near(FireBoltDamageMod, 0.75f) &&
            Near(FireBoltDamageVolatility, 0.60f) &&
            FireBoltHasDirectDamageEffect &&
            !FireBoltHasProjectileModifierDamage &&
            string.IsNullOrEmpty(FireBoltProjectileModifierId);

        public bool PoisonShotProjectileModifierSplit =>
            PoisonShotFound &&
            PoisonShotAttackType == AttackType.RANGED &&
            PoisonShotDamageType == DamageElement.POISON &&
            PoisonShotRange == 176 &&
            Near(PoisonShotCooldown, 1f) &&
            Near(PoisonShotProjectileSpeed, 200f) &&
            Near(PoisonShotProjectileSize, 8f) &&
            Near(PoisonShotProjectileLifespan, 23f) &&
            Near(PoisonShotDamageMod, 0f) &&
            Near(PoisonShotDamageVolatility, 0f) &&
            !PoisonShotHasDirectDamageEffect &&
            PoisonShotHasImmediateWeaponDamageEffect &&
            PoisonShotARModMin == 300 &&
            PoisonShotARModMax == 300 &&
            PoisonShotWeaponEffectDamageModMin == 0 &&
            PoisonShotWeaponEffectDamageModMax == 0 &&
            PoisonShotHasProjectileModifierDamage &&
            !string.IsNullOrEmpty(PoisonShotProjectileEffectId) &&
            !string.IsNullOrEmpty(PoisonShotProjectileModifierId) &&
            !string.IsNullOrEmpty(PoisonShotProjectileModifierEffectId) &&
            PoisonShotProjectileModifierAttackType == AttackType.MAGIC &&
            PoisonShotProjectileModifierDamageType == DamageElement.POISON &&
            Near(PoisonShotProjectileModifierDuration, 4f) &&
            Near(PoisonShotProjectileModifierFrequency, 1f) &&
            string.Equals(PoisonShotProjectileModifierStackRule, "UNIQUEBYSOURCE", StringComparison.OrdinalIgnoreCase) &&
            Near(PoisonShotProjectileModifierDamageMod, 0.43f) &&
            Near(PoisonShotProjectileModifierDamageVolatility, 0.30f) &&
            Near(PoisonShotProjectileModifierCriticalChance, 0.25f);

        public bool AuthoredSplitMatchesExpectations =>
            FireBoltDirectProjectileDamage &&
            PoisonShotProjectileModifierSplit;

        public override string ToString()
        {
            return $"fireBolt found={FireBoltFound} projectile={FireBoltProjectileSpeed}/{FireBoltProjectileSize}/{FireBoltProjectileLifespan} dmg={FireBoltDamageMod}/{FireBoltDamageVolatility} direct={FireBoltDirectProjectileDamage}; poisonShot found={PoisonShotFound} projectile={PoisonShotProjectileSpeed}/{PoisonShotProjectileSize}/{PoisonShotProjectileLifespan} directDmg={PoisonShotDamageMod}/{PoisonShotDamageVolatility} immediateWeapon={PoisonShotHasImmediateWeaponDamageEffect} mod={PoisonShotProjectileModifierId} effect={PoisonShotProjectileModifierEffectId} modType={PoisonShotProjectileModifierAttackType}/{PoisonShotProjectileModifierDamageType} duration={PoisonShotProjectileModifierDuration} frequency={PoisonShotProjectileModifierFrequency} split={PoisonShotProjectileModifierSplit} authoredMatch={AuthoredSplitMatchesExpectations}";
        }

        private static bool Near(float actual, float expected)
        {
            return Math.Abs(actual - expected) <= 0.0001f;
        }
    }
}
