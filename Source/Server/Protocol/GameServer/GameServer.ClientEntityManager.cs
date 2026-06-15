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
        private uint _nextEntityId = 170;

        // ═══════════════════════════════════════════════════════════════════════════════
        // LOOT DROP ENTITY ID ALLOCATION — DEDICATED RANGE
        // ═══════════════════════════════════════════════════════════════════════════════
        // Dropped items live in their OWN ushort ID range (0xC000–0xFDFF, ~16k IDs) so
        // they never collide with player/NPC/component IDs (which reset to ConnId*500+10
        // on every zone transition), with character-list entities (10000+), or with
        // portal entities (0xFE00–0xFEFF).
        //
        // The bug this fixes: dropped items in zone N stayed alive on the client AND in
        // _droppedItems after the player zoned. The new zone reset _nextEntityId back to
        // 1010, and as new kills incremented past existing leftover drop IDs, the server
        // sent CreateEntity for an ID the client already had →
        // ClientEntityManager::processEntityCreate ERROR: EntityID(N) already exists →
        // client crash. With drops in their own dedicated range that nothing else uses,
        // the collision is structurally impossible.
        //
        // The counter is monotonic, atomic via Interlocked.Increment (UDP-receive
        // callbacks run on the .NET thread pool, bare ++ on a uint is racy), and wraps
        // within its own range. With 15,872 IDs and the DROPPED_ITEM_EXPIRE_MINUTES
        // cleanup timer, wrap collisions are vanishingly rare in practice.
        private long _nextLootEntityId = 0xBFFF;
        public ushort GetNextLootEntityId()
        {
            const long range = (long)(LOOT_ID_MAX - LOOT_ID_MIN + 1);
            long raw = System.Threading.Interlocked.Increment(ref _nextLootEntityId);
            return (ushort)(LOOT_ID_MIN + ((raw - LOOT_ID_MIN) % range));
        }

        // ═══════════════════════════════════════════════════════════════════
        // BLING GNOME HELPER METHODS — used by BlingGnomeManager
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>Find all dropped items near a position for Bling Gnome pickup.</summary>
        public List<(ushort entityId, DroppedItemInfo info)> GetDroppedItemsNear(string zone, uint instanceId, float x, float y, float radius)
        {
            var result = new List<(ushort, DroppedItemInfo)>();
            lock (_droppedItems)
            {
                foreach (var kvp in _droppedItems)
                {
                    var info = kvp.Value;
                    if (info.Zone != zone || info.InstanceId != instanceId) continue;
                    float dx = info.PosX - x;
                    float dy = info.PosY - y;
                    if (dx * dx + dy * dy <= radius * radius)
                        result.Add((kvp.Key, info));
                }
            }
            return result;
        }

        /// <summary>Remove a dropped item from the world and notify clients.</summary>
        public bool BlingGnomePickupItem(RRConnection conn, ushort entityId)
        {
            if (!_droppedItems.TryGetValue(entityId, out var info)) return false;

            if (info.DbId > 0)
            {
                try
                {
                    using (var db = DungeonRunners.Database.GameDatabase.GetConnection())
                    {
                        DungeonRunners.Database.GameDatabase.ExecuteNonQuery(db,
                            "DELETE FROM dropped_items WHERE id = @id",
                            ("@id", info.DbId));
                    }
                    _dbIdToEntityId.Remove(info.DbId);
                    Debug.LogError($"[BLING-PICKUP] Deleted converted drop from DB: dbId={info.DbId}, entityId=0x{entityId:X4}");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[BLING-PICKUP] DB delete failed: {ex.Message}");
                }
            }

            bool sentDespawn = false;
            foreach (var other in _connections.Values)
            {
                if (other == null || !other.IsSpawned) continue;
                if (!string.Equals(other.CurrentZoneName ?? "", info.Zone ?? "", StringComparison.OrdinalIgnoreCase)) continue;
                if (other.InstanceId != info.InstanceId) continue;
                SendDespawnEntity(other, entityId);
                sentDespawn = true;
            }

            if (!sentDespawn && conn != null)
                SendDespawnEntity(conn, entityId);

            _droppedItems.Remove(entityId);
            Debug.LogError($"[BLING-PICKUP] Removed entity 0x{entityId:X4} from world dbId={info.DbId}");
            return true;
        }

        /// <summary>Credit gold to player and update DB + UI.</summary>
        public void BlingGnomeCreditGold(RRConnection conn, uint goldAmount)
        {
            if (goldAmount == 0) return;
            if (!_playerStates.TryGetValue(conn.LoginName, out var state)) return;
            state.Gold += goldAmount;
            if (conn.UnitContainerId != 0)
            {
                var goldPkt = new LEWriter();
                goldPkt.WriteByte(0x07);
                goldPkt.WriteByte(0x35);
                goldPkt.WriteUInt16(conn.UnitContainerId);
                goldPkt.WriteByte(0x20);           // AddCurrency
                goldPkt.WriteUInt32(goldAmount);
                goldPkt.WriteByte(0x00);
                goldPkt.WriteUInt32(0x00000000);
                goldPkt.WriteByte(0x01);
                if (TryWriteEntitySynchForComponent(conn, goldPkt, conn.UnitContainerId, 0x20, SyncContext.PlayerActionResponse, "BLING-GOLD", true))
                {
                    goldPkt.WriteByte(0x06);
                    SendToClient(conn, goldPkt.ToArray());
                }
            }
            Debug.LogError($"[BLING-GOLD] Credited {goldAmount} gold to {conn.LoginName} (total: {state.Gold})");
            try
            {
                if (_selectedCharacter.ContainsKey(conn.LoginName))
                    using (var db = GameDatabase.GetConnection())
                        GameDatabase.ExecuteNonQuery(db,
                            "UPDATE characters SET gold=@g WHERE id=@id",
                            ("@g", (int)state.Gold), ("@id", (int)_selectedCharacter[conn.LoginName].Id));
            }
            catch (Exception ex) { Debug.LogError($"[BLING-GOLD] DB error: {ex.Message}"); }
        }

        /// <summary>Spawn a gold pile entity for Bling Gnome conversion.</summary>
        public void BlingGnomeSpawnGoldPile(RRConnection conn, float posX, float posY, float posZ, uint goldAmount)
        {
            ushort entityId = GetNextLootEntityId();
            var goldInfo = new DroppedItemInfo
            {
                Item = null,
                DbId = 0,
                Zone = conn.CurrentZoneName ?? "",
                ZoneId = conn.CurrentZoneId,
                InstanceId = conn.InstanceId,
                PosX = posX,
                PosY = posY,
                PosZ = posZ,
                PlayerLevel = 1,
                DroppedBy = conn.LoginName ?? "",
                IsGoldDrop = true,
                GoldAmount = goldAmount
            };
            _droppedItems[entityId] = goldInfo;
            SendGoldPileSpawnPacket(conn, entityId, posX, posY, posZ);
            Debug.LogError($"[BLING-GOLD] Created gold pile 0x{entityId:X4} worth {goldAmount}g at ({posX:F1},{posY:F1},{posZ:F1})");
        }

        /// <summary>Get gold value for an item GC class (for Bling Gnome conversion).</summary>
        public uint GetItemGoldValue(DroppedItemInfo item)
        {
            if (item.Item != null)
            {
                var itemData = DatabaseLoader.FindItem(item.Item.GCClass);
                if (itemData != null && itemData.goldValue > 0)
                    return (uint)itemData.goldValue;
            }
            return (uint)Math.Max(1, item.PlayerLevel * 2);
        }

        private Dictionary<string, uint> _spawnedAvatarIds = new Dictionary<string, uint>();
        private uint _nextCombatComponentId = 60000;
        private Dictionary<string, Dictionary<ushort, string>> _playerComponentTypes = new Dictionary<string, Dictionary<ushort, string>>();

        // ── Trainer response: track player Skills component + per-skill entity IDs ──
        private Dictionary<string, ushort> _playerSkillsComponentId = new Dictionary<string, ushort>();
        // ── Track avatar entity ID per player (for 0x37 addProfession entity ref) ──
        private Dictionary<string, uint> _playerAvatarEntityId = new Dictionary<string, uint>();
        // ── Track next available skill entity ID per player (for dynamically adding skills) ──
        private Dictionary<string, uint> _playerNextSkillEntityId = new Dictionary<string, uint>();
        private ushort _entityIdCounter = 0x0100;
        private Dictionary<long, ushort> _dbIdToEntityId = new Dictionary<long, ushort>(); // DB row ID → entity ID
        private float _droppedItemCleanupTimer = 0f;

        public ushort GetNextEntityId()
        {
            return _entityIdCounter++;
        }
        // ✅ ADD THIS METHOD:
        public uint GetPlayerAvatarId(string loginName)
        {
            if (_spawnedAvatarIds.TryGetValue(loginName, out uint avatarId))
            {
                return avatarId;
            }
            return 0;
        }

        public GCObject GetSelectedCharacter(string loginName)
        {
            if (!string.IsNullOrEmpty(loginName) && _selectedCharacter.TryGetValue(loginName, out var ch))
                return ch;
            return null;
        }

        // Yields every currently-connected RRConnection (used by PosseManager when it needs to
        // find a target player by character id, e.g., kick/invite recipients).
        public IEnumerable<RRConnection> AllConnectedConnections()
        {
            foreach (var conn in _connections.Values)
            {
                if (!conn.IsConnected || string.IsNullOrEmpty(conn.LoginName)) continue;
                yield return conn;
            }
        }

        // Yields each online RRConnection whose selected character belongs to the given posse.
        // Used by PosseManager to push CachedPosseFull updates to every member when posse state
        // changes (rename, MOTD, member kick/promote/demote etc.).
        public IEnumerable<RRConnection> GetConnectedMemberConnsForPosse(uint posseId)
        {
            if (posseId == 0) yield break;
            foreach (var conn in _connections.Values)
            {
                if (!conn.IsConnected || string.IsNullOrEmpty(conn.LoginName)) continue;
                if (!_selectedCharacter.TryGetValue(conn.LoginName, out var gcObj) || gcObj == null) continue;
                var sc = CharacterRepository.GetCharacter(gcObj.Id);
                if (sc != null && sc.posseId == posseId) yield return conn;
            }
        }

        // Pushes the right CachedPosseInfo state to the client based on whether the
        // character is in a posse. Called on login (HandleCharacterPlay) and the
        // queue-bridge resend path.
        private void SendPosseStateForCharacter(RRConnection conn, uint characterId,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            try
            {
                var savedChar = CharacterRepository.GetCharacter(characterId);
                if (savedChar != null && savedChar.posseId != 0)
                {
                    var posse = PosseRepository.GetPosse(savedChar.posseId);
                    if (posse != null)
                    {
                        var memberNames = PosseRepository.MemberNames(posse.Id);
                        var members = new List<(uint, string, bool)>(memberNames.Count);
                        using (var dbConn = GameDatabase.GetConnection())
                        using (var r = GameDatabase.ExecuteReader(dbConn,
                            "SELECT id, name FROM characters WHERE posse_id = @pid ORDER BY id",
                            ("@pid", (int)posse.Id)))
                        {
                            while (r.Read())
                            {
                                uint cid = (uint)r.GetInt32(0);
                                string cname = r.GetString(1);
                                members.Add((cid, cname, cid == posse.FounderCharacterId));
                            }
                        }
                        Debug.LogError($"[POSSE-LOGIN] Restoring posse '{posse.Name}' id={posse.Id} ({members.Count} members) for {savedChar.name}");
                        PosseManager.Instance.SendCachedPosseFull(conn, characterId, posse, members, sendCompressed, this);
                        return;
                    }
                    Debug.LogError($"[POSSE-LOGIN] character id={characterId} has posse_id={savedChar.posseId} but row missing — falling back to no-posse state");
                }
                // No posse: do NOT send UpdateCachedPosse(0,0). Any UpdateCachedPosse sets bit 0
                // of [PosseClient+0x128] ("have cached posse info"), which Tad's button check
                // reads as "Already in a Posse!". ConnectionNotification alone (sent before this
                // helper runs) is enough to unlock the Posse tab and /posse chat verbs.
                Debug.LogError($"[POSSE-LOGIN] character id={characterId} has no posse — skipping cache push so Tad button reads 'eligible'");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[POSSE-LOGIN] SendPosseStateForCharacter error: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // COMBAT SYSTEM METHODS
        // ═══════════════════════════════════════════════════════════════════

        private void OnMonsterSpawned(Monster monster)
        {
            _monsterBehaviorIds[monster.EntityId] = monster.BehaviorId;
            Debug.LogError($"[Combat] Monster {monster.Name} spawned ID:{monster.EntityId} behaviorId:{monster.BehaviorId}");
            WirePacketTally.RegisterMonster(
                (ushort)monster.EntityId, (ushort)monster.BehaviorId,
                (ushort)monster.SkillsId, (ushort)monster.ManipulatorsId,
                (ushort)monster.ModifiersId);
        }

        // ═══ MULTIPLAYER: Track which connection owns which monsters ═══
        private Dictionary<uint, int> _monsterOwnerConnId = new Dictionary<uint, int>();

        private void OnMonsterDespawned(Monster monster)
        {
            if (monster == null) return;
            uint entityId = monster.EntityId;
            _monsterBehaviorIds.Remove(entityId);
            _lastMonsterMoveSentAt.Remove(entityId);
            _pendingMonsterBehaviorUpdates.Remove(entityId);
            var packet = CombatPackets.BuildMonsterDespawnPacket(entityId);
            foreach (var zoneConn in _connections.Values)
            {
                if (zoneConn == null || !zoneConn.IsConnected || !zoneConn.IsSpawned) continue;
                string zoneKey = GetInstanceZoneKey(zoneConn);
                if (!string.Equals(zoneKey, monster.ZoneName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(zoneConn.CurrentZoneName, monster.ZoneName, StringComparison.OrdinalIgnoreCase))
                    continue;
                SendCompressedA(zoneConn, 0x01, 0x0f, packet);
            }
            _monsterOwnerConnId.Remove(entityId);
        }

        private void OnDamageDealt(DamageEvent evt)
        {
            var packet = CombatPackets.BuildDamagePacket(evt);
            // Send only to the connection that owns this monster
            if (_monsterOwnerConnId.TryGetValue(evt.DefenderId, out int ownerConnId) && _connections.TryGetValue(ownerConnId, out var ownerConn))
            {
                SendCompressedA(ownerConn, 0x01, 0x0f, packet);
                var monster = CombatManager.Instance.GetMonster(evt.DefenderId);
                if (monster != null)
                {
                    if (ResolveEntitySynchInfoForComponent(ownerConn, 0, 0, SyncContext.MonsterDamage, evt.DefenderId, "DAMAGE-HP", false, out EntitySynchInfoDecision decision)
                        && (decision.Flags & 0x02) != 0
                        && TryPrimeMonsterHPBeforeSync(ownerConn, monster, decision.HPWire, "DAMAGE-HP"))
                    {
                        var hpPacket = CombatPackets.BuildHPUpdatePacket(evt.DefenderId, decision.HPWire, monster.MaxHPWire);
                        SendCompressedA(ownerConn, 0x01, 0x0f, hpPacket);
                    }
                }
            }
        }

        private void PrimeMonsterHPBeforeSync(Monster monster)
        {
            CombatManager.Instance.PeekMonsterCurrentHPWire(monster);
        }

        private void PrimeMonsterHPBeforeSync(Monster monster, float now)
        {
            CombatManager.Instance.PeekMonsterCurrentHPWire(monster);
        }

        private WeaponCycleFlushResult FlushMonsterRuntimeBeforeSynch(RRConnection conn, Monster monster, SyncContext context, string packetName, float validationCutoffTime, float suffixNativeNow, uint validationCutoffTick)
        {
            var empty = new WeaponCycleFlushResult();
            if (monster == null)
                return empty;

            string source = $"{packetName ?? "unknown"} context={context}";
            uint beforeHP = CombatManager.Instance.PeekMonsterCurrentHPWire(monster);
            if (conn?.Avatar != null)
                CombatManager.Instance.UpdatePlayerPosition((uint)conn.Avatar.Id, conn.PlayerPosX, conn.PlayerPosY);

            var weaponFlush = Combat.WeaponCycleTracker.Instance.FlushMonsterEntityBeforeSynch(
                monster.EntityId,
                CombatManager.Instance.GetRoomRngForMonster(monster),
                validationCutoffTime,
                $"MonsterPreSuffix:{source}");

            if (Combat.WeaponCycleTracker.Instance.HasPendingKills)
                DrainWeaponCycleKills($"MonsterPreSuffix:{source}");

            uint afterWeaponHP = CombatManager.Instance.PeekMonsterCurrentHPWire(monster);
            var spellFlush = FlushPendingSpellsForMonsterBeforeSynch(monster, validationCutoffTime, $"MonsterPreSuffix:{source}");
            uint afterSpellHP = CombatManager.Instance.PeekMonsterCurrentHPWire(monster);
            uint afterHP = CombatManager.Instance.AdvanceMonsterRuntimeBeforeSync(monster, validationCutoffTime, $"MON-PRE-SUFFIX:{source}");
            if (CombatManager.Instance.HasPendingModifierKills)
                DrainPendingModifierKills();
            if (weaponFlush.BeforeHPWire == 0 && beforeHP != 0)
                weaponFlush.BeforeHPWire = beforeHP;
            weaponFlush.AfterHPWire = afterHP;

            Debug.LogError($"[MON-PRE-SUFFIX-COMBAT] source={packetName ?? "unknown"} context={context} target={monster.TargetId} monster={monster.Name}#{monster.EntityId} behavior={monster.BehaviorId} hp={beforeHP}->{afterHP}/{monster.MaxHPWire} spellHP={spellFlush.BeforeHPWire}->{afterSpellHP} spellPending={spellFlush.PendingBefore}->{spellFlush.PendingAfter} spellDue={spellFlush.DueForTarget} spellDueOther={spellFlush.DueOther} spellApplied={spellFlush.Applied} weaponHP={weaponFlush.BeforeHPWire}->{afterWeaponHP} pendingProjectiles={weaponFlush.PendingBefore}->{weaponFlush.PendingAfter} projectilesResolved={weaponFlush.ProjectilesResolved} cycleTicks={weaponFlush.CycleTicks} hadTargetCycle={weaponFlush.HadTargetCycle} nativeNow={suffixNativeNow:F3} cutoffTick={validationCutoffTick} cutoffTime={validationCutoffTime:F3}");
            return weaponFlush;
        }

        private bool TryPrimeMonsterHPBeforeSync(RRConnection conn, Monster monster, uint hpWire, string source)
        {
            if (conn == null || monster == null) return false;
            string authority = CombatManager.Instance != null ? CombatManager.Instance.DescribeMonsterHPAuthority(monster) : "authority=<missing>";
            Debug.LogError($"[MON-HP-PRIMER] suffix current {monster.Name}#{monster.EntityId} hp={hpWire / 256f:F2}/{monster.MaxHPWire / 256f:F2} source={source} {authority}");
            return true;
        }

        private EntitySynchInfoDecision ResolveMonsterRuntimeHPDecision(Monster monster, string packetName, string reason)
        {
            uint hpWire = CombatManager.Instance.PeekMonsterCurrentHPWire(monster);
            HpSyncService.Instance.RecordMonsterOutboundHP(monster, hpWire, $"{packetName} direct-runtime-hp {reason}");
            Debug.LogError($"[SYNC-SUFFIX-RECOVER] packet={packetName} owner=Monster entity={monster.EntityId} hp={hpWire} reason={reason}");
            GetNativeValidationCutoff(out uint validationCutoffTick, out float validationCutoffTime);
            return EntitySynchInfoDecision.HP(EntitySynchInfoOwner.Monster, hpWire, $"{packetName} direct-runtime-hp {reason}", monster.EntityId, monster.BehaviorId, 0x04, GetNativeCombatNow(), $"direct-runtime-recovery; validationCutoffTick={validationCutoffTick} validationCutoffTime={validationCutoffTime:F3}", validationCutoffTick, validationCutoffTime);
        }

        private void OnMonsterAttackStarted(Monster monster, CombatPlayer target, byte sessionId)
        {
            if (monster == null || target == null) return;


            RRConnection targetConn = null;
            foreach (var conn in _connections.Values)
            {
                if (conn == null || !conn.IsConnected || conn.Avatar == null) continue;
                if (conn.Avatar.Id != target.EntityId) continue;
                targetConn = conn;
                break;
            }

            if (targetConn == null)
            {
                CombatManager.Instance.CancelMonsterPendingAttack(monster, "MON-ATTACK-no-target-connection");
                return;
            }

            if (IsZoneSpawnInvulnerabilityBlockingCombat(targetConn))
            {
                CombatManager.Instance.CancelMonsterPendingAttack(monster, "ZoneSpawn-blocked-MON-ATTACK");
                Debug.LogError($"[MON-ATTACK] Deferred packet while ZoneSpawn blocks combat {monster.Name}->{target.Name} behavior={monster.BehaviorId} session={sessionId}");
                return;
            }

            if (!monster.IsAlive || CombatManager.Instance.PeekMonsterCurrentHPWire(monster) == 0)
            {
                CombatManager.Instance.CancelMonsterPendingAttack(monster, "MON-ATTACK-flush-dead");
                return;
            }
            byte useFlags = ResolveMonsterPrimaryManipulatorId(monster);
            bool useTargetAction = ShouldUseMonsterUseTargetAction(monster);
            monster.AttackClientVisible = true;
            monster.AttackClientVisibleTime = GetNativeCombatNow();
            QueuePendingMonsterBehaviorUpdate(new PendingMonsterBehaviorUpdate
            {
                Kind = PendingMonsterBehaviorKind.Attack,
                EntityId = monster.EntityId,
                BehaviorId = monster.BehaviorId,
                ConnId = targetConn.ConnId,
                TargetEntityId = target.EntityId,
                UseFlags = useFlags,
                SessionId = sessionId,
                UseTargetAction = useTargetAction,
                QueuedAt = GetNativeCombatNow()
            }, "MON-ATTACK");
            Debug.LogError($"[MON-ATTACK-QUEUE] native {(useTargetAction ? "UseTarget" : "AttackTarget2")} {monster.Name}->{target.Name} behavior={monster.BehaviorId} session={sessionId} flags={useFlags} target={target.EntityId}");
        }

        private void OnMonsterAttackResolved(Monster monster, CombatPlayer target, bool damaged, uint hpWire)
        {
            HandlePlayerDamageResolved(monster, target, damaged, hpWire, damaged ? "MON-ATTACK-RESOLVE-HIT" : "MON-ATTACK-RESOLVE-NO-DAMAGE");
        }

        private void OnPlayerDamageResolved(Monster monster, CombatPlayer target, bool damaged, uint hpWire, string source)
        {
            HandlePlayerDamageResolved(monster, target, damaged, hpWire, string.IsNullOrWhiteSpace(source) ? (damaged ? "PLAYER-DAMAGE-RESOLVE-HIT" : "PLAYER-DAMAGE-RESOLVE-NO-DAMAGE") : source);
        }

        private void OnPlayerStunActionResolved(Monster monster, CombatPlayer target, CombatManager.PlayerStunActionResolved action)
        {
            if (target == null || action == null)
                return;
            RRConnection targetConn = null;
            foreach (var conn in _connections.Values)
            {
                if (conn == null || !conn.IsConnected || conn.Avatar == null) continue;
                if (conn.Avatar.Id != target.EntityId) continue;
                targetConn = conn;
                break;
            }
            if (targetConn == null)
            {
                Debug.LogError($"[PLAYER-STUN-ACTION] not-sent reason=missing-target-connection player={target.Name}#{target.EntityId} action={action.ActionClassName} actionId=0x{action.ActionClassId:X2}");
                return;
            }
            ushort componentId = targetConn.UnitBehaviorId != 0 ? (ushort)targetConn.UnitBehaviorId : targetConn.BehaviorComponentId;
            if (componentId == 0)
            {
                Debug.LogError($"[PLAYER-STUN-ACTION] not-sent reason=missing-behavior-component player={target.Name}#{target.EntityId} action={action.ActionClassName} actionId=0x{action.ActionClassId:X2}");
                return;
            }
            if (!ResolveEntitySynchInfoForComponent(targetConn, componentId, 0x04, SyncContext.PlayerActionResponse, target.EntityId, "PLAYER-STUN-ACTION", false, out EntitySynchInfoDecision decision))
            {
                Debug.LogError($"[PLAYER-STUN-ACTION] not-sent reason=sync-decision player={target.Name}#{target.EntityId} action={action.ActionClassName} actionId=0x{action.ActionClassId:X2} decision={decision.Reason ?? "none"}");
                return;
            }
            ResolvedEntitySynchInfo sync = decision.ToResolved(target.EntityId, componentId, 0x04, GetNativeCombatNow(), "PLAYER-STUN-ACTION native=KnockBack::writeData@0x0052A320");
            byte[] packet = CombatPackets.BuildPlayerStunActionPacket(componentId, action.ActionClassId, action.HeadingWire, action.StrengthWire, sync);
            bool sent = SendCompressedA(targetConn, 0x01, 0x0F, packet, SyncContext.PlayerActionResponse, "PLAYER-STUN-ACTION");
            Debug.LogError($"[PLAYER-STUN-ACTION] sent={sent} monster={monster?.Name ?? "monster"}#{monster?.EntityId ?? 0} player={target.Name}#{target.EntityId} component=0x{componentId:X4} action={action.ActionClassName} actionId=0x{action.ActionClassId:X2} heading={action.HeadingWire} strengthWire={action.StrengthWire} authoredStrength={action.AuthoredStrength} knockDownPlayerBranch={action.KnockDownPlayerBranch} hp={sync.HPWire} chanceRaw=0x{action.ChanceRaw:X8} chanceRoll={action.ChanceRoll} stunResistWire={action.StunResistWire} stunRaw=0x{action.StunRaw:X8} stunRoll={action.StunRoll} source={action.Source ?? "unknown"} native=Behavior::processUpdate@0x00515620 KnockBack::writeData@0x0052A320");
        }

        private void OnPlayerModifierNetworkEvent(Monster monster, CombatPlayer target, CombatManager.PlayerModifierNetworkEvent mod)
        {
            if (target == null || mod == null)
                return;
            RRConnection targetConn = null;
            foreach (var conn in _connections.Values)
            {
                if (conn == null || !conn.IsConnected || conn.Avatar == null) continue;
                if (conn.Avatar.Id != target.EntityId) continue;
                targetConn = conn;
                break;
            }
            if (targetConn == null)
            {
                Debug.LogError($"[PLAYER-MODIFIER-PACKET] not-sent reason=missing-target-connection player={target.Name}#{target.EntityId} add={mod.Add} gc={mod.GCType ?? ""} id={mod.ModifierId}");
                return;
            }
            ushort componentId = targetConn.ModifiersId != 0 ? targetConn.ModifiersId : targetConn.ModifiersComponentId;
            if (componentId == 0)
            {
                Debug.LogError($"[PLAYER-MODIFIER-PACKET] not-sent reason=missing-modifiers-component player={target.Name}#{target.EntityId} add={mod.Add} gc={mod.GCType ?? ""} id={mod.ModifierId}");
                return;
            }
            byte subtype = mod.Add ? (byte)0x00 : (byte)0x01;
            string packetName = mod.Add ? "PLAYER-MODIFIER-ADD" : "PLAYER-MODIFIER-REMOVE";
            if (!ResolveEntitySynchInfoForComponent(targetConn, componentId, subtype, SyncContext.PlayerActionResponse, target.EntityId, packetName, false, out EntitySynchInfoDecision decision))
            {
                Debug.LogError($"[PLAYER-MODIFIER-PACKET] not-sent reason=sync-decision player={target.Name}#{target.EntityId} add={mod.Add} gc={mod.GCType ?? ""} id={mod.ModifierId} decision={decision.Reason ?? "none"}");
                return;
            }
            ResolvedEntitySynchInfo sync = decision.ToResolved(target.EntityId, componentId, subtype, GetNativeCombatNow(), $"{packetName} native={mod.Native ?? "Modifiers::processUpdate"}");
            byte[] packet = mod.Add
                ? CombatPackets.BuildPlayerModifierAddPacket(componentId, mod.GCType, mod.ModifierId, mod.Level, mod.PowerLevel, mod.DurationTicks, mod.SourceIsSelf, sync)
                : CombatPackets.BuildPlayerModifierRemovePacket(componentId, mod.ModifierId, sync);
            bool sent = SendCompressedA(targetConn, 0x01, 0x0F, packet, SyncContext.PlayerActionResponse, packetName);
            string lifecycle = mod.Lifecycle != null && mod.Lifecycle.HasClientLocalLifecycle
                ? $"visual={mod.Lifecycle.Visual ?? ""} initSound={mod.Lifecycle.InitSound ?? ""} initEffect={mod.Lifecycle.InitEffect ?? ""} removeEffect={mod.Lifecycle.RemoveEffect ?? ""} overlay={mod.Lifecycle.OverlayIcon ?? ""} overlayDuration={mod.Lifecycle.OverlayDuration}"
                : "none";
            Debug.LogError($"[PLAYER-MODIFIER-PACKET] sent={sent} packet={packetName} monster={monster?.Name ?? "monster"}#{monster?.EntityId ?? 0} player={target.Name}#{target.EntityId} component=0x{componentId:X4} gc={mod.GCType ?? ""} id={mod.ModifierId} level={mod.Level} power={mod.PowerLevel} duration={mod.DurationTicks} sourceIsSelf={mod.SourceIsSelf} replace={mod.Replace} hp={sync.HPWire} skill={mod.SkillPath ?? ""} effect={mod.EffectPath ?? ""} lifecycle={lifecycle} source={mod.Source ?? "unknown"} native={mod.Native ?? "Modifiers::processUpdate"}");
        }

        private void HandlePlayerDamageResolved(Monster monster, CombatPlayer target, bool damaged, uint hpWire, string source)
        {
            if (target == null) return;
            RRConnection targetConn = null;
            foreach (var conn in _connections.Values)
            {
                if (conn == null || !conn.IsConnected || conn.Avatar == null) continue;
                if (conn.Avatar.Id != target.EntityId) continue;
                targetConn = conn;
                break;
            }

            if (targetConn == null) return;
            uint syncHP = hpWire;
            bool attackClientVisible = monster != null && monster.AttackClientVisible;
            bool nativeContact = monster != null && monster.AttackNativeContactOnly;
            bool attackPending = monster != null && monster.AttackPending;
            bool hitResolved = monster != null && monster.AttackHitResolved;
            Debug.LogError($"[PLAYER-HP-TRUTH] RESOLVE monster={monster?.Name ?? "unknown"}#{monster?.EntityId ?? 0} player={target.Name} damaged={damaged} serverHP={syncHP} clientVisible={attackClientVisible} nativeContact={nativeContact} pending={attackPending} hitResolved={hitResolved} source={source ?? "unknown"}");
            PlayerState state = GetPlayerState(targetConn.ConnId.ToString());
            if (damaged)
            {
                if (state != null)
                    CommitPlayerHPTruth(targetConn, state, source, syncHP, false, false);
                else
                    RecordPlayerHPKnown(targetConn, source, syncHP);
                if (syncHP == 0 || hpWire == 0)
                    HandleLocalPlayerDeathFromMonster(targetConn, monster, target, syncHP);
                else
                    Debug.LogError($"[PLAYER-HP-TRUTH] DAMAGE source={source} player={target.Name} hp={syncHP / 256f:F2}");
            }
            else
            {
                Debug.LogError($"[PLAYER-HP-TRUTH] NO-DAMAGE source={source} player={target.Name} keepSyncHP={(state != null ? state.SynchHP / 256f : syncHP / 256f):F2}");
            }
        }

        private void HandleLocalPlayerDeathFromMonster(RRConnection conn, Monster monster, CombatPlayer target, uint hpWire)
        {
            if (conn == null) return;
            ClearUseTarget(conn);
            Combat.WeaponCycleTracker.Instance.ClearConnection(conn.ConnId.ToString());
            if (conn.Avatar != null)
                CombatManager.Instance.SetPlayerActiveClientAttack((uint)conn.Avatar.Id, false);

            ushort componentId = conn.UnitBehaviorId != 0 ? (ushort)conn.UnitBehaviorId : conn.BehaviorComponentId;
            if (componentId == 0)
            {
                Debug.LogError($"[PLAYER-DEATH] Missing player behavior component player={conn.LoginName ?? conn.ConnId.ToString()} hp={hpWire}");
                return;
            }

            var msg = new LEWriter();
            msg.WriteByte(0x07);
            if (!WriteClientControlUpdate(conn, msg, componentId, false, "PLAYER-DEATH-CONTROL", hpWire))
            {
                Debug.LogError($"[PLAYER-DEATH] Dropped local control release player={conn.LoginName ?? conn.ConnId.ToString()} component=0x{componentId:X4} hp={hpWire}");
                return;
            }
            msg.WriteByte(0x06);

            SendCompressedA(conn, 0x01, 0x0F, msg.ToArray(), SyncContext.ControlAck, "PLAYER-DEATH-CONTROL");
            BroadcastPlayerDeath(conn);
            Debug.LogError($"[PLAYER-DEATH] Sent local control release player={target?.Name ?? conn.LoginName ?? conn.ConnId.ToString()} monster={monster?.Name ?? "unknown"}#{monster?.EntityId ?? 0} component=0x{componentId:X4} hp={hpWire}");
        }

        private static byte ResolveMonsterPrimaryManipulatorId(Monster monster)
        {
            if (monster == null || !monster.UsePrimaryActiveSkillThisAttack)
                return 10;
            if (monster.PrimaryActiveSkillId != 0)
                return monster.PrimaryActiveSkillId;
            if (monster.Manipulators == null)
                return 10;

            foreach (var manipulator in monster.Manipulators.Values)
            {
                if (!IsNativePrimaryActiveSkillManipulator(manipulator))
                    continue;
                if (TryGetManipulatorByte(manipulator, "ID", out byte id))
                    return id;
            }

            return 10;
        }

        private static bool ShouldUseMonsterUseTargetAction(Monster monster)
        {
            if (monster == null)
                return false;
            if (!monster.UsePrimaryActiveSkillThisAttack)
                return false;
            if (!string.IsNullOrWhiteSpace(monster.PrimaryActiveSkillPath))
                return true;
            if (monster.Manipulators == null)
                return false;

            foreach (var manipulator in monster.Manipulators.Values)
            {
                if (IsNativePrimaryActiveSkillManipulator(manipulator))
                    return true;
            }

            return false;
        }

        private static bool IsNativePrimaryActiveSkillManipulator(ManipulatorData manipulator)
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

        private static bool IsActiveSkillManipulatorPath(string gcType)
        {
            var gc = GCDatabase.Instance;
            string current = gcType;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (!string.IsNullOrWhiteSpace(current) && visited.Add(current))
            {
                if (current.Equals("ActiveSkill", StringComparison.OrdinalIgnoreCase) ||
                    current.EndsWith(".ActiveSkill", StringComparison.OrdinalIgnoreCase))
                    return true;

                var node = gc?.Resolve(current);
                current = node?.Extends;
            }

            return false;
        }

        private static bool TryGetManipulatorByte(ManipulatorData manipulator, string property, out byte value)
        {
            value = 0;
            if (manipulator?.properties == null || !manipulator.properties.TryGetValue(property, out string text))
                return false;
            return byte.TryParse(text, out value);
        }

        private static bool TryGetManipulatorBool(ManipulatorData manipulator, string property, out bool value)
        {
            value = false;
            if (manipulator?.properties == null || !manipulator.properties.TryGetValue(property, out string text))
                return false;
            if (bool.TryParse(text, out value))
                return true;
            if (text == "1")
            {
                value = true;
                return true;
            }
            if (text == "0")
            {
                value = false;
                return true;
            }
            return false;
        }

        private void OnEntityDeath(uint deadId, uint killerId)
        {
            var packet = CombatPackets.BuildDeathPacket(deadId, killerId);
            if (_monsterOwnerConnId.TryGetValue(deadId, out int ownerConnId) && _connections.TryGetValue(ownerConnId, out var ownerConn))
                SendCompressedA(ownerConn, 0x01, 0x0f, packet);
            Debug.LogError($"[Combat] Entity {deadId} killed by {killerId}");
        }

        private void OnMonsterMoved(Monster monster)
        {
            if (Time.time - monster.SpawnTime < 0.5f)
                return;
            if (!_monsterBehaviorIds.TryGetValue(monster.EntityId, out var behaviorId))
                return;
            float now = Time.time;
            if (_lastMonsterMoveSentAt.TryGetValue(monster.EntityId, out float lastSent) && now - lastSent < MONSTER_MOVE_SEND_INTERVAL)
                return;
            _lastMonsterMoveSentAt[monster.EntityId] = now;
            RRConnection ownerConn = null;
            if (_monsterOwnerConnId.TryGetValue(monster.EntityId, out int ownerConnId) && _connections.TryGetValue(ownerConnId, out var foundConn))
                ownerConn = foundConn;
            if (ownerConn == null) return;
            var target = CombatManager.Instance.GetPlayer(monster.TargetId);
            float targetX = target != null ? target.PosX : monster.PosX;
            float targetY = target != null ? target.PosY : monster.PosY;
            if (!monster.IsAlive || CombatManager.Instance.PeekMonsterCurrentHPWire(monster) == 0)
                return;

            QueuePendingMonsterBehaviorUpdate(new PendingMonsterBehaviorUpdate
            {
                Kind = PendingMonsterBehaviorKind.Move,
                EntityId = monster.EntityId,
                BehaviorId = behaviorId,
                ConnId = ownerConn.ConnId,
                TargetEntityId = monster.TargetId,
                TargetX = targetX,
                TargetY = targetY,
                QueuedAt = GetNativeCombatNow()
            }, "MON-MOVE");
            Debug.LogError($"[MON-MOVE-QUEUE] {monster.Name}#{monster.EntityId} behavior={behaviorId} target={monster.TargetId} pos=({monster.PosX:F1},{monster.PosY:F1}) dest=({targetX:F1},{targetY:F1})");
        }

        private void QueuePendingMonsterBehaviorUpdate(PendingMonsterBehaviorUpdate update, string packetName)
        {
            if (update.EntityId == 0 || update.BehaviorId == 0 || update.ConnId == 0)
                return;
            _pendingMonsterBehaviorUpdates[update.EntityId] = update;
            Debug.LogError($"[MON-BEHAVIOR-QUEUE] packet={packetName} entity={update.EntityId} behavior={update.BehaviorId} kind={update.Kind} conn={update.ConnId} target={update.TargetEntityId} queuedAt={update.QueuedAt:F3}");
        }

        private void FlushPendingMonsterBehaviorUpdates(float writerNow, uint writerTick)
        {
            if (_pendingMonsterBehaviorUpdates.Count == 0)
                return;

            var pending = _pendingMonsterBehaviorUpdates.Values.ToList();
            _pendingMonsterBehaviorUpdates.Clear();
            foreach (var update in pending)
            {
                switch (update.Kind)
                {
                    case PendingMonsterBehaviorKind.Move:
                        FlushPendingMonsterMoveUpdate(update, writerNow, writerTick);
                        break;
                    case PendingMonsterBehaviorKind.Attack:
                        FlushPendingMonsterAttackUpdate(update, writerNow, writerTick);
                        break;
                }
            }
        }

        private void FlushPendingMonsterMoveUpdate(PendingMonsterBehaviorUpdate update, float writerNow, uint writerTick)
        {
            if (!_connections.TryGetValue(update.ConnId, out var ownerConn) || ownerConn == null || !ownerConn.IsConnected)
                return;

            Monster monster = CombatManager.Instance.GetMonster(update.EntityId);
            if (monster == null)
                return;
            if (!monster.IsAlive || CombatManager.Instance.PeekMonsterCurrentHPWire(monster) == 0)
            {
                Debug.LogError($"[MON-MOVE-FLUSH-SKIP] dead {monster.Name}#{monster.EntityId} behavior={update.BehaviorId} queuedAt={update.QueuedAt:F3} writer={writerTick}@{writerNow:F3}");
                return;
            }

            EntitySynchInfoDecision decision = ResolveMonsterBehaviorWriterHPDecision(ownerConn, monster, (ushort)update.BehaviorId, 0x04, SyncContext.MonsterMove, "MON-MOVE", writerNow, writerTick);
            if (!monster.IsAlive || decision.HPWire == 0 || CombatManager.Instance.PeekMonsterCurrentHPWire(monster) == 0)
            {
                Debug.LogError($"[MON-MOVE-FLUSH-SKIP] native-writer-dead {monster.Name}#{monster.EntityId} behavior={update.BehaviorId} hp={decision.HPWire} queuedAt={update.QueuedAt:F3} writer={writerTick}@{writerNow:F3}");
                return;
            }
            if ((decision.Flags & 0x02) != 0 && !TryPrimeMonsterHPBeforeSync(ownerConn, monster, decision.HPWire, "MON-MOVE"))
                return;
            var packet = CombatPackets.BuildMonsterMovePacket(monster.EntityId, update.BehaviorId, update.TargetX, update.TargetY, decision.ToResolved(monster.EntityId, update.BehaviorId, 0x04, writerNow, "MON-MOVE native-writer"));
            bool sent = SendCompressedA(ownerConn, 0x01, 0x0f, packet, SyncContext.MonsterMove, "MON-MOVE");
            if (sent)
                CombatManager.Instance.RecordMonsterMoveClientVisible(monster, update.TargetX, update.TargetY, writerNow, "MON-MOVE");
            Debug.LogError($"[MON-MOVE-FLUSH] {monster.Name}#{monster.EntityId} behavior={update.BehaviorId} target={update.TargetEntityId} dest=({update.TargetX:F1},{update.TargetY:F1}) hp={decision.HPWire} queuedAt={update.QueuedAt:F3} writer={writerTick}@{writerNow:F3} sent={sent}");
        }

        private void FlushPendingMonsterAttackUpdate(PendingMonsterBehaviorUpdate update, float writerNow, uint writerTick)
        {
            if (!_connections.TryGetValue(update.ConnId, out var targetConn) || targetConn == null || !targetConn.IsConnected)
                return;

            Monster monster = CombatManager.Instance.GetMonster(update.EntityId);
            CombatPlayer target = CombatManager.Instance.GetPlayer(update.TargetEntityId);
            if (monster == null || target == null)
                return;
            if (IsZoneSpawnInvulnerabilityBlockingCombat(targetConn))
            {
                monster.AttackClientVisible = false;
                CombatManager.Instance.CancelMonsterPendingAttack(monster, "ZoneSpawn-blocked-MON-ATTACK-flush");
                Debug.LogError($"[MON-ATTACK-FLUSH-SKIP] zoneSpawn {monster.Name}->{target.Name} behavior={update.BehaviorId} session={update.SessionId}");
                return;
            }
            if (!monster.IsAlive || CombatManager.Instance.PeekMonsterCurrentHPWire(monster) == 0)
            {
                monster.AttackClientVisible = false;
                CombatManager.Instance.CancelMonsterPendingAttack(monster, "MON-ATTACK-flush-dead");
                Debug.LogError($"[MON-ATTACK-FLUSH-SKIP] dead {monster.Name}#{monster.EntityId} behavior={update.BehaviorId} session={update.SessionId} queuedAt={update.QueuedAt:F3} writer={writerTick}@{writerNow:F3}");
                return;
            }

            EntitySynchInfoDecision decision = ResolveMonsterBehaviorWriterHPDecision(targetConn, monster, (ushort)update.BehaviorId, 0x04, SyncContext.MonsterAction, "MON-ATTACK", writerNow, writerTick);
            if (!monster.IsAlive || decision.HPWire == 0 || CombatManager.Instance.PeekMonsterCurrentHPWire(monster) == 0)
            {
                monster.AttackClientVisible = false;
                CombatManager.Instance.CancelMonsterPendingAttack(monster, "MON-ATTACK-native-writer-dead");
                Debug.LogError($"[MON-ATTACK-FLUSH-SKIP] native-writer-dead {monster.Name}#{monster.EntityId} behavior={update.BehaviorId} session={update.SessionId} hp={decision.HPWire} queuedAt={update.QueuedAt:F3} writer={writerTick}@{writerNow:F3}");
                return;
            }
            if ((decision.Flags & 0x02) != 0 && !TryPrimeMonsterHPBeforeSync(targetConn, monster, decision.HPWire, "MON-ATTACK"))
            {
                string hpAuthority = CombatManager.Instance.DescribeMonsterHPAuthority(monster);
                Debug.LogError($"[MON-ATTACK] primer unresolved; continuing with native-writer HP {monster.Name}->{target.Name} behavior={update.BehaviorId} session={update.SessionId} hp={decision.HPWire} hpAuth={hpAuthority}");
            }

            byte[] packet = CombatPackets.BuildMonsterAttackPacket(
                monster.EntityId,
                update.BehaviorId,
                (ushort)update.TargetEntityId,
                update.UseFlags,
                decision.ToResolved(monster.EntityId, update.BehaviorId, 0x04, writerNow, "MON-ATTACK native-writer"),
                update.UseTargetAction);
            if (!SendCompressedA(targetConn, 0x01, 0x0F, packet, SyncContext.MonsterAction, "MON-ATTACK"))
            {
                monster.AttackClientVisible = false;
                CombatManager.Instance.DelayMonsterAttackRetry(monster, "MON-ATTACK-unsent-send");
                CombatManager.Instance.CancelMonsterPendingAttack(monster, "MON-ATTACK-unsent-send");
                Debug.LogError($"[MON-ATTACK] Deferred packet and canceled unsent native UseTarget after send failure {monster.Name}->{target.Name} behavior={update.BehaviorId} session={update.SessionId} hp={decision.HPWire}");
                return;
            }
            if (update.UseTargetAction)
                CombatManager.Instance.CommitMonsterPrimarySkillUse(monster, "MON-ATTACK");
            monster.AttackClientVisible = true;
            monster.AttackClientVisibleTime = writerNow;
            LogPlayerHPVisibleEvent(targetConn, $"MonsterAttackStarted {monster.Name}#{monster.EntityId}");
            Debug.LogError($"[MON-ATTACK-FLUSH] Sent native {(update.UseTargetAction ? "UseTarget" : "AttackTarget2")} {monster.Name}->{target.Name} behavior={update.BehaviorId} session={update.SessionId} flags={update.UseFlags} target={update.TargetEntityId} hp={decision.HPWire} queuedAt={update.QueuedAt:F3} writer={writerTick}@{writerNow:F3}");
        }

        private EntitySynchInfoDecision ResolveMonsterBehaviorWriterHPDecision(RRConnection conn, Monster monster, ushort componentId, byte subtype, SyncContext context, string packetName, float writerNow, uint writerTick)
        {
            HpSyncService.Instance.RegisterMonster(monster);
            uint beforeHP = CombatManager.Instance.PeekMonsterCurrentHPWire(monster);

            string runtimeInstanceKey = !string.IsNullOrWhiteSpace(monster.InstanceKey) ? monster.InstanceKey : monster.ZoneName;
            CombatManager.NativeHpVisibilityCutoff hpCutoff = CombatManager.Instance.GetEntitySynchInfoValidationCutoff(context, $"{packetName} native-writer");
            FlushMonsterRuntimeBeforeSynch(conn, monster, context, packetName, hpCutoff.Time, writerNow, hpCutoff.Tick);
            int rngPos = CombatManager.Instance.GetRoomRngPosForInstance(runtimeInstanceKey);
            uint hpWire = CombatManager.Instance.PeekMonsterCurrentHPWire(monster);
            string hpReason = "writer-runtime-hp";
            HpSyncService.Instance.RecordMonsterOutboundHP(monster, hpWire, $"{packetName} {hpReason}");
            if (conn != null)
                conn.EntitySchedulerMirror.ObserveSuffixCutoff(runtimeInstanceKey, hpCutoff.Tick, hpCutoff.Time, hpCutoff.IncludeSubEntityEffects, hpCutoff.Phase, packetName);
            string provenance = $"{hpReason}; beforeHP={beforeHP}; writerTick={writerTick}; writerTime={writerNow:F3}; visibleCutoffTick={hpCutoff.Tick}; visibleCutoffTime={hpCutoff.Time:F3}; cutoffPhase={hpCutoff.Phase}; includeSubEntity={hpCutoff.IncludeSubEntityEffects}; cutoffReason={hpCutoff.Reason}; lastEntity={hpCutoff.LastEntityTick}@{hpCutoff.LastEntityTime:F3}; lastSubEntity={hpCutoff.LastSubEntityTick}@{hpCutoff.LastSubEntityTime:F3}";
            return EntitySynchInfoDecision.HP(EntitySynchInfoOwner.Monster, hpWire, $"{packetName} {hpReason}", monster.EntityId, componentId, subtype, writerNow, provenance, hpCutoff.Tick, hpCutoff.Time, runtimeInstanceKey, conn?.EntitySchedulerMirror.SchedulerTick ?? writerTick, hpCutoff.IncludeSubEntityEffects, rngPos, hpReason);
        }

        private void TickLazyEncounterSpawns()
        {
            if (_connections == null || _connections.Count == 0)
                return;

            Dictionary<string, List<RRConnection>> connsByInstance = null;
            foreach (var conn in _connections.Values)
            {
                if (conn == null || !conn.IsConnected || conn.Avatar == null)
                    continue;
                string instanceKey = conn.RuntimeInstanceKey;
                if (string.IsNullOrEmpty(instanceKey))
                    continue;
                if (!ZoneSpawnManager.Instance.HasPendingEncounters(instanceKey))
                    continue;
                connsByInstance ??= new Dictionary<string, List<RRConnection>>(StringComparer.OrdinalIgnoreCase);
                if (!connsByInstance.TryGetValue(instanceKey, out var conns))
                {
                    conns = new List<RRConnection>();
                    connsByInstance[instanceKey] = conns;
                }
                conns.Add(conn);
            }

            if (connsByInstance == null)
                return;

            foreach (var kvp in connsByInstance)
            {
                var positions = new List<(float x, float y)>(kvp.Value.Count);
                foreach (var c in kvp.Value)
                    positions.Add((c.PlayerPosX, c.PlayerPosY));

                var newly = ZoneSpawnManager.Instance.SpawnDueEncounters(kvp.Key, positions);
                if (newly == null || newly.Count == 0)
                    continue;

                foreach (var monster in newly)
                {
                    monster.AggroTriggered = false;
                    monster.State = MonsterState.Idle;
                    monster.TargetId = 0;
                    foreach (var c in kvp.Value)
                        SendMonsterToClient(c, monster);
                }
                Debug.LogError($"[LAZY-ENCOUNTER] replicated {newly.Count} lazily-generated monsters to {kvp.Value.Count} conn(s) instance='{kvp.Key}' native=EncounterObject::update+SendMonsterToClient");
            }
        }

        public void SendMonsterToClient(RRConnection conn, Monster monster)
        {
            ushort targetId = 0;
            CombatManager.Instance.ResetMonsterClientVisiblePosition(monster, monster.PosX, monster.PosY, GetNativeCombatNow(), "SPAWN-PKT");
            if (!ResolveEntitySynchInfoForComponent(conn, (ushort)monster.BehaviorId, 0x04, SyncContext.EntityInitPrimer, monster.EntityId, "SPAWN-PKT", false, out EntitySynchInfoDecision spawnDecision)
                || (spawnDecision.Flags & 0x02) == 0)
            {
                spawnDecision = ResolveMonsterRuntimeHPDecision(monster, "SPAWN-PKT", spawnDecision.Reason);
            }
            byte[] packet = CombatPackets.BuildMonsterSpawnPacket(
                monster,
                monster.BehaviorId,
                monster.SkillsId,
                monster.ManipulatorsId,
                monster.ModifiersId,
                targetId,
                targetId,
                spawnDecision.ToResolved(monster.EntityId, monster.BehaviorId, 0x04, GetNativeCombatNow(), "SPAWN-PKT")
            );
            SendToClient(conn, packet);
            // Track which connection owns this monster for targeted broadcasts
            _monsterOwnerConnId[monster.EntityId] = conn.ConnId;
            CombatManager.Instance.RecordMonsterOutboundHP(monster, spawnDecision.HPWire, "SPAWN-PKT");

            Debug.LogError($"[SPAWN] Monster {monster.Name} spawned with RNG seed 0x{monster.RngSeed:X8}");

        }





        public void SendMonsterFollowClientUDP(RRConnection conn, ushort behaviorId, uint currentHPWire)
        {
            // Get player entity ID as target
            uint targetEntityId = conn.Avatar?.Id ?? 0;
            // SendMonsterFollowClientUDP(conn, behaviorId, currentHPWire, targetEntityId);
        }

        public void SendMonsterFollowClientUDP(RRConnection conn, ushort behaviorId, uint currentHPWire, uint targetEntityId)
        {

            var session = GetUDPSessionForConnection(conn);
            if (session == null || !session.IsEstablished)
            {
                Debug.LogError($"[UDP-FOLLOW] No UDP session for monster behaviorId={behaviorId}");
                return;
            }

            if (!ResolveEntitySynchInfoForComponent(conn, behaviorId, 0x64, SyncContext.MonsterMove, 0, "UDP-FOLLOW", false, out EntitySynchInfoDecision decision))
                return;

            var writer = new LEWriter();
            writer.WriteByte(0x07);  // BeginStream (entity channel)
            writer.WriteByte(0x35);  // ComponentUpdate
            writer.WriteUInt16(behaviorId);
            writer.WriteByte(0x64);  // UnitBehavior::processUpdate reads one byte
            writer.WriteByte(0x01);
            if (!TryWriteResolvedEntitySynchInfo(writer, behaviorId, 0x64, SyncContext.MonsterMove, "UDP-FOLLOW", decision))
                return;
            writer.WriteByte(0x06);  // EndStream

            byte[] packet = writer.ToArray();

            // Pad to 8 bytes for Blowfish ECB encryption
            int padLen = (8 - (packet.Length % 8)) % 8;
            if (padLen > 0)
            {
                byte[] padded = new byte[packet.Length + padLen];
                Array.Copy(packet, padded, packet.Length);
                packet = padded;
            }

            SendEncryptedUDP(session, packet);
            Debug.LogError($"[UDP-FOLLOW] Sent FollowClient behaviorId={behaviorId} target={targetEntityId} HP={currentHPWire}");
        }

        /// <summary>
        /// Send RequestClientControl (0x64) message to trigger client to send AGGRO/CombatTick.
        /// Binary analysis at 0x40bbe0: Client checks [msg]==0x64 && [msg+0x30]==0, then sends AGGRO (type 9).
        /// This is the MISSING piece - without this, client never sends combat messages!
        /// </summary>
        // REPLACES SendRequestClientControlUDP
        // Binary-verified: Type 0x0C in state 5 calls fcn.0x51dd70 (combat tick)
        // which auto-finds nearby targets and generates Type 8 attacks on them
        // Monster STAYS in state 5 after each tick (ready for next one)
        public void SendMonsterIdleActivateUDP(RRConnection conn, ushort behaviorId, uint currentHPWire)
        {
            var session = GetUDPSessionForConnection(conn);
            if (session == null || !session.IsEstablished)
            {
                Debug.LogError($"[UDP-IDLE] No UDP session for behaviorId={behaviorId}");
                return;
            }

            if (!ResolveEntitySynchInfoForComponent(conn, behaviorId, 0x64, SyncContext.MonsterAction, 0, "UDP-IDLE", false, out EntitySynchInfoDecision decision))
                return;

            uint hpWire = (decision.Flags & 0x02) != 0 ? decision.HPWire : currentHPWire;

            var writer = new LEWriter();
            writer.WriteByte(0x07);  // BeginStream
            writer.WriteByte(0x35);  // ComponentUpdate
            writer.WriteUInt16(behaviorId);
            writer.WriteByte(0x64);  // StateMachine
            writer.WriteByte(0x01);  // flags = 0x01 (Client controls, NO target ID)
            writer.WriteUInt16(0x05);  // messageType = 5 (Wait - stay idle)
            writer.WriteUInt16(0xFFFF);  // scope = GLOBAL
            writer.WriteUInt32(hpWire);  // value = HP
            if (!TryWriteResolvedEntitySynchInfo(writer, behaviorId, 0x64, SyncContext.MonsterAction, "UDP-IDLE", decision))
                return;
            writer.WriteByte(0x06);  // EndStream

            byte[] packet = writer.ToArray();
            int padLen = (8 - (packet.Length % 8)) % 8;
            if (padLen > 0)
            {
                byte[] padded = new byte[packet.Length + padLen];
                Array.Copy(packet, padded, packet.Length);
                packet = padded;
            }

            SendEncryptedUDP(session, packet);
            Debug.LogError($"[UDP-IDLE] Sent IdleActivate behaviorId={behaviorId} HP={currentHPWire}");
        }

        public void SendMonsterCombatTickUDP(RRConnection conn, ushort behaviorComponentId, uint playerEntityId)
        {
            var session = GetUDPSessionForConnection(conn);
            if (session == null || !session.IsEstablished)
            {
                Debug.LogError($"[COMBAT-TICK] No UDP session for behaviorId={behaviorComponentId}");
                return;
            }

            if (!ResolveEntitySynchInfoForComponent(conn, behaviorComponentId, 0x64, SyncContext.MonsterAction, 0, "UDP-COMBAT-TICK", false, out EntitySynchInfoDecision decision))
                return;

            var writer = new LEWriter();
            writer.WriteByte(0x07);           // BeginStream
            writer.WriteByte(0x35);           // ComponentUpdate
            writer.WriteUInt16(behaviorComponentId);
            writer.WriteByte(0x64);           // StateMachineMessage
            writer.WriteByte(0x01);           // flags = 0x01 (HAS TARGET ID)
            writer.WriteUInt16(0x000C);       // messageType = 12 = COMBAT TICK (NOT 0x0A!)
            writer.WriteUInt16(0xFFFF);       // targetScope
            writer.WriteUInt32(0);            // value
            writer.WriteUInt16((ushort)playerEntityId);  // target player entity
            if (!TryWriteResolvedEntitySynchInfo(writer, behaviorComponentId, 0x64, SyncContext.MonsterAction, "UDP-COMBAT-TICK", decision))
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
            Debug.LogError($"[COMBAT-TICK] Sent Type 0x0C combatTick to behaviorId={behaviorComponentId} target={playerEntityId}");
        }
        public void SendCombatTickUDP(RRConnection conn, ushort componentId, uint targetEntityId)
        {
            var session = GetUDPSessionForConnection(conn);
            if (session == null || !session.IsEstablished) return;

            if (!ResolveEntitySynchInfoForComponent(conn, componentId, 0x64, SyncContext.MonsterAction, 0, "UDP-COMBAT", false, out EntitySynchInfoDecision decision))
                return;

            var writer = new LEWriter();
            writer.WriteByte(0x07);           // BeginStream
            writer.WriteByte(0x35);           // ComponentUpdate
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x64);           // subMessage = StateMachineMessage
            writer.WriteByte(0x01);           // flags = 0x01 (HAS TARGET)
            writer.WriteUInt16(0x0C);         // type 12 = CombatTick
            writer.WriteUInt32(0x0000FFFF);   // value
            writer.WriteUInt16((ushort)targetEntityId);  // target - THE PLAYER!
            if (!TryWriteResolvedEntitySynchInfo(writer, componentId, 0x64, SyncContext.MonsterAction, "UDP-COMBAT", decision))
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
            Debug.LogError($"[UDP-COMBAT] Sent CombatTick type=12 to cid={componentId}, target={targetEntityId}");
        }
        private void HandleMonsterAggro(Monster monster, CombatPlayer player)
        {
            if (monster == null || player == null) return;
            Debug.LogError($"[SERVER-AGGRO] native-state-only {monster.Name}#{monster.EntityId} -> {player.Name}#{player.EntityId} behavior={monster.BehaviorId}");
        }
        void Start()
        {
            //Debug.logger.logEnabled = false;  // ADD THIS LINE FIRST

            Debug.LogError("═══════════════════════════════════════════");
            Debug.LogError("🚀 SERVER STARTING - LOADING DATABASE...");
            Debug.LogError("═══════════════════════════════════════════");

            NativeRandomStreams.EnsureGlobalStaticSeededFromTime64("GameServer.Start");
            Database.GameDatabase.Initialize();
            ServerSettings.Load();

            // Create membership table if needed
            // accounts.is_member column: 1=member, 0=free (already exists in accounts table)
            Debug.LogError("[MEMBER] Using accounts.is_member column (1=member, 0=free)");
            DungeonRunners.Networking.DRLog.InitFromConfig();  // Apply enableDebugLog from server.cfg
            _udpPort = ServerSettings.Get("gamePort", 2603);

            // Load GC files — server reads same data as client
            string gcPath = DungeonRunners.Core.DataPaths.GcDir;
            bool nativeSidecarLoaded = DungeonRunners.Data.NativeSidecarCatalog.Instance.LoadFromAssets();
            DungeonRunners.Data.NativePackageCatalog.Instance.LoadFromAssets();
            DungeonRunners.Data.GCDatabase.Instance.Load(gcPath);
            if (!nativeSidecarLoaded ||
                !DungeonRunners.Data.NativeDescriptorCatalog.Instance.BuildFromNativeSidecar(DungeonRunners.Data.NativeSidecarCatalog.Instance))
                DungeonRunners.Data.NativeDescriptorCatalog.Instance.BuildFromGCDatabase(DungeonRunners.Data.GCDatabase.Instance);
            DungeonRunners.Data.NativeDescriptorCatalog.Instance.RunStartupSelfTest();
            WorldCollisionManager.Instance.RunStartupSelfTest();

            // Load item stat lookups from existing game database
            DungeonRunners.Data.ItemStatDatabase.Instance.Load();
            DatabaseLoader.LoadAll();
            DungeonMazeSpawner.RunStartupManifestSelfTest();
            LootManager.Instance.Initialize();
            WorldEntitySpawner.Instance.Initialize();

            Debug.LogError("[SERVER] Loading class configuration...");
            Debug.LogError("[SERVER] Loading class configuration...");
            ClassConfig.Load();
            Debug.LogError("[SERVER] Class configuration loaded");
            NativeFallbackAudit.RunStartupCoverage();


            LoadZones(); // ✅ ADDED
            _inventoryHandler = new InventoryHandler(this);
            _equipmentHandler = new EquipmentHandler(this);
            Debug.LogError("✅ Inventory and Equipment handlers initialized");
            // ═══════════════════════════════════════════════════════════════════
            // COMBAT SYSTEM INITIALIZATION
            // ═══════════════════════════════════════════════════════════════════
            CombatManager.Instance.OnMonsterSpawned += OnMonsterSpawned;
            CombatManager.Instance.OnMonsterDespawned += OnMonsterDespawned;
            CombatManager.Instance.OnMonsterAttackStarted += OnMonsterAttackStarted;
            CombatManager.Instance.OnMonsterAttackResolved += OnMonsterAttackResolved;
            CombatManager.Instance.OnPlayerDamageResolved += OnPlayerDamageResolved;
            CombatManager.Instance.OnPlayerStunActionResolved += OnPlayerStunActionResolved;
            CombatManager.Instance.OnPlayerModifierNetworkEvent += OnPlayerModifierNetworkEvent;
            CombatManager.Instance.OnMonsterPositionChanged += OnMonsterMoved;
            CombatManager.Instance.OnMonsterAggro += HandleMonsterAggro;
            // CombatManager.Instance.OnDamageDealt += OnDamageDealt;
            // CombatManager.Instance.OnEntityDeath += OnEntityDeath;
            // Add subscription with other combat subscriptions
            //  CombatManager.Instance.OnMonsterAttack += OnMonsterAttack;
            // Wire up UDP RNG seed sync for monster attacks
            /*  Monster.OnPreAttackSeedSync = (monster, target, seed) =>
              {
                  foreach (var kvp in _connections)
                  {
                      if (kvp.Value.Avatar?.Id == target.EntityId)
                      {
                          // Send RNG seed
                          SendRNGSeedUDP(kvp.Value, seed);

                          // ALSO send HP sync
                          var session = GetUDPSessionForConnection(kvp.Value);
                          if (session != null && session.IsEstablished)
                          {
                              byte[] hpPacket = new byte[8];
                              hpPacket[0] = 0x36;
                              BitConverter.GetBytes(target.PlayerState.CurrentHPWire).CopyTo(hpPacket, 1);
                              SendEncryptedUDP(session, hpPacket);
                              Debug.LogError($"[UDP-HP] Sent HP: {target.PlayerState.CurrentHPWire}");
                          }
                          break;
                      }
                  }
              };*/
            Debug.LogError($"✅ Combat System initialized - {DatabaseLoader.Creatures.Count} creatures loaded");




            Debug.LogError("═══════════════════════════════════════════");
            Debug.LogError("✅ DATABASE LOADED - SERVER READY!");
            Debug.LogError("═══════════════════════════════════════════");
            InitializeTownNPCs();  // ADD THIS LINE
            InitializeZonePortals();
            InitializeZoneCheckpoints();
            QuestManager.Instance.SetSendCallback(SendCompressedA);
            QuestManager.Instance.SetEntitySynchCallback(WritePlayerEntitySynch);
            StartServer();
            // MerchantManager.ResetAllTimers();  // Add this
            // 🔥 Subscribe to monster aggro event
            //CombatManager.Instance.OnMonsterAggro += HandleMonsterAggro;
        }

        void OnDestroy()
        {
            CombatManager.Instance.OnMonsterAttackStarted -= OnMonsterAttackStarted;
            CombatManager.Instance.OnMonsterAttackResolved -= OnMonsterAttackResolved;
            CombatManager.Instance.OnPlayerDamageResolved -= OnPlayerDamageResolved;
            CombatManager.Instance.OnPlayerStunActionResolved -= OnPlayerStunActionResolved;
            CombatManager.Instance.OnPlayerModifierNetworkEvent -= OnPlayerModifierNetworkEvent;
            CombatManager.Instance.OnMonsterPositionChanged -= OnMonsterMoved;
            CombatManager.Instance.OnMonsterAggro -= HandleMonsterAggro;
            StopServer();
        }
        // 🔥 ADD THIS ENTIRE METHOD:
        private float _tickTimer = 0f; // entity IDs the DLL pre-confirmed

        /// <summary>
        /// Public reset for callers that just sent admin XP packets and want to prevent
        /// the client DLL's echo from contaminating the next mob kill's XP award.
        /// Called from ChatCommandHandler @level after the SendAdminXPUpdate loop, and
        /// from ProcessPendingAdminActions after the web admin level action.
        /// </summary>
        public void ResetLastClientXP()
        {
            _lastClientXP = 0;
        }

        // DLL entity position tracking
        private HashSet<uint> _positionReportedMonsters = new HashSet<uint>();

        private void CheckWorldEntityProximity(RRConnection conn)
        {
            float now = DungeonRunners.Engine.Time.time;
            if (now - _lastWorldEntityCheckTime < 0.5f) return;
            _lastWorldEntityCheckTime = now;

            if (WorldEntitySpawner.Instance == null) return;
            float px = conn.PlayerPosX;
            float py = conn.PlayerPosY;
            const float RANGE = 40f;

            foreach (var kvp in WorldEntitySpawner.Instance.GetSpawnedEntities())
            {
                ushort entId = kvp.Key;
                var data = kvp.Value;
                if (_activatedWorldEntities.Contains(entId)) continue;

                float dx = px - data.PosX;
                float dy = py - data.PosY;
                if (dx * dx + dy * dy > RANGE * RANGE) continue;

                if (data.IsChest)
                {
                    _activatedWorldEntities.Add(entId);
                    if (_chestEntities.TryGetValue(entId, out var chest))
                    {
                        Debug.LogError($"[PROXIMITY] Opening chest {data.Label} (0x{entId:X4})");
                        HandleChestActivation(conn, 0, entId, 0, conn.SessionID, chest);
                    }
                }
                else if (data.IsTeleporter)
                {
                    _activatedWorldEntities.Add(entId);
                    Debug.LogError($"[PROXIMITY] Teleporter {data.Label} (0x{entId:X4})");
                    HandleTeleporterActivation(conn, 0, entId, 0, conn.SessionID, data);
                }
                else if (data.IsShrine)
                {
                    _activatedWorldEntities.Add(entId);
                    Debug.LogError($"[PROXIMITY] Shrine {data.Label} (0x{entId:X4})");
                }
            }
        }


        private bool IsPortal(ushort entityId, out ZonePortal portal)
        {
            bool found = _portalEntities.TryGetValue(entityId, out portal);
            Debug.LogError($"[IsPortal] entityId=0x{entityId:X4} found={found} count={_portalEntities.Count}");
            if (!found)
                Debug.LogError($"[IsPortal] Known IDs: {string.Join(", ", _portalEntities.Keys.Select(k => $"0x{k:X4}"))}");
            return found;
        }

        private bool IsCheckpoint(ushort entityId, out ZoneCheckpoint checkpoint)
        {
            return _checkpointEntities.TryGetValue(entityId, out checkpoint);
        }

        private void HandlePortalActivation(RRConnection conn, ushort componentId, ushort targetEntityId, byte responseId, byte sessionId, ZonePortal portal)
        {
            Debug.LogError($"[PORTAL] Activating portal to {portal.TargetZone} @ {portal.SpawnPoint}");
            conn.SessionID = sessionId;

            // Use same pattern as HandleNPCClick which works
            var msg = new LEWriter();
            msg.WriteByte(0x35);              // BeginComponentUpdate
            msg.WriteUInt16(componentId);
            msg.WriteByte(0x01);              // Response success
            msg.WriteByte(responseId);
            msg.WriteByte(0x06);              // BehaviourActionActivate
            msg.WriteByte(sessionId);
            msg.WriteUInt16(targetEntityId);

            WritePlayerEntitySynch(conn, msg);
            conn.MessageQueue.Enqueue(msg.ToArray());

            Debug.LogError($"[PORTAL] ✅ Queued portal activation response");

            // If this is a town portal, despawn it
            if (portal.Name == "TownPortal")
            {
                SendDespawnEntity(conn, targetEntityId);
                _portalEntities.Remove(targetEntityId);
                // Only clear saved state when using the RETURN portal (going back to dungeon)
                // NOT the outgoing portal (going to town) — obelisk needs it
                if (portal.TargetZone.Equals(conn.TownPortalZoneName, StringComparison.OrdinalIgnoreCase))
                {
                    conn.HasSavedTownPortal = false;
                    ClearTownPortalFromDB(conn);
                    Debug.LogError($"[PORTAL] Return portal used, cleared saved state");
                }
                Debug.LogError($"[PORTAL] Despawned town portal 0x{targetEntityId:X4}");
            }

            // Look up spawn point waypoint coordinates for the target zone
            if (!string.IsNullOrEmpty(portal.SpawnPoint))
            {
                var waypoints = DatabaseLoader.GetWaypointsForZone(portal.TargetZone);
                var wp = waypoints.FirstOrDefault(w => w.name.Equals(portal.SpawnPoint, StringComparison.OrdinalIgnoreCase));
                if (wp != null)
                {
                    conn.PendingSpawnX = (float)wp.posX;
                    conn.PendingSpawnY = (float)wp.posY;
                    conn.PendingSpawnZ = (float)wp.posZ;
                    Debug.LogError($"[PORTAL] Using waypoint '{portal.SpawnPoint}' coords: ({wp.posX}, {wp.posY}, {wp.posZ})");
                }
                else
                {
                    Debug.LogError($"[PORTAL] ⚠️ Waypoint '{portal.SpawnPoint}' not found for zone '{portal.TargetZone}'");
                }
            }

            // Walking through a portal sets the saved place (Recent Zone Portal)
            conn.ZonePortalSource = conn.CurrentZoneName;

            // Change zone
            ChangeZone(conn, portal.TargetZone, portal.SpawnPoint);
        }

        private void HandleCheckpointActivation(RRConnection conn, ushort componentId, ushort targetEntityId, byte responseId, byte sessionId, ZoneCheckpoint checkpoint)
        {
            Debug.LogError($"[CHECKPOINT] Player activated checkpoint: {checkpoint.GCType}");
            conn.SessionID = sessionId;

            var msg = new LEWriter();
            msg.WriteByte(0x35);
            msg.WriteUInt16(componentId);
            msg.WriteByte(0x01);
            msg.WriteByte(responseId);
            msg.WriteByte(0x06);
            msg.WriteByte(sessionId);
            msg.WriteUInt16(targetEntityId);
            WritePlayerEntitySynch(conn, msg);
            conn.MessageQueue.Enqueue(msg.ToArray());

            // If this is an OBELISK (in DatabaseLoader.Checkpoints), unlock it for teleport
            // Dungeon shrines are NOT obelisks — they're for respawn only
            string cpClass = checkpoint.GCType;
            if (cpClass.EndsWith("Entity", StringComparison.OrdinalIgnoreCase))
                cpClass = cpClass.Substring(0, cpClass.Length - 6);

            var knownCp = DatabaseLoader.Checkpoints.FirstOrDefault(c =>
                c.id.Equals(cpClass, StringComparison.OrdinalIgnoreCase));
            if (knownCp != null)
            {
                string connId = conn.ConnId.ToString();
                var playerState = QuestManager.Instance.GetPlayerState(connId);
                if (playerState != null && !playerState.UnlockedCheckpoints.Contains(cpClass))
                {
                    playerState.UnlockedCheckpoints.Add(cpClass);
                    Debug.LogError($"[CHECKPOINT] ✅ Unlocked obelisk: {cpClass} → zone '{knownCp.zone}'");
                    SavePlayerQuests(conn);
                }
            }

            Debug.LogError($"[CHECKPOINT] ✅ Queued activation response");
        }

        private bool IsChest(ushort entityId, out ChestSpawnData chest)
        {
            return _chestEntities.TryGetValue(entityId, out chest);
        }

        private void HandleChestActivation(RRConnection conn, ushort componentId,
            ushort targetEntityId, byte responseId, byte sessionId, ChestSpawnData chest)
        {
            Debug.LogError($"[CHEST] ═══════════════════════════════════════════════");
            Debug.LogError($"[CHEST] Opening: {chest.Label} ({chest.ItemGenerator} x{chest.ItemCount})");
            conn.SessionID = sessionId;

            // Send activation response — SAME pattern as checkpoint/portal
            var msg = new LEWriter();
            msg.WriteByte(0x35);              // BeginComponentUpdate
            msg.WriteUInt16(componentId);
            msg.WriteByte(0x01);              // Response success
            msg.WriteByte(responseId);
            msg.WriteByte(0x06);              // BehaviourActionActivate
            msg.WriteByte(sessionId);
            msg.WriteUInt16(targetEntityId);
            WritePlayerEntitySynch(conn, msg);
            conn.MessageQueue.Enqueue(msg.ToArray());

            // NCI activate — plays chest open animation
            // Behavior component now exists (0x32 CreateChild in spawn), so this works
            var nciMsg = new LEWriter();
            nciMsg.WriteByte(0x03);               // processEntityUpdate
            nciMsg.WriteUInt16(targetEntityId);    // entityId
            nciMsg.WriteByte(0x0A);               // NCI activate
            nciMsg.WriteUInt32(0x00000001);       // activation counter (1 = triggers open effect)
            WriteNonCombatInteractiveEntitySynchInfo(nciMsg, chest.GCType);
            conn.MessageQueue.Enqueue(nciMsg.ToArray());

            // Generate loot from chest
            PlayerState playerState = GetPlayerState(conn.ConnId.ToString());
            int pLevel = playerState?.Level ?? 1;

            var drops = new List<LootDrop>();
            foreach (var (generator, count, slot) in chest.GetNativeChestGenerators("TreasureChestIG", 2))
            {
                var slotDrops = LootManager.Instance.GenerateChestLoot(generator, count, pLevel);
                drops.AddRange(slotDrops);
                Debug.LogError($"[CHEST] slot={slot} generator={generator} count={count} drops={slotDrops.Count} native=NCI-chest-generator-slots-1-5");
            }

            // Member players get Major potions instead of Minor (see UpgradePotionsForMembers).
            UpgradePotionsForMembers(drops, conn);

            foreach (var drop in drops)
            {
                if (drop.IsGold)
                {
                    Debug.LogError($"[CHEST] +{drop.GoldAmount} gold");
                }
                else
                {
                    // Same NativeClass detection bug as ProcessMonsterKill loot
                    // path — see comment around line 6580 for full explanation.
                    // Without this fix, chest-dropped weapons get NativeClass="Armor"
                    // and crash the client with "Couldn't find GC object of type X.
                    // Native Type: 'Armor'" → "Unknown message type(10)".
                    if (string.IsNullOrEmpty(drop.GCType)) { Debug.LogError("[CHEST] ⚠️ Skipping null GCType"); continue; }
                    string _detectedChestNative = ResolveAuthoredItemNativeClass(drop.GCType);

                    var item = new GCObject
                    {
                        GCClass = drop.GCType,
                        NativeClass = _detectedChestNative,
                        PresetScaleMod = drop.ScaleMod,
                        StoredRarity = (int)drop.Rarity,
                        StoredLevel = drop.ItemLevel
                    };

                    var chestPlacement = ResolveNativeItemDropPlacement(
                        conn,
                        conn.CurrentZoneName,
                        conn.InstanceId,
                        chest.PosX,
                        chest.PosY,
                        chest.PosZ,
                        chest.Heading,
                        $"nci-chest:{chest.Label}:{drop.GCType}");
                    float px = chestPlacement.X;
                    float py = chestPlacement.Y;
                    float pz = chestPlacement.Z;

                    ushort lootId = GetNextLootEntityId();
                    TrackDroppedItem(lootId, item, conn, 1, px, py, pz, pLevel);

                    SendDroppedItemSpawnPacket(conn, lootId, _droppedItems[lootId]);
                    Debug.LogError($"[CHEST] ★ {drop.Label} ({drop.Rarity}) at ({px:F0},{py:F0},{pz:F0})");
                }
            }

            // Chest stays open after activation (no despawn — matches original behavior)
            _chestEntities.Remove(targetEntityId);  // Remove from tracking so it can't be looted again

            Debug.LogError($"[CHEST] {chest.Label}: {drops.Count} drops, chest opened");
            Debug.LogError($"[CHEST] ═══════════════════════════════════════════════");
        }

        private void HandleCheckpointUse(RRConnection conn, ushort componentId, byte responseId, byte sessionID, LEReader reader)
        {
            Debug.LogError($"[CHECKPOINT-USE] ═══════════════════════════════════════════════════");
            Debug.LogError($"[CHECKPOINT-USE] Player selected checkpoint destination!");

            try
            {
                // Read the checkpoint gcType from the message
                // The client sends the gcType of the checkpoint they want to teleport to
                string checkpointGcType = reader.ReadCString();
                Debug.LogError($"[CHECKPOINT-USE] Target checkpoint gcType: '{checkpointGcType}'");

                // Look up the checkpoint in our database to get the target zone and position
                var checkpointData = DatabaseLoader.GetCheckpointByGcType(checkpointGcType);

                if (checkpointData == null)
                {
                    Debug.LogError($"[CHECKPOINT-USE] ❌ Checkpoint '{checkpointGcType}' not found in database!");
                    return;
                }

                Debug.LogError($"[CHECKPOINT-USE] Found checkpoint: zone='{checkpointData.zone}', pos=({checkpointData.posX}, {checkpointData.posY}, {checkpointData.posZ})");

                // Send action response (acknowledge the use action)
                var msg = new LEWriter();
                msg.WriteByte(0x35);              // ComponentUpdate
                msg.WriteUInt16(componentId);
                msg.WriteByte(0x01);              // ActionResponse
                msg.WriteByte(responseId);
                msg.WriteByte(0x52);              // BehaviourActionUse
                msg.WriteByte(sessionID);
                msg.WriteByte(0x00);
                WritePlayerEntitySynch(conn, msg);
                conn.MessageQueue.Enqueue(msg.ToArray());

                Debug.LogError($"[CHECKPOINT-USE] ✅ Queued use response, teleporting to {checkpointData.zone}");

                // Teleport to the checkpoint's zone and position
                // We'll use the checkpoint's position as the spawn point
                ChangeZoneToPosition(conn, checkpointData.zone, checkpointData.posX, checkpointData.posY, checkpointData.posZ);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CHECKPOINT-USE] ❌ Error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static bool ShouldUseNativeFullHPZoneBootstrap(string fromZone, string toZone)
        {
            if (!DungeonMazeSpawner.TryResolveNativeExploredBitCount(toZone, out _))
                return false;
            if (DungeonMazeSpawner.TryResolveNativeExploredBitCount(fromZone, out _))
                return false;
            return true;
        }

        private static string ResolveZoneGcType(Zone zone)
        {
            if (zone != null && !string.IsNullOrEmpty(zone.gcType))
                return zone.gcType;
            string zoneName = zone?.name ?? "";
            if (zoneName.IndexOf("tutorial", StringComparison.OrdinalIgnoreCase) >= 0)
                return "world.tutorial";
            if (zoneName.IndexOf("town", StringComparison.OrdinalIgnoreCase) >= 0)
                return "world.town";
            if (!string.IsNullOrEmpty(zoneName))
                return "world." + zoneName;
            return "world.tutorial";
        }

        // New overload of ChangeZone that takes explicit coordinates
        private void ChangeZoneToPosition(RRConnection conn, string targetZone, float spawnX, float spawnY, float spawnZ)
        {
            // ═══ MULTIPLAYER: Remove avatar from old zone ═══
            if (conn.IsSpawned)
                BroadcastEntityRemove(conn, conn.CurrentZoneGcType);

            Debug.LogError($"[ZONE] ═══════════════════════════════════════════════════════════");
            Debug.LogError($"[ZONE] CHECKPOINT TELEPORT: {targetZone} @ ({spawnX}, {spawnY}, {spawnZ})");
            Debug.LogError($"[ZONE] ═══════════════════════════════════════════════════════════");
            BlingGnomeManager.Instance.SetServer(this);
            BlingGnomeManager.Instance.CleanupForZoneTransition(conn.ConnId);

            // Stop tick coroutine
            if (conn.TickCoroutine != null)
            {
                StopCoroutine(conn.TickCoroutine);
                conn.TickCoroutine = null;
                Debug.LogError("[ZONE] ✅ Stopped tick coroutine");
            }

            conn.AllowFlush = false;
            conn.MessageQueue.Clear();

            var zone = _zones.Values.FirstOrDefault(z => z.name.Equals(targetZone, StringComparison.OrdinalIgnoreCase));
            if (zone == null)
            {
                Debug.LogError($"[ZONE] ❌ ERROR: Zone '{targetZone}' not found!");
                conn.AllowFlush = true; // re-enable if zone change fails
                return;
            }

            conn.NativeFullHPOnNextSpawn = ShouldUseNativeFullHPZoneBootstrap(conn.CurrentZoneName, zone.name);
            if (conn.NativeFullHPOnNextSpawn)
                Debug.LogError($"[ZONE-HP-BOOTSTRAP] Native full HP baseline queued: {conn.CurrentZoneName} -> {zone.name}");
            conn.CurrentZoneId = zone.id;
            conn.CurrentZoneName = zone.name;  // Exact zone name for multiplayer matching
            // Complete any goto objectives that fire on zone entry (e.g. "Go to Dew Valley")
            CompleteGotoObjectivesOnZoneEntry(conn);
            GroupManager.Instance.UpdateMemberZone(conn.ConnId, zone.name);
            conn.CurrentZoneGcType = ResolveZoneGcType(zone);

            Debug.LogError($"[ZONE] CurrentZoneGcType set to: {conn.CurrentZoneGcType}");
            Debug.LogError($"[ZONE] CurrentZoneName set to: {conn.CurrentZoneName}");
            AssignInstanceId(conn);

            // Social: notify friends of zone change + refresh who list
            if (_selectedCharacter.TryGetValue(conn.LoginName, out var zoneChangeChar1))
            {
                SocialManager.Instance.NotifyFriendsZoneChange(conn.LoginName, zoneChangeChar1.Name, zone.name, SendSocialViaAuth);
                SocialManager.Instance.PushWhoListToAll(SendSocialViaAuth);
            }

            // Store the spawn position for use when zone loads
            conn.PendingSpawnX = spawnX;
            conn.PendingSpawnY = spawnY;
            conn.PendingSpawnZ = spawnZ;

            _portalEntities.Clear();
            _checkpointEntities.Clear();
            _chestEntities.Clear();
            _activatedWorldEntities.Clear();
            WorldEntitySpawner.Instance?.ClearZoneEntities();

            // Save inventory before zone transition
            SavePlayerInventory(conn);
            SavePlayerQuests(conn);
            SavePlayerLevel(conn);
            // Send zone disconnect
            var disconnectWriter = new LEWriter();
            disconnectWriter.WriteByte(0x0D);
            disconnectWriter.WriteByte(0x02);
            disconnectWriter.WriteCString("zoneleave");
            SendCompressedA(conn, 0x01, 0x0F, disconnectWriter.ToArray());
            Debug.LogError("[ZONE] ✅ Sent DISCONNECT");

            // Send zone connect
            var writer = new LEWriter();
            writer.WriteByte(0x0D);
            writer.WriteByte(0x00);
            writer.WriteCString(zone.name);
            uint zoneSeed = ResolveZoneConnectSeed(conn, zone.name);
            writer.WriteUInt32(zoneSeed);
            writer.WriteByte(0x01);
            writer.WriteByte(0xFF);
            writer.WriteCString("");
            writer.WriteUInt32(0x00);

            SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
            Debug.LogError($"[ZONE] ✅ Sent CONNECT seed=0x{zoneSeed:X8} - waiting for client 0x06 response");
        }




        private void SendActivationResponse(RRConnection conn, ushort componentId, ushort targetEntityId, byte responseId, byte sessionId)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);  // BeginStream
            writer.WriteByte(0x35);  // BeginComponentUpdate
            writer.WriteUInt16(componentId);
            writer.WriteByte(0x2E);  // Flags

            // Action response
            writer.WriteByte(0x02);  // ActionResponse
            writer.WriteByte(0x06);  // BehaviourActionActivate
            writer.WriteByte(responseId);
            writer.WriteByte(sessionId);

            // ActionActivate data
            writer.WriteUInt16(targetEntityId);

            writer.WriteByte(0x06);  // EndStream

            SendCompressedA(conn, 0x01, 0x0f, writer.ToArray());
            Debug.LogError($"[ACTIVATE] Sent activation response");
        }

        public void ChatChangeZone(RRConnection conn, string zoneName)
        {
            Debug.LogError($"[CHAT-ZONE] @z command: {zoneName}");
            ChangeZone(conn, zoneName, "");
        }

        /// <summary>
        /// Town Portal Scroll: delays zone change to let client play
        /// the SpellTownPortalEffect animation (AnimationID=61, AnimationLength=30).
        /// The client handles the visual/sound locally via InstantUse=true.
        /// </summary>
        public void UseTownPortalScroll(RRConnection conn)
        {
            Debug.LogError($"[TOWN-PORTAL] ★ Scroll used — delaying zone for effect");
            StartCoroutine(DelayedTownPortalZone(conn));
        }

        private System.Collections.IEnumerator DelayedTownPortalZone(RRConnection conn)
        {
            // AnimationLength=30 frames, TriggerTime=5 — give ~3 seconds for effect
            yield return new DungeonRunners.Engine.WaitForSeconds(3.0f);

            if (conn != null && conn.IsConnected)
            {
                Debug.LogError($"[TOWN-PORTAL] ★ Effect done — zoning to town");
                ChangeZone(conn, "dungeon00_level01", "");
            }
        }

        /// <summary>
        /// Spawns a TownPortalBlue world entity in front of the player.
        /// Uses EXACT same entity format as zone portals in SendZonePortals.
        /// Combines scroll removal + entity spawn into one packet to avoid message size errors.
        /// </summary>
        public void SpawnTownPortalWithRemoval(RRConnection conn, string targetZone,
      ushort componentId, uint itemSlotId, PlayerState playerState,
      string gcLower, byte itemX, byte itemY, int remainingCount)
        {
            ushort portalEntityId = (ushort)(0xFE00 + (_nextEntityId++ & 0xFF));

            float headingRad = conn.PlayerHeading * (float)System.Math.PI / 180.0f;
            float spawnX = conn.PlayerPosX + (float)System.Math.Sin(headingRad) * 10.0f;
            float spawnY = conn.PlayerPosY + (float)System.Math.Cos(headingRad) * 10.0f;
            Debug.LogError($"[TOWN-PORTAL] Player pos=({conn.PlayerPosX:F1}, {conn.PlayerPosY:F1}) heading={conn.PlayerHeading:F1}deg portal=({spawnX:F1}, {spawnY:F1})");
            int fx = (int)(spawnX * 256);
            int fy = (int)(spawnY * 256);
            int fz = (int)(conn.PlayerPosZ * 256);

            _portalEntities[portalEntityId] = new ZonePortal
            {
                Id = portalEntityId,
                GCType = "items.townportal.TownPortalBlue",
                Name = "TownPortal",
                PosX = spawnX,
                PosY = spawnY,
                PosZ = conn.PlayerPosZ,
                TargetZone = targetZone,
                SpawnPoint = "",
                Width = 3,
                Height = 3,
                Color = 0x0000FFFF
            };

            conn.HasSavedTownPortal = true;
            conn.TownPortalZoneName = conn.CurrentZoneName;
            conn.TownPortalZoneId = conn.CurrentZoneId;
            conn.TownPortalTargetZone = targetZone;
            conn.TownPortalPosX = spawnX;
            conn.TownPortalPosY = spawnY;
            conn.TownPortalPosZ = conn.PlayerPosZ;

            if (_selectedCharacter.ContainsKey(conn.LoginName))
            {
                var saveChar = CharacterRepository.GetCharacter(_selectedCharacter[conn.LoginName].Id);
                if (saveChar != null)
                {
                    saveChar.tpZone = conn.TownPortalZoneName;
                    saveChar.tpZoneId = (int)conn.TownPortalZoneId;
                    saveChar.tpTargetZone = conn.TownPortalTargetZone;
                    saveChar.tpPosX = conn.TownPortalPosX;
                    saveChar.tpPosY = conn.TownPortalPosY;
                    saveChar.tpPosZ = conn.TownPortalPosZ;
                    CharacterRepository.SaveCharacter(saveChar);
                    Debug.LogError($"[TOWN-PORTAL] Saved to DB");
                }
            }

            // --- Packet 1: Remove scroll ---
            var removeWriter = new LEWriter();
            removeWriter.WriteByte(0x07);
            removeWriter.WriteByte(0x35);
            removeWriter.WriteUInt16(componentId);
            removeWriter.WriteByte(0x1F);
            removeWriter.WriteUInt32(itemSlotId);
            WritePlayerEntitySynch(conn, removeWriter);

            if (remainingCount > 0)
            {
                removeWriter.WriteByte(0x35);
                removeWriter.WriteUInt16(componentId);
                removeWriter.WriteByte(0x1E);
                removeWriter.WriteByte(0x0B);
                removeWriter.WriteByte(0xFF);
                removeWriter.WriteCString(GCObject.GetPacketGCClassFor(gcLower));
                removeWriter.WriteUInt32(itemSlotId);
                removeWriter.WriteByte(itemX);
                removeWriter.WriteByte(itemY);
                removeWriter.WriteByte((byte)remainingCount);
                removeWriter.WriteByte(0x01);
                removeWriter.WriteByte(0x00);
                removeWriter.WriteByte(0x00);
                WritePlayerEntitySynch(conn, removeWriter);
            }

            removeWriter.WriteByte(0x06);
            SendToClient(conn, removeWriter.ToArray());

            // --- Packet 2: QM update - set town portal state ---
            var qmWriter = new LEWriter();
            qmWriter.WriteByte(0x07);
            qmWriter.WriteByte(0x35);
            qmWriter.WriteUInt16(conn.QuestManagerId);
            qmWriter.WriteByte(0x0A);
            qmWriter.WriteByte(0x01);
            qmWriter.WriteUInt32(conn.CurrentZoneId);
            qmWriter.WriteCString(conn.CurrentZoneName);
            qmWriter.WriteCString("");
            if (!WritePlayerEntitySynch(conn, qmWriter)) return;
            qmWriter.WriteByte(0x06);
            SendToClient(conn, qmWriter.ToArray());

            // --- Packet 3: Spawn portal entity (visual + ambient sound) ---
            var spawnWriter = new LEWriter();
            spawnWriter.WriteByte(0x07);
            spawnWriter.WriteByte(0x01);
            spawnWriter.WriteUInt16(portalEntityId);
            spawnWriter.WriteByte(0xFF);
            spawnWriter.WriteCString("items.townportal.TownPortalBlue");

            spawnWriter.WriteByte(0x02);
            spawnWriter.WriteUInt16(portalEntityId);
            spawnWriter.WriteUInt32(0x06);
            spawnWriter.WriteInt32(fx);
            spawnWriter.WriteInt32(fy);
            spawnWriter.WriteInt32(fz);
            spawnWriter.WriteInt32(0);
            spawnWriter.WriteByte(0x01);
            spawnWriter.WriteUInt16((ushort)(conn.Avatar?.Id ?? 0));

            spawnWriter.WriteCString(targetZone);
            spawnWriter.WriteCString("");
            spawnWriter.WriteByte(0x02);
            spawnWriter.WriteUInt32(0x00);
            spawnWriter.WriteUInt32(conn.CurrentZoneId);

            spawnWriter.WriteByte(0x06);

            Debug.LogError($"[TOWN-PORTAL] Spawning TownPortalBlue 0x{portalEntityId:X4} at ({spawnX:F1}, {spawnY:F1}) → {targetZone} zoneGUID={conn.CurrentZoneId}");
            Debug.LogError($"[TOWN-PORTAL] Saved portal: zone={conn.TownPortalZoneName} pos=({spawnX:F1},{spawnY:F1})");
            SendCompressedA(conn, 0x01, 0x0f, spawnWriter.ToArray());

            // --- Broadcast portal to other players in same zone (visual only) ---
            var otherWriter = new LEWriter();
            otherWriter.WriteByte(0x07);
            otherWriter.WriteByte(0x01);
            otherWriter.WriteUInt16(portalEntityId);
            otherWriter.WriteByte(0xFF);
            otherWriter.WriteCString("items.townportal.TownPortalBlue");
            otherWriter.WriteByte(0x02);
            otherWriter.WriteUInt16(portalEntityId);
            otherWriter.WriteUInt32(0x04); // visible, no collider, not clickable for others
            otherWriter.WriteInt32(fx);
            otherWriter.WriteInt32(fy);
            otherWriter.WriteInt32(fz);
            otherWriter.WriteInt32(0);
            otherWriter.WriteByte(0x01);
            otherWriter.WriteUInt16((ushort)(conn.Avatar?.Id ?? 0));
            otherWriter.WriteCString(targetZone);
            otherWriter.WriteCString("");
            otherWriter.WriteByte(0x02);
            otherWriter.WriteUInt32(0x00);
            otherWriter.WriteUInt32(conn.CurrentZoneId);
            otherWriter.WriteByte(0x06);

            byte[] otherPortalPacket = otherWriter.ToArray();
            foreach (var other in _connections.Values)
            {
                if (other == conn) continue;
                if (!other.IsSpawned) continue;
                if (other.CurrentZoneGcType != conn.CurrentZoneGcType) continue;
                if (other.InstanceId != conn.InstanceId) continue;
                SendCompressedA(other, 0x01, 0x0f, otherPortalPacket);
            }
            Debug.LogError($"[TOWN-PORTAL] Broadcast portal to other players in zone");
        }

        /// <summary>
        /// Re-spawns the blue portal when player returns to the zone where they used the scroll.
        /// Called after SendZonePortals during zone transition.
        /// </summary>
        private void SpawnReturnTownPortal(RRConnection conn)
        {
            if (!conn.HasSavedTownPortal) return;
            if (!conn.CurrentZoneName.Equals(conn.TownPortalZoneName, StringComparison.OrdinalIgnoreCase)) return;

            ushort portalEntityId = (ushort)(0xFE00 + (_nextEntityId++ & 0xFF));
            int fx = (int)(conn.TownPortalPosX * 256);
            int fy = (int)(conn.TownPortalPosY * 256);
            int fz = (int)(conn.TownPortalPosZ * 256);

            // No _portalEntities registration — return portal is visual only, not clickable

            var spawnWriter = new LEWriter();
            spawnWriter.WriteByte(0x07);
            spawnWriter.WriteByte(0x01);
            spawnWriter.WriteUInt16(portalEntityId);
            spawnWriter.WriteByte(0xFF);
            spawnWriter.WriteCString("items.townportal.TownPortalBlue");
            spawnWriter.WriteByte(0x02);
            spawnWriter.WriteUInt16(portalEntityId);
            spawnWriter.WriteUInt32(0x04);     // visible no collider, NOT clickable  0x05 has colider no click
            spawnWriter.WriteInt32(fx);
            spawnWriter.WriteInt32(fy);
            spawnWriter.WriteInt32(fz);
            spawnWriter.WriteInt32(0);
            spawnWriter.WriteByte(0x01);
            spawnWriter.WriteUInt16((ushort)(conn.Avatar?.Id ?? 0));
            spawnWriter.WriteCString(conn.TownPortalTargetZone);
            spawnWriter.WriteCString("");
            spawnWriter.WriteByte(0x01);  // state 0x01 = materializing (plays spawn animation)
            spawnWriter.WriteUInt32(0x00);
            spawnWriter.WriteUInt32(conn.TownPortalZoneId);
            spawnWriter.WriteByte(0x06);

            SendCompressedA(conn, 0x01, 0x0f, spawnWriter.ToArray());
            conn.HasSavedTownPortal = false;  // one return trip per scroll
            ClearTownPortalFromDB(conn);
            Debug.LogError($"[TOWN-PORTAL] Re-spawned return portal at ({conn.TownPortalPosX:F1}, {conn.TownPortalPosY:F1})");

            // Broadcast return portal to other players in zone
            byte[] returnPortalPacket = spawnWriter.ToArray();
            foreach (var other in _connections.Values)
            {
                if (other == conn) continue;
                if (!other.IsSpawned) continue;
                if (other.CurrentZoneGcType != conn.CurrentZoneGcType) continue;
                if (other.InstanceId != conn.InstanceId) continue;
                SendCompressedA(other, 0x01, 0x0f, returnPortalPacket);
            }

            // Auto-despawn after 5 seconds (materializing anim is ~3s, then brief idle)
            StartCoroutine(DespawnPortalAfterDelay(conn, portalEntityId, 5.0f));
        }

        private System.Collections.IEnumerator DespawnPortalAfterDelay(RRConnection conn, ushort portalEntityId, float delay)
        {
            yield return new DungeonRunners.Engine.WaitForSeconds(delay);
            if (conn == null || !conn.IsConnected) yield break;
            SendDespawnEntity(conn, portalEntityId);

            // Despawn for other players too
            foreach (var other in _connections.Values)
            {
                if (other == conn) continue;
                if (!other.IsSpawned) continue;
                if (other.CurrentZoneGcType != conn.CurrentZoneGcType) continue;
                if (other.InstanceId != conn.InstanceId) continue;
                SendDespawnEntity(other, portalEntityId);
            }

            Debug.LogError($"[TOWN-PORTAL] Auto-despawned return portal 0x{portalEntityId:X4}");
        }

        private void ClearTownPortalFromDB(RRConnection conn)
        {
            if (conn.LoginName == null || !_selectedCharacter.ContainsKey(conn.LoginName)) return;
            var ch = CharacterRepository.GetCharacter(_selectedCharacter[conn.LoginName].Id);
            if (ch == null) return;
            ch.tpZone = "";
            ch.tpZoneId = 0;
            ch.tpTargetZone = "";
            ch.tpPosX = 0;
            ch.tpPosY = 0;
            ch.tpPosZ = 0;
            CharacterRepository.SaveCharacter(ch);
            Debug.LogError($"[TOWN-PORTAL] Cleared from DB");
        }


        private void ChangeZone(RRConnection conn, string targetZone, string spawnPoint)
        {
            BlingGnomeManager.Instance.SetServer(this);
            BlingGnomeManager.Instance.CleanupForZoneTransition(conn.ConnId);

            // Clean up admin shop NPCs before zone change
            if (_adminShopNPCs.ContainsKey(conn.ConnId))
            {
                if (_adminShopNPCs.TryGetValue(conn.ConnId, out var shopNpcs))
                {
                    foreach (var npc in shopNpcs)
                    {
                        if (_zoneNPCs.TryGetValue(conn.CurrentZoneId, out var zoneList))
                            zoneList.Remove(npc);
                    }
                    _adminShopNPCs.Remove(conn.ConnId);
                }
            }

            // ═══ MULTIPLAYER: Remove avatar from old zone ═══
            if (conn.IsSpawned)
                BroadcastEntityRemove(conn, conn.CurrentZoneGcType);

            Debug.LogError($"[ZONE-TRACK] ════════════════════════════════════════════════════");
            Debug.LogError($"[ZONE-TRACK] CHANGEZONE START");
            Debug.LogError($"[ZONE-TRACK] conn.UpdateNumber BEFORE: {conn.UpdateNumber}");
            Debug.LogError($"[ZONE-TRACK] Target zone: {targetZone}");
            Debug.LogError($"[ZONE-TRACK] ════════════════════════════════════════════════════");
            Debug.LogError($"[ZONE] ═══════════════════════════════════════════════════════════");
            Debug.LogError($"[ZONE] ZONE TRANSITION: {targetZone} @ {spawnPoint}");
            Debug.LogError($"[ZONE] ═══════════════════════════════════════════════════════════");

            // 🔥 CRITICAL: Stop tick coroutine FIRST - prevents UpdateNumber mismatch
            // The tick coroutine sends position updates to the old UnitBehavior ID
            // If we don't stop it, it will send to invalid entity after zone change
            if (conn.TickCoroutine != null)
            {
                StopCoroutine(conn.TickCoroutine);
                conn.TickCoroutine = null;
                Debug.LogError("[ZONE] ✅ Stopped tick coroutine");
            }

            // 🔥 Stop flushing during transition - prevents partial messages being sent
            conn.AllowFlush = false;

            // Clear message queue - any pending messages are for old zone
            conn.MessageQueue.Clear();
            Debug.LogError("[ZONE] ✅ Cleared message queue");

            var zone = _zones.Values.FirstOrDefault(z => z.name.Equals(targetZone, StringComparison.OrdinalIgnoreCase));
            if (zone == null)
            {
                Debug.LogError($"[ZONE] ❌ ERROR: Zone '{targetZone}' not found!");
                conn.AllowFlush = true; // re-enable if zone change fails
                return;
            }

            // Update connection state with new zone ID
            conn.PendingSpawnPoint = spawnPoint ?? "";
            Debug.LogError($"[ZONE] PendingSpawnPoint set to '{conn.PendingSpawnPoint}' for target zone {zone.name}");
            conn.NativeFullHPOnNextSpawn = ShouldUseNativeFullHPZoneBootstrap(conn.CurrentZoneName, zone.name);
            if (conn.NativeFullHPOnNextSpawn)
                Debug.LogError($"[ZONE-HP-BOOTSTRAP] Native full HP baseline queued: {conn.CurrentZoneName} -> {zone.name}");
            conn.CurrentZoneId = zone.id;
            conn.CurrentZoneName = zone.name;  // Exact zone name for multiplayer matching
            // Complete any goto objectives that fire on zone entry (e.g. "Go to Dew Valley")
            CompleteGotoObjectivesOnZoneEntry(conn);
            GroupManager.Instance.UpdateMemberZone(conn.ConnId, zone.name);
            conn.CurrentZoneGcType = ResolveZoneGcType(zone);

            Debug.LogError($"[ZONE] CurrentZoneGcType set to: {conn.CurrentZoneGcType}");
            Debug.LogError($"[ZONE] CurrentZoneName set to: {conn.CurrentZoneName}");
            AssignInstanceId(conn);

            // Social: notify friends of zone change + refresh who list
            if (_selectedCharacter.TryGetValue(conn.LoginName, out var zoneChangeChar2))
            {
                SocialManager.Instance.NotifyFriendsZoneChange(conn.LoginName, zoneChangeChar2.Name, zone.name, SendSocialViaAuth);
                SocialManager.Instance.PushWhoListToAll(SendSocialViaAuth);
            }

            // Clear entity tracking for old zone
            _portalEntities.Clear();
            _checkpointEntities.Clear();
            _chestEntities.Clear();
            _activatedWorldEntities.Clear();
            WorldEntitySpawner.Instance?.ClearZoneEntities();

            // NOTE: We keep conn.Avatar and conn.Player intact (like Go server does)
            // Go server reuses same entity IDs across zone transitions
            Debug.LogError("[ZONE] ✅ Keeping conn.Avatar and conn.Player for entity reuse");

            // Save inventory before zone transition
            SavePlayerInventory(conn);
            SavePlayerQuests(conn);
            SavePlayerLevel(conn);
            // conn.Avatar = null;
            // conn.Player = null;
            // ═══════════════════════════════════════════════════════════════════════════
            // PACKET 1: ZONE DISCONNECT
            // Channel: ZoneChannel (0x0D = 13)
            // Tells client to leave current zone and clean up zone state
            // ═══════════════════════════════════════════════════════════════════════════
            var disconnectWriter = new LEWriter();
            disconnectWriter.WriteByte(0x0D);           // Byte 0: Channel = ZoneChannel (13)
            disconnectWriter.WriteByte(0x02);           // Byte 1: Message = ZoneMessageDisconnected (2)
            disconnectWriter.WriteCString("zoneleave"); // Bytes 2+: Zone name string (null-terminated)
                                                        // Go server uses "zoneleaveuhh" but any string works
            SendCompressedA(conn, 0x01, 0x0F, disconnectWriter.ToArray());
            Debug.LogError("[ZONE] ✅ Sent DISCONNECT");

            // ═══════════════════════════════════════════════════════════════════════════
            // PACKET 2: ZONE CONNECT
            // Channel: ZoneChannel (0x0D = 13)
            // Tells client to load the new zone
            // Client will respond with ZoneJoin (0x06) when zone is loaded and ready
            // ═══════════════════════════════════════════════════════════════════════════
            var writer = new LEWriter();
            writer.WriteByte(0x0D);                     // Byte 0: Channel = ZoneChannel (13)
            writer.WriteByte(0x00);                     // Byte 1: Message = ZoneMessageConnected (0)
            writer.WriteCString(zone.name);             // Bytes 2+: Zone name to load (null-terminated)
                                                        //           e.g. "dungeon00_level01", "world.town"

            uint zoneSeed = ResolveZoneConnectSeed(conn, zone.name);
            writer.WriteUInt32(zoneSeed);
            Debug.LogError($"[ZONE] Sending seed: 0x{zoneSeed:X8} for zone {zone.name}");

            writer.WriteByte(0x01);                     // Byte N+4: Unknown flag (Go sends 0x01)
            writer.WriteByte(0xFF);                     // Byte N+5: Unknown flag (Go sends 0xFF)
            writer.WriteCString("");                    // Bytes N+6+: Quest lock string (null-terminated)
                                                        //             Empty = no quest required to enter
                                                        //             Go example: "world.town.quest.Q01_a1"
            writer.WriteUInt32(0x00);                   // Last 4 bytes: Unknown (Go sends 0x01 with quest)

            SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
            Debug.LogError($"[ZONE] ✅ Sent CONNECT seed=0x{zoneSeed:X8} - waiting for client 0x06 response");

            // ═══════════════════════════════════════════════════════════════════════════
            // FLOW CONTINUES IN HandleZoneChannel when client sends 0x06:
            // 
            // 1. Client loads zone assets, generates terrain from seed
            // 2. Client sends ZoneChannel message with type 0x06 (ZoneJoin)
            // 3. HandleZoneChannel receives 0x06, calls spawn sequence:
            //    a. ZoneMessageReady (0x0D 0x01) - zone ID + explored bits
            //    b. ZoneMessageInstanceCount (0x0D 0x05) - instance info
            //    c. Interval message (0x07 0x0D) - tick/timing info
            //    d. SendPlayerEntitySpawn() - spawn packet with 0x07...0x46
            // 4. Tick coroutine restarts in SendPlayerEntitySpawn
            // ═══════════════════════════════════════════════════════════════════════════
        }



        ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        ///////////// ADDING IN NPC LOGIC//////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////// 

        ////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        ///////////// ADDING IN NPC LOGIC//////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////// 

        [System.Serializable]
        public class ZoneNPC
        {
            public uint Id;
            public uint UnitBehaviorId;
            public string GCClass;
            public string Name;
            public float PosX;
            public float PosY;
            public float PosZ;
            public float Heading;
            public bool IsMerchant;
            public uint MerchantId;
            public bool IsAdminMerchant;  // ← NEW
            public bool IsTrainer;
            public uint TrainerId;
            public bool IsBank;
            public uint BankComponentId;
            public bool IsPosseMagnate;
            public uint PosseOptionComponentId;
            /// <summary>
            /// Ordered list of AvailableSkill GC classes from the trainer's GC file.
            /// Index = the uint32 V sent in the train request packet.
            /// Built from TrainerFighterBase.gc / TrainerMageBase.gc / TrainerRangerBase.gc
            /// </summary>
            public List<string> TrainerSkills;
        }

        /// <summary>
        /// Returns the ordered AvailableSkill GC class list for a trainer NPC.
        /// Parsed from the GC files: TrainerFighterBase.gc, TrainerMageBase.gc, TrainerRangerBase.gc
        /// Order matters — the client sends a 0-based index into this list.
        /// </summary>
        private static List<string> GetTrainerSkillList(string npcGcClass)
        {
            if (npcGcClass.IndexOf("TrainerFighter", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new List<string>
                {
                    "skills.generic.1HMeleeSpeedBuff",
                    "skills.generic.2HMeleeSpeedBuff",
                    "skills.generic.CleaveUpgradeProcPassive",
                    "skills.generic.FearResistModPassive",
                    "skills.generic.Butcher",
                    "skills.generic.Cleave",
                    "skills.generic.ShadowRage",
                    "skills.generic.DivineDamageBuff",
                    "skills.generic.DivineResistBuff",
                    "skills.generic.DivineResistPassive",
                    "skills.generic.FearMeleeAttack",
                    "skills.generic.FighterClassPassive",
                    "skills.generic.HealSelf",
                    "skills.generic.Charge",
                    "skills.generic.BlockKnockdownProcPassive",
                    "skills.generic.MinMoveSpeedBuff",
                    "skills.generic.MeleeAttackRatingModPassive",
                    "skills.generic.FireMeleeSummon",
                    "skills.generic.AggroIncreaseModBuff",
                    "skills.generic.MeleeDamageReflectionBuff",
                    "skills.generic.Stomp",
                    "skills.generic.DivineMeleeAttack",
                    "skills.generic.StunResistBuff",
                    "skills.generic.SlowDeBuff",
                    "skills.generic.MeleeAttackSpeedModPassive",
                };
            }
            else if (npcGcClass.IndexOf("TrainerMage", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new List<string>
                {
                    "skills.generic.ManaShield",
                    "skills.generic.MageClassPassive",
                    "skills.Generic.SummonSnowman",
                    "skills.generic.IceDamageBuff",
                    "skills.generic.IceTargetedBurst",
                    "skills.generic.SummonerClassPassive",
                    "skills.generic.FireCone",
                    "skills.generic.ShadowDamageBuff",
                    "skills.generic.FireBolt",
                    "skills.generic.FireResistBuff",
                    "skills.generic.FireResistPassive",
                    "skills.generic.DivineIntervention",
                    "skills.generic.IceBolt",
                    "skills.generic.IceResistBuff",
                    "skills.generic.IceMultiBolt",
                    "skills.generic.IceResistPassive",
                    "skills.generic.ShadowLightningUpgradeProcPassive",
                    "skills.generic.MagicDamageModPassive",
                    "skills.generic.SnowManIceDamageProcAuraModBuff",
                    "skills.generic.IceTargetedBurstUpgradeProcPassive",
                    "skills.generic.SnowmanFreezeAura",
                    "skills.generic.FireDamageBuff",
                    "skills.generic.DivineRay",
                    "skills.generic.FireRing",
                    "skills.generic.ShadowBolt",
                    "skills.generic.ShadowLightning",
                    "skills.generic.ShadowResistBuff",
                    "skills.generic.ShadowResistPassive",
                    "skills.generic.ShadowLightningKnockdown",
                    "skills.generic.ShadowTendrils",
                    "skills.generic.SnowmanHealthModAuraBuff",
                    "skills.generic.ManaSelf",
                    "skills.generic.Teleport",
                };
            }
            else if (npcGcClass.IndexOf("TrainerRanger", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new List<string>
                {
                    "skills.generic.Blight",
                    "skills.generic.InfectiousPoisonUpgradeProcPassive",
                    "skills.generic.RangerClassPassive",
                    "skills.generic.FireCurseShot",
                    "skills.generic.MonsterBaitHealthModPassive",
                    "skills.generic.FireShot",
                    "skills.generic.FearShot",
                    "skills.generic.PoisonBlastRadius",
                    "skills.generic.PoisonDamageBuff",
                    "skills.generic.SummonMonsterBait",
                    "skills.generic.PoisonTrail",
                    "skills.generic.NoxiousShot",
                    "skills.generic.PoisonResistBuff",
                    "skills.generic.PoisonResistPassive",
                    "skills.generic.PoisonShot",
                    "skills.generic.PenetrateKnockdownShot",
                    "skills.generic.RangedSpeedBuff",
                    "skills.generic.IceShot",
                    "skills.generic.Sprint",
                    "skills.generic.RangeAttackSpeedModPassive",
                };
            }
            return new List<string>();
        }


        private void InitializeTownNPCs()
        {
            Debug.LogError("═══════════════════════════════════════════════════════════");
            Debug.LogError("[InitTownNPCs] Creating NPCs for town zone...");
            Debug.LogError("═══════════════════════════════════════════════════════════");

            var townZone = _zones.Values.FirstOrDefault(z => z.name.ToLower() == "town");
            if (townZone == null)
            {
                Debug.LogError("[InitTownNPCs] Town zone not found!");
                return;
            }

            uint zoneId = townZone.id;
            _zoneNPCs[zoneId] = new List<ZoneNPC>();

            // Load NPCs from database (extracted from worlds.json)
            if (DatabaseLoader.TownNPCs == null || DatabaseLoader.TownNPCs.Count == 0)
            {
                Debug.LogError("[InitTownNPCs] ❌ No town NPCs loaded from database!");
                return;
            }

            foreach (var npcData in DatabaseLoader.TownNPCs)
            {
                bool isMerchant = MerchantManager.IsMerchant(npcData.gcType);
                bool isTrainer = npcData.gcType.IndexOf("Trainer", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isBank = npcData.gcType.EndsWith(".Bank", StringComparison.OrdinalIgnoreCase);
                bool isPosseMagnate = npcData.gcType.EndsWith(".PosseMagnate", StringComparison.OrdinalIgnoreCase);
                var npc = new ZoneNPC
                {
                    GCClass = npcData.gcType,
                    Name = npcData.name,
                    PosX = npcData.posX,
                    PosY = npcData.posY,
                    PosZ = npcData.posZ,
                    Heading = npcData.heading,
                    Id = _nextEntityId++,
                    UnitBehaviorId = _nextEntityId++,
                    IsMerchant = isMerchant,
                    MerchantId = isMerchant ? _nextEntityId++ : 0,
                    IsTrainer = isTrainer,
                    TrainerId = isTrainer ? _nextEntityId++ : 0,
                    TrainerSkills = isTrainer ? GetTrainerSkillList(npcData.gcType) : null,
                    IsBank = isBank,
                    BankComponentId = isBank ? _nextEntityId++ : 0,
                    IsPosseMagnate = isPosseMagnate,
                    PosseOptionComponentId = isPosseMagnate ? _nextEntityId++ : 0
                };

                _zoneNPCs[zoneId].Add(npc);
                string tags = (isMerchant ? " [MERCHANT]" : "") + (isTrainer ? $" [TRAINER cid={npc.TrainerId}]" : "") + (isBank ? $" [BANK cid={npc.BankComponentId}]" : "") + (isPosseMagnate ? $" [POSSE cid={npc.PosseOptionComponentId}]" : "");
                Debug.LogError($"[InitTownNPCs] ✓ Created NPC: {npc.Name} (ID: {npc.Id}){tags}");
            }
            // Initialize Tutorial NPCs
            var tutorialZone = _zones.Values.FirstOrDefault(z => z.name.ToLower() == "tutorial");
            if (tutorialZone != null)
            {
                uint tutorialZoneId = tutorialZone.id;
                _zoneNPCs[tutorialZoneId] = new List<ZoneNPC>();

                if (DatabaseLoader.TutorialNPCs != null && DatabaseLoader.TutorialNPCs.Count > 0)
                {
                    foreach (var npcData in DatabaseLoader.TutorialNPCs)
                    {
                        bool isMerchant = MerchantManager.IsMerchant(npcData.gcType);
                        bool isTrainer = npcData.gcType.IndexOf("Trainer", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool isBank = npcData.gcType.EndsWith(".Bank", StringComparison.OrdinalIgnoreCase);
                        bool isPosseMagnate = npcData.gcType.EndsWith(".PosseMagnate", StringComparison.OrdinalIgnoreCase);
                        var npc = new ZoneNPC
                        {
                            GCClass = npcData.gcType,
                            Name = npcData.name,
                            PosX = npcData.posX,
                            PosY = npcData.posY,
                            PosZ = npcData.posZ,
                            Heading = npcData.heading,
                            Id = _nextEntityId++,
                            UnitBehaviorId = _nextEntityId++,
                            IsMerchant = isMerchant,
                            MerchantId = isMerchant ? _nextEntityId++ : 0,
                            IsTrainer = isTrainer,
                            TrainerId = isTrainer ? _nextEntityId++ : 0,
                            TrainerSkills = isTrainer ? GetTrainerSkillList(npcData.gcType) : null,
                            IsBank = isBank,
                            BankComponentId = isBank ? _nextEntityId++ : 0,
                            IsPosseMagnate = isPosseMagnate,
                            PosseOptionComponentId = isPosseMagnate ? _nextEntityId++ : 0
                        };

                        _zoneNPCs[tutorialZoneId].Add(npc);
                        string tags = (isMerchant ? " [MERCHANT]" : "") + (isTrainer ? $" [TRAINER cid={npc.TrainerId}]" : "") + (isBank ? $" [BANK cid={npc.BankComponentId}]" : "") + (isPosseMagnate ? $" [POSSE cid={npc.PosseOptionComponentId}]" : "");
                        Debug.LogError($"[InitTutorialNPCs] ✓ Created NPC: {npc.Name} (ID: {npc.Id}){tags}");
                    }
                }


                Debug.LogError($"[InitTownNPCs] ✅ Created {_zoneNPCs[zoneId].Count} NPCs for town");
                Debug.LogError("═══════════════════════════════════════════════════════════");
            }

            // Initialize PVP (pvp_start / Pwnston) NPCs
            var pvpZone = _zones.Values.FirstOrDefault(z => z.name.Equals("pvp_start", StringComparison.OrdinalIgnoreCase));
            if (pvpZone != null && DatabaseLoader.PvpNPCs != null && DatabaseLoader.PvpNPCs.Count > 0)
            {
                uint pvpZoneId = pvpZone.id;
                _zoneNPCs[pvpZoneId] = new List<ZoneNPC>();

                foreach (var npcData in DatabaseLoader.PvpNPCs)
                {
                    bool isMerchant = MerchantManager.IsMerchant(npcData.gcType);
                    bool isTrainer = npcData.gcType.IndexOf("Trainer", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool isBank = npcData.gcType.EndsWith(".Bank", StringComparison.OrdinalIgnoreCase);
                    bool isPosseMagnate = npcData.gcType.EndsWith(".PosseMagnate", StringComparison.OrdinalIgnoreCase);
                    bool isPvpNpc = npcData.gcType.IndexOf("L33tenant", StringComparison.OrdinalIgnoreCase) >= 0
                                 || npcData.name.IndexOf("L33tenant", StringComparison.OrdinalIgnoreCase) >= 0;
                    var npc = new ZoneNPC
                    {
                        GCClass = npcData.gcType,
                        Name = npcData.name,
                        PosX = npcData.posX,
                        PosY = npcData.posY,
                        PosZ = npcData.posZ,
                        Heading = npcData.heading,
                        Id = _nextEntityId++,
                        UnitBehaviorId = _nextEntityId++,
                        IsMerchant = isMerchant,
                        MerchantId = isMerchant ? _nextEntityId++ : 0,
                        IsTrainer = isTrainer,
                        TrainerId = isTrainer ? _nextEntityId++ : 0,
                        TrainerSkills = isTrainer ? GetTrainerSkillList(npcData.gcType) : null,
                        IsBank = isBank,
                        BankComponentId = isBank ? _nextEntityId++ : 0,
                        IsPosseMagnate = isPosseMagnate,
                        PosseOptionComponentId = isPosseMagnate ? _nextEntityId++ : 0
                    };

                    _zoneNPCs[pvpZoneId].Add(npc);
                    Debug.LogError($"[InitPvpNPCs] ✓ Created NPC: {npc.Name} (ID: {npc.Id}){(isPvpNpc ? " [PVP-QUEUE]" : "")}");
                }
                Debug.LogError($"[InitPvpNPCs] ✅ Created {_zoneNPCs[pvpZoneId].Count} NPCs for pvp_start");
            }
        }

        private void SendZoneNPCs(RRConnection conn, uint zoneId)
        {
            if (VerbosePacketLogging) Debug.LogError("═══════════════════════════════════════════════════════════════════════════════");
            if (VerbosePacketLogging) Debug.LogError($"[SendZoneNPCs] ★★★★★ STARTING NPC SPAWN FOR ZONE {zoneId} ★★★★★");
            if (VerbosePacketLogging) Debug.LogError("═══════════════════════════════════════════════════════════════════════════════");

            if (!_zoneNPCs.TryGetValue(zoneId, out var npcs) || npcs.Count == 0)
            {
                Debug.LogError($"[SendZoneNPCs] ❌❌❌ NO NPCs FOUND FOR ZONE {zoneId} ❌❌❌");
                return;
            }

            if (VerbosePacketLogging) Debug.LogError($"[SendZoneNPCs] ✅ Found {npcs.Count} NPCs to spawn");
            if (VerbosePacketLogging) Debug.LogError($"[SendZoneNPCs] 🔥 Sending ALL NPCs in ONE packet");

            // ═══════════════════════════════════════════════════════════
            // CREATE ONE WRITER FOR ALL NPCs
            // ═══════════════════════════════════════════════════════════
            var writer = new LEWriter();
            writer.WriteByte(0x07);  // BeginStream ONCE at start
            if (VerbosePacketLogging) Debug.LogError($"[SendZoneNPCs] Wrote BeginStream (0x07) for ALL NPCs");

            int npcCounter = 0;
            foreach (var npc in npcs)
            {
                int npcStartPos = writer.Position;
                npcCounter++;
                if (VerbosePacketLogging) Debug.LogError("");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] ╔═══════════════════════════════════════════════════════════════════════════╗");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] ║ PROCESSING NPC #{npcCounter} OF {npcs.Count}");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] ╠═══════════════════════════════════════════════════════════════════════════╣");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] ║ Name:     {npc.Name}");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] ║ GCClass:  {npc.GCClass}");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] ║ Position: ({npc.PosX:F2}, {npc.PosY:F2}, {npc.PosZ:F2})");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] ║ Heading:  {npc.Heading:F2}");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] ╚═══════════════════════════════════════════════════════════════════════════╝");

                // IDs
                ushort npcId = (ushort)npc.Id;
                ushort behaviorId = (ushort)npc.UnitBehaviorId;
                // Track NPC position for range checks
                _npcPositions[npcId] = (npc.PosX, npc.PosY, npc.PosZ);
                _allEntityPositions[npcId] = (npc.PosX, npc.PosY, npc.PosZ);
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] 📍 Tracking position for entityId=0x{npcId:X4}");
                ushort skillsId = (ushort)_nextEntityId++;
                ushort manipulatorsId = (ushort)_nextEntityId++;
                ushort modifiersId = (ushort)_nextEntityId++;

                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] ┌─ ENTITY IDs ──────────────────────────────────────────────────────────┐");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] │ NPC ID:          0x{npcId:X4} ({npcId})");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] │ Behavior ID:     0x{behaviorId:X4} ({behaviorId})");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] │ Skills ID:       0x{skillsId:X4} ({skillsId})");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] │ Manipulators ID: 0x{manipulatorsId:X4} ({manipulatorsId})");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] │ Modifiers ID:    0x{modifiersId:X4} ({modifiersId})");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] └───────────────────────────────────────────────────────────────────────┘");

                string behaviorGCType = "npc.base.behavior";

                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] ┌─ GCTYPES ─────────────────────────────────────────────────────────────┐");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] │ Entity GCType:   '{npc.GCClass}' (preserveCase: true)");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] │ Behavior GCType: '{behaviorGCType}' (preserveCase: false)");
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] └───────────────────────────────────────────────────────────────────────┘");

                // ========== OP1: Create NPC Entity (0x01) ==========
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] 🔷 OPERATION 1: CREATE NPC ENTITY (0x01)");
                writer.WriteByte(0x01);
                writer.WriteUInt16(npcId);
                // Admin NPCs don't have .gc files on client - use display types
                string entityGcType = npc.GCClass;
                if (entityGcType.Contains("AdminWeaponVendor")) entityGcType = "world.town.npc.VendorWeapon1";
                else if (entityGcType.Contains("AdminArmorVendor")) entityGcType = "world.town.npc.VendorWeapon2";
                else if (entityGcType.Contains("AdminMiscVendor")) entityGcType = "world.town.npc.VendorWeapon3";
                uint npcHPWire = ResolveAuthoredUnitMaxHealthWire(entityGcType);
                WriteGCType(writer, entityGcType, preserveCase: true);

                // ========== OP2: Create Behavior Component (0x32) ==========
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] 🔷 OPERATION 2: CREATE BEHAVIOR COMPONENT (0x32)");
                writer.WriteByte(0x32);
                writer.WriteUInt16(npcId);
                writer.WriteUInt16(behaviorId);
                WriteGCType(writer, behaviorGCType, preserveCase: false);
                writer.WriteByte(0x01); // hasInit

                // Behavior::WriteInit (4 bytes)
                writer.WriteByte(0xFF);
                writer.WriteByte(0x00);
                writer.WriteByte(0x00);
                writer.WriteByte(0x01);

                // UnitMover::readInit (22 bytes)
                writer.WriteByte(0x85);
                writer.WriteByte(0x00);
                writer.WriteUInt32(0x00000000);
                writer.WriteUInt32(0x00000000);
                writer.WriteUInt32(0x00000000);
                writer.WriteUInt32(0x00000000);
                writer.WriteUInt32(0x00000000);

                writer.WriteByte(0x00);  // FLAGS BYTE

                // UnitBehavior::readInit (3 bytes)
                writer.WriteByte(0xFF);
                writer.WriteByte(0x00);
                writer.WriteByte(0x00);

                // MonsterBehavior2::readInit (10 bytes)
                writer.WriteByte(0x00);
                writer.WriteByte(0x00);
                writer.WriteUInt32(0x00000000);
                writer.WriteUInt32(0x00000000);

                // ========== OP3: Create Skills Component (0x32) ==========
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] 🔷 OPERATION 3: CREATE SKILLS COMPONENT (0x32)");
                writer.WriteByte(0x32);
                writer.WriteUInt16(npcId);
                writer.WriteUInt16(skillsId);
                WriteGCType(writer, "skills", preserveCase: false);
                writer.WriteByte(0x01);
                writer.WriteByte(0xFF);
                writer.WriteByte(0xFF);
                writer.WriteByte(0xFF);
                writer.WriteByte(0xFF);
                writer.WriteByte(0x00);
                writer.WriteByte(0x01);
                WriteGCType(writer, "skills.professions.Warrior", preserveCase: true);

                // ========== OP4: Create Manipulators Component (0x32) ==========
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] 🔷 OPERATION 4: CREATE MANIPULATORS COMPONENT (0x32)");
                writer.WriteByte(0x32);
                writer.WriteUInt16(npcId);
                writer.WriteUInt16(manipulatorsId);
                WriteGCType(writer, "manipulators", preserveCase: false);
                writer.WriteByte(0x01);
                writer.WriteByte(0x00);

                // ========== OP5: Create Modifiers Component (0x32) ==========
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] 🔷 OPERATION 5: CREATE MODIFIERS COMPONENT (0x32)");
                writer.WriteByte(0x32);
                writer.WriteUInt16(npcId);
                writer.WriteUInt16(modifiersId);
                WriteGCType(writer, "modifiers", preserveCase: false);
                writer.WriteByte(0x01);
                writer.WriteUInt32(0x00000000);
                writer.WriteByte(0x00);
                writer.WriteUInt32(0x00000000);

                // ========== OP5b: Create Merchant Component (0x32) - IF MERCHANT ==========
                if (npc.IsMerchant)
                {
                    // ItemTimeline: regenerate items for this player's level
                    int playerLevel = GetPlayerState(conn.ConnId.ToString()).Level;
                    MerchantManager.EnsureInventoryForLevel(npc.GCClass, playerLevel);

                    int merchantStart = writer.Position;
                    if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] 🔷 CREATING MERCHANT COMPONENT (startPos={merchantStart})");
                    MerchantManager.WriteMerchantComponent(writer, npc.GCClass, npcId, (ushort)npc.MerchantId);
                    if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] 🔷 MERCHANT DONE (endPos={writer.Position}, bytes={writer.Position - merchantStart})");
                }

                // ========== OP5c: Create SkillTrainer Component (0x32) - IF TRAINER ==========
                if (npc.IsTrainer)
                {
                    if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] 🔷 CREATING SKILLTRAINER COMPONENT (trainerId={npc.TrainerId})");
                    writer.WriteByte(0x32);
                    writer.WriteUInt16(npcId);
                    writer.WriteUInt16((ushort)npc.TrainerId);
                    // Derive NPC-specific SkillTrainer GCType from drcategories:
                    //   world.town.npc.TrainerFighter → world.town.npc.base.TrainerFighterBase.SkillTrainer
                    int lastDot = npc.GCClass.LastIndexOf('.');
                    string gcPrefix = npc.GCClass.Substring(0, lastDot);
                    string npcName = npc.GCClass.Substring(lastDot + 1);
                    string skillTrainerGcType = gcPrefix + ".base." + npcName + "Base.SkillTrainer";
                    if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] 🔷 SkillTrainer GCType: {skillTrainerGcType}");
                    WriteGCType(writer, skillTrainerGcType, preserveCase: true);
                    writer.WriteByte(0x00); // hasInit = false → client reads available skills from GC
                    if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] 🔷 SKILLTRAINER DONE (trainerId=0x{npc.TrainerId:X4})");
                }

                // ========== OP5d: Create Bank Component (0x32) - IF BANK ==========
                // GCType "banker" sourced from DungeonRunners.exe strings table (offset 4798220,
                // paired with "Merchant" at 4798452). hasInit=0x00 mirrors trainer pattern since
                // the player's avatar.base.Bank container is already sent in OP8 UNITCONTAINER.
                // Probe 1 with "bank" caused Zone error 10; "banker" is the corrected guess.
                if (npc.IsBank)
                {
                    Debug.LogError($"[NPC-{npcCounter}] 🔷 CREATING BANK COMPONENT (bankId={npc.BankComponentId})");
                    writer.WriteByte(0x32);
                    writer.WriteUInt16(npcId);
                    writer.WriteUInt16((ushort)npc.BankComponentId);
                    WriteGCType(writer, "banker", preserveCase: false);
                    writer.WriteByte(0x00); // hasInit = false (speculative; iterate if rejected)
                    Debug.LogError($"[NPC-{npcCounter}] 🔷 BANK DONE (bankId=0x{npc.BankComponentId:X4})");
                }

                // ========== OP5e: Posse option component — PROBE 3 ==========
                // The client registers 8 Posse* classes via DFCKernel::registerClass + a
                // matching create<Name> factory (mirroring _register_class_Banker / createBanker
                // at 0x0059A910). Of those, only "PosseRegistry" is the bare-noun analog of
                // "Banker" / "Merchant" — every other Posse* is a UI class (Dialog/Panel/Control).
                // Probes attempted previously:
                //   "PosseRegistryOption" (preserveCase) — UI class name, Zone Error 10
                //   "posse" (lowercase)                  — bare noun, Zone Error 10
                // This probe writes "PosseRegistry" via the same path as banker (WriteGCType
                // lowercases to "posseregistry" — DFCKernel::registerClass is case-insensitive).
                if (npc.IsPosseMagnate)
                {
                    Debug.LogError($"[POSSE] CREATING POSSE COMPONENT (cid={npc.PosseOptionComponentId}) for {npc.GCClass}");
                    writer.WriteByte(0x32);
                    writer.WriteUInt16(npcId);
                    writer.WriteUInt16((ushort)npc.PosseOptionComponentId);
                    WriteGCType(writer, "PosseRegistry", preserveCase: false);
                    writer.WriteByte(0x00);
                    Debug.LogError($"[POSSE] POSSE COMPONENT DONE (cid=0x{npc.PosseOptionComponentId:X4})");
                }

                // ========== OP6: Init NPC Entity (0x02) ==========




                // ========== OP6: Init NPC Entity (0x02) ==========
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] 🔷 OPERATION 6: INIT NPC ENTITY (0x02)");
                writer.WriteByte(0x02);
                writer.WriteUInt16(npcId);
                writer.WriteUInt32(0x06);

                int posX = (int)(npc.PosX * 256);
                int posY = (int)(npc.PosY * 256);
                int posZ = (int)(npc.PosZ * 256);
                int heading = (int)(npc.Heading * 256);

                writer.WriteInt32(posX);
                writer.WriteInt32(posY);
                writer.WriteInt32(posZ);
                writer.WriteInt32(heading);
                writer.WriteByte(0x00);
                writer.WriteByte(0x01);

                for (int i = 0; i < 8; i++)
                    writer.WriteUInt32(0x00000000);

                // ========== OP7: WarpTo (0x35) ==========
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] 🔷 OPERATION 7: WARP TO POSITION (0x35)");
                writer.WriteByte(0x35);
                writer.WriteUInt16(behaviorId);
                writer.WriteByte(0x04);
                writer.WriteByte(0x11);      // WarpTo
                writer.WriteByte(0x00);      // SessionID

                writer.WriteInt32(posX);
                writer.WriteInt32(posY);
                writer.WriteInt32(posZ);

                // WriteSynch (5 bytes)
                writer.WriteByte(0x02);// Flag
                                       // writer.WriteUInt32(0x00000000);  // Synch value
                writer.WriteUInt32(npcHPWire);
                if (VerbosePacketLogging) Debug.LogError($"[NPC-{npcCounter}] ✅ NPC OPERATIONS WRITTEN TO BATCH (startPos={npcStartPos}, endPos={writer.Position}, bytes={writer.Position - npcStartPos}, isMerchant={npc.IsMerchant}, isTrainer={npc.IsTrainer})");
            }

            // ═══════════════════════════════════════════════════════════
            // END STREAM ONCE AFTER ALL NPCs
            // ═══════════════════════════════════════════════════════════
            writer.WriteByte(0x06);  // EndStream ONCE at end
            if (VerbosePacketLogging) Debug.LogError($"[SendZoneNPCs] Wrote EndStream (0x06) for ALL NPCs");

            byte[] packetData = writer.ToArray();
            int totalPacketSize = packetData.Length;
            if (VerbosePacketLogging) Debug.LogError($"[SendZoneNPCs] 📦 COMPLETE BATCH PACKET: {totalPacketSize} bytes for {npcs.Count} NPCs");
            if (VerbosePacketLogging) Debug.LogError($"[SendZoneNPCs] Packet hex (first 200 bytes): {BitConverter.ToString(packetData, 0, Math.Min(200, packetData.Length))}");

            // ═══════════════════════════════════════════════════════════
            // SEND ONCE
            // ═══════════════════════════════════════════════════════════
            if (VerbosePacketLogging) Debug.LogError($"[SendZoneNPCs] 🚀 SENDING COMPLETE BATCH PACKET...");
            SendCompressedA(conn, 0x01, 0x0f, packetData);
            if (VerbosePacketLogging) Debug.LogError($"[SendZoneNPCs] ✅ BATCH PACKET SENT!");

            if (VerbosePacketLogging) Debug.LogError("");
            if (VerbosePacketLogging) Debug.LogError("═══════════════════════════════════════════════════════════════════════════════");
            if (VerbosePacketLogging) Debug.LogError($"[SendZoneNPCs] ✅ ALL {npcs.Count} NPCs SENT IN ONE BATCH");
            if (VerbosePacketLogging) Debug.LogError("═══════════════════════════════════════════════════════════════════════════════");
        }






        /// //////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////





        /// <summary>
        /// Handle incoming GroupClient messages from the client on channel 0x0B.
        /// Binary: GroupClient::sendMessage(byte) and sendMessage(byte, msg)
        /// Binary: Client sends invite/accept/decline/leave/kick/leader/resetInstances/difficulty
        /// 
        /// The client sends commands, server processes and responds with processXxx packets.
        /// Client-to-server type values may differ from server-to-client type values.
        /// Logging everything so we can map exact types from testing.
        /// </summary>
        private void HandleGroupClientChannel(RRConnection conn, byte messageType, byte[] data)
        {
            string hex = data != null ? BitConverter.ToString(data, 0, Math.Min(data.Length, 60)) : "null";
            Debug.LogError($"[GROUP-CH0B] ★ RECEIVED from {conn.LoginName}: type=0x{messageType:X2} len={data?.Length ?? 0} hex={hex}");

            var reader = (data != null && data.Length > 0) ? new LEReader(data) : null;

            try
            {
                switch (messageType)
                {
                    // ── Binary-verified wire types (PDB + disasm of GroupClient::sendMessage calls) ──
                    // inviteUser(CharSQLID) sends 0x12, inviteUserByCharacterName sends 0x16
                    // removeUser sends 0x14, setLeader sends 0x15, setMonsterDifficulty sends 0x17
                    // acceptInvite sends 0x20, declineInvite sends 0x21, leaveGroup sends 0x22
                    // setPublic sends 0x24, resetInstances sends 0x26, gotoMember sends 0x27
                    // setInviteMode sends 0x28

                    case 0x16: // inviteUserByCharacterName — data: [string charName\0]
                        {
                            if (reader == null) { Debug.LogError("[GROUP-CH0B] 0x16 no data"); break; }
                            string targetName = reader.ReadCString();
                            Debug.LogError($"[GROUP-CH0B] INVITE BY NAME '{targetName}' from {conn.LoginName}");
                            var target = FindConnectionByName(targetName);
                            if (target == null)
                            {
                                Debug.LogError($"[GROUP-CH0B] Target '{targetName}' not found online");
                                break;
                            }

                            // Create group if inviter not in one
                            if (!GroupManager.Instance.IsInGroup(conn.ConnId))
                            {
                                GroupManager.Instance.CreateGroup(conn.ConnId, conn.LoginName, conn.LoginName);
                                // Send group state to inviter immediately so GroupClient initializes
                                var newGroup = GroupManager.Instance.GetGroupForConn(conn.ConnId);
                                if (newGroup != null)
                                    SendGroupConnectedToAll(newGroup);
                            }

                            if (GroupManager.Instance.InvitePlayer(conn.ConnId, target.ConnId))
                            {
                                // Send processInvitation (0x32) to TARGET — shows GroupInviteDialog
                                // Binary: processInvitation @ 0x5F8210 reads:
                                //   uint32 inviteId → GC+0xF0 (client sends this back in accept/decline)
                                //   uint32 groupId  → GC+0xF4
                                //   string name     → GC+0xF8
                                //   byte flags      → GC+0xFC
                                var group = GroupManager.Instance.GetGroupForConn(conn.ConnId);
                                uint inviterCharId = 0;
                                string inviterName = conn.LoginName;
                                if (_selectedCharacter.TryGetValue(conn.LoginName, out var inviterChar))
                                {
                                    inviterCharId = (uint)inviterChar.Id;
                                    inviterName = inviterChar.Name ?? inviterName;
                                }
                                byte[] invitePacket = GroupPackets.BuildProcessInvitation(
                                    inviterCharId, group.GroupId, inviterName, 0x00);
                                SendToClient(target, invitePacket);
                                Debug.LogError($"[GROUP] Sent processInvitation(0x32) to {target.LoginName}: inviteId=0x{inviterCharId:X8} groupId={group.GroupId} name='{inviterName}'");

                                // Send processInviteResult (0x34) to INVITER — "Invite sent to X"
                                // BLOCKED(native-group-result-wire): build 0x34 invite result after packet order is closed.
                                SendSystemMessage(conn, $"Invite sent to {targetName}.");
                            }
                            break;
                        }

                    case 0x12: // inviteUser (by CharSQLID) — data: [uint32 charSQLID]
                        {
                            if (reader == null) { Debug.LogError("[GROUP-CH0B] 0x12 no data"); break; }
                            uint targetId = reader.ReadUInt32();
                            Debug.LogError($"[GROUP-CH0B] INVITE BY ID 0x{targetId:X8} from {conn.LoginName}");
                            // BLOCKED(native-group-charSQLID-scope): resolve charSQLID invite scope before enabling.
                            break;
                        }

                    case 0x20: // acceptInvite — data: [uint32 inviteId]
                        {
                            uint inviteId = reader?.ReadUInt32() ?? 0;
                            Debug.LogError($"[GROUP-CH0B] ACCEPT invite (inviteId=0x{inviteId:X8}) from {conn.LoginName}");
                            var group = GroupManager.Instance.AcceptInvite(conn.ConnId, conn.LoginName, conn.LoginName);
                            if (group != null)
                            {
                                // Send processAddUser (0x42) to existing members
                                var newMemberInfo = BuildGroupMemberInfo(conn);
                                foreach (var m in group.Members)
                                {
                                    if (m.ConnId == conn.ConnId) continue;
                                    var mc = FindConnectionById(m.ConnId);
                                    if (mc != null)
                                        SendToClient(mc, GroupPackets.BuildProcessAddUser(group.GroupId, newMemberInfo));
                                }
                                // Full state to new member (0x30 once + 0x35)
                                SendGroupConnectedToAll(group);
                                SendGroupHealthToAll(group);
                                if (group.IsOpen)
                                    SocialManager.Instance.PushWhoListToAll(SendSocialViaAuth);
                                Debug.LogError($"[GROUP] Group {group.GroupId} formed with {group.Members.Count} members");
                            }
                            break;
                        }

                    case 0x21: // declineInvite — data: [uint32 inviteId]
                        {
                            uint inviteId = reader?.ReadUInt32() ?? 0;
                            Debug.LogError($"[GROUP-CH0B] DECLINE invite (inviteId=0x{inviteId:X8}) from {conn.LoginName}");
                            GroupManager.Instance.DeclineInvite(conn.ConnId);
                            // BLOCKED(native-group-decline-result): send 0x34 decline only after inviter scope/order is closed.
                            break;
                        }

                    case 0x22: // leaveGroup — no data (onLeaveGroup sends 0x22 via sendMessage(byte))
                        {
                            Debug.LogError($"[GROUP-CH0B] LEAVE group from {conn.LoginName}");
                            var leaveGroup = GroupManager.Instance.GetGroupForConn(conn.ConnId);
                            bool wasLeader = (leaveGroup != null && leaveGroup.LeaderConnId == conn.ConnId);
                            SendGroupRemoveUser(conn);
                            GroupManager.Instance.LeaveGroup(conn.ConnId);
                            conn.GroupConnectedSent = false;

                            // Send solo 0x35 to leaver — clears their group UI
                            byte[] leaverSolo = GroupPackets.BuildProcessUserChangedGroup(
                                1, conn.CurrentZoneId, 0xFF, 0, 0, 0, 0, new GroupMemberInfo[0]);
                            SendCompressedA(conn, 0x01, 0x0F, leaverSolo);

                            // Update remaining members with new group state
                            if (leaveGroup != null && leaveGroup.Members.Count > 0)
                            {
                                if (leaveGroup.Members.Count == 1)
                                {
                                    var lastConn = FindConnectionById(leaveGroup.Members[0].ConnId);
                                    if (lastConn != null)
                                    {
                                        byte[] soloState = GroupPackets.BuildProcessUserChangedGroup(
                                            1, lastConn.CurrentZoneId, 0xFF, 0, 0, 0, 0, new GroupMemberInfo[0]);
                                        SendCompressedA(lastConn, 0x01, 0x0F, soloState);
                                        lastConn.GroupConnectedSent = false;
                                    }
                                    GroupManager.Instance.LeaveGroup(leaveGroup.Members[0].ConnId);
                                }
                                else
                                {
                                    // Leader left → GC+0xB0 must update to new leader's zoneGUID
                                    if (wasLeader)
                                    {
                                        foreach (var rm in leaveGroup.Members)
                                        {
                                            var rmc = FindConnectionById(rm.ConnId);
                                            if (rmc != null) rmc.GroupConnectedSent = false;
                                        }
                                    }
                                    SendGroupConnectedToAll(leaveGroup);
                                }
                            }
                            SocialManager.Instance.PushWhoListToAll(SendSocialViaAuth);
                            break;
                        }

                    case 0x14: // removeUser/kick — data: [uint32 charSQLID]
                        {
                            if (reader == null) break;
                            uint kickId = reader.ReadUInt32();
                            Debug.LogError($"[GROUP-CH0B] KICK member 0x{kickId:X8} from {conn.LoginName}");
                            var group = GroupManager.Instance.GetGroupForConn(conn.ConnId);
                            if (group == null || group.LeaderConnId != conn.ConnId) break;
                            // Find target by charSqlId
                            RRConnection kickTarget = FindGroupMemberByCharSqlId(group, kickId);
                            if (kickTarget == null || kickTarget.ConnId == conn.ConnId) break;
                            // Send remove to all members
                            byte[] kickPacket = GroupPackets.BuildProcessRemoveUser(group.GroupId, kickId);
                            foreach (var m in group.Members)
                            {
                                var mc = FindConnectionById(m.ConnId);
                                if (mc != null) SendToClient(mc, kickPacket);
                            }
                            GroupManager.Instance.LeaveGroup(kickTarget.ConnId);
                            kickTarget.GroupConnectedSent = false;
                            // Solo 0x35 to kicked player
                            byte[] kickSolo = GroupPackets.BuildProcessUserChangedGroup(
                                1, kickTarget.CurrentZoneId, 0xFF, 0, 0, 0, 0, new GroupMemberInfo[0]);
                            SendCompressedA(kickTarget, 0x01, 0x0F, kickSolo);
                            // Update remaining
                            if (group.Members.Count == 1)
                            {
                                var lastConn = FindConnectionById(group.Members[0].ConnId);
                                if (lastConn != null)
                                {
                                    byte[] soloState = GroupPackets.BuildProcessUserChangedGroup(
                                        1, lastConn.CurrentZoneId, 0xFF, 0, 0, 0, 0, new GroupMemberInfo[0]);
                                    SendCompressedA(lastConn, 0x01, 0x0F, soloState);
                                    lastConn.GroupConnectedSent = false;
                                }
                                GroupManager.Instance.LeaveGroup(group.Members[0].ConnId);
                            }
                            else
                            {
                                SendGroupConnectedToAll(group);
                            }
                            SocialManager.Instance.PushWhoListToAll(SendSocialViaAuth);
                            Debug.LogError($"[GROUP] Kicked charSqlId=0x{kickId:X8} from group {group.GroupId}");
                            break;
                        }

                    case 0x15: // setLeader — data: [uint32 charSQLID]
                        {
                            if (reader == null) break;
                            uint newLeaderId = reader.ReadUInt32();
                            Debug.LogError($"[GROUP-CH0B] SET LEADER 0x{newLeaderId:X8} from {conn.LoginName}");
                            var group = GroupManager.Instance.GetGroupForConn(conn.ConnId);
                            if (group == null || group.LeaderConnId != conn.ConnId) break;
                            // Find new leader by charSqlId
                            RRConnection newLeaderConn = FindGroupMemberByCharSqlId(group, newLeaderId);
                            if (newLeaderConn == null) break;
                            int newLeaderConnId = newLeaderConn.ConnId;
                            GroupManager.Instance.SetLeader(conn.ConnId, newLeaderConnId);
                            // Reset GroupConnectedSent — new leader means new zoneGUID for GC+0xB0
                            // SendGroupConnectedToAll sends 0x30 → 0x35 → 0x44 (leader only)
                            foreach (var m in group.Members)
                            {
                                var mc = FindConnectionById(m.ConnId);
                                if (mc != null)
                                    mc.GroupConnectedSent = false;
                            }
                            SendGroupConnectedToAll(group);
                            SocialManager.Instance.PushWhoListToAll(SendSocialViaAuth);
                            Debug.LogError($"[GROUP] Leadership transferred to conn={newLeaderConnId}");
                            break;
                        }

                    case 0x17: // setMonsterDifficulty — data: [byte difficulty]
                        {
                            if (reader == null) break;
                            byte diff = reader.ReadByte();
                            Debug.LogError($"[GROUP-CH0B] SET DIFFICULTY {diff} from {conn.LoginName}");
                            bool personalOnly;
                            if (!GroupManager.Instance.SetMonsterDifficulty(conn.ConnId, diff, out personalOnly))
                                break;
                            var group = GroupManager.Instance.GetGroupForConn(conn.ConnId);
                            if (group != null)
                            {
                                byte[] diffPacket = GroupPackets.BuildMonsterDifficulty(diff, personalOnly);
                                if (personalOnly)
                                {
                                    SendToClient(conn, diffPacket);
                                }
                                else
                                {
                                    foreach (var m in group.Members)
                                    {
                                        var mc = FindConnectionById(m.ConnId);
                                        if (mc != null) SendToClient(mc, diffPacket);
                                    }
                                }
                            }
                            break;
                        }

                    case 0x24: // setOpenGroup — data: [byte flag]
                        // Binary-verified: setOpenGroup@0x5F79A0 → push 0x24 → sendMessage@0x5FA8C0
                        {
                            if (reader == null) break;
                            byte flag = reader.ReadByte();
                            Debug.LogError($"[GROUP-CH0B] SET OPEN GROUP flag={flag} from {conn.LoginName}");
                            var group = GroupManager.Instance.GetGroupForConn(conn.ConnId);
                            if (group != null)
                            {
                                group.IsOpen = (flag != 0);
                                SendGroupConnectedToAll(group);
                                SocialManager.Instance.PushWhoListToAll(SendSocialViaAuth);
                            }
                            break;
                        }

                    case 0x26: // resetInstances — no data
                        {
                            Debug.LogError($"[GROUP-CH0B] RESET INSTANCES from {conn.LoginName}");
                            GroupManager.Instance.ResetInstances(conn.ConnId);
                            SendSystemMessage(conn, "Dungeon instances reset.");
                            break;
                        }

                    case 0x28: // setInviteMode — data: [byte mode]
                        // Binary-verified: setInviteMode@0x5F6C60 → push 0x28 → sendMessage@0x5FA8C0
                        {
                            if (reader == null) break;
                            byte mode = reader.ReadByte();
                            Debug.LogError($"[GROUP-CH0B] SET INVITE MODE {mode} from {conn.LoginName}");
                            var group = GroupManager.Instance.GetGroupForConn(conn.ConnId);
                            if (group != null)
                                group.InviteMode = mode;
                            SendToClient(conn, GroupPackets.BuildChangedInviteMode(mode));
                            break;
                        }

                    case 0x27: // gotoMember — data: [uint32 charSQLID]
                        {
                            if (reader == null) break;
                            uint gotoId = reader.ReadUInt32();
                            Debug.LogError($"[GROUP-CH0B] GOTO member 0x{gotoId:X8} from {conn.LoginName}");
                            var group = GroupManager.Instance.GetGroupForConn(conn.ConnId);
                            if (group == null) break;
                            RRConnection gotoTarget = FindGroupMemberByCharSqlId(group, gotoId);
                            if (gotoTarget == null || gotoTarget.ConnId == conn.ConnId) break;
                            string targetZone = gotoTarget.CurrentZoneName ?? "tutorial";
                            float gotoX = gotoTarget.PlayerPosX;
                            float gotoY = gotoTarget.PlayerPosY;
                            float gotoZ = gotoTarget.PlayerPosZ;
                            Debug.LogError($"[GROUP] GoTo: {conn.LoginName} → {gotoTarget.LoginName} at {targetZone} ({gotoX},{gotoY},{gotoZ})");
                            ChangeZoneToPosition(conn, targetZone, gotoX, gotoY, gotoZ);
                            break;
                        }

                    // ── PVP OPCODES (Binary-verified from GroupClient::sendMessage calls) ──
                    case 0x29: // enterPVPZone — no payload
                        Debug.LogError($"[PVP] {conn.LoginName} entering PVP hub zone");
                        HandleEnterPvpZone(conn);
                        break;

                    case 0x2A: // requestPVPMatch — payload: gctype string of match archetype
                        Debug.LogError($"[PVP] {conn.LoginName} requesting PVP match");
                        HandleRequestPvpMatch(conn, data);
                        break;

                    case 0x2B: // cancelPVPMatch — no payload
                        Debug.LogError($"[PVP] {conn.LoginName} cancelling PVP queue");
                        HandleCancelPvpMatch(conn);
                        break;

                    case 0x2C: // leavePVP — no payload
                        Debug.LogError($"[PVP] {conn.LoginName} leaving PVP system");
                        HandleLeavePvp(conn);
                        break;

                    case 0x2D: // requestPVPDuel — data: [uint32 targetCharSqlId]
                        {
                            if (reader == null || data.Length < 4) { Debug.LogError("[PVP] 0x2D no data"); break; }
                            uint targetCharSqlId = reader.ReadUInt32();
                            Debug.LogError($"[PVP-DUEL] {conn.LoginName} requests duel with CharSQLID {targetCharSqlId}");
                            HandleDuelRequest(conn, targetCharSqlId);
                            break;
                        }

                    case 0x2E: // acceptPVPDuel — no payload
                        {
                            Debug.LogError($"[PVP-DUEL] {conn.LoginName} accepts duel");
                            HandleDuelAccept(conn);
                            break;
                        }

                    case 0x2F: // declinePVPDuel — no payload
                        {
                            Debug.LogError($"[PVP-DUEL] {conn.LoginName} declines duel");
                            HandleDuelDecline(conn);
                            break;
                        }

                    default:
                        Debug.LogError($"[GROUP-CH0B] UNKNOWN type 0x{messageType:X2} — LOG FOR MAPPING");
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GROUP-CH0B] Error handling type 0x{messageType:X2}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        // PVP DUEL SYSTEM (Phase 1)
        // Opcodes: 0x2D request, 0x2E accept, 0x2F decline
        // Response packets: PVPPackets.BuildDuelStatus (type 0x4F)
        // ══════════════════════════════════════════════════════════════
        private readonly Managers.DuelManager _duelManager = new Managers.DuelManager();
    }
}
