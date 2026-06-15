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
        private Dictionary<int, uint> _pendingZoneConnectSeeds = new Dictionary<int, uint>();
        private Dictionary<string, uint> _zoneInstanceLayoutSeeds = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, uint> _zoneInstanceRoomSeeds = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, uint> _soloDungeonInstanceIds = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, DateTime> _soloDungeonLastActiveUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private const double SoloDungeonMemorySeconds = 10 * 60;
        private uint _nextSoloDungeonInstanceId = 0x80000000u;
        private HashSet<string> _loggedRuntimeZoneSeeds = new HashSet<string>();
        private Dictionary<uint, Zone> _zones = new Dictionary<uint, Zone>(); // ✅ ADDED
        private Dictionary<uint, List<ZoneNPC>> _zoneNPCs = new Dictionary<uint, List<ZoneNPC>>();
        private Dictionary<uint, List<ZonePortal>> _zonePortals = new Dictionary<uint, List<ZonePortal>>();
        private Dictionary<uint, List<ZoneCheckpoint>> _zoneCheckpoints = new Dictionary<uint, List<ZoneCheckpoint>>();
        private Dictionary<ushort, ZonePortal> _portalEntities = new Dictionary<ushort, ZonePortal>();
        private Dictionary<ushort, ZoneCheckpoint> _checkpointEntities = new Dictionary<ushort, ZoneCheckpoint>();
        private HashSet<ushort> _activatedWorldEntities = new HashSet<ushort>();
        private Dictionary<int, List<ZoneNPC>> _adminShopNPCs = new Dictionary<int, List<ZoneNPC>>();

        // ═══════════════════════════════════════════════════════════════════
        // Quest goto-objective metadata parsed from original DungeonRunners GC
        // files (Q*.gc). Maps the in-game objective Label to its target zone,
        // target entity/waypoint name, and proximity range in world units.
        //
        // Used by CheckGotoProximity() and CompleteGotoObjectivesOnZoneEntry()
        // to replicate the original server's goto-completion logic.
        //
        // 50 entries, generated from full GC parse.
        // ═══════════════════════════════════════════════════════════════════
        private static readonly Dictionary<string, GotoGCData> _gotoGCByLabel =
            new Dictionary<string, GotoGCData>(StringComparer.OrdinalIgnoreCase)
        {
            { "Go to Dew Valley", new GotoGCData("Tutorial", "", 0) },
            { "Algor's Terror-Dome Base Camp", new GotoGCData("dungeon02_level00", "", 0) },
            { "Algor's Terror-Dome", new GotoGCData("dungeon02_level01", "", 0) },
            { "Explore Subterrorania", new GotoGCData("dungeon02_level01_off1a", "", 0) },
            { "Explore Arborgeddon", new GotoGCData("dungeon02_level03_off1a", "", 0) },
            { "Explore Haterium", new GotoGCData("dungeon02_level04_off1a", "", 0) },
            { "Explore Grave Forge", new GotoGCData("dungeon02_level05_off1a", "", 0) },
            { "Explore Plague Plant", new GotoGCData("dungeon02_level07_off1a", "", 0) },
            { "Explore Abyss Mill", new GotoGCData("dungeon02_level07_off2a", "", 0) },
            { "Locate Entrance to Rattle Tooth's Lair", new GotoGCData("dungeon00_level03", "boss_spawn", 200) },
            { "Find the Townston Commander", new GotoGCData("town", "Guard_Commander", 150) },
            { "Locate the Farm on Level 1", new GotoGCData("dungeon15_level01", "farm", 50) },
            { "Locate the Farm on Level 1 Branch", new GotoGCData("dungeon15_level01_off1a", "farm", 50) },
            { "Locate the Farm on Level 2", new GotoGCData("dungeon15_level02", "farm", 50) },
            { "Locate the Farm on Level 3", new GotoGCData("dungeon15_level03", "farm", 50) },
            { "Locate the Farm on Level 3 Branch", new GotoGCData("dungeon15_level03_off1a", "farm", 50) },
            { "Locate the Farm on Level 4", new GotoGCData("dungeon15_level04", "farm", 50) },
            { "Locate the Farm on Level 4 Branch", new GotoGCData("dungeon15_level04_off1a", "farm", 50) },
            { "Farm on Level 1", new GotoGCData("dungeon15_level01", "farm", 50) },
            { "Farm on Level 1 Branch", new GotoGCData("dungeon15_level01_off1a", "farm", 50) },
            { "Farm on Level 2", new GotoGCData("dungeon15_level02", "farm", 50) },
            { "Located the ''Mutant Operations Center''", new GotoGCData("dungeon01_level05", "off1", 150) },
            { "Explored the ''Mutant Operations Center''", new GotoGCData("dungeon01_level05_off1a", "level05_off1a_teleporter", 150) },
            { "Located the ''Rumored Refinery''", new GotoGCData("dungeon01_level04", "off1", 150) },
            { "Explore the ''Rumored Refinery''", new GotoGCData("dungeon01_level04_off1a", "level04_off1a_teleporter", 150) },
            { "Locate Sissirat's Revenge", new GotoGCData("dungeon15_level04_off1a_lair", "", 0) },
            { "Locate Vergrim's Revenge", new GotoGCData("dungeon15_level07_off1a_lair", "", 0) },
            { "Survey the Orok Outpost", new GotoGCData("dungeon01_level01_off1a", "", 0) },
            { "Survey Capwn's Family Ratstaurant", new GotoGCData("dungeon01_level03_off1a", "", 0) },
            { "Survey Not the Rumored Refinery", new GotoGCData("dungeon01_level04_off1a", "", 0) },
            { "Survey Not the Mutant Operation Center", new GotoGCData("dungeon01_level05_off1a", "", 0) },
            { "Survey Little Dew Valley Forest", new GotoGCData("dungeon01_level07_off1a", "", 0) },
            { "Survey Porthole Laboratories", new GotoGCData("dungeon01_level07_off2a", "", 0) },
            { "Go to Townston", new GotoGCData("Town", "", 0) },
        };
        private const float ZoneSpawnInvulnerabilityTicksPerSecond = 30f;
        private const float ZoneSpawnInvulnerabilityAddDelay = 0f;

        private IEnumerator SendZoneSpawnInvulnerabilityAfterDelay(RRConnection conn, float delay)
        {
            if (delay > 0f)
                yield return new WaitForSeconds(delay);
            if (conn == null || !conn.IsConnected || !conn.IsSpawned) yield break;
            SendZoneSpawnInvulnerability(conn);
        }

        private void SetZoneSpawnInvulnerability(RRConnection conn)
        {
            if (conn == null) return;
            conn.ZoneSpawnInvulnerabilityActive = true;
            conn.ZoneSpawnInvulnerabilityExpiresAt = Time.time + ZoneSpawnInvulnerabilityDuration / ZoneSpawnInvulnerabilityTicksPerSecond;
            conn.ZoneSpawnInvulnerabilitySentAt = Time.time;
            conn.ZoneSpawnInvulnerabilityClearFrame = -1;
            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            if (state != null)
                state.IsZoneSpawnDamageImmune = true;
            Debug.LogError($"[ZONE-INVULN] Server immunity active for {conn.LoginName} until={conn.ZoneSpawnInvulnerabilityExpiresAt:F3}");
        }

        private void RefreshZoneSpawnInvulnerability(RRConnection conn)
        {
            if (conn == null || !conn.ZoneSpawnInvulnerabilityActive) return;
            if (conn.ZoneSpawnInvulnerabilityExpiresAt > 0f && Time.time >= conn.ZoneSpawnInvulnerabilityExpiresAt)
                ClearZoneSpawnInvulnerability(conn, "EXPIRE");
        }

        private bool IsZoneSpawnInvulnerabilityBlockingCombat(RRConnection conn)
        {
            if (conn == null) return false;
            RefreshZoneSpawnInvulnerability(conn);
            return conn.ZoneSpawnInvulnerabilityActive;
        }

        private void ClearZoneSpawnInvulnerability(RRConnection conn, string reason)
        {
            if (conn == null) return;
            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            bool had = conn.ZoneSpawnInvulnerabilityActive || (state != null && state.IsZoneSpawnDamageImmune);
            if (!had) return;
            conn.ZoneSpawnInvulnerabilityActive = false;
            conn.ZoneSpawnInvulnerabilityExpiresAt = 0f;
            conn.ZoneSpawnInvulnerabilitySentAt = 0f;
            conn.ZoneSpawnInvulnerabilityClearFrame = Time.frameCount;
            if (state != null)
                state.IsZoneSpawnDamageImmune = false;
            Debug.LogError($"[ZONE-INVULN] Cleared server immunity for {conn.LoginName} reason={reason}");
        }

        private bool ShouldSendZoneSpawnInvulnerability(RRConnection conn)
        {
            if (conn == null) return false;

            string zoneName = conn.CurrentZoneName;
            if (string.IsNullOrWhiteSpace(zoneName) && conn.CurrentZoneId != 0 && _zones.TryGetValue(conn.CurrentZoneId, out Zone zone))
                zoneName = zone.name;

            return ZoneAllowsSpawnInvulnerability(zoneName);
        }

        private static bool ZoneAllowsSpawnInvulnerability(string zoneName)
        {
            zoneName = (zoneName ?? "").Trim().ToLowerInvariant();
            if (zoneName.Length == 0) return false;

            if (zoneName == "tutorial") return false;
            if (zoneName == "world.tutorial") return false;
            if (zoneName == "town") return false;
            if (zoneName == "world.town") return false;
            if (zoneName == "thehub") return false;
            if (zoneName == "world.thehub") return false;
            if (zoneName == "pvp_start") return false;
            if (zoneName == "pvp_hub") return false;
            if (zoneName.StartsWith("town")) return false;
            if (zoneName.StartsWith("world.town")) return false;
            if (zoneName.Contains("hub")) return false;

            if (zoneName == "amazon_dungeon") return true;
            if (zoneName.StartsWith("dungeon")) return true;
            if (zoneName.StartsWith("world.dungeon")) return true;
            if (zoneName.StartsWith("d0")) return true;
            if (zoneName.StartsWith("d1")) return true;
            if (zoneName.StartsWith("elite")) return true;
            if (zoneName.StartsWith("epic")) return true;
            if (zoneName.StartsWith("squeakeasy")) return true;
            if (zoneName.StartsWith("deathmatch")) return true;
            if (zoneName.StartsWith("pvpgroup")) return true;
            if (zoneName.StartsWith("pvpduel")) return true;

            return false;
        }

        // Admin "CompleteQuest <instanceId>" cheat handler.
        // Wired up in SetServerCallbacks; called by AdminCommandHandler when the client
        // sends the "CompleteQuest %u" string on channel 9 (binary: 0x46bbb0).
        // Mirrors the regular turn-in flow at the 0x05 turn-in handler so it produces
        // exactly the same client-visible result: items removed, quest removed from log,
        // rewards (XP/gold) applied, NPC markers refreshed.
        private void AdminCompleteQuest(RRConnection conn, uint instanceId)
        {
            try
            {
                var completingQuest = QuestManager.Instance.GetQuestByInstanceId(conn.ConnId.ToString(), instanceId);
                if (completingQuest == null)
                {
                    Debug.LogError($"[ADMIN-COMPLETEQUEST] No active quest with instanceId={instanceId}");
                    return;
                }

                Debug.LogError($"[ADMIN-COMPLETEQUEST] Completing {completingQuest.QuestId} (instanceId={instanceId})");

                QuestData completedQuestData = DatabaseLoader.Quests
                    .FirstOrDefault(q => q.id.Equals(completingQuest.QuestId, StringComparison.OrdinalIgnoreCase));

                // Mark all objectives complete in case anything down the line checks.
                if (completingQuest.Objectives != null)
                {
                    foreach (var obj in completingQuest.Objectives)
                    {
                        if (obj.Required > 0 && obj.Current < obj.Required)
                            obj.Current = obj.Required;
                    }
                }

                RemoveQuestItemsFromInventory(conn, completingQuest);
                QuestManager.Instance.HandleTurnInConfirmed(conn, instanceId);
                SavePlayerQuests(conn);
                ApplyQuestRewards(conn, completedQuestData);

                Debug.LogError($"[ADMIN-COMPLETEQUEST] Done");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ADMIN-COMPLETEQUEST] EXCEPTION: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void SavePlayerQuests(RRConnection conn)
        {
            string connId = conn.ConnId.ToString();
            if (!_selectedCharacter.ContainsKey(conn.LoginName)) return;
            var savedChar = CharacterRepository.GetCharacter(_selectedCharacter[conn.LoginName].Id);
            if (savedChar == null) return;

            var questState = QuestManager.Instance.GetPlayerState(connId);
            if (questState == null) return;

            // Save active quests
            savedChar.activeQuests = questState.ActiveQuests.Select(q => new SavedQuest
            {
                questId = q.QuestId,
                questGiverId = q.QuestGiverId,
                acceptedAt = q.AcceptedAt.ToString("o"),
                objectives = q.Objectives.Select(o => new SavedQuestObjective
                {
                    objectiveName = o.ObjectiveName,
                    type = o.Type,
                    target = o.Target,
                    label = o.Label,
                    required = o.Required,
                    current = o.Current
                }).ToList()
            }).ToList();

            // Save completed quests
            savedChar.completedQuests = new List<string>(questState.CompletedQuests);

            // Save unlocked checkpoints
            savedChar.unlockedCheckpoints = new List<string>(questState.UnlockedCheckpoints);

            savedChar.currentZoneName = conn.CurrentZoneName ?? "tutorial";
            savedChar.zoneId = (int)conn.CurrentZoneId;
            CharacterRepository.SaveCharacter(savedChar);
            Debug.LogError($"[SAVE] Saved {savedChar.activeQuests.Count} active, {savedChar.completedQuests.Count} completed quests for {conn.LoginName}");
        }

        /// <summary>
        /// Called by MerchantManager (or item pickup) when a player receives an item,
        /// so QuestManager can update any 'item' type objectives and refresh NPC markers.
        /// </summary>
        public void NotifyQuestItemAcquired(RRConnection conn, string itemGcType)
        {
            if (conn == null || string.IsNullOrEmpty(itemGcType)) return;
            var updates = QuestManager.Instance.OnItemPickedUp(conn, itemGcType);
            if (updates != null && updates.Count > 0)
            {
                Debug.LogError($"[QUEST-ITEM] Item '{itemGcType}' updated {updates.Count} quest objective(s) for {conn.LoginName}");
                SavePlayerQuests(conn);
                // NOTE: Do NOT call SendAvailableQuestUpdateForZone here.
                // The completion flag packet (questSubmsg=0) in SendProgressPacket already
                // tells the client to show yellow ? on the turn-in NPC.
                // Calling SendAvailableQuestUpdateForZone here re-sends the available list
                // which causes Liz Beth to show the quest twice (both ? and !).
            }
        }

        private void SavePlayerLevel(RRConnection conn, [CallerMemberName] string caller = null)
        {
            string connId = conn.ConnId.ToString();
            if (!_selectedCharacter.ContainsKey(conn.LoginName))
            {
                Debug.LogError($"[SAVE] SKIP: No selected character for {conn.LoginName}");
                return;
            }
            var savedChar = CharacterRepository.GetCharacter(_selectedCharacter[conn.LoginName].Id);
            if (savedChar == null)
            {
                Debug.LogError($"[SAVE] SKIP: Character not found for ID={_selectedCharacter[conn.LoginName].Id}");
                return;
            }

            PlayerState playerState = GetPlayerState(connId);
            if (playerState == null)
            {
                Debug.LogError($"[SAVE] SKIP: No PlayerState for {connId}");
                return;
            }

            // Don't overwrite saved progress with a fresh/default PlayerState.
            // Catches any path where SavePlayerLevel fires before playerState has been
            // populated from the DB (e.g. auto-save timer racing the spawn handler).
            // Also protects Level 1 characters that have XP > 0 in DB.
            if (playerState.Level <= 1 && playerState.Experience == 0
                && (savedChar.level > 1 || savedChar.experience > 0))
            {
                Debug.LogError($"[SAVE] SKIPPED - playerState looks uninitialized; would wipe DB lvl={savedChar.level} xp={savedChar.experience} for {conn.LoginName}");
                return;
            }
            if (playerState.MaxHPWire == 0 || playerState.MaxManaWire == 0)
            {
                Debug.LogError($"[SAVE] SKIPPED - playerState max resources uninitialized; would wipe DB maxHP={savedChar.maxHP} maxMana={savedChar.maxMana} for {conn.LoginName} caller={caller ?? "unknown"}");
                return;
            }

            byte persistedRuntimeLevel = SavedCharacterLevel.ResolvePersistedLevel(playerState.Level);
            savedChar.level = persistedRuntimeLevel;
            savedChar.experience = playerState.Experience;
            savedChar.currentHP = playerState.CurrentHPWire;
            savedChar.currentMana = playerState.CurrentManaWire;
            savedChar.currentZoneName = conn.CurrentZoneName ?? "tutorial";
            savedChar.zoneId = (int)conn.CurrentZoneId;

            // Character stats from equipment
            savedChar.maxHP = (int)(playerState.MaxHPWire / 256);
            savedChar.maxMana = (int)(playerState.MaxManaWire / 256);
            // Equipment bonuses are transient — do NOT overwrite allocated stat points
            // savedChar.statStrength/Agility/Intellect/Endurance hold ALLOCATED points only

            // Persist skill levels
            if (_playerSkillLevels.TryGetValue(connId, out var skillLevels))
            {
                // Only save full GC class keys (not short name duplicates)
                foreach (var kvp in skillLevels)
                {
                    if (kvp.Key.Contains("."))  // Full GC class like "skills.generic.PoisonBlastRadius"
                        savedChar.SetSkillLevel(kvp.Key, kvp.Value);
                }
            }

            Debug.LogError($"[SAVE-CONTEXT] caller={caller ?? "unknown"} login={conn.LoginName} char={savedChar.id} outgoingLevel={savedChar.level} outgoingNativeLevel={playerState.Level} outgoingXP={savedChar.experience} currentHP={savedChar.currentHP} maxHP={savedChar.maxHP} currentMana={savedChar.currentMana} maxMana={savedChar.maxMana} playerStateLevel={playerState.Level} playerStateXP={playerState.Experience} stateHP={playerState.CurrentHPWire}/{playerState.MaxHPWire} stateMana={playerState.CurrentManaWire}/{playerState.MaxManaWire}");
            CharacterRepository.SaveCharacter(savedChar, $"SavePlayerLevel:{caller ?? "unknown"}");
            Debug.LogError($"[SAVE] Level={savedChar.level} XP={savedChar.experience} HP={savedChar.currentHP / 256}/{savedChar.maxHP} Mana={savedChar.currentMana / 256}/{savedChar.maxMana} for {conn.LoginName} caller={caller ?? "unknown"}");
        }

        public (GCObject item, byte x, byte y)? GetInventoryItemBySlot(string connId, uint slotIndex, byte containerId = 0x0B)
        {
            string key = InvKey(connId, containerId);
            if (_playerInventoryItems.ContainsKey(key) && _playerInventoryItems[key].ContainsKey(slotIndex))
            {
                return _playerInventoryItems[key][slotIndex];
            }
            Debug.LogError($"[INV-TRACK] No item at slot {slotIndex} in container 0x{containerId:X2}");
            return null;
        }

        public void RemoveInventoryItemBySlot(string connId, uint slotIndex, byte containerId = 0x0B)
        {
            string key = InvKey(connId, containerId);
            if (_playerInventoryItems.ContainsKey(key) && _playerInventoryItems[key].ContainsKey(slotIndex))
            {
                _playerInventoryItems[key].Remove(slotIndex);
                if (_inventoryStackCounts.ContainsKey(key)) _inventoryStackCounts[key].Remove(slotIndex);
                Debug.LogError($"[INV-TRACK] Removed item at slot {slotIndex} in container 0x{containerId:X2}");
            }
        }
        public Dictionary<uint, (GCObject item, byte x, byte y)> GetAllInventoryItems(string connId, byte containerId = 0x0B)
        {
            string key = InvKey(connId, containerId);
            if (_playerInventoryItems.ContainsKey(key))
                return _playerInventoryItems[key];
            return null;
        }

        // Pickup packets carry only a slot index, not a container ID. This searches
        // main inventory first, then each bank page. Returns the containerId where
        // the slot lives, or null if no container has it.
        public byte? FindContainerForSlot(string connId, uint slotIndex)
        {
            if (_playerInventoryItems.ContainsKey(connId) && _playerInventoryItems[connId].ContainsKey(slotIndex))
                return 0x0B;
            byte[] bankIds = { 0x0C, 0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13 };
            foreach (byte cid in bankIds)
            {
                string key = InvKey(connId, cid);
                if (_playerInventoryItems.ContainsKey(key) && _playerInventoryItems[key].ContainsKey(slotIndex))
                    return cid;
            }
            return null;
        }

        public GCObject GetEquippedItem(string connId, uint slot)
        {
            Debug.LogError($"[GET-EQUIP] Looking for Player {connId} slot {slot}");

            if (!_playerEquippedItems.ContainsKey(connId))
            {
                Debug.LogError($"[GET-EQUIP] ❌ Player {connId} NOT in _playerEquippedItems!");
                Debug.LogError($"[GET-EQUIP] Available players: {string.Join(", ", _playerEquippedItems.Keys)}");
                return null;
            }

            if (!_playerEquippedItems[connId].ContainsKey(slot))
            {
                Debug.LogError($"[GET-EQUIP] ❌ Slot {slot} not found for player {connId}!");
                Debug.LogError($"[GET-EQUIP] Available slots: {string.Join(", ", _playerEquippedItems[connId].Keys)}");
                return null;
            }

            GCObject item = _playerEquippedItems[connId][slot];
            Debug.LogError($"[GET-EQUIP] ✅ Found: {item.GCClass}");
            return item;
        }
        public void TrackManipulatorsId(string connId, ushort manipulatorsId)
        {
            _playerManipulatorsIds[connId] = manipulatorsId;
            Debug.LogError($"[MANIPULATORS-TRACK] Player {connId}: Manipulators ID = 0x{manipulatorsId:X4}");
        }

        public ushort GetManipulatorsComponentId(string connId)
        {
            if (_playerManipulatorsIds.ContainsKey(connId))
                return _playerManipulatorsIds[connId];
            Debug.LogError($"[MANIPULATORS] ❌ No ID tracked for player {connId}!");
            return 0;
        }
        public ushort GetUnitContainerComponentId(string connId)
        {
            if (_playerComponentTypes.ContainsKey(connId))
            {
                foreach (var kvp in _playerComponentTypes[connId])
                {
                    if (kvp.Value == "UnitContainer")
                        return kvp.Key;
                }
            }
            Debug.LogError($"[UNITCONTAINER] ❌ No ID tracked for player {connId}!");
            return 0;
        }

        // ═══════════════════════════════════════════════════════════════════════════════
        // DROPPED ITEM TRACKING FOR PICKUP — WITH DB PERSISTENCE
        // ═══════════════════════════════════════════════════════════════════════════════

        public void TrackDroppedItem(ushort entityId, GCObject item, RRConnection conn, int quantity = 1, float? posX = null, float? posY = null, float? posZ = null, int? playerLevelOverride = null)
        {
            // BUG-FIX (item bleed across sub-levels): previously this used
            // conn.CurrentZoneGcType, but that field collapses every sub-level of a
            // dungeon to the same parent string ("world.dungeon00" for both
            // dungeon00_level01 and dungeon00_level03). Drops in level03 then matched
            // the load query in level01. CurrentZoneName holds the literal map name
            // ("dungeon00_level01") and is the correct identity for drop scoping.
            var info = new DroppedItemInfo
            {
                Item = item,
                DbId = 0,
                Zone = conn.CurrentZoneName ?? "",
                ZoneId = conn.CurrentZoneId,
                InstanceId = conn.InstanceId,
                PosX = posX ?? conn.PlayerPosX,
                PosY = posY ?? conn.PlayerPosY,
                PosZ = posZ ?? conn.PlayerPosZ,
                PlayerLevel = playerLevelOverride ?? (GetPlayerState(conn.ConnId.ToString())?.Level ?? 1),
                DroppedBy = conn.LoginName ?? "",
                Quantity = quantity
            };

            // Save to DB
            try
            {
                using (var db = DungeonRunners.Database.GameDatabase.GetConnection())
                {
                    DungeonRunners.Database.GameDatabase.ExecuteNonQuery(db,
                        @"INSERT INTO dropped_items (zone, zone_id, instance_id, gc_class, native_class, pos_x, pos_y, pos_z, player_level, target_slot, preset_scale_mod, dropped_by, rarity, stored_level)
                          VALUES (@zone, @zid, @iid, @gc, @nc, @px, @py, @pz, @pl, @ts, @psm, @db, @rar, @slv)",
                        ("@zone", info.Zone),
                        ("@zid", (int)info.ZoneId),
                        ("@iid", (int)info.InstanceId),
                        ("@gc", item.GCClass),
                        ("@nc", item.NativeClass ?? "Armor"),
                        ("@px", (double)info.PosX),
                        ("@py", (double)info.PosY),
                        ("@pz", (double)info.PosZ),
                        ("@pl", info.PlayerLevel),
                        ("@ts", (int)(item.TargetSlot ?? 0xFFFFFFFF)),
                        ("@psm", item.PresetScaleMod ?? ""),
                        ("@db", info.DroppedBy),
                        ("@rar", item.GetEffectiveRarity()),
                        ("@slv", item.StoredLevel));

                    long dbId = Convert.ToInt64(DungeonRunners.Database.GameDatabase.ExecuteScalar(db, "SELECT last_insert_rowid()"));
                    info.DbId = dbId;
                    _dbIdToEntityId[dbId] = entityId;
                    Debug.LogError($"[DROP-TRACK] Saved to DB: dbId={dbId}, entityId={entityId}, GCClass={item.GCClass}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DROP-TRACK] ❌ DB save failed: {ex.Message}");
            }

            _droppedItems[entityId] = info;
            Debug.LogError($"[DROP-TRACK] Stored dropped item: entityId={entityId}, GCClass={item.GCClass}, zone={info.Zone}, instance={info.InstanceId}");
        }

        public bool IsDroppedItem(ushort entityId)
        {
            return _droppedItems.ContainsKey(entityId);
        }

        public GCObject GetAndRemoveDroppedItem(ushort entityId, out int quantity)
        {
            quantity = 1;
            if (_droppedItems.TryGetValue(entityId, out DroppedItemInfo info))
            {
                _droppedItems.Remove(entityId);
                quantity = info.Quantity;

                // Delete from DB
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
                        Debug.LogError($"[DROP-TRACK] Deleted from DB: dbId={info.DbId}, entityId={entityId}");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[DROP-TRACK] ❌ DB delete failed: {ex.Message}");
                    }
                }

                return info.Item;
            }
            return null;
        }

        // ═══════════════════════════════════════════════════════════════════════════════
        // LOAD DROPPED ITEMS FOR ZONE — called during zone entry after checkpoints
        // ═══════════════════════════════════════════════════════════════════════════════

        private void SendDroppedItemsForZone(RRConnection conn)
        {
            // Match TrackDroppedItem: scope drops by literal map name, not the parent
            // GC type. See note in TrackDroppedItem for why CurrentZoneGcType is wrong.
            string zone = conn.CurrentZoneName ?? "";
            uint instanceId = conn.InstanceId;

            Debug.LogError($"[DROP-LOAD] Loading dropped items for zone={zone}, instance={instanceId}");

            // Step 1: Re-send items already in memory for this zone (dropped this session by others)
            int inMemoryCount = 0;
            foreach (var kvp in _droppedItems)
            {
                var info = kvp.Value;
                if (info.Zone != zone || info.InstanceId != instanceId) continue;

                // Send spawn packet to this player
                if (info.GoldAmount > 0)
                    SendGoldPileSpawnPacket(conn, kvp.Key, info.PosX, info.PosY, info.PosZ);
                else
                    SendDroppedItemSpawnPacket(conn, kvp.Key, info);
                inMemoryCount++;
            }
            Debug.LogError($"[DROP-LOAD] Re-sent {inMemoryCount} in-memory items");

            // Step 2: Load from DB — items from previous sessions not yet in memory
            int dbCount = 0;
            try
            {
                using (var db = DungeonRunners.Database.GameDatabase.GetConnection())
                using (var reader = DungeonRunners.Database.GameDatabase.ExecuteReader(db,
                    "SELECT id, gc_class, native_class, pos_x, pos_y, pos_z, player_level, target_slot, preset_scale_mod, dropped_by, COALESCE(rarity, -1), COALESCE(stored_level, -1) FROM dropped_items WHERE zone = @zone AND instance_id = @iid",
                    ("@zone", zone), ("@iid", (int)instanceId)))
                {
                    while (reader.Read())
                    {
                        long dbId = reader.GetInt64(0);

                        // Skip if already loaded this session
                        if (_dbIdToEntityId.ContainsKey(dbId)) continue;

                        string gcClass = reader.GetString(1);
                        string nativeClass = reader.GetString(2);
                        float posX = (float)reader.GetDouble(3);
                        float posY = (float)reader.GetDouble(4);
                        float posZ = (float)reader.GetDouble(5);
                        int playerLevel = reader.GetInt32(6);
                        int targetSlotRaw = reader.GetInt32(7);
                        string presetScaleMod = reader.IsDBNull(8) ? "" : reader.GetString(8);
                        string droppedBy = reader.IsDBNull(9) ? "" : reader.GetString(9);
                        int itemRarity = reader.GetInt32(10);
                        int itemStoredLevel = reader.GetInt32(11);

                        // Reconstruct GCObject
                        var item = new GCObject
                        {
                            GCClass = gcClass,
                            NativeClass = nativeClass,
                            StoredRarity = itemRarity,
                            StoredLevel = itemStoredLevel
                        };
                        if (targetSlotRaw >= 0 && targetSlotRaw != -1 && (uint)targetSlotRaw != 0xFFFFFFFF)
                            item.TargetSlot = (uint)targetSlotRaw;
                        if (!string.IsNullOrEmpty(presetScaleMod))
                            item.PresetScaleMod = presetScaleMod;

                        // Assign entity ID and track
                        ushort entityId = GetNextLootEntityId();
                        var info = new DroppedItemInfo
                        {
                            Item = item,
                            DbId = dbId,
                            Zone = zone,
                            ZoneId = conn.CurrentZoneId,
                            InstanceId = instanceId,
                            PosX = posX,
                            PosY = posY,
                            PosZ = posZ,
                            PlayerLevel = playerLevel,
                            DroppedBy = droppedBy
                        };

                        _droppedItems[entityId] = info;
                        _dbIdToEntityId[dbId] = entityId;

                        // Send spawn packet
                        SendDroppedItemSpawnPacket(conn, entityId, info);
                        dbCount++;

                        Debug.LogError($"[DROP-LOAD] Loaded from DB: dbId={dbId}, entityId={entityId}, gc={gcClass}, pos=({posX},{posY},{posZ})");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DROP-LOAD] ❌ DB load failed: {ex.Message}");
            }

            Debug.LogError($"[DROP-LOAD] ✅ Zone load complete: {inMemoryCount} in-memory + {dbCount} from DB");
        }

        private void SendDroppedItemSpawnPacket(RRConnection conn, ushort entityId, DroppedItemInfo info)
        {
            int fx = (int)(info.PosX * 256);
            int fy = (int)(info.PosY * 256);
            int fz = (int)(info.PosZ * 256);

            var body = new LEWriter();
            body.WriteByte(0x01);                  // CreateEntity
            body.WriteUInt16(entityId);
            body.WriteByte(0xFF);
            body.WriteCString("itemobject");
            body.WriteByte(0x02);                  // SetPosition
            body.WriteUInt16(entityId);
            body.WriteUInt32(0x00000006);
            body.WriteInt32(fx);
            body.WriteInt32(fy);
            body.WriteInt32(fz);
            body.WriteInt32(0);
            body.WriteByte(0xF7);
            body.WriteUInt16(0x0000);
            body.WriteByte(0x00);
            body.WriteUInt32(0x00000000);
            body.WriteByte(0x06);                  // ItemObject::ItemObject -> writeInit fields +0x101..+0x118
            body.WriteUInt16(0x0000);
            body.WriteUInt32(0x00000000);
            body.WriteUInt32(0x00000000);
            body.WriteUInt32(27000);
            body.WriteByte(0x01);
            int beforeInit = body.Position;
            info.Item.WriteInitForDroppedItem(body, info.PlayerLevel);
            int afterInit = body.Position;

            var chan = new LEWriter();
            chan.WriteByte(0x07);
            chan.WriteBytes(body.ToArray());
            chan.WriteByte(0x06);
            byte[] finalPacket = chan.ToArray();

            // Dump the bytes WriteInitForDroppedItem emitted plus the surrounding
            // framing, so we can diff against a working drop (or against the
            // merchant write for the same gcType) when something desyncs.
            // Triggered for items only — gold piles write a separate path.
            if (info.Item != null)
            {
                byte[] bodyBytes = body.ToArray();
                int writeInitLen = afterInit - beforeInit;
                string writeInitHex = BitConverter.ToString(bodyBytes, beforeInit, writeInitLen).Replace("-", " ");
                string fullHex = BitConverter.ToString(finalPacket).Replace("-", " ");
                Debug.LogError($"[DROP-WRITEINIT] gc={info.Item.GCClass} writeInitBytes={writeInitLen} hex={writeInitHex}");
                Debug.LogError($"[DROP-WRITEINIT] full-packet ({finalPacket.Length}B) hex={fullHex}");
            }

            SendToClient(conn, finalPacket);
        }

        /// <summary>
        /// Spawn a gold pile entity on the ground. Box.gc → Visual=D3D:GroundObject_Money.
        /// Simple EntityObject — uses minimal consumable-style init (no equipment bytes).
        /// </summary>
        private void SendGoldPileSpawnPacket(RRConnection conn, ushort entityId, float posX, float posY, float posZ)
        {
            int fx = (int)(posX * 256);
            int fy = (int)(posY * 256);
            int fz = (int)(posZ * 256);

            var body = new LEWriter();
            body.WriteByte(0x01);                  // CreateEntity
            body.WriteUInt16(entityId);
            body.WriteByte(0xFF);
            body.WriteCString("itemobject");
            body.WriteByte(0x02);                  // SetPosition
            body.WriteUInt16(entityId);
            body.WriteUInt32(0x00000006);
            body.WriteInt32(fx);
            body.WriteInt32(fy);
            body.WriteInt32(fz);
            body.WriteInt32(0);
            body.WriteByte(0xF7);
            body.WriteUInt16(0x0000);
            body.WriteByte(0x00);
            body.WriteUInt32(0x00000000);
            body.WriteByte(0x06);
            body.WriteUInt16(0x0000);
            body.WriteUInt32(0x00000000);
            body.WriteUInt32(0x00000000);
            body.WriteUInt32(27000);
            body.WriteByte(0x01);
            body.WriteByte(0xFF);
            body.WriteCString("questitempal2.ww00_gb01");
            body.WriteUInt32(0);
            body.WriteByte(0x00);
            body.WriteByte(0x00);
            body.WriteByte(0x01);
            body.WriteByte(0x01);
            body.WriteByte(0x00);
            body.WriteByte(0x00);

            var chan = new LEWriter();
            chan.WriteByte(0x07);
            chan.WriteBytes(body.ToArray());
            chan.WriteByte(0x06);
            SendToClient(conn, chan.ToArray());
        }

        // ═══════════════════════════════════════════════════════════════════════════════
        // CLEANUP EXPIRED DROPPED ITEMS — called from Update() timer
        // ═══════════════════════════════════════════════════════════════════════════════

        private void CleanupExpiredDroppedItems()
        {
            // Delete expired rows from DB
            List<long> expiredDbIds = new List<long>();
            try
            {
                using (var db = DungeonRunners.Database.GameDatabase.GetConnection())
                {
                    // First find which IDs are expired
                    using (var reader = DungeonRunners.Database.GameDatabase.ExecuteReader(db,
                        $"SELECT id FROM dropped_items WHERE dropped_at < datetime('now', '-{DROPPED_ITEM_EXPIRE_MINUTES} minutes')"))
                    {
                        while (reader.Read())
                            expiredDbIds.Add(reader.GetInt64(0));
                    }

                    if (expiredDbIds.Count > 0)
                    {
                        // Delete them
                        DungeonRunners.Database.GameDatabase.ExecuteNonQuery(db,
                            $"DELETE FROM dropped_items WHERE dropped_at < datetime('now', '-{DROPPED_ITEM_EXPIRE_MINUTES} minutes')");
                        Debug.LogError($"[DROP-CLEANUP] Deleted {expiredDbIds.Count} expired items from DB");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DROP-CLEANUP] ❌ DB cleanup failed: {ex.Message}");
            }

            // Remove from in-memory tracking and despawn for online players
            foreach (long dbId in expiredDbIds)
            {
                if (_dbIdToEntityId.TryGetValue(dbId, out ushort entityId))
                {
                    if (_droppedItems.TryGetValue(entityId, out DroppedItemInfo info))
                    {
                        // Despawn for all players in this zone
                        foreach (var other in _connections.Values)
                        {
                            if (!other.IsSpawned) continue;
                            if (other.CurrentZoneName != info.Zone) continue;
                            if (other.InstanceId != info.InstanceId) continue;
                            SendDespawnEntity(other, entityId);
                        }
                        _droppedItems.Remove(entityId);
                    }
                    _dbIdToEntityId.Remove(dbId);
                }
            }
        }












        // ✅ NEW METHOD

        private void LoadZones()
        {
            try
            {
                using (var conn = DungeonRunners.Database.GameDatabase.GetConnection())
                using (var r = DungeonRunners.Database.GameDatabase.ExecuteReader(conn, "SELECT * FROM zones"))
                {
                    while (r.Read())
                    {
                        var zone = new Zone
                        {
                            id = DungeonRunners.Database.GameDatabase.GetUInt(r, "id"),
                            name = DungeonRunners.Database.GameDatabase.GetString(r, "name"),
                            gcType = DungeonRunners.Database.GameDatabase.GetString(r, "gc_type"),
                            spawnX = DungeonRunners.Database.GameDatabase.GetFloat(r, "spawn_x"),
                            spawnY = DungeonRunners.Database.GameDatabase.GetFloat(r, "spawn_y"),
                            spawnZ = DungeonRunners.Database.GameDatabase.GetFloat(r, "spawn_z"),
                            spawnHeading = DungeonRunners.Database.GameDatabase.GetFloat(r, "spawn_heading"),
                            respawnZone = DungeonRunners.Database.GameDatabase.GetString(r, "respawn_zone"),
                            exploredBitCount = GetIntSafe(r, "explored_bit_count", 0)
                        };
                        if (zone.id != 0) _zones[zone.id] = zone;
                    }
                }
                Debug.Log($"Loaded {_zones.Count} zones from SQLite");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error loading zones: {ex.Message}");
            }
        }

        private ushort ResolveExploredBitCount(RRConnection conn, string source)
        {
            if (conn != null && _zones.TryGetValue(conn.CurrentZoneId, out Zone zone) && zone.exploredBitCount > 0)
                return (ushort)zone.exploredBitCount;

            if (conn != null && DungeonMazeSpawner.TryResolveNativeExploredBitCount(conn.CurrentZoneName, out ushort proceduralBitCount))
            {
                Debug.LogError($"[MINIMAP] source={source ?? "unknown"} zoneId=0x{conn.CurrentZoneId:X8} zone='{conn.CurrentZoneName ?? ""}' exploredBitCount={proceduralBitCount} source=procedural-dungeon-maze native=MiniMapExplored::ReadExploredBits");
                return proceduralBitCount;
            }

            Debug.LogError($"[MINIMAP] source={source ?? "unknown"} zoneId=0x{(conn != null ? conn.CurrentZoneId : 0):X8} zone='{conn?.CurrentZoneName ?? ""}' exploredBitCount=0 reason=no-authored-zone-count native=ZoneMessageReady");
            return 0;
        }

        private void StartServer()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, ServerSettings.Get("gamePort", 2603));
                _listener.Start();
                _isRunning = true;

                string localIP = GetLocalIPAddress();
                Debug.Log($"Game Server started on 0.0.0.0:2603 (accessible via {localIP}:2603)");
                StartCoroutine(AcceptClientsCoroutine());
                StartCoroutine(PollPendingItemGrants());
                // 🔥 ADD THIS LINE:
                StartUDPListener();
                QueueConnectionBridge.OnQueueStreamRegistered += OnQueueStreamReady;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to start game server: {ex.Message}");
            }
        }
        // 🔥 UDP LISTENER FOR COMBAT MESSAGES
        private void StartUDPListener()
        {
            try
            {
                _udpListener = new UdpClient(_udpPort);
                Debug.LogError($"[UDP] UDP Listener started on port {_udpPort}!");
                _udpListener.BeginReceive(OnUDPReceive, null);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UDP] Failed to start on {_udpPort}: {ex.Message}");
            }
        }
        private void InitiateUDPToClient(RRConnection conn)
        {
            try
            {
                var tcpEndpoint = conn.Client.Client.RemoteEndPoint as IPEndPoint;
                if (tcpEndpoint == null) return;

                string clientIP = tcpEndpoint.Address.ToString();
                var clientEndpoint = new IPEndPoint(IPAddress.Parse(clientIP), 2603);

                byte[] synPacket = BuildUDPSynPacket(conn.ConnId);
                _udpListener.Send(synPacket, synPacket.Length, clientEndpoint);
                Debug.LogError($"[UDP] Sent SYN to {clientEndpoint}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UDP] InitiateUDPToClient error: {ex.Message}");
            }
        }

        private byte[] BuildUDPSynPacket(int connId)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x01);
            writer.WriteUInt32((uint)connId);
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            return writer.ToArray();
        }

        private void SendUDPAck(IPEndPoint clientEndpoint)
        {
            try
            {
                var writer = new LEWriter();
                writer.WriteByte(0x02);
                writer.WriteUInt32(0);
                writer.WriteUInt32(1);
                writer.WriteUInt32(1);

                byte[] ackPacket = writer.ToArray();
                _udpListener.Send(ackPacket, ackPacket.Length, clientEndpoint);
                Debug.LogError($"[UDP] Sent ACK to {clientEndpoint}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UDP] SendUDPAck error: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // UDP ENCRYPTION METHODS
        // ═══════════════════════════════════════════════════════════════════

        private byte[] EncryptUDP(UDPSession session, byte[] plaintext)
        {
            int paddedLen = ((plaintext.Length + 7) / 8) * 8;
            byte[] padded = new byte[paddedLen];
            Array.Copy(plaintext, padded, plaintext.Length);
            byte[] encrypted = new byte[paddedLen];
            for (int i = 0; i < paddedLen; i += 8)
                session.EncryptEngine.ProcessBlock(padded, i, encrypted, i);
            return encrypted;
        }

        private byte[] DecryptUDP(UDPSession session, byte[] ciphertext)
        {
            if (ciphertext.Length % 8 != 0) return null;
            byte[] decrypted = new byte[ciphertext.Length];
            for (int i = 0; i < ciphertext.Length; i += 8)
                session.DecryptEngine.ProcessBlock(ciphertext, i, decrypted, i);
            return decrypted;
        }

        private HashSet<string> _seenPacketPatterns = new HashSet<string>();
        private Dictionary<string, string> _checkpointZoneMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private const uint DungeonPortalAggColor = 0xFFFF0000u;

        private static uint ResolveAuthoredDungeonPortalColor(string gcType, uint fallback)
        {
            if (ContainsIgnoreCase(gcType, "hub"))
                return DungeonPortalHubColor;
            if (ContainsIgnoreCase(gcType, "agg"))
                return DungeonPortalAggColor;
            return fallback;
        }

        private static void ApplyDungeonPortalAuthority(DungeonMazeSpawner.ProceduralDungeonSnapshot snapshot, DungeonPortalRole role, ZonePortal portal)
        {
            if (snapshot == null || portal == null)
                return;

            string gcType = role == DungeonPortalRole.Entry ? snapshot.EntryPortalGcType : snapshot.ExitPortalGcType;
            string targetZone = role == DungeonPortalRole.Entry ? snapshot.EntryLinkToZone : snapshot.ExitLinkToZone;
            string spawnPoint = role == DungeonPortalRole.Entry ? snapshot.EntryLinkToSpawn : snapshot.ExitLinkToSpawn;

            if (!string.IsNullOrEmpty(gcType))
                portal.GCType = gcType;
            if (!string.IsNullOrEmpty(targetZone))
                portal.TargetZone = targetZone;
            if (!string.IsNullOrEmpty(spawnPoint))
                portal.SpawnPoint = spawnPoint;

            portal.Color = ResolveAuthoredDungeonPortalColor(portal.GCType, portal.Color);
        }

        private static ZonePortal CloneZonePortal(ZonePortal source)
        {
            if (source == null) return null;
            return new ZonePortal
            {
                Id = source.Id,
                GCType = source.GCType,
                Name = source.Name,
                PosX = source.PosX,
                PosY = source.PosY,
                PosZ = source.PosZ,
                Heading = source.Heading,
                Width = source.Width,
                Height = source.Height,
                TargetZone = source.TargetZone,
                SpawnPoint = source.SpawnPoint,
                Color = source.Color
            };
        }

        private List<ZonePortal> ResolveZonePortalsForConnection(RRConnection conn, List<ZonePortal> portals)
        {
            if (portals == null || portals.Count == 0)
                return portals;
            if (!TryGetProceduralDungeonSnapshot(conn, out var snapshot))
                return portals;

            var resolved = new List<ZonePortal>(portals.Count);
            foreach (var portal in portals)
            {
                var clone = CloneZonePortal(portal);
                if (clone == null) continue;
                DungeonPortalRole role = ResolveDungeonPortalRole(conn.CurrentZoneName, clone.GCType, clone.Name, clone.TargetZone, clone.SpawnPoint);
                ApplyDungeonPortalAuthority(snapshot, role, clone);
                ResolveDungeonPortalAnchor(snapshot, role, out var position, out float heading, out int sourceIndex, out string tileType, out int gridX, out int gridY, out var local, out string source);
                clone.PosX = position.x;
                clone.PosY = position.y;
                clone.PosZ = position.z;
                clone.Heading = heading;
                resolved.Add(clone);
                Debug.LogError($"[DUNGEON-PORTAL] zone={snapshot.ZoneName} layoutSeed=0x{snapshot.LayoutSeed:X8} roomSeed=0x{snapshot.RoomSeed:X8} name={clone.Name} gc={clone.GCType} target={clone.TargetZone} spawnPoint={clone.SpawnPoint} role={role.ToString().ToLowerInvariant()} src={sourceIndex} tile='{tileType}' grid=({gridX},{gridY}) local=({local.x:F1},{local.y:F1},{local.z:F1}) rawAuthoredWorld=({clone.PosX:F1},{clone.PosY:F1},{clone.PosZ:F1}) sentWorld=({clone.PosX:F1},{clone.PosY:F1},{clone.PosZ:F1}) snapApplied=False heading={clone.Heading:F1} source='{source}'");
            }
            return resolved;
        }


        private void SendZonePortals(RRConnection conn, uint zoneId)
        {
            if (!_zonePortals.TryGetValue(zoneId, out var portals) || portals.Count == 0)
            {
                Debug.LogError($"[SendZonePortals] No portals for zone {zoneId}");
                return;
            }
            portals = ResolveZonePortalsForConnection(conn, portals);

            if (VerbosePacketLogging) Debug.LogError($"[SendZonePortals] Spawning {portals.Count} portals");

            var writer = new LEWriter();
            writer.WriteByte(0x07);  // BeginStream

            foreach (var portal in portals)
            {
                ushort portalId = (ushort)_nextEntityId++;
                _portalEntities[portalId] = portal;  // ADD THIS LINE
                _allEntityPositions[portalId] = (portal.PosX, portal.PosY, portal.PosZ);
                if (VerbosePacketLogging) Debug.LogError($"[Portal] {portal.Name} (0x{portalId:X4}) at ({portal.PosX}, {portal.PosY}, {portal.PosZ}) → {portal.TargetZone}");
                if (VerbosePacketLogging) Debug.LogError($"[Portal] ★ Registered 0x{portalId:X4} → '{portal.TargetZone}'");
                // Create Portal Entity (0x01)
                writer.WriteByte(0x01);
                writer.WriteUInt16(portalId);
                WriteGCType(writer, portal.GCType, preserveCase: true);

                // Init Portal Entity (0x02)
                writer.WriteByte(0x02);
                writer.WriteUInt16(portalId);

                // WorldEntity::WriteInit
                writer.WriteUInt32(0x06);  // flags: visible | activatable


                int posX = (int)(portal.PosX * 256);
                int posY = (int)(portal.PosY * 256);
                int posZ = (int)(portal.PosZ * 256);
                int heading = (int)(portal.Heading * 256);

                writer.WriteInt32(posX);
                writer.WriteInt32(posY);
                writer.WriteInt32(posZ);
                writer.WriteInt32(heading);
                // Native WorldEntity::writeInit only serializes optional fields when they are non-default.
                writer.WriteByte(0x00);  // initFlags: no parent/extra fields for authored zone portals

                if (VerbosePacketLogging) Debug.LogError($"[Portal-Write] EntityID=0x{portalId:X4} Label='{portal.TargetZone}' SpawnPt='{portal.SpawnPoint}' W={portal.Width} H={portal.Height} Color=0x{portal.Color:X8}");
                writer.WriteCString(portal.SpawnPoint ?? "");
                writer.WriteCString(portal.TargetZone ?? "");

                writer.WriteUInt16((ushort)portal.Width);
                writer.WriteUInt16((ushort)portal.Height);
                writer.WriteUInt32(portal.Color);
                if (VerbosePacketLogging) Debug.LogError($"[Portal-HEX] {BitConverter.ToString(writer.ToArray())}");
                if (VerbosePacketLogging) Debug.LogError($"[Portal-Write] ★ EntityID=0x{portalId:X4} → Label='{portal.TargetZone}' SpawnPt='{portal.SpawnPoint}' W={portal.Width} H={portal.Height} Color=0x{portal.Color:X8}");
            }

            writer.WriteByte(0x06);  // EndStream

            byte[] packetData = writer.ToArray();
            if (VerbosePacketLogging) Debug.LogError($"[SendZonePortals] FULL HEX ({packetData.Length} bytes): {BitConverter.ToString(packetData).Replace("-", " ")}");
            if (VerbosePacketLogging) Debug.LogError($"[SendZonePortals] ★★★ PORTAL PACKET HEX DUMP ★★★");
            if (VerbosePacketLogging) Debug.LogError($"[SendZonePortals] Length: {packetData.Length} bytes");
            if (VerbosePacketLogging) Debug.LogError($"[SendZonePortals] Hex: {BitConverter.ToString(packetData)}");
            SendCompressedA(conn, 0x01, 0x0f, packetData);

            if (VerbosePacketLogging) Debug.LogError($"[SendZonePortals] ✅ Sent {portals.Count} portals ({packetData.Length} bytes)");
        }








        private void SendZoneCheckpoints(RRConnection conn, uint zoneId)
        {
            if (!_zoneCheckpoints.TryGetValue(zoneId, out var checkpoints) || checkpoints.Count == 0)
            {
                Debug.LogError($"[SendZoneCheckpoints] No checkpoints for zone {zoneId}");
                return;
            }

            if (VerbosePacketLogging) Debug.LogError($"[SendZoneCheckpoints] Spawning {checkpoints.Count} checkpoints");

            var writer = new LEWriter();
            writer.WriteByte(0x07);  // BeginStream

            foreach (var checkpoint in checkpoints)
            {
                ushort checkpointId = (ushort)_nextEntityId++;
                _checkpointEntities[checkpointId] = checkpoint;  // ADD THIS LINE
                _allEntityPositions[checkpointId] = (checkpoint.PosX, checkpoint.PosY, checkpoint.PosZ);
                if (VerbosePacketLogging) Debug.LogError($"[Checkpoint] {checkpoint.GCType} (0x{checkpointId:X4}) at ({checkpoint.PosX}, {checkpoint.PosY}, {checkpoint.PosZ})");

                // Create Checkpoint Entity (0x01)
                writer.WriteByte(0x01);
                writer.WriteUInt16(checkpointId);
                WriteGCType(writer, checkpoint.GCType, preserveCase: true);

                // Init Checkpoint Entity (0x02)
                writer.WriteByte(0x02);
                writer.WriteUInt16(checkpointId);

                // WorldEntity::WriteInit (flags 0x06 — visible|activatable, NOT blocking)
                // Blocking flag (0x01) causes pathfinder to scan cells around entity.
                // Near map edges this crashes at 0x4C5042 (null pathmap node).
                writer.WriteUInt32(0x06);

                int posX = (int)(checkpoint.PosX * 256);
                int posY = (int)(checkpoint.PosY * 256);
                int posZ = (int)(checkpoint.PosZ * 256);
                int heading = (int)(checkpoint.Heading * 256);

                writer.WriteInt32(posX);
                writer.WriteInt32(posY);
                writer.WriteInt32(posZ);
                writer.WriteInt32(heading);
                writer.WriteByte(0x00);  // initFlags
            }

            writer.WriteByte(0x06);  // EndStream

            byte[] packetData = writer.ToArray();
            SendCompressedA(conn, 0x01, 0x0f, packetData);

            if (VerbosePacketLogging) Debug.LogError($"[SendZoneCheckpoints] ✅ Sent {checkpoints.Count} checkpoints ({packetData.Length} bytes)");
        }

        private void SendZoneChests(RRConnection conn, string zoneName)
        {
            if (string.IsNullOrEmpty(zoneName)) return;
            var chests = new List<ChestSpawnData>();
            if (chests.Count == 0) return;

            Debug.LogError($"[CHESTS] Spawning {chests.Count} treasure chests in {zoneName}");

            var writer = new LEWriter();
            writer.WriteByte(0x07);  // BeginStream

            foreach (var chest in chests)
            {
                ushort chestId = (ushort)_nextEntityId++;
                ushort behaviorId = (ushort)_nextEntityId++;

                var chestPathMap = DungeonRunners.Core.PathMapManager.Instance.GetPathMap(zoneName);
                if (chestPathMap != null && chestPathMap.IsWalkable(chest.PosX, chest.PosY))
                {
                    float terrainZ = chestPathMap.GetHeightAt(chest.PosX, chest.PosY, chest.PosZ);
                    chest.PosZ = terrainZ;
                }

                _chestEntities[chestId] = chest;
                _allEntityPositions[chestId] = (chest.PosX, chest.PosY, chest.PosZ);

                // Create Entity (0x01) — same pattern as checkpoint
                writer.WriteByte(0x01);
                writer.WriteUInt16(chestId);
                WriteGCType(writer, chest.GCType, preserveCase: true);

                // Init Entity (0x02)
                writer.WriteByte(0x02);
                writer.WriteUInt16(chestId);

                // WorldEntity::WriteInit flags 0x06 (visible|activatable, NOT blocking)
                // Blocking flag (0x01) crashes at 0x4C5042 near map edges (null pathmap node)
                writer.WriteUInt32(0x06);

                writer.WriteInt32((int)(chest.PosX * 256));
                writer.WriteInt32((int)(chest.PosY * 256));
                writer.WriteInt32((int)(chest.PosZ * 256));
                writer.WriteInt32((int)(chest.Heading * 256));
                writer.WriteByte(0x00);  // initFlags

                // Chest GC types extend NonCombatInteractive — client reads extra bytes
                // after WorldEntity::readInit. Without these, stream desyncs → "Unknown message type"

                // Intermediate parent::readInit @ 0x50A580 — 6 bytes:
                writer.WriteByte(0x00);  // intermediate flags (no conditionals)
                writer.WriteByte(0x00);  // level/mode
                writer.WriteUInt16(0);   // +0x316
                writer.WriteUInt16(0);   // +0x318

                // NCI::readInit @ 0x5A8E20 — 4 bytes:
                writer.WriteByte(0x00);  // +0x31D activation flags
                writer.WriteByte(0x00);  // +0x326 state
                writer.WriteUInt16(0);   // +0x324 counter

                // 0x32 CreateChild: Behavior — required for NCI activate (0x03/0x0A) to work
                writer.WriteByte(0x32);
                writer.WriteUInt16(chestId);
                writer.WriteUInt16(behaviorId);
                WriteGCType(writer, "base.noncombatinteractive.behavior", preserveCase: true);
                writer.WriteByte(0x01);  // flag byte

                // Behavior::readInit (4 bytes)
                writer.WriteByte(0xFF);  // flags
                writer.WriteByte(0x00);  // action class ID
                writer.WriteByte(0x00);  // second action class ID
                writer.WriteByte(0x01);  // end byte

                // UnitMover::readInit (10 bytes)
                writer.WriteByte(0x08);  // mover flags
                writer.WriteInt32((int)(chest.Heading * 256));  // heading1
                writer.WriteInt32((int)(chest.Heading * 256));  // heading2
                writer.WriteByte(0x00);  // waypoint

                // UnitBehavior::readInit own (3 bytes)
                writer.WriteByte(0xFF);  // flags
                writer.WriteByte(0x00);  // extra
                writer.WriteByte(0x00);  // extra2

                Debug.LogError($"[CHEST] {chest.Label} ({chest.GCType}) id=0x{chestId:X4} beh=0x{behaviorId:X4} at ({chest.PosX:F0},{chest.PosY:F0},{chest.PosZ:F0})");
            }

            writer.WriteByte(0x06);  // EndStream

            byte[] chestPacket = writer.ToArray();
            SendCompressedA(conn, 0x01, 0x0f, chestPacket);
            Debug.LogError($"[CHESTS] ✅ Sent {chests.Count} chests ({chestPacket.Length} bytes)");
        }

        // ═══════════════════════════════════════════════════════════════
        // WORLD ENTITY SPAWNING — chests, shrines, gates, teleporters, NCIs
        // from zone_world_entities DB table
        // ═══════════════════════════════════════════════════════════════

        private static WorldEntityData CloneWorldEntityData(WorldEntityData source)
        {
            if (source == null) return null;
            return new WorldEntityData
            {
                Id = source.Id,
                Zone = source.Zone,
                Name = source.Name,
                GCType = source.GCType,
                EntityType = source.EntityType,
                PosX = source.PosX,
                PosY = source.PosY,
                PosZ = source.PosZ,
                Heading = source.Heading,
                Flags = source.Flags,
                ItemGenerator = source.ItemGenerator,
                ItemCount = source.ItemCount,
                ItemGenerator2 = source.ItemGenerator2,
                ItemCount2 = source.ItemCount2,
                ItemGenerator3 = source.ItemGenerator3,
                ItemCount3 = source.ItemCount3,
                ItemGenerator4 = source.ItemGenerator4,
                ItemCount4 = source.ItemCount4,
                ItemGenerator5 = source.ItemGenerator5,
                ItemCount5 = source.ItemCount5,
                TargetZone = source.TargetZone,
                TargetSpawn = source.TargetSpawn,
                Label = source.Label,
                AllowMultiple = source.AllowMultiple
            };
        }

        private WorldEntityData ResolveWorldEntityDataForConnection(RRConnection conn, WorldEntityData data)
        {
            if (data == null || !data.IsTeleporter)
                return data;
            if (!TryGetProceduralDungeonSnapshot(conn, out var snapshot))
                return data;

            var clone = CloneWorldEntityData(data);
            DungeonPortalRole role = ResolveDungeonPortalRole(conn.CurrentZoneName, clone.GCType, clone.Name, clone.TargetZone, clone.TargetSpawn);
            ResolveDungeonPortalAnchor(snapshot, role, out var position, out float heading, out int sourceIndex, out string tileType, out int gridX, out int gridY, out var local, out string source);
            clone.PosX = position.x;
            clone.PosY = position.y;
            clone.PosZ = position.z;
            clone.Heading = heading;
            Debug.LogError($"[DUNGEON-PORTAL] world-teleporter zone={snapshot.ZoneName} layoutSeed=0x{snapshot.LayoutSeed:X8} roomSeed=0x{snapshot.RoomSeed:X8} label={clone.Label} gc={clone.GCType} target={clone.TargetZone} spawnPoint={clone.TargetSpawn} role={role.ToString().ToLowerInvariant()} src={sourceIndex} tile='{tileType}' grid=({gridX},{gridY}) local=({local.x:F1},{local.y:F1},{local.z:F1}) rawAuthoredWorld=({clone.PosX:F1},{clone.PosY:F1},{clone.PosZ:F1}) sentWorld=({clone.PosX:F1},{clone.PosY:F1},{clone.PosZ:F1}) snapApplied=False heading={clone.Heading:F1} source='{source}'");
            return clone;
        }

        private void SendZoneWorldEntities(RRConnection conn)
        {
            if (WorldEntitySpawner.Instance == null) return;
            string zoneName = conn.CurrentZoneName;
            if (string.IsNullOrEmpty(zoneName)) return;

            var entities = WorldEntitySpawner.Instance.GetEntitiesForZone(zoneName);
            if (entities.Count == 0) return;

            Debug.LogError($"[WORLD-ENTITIES] Spawning {entities.Count} world entities in {zoneName}");

            var writer = new LEWriter();
            writer.WriteByte(0x07);  // BeginStream

            foreach (var ent in entities)
            {
                ushort entId = (ushort)_nextEntityId++;
                ushort behaviorId = (ushort)_nextEntityId++;
                ent.EntityId = entId;
                var data = ResolveWorldEntityDataForConnection(conn, ent.Data);

                WorldEntitySpawner.WriteEntitySpawn(writer, entId, behaviorId, data);
                WorldEntitySpawner.Instance.TrackSpawnedEntity(entId, data);

                _allEntityPositions[entId] = (data.PosX, data.PosY, data.PosZ);

                // World entity chests use NCI activation (open animation) — NOT the old chest handler
                // Loot spawning will be added separately after NCI activation
                // Do NOT register in _chestEntities — that handler despawns them immediately

                Debug.LogError($"[WORLD-ENTITY] {data.EntityType}: {data.Label} ({data.GCType}) id=0x{entId:X4} at ({data.PosX:F0},{data.PosY:F0},{data.PosZ:F0})");
            }

            writer.WriteByte(0x06);  // EndStream

            byte[] packet = writer.ToArray();
            SendCompressedA(conn, 0x01, 0x0f, packet);
            Debug.LogError($"[WORLD-ENTITIES] ✅ Sent {entities.Count} entities ({packet.Length} bytes)");
        }

        // ═══════════════════════════════════════════════════════════════
        // TELEPORTER ACTIVATION — zone player to target
        // ═══════════════════════════════════════════════════════════════

        private void HandleTeleporterActivation(RRConnection conn, ushort componentId,
            ushort targetEntityId, byte responseId, byte sessionId, WorldEntityData teleporter)
        {
            Debug.LogError($"[TELEPORTER] ═══════════════════════════════════════════════");
            Debug.LogError($"[TELEPORTER] {teleporter.Label} → {teleporter.TargetZone} ({teleporter.TargetSpawn})");
            conn.SessionID = sessionId;

            // Send activation response
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

            // Zone the player
            if (!string.IsNullOrEmpty(teleporter.TargetZone))
            {
                float spawnX = 0, spawnY = 0, spawnZ = 0;
                if (!string.IsNullOrEmpty(teleporter.TargetSpawn))
                {
                    var waypoints = DatabaseLoader.GetWaypointsForZone(teleporter.TargetZone);
                    if (waypoints != null)
                    {
                        var wp = waypoints.FirstOrDefault(w =>
                            w.name.Equals(teleporter.TargetSpawn, StringComparison.OrdinalIgnoreCase));
                        if (wp != null)
                        {
                            spawnX = wp.posX;
                            spawnY = wp.posY;
                            spawnZ = wp.posZ;
                            Debug.LogError($"[TELEPORTER] Found spawn '{teleporter.TargetSpawn}' at ({spawnX},{spawnY},{spawnZ})");
                        }
                    }
                }

                conn.PendingSpawnX = spawnX;
                conn.PendingSpawnY = spawnY;
                conn.PendingSpawnZ = spawnZ;

                ChangeZone(conn, teleporter.TargetZone, teleporter.TargetSpawn ?? "");
                Debug.LogError($"[TELEPORTER] Player {conn.LoginName} teleported to {teleporter.TargetZone}");
            }
        }

        private float _lastWorldEntityCheckTime;

        private void HandleEnterPvpZone(RRConnection conn)
        {
            Debug.LogError($"[PVP] {conn.LoginName} marked as in PVP hub area");
        }

        private void HandleRequestPvpMatch(RRConnection conn, byte[] data)
        {
            if (data == null || data.Length < 2)
            {
                Debug.LogError($"[PVP] requestPVPMatch payload too short ({data?.Length ?? 0} bytes)");
                return;
            }

            string archetypeGcType = TryReadGcTypeString(data);
            if (string.IsNullOrEmpty(archetypeGcType))
            {
                Debug.LogError($"[PVP] Could not parse match archetype from payload (hex: {BitConverter.ToString(data)})");
                return;
            }
            Debug.LogError($"[PVP] {conn.LoginName} requested archetype: '{archetypeGcType}'");

            if (!Managers.PVPMatchManager.TryParseArchetype(archetypeGcType,
                    out Managers.PVPMatchManager.Archetype archetype))
            {
                Debug.LogError($"[PVP] Unknown archetype '{archetypeGcType}'");
                return;
            }

            int rating = 1500;
            try
            {
                var dbChar = Database.CharacterRepository.GetCharacterByName(conn.LoginName, conn.LoginName);
                if (dbChar != null && dbChar.pvpRating > 0)
                    rating = dbChar.pvpRating;
            }
            catch (Exception ex) { Debug.LogError($"[PVP] Could not load PvP rating: {ex.Message}"); }

            uint charSqlId = conn.CharSqlId;
            bool ok = Managers.PVPMatchManager.Instance.EnqueuePlayer(
                conn.LoginName, charSqlId, archetype, rating);

            if (!ok)
            {
                Debug.LogError($"[PVP] {conn.LoginName} could not be enqueued");
                return;
            }

            ProcessMatchmakingTick(forceRun: true);
        }

        private void HandleCancelPvpMatch(RRConnection conn)
        {
            bool removed = Managers.PVPMatchManager.Instance.DequeuePlayer(conn.LoginName);
            Debug.LogError($"[PVP] cancel queue for {conn.LoginName}: {(removed ? "OK" : "was not queued")}");
        }

        private void HandleLeavePvp(RRConnection conn)
        {
            Managers.PVPMatchManager.Instance.DequeuePlayer(conn.LoginName);
            var match = Managers.PVPMatchManager.Instance.HandleDisconnect(conn.LoginName);
            if (match != null)
            {
                Debug.LogError($"[PVP] {conn.LoginName} forfeited match {match.MatchId}");
                FinalizeMatchResults(match);
            }
            if (_zones.TryGetValue(conn.CurrentZoneId, out var z) && IsPvpZone(z.name))
                ChangeZone(conn, "town", "");
        }

        public void ProcessMatchmakingTick(bool forceRun = false)
        {
            if (!forceRun && (DateTime.UtcNow - _lastMatchmakingTick).TotalMilliseconds < 1000)
                return;
            _lastMatchmakingTick = DateTime.UtcNow;

            // Drain pending duel Countdown→Active activations.
            if (_pendingDuelActivations.Count > 0)
            {
                var now = DateTime.UtcNow;
                for (int i = _pendingDuelActivations.Count - 1; i >= 0; i--)
                {
                    var (dueAt, duel) = _pendingDuelActivations[i];
                    if (dueAt <= now)
                    {
                        _pendingDuelActivations.RemoveAt(i);
                        // Skip if duel was cancelled (declined / disconnect) before countdown elapsed.
                        if (_duelManager.ActivateCombat(duel.ChallengerLogin))
                            SendCombatStart(duel);
                    }
                }
            }

            var (newMatches, endedMatches) = Managers.PVPMatchManager.Instance.Tick();
            foreach (var match in newMatches)
                SpawnMatchParticipants(match);
            foreach (var match in endedMatches)
                FinalizeMatchResults(match);
        }

        private void SpawnMatchParticipants(Managers.PVPMatchManager.Match match)
        {
            if (!_zones.Values.Any(z => z.name.Equals(match.ZoneName, StringComparison.OrdinalIgnoreCase)))
            {
                Debug.LogError($"[PVP-MATCH] Zone '{match.ZoneName}' not loaded — match {match.MatchId} cannot start");
                Managers.PVPMatchManager.Instance.EndMatch(match.MatchId, "zone not loaded");
                return;
            }

            foreach (var login in match.ParticipantLogins)
            {
                var pConn = _connections.Values.FirstOrDefault(c =>
                    c.LoginName?.Equals(login, StringComparison.OrdinalIgnoreCase) == true);
                if (pConn == null) { Debug.LogError($"[PVP-MATCH] {login} not connected"); continue; }
                Debug.LogError($"[PVP-MATCH] Spawning {login} into {match.ZoneName}#{match.InstanceId}");
                ChangeZone(pConn, match.ZoneName, "");
            }
            Managers.PVPMatchManager.Instance.MarkMatchStarted(match.MatchId);
        }

        private void FinalizeMatchResults(Managers.PVPMatchManager.Match match)
        {
            if (string.IsNullOrEmpty(match.WinnerLogin))
            {
                Debug.LogError($"[PVP-MATCH] Match {match.MatchId} ended with no winner");
                return;
            }

            bool isRanked = Managers.PVPMatchManager.IsRanked(match.Archetype);

            foreach (var login in match.ParticipantLogins.Concat(new[] { match.WinnerLogin }).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                bool isWinner = login.Equals(match.WinnerLogin, StringComparison.OrdinalIgnoreCase);
                int ratingDelta = isRanked ? (isWinner ? +25 : -20) : 0;
                try
                {
                    Database.CharacterRepository.UpdatePvpRecord(login, isWinner, ratingDelta);
                    Debug.LogError($"[PVP-MATCH] {login}: {(isWinner ? "WIN" : "LOSS")}, rating Δ {ratingDelta:+#;-#;0}");
                    var pConn = _connections.Values.FirstOrDefault(c =>
                        c.LoginName?.Equals(login, StringComparison.OrdinalIgnoreCase) == true);
                    if (pConn != null && _zones.TryGetValue(pConn.CurrentZoneId, out var z) && IsPvpZone(z.name))
                        ChangeZone(pConn, "town", "");
                }
                catch (Exception ex) { Debug.LogError($"[PVP-MATCH] Failed record for {login}: {ex.Message}"); }
            }
        }

        private static bool IsPvpZone(string zoneName)
        {
            if (string.IsNullOrEmpty(zoneName)) return false;
            return zoneName.StartsWith("PVPGroup", StringComparison.OrdinalIgnoreCase)
                || zoneName.StartsWith("DeathMatch", StringComparison.OrdinalIgnoreCase);
        }

        private static string TryReadGcTypeString(byte[] data)
        {
            if (data == null || data.Length < 2) return null;
            if (data[0] != 0xFF) return null;
            int end = 1;
            while (end < data.Length && data[end] != 0) end++;
            if (end <= 1) return null;
            return System.Text.Encoding.ASCII.GetString(data, 1, end - 1);
        }

        /// <summary>
        /// Send group state to ALL group members.
        /// S65C binary-verified fixes:
        ///   - leaderCharId must be charSQLID (binary@0x5F8C85: cmp member+0xC vs GC+0xB0)
        ///   - selfId1/selfId2 must be 0 (binary@0x5F9652→GC+0xBC, gate@0x470B13→bit1 of 0x19C)
        ///   - 4th byte of 0x35 = isOpenGroup (binary@0x5F8A28→GC+0xD8 bit 0), NOT isLeader
        ///   - 0x30 sent ONCE (deferred callback@0x434AC0 resets UI if repeated)
        /// </summary>
        public void SendGroupConnectedToAll(Managers.Group group)
        {
            var leaderConn = FindConnectionById(group.LeaderConnId);

            // GC+0xB0 must be a valid zoneGUID. If leader is offline, use any online member's.
            // All players in same zone have same zoneGUID, so any online member works.
            uint leaderGroupId = 0;
            if (leaderConn != null)
                leaderGroupId = leaderConn.CurrentZoneId;
            else
            {
                foreach (var m in group.Members)
                {
                    if (!m.IsOnline) continue;
                    var mc = FindConnectionById(m.ConnId);
                    if (mc != null) { leaderGroupId = mc.CurrentZoneId; break; }
                }
            }

            // Build member list with REAL charSqlIds, leader FIRST.
            // Binary @0x435D4F: HP bars created in member vector order (index 0, 1, 2...).
            // Leader first in list = leader bar on top of group portrait area.
            // Include offline members with IsOnline=false — binary readMember@0x5FA6D0
            // reads the isOnline byte and stores at member+0x18.
            var members = new System.Collections.Generic.List<GroupMemberInfo>();
            // Leader first
            var leaderMember = group.Members.Find(m => m.ConnId == group.LeaderConnId);
            if (leaderMember != null)
            {
                var mc = FindConnectionById(leaderMember.ConnId);
                if (mc != null && leaderMember.IsOnline)
                    members.Add(BuildGroupMemberInfo(mc));
                else if (leaderMember.CharSqlId != 0)
                    members.Add(BuildGroupMemberInfoFromCache(leaderMember));
            }
            // Then non-leaders
            foreach (var m in group.Members)
            {
                if (m.ConnId == group.LeaderConnId) continue;
                var mc = FindConnectionById(m.ConnId);
                if (mc != null && m.IsOnline)
                    members.Add(BuildGroupMemberInfo(mc));
                else if (m.CharSqlId != 0)
                    members.Add(BuildGroupMemberInfoFromCache(m));
            }

            foreach (var m in group.Members)
            {
                if (!m.IsOnline) continue;  // only send to online members
                var mc = FindConnectionById(m.ConnId);
                if (mc != null)
                {
                    if (!mc.GroupConnectedSent)
                    {
                        // 0x30 field1 → GC+0xB0 = zoneGUID (Gate 2 requirement)
                        byte[] connectedPacket = GroupPackets.BuildProcessConnected(
                            leaderGroupId, group.MonsterDifficulty, group.InviteMode);
                        SendToClient(mc, connectedPacket);
                        mc.GroupConnectedSent = true;
                        Debug.LogError($"[GROUP] Sent 0x30 to {mc.LoginName}: GC+0xB0=0x{leaderGroupId:X8}");
                    }

                    byte isOpenGroup = (byte)(group.IsOpen ? 1 : 0);
                    // 0x35 field2 → GC+0xD4 = zoneGUID (Gate 1 requirement)
                    byte[] groupPacket = GroupPackets.BuildProcessUserChangedGroup(
                        group.GroupId, mc.CurrentZoneId, 0xFF, isOpenGroup,
                        0, 0,
                        (byte)members.Count, members.ToArray());
                    SendToClient(mc, groupPacket);
                    Debug.LogError($"[GROUP] Sent 0x35 to {mc.LoginName}: GC+0xD4=0x{mc.CurrentZoneId:X8}, {members.Count} members");
                }
            }

            // 0x44 NOT sent — sets member+0x30=1 @0x5F9236 which breaks HP bars.
        }

        /// <summary>Send health/mana (0x4B) for all members to all members.</summary>
        private void SendGroupHealthToAll(Managers.Group group)
        {
            foreach (var m in group.Members)
            {
                if (!m.IsOnline) continue;
                var mc = FindConnectionById(m.ConnId);
                if (mc == null) continue;
                uint charSqlId = GetCharSqlId(mc);
                byte[] healthPacket = GroupPackets.BuildMemberHealthMana(charSqlId, 15, 15);
                foreach (var target in group.Members)
                {
                    if (!target.IsOnline) continue;
                    var tc = FindConnectionById(target.ConnId);
                    if (tc != null) SendToClient(tc, healthPacket);
                }
            }
        }

        private void HandleGroupChannel(RRConnection conn, byte messageType, byte[] data)
        {
            Debug.LogError($"[CH9] type=0x{messageType:X2} len={data?.Length ?? 0} data={BitConverter.ToString(data ?? new byte[0])}");

            if (_adminHandler == null)
                _adminHandler = new AdminCommandHandler();
            _adminHandler.SetServerCallbacks(
                (c, zone) => ChatChangeZone(c, zone),
                (c) => {
                    if (_selectedCharacter.TryGetValue(c.LoginName, out var ch))
                        return CharacterRepository.GetCharacter(ch.Id);
                    return null;
                },
                HandleAdminLevelUp,
                (login, gcType, modId) => _modifierTracker.TrackModifier(login, new ActiveModifier
                {
                    GCType = gcType,
                    Id = modId,
                    Level = 0,
                    PowerLevel = 0,
                    Duration = 0,
                    SourceIsSelf = 0,
                    AddedAt = DateTime.UtcNow
                }),
                (login, modId) => _modifierTracker.RemoveModifierById(login, modId),
                AdminCompleteQuest,
                (c, w) => { WritePlayerEntitySynch(c, w); }
            );

            PlayerState ps = GetPlayerState(conn.ConnId.ToString());

            if (IsPlayerAdmin(conn.LoginName) && _adminHandler.TryHandleAdminCommand(conn, messageType, data, ps, SendSystemMessage, SendToClient))
            {
                // Admin command handled - send ack
                var ack = new LEWriter();
                ack.WriteByte(9);
                ack.WriteByte(messageType);
                SendCompressedA(conn, 0x01, 0x0F, ack.ToArray());
                return;
            }

            // Forward group commands to HandleGroupClientChannel (ch0B) where handlers live
            switch (messageType)
            {
                case 0x00:
                    Debug.Log($"[GROUP] Connect ack from {conn.LoginName}");
                    return;
                case 0x16:
                case 0x12:
                case 0x20:
                case 0x21:
                case 0x22:
                case 0x14:
                case 0x15:
                case 0x17:
                case 0x24:
                case 0x26:
                case 0x27:
                case 0x28:
                case 0x29: // enterPVPZone
                case 0x2A: // requestPVPMatch
                case 0x2B: // cancelPVPMatch
                case 0x2C: // leavePVP
                case 0x2D: // requestPVPDuel
                case 0x2E: // acceptPVPDuel
                case 0x2F: // declinePVPDuel
                    HandleGroupClientChannel(conn, messageType, data);
                    return;
            }

            // Not an admin command - default ack
            Debug.Log($"Group channel message type: 0x{messageType:X2}");
            var groupAck = new LEWriter();
            groupAck.WriteByte(9);
            groupAck.WriteByte(messageType);
            SendCompressedA(conn, 0x01, 0x0F, groupAck.ToArray());
        }

        // ═══════════════════════════════════════════════════════════════════════════════
        // FIX #2: HandleZoneChannel - Use correct zone ID instead of hardcoded 1
        // ═══════════════════════════════════════════════════════════════════════════════

        private void HandleZoneChannel(RRConnection conn, byte[] body)
        {
            // CRITICAL FIX: Handle empty body - client sends empty 13/6 request!
            Debug.LogError($"🔵 HandleZoneChannel: bodyLen={body?.Length ?? 0}, body={BitConverter.ToString(body ?? new byte[0])}");

            if (body == null || body.Length == 0)
            {
                // Only process zone join if player hasn't spawned yet
                if (conn.TickCoroutine != null)
                {
                    Debug.LogError("[ZONE-JOIN] Player already spawned — ignoring duplicate zone join request");
                    return;
                }

                Debug.Log("CLIENT SENT EMPTY 13/6 PROGRESSION REQUEST - Starting zone progression!");

                // ✅ FIX: Zone ID already set in HandleCharacterPlay - DON'T overwrite it!
                Debug.LogError($"[ZONE-JOIN] Using zone ID from HandleCharacterPlay: {conn.CurrentZoneId} (0x{conn.CurrentZoneId:X8})");

                Debug.Log("ZoneClient should be ready - about to send progression messages");

                // STEP 1: Send ZoneChannel + ZoneMessageReady (ID 1) - should trigger State 110
                Debug.Log("STEP 1: Sending ZoneChannel + ZoneMessageReady (ID 1) - should trigger State 110");
                LEWriter zoneReadyWriter = new LEWriter();
                zoneReadyWriter.WriteByte(13);                    // ZoneChannel (like GO server)
                zoneReadyWriter.WriteByte(1);                     // ZoneMessageReady (ID 1)

                // 🔥 FIX: Use actual zone ID, NOT hardcoded 1!
                zoneReadyWriter.WriteUInt32(conn.CurrentZoneId);  // Zone ID - FIXED!
                Debug.LogError($"[STEP1-FIX] Using zone ID: {conn.CurrentZoneId} (0x{conn.CurrentZoneId:X8}) instead of hardcoded 1");

                ushort exploredBitCount = ResolveExploredBitCount(conn, "ZoneProgressionEmpty");

                zoneReadyWriter.WriteUInt16(exploredBitCount);
                for (int i = 0; i < exploredBitCount; i++)
                {
                    zoneReadyWriter.WriteUInt32(0x00000000);
                }

                byte[] step1Data = zoneReadyWriter.ToArray();
                Debug.LogError($"[STEP1] ZoneMessageReady: {step1Data.Length} bytes");
                Debug.LogError($"[STEP1] Hex: {BitConverter.ToString(step1Data.Take(20).ToArray())}...");

                SendCompressedA(conn, 0x01, 0x0F, step1Data);
                Debug.Log("STEP 1 COMPLETE: Sent ZoneChannel + ZoneMessageReady (ID 1)");

                // STEP 2: Send ZoneChannel + ZoneMessageInstanceCount (ID 5) - like GO server
                Debug.Log("STEP 2: Sending ZoneChannel + ZoneMessageInstanceCount (ID 5) - should trigger States 114, 115");
                LEWriter zoneInstanceWriter = new LEWriter();
                zoneInstanceWriter.WriteByte(13);                 // ZoneChannel (like GO server)
                zoneInstanceWriter.WriteByte(5);                  // ZoneMessageInstanceCount (ID 5)
                zoneInstanceWriter.WriteUInt32(0x00);
                zoneInstanceWriter.WriteUInt32(0x00);
                SendCompressedA(conn, 0x01, 0x0F, zoneInstanceWriter.ToArray());
                Debug.Log("STEP 2 COMPLETE: Sent ZoneChannel + ZoneMessageInstanceCount (ID 5)");

                // STEP 3: Send ClientEntityChannel + Interval (ID 0x0D) - like GO server
                Debug.Log("STEP 3: Sending ClientEntityChannel + Interval (ID 0x0D)");
                int tick = (int)(Time.time / 0.033f);
                LEWriter intervalWriter = new LEWriter();
                intervalWriter.WriteByte(7);
                intervalWriter.WriteByte(0x0D);
                intervalWriter.WriteInt32(tick);
                intervalWriter.WriteInt32(33);
                Debug.LogError($"[INTERVAL] tick={tick}, tickInterval=33");
                intervalWriter.WriteInt32(3);                     // Movement prediction buffer (from GO server)
                intervalWriter.WriteInt32(1);                     // PathManager budget (from GO server)
                intervalWriter.WriteUInt16(100);                  // Budget Per Update (from GO server)
                intervalWriter.WriteUInt16(20);                   // Budget Per Path (from GO server)

                // CRITICAL FIX: Add stream end marker like GO server does
                intervalWriter.WriteByte(0x06);                   // AddEntityUpdateStreamEnd

                Debug.Log($"INTERVAL MESSAGE SIZE: {intervalWriter.ToArray().Length} bytes (before compression)");
                Debug.Log($"INTERVAL DATA: {BitConverter.ToString(intervalWriter.ToArray())}");
                SendCompressedA(conn, 0x01, 0x0F, intervalWriter.ToArray());
                Debug.Log("STEP 3 COMPLETE: Sent ClientEntityChannel + Interval (ID 0x0D)");

                bool zoneRuntimePrepared = TryPrepareZoneJoinRoomRuntime(
                    conn,
                    "ZONE-JOIN pre-player",
                    out Zone preparedSpawnZone,
                    out string preparedZoneName,
                    out string preparedInstanceKey,
                    out uint preparedRoomSeed,
                    out uint preparedLayoutSeed);

                // STEP 4: SEND SPAWN DATA
                Debug.Log("STEP 4: SENDING SPAWN DATA (before 'Now connected' message!)");
                SendPlayerEntitySpawn(conn);
                StartCoroutine(SendZoneSpawnInvulnerabilityAfterDelay(conn, ZoneSpawnInvulnerabilityAddDelay));
                Debug.Log("STEP 4 COMPLETE: Spawn data sent");

                // Step 4b: Spawn zone mobs + send to client (INSTANCE-AWARE)
                // Binary: ServerEntityManager::writeClientInitMessages — sends existing entities to new client
                // Binary: DungeonGenerator::generate(Random) — same seed = same dungeon for all group members
                // Binary: ZoneClient::GotoInstance(int) — each group gets own dungeon instance
                if ((zoneRuntimePrepared && preparedSpawnZone != null) || _zones.TryGetValue(conn.CurrentZoneId, out preparedSpawnZone))
                {
                    string zoneName = zoneRuntimePrepared ? preparedZoneName : preparedSpawnZone.name;
                    string instanceKey = zoneRuntimePrepared ? preparedInstanceKey : GetInstanceZoneKey(conn);
                    Debug.LogError($"[ZONE-JOIN] Zone: '{zoneName}' instance: '{instanceKey}' (inst={conn.InstanceId})");

                    // Check if THIS INSTANCE already has mobs (group member already here).
                    // Native Q112 keeps a remembered dungeon after a quick relog even when all mobs are dead.
                    bool mobsAlreadyExist = CombatManager.Instance.GetMonstersInZone(instanceKey).Any();
                    bool continuousPresence = _connections.Values.Any(o =>
                        o != null && o != conn && o.IsSpawned &&
                        string.Equals(GetInstanceZoneKey(o), instanceKey, StringComparison.OrdinalIgnoreCase));
                    if (mobsAlreadyExist && !continuousPresence)
                    {
                        int reentryReset = CombatManager.Instance.ResetMonstersForInstanceToSpawn(instanceKey);
                        Debug.LogError($"[ZONE-JOIN] RE-ENTRY no continuous presence in '{instanceKey}', reset {reentryReset} monsters to spawn");
                    }

                    if (mobsAlreadyExist)
                    {
                        // LATE JOINER — send existing monsters with IDENTICAL packets
                        Debug.LogError($"[ZONE-JOIN] ★ LATE JOINER — mobs already exist in '{instanceKey}', sending existing monsters");
                        if (!zoneRuntimePrepared)
                            SendRandomSeed(conn, ResolveRuntimeZoneSeed(conn, zoneName), false);
                        foreach (var monster in CombatManager.Instance.GetMonstersInZone(instanceKey))
                        {
                            if (!monster.IsAlive) continue;
                            SendMonsterToClient(conn, monster);
                            Debug.LogError($"[ZONE-JOIN] Sent existing monster {monster.Name} (ID:{monster.EntityId}) to late joiner {conn.LoginName}");
                        }
                    }
                    else
                    {
                        // FIRST PLAYER in this instance — spawn fresh mobs
                        _finalizedMonsterKills.Clear();
                        _pendingKills.Clear();
                        _dllPreConfirmCount = 0;

                        uint rngSeed = zoneRuntimePrepared ? preparedRoomSeed : ResolveRuntimeZoneSeed(conn, zoneName);
                        uint layoutSeed = zoneRuntimePrepared ? preparedLayoutSeed : ResolveZoneLayoutSeed(conn, zoneName);
                        if (!zoneRuntimePrepared)
                            SendRandomSeed(conn, rngSeed, false);
                        Debug.LogError($"[ZONE-JOIN] EntityManager opcode 0x0C seed: 0x{rngSeed:X8} {FormatDungeonLayoutSeedForLog(zoneName, layoutSeed)} for instance '{instanceKey}'");

                        // Spawn mobs using real zone name for spawn data, but tag with instanceKey
                        var spawned = ZoneSpawnManager.Instance.SpawnZoneMobsForInstance(zoneName, instanceKey, layoutSeed, rngSeed);

                        // Apply difficulty scaling BEFORE sending to clients
                        ApplyDifficultyToMonsters(conn, instanceKey);

                        foreach (var monster in CombatManager.Instance.GetMonstersInZone(instanceKey))
                        {
                            monster.AggroTriggered = false;
                            monster.State = MonsterState.Idle;
                            monster.TargetId = 0;
                            monster.RngSeed = rngSeed;

                            SendMonsterToClient(conn, monster);
                            Debug.LogError($"[ZONE-JOIN] Sent zone monster {monster.Name} to client");
                        }
                    }
                }

                // STEP 5: RNG Seed - AFTER spawn is complete
                //  uint seed = (uint)(DateTime.Now.Ticks & 0xFFFFFFFF);
                //   SendRNGSeedUDP(conn, seed);
                //   CombatManager.Instance.InitializeRandomSeed(seed);
                //   Debug.LogError($"[RNG] Server sent seed: 0x{seed:X8}");

                //  Debug.Log("═══════════════════════════════════════════════════════════════");
                //  Debug.Log("ALL ZONE PROGRESSION MESSAGES COMPLETE!");
                // Debug.Log("═══════════════════════════════════════════════════════════════");

                // S65C: Preserve group state across zone transitions.
                // Binary: Leave Group gate at 0x46F419 checks member vector — solo 0x35 wipes it.
                {
                    var zoneGroup = GroupManager.Instance.GetGroupForConn(conn.ConnId);
                    if (zoneGroup != null)
                    {
                        GroupManager.Instance.UpdateMemberZone(conn.ConnId, conn.CurrentZoneName ?? "");
                        SendGroupConnectedToAll(zoneGroup);
                        SendGroupHealthToAll(zoneGroup);
                        Debug.LogError($"[GROUP] Sent full group state for zone join: group={zoneGroup.GroupId}");
                    }
                    else
                    {
                        byte[] groupState = GroupPackets.BuildProcessUserChangedGroup(
                            1, conn.CurrentZoneId, 0xFF, 0,
                            0, 0,
                            0, new GroupMemberInfo[0]);
                        SendCompressedA(conn, 0x01, 0x0F, groupState);
                        Debug.LogError($"[GROUP] Sent 0x35 solo: GC+0xD4=0x{conn.CurrentZoneId:X8}");
                    }
                }

                // Re-send tracked admin modifiers after zone transition spawn
                ResendAllModifiers(conn);

                return;
            }

            // Handle non-empty zone channel messages
            if (body.Length >= 1)
            {
                byte type = body[0];
                Debug.Log($"Zone channel message type: 0x{type:X2}");

                if (type == 0x06)
                {
                    // Guard: if player is already spawned, this is a spurious 0x06
                    // (e.g. from a zone transition packet arriving late, or a client
                    // resend). Re-running the full spawn sequence causes the screen jump.
                    if (conn.TickCoroutine != null)
                    {
                        Debug.LogError($"[ZONE-JOIN] ⚠️ Got 0x06 but player already spawned (TickCoroutine active) — IGNORING to prevent screen jump. body={BitConverter.ToString(body)}");
                        return;
                    }

                    conn.AllowFlush = false;
                    Debug.LogError($"[ZONE-JOIN] 🔥 Client sent ZoneJoin (0x06) - FULL SPAWN SEQUENCE");

                    // ═══════════════════════════════════════════════════════════════
                    // ZONE TRANSITION CHECK: Skip spawn if client already has entities
                    // GO SERVER PATTERN - Don't re-spawn player's own Avatar/Player
                    // ═══════════════════════════════════════════════════════════════
                    bool isZoneTransition = (conn.Avatar != null && conn.Player != null);
                    Debug.LogError($"[ZONE-JOIN] isZoneTransition={isZoneTransition} (Avatar={(conn.Avatar != null ? conn.Avatar.Id.ToString() : "null")}, Player={(conn.Player != null ? conn.Player.Id.ToString() : "null")})");

                    /* if (isZoneTransition)
                     {
                         Debug.LogError("[ZONE-JOIN] ZONE TRANSITION - Sending position update");

                         _allowFlush = true;

                         // Send portals and checkpoints
                         SendZonePortals(conn, conn.CurrentZoneId);
                         SpawnReturnTownPortal(conn);
                         SendZoneCheckpoints(conn, conn.CurrentZoneId);
                         SendZoneChests(conn, conn.CurrentZoneName);
                         SendZoneWorldEntities(conn);

                         // 🔥 CRITICAL: Send position/warp update for dungeon zones!
                         GCObject ub = conn.Avatar?.Children?.FirstOrDefault(c => c.GCClass == "UnitBehavior");
                         if (ub != null)
                         {
                             SendZoneTransitionWarp(conn, (ushort)ub.Id);
                             conn.UnitBehaviorId = (uint)ub.Id;
                             conn.TickCoroutine = StartCoroutine(SendTickUpdates(conn, ub.Id));
                         }

                         return;
                     }*/
                    // ═══════════════════════════════════════════════════════════════

                    // FIRST LOGIN - Full spawn sequence
                    Debug.LogError("[ZONE-JOIN] FIRST LOGIN - Sending full spawn sequence");

                    ushort exploredBitCount = ResolveExploredBitCount(conn, "ZoneJoin");

                    // Step 1: ZoneMessageReady - MUST include zone ID + explored bits!
                    var readyWriter = new LEWriter();
                    readyWriter.WriteByte(13);
                    readyWriter.WriteByte(1);  // 🔥 FIX: 0x01, not 0x03!
                    readyWriter.WriteUInt32(conn.CurrentZoneId);  // 🔥 FIX: Zone ID was missing!
                    readyWriter.WriteUInt16(exploredBitCount);
                    for (int i = 0; i < exploredBitCount; i++)
                    {
                        readyWriter.WriteUInt32(0x00000000);
                    }
                    SendCompressedA(conn, 0x01, 0x0F, readyWriter.ToArray());

                    // Step 2: InstanceCount
                    var instanceWriter = new LEWriter();
                    instanceWriter.WriteByte(13);
                    instanceWriter.WriteByte(5);  // 🔥 FIX: 0x05, not 0x04!
                    instanceWriter.WriteUInt32(0x00);
                    instanceWriter.WriteUInt32(0x00);
                    SendCompressedA(conn, 0x01, 0x0F, instanceWriter.ToArray());

                    // Step 3: Interval
                    int tick = (int)(Time.time / 0.033f);
                    var intervalWriter = new LEWriter();
                    intervalWriter.WriteByte(7);
                    intervalWriter.WriteByte(0x0D);
                    intervalWriter.WriteInt32(tick);
                    intervalWriter.WriteInt32(33);
                    intervalWriter.WriteInt32(3);
                    intervalWriter.WriteInt32(1);
                    intervalWriter.WriteUInt16(100);
                    intervalWriter.WriteUInt16(20);
                    intervalWriter.WriteByte(0x06);
                    SendCompressedA(conn, 0x01, 0x0F, intervalWriter.ToArray());

                    bool zoneRuntimePrepared = TryPrepareZoneJoinRoomRuntime(
                        conn,
                        "ZONE-JOIN pre-player",
                        out Zone preparedSpawnZone,
                        out string preparedZoneName,
                        out string preparedInstanceKey,
                        out uint preparedRoomSeed,
                        out uint preparedLayoutSeed);

                    // Step 4: Spawn player
                    SendPlayerEntitySpawn(conn);
                    StartCoroutine(SendZoneSpawnInvulnerabilityAfterDelay(conn, ZoneSpawnInvulnerabilityAddDelay));

                    // Step 4b: Spawn zone mobs + send to client (GROUP-AWARE)
                    // Binary: ServerEntityManager::writeClientInitMessages — sends existing entities to new client
                    if ((zoneRuntimePrepared && preparedSpawnZone != null) || _zones.TryGetValue(conn.CurrentZoneId, out preparedSpawnZone))
                    {
                        string zoneName = zoneRuntimePrepared ? preparedZoneName : preparedSpawnZone.name;
                        string instanceKey = zoneRuntimePrepared ? preparedInstanceKey : GetInstanceZoneKey(conn);
                        Debug.LogError($"[ZONE-JOIN] Zone: '{zoneName}' instance: '{instanceKey}' (inst={conn.InstanceId})");

                        // Native Q112 keeps a remembered dungeon after a quick relog even when all mobs are dead.
                        bool mobsAlreadyExist = CombatManager.Instance.GetMonstersInZone(instanceKey).Any();
                        bool continuousPresence = _connections.Values.Any(o =>
                            o != null && o != conn && o.IsSpawned &&
                            string.Equals(GetInstanceZoneKey(o), instanceKey, StringComparison.OrdinalIgnoreCase));
                        if (mobsAlreadyExist && !continuousPresence)
                        {
                            int reentryReset = CombatManager.Instance.ResetMonstersForInstanceToSpawn(instanceKey);
                            Debug.LogError($"[ZONE-JOIN] RE-ENTRY no continuous presence in '{instanceKey}', reset {reentryReset} monsters to spawn");
                        }

                        if (mobsAlreadyExist)
                        {
                            Debug.LogError($"[ZONE-JOIN] ★ LATE JOINER — mobs already exist in '{instanceKey}', sending existing monsters");
                            if (!zoneRuntimePrepared)
                                SendRandomSeed(conn, ResolveRuntimeZoneSeed(conn, zoneName), false);
                            foreach (var monster in CombatManager.Instance.GetMonstersInZone(instanceKey))
                            {
                                if (!monster.IsAlive) continue;
                                SendMonsterToClient(conn, monster);
                                Debug.LogError($"[ZONE-JOIN] Sent existing monster {monster.Name} (ID:{monster.EntityId}) to late joiner {conn.LoginName}");
                            }
                        }
                        else
                        {
                            _finalizedMonsterKills.Clear();
                            _pendingKills.Clear();
                            _dllPreConfirmCount = 0;

                            uint rngSeed = zoneRuntimePrepared ? preparedRoomSeed : ResolveRuntimeZoneSeed(conn, zoneName);
                            uint layoutSeed = zoneRuntimePrepared ? preparedLayoutSeed : ResolveZoneLayoutSeed(conn, zoneName);
                            if (!zoneRuntimePrepared)
                                SendRandomSeed(conn, rngSeed, false);
                            Debug.LogError($"[ZONE-JOIN] EntityManager opcode 0x0C seed: 0x{rngSeed:X8} {FormatDungeonLayoutSeedForLog(zoneName, layoutSeed)} for instance '{instanceKey}'");

                            var spawned = ZoneSpawnManager.Instance.SpawnZoneMobsForInstance(zoneName, instanceKey, layoutSeed, rngSeed);

                            // Apply difficulty scaling BEFORE sending to clients
                            ApplyDifficultyToMonsters(conn, instanceKey);

                            foreach (var monster in CombatManager.Instance.GetMonstersInZone(instanceKey))
                            {
                                monster.AggroTriggered = false;
                                monster.State = MonsterState.Idle;
                                monster.TargetId = 0;
                                monster.RngSeed = rngSeed;

                                SendMonsterToClient(conn, monster);
                                Debug.LogError($"[ZONE-JOIN] Sent zone monster {monster.Name} to client");
                            }
                        }
                    }

                    // Step 5: RNG Seed - AFTER spawn is complete
                    // uint seed = (uint)(DateTime.Now.Ticks & 0xFFFFFFFF);
                    // SendRNGSeedUDP(conn, seed);
                    //  CombatManager.Instance.InitializeRandomSeed(seed);
                    //  Debug.LogError($"[RNG] Server sent seed: 0x{seed:X8}");
                    //   Debug.LogError("[ZONE-JOIN] ✅ Complete spawn sequence sent");

                    // S65C: Preserve group state across zone transitions
                    {
                        var zoneGroup = GroupManager.Instance.GetGroupForConn(conn.ConnId);
                        if (zoneGroup != null)
                        {
                            GroupManager.Instance.UpdateMemberZone(conn.ConnId, conn.CurrentZoneName ?? "");
                            SendGroupConnectedToAll(zoneGroup);
                            Debug.LogError($"[GROUP] Sent full group state for zone-transition: group={zoneGroup.GroupId}");
                            uint charSqlId = 0;
                            if (_selectedCharacter.TryGetValue(conn.LoginName, out var zc))
                                charSqlId = (uint)zc.Id;
                            byte[] zoneNotify = GroupPackets.BuildUserChangedZone(
                                zoneGroup.GroupId, charSqlId, conn.CurrentZoneName ?? "");
                            foreach (var gm in zoneGroup.Members)
                            {
                                if (gm.ConnId == conn.ConnId) continue;
                                var gmc = FindConnectionById(gm.ConnId);
                                if (gmc != null) SendToClient(gmc, zoneNotify);
                            }
                        }
                        else
                        {
                            byte[] groupState = GroupPackets.BuildProcessUserChangedGroup(
                                1, conn.CurrentZoneId, 0xFF, 0,
                                0, 0, 0, new GroupMemberInfo[0]);
                            SendCompressedA(conn, 0x01, 0x0F, groupState);
                            Debug.LogError($"[GROUP] Sent 0x35 zone-transition solo: GC+0xD4=0x{conn.CurrentZoneId:X8}");
                        }
                    }

                    // Re-send tracked admin modifiers after zone transition spawn
                    ResendAllModifiers(conn);

                    return;
                }
                else if (type == 0x00)
                {
                    Debug.Log("ZoneJoin request (0x00) - acknowledging zone join");
                    LEWriter joinAck = new LEWriter();
                    joinAck.WriteByte(13);
                    joinAck.WriteByte(0x00);
                    SendCompressedA(conn, 0x01, 0x0F, joinAck.ToArray());
                }
                else if (type == 0x01)
                {
                    Debug.Log("ZoneEnter confirmation (0x01) - client successfully entered zone");
                }
                else if (type == 0x08)
                {
                    Debug.Log("IGNORING 13/8 message - do not acknowledge to prevent crashes");
                }
                else
                {
                    Debug.LogWarning($"Unknown zone channel message type: 0x{type:X2}");
                }
            }
        }

        private void SendCharacterList(RRConnection conn)
        {
            Debug.LogError($"[CHARLIST] *** ENTRY *** Sending character list for {conn.LoginName}");

            try
            {
                if (!_persistentCharacters.TryGetValue(conn.LoginName, out var characters))
                {
                    characters = new List<GCObject>();
                    _persistentCharacters[conn.LoginName] = characters;
                }

                // 🔥 FIX: If empty, load from saved JSON
                if (characters.Count == 0)
                {
                    /* DB always current — no reload needed */
                    ;  // Force reload from disk
                    var savedChars = CharacterRepository.GetCharactersForAccount(conn.LoginName);
                    Debug.LogError($"[CHARLIST] Loading {savedChars?.Count ?? 0} characters for account '{conn.LoginName}'");

                    foreach (var savedChar in savedChars ?? new List<SavedCharacter>())
                    {
                        var charObj = new GCObject
                        {
                            Id = savedChar.id,
                            NativeClass = "Player",
                            GCClass = "Player",
                            Name = savedChar.name
                        };
                        characters.Add(charObj);
                        Debug.LogError($"[CHARLIST] Loaded: {savedChar.name} (ID={savedChar.id})");
                    }
                }

                Debug.LogError($"[CHARLIST] Found {characters.Count} characters");

                if (characters.Count == 0)
                {
                    var emptyWriter = new LEWriter();
                    emptyWriter.WriteByte(4);
                    emptyWriter.WriteByte(3);
                    emptyWriter.WriteByte(0);
                    SendCompressedA(conn, 0x01, 0x0f, emptyWriter.ToArray());
                    _charListSent[conn.ConnId] = true;
                    return;
                }



                // Character list entities use a high ID range (10000+) to avoid
                // colliding with game spawn entities (10-500) which include player,
                // NPCs, portals, checkpoints. Old value of 100 caused crashes when
                // NPC count pushed game IDs past 170 into character list range.
                // Character list uses temporary IDs in a high range.
                // Save/restore the real counter so game IDs aren't clobbered.
                uint savedNextEntityId = _nextEntityId;
                _nextEntityId = 10000;
                var sortedCharacters = characters.OrderBy(c => c.Id).ToList();

                int slotIndex = 0;
                foreach (var character in sortedCharacters)
                {
                    // ═══════════════════════════════════════════════════════════════
                    // 🔥 FIX: USE TYPE 2 FORMAT LIKE HandleCharacterCreate
                    // ═══════════════════════════════════════════════════════════════
                    var writer = new LEWriter();
                    writer.WriteByte(4);
                    writer.WriteByte(2);  // 🔥 TYPE 2 - same as HandleCharacterCreate!
                    writer.WriteUInt32(character.Id);
                    writer.WriteCString(character.Name);  // 🔥 ADD NAME - same as HandleCharacterCreate!

                    GCObject tempChar = GCObjectFactory.NewPlayer(character.Name);
                    tempChar.Id = character.Id;  // 🔥 USE CHARACTER ID - same as HandleCharacterCreate!

                    SavedCharacter savedChar = CharacterRepository.GetCharacter(character.Id);
                    if (savedChar == null)
                    {
                        savedChar = CharacterRepository.CreateCharacter(character.Name, "Fighter", AccountRepository.GetAccountId(conn.LoginName), conn.LoginName);
                    }

                    var avatar = GCObjectFactory.LoadAvatar(savedChar);
                    avatar.Id = _nextEntityId++;

                    foreach (var child in avatar.Children)
                    {
                        child.Id = _nextEntityId++;
                        if (child.Children != null)
                        {
                            foreach (var grandchild in child.Children)
                            {
                                grandchild.Id = _nextEntityId++;
                            }
                        }
                    }

                    // ADD THIS DEBUG BLOCK:
                    Debug.LogError($"[CHARLIST-IDS] Character: {character.Name} (slot {slotIndex})");
                    var equipComp = avatar.Children.FirstOrDefault(c => c.NativeClass == "Equipment");
                    var manipComp = avatar.Children.FirstOrDefault(c => c.NativeClass == "Manipulators");
                    if (equipComp != null)
                    {
                        foreach (var eq in equipComp.Children)
                        {
                            Debug.LogError($"[CHARLIST-IDS]   Equipment child: {eq.GCClass} ID={eq.Id}");
                        }
                    }
                    if (manipComp != null)
                    {
                        foreach (var man in manipComp.Children)
                        {
                            Debug.LogError($"[CHARLIST-IDS]   Manipulators child: {man.GCClass} ID={man.Id}");
                        }
                    }

                    tempChar.AddChild(avatar);

                    // SIMPLE DEBUG
                    Debug.LogError($"[CHARLIST-DEBUG] Slot {slotIndex}: {character.Name}");
                    Debug.LogError($"[CHARLIST-DEBUG] Avatar has {avatar.Children.Count} children");
                    foreach (var child in avatar.Children)
                    {
                        int grandchildCount = child.Children?.Count ?? 0;
                        Debug.LogError($"[CHARLIST-DEBUG]   {child.NativeClass}: ID={child.Id}, children={grandchildCount}");
                    }

                    var procMod = GCObjectFactory.NewProcModifier();
                    procMod.Id = _nextEntityId++;
                    tempChar.AddChild(procMod);

                    tempChar.WriteFullGCObject(writer);

                    // ═══════════════════════════════════════════════════════════════
                    // DUMP THIS CHARACTER'S BYTES
                    // ═══════════════════════════════════════════════════════════════
                    byte[] currentData = writer.ToArray();
                    Debug.LogError($"[CHARLIST-DUMP] ═══ SLOT {slotIndex}: {character.Name} (ID={character.Id}) ═══");
                    Debug.LogError($"[CHARLIST-DUMP] Bytes: {currentData.Length}");
                    Debug.LogError($"[CHARLIST-DUMP] First 50 bytes:");
                    int dumpLen = Math.Min(50, currentData.Length);
                    Debug.LogError($"[CHARLIST-DUMP] {BitConverter.ToString(currentData, 0, dumpLen)}");

                    // 🔥 SEND EACH CHARACTER AS TYPE 2 PACKET
                    Debug.LogError($"[CHARLIST] Sending slot {slotIndex} as TYPE 2: {currentData.Length} bytes");
                    SendCompressedA(conn, 0x01, 0x0f, currentData);

                    slotIndex++;
                }

                _charListSent[conn.ConnId] = true;
                _nextEntityId = Math.Max(savedNextEntityId, _nextEntityId);  // Restore, keep highest
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CHARLIST] ❌ ERROR: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════════
        // FIX #1: HandleCharacterPlay - Complete zone message format
        // ═══════════════════════════════════════════════════════════════════════════════

        private bool TryFindZoneByName(string zoneName, out Zone zone)
        {
            zone = null;
            if (string.IsNullOrWhiteSpace(zoneName))
                return false;
            zone = _zones.Values.FirstOrDefault(z =>
                z != null && !string.IsNullOrEmpty(z.name) && z.name.Equals(zoneName.Trim(), StringComparison.OrdinalIgnoreCase));
            return zone != null;
        }

        private Zone ResolveCharacterPlayZone(RRConnection conn, uint charId, SavedCharacter savedChar, out string reason)
        {
            reason = "default";
            TryFindZoneByName("tutorial", out Zone tutorialZone);

            string savedZoneName = savedChar?.currentZoneName;
            if (!TryFindZoneByName(savedZoneName, out Zone savedZone))
            {
                reason = $"missing-saved-zone:{savedZoneName ?? ""}";
                return tutorialZone ?? _zones.Values.FirstOrDefault();
            }

            if (IsPublicZone(savedZone.name))
            {
                reason = "saved-public-zone";
                return savedZone;
            }

            if (GroupManager.Instance.GetGroupForConn(conn.ConnId) != null)
            {
                reason = "saved-group-dungeon";
                return savedZone;
            }

            if (HasRememberedSoloDungeonInstance(charId, savedZone.name, out uint rememberedInstance, out bool expired))
            {
                reason = $"saved-solo-dungeon-memory:{rememberedInstance:X8}";
                return savedZone;
            }

            if (expired)
                reason = "saved-solo-dungeon-expired";
            else
                reason = "saved-solo-dungeon-missing";

            if (!string.IsNullOrEmpty(savedZone.respawnZone) && TryFindZoneByName(savedZone.respawnZone, out Zone respawnZone))
            {
                reason += $":redirect:{respawnZone.name}";
                return respawnZone;
            }

            reason += ":no-respawn-zone";
            return savedZone ?? tutorialZone;
        }

        private void HandleCharacterPlay(RRConnection conn, byte[] data)
        {
            Debug.Log($"HandleCharacterPlay for {conn.LoginName}");
            try
            {
                if (!_persistentCharacters.TryGetValue(conn.LoginName, out var characters) || characters.Count == 0)
                {
                    Debug.LogError($"No characters found for {conn.LoginName}");
                    return;
                }

                GCObject character = null;
                if (data != null && data.Length >= 4)
                {
                    var reader = new LEReader(data);
                    uint selectedCharId = reader.ReadUInt32();
                    Debug.LogError($"[CHAR-PLAY] Client selected ID: {selectedCharId}");
                    character = characters.Find(c => c.Id == selectedCharId);
                }
                if (character == null)
                    character = characters[0];

                _selectedCharacter[conn.LoginName] = character;
                conn.CharSqlId = character.Id;
                Debug.Log($"Selected character: {character.Name} (ID={character.Id})");

                // Write active character to DB for admin panel tracking
                try
                {
                    using (var dbConn = GameDatabase.GetConnection())
                        GameDatabase.ExecuteNonQuery(dbConn,
                            "UPDATE accounts SET current_character_id=@cid WHERE username=@u COLLATE NOCASE",
                            ("@cid", (int)character.Id), ("@u", conn.LoginName));
                }
                catch (System.Exception ex) { Debug.LogError($"[SAVE] set current_character_id failed for {conn.LoginName}: {ex.Message}"); }
                // 🔧 GO SERVER PATTERN: Character play just stores selection, no children needed for spawning
                // GO server creates fresh entities during spawn, doesn't need persistent children
                Debug.Log("Character selected for spawning - will create fresh entities during spawn");
                // STEP 1: Send acknowledgment (Channel 4, Type 5)
                var ackWriter = new LEWriter();
                ackWriter.WriteByte(4);
                ackWriter.WriteByte(5);
                SendCompressedA(conn, 0x01, 0x0F, ackWriter.ToArray());
                //SendCompressedE(conn, ackWriter.ToArray());
                Debug.Log("Sent 4/5 acknowledgment");
                // STEP 2: Send group connected (Channel 9, Type 0)
                var groupWriter = new LEWriter();
                groupWriter.WriteByte(9);
                groupWriter.WriteByte(0);
                SendCompressedA(conn, 0x01, 0x0F, groupWriter.ToArray());
                Debug.Log("Sent 9/0 group connected");

                // GROUP RECONNECT: Check if player was in a group before disconnect.
                // Binary @0x5F9FF0 (wire type 0x4A): reads [groupId][charId], looks up member,
                // sets member+0x18=1 (online), member+0x30=1, fires event 0x121110.
                // NOTE: Do NOT send 0x30/0x35 here — CurrentZoneId is not set yet.
                // The zone join handler sends full group state after CurrentZoneId is assigned.
                {
                    var reconnectedGroup = GroupManager.Instance.ReconnectMember(conn.LoginName, conn.ConnId);
                    if (reconnectedGroup != null)
                    {
                        uint reconnCharId = GetCharSqlId(conn);
                        // Send 0x4A to existing online members — clears "disconnected" overlay
                        byte[] reconnectPacket = GroupPackets.BuildMemberReconnected(reconnectedGroup.GroupId, reconnCharId);
                        foreach (var rm in reconnectedGroup.Members)
                        {
                            if (!rm.IsOnline) continue;
                            if (rm.ConnId == conn.ConnId) continue;
                            var rmc = FindConnectionById(rm.ConnId);
                            if (rmc != null)
                                SendToClient(rmc, reconnectPacket);
                        }
                        // Reset GroupConnectedSent for ALL members — zone join will send
                        // fresh 0x30 (GC+0xB0) + 0x35 to everyone. Without this, existing
                        // members keep stale GC+0xB0 and gates fail for kick/promote.
                        foreach (var rm in reconnectedGroup.Members)
                        {
                            var rmc = FindConnectionById(rm.ConnId);
                            if (rmc != null) rmc.GroupConnectedSent = false;
                        }
                        conn.GroupConnectedSent = false;
                        Debug.LogError($"[GROUP] Reconnected {conn.LoginName} to group {reconnectedGroup.GroupId}, sent 0x4A, reset all GroupConnectedSent.");
                    }
                }

                // NOTE: Do NOT send processConnected (0x30) for solo players.
                // S65 TTD-verified: GroupClient::connect() fires from [4][5] processSelectCharacter.
                // Constructor at 0x5F69B0 sets correct defaults: inviteMode=3 ("anyone"),
                // GC+0xD4=[0x932E58] (matches entity+0x98 for invite gate).
                // Sending 0x30 OVERWRITES these with leaderCharId=0/difficulty=0/inviteMode=0,
                // which breaks "Allow Group Invites From Anyone" and hides invite UI.
                // 0x30 is only needed when forming/joining a group (see SendGroupConnectedToAll).
                // STEP 3: Send zone info (Channel 13, Type 0) - 🔥 COMPLETE FORMAT MATCHING GO SERVER!
                Debug.LogError("[ZONE-MSG] Building complete zone message (13/0)...");
                var zoneWriter = new LEWriter();
                zoneWriter.WriteByte(13);
                zoneWriter.WriteByte(0);

                ////THIS BELOW IS SPAWN CODE TO TOWN CHANGED TO DEW VALLEY  NOW  TO BE CORRECT///
                /*  zoneWriter.WriteCString("town");
                  // 🔥 FIX: Get zone ID and add missing fields!
                  var townZone = _zones.Values.FirstOrDefault(z => z.name.ToLower() == "town");
                  uint zoneId = townZone?.id ?? 2781714545u;
                  conn.CurrentZoneId = zoneId;  // Store for later use in HandleZoneChannel!
                  Debug.LogError($"[ZONE-MSG] Zone ID: {zoneId} (0x{zoneId:X8})");
                  zoneWriter.WriteUInt32(zoneId);                          // Zone ID - WAS MISSING!
                  zoneWriter.WriteByte(0x01);                              // Flag - WAS MISSING!
                  zoneWriter.WriteByte(0xFF);                              // Flag - WAS MISSING!
                  zoneWriter.WriteCString("world.town.quest.Q01_a1");      // Quest zone source - WAS MISSING!*/

                var savedChar = CharacterRepository.GetCharacter(character.Id);
                var startZone = ResolveCharacterPlayZone(conn, character.Id, savedChar, out string zoneResolveReason);
                string zoneName = startZone?.name ?? "tutorial";
                zoneWriter.WriteCString(zoneName);
                uint zoneId = startZone?.id ?? 2781714545u;
                conn.CurrentZoneId = zoneId;  // Store for HandleZoneChannel tracking
                conn.CurrentZoneName = zoneName;  // Exact zone name for multiplayer
                GroupManager.Instance.UpdateMemberZone(conn.ConnId, zoneName);
                // Posse: live-update other members' rosters with the new Location/world.
                try { if (conn.CharSqlId != 0) PosseManager.Instance.NotifyMemberStateChange(conn.CharSqlId, this); }
                catch (Exception px) { Debug.LogError($"[POSSE] zone-notify failed: {px.Message}"); }
                //  conn.CurrentZoneGcType = startZone?.gcType ?? "world.tutorial"; // For quest filtering
                Debug.LogError($"[ZONE-MSG] Zone ID: {zoneId} (0x{zoneId:X8})");
                // Extract zone prefix for quest filtering
                conn.CurrentZoneGcType = ResolveZoneGcType(startZone);

                Debug.LogError($"[ZONE] CurrentZoneGcType set to: {conn.CurrentZoneGcType} reason={zoneResolveReason}");
                AssignInstanceId(conn);

                uint zoneSeed = ResolveZoneConnectSeed(conn, zoneName);
                zoneWriter.WriteUInt32(zoneSeed);
                Debug.LogError($"[ZONE-MSG] Sending seed: 0x{zoneSeed:X8} zone={zoneName}");
                zoneWriter.WriteByte(0x01);                              // Flag - WAS MISSING!
                zoneWriter.WriteByte(0xFF);                              // Flag - WAS MISSING!
                zoneWriter.WriteCString("");      // Quest zone source - WAS MISSING!
                zoneWriter.WriteUInt32(0x00);
                // zoneWriter.WriteCString("world.town.quest.Q01_a1");      // Quest zone source - WAS MISSING!

                // zoneWriter.WriteUInt32(0x01);                            // Count - WAS MISSING!
                byte[] zoneData = zoneWriter.ToArray();
                Debug.LogError($"[ZONE-MSG] Complete zone message: {zoneData.Length} bytes");
                Debug.LogError($"[ZONE-MSG] Hex: {BitConverter.ToString(zoneData)}");
                SendCompressedA(conn, 0x01, 0x0F, zoneData);
                Debug.Log("Sent 13/0 zone info (COMPLETE FORMAT)");
                Debug.Log("Waiting for client to send zone progression request...");

                // Social system — register player online and send initial social data
                SocialManager.Instance.PlayerOnline(conn.LoginName, character.Name, conn, SendSocialViaAuth);
                SocialManager.Instance.SendLoginSocialInit(conn, character.Name, SendSocialViaAuth);

                // Posse system — push processConnectionNotification(connected=true) so the
                // right-side Posse tab flips from "Posses are currently unavailable" to enabled.
                // Unlocks the entire posse action surface (Create/Join/Invite/Kick/...).
                PosseManager.Instance.SendConnectionNotification(conn, true, SendSocialViaAuth);
                SendPosseStateForCharacter(conn, character.Id, SendSocialViaAuth);
                // Tell other posse members this player came online so their rosters refresh.
                try { PosseManager.Instance.NotifyMemberStateChange(character.Id, this); }
                catch (Exception ex2) { Debug.LogError($"[POSSE] login-notify (HandleCharacterPlay) failed: {ex2.Message}"); }
            }
            catch (Exception ex)
            {
                Debug.LogError($"HandleCharacterPlay error: {ex.Message} {ex.StackTrace}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // ENTITY REQUEST DISPATCHER (opcode 0x04)
        // Binary RE: client sends requestType byte after 0x04 opcode.
        //   0x11 = SpendAttribPoint (statType 0-3, numPoints)
        //   0x12 = ReturnAttribPoint
        //   0x00 / no data = Respawn
        // ═══════════════════════════════════════════════════════════════════
        private void HandleEntityRequest(RRConnection conn, LEReader reader, byte[] data)
        {
            // Wire format (same as 0x03): uint16 entityId + byte requestType + data + EntitySynchInfo
            if (reader.Remaining < 3)
            {
                HandleClientRequestRespawn(conn);
                return;
            }

            ushort entityId = reader.ReadUInt16();  // skip entity ID
            byte requestType = reader.ReadByte();
            Debug.LogError($"[ENTITY-REQ] entity={entityId} type=0x{requestType:X2} remaining={reader.Remaining} from {conn.LoginName}");

            switch (requestType)
            {
                case 0x11:
                    HandleStatSpendRequest(conn, reader);
                    break;
                case 0x12:
                    HandleStatReturnRequest(conn, reader);
                    break;
                case 0x13:
                    HandleRespecRequest(conn);
                    break;
                default:
                    HandleClientRequestRespawn(conn);
                    break;
            }
        }

        private void HandleStatSpendRequest(RRConnection conn, LEReader reader)
        {
            if (reader.Remaining < 2) { Debug.LogError("[STAT-SPEND] Not enough data"); return; }

            byte statType = reader.ReadByte();   // 0=STR, 1=AGI, 2=END, 3=INT
            byte numPoints = reader.ReadByte();
            Debug.LogError($"[STAT-SPEND] statType={statType} numPoints={numPoints} from {conn.LoginName}");

            if (statType > 3 || numPoints == 0) { Debug.LogError($"[STAT-SPEND] Invalid"); return; }

            if (!_selectedCharacter.TryGetValue(conn.LoginName, out var selChar)) return;
            var savedChar = CharacterRepository.GetCharacter(selChar.Id);
            if (savedChar == null) return;

            int totalAllocated = savedChar.statStrength + savedChar.statAgility
                               + savedChar.statEndurance + savedChar.statIntellect;
            int pointsPerLevel = NativeStatPointsPerLevel;
            int totalAvailable = (SavedCharacterLevel.ResolveNativeRuntimeLevel(savedChar) - 1) * pointsPerLevel;
            int remaining = totalAvailable - totalAllocated;

            if (numPoints > remaining) { Debug.LogError($"[STAT-SPEND] Not enough: want={numPoints} have={remaining}"); return; }

            switch (statType)
            {
                case 0: savedChar.statStrength += numPoints; break;
                case 1: savedChar.statAgility += numPoints; break;
                case 2: savedChar.statEndurance += numPoints; break;
                case 3: savedChar.statIntellect += numPoints; break;
            }

            CharacterRepository.SaveCharacter(savedChar);
            Debug.LogError($"[STAT-SPEND] STR={savedChar.statStrength} AGI={savedChar.statAgility} END={savedChar.statEndurance} INT={savedChar.statIntellect} pts={remaining - numPoints}");
            GetPlayerState(conn.ConnId.ToString()).ApplyAllocatedStats(savedChar.statStrength, savedChar.statAgility, savedChar.statEndurance, savedChar.statIntellect);
            SendHeroStatUpdate(conn, 0x11, numPoints, statType);
        }

        private void HandleStatReturnRequest(RRConnection conn, LEReader reader)
        {
            if (reader.Remaining < 2) { Debug.LogError("[STAT-RETURN] Not enough data"); return; }

            byte statType = reader.ReadByte();
            byte numPoints = reader.ReadByte();
            Debug.LogError($"[STAT-RETURN] statType={statType} numPoints={numPoints} from {conn.LoginName}");

            if (statType > 3 || numPoints == 0) return;
            if (!_selectedCharacter.TryGetValue(conn.LoginName, out var selChar)) return;
            var savedChar = CharacterRepository.GetCharacter(selChar.Id);
            if (savedChar == null) return;

            int currentVal = 0;
            switch (statType) { case 0: currentVal = savedChar.statStrength; break; case 1: currentVal = savedChar.statAgility; break; case 2: currentVal = savedChar.statEndurance; break; case 3: currentVal = savedChar.statIntellect; break; }
            if (numPoints > currentVal) return;

            switch (statType)
            {
                case 0: savedChar.statStrength -= numPoints; break;
                case 1: savedChar.statAgility -= numPoints; break;
                case 2: savedChar.statEndurance -= numPoints; break;
                case 3: savedChar.statIntellect -= numPoints; break;
            }

            CharacterRepository.SaveCharacter(savedChar);
            Debug.LogError($"[STAT-RETURN] STR={savedChar.statStrength} AGI={savedChar.statAgility} END={savedChar.statEndurance} INT={savedChar.statIntellect}");
            GetPlayerState(conn.ConnId.ToString()).ApplyAllocatedStats(savedChar.statStrength, savedChar.statAgility, savedChar.statEndurance, savedChar.statIntellect);
            SendHeroStatUpdate(conn, 0x12, numPoints, statType);
        }

        // ═══════════════════════════════════════════════════════════
        // ReSpecCost curve evaluator — exactly matches client binary
        // ═══════════════════════════════════════════════════════════
        // Tables.gc defines ReSpecCost with 3 points: L1=0.2, L10=0.2, L120=360
        // Comment in Tables.gc: "numbers are defined in thousands, so 1 = 1000"
        //
        // Binary curve evaluator at 0x5d4050 (called from 0x5d3790):
        //   1. Convert V_lo and V_hi to Fixed32 (ceil(val * 256)) AT LOAD TIME
        //   2. t_fp = (target - L_lo) * 65536 / (L_hi - L_lo)    ; signed integer divide
        //   3. delta = V_hi_fx32 - V_lo_fx32
        //   4. result_fx32 = V_lo_fx32 + ((delta * t_fp) >> 16)  ; arithmetic right shift
        //
        // Critical: ALL math is integer. The order of operations (multiply-then-shift)
        // matches the x86 imul + shrd + idiv sequence. Using float math produces
        // subtly different results at some levels (e.g. L20 was off by 1 in fx32 = 4 gold).
        //
        // Endpoints stored as Fixed32 with CEIL: 0.2 → 52, 360 → 92160
        // Verified matches on L14, L20, L30, L40.
        // ═══════════════════════════════════════════════════════════
        private List<(int level, int fx32Value)> _respecCostCurveFx;

        private static int GetNativeRespecCooldownSeconds()
        {
            var knobs = DungeonRunners.Data.GCDatabase.Instance.GlobalKnobs;
            if (knobs == null || !knobs.HasProperty("ReSpecTime"))
                throw new InvalidDataException("GlobalKnobs.ReSpecTime not loaded");
            return Math.Max(0, knobs.GetInt("ReSpecTime", 0));
        }

        /// <summary>
        /// Returns the ReSpecCost curve value at target level, as Fixed32 (value << 8).
        /// Mirrors the client's integer-math curve evaluator exactly.
        /// </summary>
        private int EvaluateReSpecCostCurveFx32(int targetLevel)
        {
            if (_respecCostCurveFx == null)
            {
                _respecCostCurveFx = new List<(int, int)>();
                var tables = DungeonRunners.Data.GCDatabase.Instance.GetNode("Tables");
                var respecTable = tables?.GetChild("ReSpecCost");
                if (respecTable != null)
                {
                    foreach (var entry in respecTable.AnonymousChildren)
                    {
                        int level = entry.GetInt("Level", 0);
                        float value = entry.GetFloat("Value", 0f);
                        if (level > 0)
                        {
                            // Endpoints stored as Fixed32 via CEIL: matches how the client's
                            // compile-time conversion rounds small floats like 0.2 (51.2 → 52)
                            int fx32 = (int)Math.Ceiling(value * 256.0);
                            _respecCostCurveFx.Add((level, fx32));
                        }
                    }
                    _respecCostCurveFx.Sort((a, b) => a.level.CompareTo(b.level));
                    Debug.LogError($"[RESPEC-CURVE] Loaded {_respecCostCurveFx.Count} entries from Tables.ReSpecCost");
                    foreach (var e in _respecCostCurveFx)
                        Debug.LogError($"[RESPEC-CURVE]   L{e.level} = fx32({e.fx32Value})");
                }
                else
                {
                    RuntimeEvidenceManager.LogFallbackHit("respec-cost", "missing-ReSpecCost", "native=blocked compatibility=none", 1);
                    throw new InvalidDataException("Tables.ReSpecCost not loaded");
                }
            }

            if (_respecCostCurveFx.Count == 0)
            {
                RuntimeEvidenceManager.LogFallbackHit("respec-cost", "empty-ReSpecCost", "native=blocked compatibility=none", 1);
                throw new InvalidDataException("Tables.ReSpecCost has no entries");
            }

            // Clamp below first point → return first point's value directly
            if (targetLevel <= _respecCostCurveFx[0].level)
                return _respecCostCurveFx[0].fx32Value;

            // Clamp above last point → return last point's value directly
            if (targetLevel >= _respecCostCurveFx[_respecCostCurveFx.Count - 1].level)
                return _respecCostCurveFx[_respecCostCurveFx.Count - 1].fx32Value;

            // Find the segment and interpolate using binary-accurate integer math
            for (int i = 1; i < _respecCostCurveFx.Count; i++)
            {
                if (targetLevel <= _respecCostCurveFx[i].level)
                {
                    int L_lo = _respecCostCurveFx[i - 1].level;
                    int L_hi = _respecCostCurveFx[i].level;
                    int V_lo = _respecCostCurveFx[i - 1].fx32Value;
                    int V_hi = _respecCostCurveFx[i].fx32Value;

                    // Binary sequence (0x5d4050):
                    //   t_fp = (target - L_lo) * 65536 / (L_hi - L_lo)
                    //   result = V_lo + ((V_hi - V_lo) * t_fp) >> 16
                    // All 32-bit signed integer ops (with sign extension for intermediate 64-bit).
                    int t_fp = ((targetLevel - L_lo) * 65536) / (L_hi - L_lo);
                    int delta = V_hi - V_lo;
                    // Arithmetic right shift (>> in C# on signed int32 is arithmetic)
                    int interpDelta = (int)(((long)delta * t_fp) >> 16);
                    return V_lo + interpDelta;
                }
            }

            // Shouldn't reach here given clamp above, but return last point as safety
            return _respecCostCurveFx[_respecCostCurveFx.Count - 1].fx32Value;
        }

        private void HandleRespecRequest(RRConnection conn)
        {
            Debug.LogError($"[RESPEC] from {conn.LoginName}");

            if (!_selectedCharacter.TryGetValue(conn.LoginName, out var selChar)) return;
            var savedChar = CharacterRepository.GetCharacter(selChar.Id);
            if (savedChar == null) return;

            // Respec cooldown comes from GlobalKnobs.ReSpecTime.
            int nowUnix = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int cooldownSeconds = GetNativeRespecCooldownSeconds();
            int elapsed = nowUnix - savedChar.lastRespecTime;
            if (savedChar.lastRespecTime > 0 && elapsed < cooldownSeconds)
            {
                int remaining = cooldownSeconds - elapsed;
                int mins = remaining / 60;
                int secs = remaining % 60;
                SendSystemMessage(conn, $"Respec on cooldown. {mins}m {secs}s remaining.");
                Debug.LogError($"[RESPEC] REJECTED: cooldown {remaining}s remaining");
                return;
            }

            // ═══════════════════════════════════════════════════════════
            // RESPEC COST — exact match with client binary
            // ═══════════════════════════════════════════════════════════
            // Binary: handler @ 0x4F92E0, dialog builder @ 0x411700.
            // Both call curve evaluator 0x5d3790 → 0x5d4050 on the "ReSpecCost"
            // CurveTable in Tables.gc (points: L1=0.2, L10=0.2, L120=360).
            //
            // Binary math flow:
            //   1. Hero level loaded from [esi+0x314]
            //   2. Curve evaluator (0x5d4050) returns interpolated Fixed32 value:
            //        t_fp   = (level - L_lo) * 65536 / (L_hi - L_lo)
            //        result = V_lo_fx32 + ((V_hi_fx32 - V_lo_fx32) * t_fp) >> 16
            //      (endpoints V_lo/V_hi stored as ceil(float * 256) Fixed32)
            //   3. Multiply result by 1000 (imul with 0x3e800 = 1000<<8)
            //   4. shrd eax, edx, 8 (>> 8) to get cost in gold
            //
            // Final: cost = (fx32 * 1000) >> 8
            //
            // Integer math all the way. A float-based interp produces different
            // rounding at some levels (e.g. L20 was +4 gold over). See
            // EvaluateReSpecCostCurveFx32 above for the exact integer evaluator.
            //
            // Verified matches: L1–L10 (203), L14 (13285), L20 (32906),
            //                   L30 (65617), L40 (98324).
            // ═══════════════════════════════════════════════════════════
                int curveFixed32 = EvaluateReSpecCostCurveFx32(SavedCharacterLevel.ResolveNativeRuntimeLevel(savedChar));
            uint goldCost = (uint)(((long)curveFixed32 * 1000) >> 8);
            Debug.LogError($"[RESPEC] Level={savedChar.level} fx32={curveFixed32} → cost={goldCost} gold");

            if (savedChar.gold < goldCost)
            {
                SendSystemMessage(conn, $"Not enough gold to respec. Need {goldCost}, have {savedChar.gold}.");
                Debug.LogError($"[RESPEC] REJECTED: gold {savedChar.gold} < cost {goldCost}");
                return;
            }

            savedChar.gold -= goldCost;
            savedChar.statStrength = 0;
            savedChar.statAgility = 0;
            savedChar.statEndurance = 0;
            savedChar.statIntellect = 0;
            savedChar.lastRespecTime = nowUnix;
            savedChar.respecCount++;

            CharacterRepository.SaveCharacter(savedChar);
            Debug.LogError($"[RESPEC] Stats reset, gold {savedChar.gold + goldCost} -> {savedChar.gold} (cost {goldCost}), respec #{savedChar.respecCount}");
            GetPlayerState(conn.ConnId.ToString()).ApplyAllocatedStats(savedChar.statStrength, savedChar.statAgility, savedChar.statEndurance, savedChar.statIntellect);

            uint avatarId = GetPlayerAvatarId(conn.LoginName);
            if (avatarId == 0) return;

            // Gold removal — EXACT same format as MerchantManager.HandleBuyItem (line 1585-1596)
            // and Admin gold removal (line 4448-4459) — both proven to play coin sound
            if (conn.UnitContainerId != 0)
            {
                var goldWriter = new LEWriter();
                goldWriter.WriteByte(0x07);                    // BeginStream
                goldWriter.WriteByte(0x35);                    // ComponentUpdate
                goldWriter.WriteUInt16(conn.UnitContainerId);  // Player's UnitContainer
                goldWriter.WriteByte(0x20);                    // AddCurrency (fires 0x138A jingle)
                goldWriter.WriteInt32(-(int)goldCost);         // negative = subtract via two's complement
                goldWriter.WriteByte(0x00);                    // CurrencySource
                goldWriter.WriteUInt32(0x00000000);            // entityHandle (required by 0x20 parser)
                goldWriter.WriteByte(0x01);                    // notifyFlag (required by 0x20 parser)
                WritePlayerEntitySynch(conn, goldWriter);
                goldWriter.WriteByte(0x06);                    // EndStream
                byte[] goldPacket = goldWriter.ToArray();
                SendCompressedA(conn, 0x01, 0x0F, goldPacket);
                Debug.LogError($"[RESPEC-GOLD] Sent RemoveCurrency {goldCost} (merchant format)");
            }

            // Respec stat reset
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x03);
            writer.WriteUInt16((ushort)avatarId);
            writer.WriteByte(0x13);
            writer.WriteByte(0x02);
            writer.WriteUInt32(0xFFFF00);
            writer.WriteByte(0x06);
            SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());

            Debug.LogError($"[RESPEC] Sent respec packet, avatar={avatarId}");
        }

        /// <summary>
        /// Binary RE proven: Avatar vtable 0x86DE00, vtable+0xC0 = 0x4EDDF0 (processUpdate wrapper).
        /// 0x4EDDF0 reads subType from stack [esp+0x10], CEM context from [esp+0x24],
        /// then calls Hero::processUpdate@0x4F8F20(subType, msg).
        /// processUpdateSpendAttribPoint@0x4F9500 reads: byte numPoints, byte statType from stream.
        /// CEM 0x03 passes subType on stack, data in stream — wrapper consumes data BEFORE EntitySynchInfo.
        /// </summary>
        private void SendHeroStatUpdate(RRConnection conn, byte subType, byte numPoints, byte statType)
        {
            uint avatarId = GetPlayerAvatarId(conn.LoginName);
            if (avatarId == 0) return;

            var writer = new LEWriter();
            writer.WriteByte(0x07);              // BeginStream
            writer.WriteByte(0x03);              // processEntityUpdate
            writer.WriteUInt16((ushort)avatarId);
            writer.WriteByte(subType);           // 0x11=Spend, 0x12=Return
            writer.WriteByte(statType);          // FIRST read by processUpdateSpendAttribPoint → [esp+3]
            writer.WriteByte(numPoints);         // SECOND read → [esp+2]
            WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);              // EndStream

            SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
            Debug.LogError($"[STAT-UPDATE] Sent 0x03 sub=0x{subType:X2} pts={numPoints} stat={statType} avatar={avatarId}");
        }

        private void HandleClientRequestRespawn(RRConnection conn)
        {
            Debug.LogError($"[RESPAWN] HandleClientRequestRespawn for {conn.LoginName}");

            try
            {
                if (_playerStates.TryGetValue(conn.LoginName, out var playerState))
                {
                    playerState.RestoreToFull();
                    Debug.LogError($"[RESPAWN] Restored HP={playerState.CurrentHPWire} Mana={playerState.CurrentManaWire}");
                }

                string respawnZone = GetRespawnZone(conn.CurrentZoneGcType);
                var respawnPos = GetRespawnPosition(respawnZone);
                Debug.LogError($"[RESPAWN] {conn.CurrentZoneGcType} -> {respawnZone} at ({respawnPos.x}, {respawnPos.y}, {respawnPos.z})");

                ChangeZoneToPosition(conn, respawnZone, respawnPos.x, respawnPos.y, respawnPos.z);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RESPAWN] ERROR: {ex.Message}\n{ex.StackTrace}");
            }
        }
        private (float x, float y, float z) GetRespawnPosition(string zoneName)
        {
            var waypoints = DatabaseLoader.GetWaypointsForZone(zoneName);
            foreach (var wp in waypoints)
            {
                if (wp.name.Equals("Respawn", StringComparison.OrdinalIgnoreCase))
                    return (wp.posX, wp.posY, wp.posZ);
            }

            var zone = _zones.Values.FirstOrDefault(z =>
                z.name.Equals(zoneName, StringComparison.OrdinalIgnoreCase));
            if (zone != null)
                return (zone.spawnX, zone.spawnY, zone.spawnZ);

            var tutorial = _zones.Values.FirstOrDefault(z =>
                z.name.Equals("tutorial", StringComparison.OrdinalIgnoreCase));
            if (tutorial != null)
            {
                Debug.LogError($"[RESPAWN] zone='{zoneName}' missing authored spawn; using authored tutorial spawn ({tutorial.spawnX}, {tutorial.spawnY}, {tutorial.spawnZ})");
                return (tutorial.spawnX, tutorial.spawnY, tutorial.spawnZ);
            }

            Debug.LogError($"[RESPAWN] zone='{zoneName}' missing authored spawn and tutorial fallback zone");
            return (0f, 0f, 0f);
        }
        private string GetRespawnZone(string currentZoneGcType)
        {
            string requested = currentZoneGcType?.Trim();
            var zone = _zones.Values.FirstOrDefault(z =>
                !string.IsNullOrEmpty(requested) &&
                !string.IsNullOrEmpty(z.gcType) &&
                z.gcType.Equals(requested, StringComparison.OrdinalIgnoreCase));

            if (zone == null && !string.IsNullOrEmpty(requested))
            {
                zone = _zones.Values.FirstOrDefault(z =>
                    !string.IsNullOrEmpty(z.name) &&
                    (z.name.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
                     ("world." + z.name).Equals(requested, StringComparison.OrdinalIgnoreCase)));
            }

            if (zone != null && !string.IsNullOrEmpty(zone.respawnZone))
                return zone.respawnZone;

            return "tutorial";
        }





        private static void WriteGCType(LEWriter writer, string typeName, bool preserveCase = false)
        {
            bool verbose = VerbosePacketLogging;
            if (verbose)
                Debug.LogError($"[WriteGCType-START] Called with '{typeName}', preserveCase={preserveCase}");
            int positionBefore = writer.Position;
            if (verbose)
                Debug.LogError($"[WriteGCType-POS] Position before: {positionBefore}");

            // 🔥 CRITICAL: Only lowercase if preserveCase is false
            string safeTypeName = preserveCase ? typeName : typeName.ToLower();

            if (verbose)
                Debug.LogError($"[WriteGCType-FINAL] Using: '{safeTypeName}' (original: '{typeName}')");
            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(safeTypeName);
            if (verbose)
                Debug.LogError($"[WriteGCType-BYTES] String bytes: {nameBytes.Length}");
            writer.WriteByte(0xFF);
            if (verbose)
                Debug.LogError($"[WriteGCType-WRITE] Wrote 0xFF at position {positionBefore}");
            writer.WriteCString(safeTypeName);
            if (verbose)
                Debug.LogError($"[WriteGCType-CSTRING] WriteCString completed");
            int positionAfter = writer.Position;
            int bytesWritten = positionAfter - positionBefore;
            int expectedBytes = 1 + nameBytes.Length + 1;
            if (verbose)
            {
                Debug.LogError($"[WriteGCType-POS] Position after: {positionAfter}");
                Debug.LogError($"[WriteGCType-RESULT] Wrote {bytesWritten} bytes, expected {expectedBytes}");
                if (bytesWritten != expectedBytes)
                {
                    Debug.LogError($"❌ [WriteGCType-ERROR] MISMATCH! Expected {expectedBytes}, wrote {bytesWritten}");
                }
                else
                {
                    Debug.LogError($"✅ [WriteGCType-OK] Byte count matches!");
                }
                string hex = BitConverter.ToString(nameBytes).Replace("-", " ");
                Debug.LogError($"[WriteGCType-HEX] Hex: FF {hex} 00");
            }
        }

        /// <summary>
        /// Tag 22 format - LENGTH-PREFIXED for CHILD DATA contexts only
        /// Used when writing items in inventories, skills in skill lists, etc.
        /// NOT for entity creation!
        /// </summary>
        private static void WriteGCTypeTag22(LEWriter writer, string typeName, bool preserveCase = false)
        {
            // 🔥 CRITICAL: Only lowercase if preserveCase is false
            string safeTypeName = preserveCase ? typeName : typeName.ToLower();

            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(safeTypeName);
            writer.WriteByte(0x16);                      // Tag 22 marker
            writer.WriteUInt16((ushort)nameBytes.Length); // Length prefix
            writer.WriteBytes(nameBytes);                 // String (NO null terminator)
            if (VerbosePacketLogging)
                Debug.LogError($"[WriteGCTypeTag22] '{safeTypeName}' → {3 + nameBytes.Length} bytes (0x16 + length + string)");
        }





        // ═══════════════════════════════════════════════════════════════════════════════
        // MULTIPLAYER: Build spawn packet for OTHER players to see this avatar
        // ═══════════════════════════════════════════════════════════════════════════════

        private byte[] BuildOtherPlayerSpawnPacket(RRConnection playerConn, uint avatarEntityId, RRConnection viewerConn)
        {
            if (!TryResolvePlayerSynchronizedHP(playerConn, "MP-SPAWN", false, out uint remoteHPWire))
                return null;

            PlayerState remoteState = GetPlayerState(playerConn.ConnId.ToString());
            uint remoteManaWire = remoteState != null ? Math.Min(remoteState.CurrentManaWire, remoteState.MaxManaWire) : 0;
            var writer = new LEWriter();
            int posX = (int)(playerConn.PlayerPosX * 256);
            int posY = (int)(playerConn.PlayerPosY * 256);
            int posZ = (int)(playerConn.PlayerPosZ * 256);
            int heading = (int)(playerConn.PlayerHeading * 256);
            byte level = (byte)Math.Max(1, playerConn.PlayerLevel);

            // Offset slightly so players don't stack
            posX += 1280;  // 5 units offset

            ushort remoteBehaviorId = (ushort)_nextEntityId++;
            ushort remoteSkillsId = (ushort)_nextEntityId++;
            ushort remoteManipId = (ushort)_nextEntityId++;
            ushort remoteModId = (ushort)_nextEntityId++;

            if (!_remoteBehaviorIds.ContainsKey(viewerConn.LoginName))
                _remoteBehaviorIds[viewerConn.LoginName] = new Dictionary<string, ushort>();
            _remoteBehaviorIds[viewerConn.LoginName][playerConn.LoginName] = remoteBehaviorId;

            if (!_remoteAvatarIds.ContainsKey(viewerConn.LoginName))
                _remoteAvatarIds[viewerConn.LoginName] = new Dictionary<string, ushort>();
            _remoteAvatarIds[viewerConn.LoginName][playerConn.LoginName] = (ushort)avatarEntityId;

            writer.WriteByte(0x07);  // BeginStream

            // ═══════════════════════════════════════════════════════════════════
            // COMPONENT ORDER MATCHES SELF-SPAWN:
            //   1. EntityCreate
            //   2. Skills component
            //   3. Manipulators component (skills + equipment)
            //   4. Modifiers component
            //   5. UnitBehavior component  ← LAST! (self-spawn Op11)
            //   6. EntityInit              ← (self-spawn Op12)
            //   7. WarpTo
            //   8. EndStream
            //
            // UnitBehavior MUST be created AFTER Manipulators because
            // readInit accesses the Manipulators component for skill slots.
            // ═══════════════════════════════════════════════════════════════════

            // ── OP1: Create Entity ──
            writer.WriteByte(0x01);
            writer.WriteUInt16((ushort)avatarEntityId);
            WriteGCType(writer, playerConn.AvatarGcType, preserveCase: true);

            // ── OP2: Skills Component ──
            writer.WriteByte(0x32);
            writer.WriteUInt16((ushort)avatarEntityId);
            writer.WriteUInt16(remoteSkillsId);
            WriteGCType(writer, "skills", preserveCase: false);
            writer.WriteByte(0x01);  // hasInit
            writer.WriteByte(0xFF); writer.WriteByte(0xFF);
            writer.WriteByte(0xFF); writer.WriteByte(0xFF);  // gold
            writer.WriteByte(0x00);  // zero skills
            writer.WriteByte(0x01);  // one profession
            string profession = (playerConn.ClassName?.ToLower()) switch
            {
                "mage" or "warlock" => "skills.professions.Warlock",
                "ranger" => "skills.professions.Ranger",
                _ => "skills.professions.Warrior"
            };
            WriteGCType(writer, profession, preserveCase: true);

            // ── OP3: Manipulators Component (equipment + skills) ──
            writer.WriteByte(0x32);
            writer.WriteUInt16((ushort)avatarEntityId);
            writer.WriteUInt16(remoteManipId);
            WriteGCType(writer, "manipulators", preserveCase: false);
            writer.WriteByte(0x01);  // hasInit

            // Collect skills AND equipment from player's avatar manipulators
            var skillItems = new List<GCObject>();
            var equipItems = new List<GCObject>();
            // Get skill slot mapping for this player
            string pConnKey = playerConn.ConnId.ToString();
            Dictionary<uint, string> pManipMap = null;
            if (_playerManipMap.ContainsKey(pConnKey))
                pManipMap = _playerManipMap[pConnKey];

            if (playerConn.Avatar != null)
            {
                var manipComp = playerConn.Avatar.Children?.FirstOrDefault(c =>
                    c.NativeClass == "Manipulators" || c.GCClass == "Manipulators");
                if (manipComp?.Children != null)
                {
                    foreach (var child in manipComp.Children)
                    {
                        bool isEquip = (child.NativeClass == "Armor" || child.NativeClass == "Item" ||
                                       child.NativeClass == "MeleeWeapon" || child.NativeClass == "RangedWeapon");
                        if (isEquip && !string.IsNullOrEmpty(child.GCClass))
                        {
                            var itemData = DatabaseLoader.FindItem(child.GCClass);
                            var generalItem = itemData == null ? DatabaseLoader.FindGeneralItem(child.GCClass) : null;
                            if (itemData != null || generalItem != null)
                            {
                                equipItems.Add(child);
                            }
                            else
                            {
                                // Check authored special equipment slot catalog
                                string mlk = child.GCClass.ToLowerInvariant();
                                if (mlk.StartsWith("items.pal.")) mlk = mlk.Substring(10);
                                if (DungeonRunners.Managers.MerchantManager.HasAuthoredMerchantModSlots(mlk))
                                    equipItems.Add(child);
                            }
                        }
                        else if (!isEquip && !string.IsNullOrEmpty(child.GCClass))
                        {
                            skillItems.Add(child);
                        }
                    }
                }
            }

            // Child count = skills + equipment
            writer.WriteByte((byte)(skillItems.Count + equipItems.Count));

            // PASS 1: Write skills FIRST (same format as self-spawn Op4)
            uint remoteSlotCounter = 200;
            foreach (var skill in skillItems)
            {
                // Look up canonical slot ID from manip map
                uint slotId = remoteSlotCounter++;
                if (pManipMap != null)
                {
                    foreach (var kv in pManipMap)
                    {
                        if (string.Equals(kv.Value, skill.GCClass, StringComparison.OrdinalIgnoreCase))
                        {
                            slotId = kv.Key;
                            break;
                        }
                    }
                }
                writer.WriteByte(0xFF);
                writer.WriteCString(skill.GCClass.ToLower());
                writer.WriteUInt32(slotId);
                writer.WriteByte(0x01);  // skill level
                writer.WriteByte(0x00);
            }

            // PASS 2: Write equipment (WriteInit format)
            foreach (var item in equipItems)
            {
                item.WriteInit(writer, playerConn.PlayerLevel);
            }
            Debug.LogError($"[MULTIPLAYER-MANIP] {skillItems.Count} skills + {equipItems.Count} equip for {playerConn.LoginName}");

            // ── OP4: Modifiers Component ──
            writer.WriteByte(0x32);
            writer.WriteUInt16((ushort)avatarEntityId);
            writer.WriteUInt16(remoteModId);
            WriteGCType(writer, "modifiers", preserveCase: false);
            writer.WriteByte(0x01);  // hasInit
            writer.WriteUInt32(0x00000000);
            writer.WriteByte(0x00);
            writer.WriteUInt32(0x00000000);

            // ── OP5: UnitBehavior Component — LAST! (matches self-spawn Op11) ──
            // Created AFTER Manipulators so readInit can access skill slots
            writer.WriteByte(0x32);
            writer.WriteUInt16((ushort)avatarEntityId);
            writer.WriteUInt16(remoteBehaviorId);
            WriteGCType(writer, "avatar.base.UnitBehavior");  // REAL avatar behavior
            writer.WriteByte(0x01);  // hasInit

            // Behavior::readInit — EXACT bytes from self-spawn Op11 (line 10783-10817)
            writer.WriteByte(0xFF);  // Behavior marker
            writer.WriteByte(0x00);  // Action1 null
            writer.WriteByte(0x00);  // Action2 null

            // UnitMover::readInit
            writer.WriteByte(0xFF);  // Generation counter
            writer.WriteByte(0x00);  // UnitMoverFlags
            writer.WriteUInt32(0x00000000);  // UnitMoverUnk3
            writer.WriteUInt32(0x00000000);  // UnitMoverUnk4

            // Waypoint
            writer.WriteByte(0x00);  // WaypointFlags

            // UnitBehavior::readInit
            writer.WriteByte(0xFF);  // SessionID — [+0x154] set to 0xFF
            writer.WriteByte(0x01);  // UnitBehaviorUnk1 — bit 0 SET for activation (0x5202F0 runs frame 1)
            writer.WriteByte(0x00);  // UnitBehaviorUnk2

            // ── OP6: EntityInit — avatar format (self-spawn Op12) ──
            writer.WriteByte(0x02);
            writer.WriteUInt16((ushort)avatarEntityId);

            // WorldEntity.WriteInit
            writer.WriteUInt32(0x04);  // flags — MUST be 0x04 for avatar
            writer.WriteInt32(posX);
            writer.WriteInt32(posY);
            writer.WriteInt32(posZ);
            writer.WriteInt32(heading);
            writer.WriteByte(0x01);    // initFlags
            writer.WriteUInt16(0);     // Unk1Case

            // Unit.WriteInit
            writer.WriteByte(0x07);    // unitFlags
            writer.WriteByte(level);
            writer.WriteUInt16(0);     // unk1
            writer.WriteUInt16(0);     // unk2
            writer.WriteUInt16(0);     // ownerID (0 for other players)
            writer.WriteUInt32(remoteHPWire);
            writer.WriteUInt32(remoteManaWire);

            // Hero.WriteInit
            writer.WriteUInt32(0);     // experience (not shown for other players)
            int _mpStr = 0, _mpAgi = 0, _mpEnd = 0, _mpInt = 0;
            if (_selectedCharacter.TryGetValue(playerConn.LoginName, out var mpSel))
            {
                var mpSaved = CharacterRepository.GetCharacter(mpSel.Id);
                if (mpSaved != null) { _mpStr = mpSaved.statStrength; _mpAgi = mpSaved.statAgility; _mpEnd = mpSaved.statEndurance; _mpInt = mpSaved.statIntellect; }
            }
            writer.WriteUInt16((ushort)_mpStr); writer.WriteUInt16((ushort)_mpAgi);  // Str, Agi
            writer.WriteUInt16((ushort)_mpEnd); writer.WriteUInt16((ushort)_mpInt);  // End, Int
            writer.WriteUInt16(0); writer.WriteUInt16(0);  // StatPts, Respec (0 for other players)
            uint _opvpW = 0, _opvpR = 0;
            if (_selectedCharacter.TryGetValue(playerConn.LoginName, out var _opvpSel))
            {
                var _opvpChar = CharacterRepository.GetCharacter(_opvpSel.Id);
                if (_opvpChar != null) { _opvpW = (uint)_opvpChar.pvpWins; _opvpR = (uint)_opvpChar.pvpRating; }
            }
            writer.WriteUInt32(_opvpW); writer.WriteUInt32(_opvpR);  // +0x324 pvpWins, +0x328 pvpRating (from DB)

            // Avatar.WriteInit
            byte face = 0, hair = 0, hairColor = 0;
            if (_selectedCharacter.TryGetValue(playerConn.LoginName, out var selCharAppear))
            {
                var savedAppear = CharacterRepository.GetCharacter(selCharAppear.Id);
                if (savedAppear != null)
                {
                    face = savedAppear.face;
                    hair = savedAppear.hair;
                    hairColor = savedAppear.hairColor;
                }
            }
            writer.WriteByte(face);
            writer.WriteByte(hair);
            writer.WriteByte(hairColor);

            // ── OP7: WarpTo — set initial position ──
            writer.WriteByte(0x35);
            writer.WriteUInt16(remoteBehaviorId);
            writer.WriteByte(0x04);
            writer.WriteByte(0x11);   // WarpTo
            writer.WriteByte(0x00);   // SessionID
            writer.WriteInt32(posX);
            writer.WriteInt32(posY);
            writer.WriteInt32(posZ);
            GetNativeValidationCutoff(out uint spawnCutoffTick, out float spawnCutoffTime);
            if (!TryWriteResolvedEntitySynchInfo(writer, remoteBehaviorId, 0x04, SyncContext.PlayerActionResponse, "MP-SPAWN-WARP", EntitySynchInfoDecision.HP(EntitySynchInfoOwner.Avatar, remoteHPWire, $"MP-SPAWN-WARP source={playerConn.LoginName}", remoteBehaviorId, remoteBehaviorId, 0x04, GetNativeCombatNow(), $"remote-spawn-warp; validationCutoffTick={spawnCutoffTick} validationCutoffTime={spawnCutoffTime:F3}", spawnCutoffTick, spawnCutoffTime)))
                return null;

            writer.WriteByte(0x06);  // EndStream

            Debug.LogError($"[MULTIPLAYER] Built avatar spawn for {playerConn.LoginName}: {writer.Position} bytes (avatar.base.UnitBehavior)");
            return writer.ToArray();
        }

        private void BroadcastEntityRemove(RRConnection leavingConn, string zoneGcType)
        {
            Debug.LogError($"[MULTIPLAYER] Player {leavingConn.LoginName} leaving zone {zoneGcType}");

            foreach (var kvp in _connections)
            {
                var other = kvp.Value;
                if (other == leavingConn) continue;
                if (!other.IsSpawned) continue;
                if (other.CurrentZoneGcType != zoneGcType) continue;
                if (other.InstanceId != leavingConn.InstanceId) continue;  // Instance check
                if (_remoteAvatarIds.TryGetValue(other.LoginName, out var avatarMap) &&
                    avatarMap.TryGetValue(leavingConn.LoginName, out ushort avatarEntityId))
                {
                    var writer = new LEWriter();
                    writer.WriteByte(0x07);  // BeginStream
                    writer.WriteByte(0x05);  // EntityDespawn (same as gnome DespawnGnome — NOT 0x03!)
                    writer.WriteUInt16(avatarEntityId);
                    writer.WriteByte(0x06);  // EndStream
                    SendToClient(other, writer.ToArray());
                    Debug.LogError($"[MULTIPLAYER] Sent entity despawn (0x05) for avatar {avatarEntityId} to {other.LoginName}");
                    avatarMap.Remove(leavingConn.LoginName);
                }

                // Also remove the leaving player's behavior tracking for this viewer
                if (_remoteBehaviorIds.TryGetValue(other.LoginName, out var behaviorMap))
                    behaviorMap.Remove(leavingConn.LoginName);
            }

            // Clean up the leaving player's own viewer tracking
            _remoteBehaviorIds.Remove(leavingConn.LoginName);
            _remoteAvatarIds.Remove(leavingConn.LoginName);

            // Clean up session IDs involving this player
            var keysToRemove = new List<string>();
            foreach (var key in _remoteSessionIds.Keys)
                if (key.Contains(leavingConn.LoginName))
                    keysToRemove.Add(key);
            foreach (var key in keysToRemove)
                _remoteSessionIds.Remove(key);
            _lastRelayPosX.Remove(leavingConn.LoginName);
            _lastRelayPosY.Remove(leavingConn.LoginName);
            _lastRawMoveData.Remove(leavingConn.LoginName);
            _lastRawMoveCount.Remove(leavingConn.LoginName);
            _stopSignalSent.Remove(leavingConn.LoginName);
        }

        private Dictionary<string, byte> _remoteSessionIds = new Dictionary<string, byte>();

        private void BroadcastChatToZone(RRConnection sender, string message)
        {
            // Build Say packet: ch=0x06, type=0x00, subtype=0x02 (Say=white)
            // Format: [0x06][0x00][0x02][0x00][sender\0][message\0]
            // Binary: processMessage@0x5FF450 — sender name displayed in chat window
            string senderName = sender.LoginName ?? "Unknown";
            if (_selectedCharacter.TryGetValue(sender.LoginName, out var ch))
                senderName = ch.Name ?? senderName;

            var writer = new LEWriter();
            writer.WriteByte(0x06);   // Chat channel
            writer.WriteByte(0x00);   // Type 0
            writer.WriteByte(0x02);   // Subtype 0x02 = Say (white)
            writer.WriteByte(0x00);   // Padding
            writer.WriteCString(senderName);
            writer.WriteCString(message);
            byte[] chatPacket = writer.ToArray();

            // S52/S65C: Echo to ALL including sender — client does NOT echo locally
            foreach (var other in _connections.Values)
            {
                if (!other.IsSpawned) continue;

                SendCompressedA(other, 0x01, 0x0F, chatPacket);
            }
        }

        /// <summary>
        /// Relay a melee attack animation (0x50 UseTarget) to other players.
        /// Other players see the swing animation. Target is set to 0 since monsters are per-player.
        /// </summary>
        private void BroadcastMeleeAttack(RRConnection conn, byte responseId, byte manipulatorId, byte useFlags)
        {
            // DISABLED: UseTarget (0x50) needs a valid target entity on the viewer's client.
            // Without shared monster IDs, target=0 produces no animation.
            // Re-enable once shared monsters are implemented.
            return;
            foreach (var other in _connections.Values)
            {
                if (other == conn) continue;
                if (!other.IsSpawned) { Debug.LogError($"[MP-MELEE] SKIP {other.LoginName}: not spawned"); continue; }
                if (other.CurrentZoneGcType != conn.CurrentZoneGcType) { Debug.LogError($"[MP-MELEE] SKIP {other.LoginName}: zone '{other.CurrentZoneGcType}' != '{conn.CurrentZoneGcType}'"); continue; }
                if (other.InstanceId != conn.InstanceId) continue;  // Instance check
                if (!_remoteBehaviorIds.TryGetValue(other.LoginName, out var playerMap)) { Debug.LogError($"[MP-MELEE] SKIP {other.LoginName}: no remoteBehaviorIds entry"); continue; }
                if (!playerMap.TryGetValue(conn.LoginName, out ushort remoteBehaviorId)) { Debug.LogError($"[MP-MELEE] SKIP {other.LoginName}: no behavior for {conn.LoginName}"); continue; }

                // CreateAction (0x04) — INITIATES attack animation on remote entity
                // ActionResponse (0x01) was wrong — remote entity has no pending action to respond to
                var w = new LEWriter();
                w.WriteByte(0x07);
                w.WriteByte(0x35);
                w.WriteUInt16(remoteBehaviorId);
                w.WriteByte(0x04);           // CreateAction (NOT 0x01 ActionResponse!)
                w.WriteByte(0x50);           // UseTarget
                w.WriteByte(manipulatorId);  // sessionId
                w.WriteByte(useFlags);
                w.WriteUInt16(0x0000);       // target=0
                if (!TryWriteRemoteAvatarEntitySynchInfo(conn, w, remoteBehaviorId, 0x04, "MP-MELEE"))
                    continue;
                w.WriteByte(0x06);
                SendToClient(other, w.ToArray());
                Debug.LogError($"[MP-MELEE] SENT to {other.LoginName} behaviorId={remoteBehaviorId}");
            }
        }

        /// <summary>
        /// Relay a position-targeted spell (0x51 UsePosition — FireBolt, etc.) to other players.
        /// Other players see the spell cast animation + projectile direction.
        /// </summary>
        private void BroadcastSpellCast(RRConnection conn, byte responseId, byte manipulatorId, byte actionID, int posX, int posY, int posZ)
        {
            Debug.LogError($"[MP-SPELL] from {conn.LoginName} zone='{conn.CurrentZoneGcType}' actionID={actionID}");
            foreach (var other in _connections.Values)
            {
                if (other == conn) continue;
                if (!other.IsSpawned) continue;
                if (other.CurrentZoneGcType != conn.CurrentZoneGcType) continue;
                if (other.InstanceId != conn.InstanceId) continue;  // Instance check
                if (!_remoteBehaviorIds.TryGetValue(other.LoginName, out var playerMap)) continue;
                if (!playerMap.TryGetValue(conn.LoginName, out ushort remoteBehaviorId)) continue;

                var w = new LEWriter();
                w.WriteByte(0x07);
                w.WriteByte(0x35);
                w.WriteUInt16(remoteBehaviorId);
                w.WriteByte(0x04);           // CreateAction
                w.WriteByte(0x51);           // UsePosition
                w.WriteByte(manipulatorId);  // sessionId
                w.WriteByte(actionID);
                w.WriteUInt32((uint)posX);
                w.WriteUInt32((uint)posY);
                w.WriteUInt32((uint)posZ);
                if (!TryWriteRemoteAvatarEntitySynchInfo(conn, w, remoteBehaviorId, 0x04, "MP-SPELL"))
                    continue;
                w.WriteByte(0x06);
                SendToClient(other, w.ToArray());
            }
        }

        /// <summary>
        /// Relay a self-cast spell (0x52 — Gaseous Blast, buffs) to other players.
        /// Other players see the buff/cast animation on the player.
        /// </summary>
        private void BroadcastSelfCast(RRConnection conn, byte responseId, byte manipulatorId, byte slotID)
        {
            Debug.LogError($"[MP-SELFCAST] from {conn.LoginName} zone='{conn.CurrentZoneGcType}' slotID={slotID}");
            foreach (var other in _connections.Values)
            {
                if (other == conn) continue;
                if (!other.IsSpawned) continue;
                if (other.CurrentZoneGcType != conn.CurrentZoneGcType) continue;
                if (other.InstanceId != conn.InstanceId) continue;  // Instance check
                if (!_remoteBehaviorIds.TryGetValue(other.LoginName, out var playerMap)) continue;
                if (!playerMap.TryGetValue(conn.LoginName, out ushort remoteBehaviorId)) continue;

                var w = new LEWriter();
                w.WriteByte(0x07);
                w.WriteByte(0x35);
                w.WriteUInt16(remoteBehaviorId);
                w.WriteByte(0x04);           // CreateAction
                w.WriteByte(0x52);           // SelfCast
                w.WriteByte(manipulatorId);  // sessionId
                w.WriteByte(slotID);
                if (!TryWriteRemoteAvatarEntitySynchInfo(conn, w, remoteBehaviorId, 0x04, "MP-SELFCAST"))
                    continue;
                w.WriteByte(0x06);
                SendToClient(other, w.ToArray());
            }
        }

        /// <summary>
        /// Broadcast a MoveTo for when the player clicks an entity (NPC, portal, checkpoint).
        /// The client pathfinds silently without sending 0x65 updates, so we send the
        /// destination position directly to keep other players in sync.
        /// </summary>
        private void BroadcastWalkToPosition(RRConnection conn, float targetX, float targetY)
        {
            int posX = (int)(targetX * 256);
            int posY = (int)(targetY * 256);

            // Suppress regular movement broadcasts briefly so the walk-to
            // isn't overridden by intermediate 0x65 position updates
            conn.LastPositionUpdateTime = Time.time + 0.4f; // suppress for 400ms

            foreach (var other in _connections.Values)
            {
                if (other == conn) continue;
                if (!other.IsSpawned) continue;
                if (other.CurrentZoneGcType != conn.CurrentZoneGcType) continue;
                if (other.InstanceId != conn.InstanceId) continue;  // Instance check
                if (!_remoteBehaviorIds.TryGetValue(other.LoginName, out var playerMap)) continue;
                if (!playerMap.TryGetValue(conn.LoginName, out ushort remoteBehaviorId)) continue;

                string sessionKey = $"{other.LoginName}:{conn.LoginName}";
                if (!_remoteSessionIds.ContainsKey(sessionKey))
                    _remoteSessionIds[sessionKey] = 0;
                byte remoteSessionId = _remoteSessionIds[sessionKey]++;

                var w = new LEWriter();
                w.WriteByte(0x07);
                w.WriteByte(0x35);
                w.WriteUInt16(remoteBehaviorId);
                w.WriteByte(0x04);  // CreateAction
                w.WriteByte(0x01);  // MoveTo
                w.WriteByte(remoteSessionId);
                w.WriteInt32(posX);
                w.WriteInt32(posY);
                if (!TryWriteRemoteAvatarEntitySynchInfo(conn, w, remoteBehaviorId, 0x04, "MP-WALKTO"))
                    continue;
                w.WriteByte(0x06);
                SendToClient(other, w.ToArray());
            }
        }

        /// <summary>
        /// Broadcast death animation (0x28 + 0x20) to other players.
        /// Same packet format as BuildDeathPacket but using remote avatar entity ID.
        /// </summary>
        private void BroadcastPlayerDeath(RRConnection deadConn)
        {
            Debug.LogError($"[MP-DEATH] from {deadConn.LoginName} zone='{deadConn.CurrentZoneGcType}'");
            foreach (var other in _connections.Values)
            {
                if (other == deadConn) continue;
                if (!other.IsSpawned) continue;
                if (other.CurrentZoneGcType != deadConn.CurrentZoneGcType) continue;
                if (other.InstanceId != deadConn.InstanceId) continue;  // Instance check

                if (!_remoteBehaviorIds.TryGetValue(other.LoginName, out var behaviorMap)) continue;
                if (!behaviorMap.TryGetValue(deadConn.LoginName, out ushort remoteBehaviorId)) continue;

                // Set HP to 0 via 0x36 ComponentSync — triggers death animation client-side
                // 0x28 crashes because remote avatars don't have combat entity setup
                var w = new LEWriter();
                w.WriteByte(0x07);  // BeginStream
                w.WriteByte(0x36);  // ComponentSync
                w.WriteUInt16(remoteBehaviorId);
                GetNativeValidationCutoff(out uint deathCutoffTick, out float deathCutoffTime);
                if (!TryWriteResolvedEntitySynchInfo(w, remoteBehaviorId, 0x00, SyncContext.PlayerActionResponse, "MP-DEATH", EntitySynchInfoDecision.HP(EntitySynchInfoOwner.Avatar, 0, $"MP-DEATH source={deadConn.LoginName}", remoteBehaviorId, remoteBehaviorId, 0x00, GetNativeCombatNow(), $"remote-death; validationCutoffTick={deathCutoffTick} validationCutoffTime={deathCutoffTime:F3}", deathCutoffTick, deathCutoffTime)))
                    continue;
                w.WriteByte(0x06);  // EndStream
                SendToClient(other, w.ToArray());
                Debug.LogError($"[MULTIPLAYER-DEATH] Sent HP=0 sync to {other.LoginName} for {deadConn.LoginName}");
            }
        }

        private bool TryPrepareProceduralDungeonSnapshot(RRConnection conn, out DungeonMazeSpawner.ProceduralDungeonSnapshot snapshot)
        {
            snapshot = null;
            if (conn == null || string.IsNullOrEmpty(conn.CurrentZoneName))
                return false;
            if (!DungeonMazeSpawner.IsProceduralZone(conn.CurrentZoneName))
                return false;

            string zoneName = conn.CurrentZoneName;
            string instanceKey = GetInstanceZoneKey(conn);
            uint layoutSeed = ResolveZoneLayoutSeed(conn, zoneName);
            uint roomSeed = ResolveRuntimeZoneSeed(conn, zoneName);
            snapshot = ZoneSpawnManager.Instance.GetOrCreateProceduralSnapshot(zoneName, instanceKey, layoutSeed, roomSeed);
            return snapshot != null;
        }

        private bool TryGetProceduralDungeonSnapshot(RRConnection conn, out DungeonMazeSpawner.ProceduralDungeonSnapshot snapshot)
        {
            snapshot = null;
            if (conn == null || string.IsNullOrEmpty(conn.CurrentZoneName))
                return false;
            if (!DungeonMazeSpawner.IsProceduralZone(conn.CurrentZoneName))
                return false;

            string instanceKey = GetInstanceZoneKey(conn);
            if (ZoneSpawnManager.Instance.TryGetProceduralSnapshot(instanceKey, out snapshot))
                return true;

            return TryPrepareProceduralDungeonSnapshot(conn, out snapshot);
        }

        private (float x, float y, float z) ResolvePlayerSpawnPosition(RRConnection conn)
        {
            float spawnX, spawnY, spawnZ;
            bool proceduralSnapshotSpawn = false;

            if (TryPrepareProceduralDungeonSnapshot(conn, out var dungeonSnapshot))
            {
                proceduralSnapshotSpawn = true;
                string pendingSpawnPoint = conn.PendingSpawnPoint ?? "";
                if (DungeonMazeSpawner.TryResolveSpawnPoint(
                    dungeonSnapshot,
                    pendingSpawnPoint,
                    out var resolvedSpawn,
                    out float resolvedHeading,
                    out int resolvedSourceIndex,
                    out string resolvedTileType,
                    out int resolvedGridX,
                    out int resolvedGridY,
                    out var resolvedLocal,
                    out string resolvedSource))
                {
                    spawnX = resolvedSpawn.x;
                    spawnY = resolvedSpawn.y;
                    spawnZ = resolvedSpawn.z;
                    conn.PlayerHeading = resolvedHeading;
                    Debug.LogError($"[SPAWN] Procedural snapshot named spawnPoint='{pendingSpawnPoint}' zone={dungeonSnapshot.ZoneName} layoutSeed=0x{dungeonSnapshot.LayoutSeed:X8} roomSeed=0x{dungeonSnapshot.RoomSeed:X8} player=({spawnX:F1}, {spawnY:F1}, {spawnZ:F1}) heading={conn.PlayerHeading:F1} src={resolvedSourceIndex} tile='{resolvedTileType}' grid=({resolvedGridX},{resolvedGridY}) local=({resolvedLocal.x:F1},{resolvedLocal.y:F1},{resolvedLocal.z:F1}) source='{resolvedSource}'");
                }
                else
                {
                    spawnX = dungeonSnapshot.PlayerSpawn.x;
                    spawnY = dungeonSnapshot.PlayerSpawn.y;
                    spawnZ = dungeonSnapshot.PlayerSpawn.z;
                    conn.PlayerHeading = dungeonSnapshot.PlayerHeading;
                    Debug.LogError($"[SPAWN] Procedural snapshot default entry spawnPoint='{pendingSpawnPoint}' zone={dungeonSnapshot.ZoneName} layoutSeed=0x{dungeonSnapshot.LayoutSeed:X8} roomSeed=0x{dungeonSnapshot.RoomSeed:X8} player=({spawnX:F1}, {spawnY:F1}, {spawnZ:F1}) heading={conn.PlayerHeading:F1} entry=({dungeonSnapshot.EntryGridX},{dungeonSnapshot.EntryGridY}) src={dungeonSnapshot.EntrySourceIndex} tile='{dungeonSnapshot.EntryTileType}' playerLocal=({dungeonSnapshot.PlayerAnchorLocal.x:F1},{dungeonSnapshot.PlayerAnchorLocal.y:F1},{dungeonSnapshot.PlayerAnchorLocal.z:F1}) entryPortal=({dungeonSnapshot.EntryPortalSpawn.x:F1},{dungeonSnapshot.EntryPortalSpawn.y:F1},{dungeonSnapshot.EntryPortalSpawn.z:F1}) entryPortalLocal=({dungeonSnapshot.EntryPortalAnchorLocal.x:F1},{dungeonSnapshot.EntryPortalAnchorLocal.y:F1},{dungeonSnapshot.EntryPortalAnchorLocal.z:F1}) walkable={dungeonSnapshot.PlayerAnchorWalkable}/{dungeonSnapshot.EntryPortalAnchorWalkable} yTransform=worldGridY=gridY/native-BuildWorld source='{dungeonSnapshot.PlayerAnchorSource}'");
                }
                conn.PendingSpawnX = 0;
                conn.PendingSpawnY = 0;
                conn.PendingSpawnZ = 0;
                conn.PendingSpawnPoint = "";
            }
            else if (conn.PendingSpawnX != 0 || conn.PendingSpawnY != 0 || conn.PendingSpawnZ != 0)
            {
                spawnX = conn.PendingSpawnX;
                spawnY = conn.PendingSpawnY;
                spawnZ = conn.PendingSpawnZ;
                conn.PendingSpawnX = 0;
                conn.PendingSpawnY = 0;
                conn.PendingSpawnZ = 0;
                Debug.LogError($"[SPAWN] Using CHECKPOINT position: ({spawnX}, {spawnY}, {spawnZ})");
            }
            else if (_zones.TryGetValue(conn.CurrentZoneId, out Zone currentZone) && (currentZone.spawnX != 0 || currentZone.spawnY != 0))
            {
                spawnX = currentZone.spawnX;
                spawnY = currentZone.spawnY;
                spawnZ = currentZone.spawnZ;
                Debug.LogError($"[SPAWN] Zone '{currentZone.name}' spawn: ({spawnX}, {spawnY}, {spawnZ})");
            }
            else
            {
                string fallbackZoneName = conn.CurrentZoneName ?? "";
                var waypoints = DatabaseLoader.GetWaypointsForZone(fallbackZoneName);
                var wp = waypoints.FirstOrDefault(w => w.name.Equals("Start", StringComparison.OrdinalIgnoreCase));
                if (wp != null)
                {
                    spawnX = (float)wp.posX;
                    spawnY = (float)wp.posY;
                    spawnZ = (float)wp.posZ;
                    Debug.LogError($"[SPAWN] Using waypoint '{wp.name}' fallback: ({spawnX}, {spawnY}, {spawnZ})");
                }
                else
                {
                    spawnX = 415f;
                    spawnY = -180f;
                    spawnZ = 50f;
                    Debug.LogError($"[SPAWN] ⚠️ Zone {conn.CurrentZoneId} has no spawn data, using town fallback");
                }
            }

            string pmZone = conn.CurrentZoneName ?? "";
            var pathMap = DungeonRunners.Core.PathMapManager.Instance.GetPathMap(pmZone);
            if (pathMap != null && !proceduralSnapshotSpawn)
            {
                if (pathMap.IsWalkable(spawnX, spawnY))
                {
                    float terrainZ = pathMap.GetHeightAt(spawnX, spawnY, spawnZ);
                    Debug.LogError($"[SPAWN] PathMap height correction: Z {spawnZ:F1} → {terrainZ:F1}");
                    spawnZ = terrainZ;
                }
                else
                {
                    Debug.LogError($"[SPAWN] ⚠️ PathMap says ({spawnX:F1}, {spawnY:F1}) is NOT walkable — using spawn Z as-is");
                }
            }
            else if (proceduralSnapshotSpawn)
            {
                Debug.LogError($"[SPAWN] Procedural snapshot raw anchor kept: no PathMap height correction pos=({spawnX:F1},{spawnY:F1},{spawnZ:F1})");
            }

            int playersInZone = 0;
            foreach (var other in _connections.Values)
            {
                if (other == conn) continue;
                if (!other.IsSpawned) continue;
                if (other.CurrentZoneGcType == conn.CurrentZoneGcType && other.InstanceId == conn.InstanceId)
                    playersInZone++;
            }
            if (playersInZone > 0 && !proceduralSnapshotSpawn)
            {
                float angle = playersInZone * 1.2566f;
                spawnX += (float)Math.Cos(angle) * 5f;
                spawnY += (float)Math.Sin(angle) * 5f;
                Debug.LogError($"[SPAWN] Offset for player #{playersInZone + 1}: ({spawnX:F1}, {spawnY:F1})");
            }
            else if (playersInZone > 0)
            {
                Debug.LogError($"[SPAWN] Procedural snapshot keeps authored spawn with {playersInZone} existing players in instance");
            }

            return (spawnX, spawnY, spawnZ);
        }

        private IEnumerator SendDeferredClientControl(RRConnection conn, ushort behaviorId)
        {
            yield return new WaitForSeconds(0.55f);
            if (conn == null || !conn.IsConnected) yield break;
            ushort cid = conn.UnitBehaviorId != 0 ? (ushort)conn.UnitBehaviorId : behaviorId;
            if (cid == 0) yield break;

            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(cid);
            writer.WriteByte(0x64);
            writer.WriteByte(0x01);
            WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);
            SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
            Debug.LogError($"[FOLLOW-DEFER] Sent client control for UnitBehavior={cid}");
        }

        private void SendPlayerEntitySpawn(RRConnection conn)
        {
            Debug.LogError("=== SendPlayerEntitySpawn STARTING ===");
            Debug.LogError($"🚨🚨🚨 SendPlayerEntitySpawn CALLED! Stack trace:");
            Debug.LogError(Environment.StackTrace);

            try
            {
                // Ban check — client is fully connected here, can receive messages
                if (conn.LoginName != null && _playerIsFree.ContainsKey(conn.LoginName))
                {
                    // Check ban flag from LoadAccountFlags cache
                    try
                    {
                        using (var banDb = Database.GameDatabase.GetConnection())
                        {
                            object banResult = Database.GameDatabase.ExecuteScalar(banDb,
                                "SELECT is_banned FROM accounts WHERE username = @name",
                                ("@name", conn.LoginName));
                            if (banResult != null && System.Convert.ToInt32(banResult) != 0)
                            {
                                SendSystemMessage(conn, "Your account has been banned. Contact an administrator.");
                                Debug.LogError($"[ACCOUNT] Banned user {conn.LoginName} tried to enter game");
                                StartCoroutine(DelayedBanDisconnect(conn));
                                return;
                            }
                        }
                    }
                    catch { }
                }

                if (!_selectedCharacter.TryGetValue(conn.LoginName, out var character))
                {
                    Debug.LogError($"No selected character found for {conn.LoginName}");
                    return;
                }

                // Load character data (needed for both first login and zone transition)
                SavedCharacter savedChar = CharacterRepository.GetCharacter(character.Id);
                if (savedChar == null)
                {
                    Debug.LogError($"[OP4] ⚠️ No saved data for character {character.Id}, creating default Fighter");
                    savedChar = CharacterRepository.CreateCharacter(character.Name, "Fighter", AccountRepository.GetAccountId(conn.LoginName), conn.LoginName);
                }
                else
                {
                    Debug.LogError($"[OP4] ✅ Loaded character {savedChar.name} ({savedChar.className}) from JSON");
                }
                // ═══════════════════════════════════════════════════════════════════════════
                // INITIALIZE PLAYER STATE WITH CHARACTER CLASS/LEVEL
                // ═══════════════════════════════════════════════════════════════════════════
                // Initialize PlayerState with character class for proper HP calculation
                // ═══════════════════════════════════════════════════════════════════════════
                // INITIALIZE PLAYER STATE WITH CHARACTER CLASS/LEVEL
                // ═══════════════════════════════════════════════════════════════════════════
                // Initialize PlayerState with character class for proper HP calculation
                string spawnConnIdKey = conn.ConnId.ToString();
                Debug.LogError($"[SPAWN] Initializing PlayerState with key='{spawnConnIdKey}' for conn.ConnId={conn.ConnId}");
                PlayerState playerState = GetPlayerState(spawnConnIdKey);

                // Save any unsaved XP/level progress before reloading from DB
                if (playerState.Experience > 0 || playerState.Level > 1)
                {
                    var currentChar = CharacterRepository.GetCharacter(_selectedCharacter[conn.LoginName].Id);
                    byte currentPersistedLevel = SavedCharacterLevel.ResolvePersistedLevel(playerState.Level);
                    if (currentChar != null && (currentPersistedLevel > currentChar.level || playerState.Experience > currentChar.experience))
                    {
                        currentChar.level = currentPersistedLevel;
                        currentChar.experience = playerState.Experience;
                        CharacterRepository.SaveCharacter(currentChar);
                        savedChar = currentChar;
                        Debug.LogError($"[ZONE] Saved progress before zone transition: persistedLevel={currentChar.level} nativeLevel={playerState.Level} xp={currentChar.experience}");
                    }
                }

                // Capture "fresh PlayerState" state BEFORE InitializeStats mutates Level.
                // Without this, any Level 2+ character would fail the guard below because
                // InitializeStats sets _level = savedChar.level, so playerState.Level <= 1
                // would never be true on a real login — and DB XP would never load into memory.
                // That caused login XP wipe: playerState.Experience stayed at 0 after login,
                // and the next SavePlayerLevel (auto-save / kill / zone) wrote 0 over the DB.
                bool isFreshPlayerState = (playerState.Experience == 0 && playerState.Level <= 1);
                bool isZoneTransition = (conn.Avatar != null && conn.Player != null);
                bool useNativeFullHPBootstrap = isZoneTransition && conn.NativeFullHPOnNextSpawn;
                bool includeSavedCharacterHP = isZoneTransition || (savedChar != null && savedChar.currentHP > 0);
                PlayerHPPreserve spawnHPPreserve = CapturePlayerHPPreserve(conn, playerState, savedChar, isZoneTransition ? "zone-preinit" : "spawn-preinit", includeSavedCharacterHP);
                bool preservedHPFromLiveState = spawnHPPreserve.FromLiveState;
                uint preservedLiveHPWire = preservedHPFromLiveState ? spawnHPPreserve.HPWire : 0;
                uint preservedSavedHPWire = savedChar.currentHP;
                uint hpToKeepWire = spawnHPPreserve.HasHP ? spawnHPPreserve.HPWire : 0;
                bool preservedHPKnown = spawnHPPreserve.HasHP;
                string hpToKeepSource = spawnHPPreserve.HasHP ? spawnHPPreserve.Source : "none";
                uint manaToKeepWire = isZoneTransition && playerState.HasClientMana ? playerState.CurrentManaWire : savedChar.currentMana;
                Debug.LogError($"[SPAWN-HP-PRESERVE] zoneTransition={isZoneTransition} bootstrap={useNativeFullHPBootstrap} includeSaved={includeSavedCharacterHP} source={hpToKeepSource} preserved={hpToKeepWire} live={preservedLiveHPWire} saved={preservedSavedHPWire}");

                int nativeRuntimeLevel = SavedCharacterLevel.ResolveNativeRuntimeLevel(savedChar);
                Debug.LogError($"[SPAWN-LEVEL] persistedLevel={savedChar.level} nativeRuntimeLevel={nativeRuntimeLevel} xp={savedChar.experience} source=SavedCharacterLevel");
                playerState.InitializeStats(savedChar.className ?? "Fighter", nativeRuntimeLevel);
                playerState.ApplyAllocatedStats(savedChar.statStrength, savedChar.statAgility, savedChar.statEndurance, savedChar.statIntellect);
                // Only load XP from DB on fresh login — during zone transition, keep live value
                if (isFreshPlayerState)
                {
                    playerState.Experience = savedChar.experience;
                    Debug.LogError($"[SPAWN-XP] Fresh login: loaded XP={savedChar.experience} persistedLv={savedChar.level} nativeLv={playerState.Level} from DB for {conn.LoginName}");
                }
                else
                {
                    Debug.LogError($"[SPAWN-XP] Zone transition: keeping live XP={playerState.Experience} Lv={playerState.Level} for {conn.LoginName}");
                }
                playerState.RefreshNativeRegenFactors("spawn-authored-hero-desc");

                // NOTE: HP/Mana restore is done AFTER CalculateEquipmentBonuses below,
                // because InitializeStats sets MaxHP/MaxMana to base class values only.
                // Equipment bonuses increase the max, so we need the real max for comparison.

                InitializePlayerSkillLevels(conn, savedChar);

                // Load saved town portal from DB (survives crashes)
                if (!string.IsNullOrEmpty(savedChar.tpZone))
                {
                    conn.HasSavedTownPortal = true;
                    conn.TownPortalZoneName = savedChar.tpZone;
                    conn.TownPortalZoneId = (uint)savedChar.tpZoneId;
                    conn.TownPortalTargetZone = savedChar.tpTargetZone;
                    conn.TownPortalPosX = savedChar.tpPosX;
                    conn.TownPortalPosY = savedChar.tpPosY;
                    conn.TownPortalPosZ = savedChar.tpPosZ;
                    Debug.LogError($"[SPAWN] Loaded saved town portal: {savedChar.tpZone}");
                }

                // Load weapon damage from item database — binary formula uses this
                if (savedChar.equipment != null && !string.IsNullOrEmpty(savedChar.equipment.weapon))
                {
                    var weaponData = DatabaseLoader.FindItem(savedChar.equipment.weapon);
                    int weaponLevel = 1;
                    int weaponStoredLevel = -1;
                    if (savedChar.equipment.slotLevel != null && savedChar.equipment.slotLevel.TryGetValue("weapon", out int savedWeaponLevel) && savedWeaponLevel >= 0)
                    {
                        weaponLevel = savedWeaponLevel;
                        weaponStoredLevel = savedWeaponLevel;
                    }
                    else
                        weaponLevel = DungeonRunners.Managers.RarityHelper.GetItemLevel(savedChar.equipment.weapon);
                    var weaponNode = GCDatabase.Instance.ResolveWithInheritance(savedChar.equipment.weapon);
                    (float damage, float volatility, float range, float cooldown, string weaponClass, float weaponSpeed,
                     string damageType, string weaponCategory, bool useProjectile, float projectileSpeed, float projectileSize, int burstCount) weaponStats = default;
                    if (weaponNode == null)
                        RuntimeEvidenceManager.LogFallbackHit("damage-weapon-desc", "missing-gc-node", $"source=spawn weapon={savedChar.equipment.weapon} native=Weapon::ComputeAttributes-return", 64);
                    else
                        weaponStats = GCDatabase.Instance.GetWeaponStats(savedChar.equipment.weapon);
                    float authoredWeaponDamage = weaponStats.damage > 0f ? weaponStats.damage : 0f;
                    float authoredWeaponVolatility = weaponStats.volatility > 0f ? weaponStats.volatility : 0.25f;
                    string resolvedWeaponClass = !string.IsNullOrEmpty(weaponStats.weaponClass) ? weaponStats.weaponClass : weaponData != null && !string.IsNullOrEmpty(weaponData.weaponClass) ? weaponData.weaponClass : "";
                    string resolvedDamageType = !string.IsNullOrEmpty(weaponStats.damageType) ? weaponStats.damageType : "";
                    if (authoredWeaponDamage > 0f &&
                        TryResolveNativeWeaponDescIds("spawn", savedChar.equipment.weapon, resolvedWeaponClass, resolvedDamageType, out int nativeWeaponClassId, out int nativeDamageTypeId))
                    {
                        playerState.WeaponDamage = authoredWeaponDamage;
                        playerState.WeaponDamageVolatility = Mathf.Clamp(authoredWeaponVolatility, 0f, 0.95f);
                        playerState.WeaponLevel = Math.Max(1, weaponLevel);
                        playerState.WeaponClass = resolvedWeaponClass;
                        playerState.WeaponDamageType = resolvedDamageType;
                        playerState.WeaponCategory = !string.IsNullOrEmpty(weaponStats.weaponCategory) ? weaponStats.weaponCategory : "";
                        playerState.WeaponStatsResolved = true;
                        playerState.NativeWeaponClassId = nativeWeaponClassId;
                        playerState.NativeDamageTypeId = nativeDamageTypeId;
                        DamageComputer.ApplyNativeWeaponRuntimeBaseDamage(playerState, playerState.Level, weaponStoredLevel, playerState.WeaponLevel, "spawn");
                        playerState.WeaponRange = weaponStats.range > 0 ? Mathf.RoundToInt(weaponStats.range) : weaponData != null && weaponData.range > 0 ? weaponData.range : 0;
                        playerState.WeaponCooldown = weaponStats.cooldown > 0 ? weaponStats.cooldown : weaponData != null && weaponData.cooldown > 0 ? weaponData.cooldown : 0f;
                        playerState.WeaponSpeed = weaponStats.weaponSpeed > 0 ? weaponStats.weaponSpeed : weaponData != null && weaponData.weaponSpeed > 0 ? weaponData.weaponSpeed : 105f;
                        playerState.WeaponUsesProjectile = weaponStats.useProjectile;
                        playerState.WeaponProjectileSpeed = weaponStats.projectileSpeed;
                        playerState.WeaponProjectileSize = weaponStats.projectileSize;
                        playerState.WeaponBurstCount = Math.Max(1, weaponStats.burstCount);
                        Debug.LogError($"[SPAWN] Weapon '{savedChar.equipment.weapon}' damage={playerState.WeaponDamage} vol={playerState.WeaponDamageVolatility:F2} level={playerState.WeaponLevel} nativeDamageLevel={playerState.NativeWeaponDamageLevel} nativeBaseDamage={playerState.NativeWeaponBaseDamage} nativeBaseSource={playerState.NativeWeaponBaseDamageSource} class={playerState.WeaponClass}/{playerState.NativeWeaponClassId} damageType={playerState.WeaponDamageType}/{playerState.NativeDamageTypeId} category={playerState.WeaponCategory} range={playerState.WeaponRange} cooldown={playerState.WeaponCooldown:F2} speed={playerState.WeaponSpeed:F2} useProjectile={playerState.WeaponUsesProjectile} projectileSpeed={playerState.WeaponProjectileSpeed:F2} projectileSize={playerState.WeaponProjectileSize:F2} burst={playerState.WeaponBurstCount} from authored data");
                    }
                    else
                    {
                        playerState.WeaponDamage = 0f;
                        playerState.WeaponDamageVolatility = 0f;
                        playerState.WeaponLevel = 0;
                        playerState.WeaponClass = "";
                        playerState.WeaponDamageType = "";
                        playerState.WeaponCategory = "";
                        playerState.WeaponStatsResolved = false;
                        playerState.NativeWeaponClassId = 0;
                        playerState.NativeDamageTypeId = -1;
                        playerState.NativeWeaponDamageLevel = 0;
                        playerState.NativeWeaponBaseDamage = 0;
                        playerState.NativeWeaponBaseDamageTracksPlayerLevel = false;
                        playerState.NativeWeaponBaseDamageSource = "spawn:unresolved";
                        playerState.WeaponRange = 0;
                        playerState.WeaponCooldown = 0f;
                        playerState.WeaponSpeed = 0f;
                        playerState.WeaponUsesProjectile = false;
                        playerState.WeaponProjectileSpeed = 0f;
                        playerState.WeaponProjectileSize = 0f;
                        playerState.WeaponBurstCount = 1;
                        RuntimeEvidenceManager.LogFallbackHit("damage-level", "spawn-weapon-unresolved", $"weapon={savedChar.equipment.weapon}", 32);
                        Debug.LogError($"[SPAWN] Weapon '{savedChar.equipment.weapon}' unresolved; leaving weapon damage fields blocked until equipment data resolves");
                    }
                }

                Debug.LogError($"[SPAWN] PlayerState initialized: Key='{spawnConnIdKey}', Level={playerState.Level}, XP={playerState.Experience}, WeaponDmg={playerState.WeaponDamage}, WeaponVol={playerState.WeaponDamageVolatility}, WeaponLevel={playerState.WeaponLevel}, NativeBaseDamage={playerState.NativeWeaponBaseDamage}, NativeBaseSource={playerState.NativeWeaponBaseDamageSource}, Op12HP={playerState.Op12HP}, SynchHP={playerState.SynchHP}");

                // Initialize QuestManager
                var activeQuests = ConvertToActiveQuests(savedChar.activeQuests);

                // Assign InstanceIds to loaded quests (DB doesn't store them)
                // conn.NextQuestInstanceId starts at 1; advance it past all loaded quests
                foreach (var loadedQ in activeQuests)
                    loadedQ.InstanceId = conn.NextQuestInstanceId++;

                var completedQuests = savedChar.completedQuests ?? new List<string>();
                var unlockedCheckpoints = savedChar.unlockedCheckpoints ?? new List<string>();
                if (unlockedCheckpoints.Count == 0)
                {
                    unlockedCheckpoints.Add("world.checkpoints.TownCheckpoint");
                    unlockedCheckpoints.Add("world.checkpoints.TutorialCheckpoint");
                }
                QuestManager.Instance.InitializePlayer(conn.ConnId.ToString(), activeQuests, completedQuests, unlockedCheckpoints, playerState.Level);
                Debug.LogError($"[SPAWN] QuestManager initialized: persistedLevel={savedChar.level} nativeLevel={playerState.Level}, {activeQuests.Count} active, {completedQuests.Count} completed");

                GCObject avatar;
                GCObject player;





                // ═══════════════════════════════════════════════════════════════════════════════
                // CHECK IF ZONE TRANSITION OR FIRST LOGIN
                // ═══════════════════════════════════════════════════════════════════════════════


                // FIRST LOGIN - Create new entities
                Debug.LogError("=== FIRST LOGIN - CREATING NEW ENTITIES ===");

                // Check for zone transition BEFORE creating entities
                if (isZoneTransition)
                {
                    // ZONE TRANSITION: REUSE existing objects but REASSIGN ALL entity IDs
                    // The zone disconnect cleans up client entities. The new spawn packet
                    // must create everything fresh. If we don't reset _nextEntityId, NPC IDs
                    // keep climbing and eventually collide with character-list entities.
                    // By resetting to 10, every zone gets identical entity layout.
                    Debug.LogError("=== ZONE TRANSITION: REUSING OBJECTS, REASSIGNING ALL IDs ===");
                    avatar = conn.Avatar;
                    player = conn.Player;

                    // Clear stale tracking from previous zone
                    if (_playerComponentTypes.ContainsKey(spawnConnIdKey))
                        _playerComponentTypes[spawnConnIdKey].Clear();
                    if (_playerManipulatorsIds.ContainsKey(spawnConnIdKey))
                        _playerManipulatorsIds.Remove(spawnConnIdKey);
                    if (_playerEquippedItems.ContainsKey(spawnConnIdKey))
                        _playerEquippedItems[spawnConnIdKey].Clear();

                    _nextEntityId = (uint)(conn.ConnId * 500 + 10);

                    avatar.Id = _nextEntityId++;
                    player.Id = _nextEntityId++;

                    // Re-assign avatar component IDs (same walk as first login)
                    // Read fresh equipment from DB, rebuild both Manipulators and Equipment
                    var freshChar = CharacterRepository.GetCharacter(_selectedCharacter[conn.LoginName].Id);

                    var freshEquipItems = new List<GCObject>();
                    if (freshChar?.equipment != null)
                    {
                        string[] eqSlots = { freshChar.equipment.weapon, freshChar.equipment.armor,
                            freshChar.equipment.helmet, freshChar.equipment.gloves, freshChar.equipment.boots,
                            freshChar.equipment.shoulders, freshChar.equipment.shield,
                            freshChar.equipment.ring1, freshChar.equipment.ring2, freshChar.equipment.amulet };
                        string[] eqSlotNames = { "weapon", "armor", "helmet", "gloves", "boots",
                            "shoulders", "shield", "ring1", "ring2", "amulet" };
                        for (int eqIdx = 0; eqIdx < eqSlots.Length; eqIdx++)
                        {
                            string gc = eqSlots[eqIdx];
                            if (string.IsNullOrEmpty(gc)) continue;
                            // Compute prefixed gc class via single source of truth in GCObject
                            string fixedGc = GCObject.GetPacketGCClassFor(gc);
                            string gcLower = fixedGc.ToLower();
                            string nc = "Armor";
                            if (gcLower.Contains("ring") || gcLower.Contains("amulet"))
                                nc = "Item";
                            else if (gcLower.Contains("sword") || gcLower.Contains("axe") || gcLower.Contains("mace") || gcLower.Contains("dagger") || gcLower.Contains("hammer") || gcLower.Contains("staff") || gcLower.Contains("spear") || gcLower.Contains("pick") || gcLower.Contains("club") || gcLower.Contains("katana") || gcLower.Contains("polearm"))
                                nc = "MeleeWeapon";
                            else if (gcLower.Contains("bow") || gcLower.Contains("gun") || gcLower.Contains("crossbow") || gcLower.Contains("cannon"))
                                nc = "RangedWeapon";
                            freshEquipItems.Add(new GCObject
                            {
                                GCClass = fixedGc,
                                NativeClass = nc,
                                StoredRarity = (freshChar.equipment.slotRarity != null &&
                                    freshChar.equipment.slotRarity.TryGetValue(eqSlotNames[eqIdx], out int eqRar)) ? eqRar : -1,
                                StoredLevel = (freshChar.equipment.slotLevel != null &&
                                    freshChar.equipment.slotLevel.TryGetValue(eqSlotNames[eqIdx], out int eqLvl)) ? eqLvl : -1,
                                // Dual-wield: weapon in "shield" slot needs TargetSlot=11 so packets send correct slot
                                TargetSlot = (eqSlotNames[eqIdx] == "shield" && (nc == "MeleeWeapon" || nc == "RangedWeapon")) ? (uint?)11 : null
                            });
                        }
                        Debug.LogError($"[ZONE-EQUIP] Built {freshEquipItems.Count} fresh equipment items from DB");
                    }

                    // Manipulators: strip old equipment (keep skills), add fresh from DB
                    GCObject ztManipulators = avatar.Children.FirstOrDefault(c => c.GCClass == "Manipulators");
                    if (ztManipulators != null)
                    {
                        int removedCount = ztManipulators.Children.RemoveAll(c =>
                            c.NativeClass == "Armor" || c.NativeClass == "MeleeWeapon" ||
                            c.NativeClass == "RangedWeapon" || c.NativeClass == "Item");
                        Debug.LogError($"[ZONE-MANIP] Stripped {removedCount} stale equipment, {ztManipulators.Children.Count} skills remain");

                        foreach (var eqItem in freshEquipItems)
                            ztManipulators.Children.Add(eqItem);

                        ztManipulators.Id = _nextEntityId++;
                        foreach (var child in ztManipulators.Children)
                            child.Id = _nextEntityId++;
                        conn.ManipulatorsComponentId = (ushort)ztManipulators.Id;
                        TrackManipulatorsId(spawnConnIdKey, (ushort)ztManipulators.Id);
                    }
                    // Equipment: clear and rebuild from same fresh data
                    GCObject ztEquipment = avatar.Children.FirstOrDefault(c => c.GCClass == "avatar.base.Equipment");
                    if (ztEquipment != null)
                    {
                        ztEquipment.Id = _nextEntityId++;
                        ztEquipment.Children.Clear();
                        foreach (var eqItem in freshEquipItems)
                            ztEquipment.Children.Add(eqItem);
                        TrackComponent(spawnConnIdKey, (ushort)ztEquipment.Id, "Equipment");
                    }
                    GCObject ztQM = player.Children.FirstOrDefault(c => c.GCClass == "QuestManager");
                    if (ztQM != null)
                    {
                        ztQM.Id = _nextEntityId++;
                        conn.QuestManagerId = (ushort)ztQM.Id;
                    }
                    GCObject ztDM = player.Children.FirstOrDefault(c => c.GCClass == "DialogManager");
                    if (ztDM != null)
                    {
                        ztDM.Id = _nextEntityId++;
                        conn.DialogManagerId = (ushort)ztDM.Id;
                    }
                    GCObject ztUC = avatar.Children.FirstOrDefault(c => c.GCClass == "UnitContainer");
                    if (ztUC != null)
                    {
                        ztUC.Id = _nextEntityId++;
                        foreach (var child in ztUC.Children)
                            child.Id = _nextEntityId++;
                        conn.UnitContainerId = (ushort)ztUC.Id;                          // ADD THIS
                        TrackComponent(spawnConnIdKey, (ushort)ztUC.Id, "UnitContainer"); // ADD THIS
                    }
                    GCObject ztMod = avatar.Children.FirstOrDefault(c => c.GCClass == "Modifiers");
                    if (ztMod != null)
                    {
                        ztMod.Id = _nextEntityId++;
                        conn.ModifiersId = (ushort)ztMod.Id;
                    }
                    GCObject ztSkills = avatar.Children.FirstOrDefault(c => c.GCClass == "avatar.base.skills");
                    if (ztSkills != null) ztSkills.Id = _nextEntityId++;
                    GCObject ztUB = avatar.Children.FirstOrDefault(c => c.GCClass == "avatar.base.UnitBehavior");
                    if (ztUB != null) ztUB.Id = _nextEntityId++;

                    Debug.LogError($"✅ Zone transition IDs reassigned. Avatar={avatar.Id}, Player={player.Id}, next={_nextEntityId}");

                    // Store avatar entity ID for trainer packets
                    if (_playerAvatarEntityId.TryGetValue(spawnConnIdKey, out uint previousAvatarIdZone) && previousAvatarIdZone != avatar.Id)
                    {
                        Combat.WeaponCycleTracker.Instance.ClearConnection(spawnConnIdKey);
                        CombatManager.Instance.UnregisterPlayer(previousAvatarIdZone);
                        Debug.LogError($"[COMBAT-LIFECYCLE] avatar id changed conn={spawnConnIdKey} old={previousAvatarIdZone} new={avatar.Id}");
                    }
                    _playerAvatarEntityId[spawnConnIdKey] = avatar.Id;

                    CalculateEquipmentBonuses(conn.ConnId.ToString(), avatar);
                    if (useNativeFullHPBootstrap)
                    {
                        ApplyNativeFullHPBootstrap(conn, playerState, spawnHPPreserve, "zone");
                    }
                    else if (ApplyPlayerHPPreserve(conn, playerState, spawnHPPreserve, "zone", true))
                    {
                        Debug.LogError($"[ZONE-HP-PRESERVE] source={hpToKeepSource} preserved={hpToKeepWire} applied={playerState.CurrentHPWire}/{playerState.MaxHPWire} bootstrap={useNativeFullHPBootstrap} live={preservedHPFromLiveState}");
                        Debug.LogError($"[ZONE-HP-REGEN] Native damage regen cooldown restored ticks={PlayerState.NativeDamageRegenSuppressTicks} hp={playerState.CurrentHPWire}/{playerState.MaxHPWire}");
                    }
                    else
                    {
                        Debug.LogError($"[ZONE-HP-FULL] Restoring full HP source={hpToKeepSource} preserved={hpToKeepWire} bootstrap={useNativeFullHPBootstrap}");
                        playerState.RestoreToFull();
                    }
                    if (manaToKeepWire > 0)
                        playerState.SetCurrentMana(manaToKeepWire, "ZONE-HP-PRESERVE", false);
                    conn.IgnoreClientHPUntilTime = 0f;
                    RecordPlayerHPKnown(conn, "ZONE-HP-INIT", playerState.CurrentHPWire);

                    // DFC HitPoints: use high value so client clamps to its own calculated max (original pattern)
                    foreach (var prop in avatar.Properties)
                    {
                        if (prop is UInt32Property up && up.Name == "HitPoints")
                        {
                            up.Value = 1337;
                            break;
                        }
                    }
                    Debug.LogError($"[ZONE] HP={playerState.CurrentHPWire / 256}/{playerState.MaxHPWire / 256} Mana={playerState.CurrentManaWire / 256}/{playerState.MaxManaWire / 256}");

                    // Save stats to DB after zone transition
                    try { SavePlayerLevel(conn); }
                    catch (Exception ex) { Debug.LogError($"[ZONE] Save failed: {ex.Message}"); }

                    playerState.LogFullState("ZONE-TRANSITION");
                }
                else
                {
                    // FIRST LOGIN: Create new entities and assign ALL IDs
                    Debug.LogError("=== FIRST LOGIN: CREATING NEW ENTITIES ===");

                    _nextEntityId = (uint)(conn.ConnId * 500 + 10);

                    avatar = GCObjectFactory.LoadAvatar(savedChar);
                    player = GCObjectFactory.NewPlayer(character.Name);

                    CalculateEquipmentBonuses(conn.ConnId.ToString(), avatar);
                    playerState.LogFullState("AFTER-AVATAR-LOAD");

                    // Store on connection for zone transitions
                    conn.Avatar = avatar;
                    conn.Player = player;

                    Debug.LogError("=== ASSIGNING ENTITY IDs (FIRST LOGIN ONLY) ===");

                    avatar.Id = _nextEntityId++;
                    Debug.LogError($"✓ [OP1] Avatar ID: {avatar.Id}");

                    player.Id = _nextEntityId++;
                    Debug.LogError($"✓ [OP2] Player ID: {player.Id}");

                    Debug.LogError("=== ASSIGNING AVATAR COMPONENT IDs ===");

                    GCObject tempManipulators = avatar.Children.FirstOrDefault(c => c.GCClass == "Manipulators");
                    if (tempManipulators != null)
                    {
                        tempManipulators.Id = _nextEntityId++;
                        Debug.LogError($"✓ [OP4] Manipulators ID: {tempManipulators.Id}");
                        foreach (var child in tempManipulators.Children)
                        {
                            child.Id = _nextEntityId++;
                            Debug.LogError($"  ↳ Manipulator child '{child.GCClass}' ID: {child.Id}");
                        }
                    }

                    GCObject tempEquipment = avatar.Children.FirstOrDefault(c => c.GCClass == "avatar.base.Equipment");
                    if (tempEquipment != null)
                    {
                        tempEquipment.Id = _nextEntityId++;
                        Debug.LogError($"✓ [OP5] Equipment ID: {tempEquipment.Id}");
                        foreach (var child in tempEquipment.Children)
                        {
                            child.Id = _nextEntityId++;
                            Debug.LogError($"  ↳ Equipment child '{child.GCClass}' ID: {child.Id}");
                        }
                    }

                    Debug.LogError("=== ASSIGNING PLAYER COMPONENT IDs ===");

                    GCObject tempQuestManager = player.Children.FirstOrDefault(c => c.GCClass == "QuestManager");
                    if (tempQuestManager == null)
                    {
                        Debug.LogError("🔧 QuestManager not found - creating it");
                        tempQuestManager = new GCObject
                        {
                            GCClass = "QuestManager",
                            NativeClass = "QuestManager",
                            Name = "QuestManager"
                        };
                        player.AddChild(tempQuestManager);
                    }
                    tempQuestManager.Id = _nextEntityId++;
                    Debug.LogError($"✓ [OP6] QuestManager ID: {tempQuestManager.Id}");

                    GCObject tempDialogManager = player.Children.FirstOrDefault(c => c.GCClass == "DialogManager");
                    if (tempDialogManager == null)
                    {
                        Debug.LogError("🔧 DialogManager not found - creating it");
                        tempDialogManager = new GCObject
                        {
                            GCClass = "DialogManager",
                            NativeClass = "DialogManager",
                            Name = "DialogManager"
                        };
                        player.AddChild(tempDialogManager);
                    }
                    tempDialogManager.Id = _nextEntityId++;
                    Debug.LogError($"✓ [OP7] DialogManager ID: {tempDialogManager.Id}");

                    GCObject tempUnitContainer = avatar.Children.FirstOrDefault(c => c.GCClass == "UnitContainer");
                    if (tempUnitContainer != null)
                    {
                        tempUnitContainer.Id = _nextEntityId++;
                        Debug.LogError($"✓ [OP8] UnitContainer ID: {tempUnitContainer.Id}");
                        foreach (var child in tempUnitContainer.Children)
                        {
                            child.Id = _nextEntityId++;
                            Debug.LogError($"  ↳ Inventory '{child.GCClass}' ID: {child.Id}");
                        }
                    }

                    GCObject tempModifiers = avatar.Children.FirstOrDefault(c => c.GCClass == "Modifiers");
                    if (tempModifiers != null)
                    {
                        tempModifiers.Id = _nextEntityId++;
                        conn.ModifiersId = (ushort)tempModifiers.Id;
                        Debug.LogError($"✓ [OP9] Modifiers ID: {tempModifiers.Id}");

                    }

                    GCObject tempSkills = avatar.Children.FirstOrDefault(c => c.GCClass == "avatar.base.skills");
                    if (tempSkills == null)
                    {
                        Debug.LogError("🔧 Skills not found - creating it");
                        tempSkills = new GCObject
                        {
                            GCClass = "avatar.base.skills",
                            NativeClass = "Skills",
                            Name = "Skills"
                        };
                        avatar.AddChild(tempSkills);
                    }
                    tempSkills.Id = _nextEntityId++;
                    Debug.LogError($"✓ [OP10] Skills ID: {tempSkills.Id}");

                    GCObject tempUnitBehavior = avatar.Children.FirstOrDefault(c => c.GCClass == "avatar.base.UnitBehavior");
                    if (tempUnitBehavior != null)
                    {
                        tempUnitBehavior.Id = _nextEntityId++;
                        Debug.LogError($"✓ [OP11] UnitBehavior ID: {tempUnitBehavior.Id}");
                    }

                    Debug.LogError($"=== ALL IDs ASSIGNED. Next available ID: {_nextEntityId} ===");
                }

                Debug.LogError($"Entity mapping: Avatar ID: 0x{avatar.Id:X4}, Player ID: 0x{player.Id:X4}");

                // ── Store avatar entity ID for trainer 0x37 packets ──
                if (_playerAvatarEntityId.TryGetValue(spawnConnIdKey, out uint previousAvatarIdNormal) && previousAvatarIdNormal != avatar.Id)
                {
                    Combat.WeaponCycleTracker.Instance.ClearConnection(spawnConnIdKey);
                    CombatManager.Instance.UnregisterPlayer(previousAvatarIdNormal);
                    Debug.LogError($"[COMBAT-LIFECYCLE] avatar id changed conn={spawnConnIdKey} old={previousAvatarIdNormal} new={avatar.Id}");
                }
                _playerAvatarEntityId[spawnConnIdKey] = avatar.Id;

                if (useNativeFullHPBootstrap)
                {
                    ApplyNativeFullHPBootstrap(conn, playerState, spawnHPPreserve, isZoneTransition ? "zone-final" : "spawn");
                }
                else if (ApplyPlayerHPPreserve(conn, playerState, spawnHPPreserve, isZoneTransition ? "zone-final" : "spawn", true))
                {
                    Debug.LogError($"[SPAWN-HP-PRESERVE] source={hpToKeepSource} preserved={hpToKeepWire} applied={playerState.CurrentHPWire}/{playerState.MaxHPWire} bootstrap={useNativeFullHPBootstrap} live={preservedHPFromLiveState}");
                    Debug.LogError($"[SPAWN-HP-REGEN] Native damage regen cooldown restored ticks={PlayerState.NativeDamageRegenSuppressTicks} hp={playerState.CurrentHPWire}/{playerState.MaxHPWire}");
                }
                else
                {
                    Debug.LogError($"[SPAWN-HP-FULL] Restoring full HP source={hpToKeepSource} preserved={hpToKeepWire} bootstrap={useNativeFullHPBootstrap}");
                    playerState.RestoreToFull();
                }

                if (manaToKeepWire > 0)
                    playerState.SetCurrentMana(manaToKeepWire, "SPAWN-HP-PRESERVE", false);

                conn.IgnoreClientHPUntilTime = 0f;
                RecordPlayerHPKnown(conn, "SPAWN-HP-INIT", playerState.CurrentHPWire);

                if (!isZoneTransition)
                {
                    foreach (var prop in avatar.Properties)
                    {
                        if (prop is UInt32Property up && up.Name == "HitPoints")
                        {
                            up.Value = 1337;
                            break;
                        }
                    }
                    Debug.LogError($"[SPAWN] Full HP: {playerState.MaxHPWire / 256} Mana: {playerState.MaxManaWire / 256} (savedHP={savedChar.currentHP / 256} savedMana={savedChar.currentMana / 256})");
                }

                playerState.AvatarHP = playerState.CurrentHPWire;
                if (useNativeFullHPBootstrap)
                {
                    Debug.LogError($"[ZONE-HP-BOOTSTRAP] Completed bootstrap flag source={hpToKeepSource} preservedKnown={preservedHPKnown} hp={playerState.CurrentHPWire}/{playerState.MaxHPWire}");
                    conn.NativeFullHPOnNextSpawn = false;
                }
                Debug.LogError($"[SPAWN] Server HP set: {playerState.CurrentHPWire / 256} / {playerState.MaxHPWire / 256} Mana: {playerState.CurrentManaWire / 256} / {playerState.MaxManaWire / 256}");

                // Save stats to DB on login
                try { SavePlayerLevel(conn); }
                catch (Exception ex) { Debug.LogError($"[SPAWN] Login save failed: {ex.Message}"); }








                var writer = new LEWriter();

                writer.WriteByte(0x07);  // BeginStream - ClientEntityChannel
                Debug.LogError("✓ BeginStream (ClientEntityChannel 0x07)");
                Debug.LogError($"[OP1-DEBUG] avatar.GCClass = '{avatar.GCClass}'");
                Debug.LogError($"[OP1-DEBUG] savedChar.avatarClass = '{savedChar.avatarClass}'");
                Debug.LogError($"[OP1-DEBUG] savedChar.className = '{savedChar.className}'");
                // ╔═══════════════════════════════════════════════════════════════════════════════╗
                // ║ OPERATION 1: CREATE AVATAR ENTITY                                             ║
                // ╚═══════════════════════════════════════════════════════════════════════════════╝
                Debug.LogError("=== OPERATION 1: CREATE AVATAR ===");
                int beforeOp = writer.Position;

                writer.WriteByte(0x01);
                writer.WriteUInt16((ushort)avatar.Id);
                // WriteGCType(writer, avatar.GCClass.ToLower());
                WriteGCType(writer, avatar.GCClass, preserveCase: true);  // ✅ CORRECT!
                int opBytes = (int)(writer.Position - beforeOp);
                Debug.LogError($"✓ Create Avatar ID={avatar.Id:X4} Class={avatar.GCClass.ToLower()} ({opBytes} bytes)");
                Debug.LogError($"✓ Create Avatar ID={avatar.Id:X4} Class={avatar.GCClass.ToLower()} ({opBytes} bytes)");
                Debug.LogError($"[CUMULATIVE-OP1] Total after Op1: {writer.Position} bytes");
                // ╔═══════════════════════════════════════════════════════════════════════════════╗
                // ║ OPERATION 2: CREATE PLAYER ENTITY                                             ║
                // ╚═══════════════════════════════════════════════════════════════════════════════╝
                Debug.LogError("=== OPERATION 2: CREATE PLAYER ===");
                beforeOp = writer.Position;

                writer.WriteByte(0x01);
                writer.WriteUInt16((ushort)player.Id);
                WriteGCType(writer, player.GCClass.ToLower());

                opBytes = (int)(writer.Position - beforeOp);
                Debug.LogError($"✓ Create Player ID={player.Id:X4} Class={player.GCClass.ToLower()} ({opBytes} bytes)");
                Debug.LogError($"✓ Create Player ID={player.Id:X4} Class={player.GCClass.ToLower()} ({opBytes} bytes)");
                Debug.LogError($"[CUMULATIVE-OP2] Total after Op2: {writer.Position} bytes");
                /// ╔═══════════════════════════════════════════════════════════════════════════════╗
                // ║ OPERATION 3: INIT PLAYER                                                      ║
                // ╚═══════════════════════════════════════════════════════════════════════════════╝
                Debug.LogError("=== OPERATION 3: INIT PLAYER ===");
                beforeOp = writer.Position;

                writer.WriteByte(0x02);
                writer.WriteUInt16((ushort)player.Id);

                // Player::WriteInit() - Player initialization data structure
                writer.WriteCString(player.Name);

                Debug.LogError($"[OP3] Before first uint32: position {writer.Position}");
                writer.WriteUInt32(0x00);
                Debug.LogError($"[OP3] After first uint32: position {writer.Position} (should be +4)");

                Debug.LogError($"[OP3] Before second uint32: position {writer.Position}");
                writer.WriteUInt32(0x00);
                Debug.LogError($"[OP3] After second uint32: position {writer.Position} (should be +4)");

                Debug.LogError($"[OP3] Before WriteByte membership: position {writer.Position}");
                // Player+0xA0 = membershipStatus.
                // Per-player: DB flag determines free (2) vs member (1).
                // Player+0xA0 = membershipStatus (IsAdmin@Avatar @ 0x4EE6A0)
                //   0x00 = Admin (/adminui enabled)
                //   0x01 = Member (no /adminui)
                //   0x02 = Free player (no /adminui, shadow items, XP penalty)
                byte membershipByte;
                if (IsPlayerAdmin(conn.LoginName))
                    membershipByte = 0x00;
                else if (IsPlayerFree(conn.LoginName))
                    membershipByte = 0x02;
                else
                    membershipByte = 0x01;
                writer.WriteByte(membershipByte);

                // ✅ FIX: Use dynamic zone ID from connection!
                writer.WriteUInt32(conn.CurrentZoneId);
                Debug.LogError($"[OP3-ZONE] Using zone ID: 0x{conn.CurrentZoneId:X8}");

                // ── PvP stats: load from DB for local player's spec menu ──
                uint _pvpWins = 0, _pvpRating = 0;
                if (_selectedCharacter.TryGetValue(conn.LoginName, out var _pvpSel))
                {
                    var _pvpChar = CharacterRepository.GetCharacter(_pvpSel.Id);
                    if (_pvpChar != null) { _pvpWins = (uint)_pvpChar.pvpWins; _pvpRating = (uint)_pvpChar.pvpRating; }
                }
                writer.WriteUInt32(_pvpWins);   // +0x324 pvpWins (was hardcoded 1001 debug sentinel)
                writer.WriteUInt32(_pvpRating); // +0x328 pvpRating (was hardcoded 1000 debug sentinel)
                Debug.LogError($"[OP3] After WriteUInt32(1000): position {writer.Position} (should be +4)");

                Debug.LogError($"[OP3] Before WriteByte(0x00) PvP team: position {writer.Position}");
                writer.WriteByte(0x00);   // PvP Team null string
                Debug.LogError($"[OP3] After WriteByte(0x00) PvP team: position {writer.Position} (should be +1)");

                // Posse name (empty string → client renders <No Posse>, see EXE 0x4643A0).
                string op3PosseName = savedChar.posseName ?? "";
                Debug.LogError($"[OP3] Before WriteCString posseName='{op3PosseName}': position {writer.Position}");
                writer.WriteCString(op3PosseName);
                Debug.LogError($"[OP3] After WriteCString posseName: position {writer.Position}");

                Debug.LogError($"[OP3] Before final WriteUInt32(0x00): position {writer.Position}");
                writer.WriteUInt32(0x00);
                Debug.LogError($"[OP3] After final WriteUInt32(0x00): position {writer.Position} (should be +4)");

                opBytes = (int)(writer.Position - beforeOp);
                Debug.LogError($"[OP3] After final WriteUInt32(0x00): position {writer.Position} (should be +4)");

                Debug.LogError($"[OP3-CALC] beforeOp was: {beforeOp}");
                Debug.LogError($"[OP3-CALC] current position: {writer.Position}");

                opBytes = (int)(writer.Position - beforeOp);
                Debug.LogError($"✓ Init Player complete ({opBytes} bytes)");

                Debug.LogError($"[OP3-FINAL] Position immediately after opBytes calculation: {writer.Position}");
                Debug.LogError($"✓ Init Player complete ({opBytes} bytes)");
                Debug.LogError($"[CUMULATIVE-OP3] Total after Op3: {writer.Position} bytes");
                // ╔═══════════════════════════════════════════════════════════════════════════════╗
                // ║ OPERATION 4: MANIPULATORS COMPONENT - EQUIPMENT + SKILLS (GO SERVER PATTERN) ║
                // ╚═══════════════════════════════════════════════════════════════════════════════╝
                Debug.LogError("=== OPERATION 4: MANIPULATORS COMPONENT ===");
                Debug.LogError($"[OP4-INITIAL] Position at very start of OP4: {writer.Position}");


                beforeOp = writer.Position;

                var manipulators = avatar.Children.FirstOrDefault(c => c.GCClass == "Manipulators");
                if (manipulators == null)
                {
                    Debug.LogError("❌ Manipulators not found in avatar children!");
                    return;
                }

                Debug.LogError($"[OP4-START] Manipulators ID: {manipulators.Id}");
                Debug.LogError($"[OP4-START] Total children in Manipulators: {manipulators.Children?.Count ?? 0}");
                TrackManipulatorsId(conn.ConnId.ToString(), (ushort)manipulators.Id);
                conn.ManipulatorsComponentId = (ushort)manipulators.Id;
                // Write component header (0x32 + parent ID + component ID + GCType + 0x01)
                writer.WriteByte(0x32);
                writer.WriteUInt16((ushort)avatar.Id);
                writer.WriteUInt16((ushort)manipulators.Id);
                WriteGCType(writer, "Manipulators");
                writer.WriteByte(0x01);
                Debug.LogError($"[OP4-HEADER] Component header written");

                // ╔═════════════════════════════════════════════════════════════════════════════╗
                // ║ 🔥 CRITICAL FIX: PRE-VALIDATE AND COUNT ALL ITEMS (SKILLS + EQUIPMENT)      ║
                // ╚═════════════════════════════════════════════════════════════════════════════╝
                int validChildCount = 0;
                var validChildren = new List<GCObject>();

                if (manipulators.Children != null)
                {
                    foreach (var child in manipulators.Children)
                    {
                        bool isEquipment = (child.NativeClass == "Armor" || child.NativeClass == "Item" ||
                   child.NativeClass == "MeleeWeapon" ||
                   child.NativeClass == "RangedWeapon");

                        if (isEquipment)
                        {
                            // Validate equipment is in database
                            ItemData itemData = DatabaseLoader.FindItem(child.GCClass);
                            if (itemData != null)
                            {
                                validChildren.Add(child);
                                validChildCount++;
                                Debug.LogError($"[OP4-PRECHECK] ✅ Equipment '{child.GCClass}' is VALID (in ItemDatabase)");
                            }
                            else
                            {
                                // Check authored special slot catalog (PreBuilt/PartialBuilt/etc not in items DB)
                                string lk4 = child.GCClass.ToLowerInvariant();
                                if (lk4.StartsWith("items.pal.")) lk4 = lk4.Substring(10);
                                if (DungeonRunners.Managers.MerchantManager.HasAuthoredMerchantModSlots(lk4))
                                {
                                    validChildren.Add(child);
                                    validChildCount++;
                                    Debug.LogError($"[OP4-PRECHECK] ✅ Equipment '{child.GCClass}' is VALID (in authored slot catalog)");
                                }
                                else
                                {
                                    // Check GeneralItemDatabase for rings/amulets
                                    var generalItem = DatabaseLoader.FindGeneralItem(child.GCClass);
                                    if (generalItem != null)
                                    {
                                        validChildren.Add(child);
                                        validChildCount++;
                                        Debug.LogError($"[OP4-PRECHECK] ✅ Equipment '{child.GCClass}' is VALID (in GeneralItemDatabase)");
                                    }
                                    else
                                    {
                                        Debug.LogError($"[OP4-PRECHECK] ❌ Equipment '{child.GCClass}' NOT in any database - WILL SKIP");
                                    }
                                }
                            }
                        }
                        else if (child.GCClass != null && child.GCClass.StartsWith("skills.professions.", StringComparison.OrdinalIgnoreCase))
                        {
                            Debug.LogError($"[OP4-PRECHECK] ⏭️ Profession '{child.GCClass}' is NOT an ActiveSkill - WILL SKIP");
                        }
                        else
                        {
                            validChildren.Add(child);
                            validChildCount++;
                            Debug.LogError($"[OP4-PRECHECK] ✅ Skill '{child.GCClass}' is VALID");
                        }
                    }
                }

                byte childCount = (byte)validChildCount;
                writer.WriteByte(childCount);
                Debug.LogError($"[OP4-COUNT] Writing {childCount} VALID children (skills + equipment)");

                // ═══════════════════════════════════════════════════════════════════════════════
                // WRITE ORDER: SKILLS FIRST, EQUIPMENT SECOND (original working order)
                // 
                // SLOT ID ASSIGNMENT (binary-verified from StartingSkills.gc files):
                // Starting skills get their CANONICAL slot IDs matching SkillSlot.SlotID:
                //   Fighter: Stomp→100(Key1), Butcher→105(RMB)
                //   Ranger:  PoisonBlastRadius→100(Key1), PoisonShot→105(RMB)
                //   Warlock: ShadowLightning→100(Key1), FireBolt→105(RMB)
                // All OTHER skills get IDs 200+ (no matching SkillSlot) so they appear in
                // the skill book but NOT on the hotbar. Player drags them to empty slots.
                // This matches original game behavior where only 2 of 10 slots are filled.
                // ═══════════════════════════════════════════════════════════════════════════════
                if (validChildren.Count > 0)
                {
                    // Build starting skill → slotID map from GC definitions
                    var startingSlotMap = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
                    string playerClass = (savedChar.className ?? "Fighter").ToLower();
                    if (playerClass.Contains("fight") || playerClass.Contains("warrior"))
                    {
                        startingSlotMap["skills.generic.Stomp"] = 100;
                        startingSlotMap["skills.generic.Butcher"] = 105;
                    }
                    else if (playerClass.Contains("ranger"))
                    {
                        startingSlotMap["skills.generic.PoisonBlastRadius"] = 100;
                        startingSlotMap["skills.generic.PoisonShot"] = 105;
                    }
                    else // Mage/Warlock
                    {
                        startingSlotMap["skills.generic.ShadowLightning"] = 100;
                        startingSlotMap["skills.generic.FireBolt"] = 105;
                    }
                    Debug.LogError($"[OP4-SLOTS] Class={playerClass}, starting slots: {string.Join(", ", startingSlotMap.Select(kv => $"{kv.Key}→{kv.Value}"))}");

                    // Overlay saved hotbar positions from JSON
                    if (savedChar.hotbarSlots != null)
                        foreach (var hbs in savedChar.hotbarSlots)
                        {
                            startingSlotMap[hbs.skill] = hbs.slot;
                            Debug.LogError($"[OP4-HOTBAR] Loaded saved slot: {hbs.skill} → {hbs.slot}");
                        }

                    uint nonSlotIdCounter = 200; // IDs 200+ don't match any SkillSlot
                    byte writeIndex = 0;
                    string connKey = conn.ConnId.ToString();
                    _playerSpellSlots[connKey] = new HashSet<byte>();
                    _playerManipMap[connKey] = new Dictionary<uint, string>();

                    Debug.LogError($"");
                    Debug.LogError($"[OP4] ═══════════════════════════════════════════════════════════");
                    Debug.LogError($"[OP4] 🔥 FIRST PASS: WRITING ALL SKILLS");
                    Debug.LogError($"[OP4] ═══════════════════════════════════════════════════════════");

                    foreach (var child in validChildren)
                    {
                        bool isEquipment = (child.NativeClass == "Armor" || child.NativeClass == "Item" ||
                    child.NativeClass == "MeleeWeapon" ||
                    child.NativeClass == "RangedWeapon");

                        // Only process SKILLS in this first loop
                        if (!isEquipment)
                        {
                            // Determine skill ID: starting skill → canonical slot, others → 200+
                            uint skillId;
                            if (startingSlotMap.TryGetValue(child.GCClass, out uint slotId))
                            {
                                skillId = slotId;
                                Debug.LogError($"[OP4-SKILL] {child.GCClass} → STARTING SLOT {skillId}");
                            }
                            else
                            {
                                skillId = nonSlotIdCounter++;
                                Debug.LogError($"[OP4-SKILL] {child.GCClass} → UNASSIGNED (id={skillId}, no hotbar slot)");
                            }

                            _playerSpellSlots[connKey].Add(writeIndex);
                            _playerManipMap[connKey][skillId] = child.GCClass;
                            Debug.LogError($"[OP4-SKILL] Slot {writeIndex} = SPELL: {child.GCClass} → manipId={skillId}");

                            Debug.LogError($"");
                            Debug.LogError($"[OP4-SKILL] ═══════════════════════════════════════");
                            Debug.LogError($"[OP4-SKILL] Writing skill: {child.GCClass}");

                            int childStart = writer.Position;

                            writer.WriteByte(0xFF);
                            writer.WriteCString(child.GCClass.ToLower());
                            writer.WriteUInt32(skillId);
                            byte skillLv = (byte)savedChar.GetSkillLevel(child.GCClass);
                            writer.WriteByte(skillLv);  // Skill level — client shows as "Rank X"
                            writer.WriteByte(0x00);

                            int childBytes = writer.Position - childStart;
                            Debug.LogError($"[OP4-SKILL] ✅ Complete: {child.GCClass} Lv{skillLv} ({childBytes} bytes, ID: 0x{skillId:X8})");
                            writeIndex++;
                        }
                    }

                    Debug.LogError($"");
                    Debug.LogError($"[OP4] ═══════════════════════════════════════════════════════════");
                    Debug.LogError($"[OP4] 🔥 SECOND PASS: WRITING EQUIPMENT FULL WRITEINT DATA!");
                    Debug.LogError($"[OP4] ═══════════════════════════════════════════════════════════");

                    foreach (var child in validChildren)
                    {
                        bool isEquipment = (child.NativeClass == "Armor" || child.NativeClass == "Item" ||
                    child.NativeClass == "MeleeWeapon" ||
                    child.NativeClass == "RangedWeapon");

                        if (isEquipment)
                        {
                            Debug.LogError($"[OP4-EQUIP] Slot {writeIndex} = EQUIP: {child.GCClass}");
                            Debug.LogError($"");
                            Debug.LogError($"[OP4-EQUIP] ═══════════════════════════════════════");
                            Debug.LogError($"[OP4-EQUIP] Writing FULL WriteInit for: {child.GCClass}");

                            int childStart = writer.Position;

                            // 🔥 CRITICAL: Equipment just calls WriteInit directly!
                            child.WriteInit(writer, playerState.Level);

                            int childBytes = writer.Position - childStart;
                            Debug.LogError($"[OP4-EQUIP] ✅ WriteInit complete: {childBytes} bytes");
                            if (VerbosePacketLogging) Debug.LogError($"[OP4-EQUIP] Hex: {BitConverter.ToString(writer.ToArray(), childStart, childBytes)}");
                            writeIndex++;
                        }
                    }
                }
                else
                {
                    Debug.LogError($"[OP4-WARNING] No valid children to write!");
                }

                opBytes = (int)(writer.Position - beforeOp);
                Debug.LogError($"[OP4-COMPLETE] Manipulators component: {opBytes} bytes");
                if (_playerSpellSlots.ContainsKey(conn.ConnId.ToString()))
                    Debug.LogError($"[OP4-SPELLMAP] Spell slots for {conn.LoginName}: [{string.Join(", ", _playerSpellSlots[conn.ConnId.ToString()])}]");
                if (_playerManipMap.ContainsKey(conn.ConnId.ToString()))
                {
                    foreach (var kvp in _playerManipMap[conn.ConnId.ToString()])
                        Debug.LogError($"[OP4-MANIPMAP] manipId={kvp.Key} → {kvp.Value}");
                }

                if (VerbosePacketLogging)
                {
                    Debug.LogError($"");
                    Debug.LogError($"[OP4-HEXDUMP] ═══════════════════════════════════════════════════════════");
                    Debug.LogError($"[OP4-HEXDUMP] OPERATION 4 COMPLETE HEX DUMP:");
                    StringBuilder op4Hex = new StringBuilder();
                    for (int i = beforeOp; i < writer.Position; i += 16)
                    {
                        op4Hex.Append($"{i:X8}  ");
                        for (int j = 0; j < 16; j++)
                        {
                            if (i + j < writer.Position)
                                op4Hex.Append($"{writer.ToArray()[i + j]:X2} ");
                            else
                                op4Hex.Append("   ");
                        }
                        op4Hex.Append(" |");
                        for (int j = 0; j < 16 && i + j < writer.Position; j++)
                        {
                            byte b = writer.ToArray()[i + j];
                            op4Hex.Append((b >= 32 && b < 127) ? (char)b : '.');
                        }
                        op4Hex.AppendLine("|");
                    }
                    Debug.LogError(op4Hex.ToString());
                    Debug.LogError($"[OP4-HEXDUMP] ═══════════════════════════════════════════════════════════");
                }
                Debug.LogError($"=== OPERATION 4 COMPLETE ===");
                Debug.LogError($"[OP4-COMPLETE] Manipulators component: {opBytes} bytes");
                Debug.LogError($"[CUMULATIVE-OP4] Total after Op4: {writer.Position} bytes");


                // ╔═══════════════════════════════════════════════════════════════════════════════╗
                // ║ OPERATION 5: EQUIPMENT - WITH ITEM TRACKING!                                  ║
                // ╚═══════════════════════════════════════════════════════════════════════════════╝
                Debug.LogError("=== OPERATION 5: EQUIPMENT COMPONENT ===");
                beforeOp = writer.Position;

                var equipment = avatar.Children.FirstOrDefault(c => c.GCClass == "avatar.base.Equipment");

                if (equipment == null)
                {
                    Debug.LogError("❌ Equipment not found in avatar children!");
                    return;
                }

                Debug.LogError($"[OP5-START] Equipment ID: {equipment.Id}, Children: {equipment.Children?.Count ?? 0}");
                TrackComponent(conn.ConnId.ToString(), (ushort)equipment.Id, "Equipment");

                int op5HeaderStart = writer.Position;
                writer.WriteByte(0x32);
                writer.WriteUInt16((ushort)avatar.Id);
                writer.WriteUInt16((ushort)equipment.Id);
                WriteGCType(writer, "avatar.base.Equipment");
                writer.WriteByte(0x01);
                int op5HeaderBytes = writer.Position - op5HeaderStart;
                Debug.LogError($"[OP5-HEADER] Component header written ({op5HeaderBytes} bytes)");

                var equippedItems = equipment.Children?.Where(c =>
      c.NativeClass == "Armor" ||
      c.NativeClass == "MeleeWeapon" ||
      c.NativeClass == "RangedWeapon" ||
      c.NativeClass == "Item"  // Rings and Amulets
  ).ToList() ?? new List<GCObject>();

                var validItems = new List<GCObject>();
                foreach (var item in equippedItems)
                {
                    ItemData itemData = DatabaseLoader.FindItem(item.GCClass);
                    if (itemData != null)
                    {
                        validItems.Add(item);
                        Debug.LogError($"[OP5-VALID] Item '{item.GCClass}' found in ItemDatabase - will write");
                    }
                    else
                    {
                        // Check if it's in the authored special slot catalog (PreBuilt/PartialBuilt/etc)
                        string lk = item.GCClass.ToLowerInvariant();
                        if (lk.StartsWith("items.pal.")) lk = lk.Substring(10);
                        if (DungeonRunners.Managers.MerchantManager.HasAuthoredMerchantModSlots(lk))
                        {
                            validItems.Add(item);
                            Debug.LogError($"[OP5-VALID] Item '{item.GCClass}' found in authored slot catalog - will write");
                        }
                        else
                        {
                            // Check if it's a ring or amulet in GeneralItemDatabase
                            var generalItem = DatabaseLoader.FindGeneralItem(item.GCClass);
                            if (generalItem != null)
                            {
                                validItems.Add(item);
                                Debug.LogError($"[OP5-VALID] Item '{item.GCClass}' found in GeneralItemDatabase (ring/amulet) - will write");
                            }
                            else
                            {
                                Debug.LogError($"[OP5-SKIP] Item '{item.GCClass}' NOT in any database - SKIPPING!");
                            }
                        }
                    }
                }

                byte itemCount = (byte)validItems.Count;

                int op5CountBytePos = writer.Position;
                writer.WriteByte(itemCount);
                Debug.LogError($"[OP5-COUNT] Writing {itemCount} VALID equipped items at position {op5CountBytePos}");

                foreach (var item in validItems)
                {
                    int itemStart = writer.Position;

                    Debug.LogError($"");
                    Debug.LogError($"[OP5-ITEM] ═══════════════════════════════════════");
                    Debug.LogError($"[OP5-ITEM] Writing FULL DATA for: {item.GCClass}");
                    Debug.LogError($"[OP5-ITEM] Native class: {item.NativeClass}");
                    Debug.LogError($"[OP5-ITEM] ═══════════════════════════════════════");

                    writer.WriteByte(0xFF);
                    writer.WriteCString(item.GetPacketGCClass());

                    uint equipSlot = 0;
                    string gcLower = item.GCClass.ToLower();

                    //------------------------------------------
                    // DETECTION FLAGS (SEPARATE)
                    //------------------------------------------
                    bool isMythic = item.GCClass.IndexOf("MythicPAL", StringComparison.OrdinalIgnoreCase) >= 0
                    || DungeonRunners.Managers.MerchantManager.HasAuthoredMerchantModSlots(
                        gcLower.StartsWith("items.pal.") ? gcLower.Substring(10) : gcLower);
                    bool isPartialbuilt = gcLower.Contains("partialbuilt");
                    bool isPrebuilt = gcLower.Contains("prebuilt");
                    bool isGeneratedboss = gcLower.Contains("generatedboss");
                    bool hasMythicSeasonal = gcLower.Contains("mythicseasonal");
                    bool hasMythicInName = gcLower.Contains("mythic") && !hasMythicSeasonal;
                    bool hasSeasonal = gcLower.Contains("seasonal");
                    bool hasWishingwell = gcLower.Contains("wishingwell");
                    bool hasUnique = gcLower.Contains("unique");
                    bool hasJustSeasonal = hasSeasonal && !gcLower.Contains("mythic") && !hasUnique;
                    bool hasUniqueSeasonal = hasUnique && hasSeasonal;

                    //------------------------------------------
                    // EQUIPMENT SLOT DETECTION
                    //------------------------------------------
                    if (gcLower.Contains("helm"))
                        equipSlot = 5;
                    else if (gcLower.Contains("shoulder") || gcLower.Contains("pauldron"))
                        equipSlot = 8;
                    else if (gcLower.Contains("armor") || gcLower.Contains("chest") || gcLower.Contains("body"))
                        equipSlot = 6;
                    else if (gcLower.Contains("gloves"))
                        equipSlot = 2;
                    else if (gcLower.Contains("boots"))
                        equipSlot = 7;
                    else if (gcLower.Contains("shield"))
                        equipSlot = 11;
                    // RINGS AND AMULETS - Slots 3, 4, 9
                    // Need to track which ring this is (first = slot 3, second = slot 4)
                    else if (gcLower.Contains("ring"))
                    {
                        // Check if we already have a ring in slot 3
                        var existingRing = GetEquippedItem(conn.ConnId.ToString(), 3);
                        if (existingRing == null)
                            equipSlot = 3;  // RingSlot1
                        else
                            equipSlot = 4;  // RingSlot2
                    }
                    else if (gcLower.Contains("amulet"))
                        equipSlot = 1;  // AmuletSlot
                    else if (gcLower.Contains("weapon") || gcLower.Contains("sword") || gcLower.Contains("axe") || gcLower.Contains("mace") || gcLower.Contains("staff") || gcLower.Contains("pick") || gcLower.Contains("bow") || gcLower.Contains("gun") || gcLower.Contains("crossbow") || gcLower.Contains("cannon") || gcLower.Contains("club") || gcLower.Contains("katana") || gcLower.Contains("polearm"))
                    {
                        // Dual-wield: if TargetSlot is 11, this weapon is in the off-hand
                        if (item.TargetSlot.HasValue && item.TargetSlot.Value == 11)
                            equipSlot = 11;
                        else
                            equipSlot = 10;
                    }

                    Debug.LogError($"[OP5-ITEM] Equipment slot: {equipSlot}, isMythic: {isMythic}, isPartialbuilt: {isPartialbuilt}, isPrebuilt: {isPrebuilt}, isGeneratedboss: {isGeneratedboss}, hasMythicSeasonal: {hasMythicSeasonal}, hasMythicInName: {hasMythicInName}, hasSeasonal: {hasSeasonal}, hasWishingwell: {hasWishingwell}, hasUnique: {hasUnique}, hasJustSeasonal: {hasJustSeasonal}, hasUniqueSeasonal: {hasUniqueSeasonal}");
                    TrackEquippedItem(conn.ConnId.ToString(), equipSlot, item);

                    ItemData itemData = DatabaseLoader.FindItem(item.GCClass);

                    //------------------------------------------
                    // MOD COUNT LOGIC (SEPARATE)
                    //------------------------------------------
                    //------------------------------------------
                    // MOD COUNT LOGIC (SEPARATE)
                    //------------------------------------------
                    int modCount;
                    if (item.NativeClass == "Item" || gcLower.Contains("ring") || gcLower.Contains("amulet"))
                    {
                        bool isAmulet = gcLower.Contains("amulet");

                        if (isAmulet)
                        {
                            bool isMythicAmulet = gcLower.Contains("mythic");
                            int amuletLevel = isMythicAmulet ? (playerState.Level + 3) : item.GetItemRequiredLevel();


                            int amuletModCount;
                            List<string> amuletMods;

                            if (isMythicAmulet)
                            {
                                amuletMods = DatabaseLoader.GetAmuletModifiers(item.GCClass);
                                amuletModCount = amuletMods.Count;
                            }
                            else
                            {
                                if (gcLower.Contains("amuletpal.amulet"))
                                {
                                    amuletModCount = 1;
                                    amuletMods = new List<string>();
                                }
                                else if (gcLower.Contains("questamuletpal") || gcLower.Contains("uniqueamuletpal"))
                                {
                                    amuletModCount = 2;
                                    amuletMods = new List<string>();
                                }
                                else
                                {
                                    amuletModCount = 1;
                                    amuletMods = new List<string>();
                                }
                            }

                            Debug.LogError($"[OP5-AMULET] GCClass: {item.GCClass}, Slot: {equipSlot}, Level: {amuletLevel}, ModCount: {amuletModCount}, Mythic: {isMythicAmulet}");

                            // SLOT (4 bytes)
                            writer.WriteUInt32(equipSlot);

                            // X POSITION (1 byte)
                            writer.WriteByte(0x00);

                            // Y POSITION (1 byte)
                            writer.WriteByte(0x00);

                            // COUNT (1 byte)
                            writer.WriteByte(0x01);

                            // LEVEL (1 byte)
                            writer.WriteByte((byte)amuletLevel);

                            if (amuletModCount <= 0)
                            {
                                continue;
                            }

                            // FLAG BYTE - MYTHIC ONLY (1 byte)
                            if (isMythicAmulet)
                            {
                                writer.WriteByte(0x00);
                            }

                            // STATUS BYTES - ONE PER MOD SLOT
                            for (int m = 0; m < amuletModCount; m++)
                            {
                                writer.WriteByte(0x00);
                            }

                            // DYNAMIC COUNT - HOW MANY MODIFIERS FOLLOW (1 byte)
                            writer.WriteByte((byte)amuletMods.Count);

                            // MODIFIERS
                            foreach (var mod in amuletMods)
                            {
                                writer.WriteByte(0xFF);
                                writer.WriteCString(mod);
                                writer.WriteByte(0x03);
                                writer.WriteByte(0x15);
                                writer.WriteUInt32(0x11111111);
                            }

                            // DEBUG HEX OUTPUT
                            byte[] amuletBytes = writer.ToArray();
                            int amuletDataLen = writer.Position - itemStart;
                            Debug.LogError($"[OP5-AMULET-HEX] Wrote {amuletDataLen} bytes for {item.GCClass}:");
                            Debug.LogError($"[OP5-AMULET-HEX] {BitConverter.ToString(amuletBytes, itemStart, amuletDataLen)}");

                            continue;
                        }
                        else
                        {
                            bool isMythicRing = gcLower.Contains("mythic");
                            int ringLevel = isMythicRing ? (playerState.Level + 3) : item.GetItemRequiredLevel();
                            int ringModCount;
                            List<string> ringMods;

                            if (isMythicRing)
                            {
                                ringMods = DatabaseLoader.GetRingModifiers(item.GCClass);
                                ringModCount = ringMods.Count;
                            }
                            else if (gcLower.Contains("ringpal.ring"))
                            {
                                ringModCount = 1;
                                ringMods = new List<string>();
                            }
                            else if (gcLower.Contains("questringpal"))
                            {
                                ringModCount = DatabaseLoader.GetRingModSlotCount(item.GCClass);
                                ringMods = new List<string>();
                            }
                            else if (gcLower.Contains("uniqueringpal"))
                            {
                                // CONFIGURABLE - try different values: 0, 1, 2
                                ringModCount = 1;  // <-- CHANGE THIS TO TEST
                                ringMods = new List<string>();
                            }
                            else
                            {
                                ringModCount = 1;
                                ringMods = new List<string>();
                            }

                            Debug.LogError($"[OP5-RING] GCClass: {item.GCClass}, Slot: {equipSlot}, Level: {ringLevel}, ModCount: {ringModCount}, Mythic: {isMythicRing}");

                            // SLOT (4 bytes)
                            writer.WriteUInt32(equipSlot);
                            // X POSITION (1 byte)
                            writer.WriteByte(0x00);
                            // Y POSITION (1 byte)
                            writer.WriteByte(0x00);
                            // COUNT (1 byte)
                            writer.WriteByte(0x01);
                            // LEVEL (1 byte)
                            writer.WriteByte((byte)ringLevel);

                            if (ringModCount <= 0)
                            {
                                continue;
                            }

                            // FLAG BYTE - MYTHIC ONLY (1 byte)
                            if (isMythicRing)
                            {
                                writer.WriteByte(0x00);
                            }

                            // STATUS BYTES - ONE PER MOD SLOT
                            for (int m = 0; m < ringModCount; m++)
                            {
                                writer.WriteByte(0x00);
                            }

                            // DYNAMIC COUNT - HOW MANY MODIFIERS FOLLOW (1 byte)
                            writer.WriteByte((byte)ringMods.Count);

                            // MODIFIERS
                            foreach (var mod in ringMods)
                            {
                                writer.WriteByte(0xFF);
                                writer.WriteCString(mod);
                                writer.WriteByte(0x03);
                                writer.WriteByte(0x15);
                                writer.WriteUInt32(0x11111111);
                            }

                            // DEBUG HEX OUTPUT
                            byte[] ringBytes = writer.ToArray();
                            int ringDataLen = writer.Position - itemStart;
                            Debug.LogError($"[OP5-RING-HEX] Wrote {ringDataLen} bytes for {item.GCClass}:");
                            Debug.LogError($"[OP5-RING-HEX] {BitConverter.ToString(ringBytes, itemStart, ringDataLen)}");

                            continue;
                        }
                    }

                    // ═══ GC LOOKUP — exact modCount from parsed GC files ═══
                    // This replaces the hardcoded if/else tree for any item in the lookup.
                    // OP5 modCount = direct GC mods only (flag byte written separately).
                    int gcLookupModCount = DungeonRunners.Managers.MerchantManager.GetOP5ModCount(item.GCClass);
                    if (gcLookupModCount >= 0)
                    {
                        modCount = gcLookupModCount;
                        Debug.LogError($"[OP5] GC LOOKUP: {item.GCClass} → modCount={modCount}");
                    }
                    else if (isPartialbuilt)
                    {
                        // PARTIALBUILT items - CHECK hasMythicSeasonal FIRST!
                        if (hasMythicSeasonal)
                        {
                            if (gcLower.Contains("fighter"))
                            {
                                if (gcLower.Contains("seasonal002"))
                                {
                                    modCount = 4;
                                    Debug.LogError($"[OP5] PARTIALBUILT MYTHICSEASONAL002 FIGHTER - modCount=4");
                                }
                                else
                                {
                                    modCount = 3;
                                    Debug.LogError($"[OP5] PARTIALBUILT MYTHICSEASONAL001 FIGHTER - modCount=3");
                                }
                            }
                            else if (gcLower.Contains("ranger"))
                            {
                                modCount = 4;
                                Debug.LogError($"[OP5] PARTIALBUILT MYTHICSEASONAL RANGER - modCount=4");
                            }
                            else
                            {
                                modCount = 2;
                                Debug.LogError($"[OP5] PARTIALBUILT MYTHICSEASONAL MAGE - modCount=2");
                            }
                        }
                        else if (hasMythicInName)
                        {
                            if (gcLower.Contains("fighter"))
                            {
                                modCount = 5;
                                Debug.LogError($"[OP5] PARTIALBUILT MYTHIC FIGHTER - modCount=5");
                            }
                            else if (gcLower.Contains("ranger"))
                            {
                                modCount = 2;
                                Debug.LogError($"[OP5] PARTIALBUILT MYTHIC RANGER - modCount=2");
                            }
                            else
                            {
                                modCount = 2;
                                Debug.LogError($"[OP5] PARTIALBUILT MYTHIC MAGE - modCount=2");
                            }
                        }
                        else if (hasJustSeasonal)
                        {
                            if (gcLower.Contains("fighter"))
                            {
                                if (gcLower.Contains("helm"))
                                {
                                    modCount = 3;
                                    Debug.LogError($"[OP5] PARTIALBUILT JUST-SEASONAL FIGHTER HELM - modCount=3");
                                }
                                else
                                {
                                    modCount = 3;
                                    Debug.LogError($"[OP5] PARTIALBUILT JUST-SEASONAL FIGHTER - modCount=3");
                                }
                            }
                            else if (gcLower.Contains("ranger"))
                            {
                                if (gcLower.Contains("helm"))
                                {
                                    modCount = 3;
                                    Debug.LogError($"[OP5] PARTIALBUILT JUST-SEASONAL RANGER HELM - modCount=3");
                                }
                                else
                                {
                                    modCount = 4;
                                    Debug.LogError($"[OP5] PARTIALBUILT JUST-SEASONAL RANGER - modCount=4");
                                }
                            }
                            else if (gcLower.Contains("mage"))
                            {
                                if (gcLower.Contains("helm"))
                                {
                                    modCount = 3;
                                    Debug.LogError($"[OP5] PARTIALBUILT JUST-SEASONAL MAGE HELM - modCount=3");
                                }
                                else
                                {
                                    modCount = 4;
                                    Debug.LogError($"[OP5] PARTIALBUILT JUST-SEASONAL MAGE - modCount=4");
                                }
                            }
                            else
                            {
                                modCount = 4;
                                Debug.LogError($"[OP5] PARTIALBUILT JUST-SEASONAL DEFAULT - modCount=4");
                            }
                        }
                        else if (hasUniqueSeasonal)
                        {
                            if (gcLower.Contains("fighter"))
                            {
                                if (gcLower.Contains("shield"))
                                {
                                    modCount = 4;
                                    Debug.LogError($"[OP5] PARTIALBUILT UNIQUESEASONAL FIGHTER SHIELD - modCount=4");
                                }
                                else
                                {
                                    modCount = 3;
                                    Debug.LogError($"[OP5] PARTIALBUILT UNIQUESEASONAL FIGHTER - modCount=3");
                                }
                            }
                            else if (gcLower.Contains("ranger"))
                            {
                                modCount = 3;
                                Debug.LogError($"[OP5] PARTIALBUILT UNIQUESEASONAL RANGER - modCount=3");
                            }
                            else
                            {
                                modCount = 3;
                                Debug.LogError($"[OP5] PARTIALBUILT UNIQUESEASONAL MAGE - modCount=3");
                            }
                        }
                        else if (hasWishingwell)
                        {
                            if (gcLower.Contains("fighter"))
                            {
                                modCount = 4;
                                Debug.LogError($"[OP5] PARTIALBUILT WISHINGWELL FIGHTER - modCount=4");
                            }
                            else if (gcLower.Contains("ranger"))
                            {
                                modCount = 3;
                                Debug.LogError($"[OP5] PARTIALBUILT WISHINGWELL RANGER - modCount=3");
                            }
                            else if (gcLower.Contains("mage"))
                            {
                                if (gcLower.Contains("helm"))
                                {
                                    modCount = 4;
                                    Debug.LogError($"[OP5] PARTIALBUILT WISHINGWELL MAGE HELM - modCount=4");
                                }
                                else
                                {
                                    modCount = 3;
                                    Debug.LogError($"[OP5] PARTIALBUILT WISHINGWELL MAGE - modCount=3");
                                }
                            }
                            else
                            {
                                modCount = 3;
                                Debug.LogError($"[OP5] PARTIALBUILT WISHINGWELL DEFAULT - modCount=3");
                            }
                        }
                        else
                        {
                            if (gcLower.Contains("fighter"))
                            {
                                modCount = 4;
                                Debug.LogError($"[OP5] PARTIALBUILT UNIQUE FIGHTER - modCount=4");
                            }
                            else if (gcLower.Contains("ranger"))
                            {
                                modCount = 3;
                                Debug.LogError($"[OP5] PARTIALBUILT UNIQUE RANGER - modCount=3");
                            }
                            else
                            {
                                modCount = 3;
                                Debug.LogError($"[OP5] PARTIALBUILT UNIQUE MAGE - modCount=3");
                            }
                        }
                    }
                    else if (isPrebuilt)
                    {
                        // PREBUILT items - CHECK hasMythicInName FIRST!
                        if (hasMythicInName)
                        {
                            if (gcLower.Contains("mythic002"))
                            {
                                if (gcLower.Contains("helm"))
                                {
                                    modCount = 6;
                                    Debug.LogError($"[OP5] PREBUILT MYTHIC002 HELM - modCount=6");
                                }
                                else
                                {
                                    modCount = 5;
                                    Debug.LogError($"[OP5] PREBUILT MYTHIC002 - modCount=5");
                                }
                            }
                            else if (gcLower.Contains("mythic003"))
                            {
                                if (gcLower.Contains("helm"))
                                {
                                    modCount = 6;
                                    Debug.LogError($"[OP5] PREBUILT MYTHIC003 HELM - modCount=6");
                                }
                                else
                                {
                                    modCount = 5;
                                    Debug.LogError($"[OP5] PREBUILT MYTHIC003 - modCount=5");
                                }
                            }
                            else
                            {
                                // MYTHIC001
                                if (gcLower.Contains("fighter"))
                                {
                                    modCount = 5;
                                    Debug.LogError($"[OP5] PREBUILT MYTHIC001 FIGHTER - modCount=5");
                                }
                                else if (gcLower.Contains("ranger"))
                                {
                                    modCount = 6;
                                    Debug.LogError($"[OP5] PREBUILT MYTHIC001 RANGER - modCount=6");
                                }
                                else if (gcLower.Contains("mage"))
                                {
                                    modCount = 5;
                                    Debug.LogError($"[OP5] PREBUILT MYTHIC001 MAGE - modCount=5");
                                }
                                else
                                {
                                    modCount = 5;
                                    Debug.LogError($"[OP5] PREBUILT MYTHIC001 DEFAULT - modCount=5");
                                }
                            }
                        }
                        else if (hasMythicSeasonal)
                        {
                            if (gcLower.Contains("fighter"))
                            {
                                if (gcLower.Contains("helm"))
                                {
                                    modCount = 6;
                                    Debug.LogError($"[OP5] PREBUILT MYTHICSEASONAL FIGHTER HELM - modCount=6");
                                }
                                else if (gcLower.Contains("shield"))
                                {
                                    modCount = 6;
                                    Debug.LogError($"[OP5] PREBUILT MYTHICSEASONAL FIGHTER SHIELD - modCount=6");
                                }
                                else
                                {
                                    modCount = 5;
                                    Debug.LogError($"[OP5] PREBUILT MYTHICSEASONAL FIGHTER - modCount=5");
                                }
                            }
                            else if (gcLower.Contains("ranger"))
                            {
                                if (gcLower.Contains("helm"))
                                {
                                    modCount = 6;
                                    Debug.LogError($"[OP5] PREBUILT MYTHICSEASONAL RANGER HELM - modCount=6");
                                }
                                else if (gcLower.Contains("shield"))
                                {
                                    modCount = 6;
                                    Debug.LogError($"[OP5] PREBUILT MYTHICSEASONAL RANGER SHIELD - modCount=6");
                                }
                                else
                                {
                                    modCount = 5;
                                    Debug.LogError($"[OP5] PREBUILT MYTHICSEASONAL RANGER - modCount=5");
                                }
                            }
                            else if (gcLower.Contains("mage"))
                            {
                                if (gcLower.Contains("helm"))
                                {
                                    modCount = 6;
                                    Debug.LogError($"[OP5] PREBUILT MYTHICSEASONAL MAGE HELM - modCount=6");
                                }
                                else if (gcLower.Contains("shield"))
                                {
                                    modCount = 7;
                                    Debug.LogError($"[OP5] PREBUILT MYTHICSEASONAL MAGE SHIELD - modCount=7");
                                }
                                else
                                {
                                    modCount = 5;
                                    Debug.LogError($"[OP5] PREBUILT MYTHICSEASONAL MAGE - modCount=5");
                                }
                            }
                            else
                            {
                                modCount = 5;
                                Debug.LogError($"[OP5] PREBUILT MYTHICSEASONAL DEFAULT - modCount=5");
                            }
                        }
                        else if (gcLower.Contains("boss"))
                        {
                            if (gcLower.Contains("fighter"))
                            {
                                if (gcLower.Contains("helm"))
                                {
                                    modCount = 6;
                                    Debug.LogError($"[OP5] PREBUILT BOSS FIGHTER HELM - modCount=6");
                                }
                                else
                                {
                                    modCount = 7;
                                    Debug.LogError($"[OP5] PREBUILT BOSS FIGHTER - modCount=7");
                                }
                            }
                            else if (gcLower.Contains("ranger"))
                            {
                                if (gcLower.Contains("helm"))
                                {
                                    modCount = 6;
                                    Debug.LogError($"[OP5] PREBUILT BOSS RANGER HELM - modCount=6");
                                }
                                else
                                {
                                    modCount = 7;
                                    Debug.LogError($"[OP5] PREBUILT BOSS RANGER - modCount=7");
                                }
                            }
                            else if (gcLower.Contains("mage"))
                            {
                                if (gcLower.Contains("shield"))
                                {
                                    modCount = 6;
                                    Debug.LogError($"[OP5] PREBUILT BOSS MAGE SHIELD - modCount=6");
                                }
                                else
                                {
                                    modCount = 7;
                                    Debug.LogError($"[OP5] PREBUILT BOSS MAGE - modCount=7");
                                }
                            }
                            else
                            {
                                modCount = 4;
                                Debug.LogError($"[OP5] PREBUILT BOSS DEFAULT - modCount=4");
                            }
                        }
                        else if (hasWishingwell && !gcLower.Contains("fighter"))
                        {
                            if (gcLower.Contains("helm"))
                            {
                                modCount = 7;
                                Debug.LogError($"[OP5] PREBUILT WISHINGWELL HELM - modCount=7");
                            }
                            else
                            {
                                modCount = 5;
                                Debug.LogError($"[OP5] PREBUILT WISHINGWELL - modCount=5");
                            }
                        }
                        else
                        {
                            modCount = itemData?.modCount ?? 4;
                            Debug.LogError($"[OP5] PREBUILT DEFAULT - modCount={modCount}");
                        }
                    }
                    else if (isGeneratedboss)
                    {
                        // GENERATEDBOSS items - SEPARATE FROM PREBUILT
                        if (gcLower.Contains("fighter"))
                        {
                            if (gcLower.Contains("helm") || gcLower.Contains("shield"))
                            {
                                modCount = 6;
                                Debug.LogError($"[OP5] GENERATEDBOSS FIGHTER HELM/SHIELD - modCount=6");
                            }
                            else
                            {
                                modCount = 7;
                                Debug.LogError($"[OP5] GENERATEDBOSS FIGHTER - modCount=7");
                            }
                        }
                        else if (gcLower.Contains("ranger"))
                        {
                            if (gcLower.Contains("helm") || gcLower.Contains("shield"))
                            {
                                modCount = 6;
                                Debug.LogError($"[OP5] GENERATEDBOSS RANGER HELM/SHIELD - modCount=6");
                            }
                            else
                            {
                                modCount = 7;
                                Debug.LogError($"[OP5] GENERATEDBOSS RANGER - modCount=7");
                            }
                        }
                        else if (gcLower.Contains("mage"))
                        {
                            if (gcLower.Contains("helm") || gcLower.Contains("shield"))
                            {
                                modCount = 2;
                                Debug.LogError($"[OP5] GENERATEDBOSS MAGE HELM/SHIELD - modCount=2");
                            }
                            else
                            {
                                modCount = 2;
                                Debug.LogError($"[OP5] GENERATEDBOSS MAGE - modCount=2");
                            }
                        }
                        else
                        {
                            modCount = 4;
                            Debug.LogError($"[OP5] GENERATEDBOSS DEFAULT - modCount=4");
                        }
                    }
                    else if (isMythic && item.NativeClass == "Armor")
                    {
                        // MYTHIC items (generated, not partialbuilt)
                        modCount = 5;
                        if (gcLower.Contains("chain") && !gcLower.Contains("shield"))
                            modCount = 4;
                        else if (gcLower.Contains("crystal"))
                            modCount = 6;
                        else if (gcLower.Contains("leather"))
                        {
                            if (gcLower.Contains("shield") || gcLower.Contains("gloves") || gcLower.Contains("helm") || gcLower.Contains("armor"))
                                modCount = 6;
                        }
                        else if (gcLower.Contains("scale"))
                            modCount = 6;
                        else if (gcLower.Contains("splint"))
                        {
                            if (gcLower.Contains("100"))
                            {
                                modCount = 1;
                                Debug.LogError($"[OP5] MYTHIC SPLINT 100 - modCount=1");
                            }
                            else if (gcLower.Contains("101"))
                            {
                                modCount = 1;
                                Debug.LogError($"[OP5] MYTHIC SPLINT 101 - modCount=1");
                            }
                            else
                            {
                                modCount = 6;
                            }
                        }
                        else if (gcLower.Contains("plate") && gcLower.Contains("shield"))
                        {
                            if (gcLower.Contains("shield3"))
                            {
                                modCount = 6;
                                Debug.LogError($"[OP5] MYTHIC PLATE SHIELD FX - modCount=6");
                            }
                            else
                            {
                                modCount = 5;
                                Debug.LogError($"[OP5] MYTHIC PLATE SHIELD - modCount=5");
                            }
                        }
                        Debug.LogError($"[OP5] MYTHIC - modCount={modCount}");
                    }
                    else if (isMythic && gcLower.Contains("sword"))
                    {
                        if (gcLower.Contains("mythic6") ||
                             gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                        {
                            modCount = 6;
                            Debug.Log($"[ITEM-SERIALIZE] mythic SWORD FX - modCount=6");
                        }
                        else
                        {
                            modCount = 5;
                            Debug.Log($"[ITEM-SERIALIZE] mythic SWORD - modCount=5");
                        }
                    }
                    else if (isMythic && gcLower.Contains("axe"))
                    {
                        if (gcLower.Contains("mythic6") ||
                             gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                        {
                            modCount = 6;
                            Debug.Log($"[ITEM-SERIALIZE] mythic AXE FX - modCount=6");
                        }
                        else
                        {
                            modCount = 5;
                            Debug.Log($"[ITEM-SERIALIZE] mythic AXE - modCount=5");
                        }
                    }
                    else if (isMythic && gcLower.Contains("mace"))
                    {
                        if (gcLower.Contains("mythic6") ||
                             gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                        {
                            modCount = 6;
                            Debug.Log($"[ITEM-SERIALIZE] mythic MACE FX - modCount=6");
                        }
                        else
                        {
                            modCount = 5;
                            Debug.Log($"[ITEM-SERIALIZE] mythic MACE - modCount=5");
                        }
                    }
                    else if (isMythic && gcLower.Contains("staff"))
                    {
                        if (gcLower.Contains("mythic6") ||
                             gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                        {
                            modCount = 6;
                            Debug.Log($"[ITEM-SERIALIZE] mythic STAFF FX - modCount=6");
                        }
                        else
                        {
                            modCount = 5;
                            Debug.Log($"[ITEM-SERIALIZE] mythic STAFF - modCount=5");
                        }
                    }
                    else if (isMythic && gcLower.Contains("pick"))
                    {
                        if (gcLower.Contains("mythic6") ||
                             gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                        {
                            modCount = 6;
                            Debug.Log($"[ITEM-SERIALIZE] mythic PICK FX - modCount=6");
                        }
                        else
                        {
                            modCount = 5;
                            Debug.Log($"[ITEM-SERIALIZE] mythic PICK - modCount=5");
                        }
                    }
                    else if (isMythic && gcLower.Contains("club"))
                    {
                        if (gcLower.Contains("mythic6") ||
                             gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                        {
                            modCount = 6;
                            Debug.Log($"[ITEM-SERIALIZE] mythic CLUB FX - modCount=6");
                        }
                        else
                        {
                            modCount = 5;
                            Debug.Log($"[ITEM-SERIALIZE] mythic CLUB - modCount=5");
                        }
                    }
                    else if (isMythic && gcLower.Contains("katana"))
                    {
                        if (gcLower.Contains("mythic6") ||
                             gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                        {
                            modCount = 6;
                            Debug.Log($"[ITEM-SERIALIZE] mythic KATANA FX - modCount=6");
                        }
                        else
                        {
                            modCount = 5;
                            Debug.Log($"[ITEM-SERIALIZE] mythic KATANA - modCount=5");
                        }
                    }
                    else if (isMythic && gcLower.Contains("polearm"))
                    {
                        if (gcLower.Contains("mythic6") ||
                             gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                        {
                            modCount = 6;
                            Debug.Log($"[ITEM-SERIALIZE] mythic POLEARM FX - modCount=6");
                        }
                        else
                        {
                            modCount = 5;
                            Debug.Log($"[ITEM-SERIALIZE] mythic POLEARM - modCount=5");
                        }
                    }
                    else if (isMythic && gcLower.Contains("crossbow"))
                    {
                        if (gcLower.Contains("mythic6") ||
                             gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                        {
                            modCount = 6;
                            Debug.Log($"[ITEM-SERIALIZE] mythic CROSSBOW FX - modCount=6");
                        }
                        else
                        {
                            modCount = 5;
                            Debug.Log($"[ITEM-SERIALIZE] mythic CROSSBOW - modCount=5");
                        }
                    }
                    else if (isMythic && gcLower.Contains("bow"))
                    {
                        if (gcLower.Contains("mythic6") ||
                             gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                        {
                            modCount = 6;
                            Debug.Log($"[ITEM-SERIALIZE] mythic BOW FX - modCount=6");
                        }
                        else
                        {
                            modCount = 5;
                            Debug.Log($"[ITEM-SERIALIZE] mythic BOW - modCount=5");
                        }
                    }
                    else if (isMythic && gcLower.Contains("weapon"))
                    {
                        if (gcLower.Contains("mythic6") ||
                             gcLower.Contains("mythic7") || gcLower.Contains("mythic8"))
                        {
                            modCount = 6;
                            Debug.Log($"[ITEM-SERIALIZE] mythic WEAPON FX - modCount=6");
                        }
                        else
                        {
                            modCount = 5;
                            Debug.Log($"[ITEM-SERIALIZE] mythic WEAPON - modCount=5");
                        }
                    }
                    else
                    {
                        // REGULAR items
                        modCount = itemData?.modCount ?? 3;
                        if (gcLower.Contains("chain") && !gcLower.Contains("shield"))
                            modCount = 1;
                        Debug.LogError($"[OP5] REGULAR - modCount={modCount}");
                    }

                    Debug.LogError($"[OP5-ITEM] ModCount: {modCount} (isMythic={isMythic}, isGeneratedboss={isGeneratedboss}, dbModCount={itemData?.modCount})");

                    int equipDataStart = writer.Position;

                    writer.WriteUInt32(equipSlot);
                    writer.WriteByte(0);
                    writer.WriteByte(0);
                    writer.WriteByte(1);

                    int itemLevel = isMythic ? (playerState.Level + 3) : ItemData.GetRequiredLevelFromGCClass(item.GCClass);
                    writer.WriteByte((byte)itemLevel);
                    Debug.LogError($"[OP5-ITEM] Item level: {itemLevel} (isMythic={isMythic})");

                    //------------------------------------------
                    // FLAG BYTE LOGIC (SEPARATE)
                    //------------------------------------------
                    bool writeFlag = false;

                    // Mythic items (not partialbuilt) get flag
                    if (isMythic && !isPartialbuilt)
                    {
                        writeFlag = true;
                    }
                    // Partialbuilt MythicSeasonal - Fighter, Ranger, AND Mage all get flag
                    else if (isPartialbuilt && hasMythicSeasonal)
                    {
                        writeFlag = true;
                    }
                    // Partialbuilt UniqueSeasonal mage/ranger gets flag (NOT Fighter)
                    else if (isPartialbuilt && hasUniqueSeasonal && !gcLower.Contains("fighter"))
                    {
                        writeFlag = true;
                    }
                    // Prebuilt Mythic gets flag
                    else if (isPrebuilt && hasMythicInName)
                    {
                        writeFlag = true;
                    }
                    // Prebuilt MythicSeasonal gets flag
                    else if (isPrebuilt && hasMythicSeasonal)
                    {
                        writeFlag = true;
                    }
                    // Prebuilt wishingwell mage/ranger gets flag (NOT helm)
                    else if (isPrebuilt && hasWishingwell && !gcLower.Contains("fighter") && !gcLower.Contains("helm"))
                    {
                        writeFlag = true;
                    }
                    // NOTE: isGeneratedboss does NOT get flag byte!

                    // Fallback: NON-MythicPAL items in lookup table always get flag byte (GetOP5ModCount assumes it)
                    if (!writeFlag && isMythic && !gcLower.Contains("mythicpal"))
                    {
                        writeFlag = true;
                    }

                    // Scale/splint exception (but NOT for 100/101 tier)
                    if (writeFlag)
                    {
                        if ((gcLower.Contains("scale") && !gcLower.Contains("helm") && !gcLower.Contains("armor") && !gcLower.Contains("100") && !gcLower.Contains("101")) ||
                            (gcLower.Contains("splint") && !gcLower.Contains("shoulder") && !gcLower.Contains("armor") && !gcLower.Contains("100") && !gcLower.Contains("101")))
                        {
                            Debug.LogError($"[OP5-ITEM] SKIPPED flag for scale/splint");
                            writeFlag = false;
                            // Compensate: GetOP5ModCount assumed flag byte exists, add 1 back
                            if (gcLookupModCount >= 0) modCount++;
                        }
                    }

                    bool isWeapon = (item.NativeClass == "MeleeWeapon" || item.NativeClass == "RangedWeapon");
                    if (writeFlag && (!isWeapon || isMythic))
                    {
                        writer.WriteByte(0x00);
                        Debug.LogError($"[EQUIP-WRITEINIT] Wrote FLAG byte (armor only)");
                    }

                    Debug.LogError($"[OP5-ITEM] Basic data written (slot, position, count, level, flags)");

                    int modSlotsStart = writer.Position;
                    for (int i = 0; i < modCount; i++)
                    {
                        writer.WriteByte(0x00);
                    }
                    Debug.LogError($"[OP5-MODS] Wrote {modCount} mod slot bytes (0x00 each) at position {modSlotsStart}");

                    // ScaleMod — derive from item's actual rarity (tier suffix)
                    // Normal items (Binder.Mod1) have no attribute children, write count=0
                    // Superior+ mods extend EnhancementsPAL with children, need full trailer
                    // GC lookup items (mythic/special) always get 0x00 — mods are baked into GC class
                    if (gcLookupModCount >= 0)
                    {
                        writer.WriteByte(0x00);  // no ScaleMod children for GC lookup items
                        Debug.LogError($"[OP5-MODS] GC lookup item - no ScaleMod (mods baked in GC class)");
                    }
                    else
                    {
                        // Iteration-15 GetEffectiveRarity fallback: GetTierFromGcType returns
                        // Normal for named-rarity items (`PlatePAL.PlateUniqueArmor5` etc.)
                        // because they lack a `-N` dash-suffix. Falling back here lets the
                        // OP5 equipment write emit a Unique ScaleMod block so the client
                        // doesn't see the stream short by 7 bytes → tag-108 (0x6C=='l')
                        // comm error on the next item's cstring read.
                        int op5Tier = RarityHelper.GetTierFromGcType(item.GCClass);
                        var op5Rarity = RarityHelper.GetRarityFromTier(op5Tier);
                        if (op5Rarity == ItemRarity.Normal)
                        {
                            int effective = item.GetEffectiveRarity();
                            if (effective > 0 && effective < 5)
                                op5Rarity = (ItemRarity)effective;
                        }
                        // Path B — try wire-mod injection first (matches WriteInit / WriteInitForInventory /
                        // INV-RESTORE). OP5 is the ONLY equipment write path that fires on zone-in
                        // sequencing, and was missed when Path B was wired everywhere else — that's
                        // why equipped items kept rotating mods per zone-switch while inventory items
                        // stayed stable. Wire bytes are identical across emissions now.
                        var op5WireMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetItemWireMods(item.GCClass);
                        if (op5WireMods.Count == 0)
                            op5WireMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetWrapperIGWireMods(item.GCClass, op5Rarity.ToString());

                        if (DungeonRunners.Data.ItemStatDatabase.PathBEnabled && op5WireMods.Count > 0)
                        {
                            var op5Hashes = new List<uint>();
                            var op5Emitted = new List<string>();
                            var op5Skipped = new List<string>();
                            foreach (var (slot, modRef) in op5WireMods.OrderBy(w => w.Slot))
                            {
                                uint h = DungeonRunners.Data.ItemStatDatabase.Instance.GetGCClassHash(modRef);
                                if (h != 0) { op5Hashes.Add(h); op5Emitted.Add($"{modRef}#0x{h:X8}"); }
                                else op5Skipped.Add(modRef);
                            }
                            writer.WriteByte((byte)op5Hashes.Count);
                            foreach (uint h in op5Hashes)
                            {
                                writer.WriteByte(0x04);
                                writer.WriteUInt32(h);
                                writer.WriteByte(0x00);
                            }
                            Debug.LogError($"[IG-INJECT] op5-armor {item.GCClass} rarity={op5Rarity} storedRarity={item.StoredRarity} effective={item.GetEffectiveRarity()} mods={op5Hashes.Count}/{op5WireMods.Count} phase1ModCount={modCount} emitted=[{string.Join(" | ", op5Emitted)}] skipped=[{string.Join(",", op5Skipped)}]");
                        }
                        else if (op5Rarity == ItemRarity.Normal)
                        {
                            writer.WriteByte(0x00);  // no ScaleMod children for Normal
                            Debug.LogError($"[OP5-MODS] Normal item - no ScaleMod");
                        }
                        else
                        {
                            writer.WriteByte(0x01);
                            int itemModStart = writer.Position;
                            writer.WriteByte(0xFF);
                            // Deterministic per (gcClass, rarity) — replaces GetRandomScaleMod which
                            // re-rolled every zone-switch. Same input → same scaleMod across emissions
                            // → stable equipped tooltip mods.
                            string modifierClass = RarityHelper.GetDeterministicScaleMod(item.GCClass, op5Rarity);
                            Debug.LogError($"[OP5-MODS] Item '{item.GCClass}' -> rarity={op5Rarity} Modifier '{modifierClass}' (deterministic)");
                            writer.WriteCString(modifierClass);
                            writer.WriteByte(0x03);
                            writer.WriteByte(0x15);
                            writer.WriteUInt32(0x11111111);
                            int itemModBytes = writer.Position - itemModStart;
                            Debug.LogError($"[OP5-MODS] ItemModifier written ({itemModBytes} bytes)");
                        }
                    }

                    int itemBytes = writer.Position - itemStart;
                    Debug.LogError($"[OP5-ITEM] ✅ Complete: '{item.GCClass}'");
                    Debug.LogError($"[OP5-ITEM]    Slot: {equipSlot}");
                    Debug.LogError($"[OP5-ITEM]    ModCount: {modCount}");
                    Debug.LogError($"[OP5-ITEM]    Total Bytes: {itemBytes}");
                    if (VerbosePacketLogging) Debug.LogError($"[OP5-ITEM]    Full hex: {BitConverter.ToString(writer.ToArray(), itemStart, itemBytes)}");
                }

                opBytes = (int)(writer.Position - beforeOp);
                Debug.LogError($"[OP5-COMPLETE] Equipment component complete ({opBytes} bytes, {itemCount} items equipped)");

                if (VerbosePacketLogging)
                {
                    Debug.LogError($"");
                    Debug.LogError($"[OP5-HEXDUMP] ═══════════════════════════════════════════════════════════");
                    Debug.LogError($"[OP5-HEXDUMP] OPERATION 5 COMPLETE HEX DUMP:");
                    StringBuilder op5Hex = new StringBuilder();
                    for (int i = beforeOp; i < writer.Position; i += 16)
                    {
                        op5Hex.Append($"{i:X8}  ");
                        for (int j = 0; j < 16; j++)
                        {
                            if (i + j < writer.Position)
                                op5Hex.Append($"{writer.ToArray()[i + j]:X2} ");
                            else
                                op5Hex.Append("   ");
                        }
                        op5Hex.Append(" |");
                        for (int j = 0; j < 16 && i + j < writer.Position; j++)
                        {
                            byte b = writer.ToArray()[i + j];
                            op5Hex.Append((b >= 32 && b < 127) ? (char)b : '.');
                        }
                        op5Hex.AppendLine("|");
                    }
                    Debug.LogError(op5Hex.ToString());
                    Debug.LogError($"[OP5-HEXDUMP] ═══════════════════════════════════════════════════════════");
                }
                Debug.LogError($"=== OPERATION 5 COMPLETE ===");
                Debug.LogError($"[OP5-COMPLETE] Equipment component complete ({opBytes} bytes, {itemCount} items equipped)");
                Debug.LogError($"[CUMULATIVE-OP5] Total after Op5: {writer.Position} bytes");
                // ╔═══════════════════════════════════════════════════════════════════════════════╗
                // ║ OPERATION 6: QUESTMANAGER                                                     ║
                // ╚═══════════════════════════════════════════════════════════════════════════════╝
                Debug.LogError("=== OPERATION 6: QUESTMANAGER COMPONENT ===");
                beforeOp = writer.Position;

                var questManager = player.Children.FirstOrDefault(c => c.GCClass == "QuestManager");
                if (questManager == null)
                {
                    Debug.LogError("🔧 QuestManager not found in player children - CREATING IT!");
                    questManager = new GCObject
                    {
                        GCClass = "QuestManager",
                        NativeClass = "QuestManager",
                        Name = "QuestManager",
                        Id = _nextEntityId++
                    };
                    player.AddChild(questManager);
                    Debug.LogError("✅ QuestManager created and added to Player children");
                }

                conn.QuestManagerId = (ushort)questManager.Id;
                Debug.LogError($"✓ QuestManager ID: {questManager.Id}");

                // Delegate to QuestManager for packet building
                QuestManager.Instance.WriteQuestManagerComponent(
                    writer,
                    conn.ConnId.ToString(),
                    (ushort)player.Id,
                    (ushort)questManager.Id,
                    (w, gcType, preserveCase) => WriteGCType(w, gcType, preserveCase),
                    conn
                );

                opBytes = (int)(writer.Position - beforeOp);
                Debug.LogError($"✓ QuestManager component complete ({opBytes} bytes)");

                if (VerbosePacketLogging)
                {
                    Debug.LogError($"[OP6-HEXDUMP] ═══════════════════════════════════════");
                    StringBuilder opHex = new StringBuilder();
                    for (int i = beforeOp; i < writer.Position; i += 16)
                    {
                        opHex.Append($"{i:X8}  ");
                        for (int j = 0; j < 16; j++)
                        {
                            if (i + j < writer.Position)
                                opHex.Append($"{writer.ToArray()[i + j]:X2} ");
                            else
                                opHex.Append("   ");
                        }
                        opHex.Append(" |");
                        for (int j = 0; j < 16 && i + j < writer.Position; j++)
                        {
                            byte b = writer.ToArray()[i + j];
                            opHex.Append((b >= 32 && b < 127) ? (char)b : '.');
                        }
                        opHex.AppendLine("|");
                    }
                    Debug.LogError(opHex.ToString());
                    Debug.LogError($"[OP6-HEXDUMP] ═══════════════════════════════════════");
                }
                Debug.LogError($"[CUMULATIVE-OP6] Total after Op6: {writer.Position} bytes");


                // ╔═══════════════════════════════════════════════════════════════════════════════╗
                // ║ OPERATION 7: DIALOGMANAGER                                                    ║
                // ╚═══════════════════════════════════════════════════════════════════════════════╝
                Debug.LogError("=== OPERATION 7: DIALOGMANAGER COMPONENT ===");
                beforeOp = writer.Position;

                var dialogManager = player.Children.FirstOrDefault(c => c.GCClass == "DialogManager");
                if (dialogManager == null)
                {
                    Debug.LogError("🔧 DialogManager not found in player children - CREATING IT!");
                    dialogManager = new GCObject();
                    dialogManager.GCClass = "DialogManager";
                    dialogManager.NativeClass = "DialogManager";
                    dialogManager.Name = "DialogManager";
                    dialogManager.Id = _nextEntityId++;
                    player.AddChild(dialogManager);
                    Debug.LogError("✅ DialogManager created and added to Player children");
                }

                Debug.LogError($"✓ DialogManager ID: {dialogManager.Id}");

                writer.WriteByte(0x32);
                writer.WriteUInt16((ushort)player.Id);
                writer.WriteUInt16((ushort)dialogManager.Id);
                WriteGCType(writer, "DialogManager");
                writer.WriteByte(0x01);

                opBytes = (int)(writer.Position - beforeOp);
                Debug.LogError($"✅ DialogManager component complete ({opBytes} bytes, no data)");



                // 🔥 Track DialogManager ID for NPC interactions!
                conn.DialogManagerId = (ushort)dialogManager.Id;
                Debug.LogError($"[OP7] DialogManager tracked: ID={dialogManager.Id}");

                opBytes = (int)(writer.Position - beforeOp);
                Debug.LogError($"✅ DialogManager component complete ({opBytes} bytes, no data)");


                if (VerbosePacketLogging)
                {
                    Debug.LogError($"[OPXX-HEXDUMP] ═══════════════════════════════════════");
                    Debug.LogError($"[OPXX-HEXDUMP] OPERATION XX COMPLETE HEX DUMP:");
                    StringBuilder opHex7 = new StringBuilder();
                    for (int i = beforeOp; i < writer.Position; i += 16)
                    {
                        opHex7.Append($"{i:X8}  ");
                        for (int j = 0; j < 16; j++)
                        {
                            if (i + j < writer.Position)
                                opHex7.Append($"{writer.ToArray()[i + j]:X2} ");
                            else
                                opHex7.Append("   ");
                        }
                        opHex7.Append(" |");
                        for (int j = 0; j < 16 && i + j < writer.Position; j++)
                        {
                            byte b = writer.ToArray()[i + j];
                            opHex7.Append((b >= 32 && b < 127) ? (char)b : '.');
                        }
                        opHex7.AppendLine("|");
                    }
                    Debug.LogError(opHex7.ToString());
                    Debug.LogError($"[OPXX-HEXDUMP] ═══════════════════════════════════════");
                }
                Debug.LogError($"✅ DialogManager component complete (no data)");
                Debug.LogError($"[CUMULATIVE-OP7] Total after Op7: {writer.Position} bytes");


                // ╔═══════════════════════════════════════════════════════════════════════════════╗
                // ║ OPERATION 8: UNITCONTAINER                                                    ║
                // ╚═══════════════════════════════════════════════════════════════════════════════╝
                Debug.LogError("=== OPERATION 8: UNITCONTAINER COMPONENT ===");
                beforeOp = writer.Position;

                var unitContainer = avatar.Children.FirstOrDefault(c => c.GCClass == "UnitContainer");
                if (unitContainer == null)
                {
                    Debug.LogError("❌ UnitContainer not found in avatar children!");
                    return;
                }
                Debug.LogError($"✓ UnitContainer ID: {unitContainer.Id}");
                // Track UnitContainer component ID for proper routing
                TrackComponent(conn.ConnId.ToString(), (ushort)unitContainer.Id, "UnitContainer");
                conn.UnitContainerId = (ushort)unitContainer.Id;  // ← ADD THIS LINE
                writer.WriteByte(0x32);
                writer.WriteUInt16((ushort)avatar.Id);
                writer.WriteUInt16((ushort)unitContainer.Id);
                WriteGCType(writer, "UnitContainer");
                writer.WriteByte(0x01);

                //  writer.WriteUInt32(1);
                // writer.WriteUInt32(1);
                writer.WriteUInt32(0);
                writer.WriteUInt32(savedChar.gold);

                Debug.LogError($"[UNITCONTAINER] 💰 Writing player gold: {savedChar.gold}");

                var mainInventory = unitContainer.Children.FirstOrDefault(c => c.GCClass == "avatar.base.Inventory");
                var bankPage1 = unitContainer.Children.FirstOrDefault(c => c.GCClass == "avatar.base.Bank");
                var bankPage2 = unitContainer.Children.FirstOrDefault(c => c.GCClass == "avatar.base.Bank2");
                var bankPage3 = unitContainer.Children.FirstOrDefault(c => c.GCClass == "avatar.base.Bank3");
                var bankPage4 = unitContainer.Children.FirstOrDefault(c => c.GCClass == "avatar.base.Bank4");
                var bankPage5 = unitContainer.Children.FirstOrDefault(c => c.GCClass == "avatar.base.Bank5");
                var bankPage6 = unitContainer.Children.FirstOrDefault(c => c.GCClass == "avatar.base.Bank6");
                var bankPage7 = unitContainer.Children.FirstOrDefault(c => c.GCClass == "avatar.base.Bank7");
                var tradeInventory = unitContainer.Children.FirstOrDefault(c => c.GCClass == "avatar.base.TradeInventory");

                if (mainInventory == null || bankPage1 == null || tradeInventory == null
                    || bankPage2 == null || bankPage3 == null || bankPage4 == null
                    || bankPage5 == null || bankPage6 == null || bankPage7 == null)
                {
                    Debug.LogError("❌ Missing required inventories!");
                    return;
                }

                // Container IDs: TradeInventory stays at 0x0D (preserves trade compat).
                // Bank pages take 0x0C (page 1, original) then 0x0E..0x13 (pages 2..7).
                var inventoriesToWrite = new[] {
            (inventory: mainInventory, id: (byte)0x0B, name: "Inventory"),
            (inventory: bankPage1, id: (byte)0x0C, name: "Bank"),
            (inventory: tradeInventory, id: (byte)0x0D, name: "TradeInventory"),
            (inventory: bankPage2, id: (byte)0x0E, name: "Bank2"),
            (inventory: bankPage3, id: (byte)0x0F, name: "Bank3"),
            (inventory: bankPage4, id: (byte)0x10, name: "Bank4"),
            (inventory: bankPage5, id: (byte)0x11, name: "Bank5"),
            (inventory: bankPage6, id: (byte)0x12, name: "Bank6"),
            (inventory: bankPage7, id: (byte)0x13, name: "Bank7")
        };

                // Inventory count byte — must match the array length.
                writer.WriteByte((byte)inventoriesToWrite.Length);

                // Clear stale tracking from previous zone — for ALL containers, not just main inv.
                // Done once before the loop; slot counter is per-player so it gets reset too.
                {
                    string clearConnId = conn.ConnId.ToString();
                    byte[] allContainers = { 0x0B, 0x0C, 0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13 };
                    foreach (byte cid in allContainers)
                    {
                        string key = InvKey(clearConnId, cid);
                        if (_playerInventoryItems.ContainsKey(key))
                            _playerInventoryItems[key].Clear();
                        if (_inventoryStackCounts.ContainsKey(key))
                            _inventoryStackCounts[key].Clear();
                        if (_occupiedInventorySlots.ContainsKey(key))
                            _occupiedInventorySlots[key].Clear();
                    }
                    if (_inventorySlotCounters.ContainsKey(clearConnId))
                        _inventorySlotCounters.Remove(clearConnId);

                    // Restore buy prices once across all containers.
                    if (savedChar.inventory != null)
                    {
                        foreach (var bpItem in savedChar.inventory)
                        {
                            if (bpItem.buyPrice > 0)
                                DungeonRunners.Managers.MerchantManager.SetBuyPrice(clearConnId, bpItem.gcClass, bpItem.buyPrice);
                        }
                    }
                }

                // Slot indices must be globally unique across containers so HandlePickup's
                // single-index lookup (via FindContainerForSlot) finds the right item.
                uint globalItemIndex = 1;

                foreach (var inv in inventoriesToWrite)
                {
                    Debug.LogError($"    • Writing {inv.name}");
                    WriteGCType(writer, inv.inventory.GCClass);
                    writer.WriteByte(inv.id);
                    writer.WriteByte(0x01);

                    // Filter items belonging to this container. Trade (0x0D) is never persisted.
                    bool isPersistedContainer = (inv.id == 0x0B || inv.id == 0x0C || (inv.id >= 0x0E && inv.id <= 0x13));
                    var containerItems = (isPersistedContainer && savedChar.inventory != null)
                        ? savedChar.inventory.FindAll(i => i.containerId == inv.id)
                        : new List<SavedInventoryItem>();

                    if (containerItems.Count > 0)
                    {
                        string clearConnId = conn.ConnId.ToString();
                        writer.WriteByte((byte)containerItems.Count);
                        Debug.LogError($"      → GCType: {inv.inventory.GCClass}, ID: 0x{inv.id:X2}, Items: {containerItems.Count}");

                        foreach (var item in containerItems)
                        {
                            uint itemIndex = globalItemIndex++;
                            string gcTypeToSend = item.gcClass.ToLowerInvariant();
                            // Compute prefixed gc class via single source of truth in GCObject
                            string packetGcType = GCObject.GetPacketGCClassFor(item.gcClass);
                            var itemData = DatabaseLoader.FindItem(gcTypeToSend);
                            bool isInvChain = gcTypeToSend.Contains("chain") && !gcTypeToSend.Contains("shield");
                            bool isInvAmulet = gcTypeToSend.Contains("amulet");
                            bool isInvRing = gcTypeToSend.Contains("ring");
                            int modSlots;
                            if (isInvChain)
                                modSlots = 1;  // Chain: ScaleMod slot only, no SpeedM
                            else if (isInvAmulet || isInvRing)
                                // Empirical 2026-05-20: non-mythic ring/amulet desync —
                                // Amulets aren't in `armor` or `weapons` SQLite tables, so
                                // `FindItem` returns null → fallback `modSlots = 2` writes
                                // 2 zero bytes. But BaseAmulet/BaseRing have ZERO inherited
                                // ItemModifier children — Phase-1 reads 0 bytes — so only
                                // 1 byte is needed (the FLAGS byte for Item::readData).
                                // The extra zero gets consumed as Phase 2 count, ScaleMod
                                // block becomes orphan bytes, cascade lands on 'S'=0x53
                                // from a later `ScaleModPAL.*` cstring → tag-83 fatal.
                                // Repro'd by Kubjas with [INV-RESTORE] hex showing 2 zeros
                                // before ScaleMod count for AmuletPAL.AmuletUnique* items.
                                modSlots = 1;
                            else if (itemData != null)
                                modSlots = itemData.modCount;
                            else
                                modSlots = 2;  // Other armor: ScaleMod + SpeedM
                            // Use stored level from DB if available (fixed at creation time)
                            // Fallback: mythic items use player level, regular items use PAL tier
                            int itemLevel;
                            if (item.storedLevel >= 0)
                            {
                                itemLevel = item.storedLevel;
                            }
                            else
                            {
                                bool isInvMythic = gcTypeToSend.IndexOf("mythicpal", StringComparison.OrdinalIgnoreCase) >= 0
                                    || DungeonRunners.Managers.MerchantManager.HasAuthoredMerchantModSlots(
                                        gcTypeToSend.StartsWith("items.pal.") ? gcTypeToSend.Substring(10) : gcTypeToSend);
                                itemLevel = isInvMythic ? (playerState.Level + 3) : ItemData.GetRequiredLevelFromGCClass(gcTypeToSend);
                            }

                            bool isSimpleItem = gcTypeToSend.Contains("questitem")
                                             || gcTypeToSend.Contains("potion")
                                             || gcTypeToSend.Contains("consumable")
                                             || gcTypeToSend.Contains("townportal")
                                             || gcTypeToSend.Contains("scroll")
                                             || gcTypeToSend.Contains("skillbook")
                                             || gcTypeToSend.Contains("voucher");

                            int invItemStart = writer.Position;
                            writer.WriteByte(0xFF);
                            writer.WriteCString(packetGcType);
                            writer.WriteUInt32(itemIndex);
                            writer.WriteByte(item.x);
                            writer.WriteByte(item.y);
                            writer.WriteByte((byte)(item.count > 0 ? item.count : 1));  // quantity from save
                            writer.WriteByte((byte)itemLevel);

                            if (isSimpleItem)
                            {
                                // Quest items, potions, scrolls, consumables have NO ItemModifier
                                // archetype children. Writing a ScaleMod here would desync the parser.
                                // Format: flags(0) + ReadChildData<ItemModifier> count(0)
                                writer.WriteByte(0x00);  // flags
                                // Binary RE: 0x583920 walks transient children before count byte.
                                // DragonJuice/IntBuff have transient Mod1 (ItemAttributeModifier)
                                // whose readData@0x588AE0 reads 1 byte (flags).
                                if (gcTypeToSend.Contains("dragonjuice") || gcTypeToSend.Contains("intbuff"))
                                    writer.WriteByte(0x00);  // transient Mod1 flags
                                writer.WriteByte(0x00);  // ReadChildData<ItemModifier> Phase 2 count = 0
                                Debug.LogError($"        → Simple item {itemIndex}: {gcTypeToSend} (no ScaleMod)");
                            }
                            else if ((gcTypeToSend.Contains("ring") || gcTypeToSend.Contains("amulet")) && gcTypeToSend.Contains("mythic"))
                            {
                                // Mythic jewelry — not covered by the legacy special weapon/armor
                                // table path. Mirror the OPXX armor-mythic shape:
                                // write 1 inherited Phase-1 placeholder + N placeholders
                                // for the .gc-defined Mod1..N children + Phase-2 count=0
                                // (mods baked in GC class, identical to how `WriteItem`'s
                                // hasModChildren branch handles non-IG-stub mythics at
                                // MerchantManager.cs:2660). Writing Phase-2 hashes here —
                                // even though `WriteInitForInventory`'s mythic-ring branch
                                // does — double-stacks the same mods in the multi-item
                                // OPXX packet → desync → tag 83 (`'S'` from a later
                                // ScaleModPAL cstring). Repro'd by Kubjas 2026-05-20 with
                                // RingMythic12 + AmuletMythic7 in inventory.
                                bool isAmuletJ = gcTypeToSend.Contains("amulet");
                                List<string> jMods = isAmuletJ
                                    ? DatabaseLoader.GetAmuletModifiers(item.gcClass)
                                    : DatabaseLoader.GetRingModifiers(item.gcClass);
                                int jModCount = jMods.Count;
                                writer.WriteByte(0x00);  // inherited Phase-1 placeholder
                                for (int m = 0; m < jModCount; m++) writer.WriteByte(0x00);
                                writer.WriteByte(0x00);  // Phase-2 count = 0 (mods baked in GC)
                                Debug.LogError($"        → Mythic jewelry {itemIndex}: {gcTypeToSend} placeholders={jModCount + 1} phase2=0");
                            }
                            else
                            {
                                // ═══ GC LOOKUP for mythic items ═══
                                // This is the Container → Item::readData path (same as merchant)
                                // so we use authored merchant slots directly (includes flag byte)
                                string lookupKey = gcTypeToSend;
                                if (lookupKey.StartsWith("items.pal.")) lookupKey = lookupKey.Substring(10);
                                int invMerchantSlots;
                                if (DungeonRunners.Managers.MerchantManager.TryGetAuthoredMerchantModSlots(lookupKey, out invMerchantSlots))
                                {
                                    for (int m = 0; m < invMerchantSlots; m++)
                                    {
                                        writer.WriteByte(0x00);
                                    }
                                    writer.WriteByte(0x00);  // no ScaleMod children (mods baked in GC)
                                    Debug.LogError($"        → GC LOOKUP {itemIndex}: {gcTypeToSend} modSlots={invMerchantSlots} (no ScaleMod)");
                                }
                                else
                                {
                                    for (int m = 0; m < modSlots; m++)
                                    {
                                        writer.WriteByte(0x00);
                                    }
                                    // OP8 inventory write — same GetEffectiveRarity-style
                                    // fallback as OP5 / drop / WriteInitForInventory. Without
                                    // this, named-Unique items (rarity=4 in DB but no `-N`
                                    // suffix) write zero ScaleMod bytes, leaving the stream
                                    // short → next item's gcType cstring is read as type tags
                                    // → comm error "type tag 108" (0x6C='l' from leather/plate
                                    // gcType chars). Reproduced on login by Kubjas 2026-05-19.
                                    int tier = RarityHelper.GetTierFromGcType(item.gcClass);
                                    var itemRarity = RarityHelper.GetRarityFromTier(tier);
                                    if (itemRarity == ItemRarity.Normal)
                                    {
                                        // Use the DB-stored rarity column if it indicates a
                                        // higher tier (was set at drop-spawn time by the
                                        // reward branch).
                                        if (item.rarity > 0 && item.rarity < 5)
                                            itemRarity = (ItemRarity)item.rarity;
                                        // Else try name-pattern detection.
                                        else
                                        {
                                            int detected = DungeonRunners.Data.GCObject.DetectRarityFromGCClass(item.gcClass);
                                            if (detected > 0 && detected < 5)
                                                itemRarity = (ItemRarity)detected;
                                        }
                                    }

                                    // Path B — wire-mod injection (direct + wrapper). Mirrors
                                    // the WriteInitForInventory/Equip/Drop paths so relog
                                    // persistence preserves the per-tier visible-bonus count.
                                    var restoreWireMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetItemWireMods(item.gcClass);
                                    if (restoreWireMods.Count == 0)
                                        restoreWireMods = DungeonRunners.Data.ItemStatDatabase.Instance.GetWrapperIGWireMods(item.gcClass, itemRarity.ToString());

                                    if (DungeonRunners.Data.ItemStatDatabase.PathBEnabled && restoreWireMods.Count > 0)
                                    {
                                        var hashes = new List<uint>();
                                        foreach (var (slot, modRef) in restoreWireMods.OrderBy(w => w.Slot))
                                        {
                                            uint h = DungeonRunners.Data.ItemStatDatabase.Instance.GetGCClassHash(modRef);
                                            if (h != 0) hashes.Add(h);
                                        }
                                        writer.WriteByte((byte)hashes.Count);
                                        foreach (uint h in hashes)
                                        {
                                            writer.WriteByte(0x04);
                                            writer.WriteUInt32(h);
                                            writer.WriteByte(0x00);
                                        }
                                        Debug.LogError($"        → IG-INJECT {itemIndex}: {gcTypeToSend} rarity={itemRarity} mods={hashes.Count} phase1ModSlots={modSlots}");
                                    }
                                    else if (itemRarity == ItemRarity.Normal)
                                    {
                                        writer.WriteByte(0x00);
                                        Debug.LogError($"        → Equipment {itemIndex}: {gcTypeToSend} rarity=Normal (no ScaleMod)");
                                    }
                                    else
                                    {
                                        // Deterministic pick keyed by gcClass — fixes "mods change on
                                        // every zone-switch / relog" for items not covered by Path B
                                        // wire-mods (e.g. dash-suffix items with no wrapper IG, or
                                        // synthesized items like PlateUniqueHelm5).
                                        string scaleMod = RarityHelper.GetDeterministicScaleMod(item.gcClass, itemRarity);
                                        writer.WriteByte(0x01);
                                        writer.WriteByte(0xFF);
                                        writer.WriteCString(scaleMod);
                                        writer.WriteByte(0x03);
                                        writer.WriteByte(0x15);
                                        writer.WriteUInt32(0x11111111);
                                        Debug.LogError($"        → Equipment {itemIndex}: {gcTypeToSend} rarity={itemRarity} scaleMod={scaleMod} (deterministic)");
                                    }
                                }
                            }

                            // Per-item hex dump — every byte emitted for this item including
                            // the 0xFF prefix, cstring, slot, x/y/count/level, placeholders,
                            // and Phase-2 payload. Use this to bisect which item desyncs in
                            // the OPXX restore packet (the client crash log gives you the
                            // failing readType tag, but not the offset).
                            {
                                int invItemLen = writer.Position - invItemStart;
                                var invBuf = writer.ToArray();
                                var invHexSb = new System.Text.StringBuilder(invItemLen * 3);
                                for (int hi = 0; hi < invItemLen; hi++)
                                {
                                    if (hi > 0) invHexSb.Append(' ');
                                    invHexSb.Append(invBuf[invItemStart + hi].ToString("X2"));
                                }
                                Debug.LogError($"[INV-RESTORE] item#{itemIndex} gc={item.gcClass} rar={item.rarity} lv={item.storedLevel} bytes={invItemLen} hex={invHexSb}");
                            }

                            // Track inventory item (container-aware via inv.id)
                            string gcLow = item.gcClass.ToLower();
                            string nc = "Armor";
                            if (gcLow.Contains("questitem") || gcLow.Contains("consumable") || gcLow.Contains("townportal") || gcLow.Contains("ring") || gcLow.Contains("amulet") || gcLow.Contains("scroll") || gcLow.Contains("potion") || gcLow.Contains("skillbook") || gcLow.Contains("voucher"))
                                nc = "Item";
                            else if (gcLow.Contains("sword") || gcLow.Contains("axe") || gcLow.Contains("mace") || gcLow.Contains("dagger") || gcLow.Contains("hammer") || gcLow.Contains("staff") || gcLow.Contains("spear"))
                                nc = "MeleeWeapon";
                            else if (gcLow.Contains("bow") || gcLow.Contains("gun") || gcLow.Contains("crossbow"))
                                nc = "RangedWeapon";
                            var gcObj = new GCObject { GCClass = item.gcClass, NativeClass = nc };
                            gcObj.StoredRarity = item.rarity;
                            gcObj.StoredLevel = item.storedLevel;
                            TrackInventoryItem(conn.ConnId.ToString(), itemIndex, gcObj, item.x, item.y, inv.id);
                            var itemDims = DungeonRunners.Managers.MerchantManager.GetItemDimensions(item.gcClass);
                            int iw = itemDims.width, ih = itemDims.height;
                            OccupyInventorySlots(conn.ConnId.ToString(), item.x, item.y, iw, ih, inv.id);
                            SetStackCount(conn.ConnId.ToString(), itemIndex, item.count > 0 ? item.count : 1, inv.id);

                            Debug.LogError($"        → Item {itemIndex} (container 0x{inv.id:X2}): {gcTypeToSend} at ({item.x},{item.y})");
                        }
                    }
                    else
                    {
                        writer.WriteByte(0x00);
                        Debug.LogError($"      → GCType: {inv.inventory.GCClass}, ID: 0x{inv.id:X2}, Items: 0");
                    }
                }

                // Ensure new placements (GetNextInventorySlot starts at 100) never collide
                // with slot indices we just assigned to loaded items.
                {
                    string sc = conn.ConnId.ToString();
                    uint floor = globalItemIndex > 100 ? globalItemIndex : 100;
                    _inventorySlotCounters[sc] = floor;
                }

                writer.WriteByte(0x00);
                Debug.LogError($"    • UnitContainer final byte: 0x00");

                opBytes = (int)(writer.Position - beforeOp);
                Debug.LogError($"✓ UnitContainer component complete ({opBytes} bytes)");

                if (VerbosePacketLogging)
                {
                    Debug.LogError($"[OPXX-HEXDUMP] ═══════════════════════════════════════");
                    Debug.LogError($"[OPXX-HEXDUMP] OPERATION XX COMPLETE HEX DUMP:");
                    StringBuilder opHex8 = new StringBuilder();
                    for (int i = beforeOp; i < writer.Position; i += 16)
                    {
                        opHex8.Append($"{i:X8}  ");
                        for (int j = 0; j < 16; j++)
                        {
                            if (i + j < writer.Position)
                                opHex8.Append($"{writer.ToArray()[i + j]:X2} ");
                            else
                                opHex8.Append("   ");
                        }
                        opHex8.Append(" |");
                        for (int j = 0; j < 16 && i + j < writer.Position; j++)
                        {
                            byte b = writer.ToArray()[i + j];
                            opHex8.Append((b >= 32 && b < 127) ? (char)b : '.');
                        }
                        opHex8.AppendLine("|");
                    }
                    Debug.LogError(opHex8.ToString());
                    Debug.LogError($"[OPXX-HEXDUMP] ═══════════════════════════════════════");
                }
                Debug.LogError($"✓ UnitContainer component complete");
                Debug.LogError($"[CUMULATIVE-OP8] Total after Op8: {writer.Position} bytes");

                // ╔═══════════════════════════════════════════════════════════════════════════════╗
                // ║ OPERATION 9: MODIFIERS                                                        ║
                // ╚═══════════════════════════════════════════════════════════════════════════════╝
                Debug.LogError("=== OPERATION 9: MODIFIERS COMPONENT ===");
                beforeOp = writer.Position;

                var modifiers = avatar.Children.FirstOrDefault(c => c.GCClass == "Modifiers");
                if (modifiers == null)
                {
                    Debug.LogError("❌ Modifiers not found in avatar children!");
                    return;
                }

                Debug.LogError($"✓ Modifiers ID: {modifiers.Id}");

                writer.WriteByte(0x32);
                writer.WriteUInt16((ushort)avatar.Id);
                writer.WriteUInt16((ushort)modifiers.Id);
                WriteGCType(writer, "Modifiers");
                writer.WriteByte(0x01);

                // CORRECT FORMAT: uint32 + uint32 + byte(tag)
                writer.WriteUInt32(0x00000000);  // value1 -> this+0x74
                writer.WriteUInt32(0x00000000);  // value2 -> this+0x70
                writer.WriteByte(0x00);          // ReadChildDataComplete tag = empty

                opBytes = (int)(writer.Position - beforeOp);
                Debug.LogError($"✓ Modifiers component complete ({opBytes} bytes)");

                if (VerbosePacketLogging)
                {
                    Debug.LogError($"[OPXX-HEXDUMP] ═══════════════════════════════════════");
                    Debug.LogError($"[OPXX-HEXDUMP] OPERATION XX COMPLETE HEX DUMP:");
                    StringBuilder opHex9 = new StringBuilder();
                    for (int i = beforeOp; i < writer.Position; i += 16)
                    {
                        opHex9.Append($"{i:X8}  ");
                        for (int j = 0; j < 16; j++)
                        {
                            if (i + j < writer.Position)
                                opHex9.Append($"{writer.ToArray()[i + j]:X2} ");
                            else
                                opHex9.Append("   ");
                        }
                        opHex9.Append(" |");
                        for (int j = 0; j < 16 && i + j < writer.Position; j++)
                        {
                            byte b = writer.ToArray()[i + j];
                            opHex9.Append((b >= 32 && b < 127) ? (char)b : '.');
                        }
                        opHex9.AppendLine("|");
                    }
                    Debug.LogError(opHex9.ToString());
                    Debug.LogError($"[OPXX-HEXDUMP] ═══════════════════════════════════════");
                }
                Debug.LogError($"✓ Modifiers component complete");
                Debug.LogError($"[CUMULATIVE-OP9] Total after Op9: {writer.Position} bytes");


                // ╔═══════════════════════════════════════════════════════════════════════════════╗
                // ║ OPERATION 10: SKILLS COMPONENT                                                ║
                // ╚═══════════════════════════════════════════════════════════════════════════════╝
                Debug.LogError("=== OPERATION 10: SKILLS COMPONENT ===");
                beforeOp = writer.Position;

                var skills = avatar.Children.FirstOrDefault(c => c.GCClass == "avatar.base.skills");
                if (skills == null)
                {
                    Debug.LogError("🔧 Skills not found in avatar children - CREATING IT!");
                    skills = new GCObject();
                    skills.GCClass = "avatar.base.skills";
                    skills.NativeClass = "Skills";
                    skills.Name = "Skills";
                    skills.Id = _nextEntityId++;
                    avatar.AddChild(skills);
                    Debug.LogError("✅ Skills created and added to Avatar children");
                }

                Debug.LogError($"[OP10-START] Skills ID: {skills.Id}");

                // ── Store Skills component ID for trainer response packets ──
                _playerSkillsComponentId[spawnConnIdKey] = (ushort)skills.Id;
                _playerSkillSlots[spawnConnIdKey] = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

                // 🔥 ADD HEADER SIZE LOGGING HERE 🔥
                int headerStart = writer.Position;
                writer.WriteByte(0x32);
                writer.WriteUInt16((ushort)avatar.Id);
                writer.WriteUInt16((ushort)skills.Id);
                WriteGCType(writer, "avatar.base.skills");
                writer.WriteByte(0x01);
                int headerEnd = writer.Position;
                Debug.LogError($"[OP10-HEADER] Component header: {headerEnd - headerStart} bytes (expected 26)");
                Debug.LogError($"[OP10-HEADER] Component header written");

                writer.WriteUInt32(0xFFFFFFFF);
                Debug.LogError($"[OP10-INIT] Wrote 0xFFFFFFFF");

                // 🔥 GET SKILLS FROM MANIPULATORS (ActiveSkills only for Op10) 🔥
                var manipulatorSkills = manipulators?.Children?.Where(c =>
                    c.NativeClass == "ActiveSkill"
                    && !(c.GCClass ?? "").StartsWith("skills.professions.", StringComparison.OrdinalIgnoreCase)).ToList()
                    ?? new List<GCObject>();

                // 🔥 GET PASSIVE SKILLS FROM SAVED CHARACTER 🔥
                // PassiveSkills are in both Manipulators (for hotbar) and Op10 (for skill book/trainer)
                // Op10 lists them separately from the saved character data
                var passiveSkills = new List<GCObject>();
                if (savedChar.skills != null)
                {
                    foreach (var skillId in savedChar.skills)
                    {
                        if (skillId.ToLower().Contains("passive") || skillId.ToLower().Contains("trait"))
                        {
                            passiveSkills.Add(new GCObject
                            {
                                NativeClass = "PassiveSkill",
                                GCClass = skillId,
                                Name = null
                            });
                            Debug.LogError($"[OP10-PASSIVE] Including passive: {skillId}");
                        }
                    }
                }

                int totalSkillCount = manipulatorSkills.Count + passiveSkills.Count;
                Debug.LogError($"[OP10-COUNT] Active skills: {manipulatorSkills.Count}, Passive skills: {passiveSkills.Count}, Total: {totalSkillCount}");
                Debug.LogError($"[OP10-COUNT] DatabaseLoader.Skills.Count = {DatabaseLoader.Skills.Count} (NOT USING!)");
                writer.WriteByte((byte)totalSkillCount);
                Debug.LogError($"[OP10-COUNT] Writing {totalSkillCount} skills (active + passive)");

                // Op10 entityId = 0 for all skills — drags work fine with original EXE.
                // DLL-side 0x141F refresh doesn't need entity registration.

                foreach (var skillObj in manipulatorSkills)
                {
                    int skillStart = writer.Position;
                    uint skillEntityId = 0;

                    WriteGCType(writer, skillObj.GCClass);
                    writer.WriteUInt32(skillEntityId);

                    // Write saved level (binary: Skill+0x75) instead of hardcoded 0x01
                    int savedSkillLevel = savedChar.GetSkillLevel(skillObj.GCClass);
                    if (savedSkillLevel < 1) savedSkillLevel = 1;
                    writer.WriteByte((byte)savedSkillLevel);

                    // Build slot mapping for trainer response packets
                    _playerSkillSlots[spawnConnIdKey][skillObj.GCClass] = skillEntityId;
                    int dotIdx = skillObj.GCClass.LastIndexOf('.');
                    if (dotIdx >= 0)
                        _playerSkillSlots[spawnConnIdKey][skillObj.GCClass.Substring(dotIdx + 1)] = skillEntityId;

                    int skillBytes = writer.Position - skillStart;
                    Debug.LogError($"[OP10-SKILL] '{skillObj.GCClass}' entityId={skillEntityId} level={savedSkillLevel} ({skillBytes} bytes)");
                }
                // ── Write passive skills (not in Manipulators, but in Skills component) ──
                foreach (var skillObj in passiveSkills)
                {
                    int skillStart = writer.Position;
                    uint skillEntityId = 0;

                    WriteGCType(writer, skillObj.GCClass);
                    writer.WriteUInt32(skillEntityId);

                    int savedSkillLevel = savedChar.GetSkillLevel(skillObj.GCClass);
                    if (savedSkillLevel < 1) savedSkillLevel = 1;
                    writer.WriteByte((byte)savedSkillLevel);

                    _playerSkillSlots[spawnConnIdKey][skillObj.GCClass] = skillEntityId;
                    int dotIdx = skillObj.GCClass.LastIndexOf('.');
                    if (dotIdx >= 0)
                        _playerSkillSlots[spawnConnIdKey][skillObj.GCClass.Substring(dotIdx + 1)] = skillEntityId;

                    int skillBytes = writer.Position - skillStart;
                    Debug.LogError($"[OP10-PASSIVE] '{skillObj.GCClass}' entityId={skillEntityId} level={savedSkillLevel} ({skillBytes} bytes)");
                }
                // ── Store next available skill entity ID for dynamic skill addition ──
                _playerNextSkillEntityId[spawnConnIdKey] = _nextEntityId;
                Debug.LogError($"[OP10-TRACK] Next skill entity ID for trainer: {_nextEntityId}");
                // ═══════════════════════════════════════════════════════════════
                // 🔥 ADD THIS DEBUGGING CODE HERE! 🔥
                // ═══════════════════════════════════════════════════════════════
                Debug.LogError($"[OP10-DEBUG] ═══════════════════════════════════════");
                Debug.LogError($"[OP10-DEBUG] Position after foreach: {writer.Position}");
                Debug.LogError($"[OP10-DEBUG] Last 30 bytes after foreach:");
                if (VerbosePacketLogging && writer.Position >= 30)
                {
                    StringBuilder hexAfterLoop = new StringBuilder();
                    for (int i = writer.Position - 30; i < writer.Position; i++)
                    {
                        hexAfterLoop.Append($"{writer.ToArray()[i]:X2} ");
                    }
                    Debug.LogError(hexAfterLoop.ToString());
                }
                Debug.LogError($"[OP10-DEBUG] ═══════════════════════════════════════");

                Debug.LogError($"[OP10-PROFESSION] Writing SkillProfession...");
                int professionStart = writer.Position;

                writer.WriteByte(0x01);
                Debug.LogError($"[OP10-DEBUG] After WriteByte(0x01), position: {writer.Position}");
                if (VerbosePacketLogging) Debug.LogError($"[OP10-DEBUG] Bytes written: {BitConverter.ToString(writer.ToArray(), professionStart, writer.Position - professionStart)}");

                int beforeGCType = writer.Position;
                string profession = savedChar.className?.ToLower() switch
                {
                    "mage" or "warlock" => "skills.professions.Warlock",
                    "ranger" => "skills.professions.Ranger",
                    _ => "skills.professions.Warrior"
                };
                Debug.LogError($"[OP10-PROFESSION] savedChar.className = '{savedChar.className}', sending profession = '{profession}'");
                WriteGCType(writer, profession);
                int afterGCType = writer.Position;
                Debug.LogError($"[OP10-PROFESSION] {profession} written");

                opBytes = (int)(writer.Position - beforeOp);
                Debug.LogError($"[OP10-COMPLETE] Skills: {opBytes} bytes");


                Debug.LogError($"[OP10-DEBUG] After WriteGCType, position: {writer.Position}");
                if (VerbosePacketLogging) Debug.LogError($"[OP10-DEBUG] GCType bytes: {BitConverter.ToString(writer.ToArray(), beforeGCType, afterGCType - beforeGCType)}");

                if (VerbosePacketLogging) Debug.LogError($"[OP10-DEBUG] COMPLETE profession bytes: {BitConverter.ToString(writer.ToArray(), professionStart, writer.Position - professionStart)}");

                Debug.LogError($"[OP10-PROFESSION] {profession} written");
                // ═══════════════════════════════════════════════════════════════
                // 🔥 END OF DEBUG CODE 🔥
                // ═══════════════════════════════════════════════════════════════
                Debug.LogError($"[OP10-COMPLETE] Skills: {opBytes} bytes");
                Debug.LogError($"[CUMULATIVE-OP10] Total after Op10: {writer.Position} bytes");




                // ╔═══════════════════════════════════════════════════════════════════════════════╗
                // ║ OPERATION 12: INIT AVATAR - FINAL VERSION!                                    ║
                // ╚═══════════════════════════════════════════════════════════════════════════════╝
                var resolvedSpawn = ResolvePlayerSpawnPosition(conn);
                float spawnX = resolvedSpawn.x;
                float spawnY = resolvedSpawn.y;
                float spawnZ = resolvedSpawn.z;
                conn.PlayerPosX = spawnX;
                conn.PlayerPosY = spawnY;
                conn.PlayerPosZ = spawnZ;
                conn.LivePlayerPosX = spawnX;
                conn.LivePlayerPosY = spawnY;
                conn.LivePlayerPosZ = spawnZ;
                conn.LivePlayerHeading = conn.PlayerHeading;
                Debug.LogError($"[SPAWN-TRACK] Primed authoritative player position before OP12/combat registration pos=({spawnX:F1},{spawnY:F1},{spawnZ:F1}) heading={conn.PlayerHeading:F1} zone={conn.CurrentZoneName} instance={conn.InstanceId}");
                // Set heading from zones.spawn_heading BEFORE OP12
                if (!DungeonMazeSpawner.IsProceduralZone(conn.CurrentZoneName) && _zones.TryGetValue(conn.CurrentZoneId, out Zone preOp12Zone) && preOp12Zone.spawnHeading != 0)
                    conn.PlayerHeading = preOp12Zone.spawnHeading;
                conn.LivePlayerHeading = conn.PlayerHeading;
                Debug.LogError("=== OPERATION 12: INIT AVATAR ===");
                beforeOp = writer.Position;

                writer.WriteByte(0x02);
                writer.WriteUInt16((ushort)avatar.Id);
                Debug.LogError($"[OP12] Wrote opcode 0x02 and Avatar ID: {avatar.Id}");

                // WorldEntity.WriteInit()
                Debug.LogError($"[OP12] Before WorldEntity.WriteInit: position {writer.Position}");
                writer.WriteUInt32(0x04);  // worldEntityFlags - MUST BE 0x04!
                Debug.LogError($"[OP12] WorldEntityFlags: 0x04");

                writer.WriteInt32((int)(spawnX * 256));
                Debug.LogError($"[OP12] After posX: position {writer.Position}");
                writer.WriteInt32((int)(spawnY * 256));
                Debug.LogError($"[OP12] After posY: position {writer.Position}");
                writer.WriteInt32((int)(spawnZ * 256));
                Debug.LogError($"[OP12] After posZ: position {writer.Position}");
                writer.WriteInt32((int)(conn.PlayerHeading * 256));
                Debug.LogError($"[OP12] After heading ({conn.PlayerHeading}): position {writer.Position}");
                writer.WriteByte(0x01);  // worldEntityInitFlags
                Debug.LogError($"[OP12] After initFlags: position {writer.Position}");

                // 🔥 FIX #1: This should be 0, NOT avatar.Id!
                writer.WriteUInt16(0);  // Unk1Case - Go server defaults to 0
                Debug.LogError($"[OP12] After Unk1Case (0, was avatar.Id): position {writer.Position}");

                // Unit.WriteInit()
                Debug.LogError($"[OP12] Before Unit.WriteInit: position {writer.Position}");
                writer.WriteByte(0x07);  // unitFlags////////////////////////////////////////////////////
                Debug.LogError($"[OP12] After unitFlags: position {writer.Position}");
                //  writer.WriteByte(0x0F);  ///////////////////////////////////////////////////////////////////////////

                // writer.WriteByte(72);    // Level
                byte unitLevel = (byte)Math.Max(1, Math.Min(255, playerState.Level));
                writer.WriteByte(unitLevel);
                Debug.LogError($"[OP12] After level={unitLevel} persistedLevel={savedChar.level}: position {writer.Position}");

                writer.WriteUInt16(0);
                Debug.LogError($"[OP12] After unk1: position {writer.Position}");
                writer.WriteUInt16(0);
                Debug.LogError($"[OP12] After unk2: position {writer.Position}");

                // 🔥🔥🔥 FIX #2: CRITICAL! Must be conn.ConnId, NOT avatar.Id! 🔥🔥🔥
                // This tells the client "YOU own this avatar" - without it, loading screen hangs!
                //  writer.WriteUInt16((ushort)conn.ConnId);  // ownerID = CONNECTION ID!
                // Debug.LogError($"[OP12] 🔥 OWNER ID: conn.ConnId={conn.ConnId} (was avatar.Id={avatar.Id})");
                // 🔥🔥🔥 FIX: Owner ID must be the PLAYER ENTITY ID, not connection ID!
                // This tells the client which entity owns this avatar
                writer.WriteUInt16((ushort)player.Id);  // ownerID = Player entity ID!
                Debug.LogError($"[OP12] 🔥 OWNER ID: player.Id={player.Id} (was conn.ConnId={conn.ConnId})");


                uint avatarSynchValue = GetSynchValue(conn);
                uint avatarManaValue = playerState.CurrentManaWire;
                writer.WriteUInt32(avatarSynchValue);
                writer.WriteUInt32(avatarManaValue);
                Debug.LogError($"[OP12] HP/Mana init: hp={avatarSynchValue} mana={avatarManaValue}");

                // Hero.WriteInit()
                Debug.LogError($"[OP12] Before Hero.WriteInit: position {writer.Position}");
                writer.WriteUInt32(playerState.Experience);  // ← TRY HERE INSTEAD
                Debug.LogError($"[OP12] After Hero XP({playerState.Experience}): position {writer.Position}");
                // writer.WriteUInt32(0);
                Debug.LogError($"[OP12] After Hero uint32(0): position {writer.Position}");

                // Hero.WriteInit — Binary RE: +0x334=STR, +0x336=AGI, +0x338=END, +0x33A=INT, +0x33C=StatPts
                int _pointsPerLevel = NativeStatPointsPerLevel;
                int _totalAllocated = savedChar.statStrength + savedChar.statAgility
                                    + savedChar.statEndurance + savedChar.statIntellect;
                int _totalAvailable = (playerState.Level - 1) * _pointsPerLevel;
                int _statPtsRemaining = System.Math.Max(0, _totalAvailable - _totalAllocated);
                writer.WriteUInt16((ushort)savedChar.statStrength);
                writer.WriteUInt16((ushort)savedChar.statAgility);
                writer.WriteUInt16((ushort)savedChar.statEndurance);
                writer.WriteUInt16((ushort)savedChar.statIntellect);
                writer.WriteUInt16((ushort)_statPtsRemaining);

                // RespecCount at +0x33E is a COUNTDOWN TIMER in seconds
                // (binary RE: Avatar processInited@0x4EE23F decrements by 1 every 30 ticks ≈ 1 second)
                int respecCooldownRemaining = 0;
                if (savedChar.lastRespecTime > 0)
                {
                    int nowSec = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    int elapsedSec = nowSec - savedChar.lastRespecTime;
                    respecCooldownRemaining = System.Math.Max(0, GetNativeRespecCooldownSeconds() - elapsedSec);
                }
                writer.WriteUInt16((ushort)respecCooldownRemaining);
                Debug.LogError($"[OP12] Stats: STR={savedChar.statStrength} AGI={savedChar.statAgility} END={savedChar.statEndurance} INT={savedChar.statIntellect} pts={_statPtsRemaining} respecTimer={respecCooldownRemaining}s");

                // Hero+0x324 = stat hash max (used for PvP Power calc), Hero+0x330 = PVPRating (Fixed32)
                writer.WriteUInt32((uint)savedChar.pvpWins);   // +0x324 — persisted in DB
                writer.WriteUInt32((uint)savedChar.pvpRating); // +0x330 — persisted in DB

                // Avatar.WriteInit()
                Debug.LogError($"[OP12] Before Avatar.WriteInit: position {writer.Position}");
                // writer.WriteByte(savedChar.faceFeature); // FaceFeature (eye patch, scars, etc.)
                //writer.WriteByte(savedChar.skin);
                writer.WriteByte(savedChar.face);       // Face variant
                                                        // writer.WriteByte(savedChar.faceFeature); // FaceFeature (eye patch, scars, etc.)
                writer.WriteByte(savedChar.hair);       // Hair style  
                writer.WriteByte(savedChar.hairColor);  // Hair color
                Debug.LogError($"[OP12] Avatar appearance: Face={savedChar.face}, Hair={savedChar.hair}, HairColor={savedChar.hairColor}");
                Debug.LogError($"[OP12] After Avatar.WriteInit (3 bytes): position {writer.Position}");



                opBytes = (int)(writer.Position - beforeOp);
                Debug.LogError($"[OP12-COMPLETE] Init Avatar: {opBytes} bytes");
                Debug.LogError($"[CUMULATIVE-OP12] Total after Op12: {writer.Position} bytes");
                Debug.LogError("✓ Operation 12 complete - SPAWN PACKET ENDS HERE!");
                Debug.LogError($"[CUMULATIVE-OP12] Total after Op12: {writer.Position} bytes");
                Debug.LogError("✓ Operation 12 complete - SPAWN PACKET ENDS HERE!");

                Debug.LogError("✓ Operation 12 complete - SPAWN PACKET ENDS HERE!");

                // ╔═══════════════════════════════════════════════════════════════════════════════╗
                // ║ OPERATION 11: UNITBEHAVIOR COMPONENT                                          ║
                // ╚═══════════════════════════════════════════════════════════════════════════════╝
                Debug.LogError("=== OPERATION 11: UNITBEHAVIOR COMPONENT ===");
                beforeOp = writer.Position;

                var unitBehavior = avatar.Children.FirstOrDefault(c => c.GCClass == "avatar.base.UnitBehavior");

                if (unitBehavior == null)
                {
                    Debug.LogError("❌ CRITICAL ERROR: UnitBehavior not found in avatar children!");
                    Debug.LogError("❌ LoadAvatar() should have created it!");
                    return;
                }

                Debug.LogError($"[OP11-COMPONENT] UnitBehavior ID: {unitBehavior.Id} (already assigned)");

                writer.WriteByte(0x32);
                writer.WriteUInt16((ushort)avatar.Id);
                writer.WriteUInt16((ushort)unitBehavior.Id);
                WriteGCType(writer, "avatar.base.UnitBehavior");
                writer.WriteByte(0x01);

                Debug.LogError($"[OP11-HEADER] Component header complete - Entity {avatar.Id}, Component {unitBehavior.Id}");

                int writeInitStart = writer.Position;

                writer.WriteByte(0xFF);
                Debug.LogError($"[OP11-BYTE] Offset {writer.Position - 1}: 0xFF (Behavior marker)");

                writer.WriteByte(0x00);
                Debug.LogError($"[OP11-BYTE] Offset {writer.Position - 1}: 0x00 (Action1 null)");

                writer.WriteByte(0x00);
                Debug.LogError($"[OP11-BYTE] Offset {writer.Position - 1}: 0x00 (Action2 null)");

                // writer.WriteByte(0x01);
                // Debug.LogError($"[OP11-BYTE] Offset {writer.Position - 1}: 0x01 (Behavior end)");
                writer.WriteByte(0xFF);
                Debug.LogError($"[OP11-BYTE] Offset {writer.Position - 1}: 0xFF (Generation counter 0x7d)");
                byte unitMoverFlags = 0x08;  // bit 3: enables heading application in UnitMover::Update
                writer.WriteByte(unitMoverFlags);
                Debug.LogError($"[OP11-BYTE] Offset {writer.Position - 1}: 0x{unitMoverFlags:X2} (UnitMoverFlags)");

                writer.WriteInt32((int)(conn.PlayerHeading * 256));
                Debug.LogError($"[OP11-UINT32] Offset {writer.Position - 4}: UnitMover heading = {conn.PlayerHeading}");

                writer.WriteInt32((int)(conn.PlayerHeading * 256));
                Debug.LogError($"[OP11-UINT32] Offset {writer.Position - 4}: UnitMover heading2 = {conn.PlayerHeading}");

                byte waypointFlags = 0x00;
                writer.WriteByte(waypointFlags);
                Debug.LogError($"[OP11-BYTE] Offset {writer.Position - 1}: 0x{waypointFlags:X2} (WaypointFlags)");

                writer.WriteByte(0xFF);
                Debug.LogError($"[OP11-BYTE] Offset {writer.Position - 1}: 0xFF (SessionID)");

                writer.WriteByte(0x00);
                Debug.LogError($"[OP11-BYTE] Offset {writer.Position - 1}: 0x00 (UnitBehaviorUnk1)");

                writer.WriteByte(0x00);
                Debug.LogError($"[OP11-BYTE] Offset {writer.Position - 1}: 0x00 (UnitBehaviorUnk2)");
                // ← ADD THIS LINE HERE:
                // writer.WriteByte(0x02);
                //  Debug.LogError($"[OP11-BYTE] Offset {writer.Position - 1}: 0x02 (UnitBehaviorUnk3 - FINAL BYTE)");
                opBytes = (int)(writer.Position - beforeOp);
                int writeInitBytes = writer.Position - writeInitStart;

                Debug.LogError($"[OP11-COMPLETE] WriteInit: {writeInitBytes} bytes");
                Debug.LogError($"[OP11-COMPLETE] Total operation: {opBytes} bytes");
                Debug.LogError("=== OPERATION 11 COMPLETE ===");
                Debug.LogError($"=== PACKET SIZE BEFORE OP12: {writer.Position} bytes ===");

                // ═══════════════════════════════════════════════════════════════════════════════
                // 🔥 NEW DEBUG CODE - SIZE CHECK BEFORE OP12
                // ═══════════════════════════════════════════════════════════════════════════════
                Debug.LogError($"");
                Debug.LogError($"[SIZE-CHECK] ═══════════════════════════════════════════════════════════");
                Debug.LogError($"[SIZE-CHECK] Position before Operation 12: {writer.Position} bytes");
                Debug.LogError($"[SIZE-CHECK] Expected position (Go server): 1920 bytes");
                int diff = writer.Position - 1920;

                if (writer.Position > 1937)
                {
                    Debug.LogError($"❌ [SIZE-CHECK] TOO MANY BYTES! Extra: {writer.Position - 1937} bytes");
                }
                else if (writer.Position < 1937)
                {
                    Debug.LogError($"❌ [SIZE-CHECK] TOO FEW BYTES! Missing: {1937 - writer.Position} bytes");
                }
                else
                {
                    Debug.LogError($"✅ [SIZE-CHECK] PERFECT MATCH!");
                }

                // Show last 50 bytes before Op12 to help identify the issue
                Debug.LogError($"[SIZE-CHECK] Last 50 bytes before Op12:");
                if (VerbosePacketLogging && writer.Position >= 50)
                {
                    StringBuilder lastBytes = new StringBuilder();
                    for (int i = writer.Position - 50; i < writer.Position; i++)
                    {
                        lastBytes.Append($"{writer.ToArray()[i]:X2} ");
                        if ((i - (writer.Position - 50) + 1) % 16 == 0)
                        {
                            lastBytes.Append("\n              ");
                        }
                    }
                    Debug.LogError($"              {lastBytes.ToString()}");
                }
                Debug.LogError($"[SIZE-CHECK] ═══════════════════════════════════════════════════════════");
                Debug.LogError($"");
                // ═══════════════════════════════════════════════════════════════════════════════
                // 🔥 END OF NEW DEBUG CODE
                // ═══════════════════════════════════════════════════════════════════════════════
                Debug.LogError("=== OPERATION 11 COMPLETE ===");
                Debug.LogError($"[CUMULATIVE-OP11] Total after Op11: {writer.Position} bytes");
                Debug.LogError($"=== PACKET SIZE BEFORE OP12: {writer.Position} bytes ===");

                Debug.LogError($"[CUMULATIVE-OP11] Total after Op11: {writer.Position} bytes");
                Debug.LogError($"=== PACKET SIZE BEFORE OP12: {writer.Position} bytes ===");

                // ═══════════════════════════════════════════════════════════════════════════════
                // PACKET 1: SPAWN - ends with 0x46 (EndStreamConnected)
                // ═══════════════════════════════════════════════════════════════════════════════
                Debug.LogError($"[SPAWN-TRACK] ════════════════════════════════════════════════════");
                Debug.LogError($"[SPAWN-TRACK] ABOUT TO WRITE 0x46 (EndStreamConnected)");
                Debug.LogError($"[SPAWN-TRACK] conn.UpdateNumber BEFORE 0x46: {conn.UpdateNumber}");
                Debug.LogError($"[SPAWN-TRACK] isZoneTransition: {isZoneTransition}");

                writer.WriteByte(0x46);  // EndStreamConnected
                byte[] spawnData = writer.ToArray();
                if (VerbosePacketLogging)
                {
                    Debug.LogError($"[PLAYER-SPAWN-HEX] Size: {spawnData.Length} hex: {BitConverter.ToString(spawnData)}");
                    Debug.LogError("=== FULL UNITY SPAWN PACKET ===");
                    StringBuilder fullHex = new StringBuilder();
                    for (int i = 0; i < spawnData.Length; i += 16)
                    {
                        fullHex.Append($"{i:X8}  ");
                        for (int j = 0; j < 16; j++)
                        {
                            if (i + j < spawnData.Length)
                                fullHex.Append($"{spawnData[i + j]:X2} ");
                            else
                                fullHex.Append("   ");
                        }
                        fullHex.Append(" |");
                        for (int j = 0; j < 16 && i + j < spawnData.Length; j++)
                        {
                            byte b = spawnData[i + j];
                            fullHex.Append((b >= 32 && b < 127) ? (char)b : '.');
                        }
                        fullHex.AppendLine("|");
                    }
                    Debug.LogError(fullHex.ToString());
                }
                Debug.LogError($"[PACKET-1] Spawn packet size: {spawnData.Length} bytes");
                Debug.LogError($"[PACKET-1] Last byte: 0x{spawnData[spawnData.Length - 1]:X2} (should be 0x46)");
                // Store avatar ID
                Debug.LogError($"[PACKET-1] 🚀 SENDING SPAWN PACKET...");
                SendCompressedA(conn, 0x01, 0x0F, spawnData);
                Debug.LogError($"[PACKET-1] ✅ SPAWN PACKET SENT!");




                // ADD THIS RIGHT AFTER:

                // ═══════════════════════════════════════════════════════════════════════════════
                // RESTORE PASSIVE SKILL HOTBAR POSITIONS (post-spawn)
                // PassiveSkills can't go in Op4/Manipulators (readInit crashes).
                // Instead, send individual Add packets to place them on the hotbar.
                // ═══════════════════════════════════════════════════════════════════════════════
                if (savedChar.hotbarSlots != null)
                {
                    string passiveConnKey = conn.ConnId.ToString();
                    if (!_playerManipMap.ContainsKey(passiveConnKey)) _playerManipMap[passiveConnKey] = new Dictionary<uint, string>();
                    RecalculateHotbarPassiveBonuses(conn, savedChar);
                    ushort manipCid = GetManipulatorsComponentId(passiveConnKey);
                    if (manipCid != 0)
                    {
                        foreach (var hbs in savedChar.hotbarSlots)
                        {
                            if (IsPassiveSkill(hbs.skill))
                            {
                                _playerManipMap[passiveConnKey][hbs.slot] = hbs.skill;
                                byte skillLv = (byte)savedChar.GetSkillLevel(hbs.skill);
                                if (skillLv < 1) skillLv = 1;

                                var pkt = new LEWriter();
                                pkt.WriteByte(0x07);
                                pkt.WriteByte(0x35);
                                pkt.WriteUInt16(manipCid);
                                pkt.WriteByte(0x00);                    // subMsg = Add
                                pkt.WriteByte(0xFF);
                                pkt.WriteCString(hbs.skill.ToLower());
                                pkt.WriteUInt32(hbs.slot);
                                pkt.WriteByte(skillLv);
                                WritePlayerEntitySynch(conn, pkt);
                                pkt.WriteByte(0x06);
                                SendCompressedE(conn, pkt.ToArray());
                                Debug.LogError($"[PASSIVE-HOTBAR] Restored passive '{hbs.skill}' to slot {hbs.slot} lv={skillLv}");
                            }
                        }
                    }
                }

                SendZoneNPCs(conn, conn.CurrentZoneId);
                SendZonePortals(conn, conn.CurrentZoneId);
                SpawnReturnTownPortal(conn);
                if (conn.CurrentZoneName == null || !conn.CurrentZoneName.Contains("boss"))
                    SendZoneCheckpoints(conn, conn.CurrentZoneId);
                SendZoneChests(conn, conn.CurrentZoneName);
                SendZoneWorldEntities(conn);
                SendDroppedItemsForZone(conn);  // Load persisted dropped items for this zone

                // ═══════════════════════════════════════════════════════════
                // Auto-spawn Bling Gnome if skill is on the SKILL TRAY.
                // Per SummonBlingGnome.gc: "Place this skilzizzil in your
                // skill tray to 'auto-magically' summon up a mad Bling
                // Gnome" — only skill tray (hotbar) placement triggers it,
                // NOT just having the skill learned.
                // ═══════════════════════════════════════════════════════════
                bool hasBlingSkill = false;
                if (savedChar.hotbarSlots != null)
                    hasBlingSkill = savedChar.hotbarSlots.Any(hbs => hbs.skill.IndexOf("BlingGnome", StringComparison.OrdinalIgnoreCase) >= 0
                                                                   || hbs.skill.IndexOf("SummonBling", StringComparison.OrdinalIgnoreCase) >= 0);
                if (hasBlingSkill)
                {
                    BlingGnomeManager.Instance.SetServer(this);
                    // Zone transition destroys all client entities — clean up stale gnome state
                    BlingGnomeManager.Instance.CleanupForZoneTransition(conn.ConnId);
                    if (!BlingGnomeManager.Instance.HasGnome(conn.ConnId))
                    {
                        Debug.LogError($"[ZONE-IN] BlingGnome skill found - scheduling delayed spawn for {conn.LoginName}");
                        // Spawn AFTER zone-in completes — sending during zone load
                        // makes the gnome invisible because the client hasn't
                        // finished processing the zone data yet.
                        StartCoroutine(DelayedGnomeSpawn(conn, conn.CurrentZoneName, conn.CurrentZoneId, conn.InstanceId));
                    }
                }

                // Quest icon update — must fire AFTER CurrentZoneGcType is set and AFTER NPCs spawn
                Debug.LogError($"[ZONE-IN] Sending quest update for zone: {conn.CurrentZoneGcType}");
                if (QuestManager.Instance != null)
                    QuestManager.Instance.SendAvailableQuestUpdateForZone(conn);

                // Store avatar ID
                // Store avatar ID
                _spawnedAvatarIds[conn.LoginName] = avatar.Id;

                // ═══ MULTIPLAYER: Save component IDs for other-player spawn ═══
                conn.AvatarGcType = avatar.GCClass;
                conn.ClassName = savedChar.className ?? "Fighter";
                conn.PlayerLevel = playerState.Level;
                conn.BehaviorComponentId = (ushort)(unitBehavior?.Id ?? 0);
                conn.SkillsComponentId = (ushort)(skills?.Id ?? 0);
                conn.ManipulatorsComponentId = (ushort)(manipulators?.Id ?? 0);
                var modComp = avatar.Children?.FirstOrDefault(c => c.GCClass == "Modifiers");
                conn.ModifiersComponentId = (ushort)(modComp?.Id ?? 0);
                conn.IsSpawned = true;
                conn.LastOutboundHPWire = 0;
                conn.LastOutboundHPTime = 0f;
                conn.LastOutboundHPSource = null;
                conn.LastObservedClientHPWire = 0;
                conn.LastObservedClientHPTime = 0f;
                conn.LastObservedClientHPSource = null;
                Debug.LogError($"[MULTIPLAYER] Saved spawn data for {conn.LoginName}: avatar={avatar.Id} behavior={conn.BehaviorComponentId}");
                // ═══════════════════════════════════════════════════════════════════
                // REGISTER PLAYER FOR COMBAT
                var combatPlayerState = GetPlayerState(conn.ConnId.ToString());
                if (combatPlayerState != null)
                {
                    string combatInstanceKey = GetInstanceZoneKey(conn);
                    CombatManager.Instance.RegisterPlayer(avatar.Id, conn.LoginName, combatPlayerState, conn.PlayerPosX, conn.PlayerPosY, combatInstanceKey);
                    Debug.LogError($"[Combat] Registered player {conn.LoginName} instance={combatInstanceKey}");

                    // Send RNG seed to client to initiate synchronization
                    // uint seed = (uint)Environment.TickCount;
                    // SendRandomSeed(conn, seed, initializeRng: true);
                    // Debug.LogError($"[RNG] Sent initial seed to client: {seed}");
                }



                // ═══════════════════════════════════════════════════════════════════════════════
                // PACKET 2: SPAWN ACTION + FOLLOWCLIENT
                // Go server calls unitBehaviour:spawn(pos) which uses ActionSpawn (0x04), NOT WarpTo (0x11)!
                // ═══════════════════════════════════════════════════════════════════════════════
                if (unitBehavior != null)
                {
                    Debug.LogError("");
                    Debug.LogError("═══════════════════════════════════════════════════════════════════════════════");
                    Debug.LogError("🔥 BUILDING PACKET 2: SPAWN ACTION + FOLLOWCLIENT");
                    Debug.LogError("═══════════════════════════════════════════════════════════════════════════════");

                    var followWriter = new LEWriter();
                    // Set heading BEFORE spawn packet — tick reads this value
                    if (!DungeonMazeSpawner.IsProceduralZone(conn.CurrentZoneName) && _zones.TryGetValue(conn.CurrentZoneId, out Zone spawnHeadingZone) && spawnHeadingZone.spawnHeading != 0)
                    {
                        conn.PlayerHeading = spawnHeadingZone.spawnHeading;
                        Debug.LogError($"[HEADING] ✅ Set heading to {spawnHeadingZone.spawnHeading} from zone '{spawnHeadingZone.name}' (id={conn.CurrentZoneId})");
                    }

                    followWriter.WriteByte(0x07);  // BeginStream
                    Debug.LogError($"[PACKET-2] BeginStream: 0x07");

                    // ═══════════════════════════════════════════════════════════════
                    // SPAWN ACTION - ActionSpawn opcode is 0x04, NOT WarpTo 0x11!
                    // Format: 0x35 [ID] 0x04 0x04 [sessionID] [X] [Y] [Z] [unitID] 0x02 [HP]
                    // ═══════════════════════════════════════════════════════════════
                    int spawnStart = followWriter.Position;
                    followWriter.WriteByte(0x35);  // ComponentUpdate
                    followWriter.WriteUInt16((ushort)unitBehavior.Id);
                    followWriter.WriteByte(0x04);  // CreateAction1
                    followWriter.WriteByte(0x04);  // BehaviourActionSpawn (opcode 4!)
                    followWriter.WriteByte(0xFF);  // SessionID
                    followWriter.WriteInt32((int)(spawnX * 256));   // Position X - MUST be Int32!
                    followWriter.WriteInt32((int)(spawnY * 256));   // Position Y - MUST be Int32!
                    followWriter.WriteInt32((int)(spawnZ * 256));   // Position Z - MUST be Int32!
                    followWriter.WriteUInt16((ushort)avatar.Id);   // SomeUnitID

                    // 🔥🔥🔥 DEBUG SPAWN SYNCH 🔥🔥🔥
                    int spawnSynchPos = followWriter.Position;
                    Debug.LogError($"[SPAWN-SYNCH] Position before 0x02: {spawnSynchPos}");


                    // followWriter.WriteByte(0x00);  // Don't send HP synch - client calculates its own
                    WritePlayerEntitySynchNoCombatFlush(conn, followWriter);

                    Debug.LogError($"[SPAWN-SYNCH] SPAWN ACTION total bytes: {followWriter.Position - spawnStart}");

                    Debug.LogError($"[PACKET-2] SPAWN ACTION: UnitBehavior={unitBehavior.Id:X4}, Pos=({spawnX},{spawnY},{spawnZ})");

                    followWriter.WriteByte(0x06);  // EndStream
                    Debug.LogError($"[PACKET-2] EndStream: 0x06 at position {followWriter.Position - 1}");

                    byte[] followData = followWriter.ToArray();

                    if (VerbosePacketLogging)
                    {
                        Debug.LogError($"[PACKET-2-COMPLETE] ═══════════════════════════════════════");
                        Debug.LogError($"[PACKET-2-COMPLETE] Total packet size: {followData.Length} bytes");
                        Debug.LogError($"[PACKET-2-COMPLETE] Full hex dump:");
                        StringBuilder hexDump = new StringBuilder();
                        for (int i = 0; i < followData.Length; i++)
                        {
                            hexDump.Append($"{followData[i]:X2} ");
                            if ((i + 1) % 16 == 0)
                            {
                                hexDump.Append("\n[PACKET-2-COMPLETE]                      ");
                            }
                        }
                        Debug.LogError($"[PACKET-2-COMPLETE]                      {hexDump}");
                        Debug.LogError($"[PACKET-2-COMPLETE] ═══════════════════════════════════════");
                    }

                    Debug.LogError($"[PACKET-2] 🚀 SENDING...");
                    SendCompressedA(conn, 0x01, 0x0F, followData);
                    Debug.LogError($"[PACKET-2] ✅ SENT!");
                    Debug.LogError($"[SPAWN-TRACK] After Packet 2, conn.UpdateNumber: {conn.UpdateNumber}");

                    // ═══════════════════════════════════════════════════════════════════════════════
                    // PACKET 3: UNITMOVERUPDATE (0x65) - CRITICAL FOR LOADING SCREEN DISMISSAL!
                    // Go server sends this immediately after spawn action + FollowClient
                    // ═══════════════════════════════════════════════════════════════════════════════
                    Debug.LogError("=== PACKET 3: UNITMOVERUPDATE (0x65) ===");

                    var moveWriter = new LEWriter();
                    moveWriter.WriteByte(0x07);  // BeginStream
                    Debug.LogError($"[PACKET-3] BeginStream (0x07)");

                    moveWriter.WriteByte(0x35);  // ComponentUpdate
                    moveWriter.WriteUInt16((ushort)unitBehavior.Id);
                    moveWriter.WriteByte(0x65);  // UnitMoverUpdate
                    moveWriter.WriteByte(0x00);  // SessionID (0, not 0xFF!)
                    moveWriter.WriteByte(0x01);  // Update count
                    moveWriter.WriteByte(0x03);  // Update type (0x01 | 0x02)
                    moveWriter.WriteInt32((int)(conn.PlayerHeading * 256));    // Heading from DB
                    moveWriter.WriteInt32((int)(spawnX * 256));  // X position
                    moveWriter.WriteInt32((int)(spawnY * 256));  // Y position (NOTE: only X,Y - no Z!)

                    // 🔥🔥🔥 CRITICAL FIX: POST-INCREMENT! 🔥🔥🔥
                    int pkt3SynchPos = moveWriter.Position;
                    //  Debug.LogError($"[PACKET-3-SYNCH] Before 0x02: position={pkt3SynchPos}, ClientUpdateNumber={conn.Avatar.ClientUpdateNumber}");
                    WritePlayerEntitySynchNoCombatFlush(conn, moveWriter);


                    moveWriter.WriteByte(0x06);  // EndStream

                    byte[] moveData = moveWriter.ToArray();
                    Debug.LogError($"[PACKET-3] Total size: {moveData.Length} bytes");
                    if (VerbosePacketLogging) Debug.LogError($"[PACKET-3] Full hex: {BitConverter.ToString(moveData)}");
                    Debug.LogError($"[PACKET-3] 🚀 SENDING...");
                    SendCompressedA(conn, 0x01, 0x0F, moveData);
                    Debug.LogError($"[PACKET-3] ✅ SENT!");
                    Debug.LogError($"[SPAWN-TRACK] After Packet 3, conn.UpdateNumber: {conn.UpdateNumber}");
                    StartCoroutine(SendDeferredClientControl(conn, (ushort)unitBehavior.Id));




                    // ═══════════════════════════════════════════════════════════════════════════════
                    // 🔥 WELCOME MESSAGE - MOVED HERE! Go sends it AFTER avatar.Spawned = true
                    // ═══════════════════════════════════════════════════════════════════════════════
                    Debug.LogError("=== WELCOME MESSAGE ===");
                    var welcomeWriter = new LEWriter();
                    welcomeWriter.WriteByte(0x06);  // Chat/system message type
                    welcomeWriter.WriteByte(0x00);  // Channel (0 = system)
                    welcomeWriter.WriteByte(0x0d);  // Subtype (13 = GlobalAnnouncement)
                    string rawWelcome = ServerSettings.GetString("welcomeMessage", "Welcome to Dungeon Runners!");
                    string welcomeColor = ServerSettings.GetString("welcomeColor", "");
                    string welcomeMsg = WrapChatColor(rawWelcome, welcomeColor, "") + "\n";
                    foreach (char c in welcomeMsg)
                    {
                        welcomeWriter.WriteByte((byte)c);
                    }
                    welcomeWriter.WriteByte(0x00);  // Null terminator
                    SendCompressedA(conn, 0x01, 0x0F, welcomeWriter.ToArray());
                    Debug.LogError("[WELCOME] ✅ Sent welcome message (AFTER packets 1-3, matching Go order)");

                    // Send MOTD if set
                    string motd = ServerSettings.GetString("motd", "");
                    if (!string.IsNullOrEmpty(motd))
                    {
                        string motdColor = ServerSettings.GetString("motdColor", "");
                        var motdWriter = new LEWriter();
                        motdWriter.WriteByte(0x06);
                        motdWriter.WriteByte(0x00);
                        motdWriter.WriteByte(0x0d);
                        string motdMsg = WrapChatColor(motd, motdColor, "") + "\n";
                        foreach (char c in motdMsg)
                            motdWriter.WriteByte((byte)c);
                        motdWriter.WriteByte(0x00);
                        SendCompressedA(conn, 0x01, 0x0F, motdWriter.ToArray());
                        Debug.LogError($"[MOTD] Sent: {motd}");
                    }

                    // Enable message flushing now that spawn is complete
                    conn.AllowFlush = true;
                    Debug.LogError("[SPAWN] ✅ Spawn complete - enabling message queue flushing");

                    // Send GlobalKnobs overrides to DSOUND.dll via UDP
                    SendGlobalKnobsToDLL(conn);

                    // Send membership mode to DSOUND.dll via UDP
                    SendMembershipToDLL(conn);

                    // FreePlayerModifier — shows buff icon for free players.
                    // Only sent ONCE on first login. Persists across zones on its own.
                    if (IsPlayerFree(conn.LoginName) && conn.ModifiersId != 0
                        && !_freePlayerModifierSent.Contains(conn.LoginName))
                    {
                        _freePlayerModifierSent.Add(conn.LoginName);
                        _freePlayerModifierSent.Add(conn.LoginName + "_sent");
                        StartCoroutine(SendDelayedFreePlayerModifier(conn, 2.0f));
                    }

                    // NOW start tick updates
                    //With this:
                    // NOW start tick updates
                    if (conn.TickCoroutine != null)
                    {
                        StopCoroutine(conn.TickCoroutine);
                        Debug.LogError("[TICK] Stopped old tick coroutine");
                    }

                    // 🔥 FIX: Set connection position to ACTUAL spawn point before tick starts!
                    conn.PlayerPosX = spawnX;
                    conn.PlayerPosY = spawnY;
                    conn.PlayerPosZ = spawnZ;
                    conn.HasLivePlayerPosition = true;
                    conn.LivePlayerPosX = spawnX;
                    conn.LivePlayerPosY = spawnY;
                    conn.LivePlayerPosZ = spawnZ;
                    conn.LivePlayerHeading = conn.PlayerHeading;
                    conn.LivePlayerPositionTime = Time.time;
                    // PlayerHeading already set before spawn packet build
                    conn.SessionID = 0xFF;

                    conn.TickCoroutine = StartCoroutine(SendTickUpdates(conn, unitBehavior.Id));
                }
                else
                {
                    Debug.LogError("❌ CRITICAL: UnitBehavior not found - cannot send spawn/followclient!");
                }

                Debug.LogError($"");
                Debug.LogError("═══════════════════════════════════════════════════════════════════════════════");
                Debug.LogError($"🎯 SendPlayerEntitySpawn COMPLETE!");

                // ═══ MULTIPLAYER: Exchange spawn packets with other players in zone ═══
                try
                {
                    foreach (var other in _connections.Values)
                    {
                        if (other == conn) continue;
                        if (!other.IsSpawned) continue;
                        if (other.CurrentZoneGcType != conn.CurrentZoneGcType) continue;
                        if (other.InstanceId != conn.InstanceId) continue;  // Instance check

                        uint otherAvatarId = GetPlayerAvatarId(other.LoginName);
                        if (otherAvatarId != 0)
                        {
                            byte[] otherSpawn = BuildOtherPlayerSpawnPacket(other, otherAvatarId, conn);
                            if (otherSpawn != null)
                            {
                                SendToClient(conn, otherSpawn);
                                Debug.LogError($"[MULTIPLAYER] Sent {other.LoginName}'s avatar to {conn.LoginName}");
                            }
                        }

                        uint myAvatarId = GetPlayerAvatarId(conn.LoginName);
                        if (myAvatarId != 0)
                        {
                            byte[] mySpawn = BuildOtherPlayerSpawnPacket(conn, myAvatarId, other);
                            if (mySpawn != null)
                            {
                                SendToClient(other, mySpawn);
                                Debug.LogError($"[MULTIPLAYER] Sent {conn.LoginName}'s avatar to {other.LoginName}");
                            }
                        }
                    }
                }
                catch (Exception mpEx)
                {
                    Debug.LogError($"[MULTIPLAYER] Error in spawn exchange: {mpEx.Message}");
                }
                Debug.LogError($"   PACKET 1: Spawn ({spawnData.Length} bytes) ending with 0x46");
                Debug.LogError($"   PACKET 2: Spawn Action + FollowClient ending with 0x06");
                Debug.LogError($"   PACKET 3: UnitMoverUpdate (0x65)");
                Debug.LogError($"   WELCOME: Sent after all packets (matching Go order)");
                Debug.LogError("═══════════════════════════════════════════════════════════════════════════════");
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ SendPlayerEntitySpawn ERROR: {ex.Message}");
                Debug.LogError($"   Stack: {ex.StackTrace}");
            }
        }





        // Helper method for hex dumps
        private string HexDump(byte[] bytes, int offset, int length)
        {
            var sb = new System.Text.StringBuilder();
            int bytesPerLine = 16;

            for (int i = 0; i < length; i += bytesPerLine)
            {
                // Offset
                sb.Append($"{(offset + i):X8}  ");

                // Hex bytes
                for (int j = 0; j < bytesPerLine; j++)
                {
                    if (i + j < length)
                    {
                        sb.Append($"{bytes[offset + i + j]:X2} ");
                    }
                    else
                    {
                        sb.Append("   ");
                    }

                    if (j == 7) sb.Append(" ");
                }

                sb.Append(" |");

                // ASCII representation
                for (int j = 0; j < bytesPerLine && i + j < length; j++)
                {
                    byte b = bytes[offset + i + j];
                    sb.Append(b >= 32 && b < 127 ? (char)b : '.');
                }

                sb.Append("|");

                if (i + bytesPerLine < length)
                {
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }



        /// <summary>
        /// Sends periodic tick updates (0x65) to client with current position
        /// This keeps the client synchronized with server state
        /// </summary>
        /// <summary>
        /// Sends periodic tick updates to client with current position + RNG seed.
        /// Binary-proven: ServerEntityManager at 0x5DEB90 ticks every 33ms (B14=0x21).
        /// Every 4th tick (B10=3), function 0x5DF010 fires which:
        ///   1. Writes opcode 0x0C + timeGetTime() seed to stream
        ///   2. Seeds local EntityManager RNG (EM+0x44) with same value
        ///   3. Calls writeUpdateMessages (0x36 HP sync)
        ///   4. Calls writeInitMessages
        /// The 0x0C seed and HP sync are COUPLED — they go in the same stream.
        /// </summary>
        private IEnumerator DelayedGnomeSpawn(RRConnection conn, string zoneName, uint zoneId, uint instanceId)
        {
            yield return new WaitForSeconds(0.5f);

            if (conn == null || !conn.IsConnected) yield break;
            if (!string.Equals(conn.CurrentZoneName, zoneName, StringComparison.OrdinalIgnoreCase)) yield break;
            if (conn.CurrentZoneId != zoneId || conn.InstanceId != instanceId) yield break;
            if (BlingGnomeManager.Instance.HasGnome(conn.ConnId)) yield break;

            Debug.LogError($"[ZONE-IN] Delayed BlingGnome spawn firing for {conn.LoginName}");
            BlingGnomeManager.Instance.SetServer(this);
            BlingGnomeManager.Instance.SpawnGnome(conn,
                (c, d, t, b) => SendCompressedA(c, d, t, b),
                (c, m) => SendSystemMessage(c, m));
        }

        private IEnumerator SendTickUpdates(RRConnection conn, uint unitBehaviorId)
        {
            Debug.LogError($"[TICK] Starting for UnitBehavior 0x{unitBehaviorId:X4}");
            conn.UnitBehaviorId = unitBehaviorId;
            Debug.LogError($"[TICK] Using position ({conn.PlayerPosX}, {conn.PlayerPosY})");

            int tickCount = 0;

            while (conn != null && conn.IsConnected)
            {
                tickCount++;


                PlayerState tickState = GetPlayerState(conn.ConnId.ToString());
                uint tickPlayerEntityId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0;
                bool canAdvancePlayerHP = tickState != null
                    && CanAdvancePlayerClientSyncHP(tickPlayerEntityId);
                if (canAdvancePlayerHP)
                    tickState.AdvanceClientSyncHP(GetNativeCombatNow(), "ServerTick");

                if (_adminHandler != null && canAdvancePlayerHP && _adminHandler.IsRegenActive(conn.ConnId))
                {
                    tickState.RunRegenTick();
                    if (tickState.IsRegenComplete)
                    {
                        _adminHandler.ClearRegenFlag(conn.ConnId);
                    }
                }

                if (tickCount % 4 == 0)
                {
                    var tickWriter = new LEWriter();
                    tickWriter.WriteByte(0x07);
                    tickWriter.WriteByte(0x0D);
                    tickWriter.WriteUInt32((uint)tickCount);
                    tickWriter.WriteUInt32(0x21);
                    tickWriter.WriteUInt32(0x03);
                    tickWriter.WriteUInt32(0x01);
                    tickWriter.WriteUInt16(100);
                    tickWriter.WriteUInt16(20);
                    TryWritePendingLocalPlayerMovementAck(conn, tickWriter);

                    tickWriter.WriteByte(0x06);
                    SendCompressedA(conn, 0x01, 0x0F, tickWriter.ToArray(), SyncContext.WorldInterval, "WorldInterval");
                }

                // STEP 6: MULTIPLAYER — Fallback position broadcast every ~500ms
                // Only catches movement the direct relay misses (mid-pathfind, knockback)
                // During force relay (obelisk click), broadcast every ~100ms to catch pathfind arrival
                bool isForceRelay = _forceRelayUntil.ContainsKey(conn.ConnId) && Time.time < _forceRelayUntil.GetValueOrDefault(conn.ConnId);
                int relayInterval = isForceRelay ? 3 : 15;
                if (tickCount % relayInterval == 0)
                {
                    BroadcastPlayerMovement(conn, conn.SessionID, 0, null);
                }

                yield return new WaitForSeconds(0.033f);
            }

            Debug.LogError($"[TICK] STOPPED after {tickCount} ticks");
        }









        private void ReassignEntityIDs(GCObject player)
        {
            player.Id = _nextEntityId++;

            foreach (var child in player.Children)
            {
                child.Id = _nextEntityId++;

                if (child.Children != null)
                {
                    foreach (var grandchild in child.Children)
                    {
                        grandchild.Id = _nextEntityId++;
                    }
                }
            }
        }

        private uint GetClientId24(int connId)
        {
            return _peerId24.TryGetValue(connId, out var id) ? id : 0u;
        }

        private void SendSocialViaAuth(RRConnection conn, byte dest, byte messageType, byte[] innerData)
        {
            // Chat (channel 0x06) uses SendCompressedA and works.
            // Social (channel 0x03) must use the same path.
            // Inner data format: [0x03][socialType][data] — channel 3 routes to UserManagerClient.
            // 0x0E (CompressedE) is CLIENT→SERVER only. A-lane is SERVER→CLIENT.
            SendCompressedA(conn, dest, messageType, innerData);
        }

        private void SendSocialViaQueue(RRConnection conn, byte dest, byte messageType, byte[] innerData)
        {
            SendCompressedA(conn, dest, messageType, innerData);
        }

        private void OnQueueStreamReady(string username)
        {
            foreach (var conn in _connections.Values)
            {
                if (conn.IsConnected && conn.LoginName != null &&
                    conn.LoginName.Equals(username, StringComparison.OrdinalIgnoreCase))
                {
                    if (_selectedCharacter.TryGetValue(conn.LoginName, out var sc) && sc.Name != null)
                    {
                        Debug.LogError($"[QUEUE-BRIDGE] Queue ready for {username} — resending social init");
                        SocialManager.Instance.SendLoginSocialInit(conn, sc.Name, SendSocialViaAuth);
                        PosseManager.Instance.SendConnectionNotification(conn, true, SendSocialViaAuth);
                        SendPosseStateForCharacter(conn, sc.Id, SendSocialViaAuth);
                        try { PosseManager.Instance.NotifyMemberStateChange(sc.Id, this); }
                        catch (Exception ex2) { Debug.LogError($"[POSSE] login-notify (OnQueueStreamReady) failed: {ex2.Message}"); }
                    }
                    break;
                }
            }
        }

        private void SendCompressedE(RRConnection conn, byte[] innerData)
        {
            try
            {
                if (conn == null || innerData == null || innerData.Length == 0)
                {
                    Debug.LogError("[SEND-COMPRESSEDE] dropped empty packet");
                    return;
                }
                byte[] compressed = ZlibUtil.Deflate(innerData);
                int compressedLen = compressed.Length + 12;

                var writer = new LEWriter();
                writer.WriteByte(0x0E);
                writer.WriteUInt24((int)MSG_DEST);
                writer.WriteUInt24(compressedLen);
                writer.WriteByte(0x00);
                writer.WriteUInt24((int)MSG_SOURCE);
                writer.WriteByte(0x01);
                writer.WriteByte(0x00);
                writer.WriteByte(0x01);
                writer.WriteByte(0x00);
                writer.WriteByte(0x00);
                writer.WriteUInt32((uint)innerData.Length);
                writer.WriteBytes(compressed);

                byte[] data = writer.ToArray();
                lock (conn.SendLock)
                {
                    conn.Stream.Write(data, 0, data.Length);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"SendCompressedE error: {ex.Message}");
            }
        }



        private void SendCompressedA(RRConnection conn, byte dest, byte messageType, byte[] innerData)
        {
            SendCompressedA(conn, dest, messageType, innerData, InferSendCompressedASyncContext(conn, innerData), "SEND-COMPRESSEDA");
        }

        private SyncContext InferSendCompressedASyncContext(RRConnection conn, byte[] innerData)
        {
            if (innerData == null || innerData.Length < 2) return SyncContext.PlayerActionResponse;
            if (innerData[0] == 0x07 && innerData[1] == 0x0D) return SyncContext.WorldInterval;
            if (innerData[0] == 0x07 && (innerData[1] == 0x01 || innerData[1] == 0x02 || innerData[1] == 0x32)) return SyncContext.EntityInitPrimer;
            for (int i = 0; i + 3 < innerData.Length; i++)
            {
                if (innerData[i] != 0x35) continue;
                ushort cid = (ushort)(innerData[i + 1] | (innerData[i + 2] << 8));
                byte subtype = innerData[i + 3];
                if (subtype == 0x64)
                    return IsAvatarOrAvatarComponentId(conn, cid) ? SyncContext.ControlAck : SyncContext.MonsterAction;
                if (subtype == 0x65)
                    return IsAvatarOrAvatarComponentId(conn, cid) ? SyncContext.MoverAck : SyncContext.MonsterMove;
                if (subtype == 0x04) return SyncContext.MonsterAction;
                if (subtype == 0x01) return SyncContext.PlayerActionResponse;
            }
            return SyncContext.PlayerActionResponse;
        }

        private bool SendCompressedA(RRConnection conn, byte dest, byte messageType, byte[] innerData, SyncContext syncContext, string packetName)
        {
            try
            {
                if (conn == null || innerData == null || innerData.Length == 0)
                {
                    Debug.LogError($"[SEND-COMPRESSEDA] dropped empty packet dest=0x{dest:X2} type=0x{messageType:X2}");
                    return false;
                }
                if (dest == 0x01 && messageType == 0x0F)
                {
                    if (TryNormalizeCH07RuntimeStream(conn, innerData, syncContext, packetName, out byte[] normalizedInnerData, out bool dropNormalized))
                    {
                        if (dropNormalized)
                            return false;
                        innerData = normalizedInnerData;
                    }
                }

                byte[] compressed = ZlibUtil.Deflate(innerData);
                uint peer = GetClientId24(conn.ConnId);
                var writer = new LEWriter();
                writer.WriteByte(0x0A);
                writer.WriteUInt24((int)(peer & 0xFFFFFFu));
                writer.WriteUInt32((uint)(compressed.Length + 7));
                writer.WriteByte(dest);
                writer.WriteByte(messageType);
                writer.WriteByte(0x00);
                writer.WriteUInt32((uint)innerData.Length);
                writer.WriteBytes(compressed);
                byte[] data = writer.ToArray();
                lock (conn.SendLock)
                {
                    conn.Stream.Write(data, 0, data.Length);
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"SendCompressedA error: {ex.Message}");
                return false;
            }
        }

        /// Public wrapper for handlers to send messages to client

        public void SendToClient(RRConnection conn, byte[] data)
        {
            SendCompressedA(conn, 0x01, 0x0F, data);
        }






        private static int GetIntSafe(System.Data.IDataReader r, string col, int def)
        {
            try { for (int i = 0; i < r.FieldCount; i++) if (r.GetName(i) == col) return r.GetInt32(i); } catch { }
            return def;
        }

        [System.Serializable]
        public class Zone
        {
            public uint id;
            public string name;
            public string gcType;
            public float spawnX;
            public float spawnY;
            public float spawnZ;
            public float spawnHeading;
            public string respawnZone;
            public int exploredBitCount;  // 0 = use default, >0 = exact value from DB
            public uint Id => id;
            public string Name => name;
        }

        [System.Serializable]
        public class ZoneList
        {
            public List<Zone> zones;

        }
        private List<ActiveQuest> ConvertToActiveQuests(List<SavedQuest> saved)
        {
            if (saved == null) return new List<ActiveQuest>();
            return saved.Select(sq => new ActiveQuest
            {
                QuestId = sq.questId,
                QuestGiverId = sq.questGiverId,
                AcceptedAt = DateTime.TryParse(sq.acceptedAt, out var dt) ? dt : DateTime.UtcNow,
                Objectives = sq.objectives?.Select(o => new QuestProgress
                {
                    ObjectiveName = o.objectiveName,
                    Type = o.type,
                    Target = o.target,
                    Label = o.label,
                    Required = o.required,
                    Current = o.current
                }).ToList() ?? new List<QuestProgress>()
            }).ToList();
        }


        // ═══════════════════════════════════════════════════════════════
        // GROUP SYSTEM HELPERS — used by ChatCommandHandler
        // ═══════════════════════════════════════════════════════════════

        public Dictionary<int, RRConnection> GetConnections() => _connections;

        public List<string> GetZoneNames()
        {
            return _zones.Values.Select(z => z.name).OrderBy(n => n).ToList();
        }

        public SavedCharacter GetSavedCharacterForConn(RRConnection conn)
        {
            if (conn == null || string.IsNullOrEmpty(conn.LoginName)) return null;
            if (_selectedCharacter.TryGetValue(conn.LoginName, out var gcObj))
                return CharacterRepository.GetCharacter((uint)gcObj.Id);
            return null;
        }

        /// <summary>
        /// Grants a skill to a connected player at runtime without requiring
        /// logout/rezone. Replicates the exact pattern used by the skill
        /// trainer (HandleTrainSkill) — persists to DB, adds to the
        /// Manipulators DFC tree in memory, and sends the hot-add packets
        /// (0x32 processUpdateSkill on the Skills component + optional 0xDF
        /// UDP to DSOUND.dll for client UI refresh).
        ///
        /// Returns true if the skill was newly granted, false if the player
        /// already had it or any error occurred.
        /// </summary>
        public bool GrantSkillRuntime(RRConnection conn, string skillGcClass)
        {
            if (conn == null || string.IsNullOrEmpty(skillGcClass)) return false;

            var savedChar = GetSavedCharacterForConn(conn);
            if (savedChar == null)
            {
                Debug.LogError($"[GRANT-SKILL] No saved character for {conn.LoginName}");
                return false;
            }

            string connKey = conn.ConnId.ToString();

            // Already has it?
            bool alreadyHas = false;
            if (_playerSkillLevels.TryGetValue(connKey, out var existingLevels))
                alreadyHas = existingLevels.ContainsKey(skillGcClass);
            if (!alreadyHas && savedChar.skills != null)
                alreadyHas = savedChar.skills.Any(s => string.Equals(s, skillGcClass, StringComparison.OrdinalIgnoreCase));

            if (alreadyHas)
            {
                Debug.LogError($"[GRANT-SKILL] {conn.LoginName} already has {skillGcClass}");
                return false;
            }

            // ── Persist to DB ──
            if (savedChar.skills == null)
                savedChar.skills = new List<string>();
            savedChar.skills.Add(skillGcClass);
            savedChar.SetSkillLevel(skillGcClass, 1);
            CharacterRepository.SaveCharacter(savedChar);

            // ── Update runtime skill level tracking ──
            if (!_playerSkillLevels.ContainsKey(connKey))
                _playerSkillLevels[connKey] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _playerSkillLevels[connKey][skillGcClass] = 1;
            int lastDot = skillGcClass.LastIndexOf('.');
            if (lastDot >= 0)
                _playerSkillLevels[connKey][skillGcClass.Substring(lastDot + 1)] = 1;

            // ── Hot-add: Skills component 0x32 processUpdateSkill ──
            // Client: Skills::processUpdate @ 0x541D60. subMsg 0x32 not-found
            // path creates new Skill via createInstance() then calls vtable[0x54]
            // (DFCNode::addNode) — same pattern Avatar::checkSkills uses at
            // startup (@ 0x4ED5E2 "Adding new skill object" log) which IS
            // proven to work. Wire format per 0x541F40:
            //   0x32 [0xFF + CString skill_class] [level_byte]
            ushort skillsCid = 0;
            _playerSkillsComponentId.TryGetValue(connKey, out skillsCid);

            // ── Diagnostic dump — every piece of state that could be wrong ──
            Debug.LogError($"[GRANT-SKILL-DIAG] ═══════════════════════════════");
            Debug.LogError($"[GRANT-SKILL-DIAG] connKey='{connKey}' LoginName='{conn.LoginName}'");
            Debug.LogError($"[GRANT-SKILL-DIAG] skillsCid=0x{skillsCid:X4} ({skillsCid})");
            Debug.LogError($"[GRANT-SKILL-DIAG] UnitContainerId=0x{conn.UnitContainerId:X4}");
            Debug.LogError($"[GRANT-SKILL-DIAG] Avatar.Id={conn.Avatar?.Id ?? 0}");
            Debug.LogError($"[GRANT-SKILL-DIAG] skillGcClass raw='{skillGcClass}' len={skillGcClass.Length}");
            Debug.LogError($"[GRANT-SKILL-DIAG] skillGcClass bytes: {BitConverter.ToString(System.Text.Encoding.ASCII.GetBytes(skillGcClass))}");
            if (conn.Avatar?.Children != null)
            {
                var skillsNode = conn.Avatar.Children.FirstOrDefault(c => c.GCClass == "avatar.base.skills");
                Debug.LogError($"[GRANT-SKILL-DIAG] avatar.base.skills node Id={skillsNode?.Id ?? 0} (must match skillsCid)");
            }

            if (skillsCid != 0)
            {
                // ── Combined 0x33+0x32 packet — EXACTLY matches trainer's
                //    working format (line 10253-10276). Bare 0x32 silently
                //    fails in the NOT-FOUND add path because Skills+0x70
                //    (gold) must be written first for initialize() to run
                //    correctly. The 0x33 sub-msg writes Skills+0x70 and
                //    fires event 0x141F, then 0x32 can safely add the new
                //    Skill via vtable[0x54] (DFCNode::addNode).
                uint currentGold = 0;
                try
                {
                    var sc = GetSavedCharacterForConn(conn);
                    if (sc != null) currentGold = sc.gold;
                }
                catch { }

                var w = new LEWriter();
                w.WriteByte(0x07);                      // BeginStream (ONE stream for both sub-msgs)

                // ── Part 1: 0x33 processUpdateSkillPoints (gold) — primes initialize() ──
                w.WriteByte(0x35);                      // ComponentUpdate
                w.WriteUInt16(skillsCid);
                w.WriteByte(0x33);                      // subMessage = gold/points
                w.WriteUInt32(currentGold);             // current gold (unchanged)
                WritePlayerEntitySynch(conn, w);

                // ── Part 2: 0x32 processUpdateSkill (add new skill) ──
                w.WriteByte(0x35);                      // ComponentUpdate
                w.WriteUInt16(skillsCid);
                w.WriteByte(0x32);                      // subMessage = level/add
                w.WriteByte(0xFF);                      // entity ref = name-based
                w.WriteCString(skillGcClass);           // "skills.generic.SummonBlingGnome"
                w.WriteByte(0x01);                      // new level = 1
                WritePlayerEntitySynch(conn, w);

                w.WriteByte(0x06);                      // EndStream

                byte[] pkt = w.ToArray();
                Debug.LogError($"[GRANT-SKILL-HEX] ({pkt.Length}b) {BitConverter.ToString(pkt)}");
                Debug.LogError($"[GRANT-SKILL-ANNOT] 07 | 35 cid=0x{skillsCid:X4} 33 gold={currentGold} 00 | 35 cid=0x{skillsCid:X4} 32 FF \"{skillGcClass}\"+00 level=1 00 | 06");

                SendCompressedE(conn, pkt);
                Debug.LogError($"[GRANT-SKILL] Sent combined 0x33+0x32 (trainer-shape) for '{skillGcClass}'");
            }
            else
            {
                Debug.LogError($"[GRANT-SKILL] ⚠️ skillsCid=0 — Skills component ID not captured. Skill saved to DB but won't appear until zone.");
            }

            // ── Add to Manipulators in-memory so HandleSelfCastSpell can resolve it ──
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
                    Debug.LogError($"[GRANT-SKILL] Added '{skillGcClass}' to Manipulators, eid={newSkill.Id}");
                }
            }

            // ── 0xDF UDP to DSOUND.dll proxy for UI refresh (skill icon hotbar) ──
            try
            {
                var tcpEP = conn.Client.Client.RemoteEndPoint as System.Net.IPEndPoint;
                if (tcpEP != null)
                {
                    byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(skillGcClass);
                    byte[] dfPacket = new byte[10 + nameBytes.Length];
                    dfPacket[0] = 0xDF;
                    ushort skillEntityIdForDll = 0;
                    if (_playerSkillSlots.TryGetValue(connKey, out var dllSlots))
                    {
                        uint eid = 0;
                        if (dllSlots.TryGetValue(skillGcClass, out eid))
                            skillEntityIdForDll = (ushort)eid;
                    }
                    BitConverter.GetBytes(skillEntityIdForDll).CopyTo(dfPacket, 1);
                    BitConverter.GetBytes(savedChar.gold).CopyTo(dfPacket, 3);
                    dfPacket[7] = 1;   // level
                    dfPacket[8] = 1;   // newly learned
                    dfPacket[9] = (byte)nameBytes.Length;
                    Array.Copy(nameBytes, 0, dfPacket, 10, nameBytes.Length);
                    SendToDll(conn, dfPacket);
                    Debug.LogError($"[GRANT-SKILL] Sent 0xDF DLL packet for '{skillGcClass}'");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[GRANT-SKILL] 0xDF DLL send failed: {ex.Message}");
            }

            Debug.LogError($"[GRANT-SKILL] ✅ Granted '{skillGcClass}' to {conn.LoginName}");
            return true;
        }

        public RRConnection FindConnectionByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            // First try character name (who-list and context menu use character names)
            foreach (var kvp in _selectedCharacter)
            {
                if (kvp.Value.Name != null && kvp.Value.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    var conn = _connections.Values.FirstOrDefault(c =>
                        c.IsConnected && c.LoginName == kvp.Key);
                    if (conn != null) return conn;
                }
            }
            // Fall back to login name
            return _connections.Values.FirstOrDefault(c =>
                c.IsConnected && !string.IsNullOrEmpty(c.LoginName) &&
                c.LoginName.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        public RRConnection FindConnectionById(int connId)
        {
            _connections.TryGetValue(connId, out var conn);
            return conn;
        }

        // Public wrappers for ChatCommandHandler membership commands
        public void SetPlayerMembershipPublic(string loginName, bool isFree) { SetPlayerMembership(loginName, isFree); }
        public bool IsPlayerFreePublic(string loginName) { return IsPlayerFree(loginName); }
        public Dictionary<string, bool> GetAllMembershipsPublic()
        {
            var result = new Dictionary<string, bool>(System.StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var db = Database.GameDatabase.GetConnection())
                using (var reader = Database.GameDatabase.ExecuteReader(db, "SELECT username, is_member FROM accounts"))
                {
                    while (reader.Read())
                        result[reader.GetString(0)] = reader.GetInt32(1) == 0;  // is_member=0 → free=true
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[MEMBER] GetAll error: {ex.Message}");
            }
            return result;
        }

        // Public wrappers for ChatCommandHandler group commands
        public uint GetCharSqlIdPublic(RRConnection conn) => GetCharSqlId(conn);
    }
}
