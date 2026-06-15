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

//using UnityEditor.Experimental.GraphView;
//using UnityEditor.SceneManagement;

namespace DungeonRunners.Networking
{
    public enum SyncContext
    {
        Unknown,
        WorldInterval,
        BootstrapReplay,
        RecoveryReplay,
        RepeatResync,
        InventoryReplay,
        EquipmentReplay,
        LateArmorSync,
        ControlGrant,
        ControlAck,
        MoverAck,
        PlayerActionResponse,
        PlayerBasicAttackResponse,
        MonsterAction,
        MonsterMove,
        MonsterDamage,
        EntityInitPrimer
    }

    public partial class GameServer : MonoBehaviour
    {
        private const uint MSG_DEST = 0x000F01;
        private const uint MSG_SOURCE = 0x000F01;
        private static bool VerbosePacketLogging => ServerSettings.GetBool("verbosePacketLogging", false);
        private bool _allowFlush = false;
        private UdpClient _udpListener;
        private bool _isRunning;
        private int _udpPort = 2603; // Overridden by ServerSettings in Start()
        private IPEndPoint _clientUDPEndpoint;
        private Dictionary<string, System.Net.IPEndPoint> _dllEndpoints = new Dictionary<string, System.Net.IPEndPoint>();
        private string GetEndpointKey(IPEndPoint ep) => $"{ep.Address}:{ep.Port}";
        private Dictionary<string, float> _useTargetApproachLogTimes = new Dictionary<string, float>();
        private int _nextConnId = 1;
        private Dictionary<int, string> _users = new Dictionary<int, string>();
        private Dictionary<int, uint> _peerId24 = new Dictionary<int, uint>();
        private Dictionary<string, List<GCObject>> _persistentCharacters = new Dictionary<string, List<GCObject>>();
        private Dictionary<int, bool> _charListSent = new Dictionary<int, bool>();
        private Dictionary<string, GCObject> _selectedCharacter = new Dictionary<string, GCObject>();  // first Increment yields 0xC000
        private const ushort LOOT_ID_MIN = 0xC000;
        private const ushort LOOT_ID_MAX = 0xFDFF;
        private HashSet<string> _freePlayerModifierSent = new HashSet<string>();
        private ModifierTracker _modifierTracker = new ModifierTracker();

        private Dictionary<string, bool> _playerIsFree = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, bool> _playerIsAdmin = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        // ═══ MULTIPLAYER: Per-viewer remote behavior ID tracking ═══
        private Dictionary<string, Dictionary<string, ushort>> _remoteBehaviorIds = new Dictionary<string, Dictionary<string, ushort>>();
        private Dictionary<string, Dictionary<string, ushort>> _remoteAvatarIds = new Dictionary<string, Dictionary<string, ushort>>();
        private Dictionary<ushort, ChestSpawnData> _chestEntities = new Dictionary<ushort, ChestSpawnData>();

        private AdminCommandHandler _adminHandler;
        private const float NATIVE_CONTACT_RANGE_EPSILON = 1f / 16f;
        private const float NATIVE_ACTION_TIMING_EPSILON = 0.15f;
        private const int NativeStatPointsPerLevel = 5;
        private const float MONSTER_MOVE_SEND_INTERVAL = 0.15f;
        private Dictionary<uint, uint> _monsterBehaviorIds = new Dictionary<uint, uint>();
        private Dictionary<uint, float> _lastMonsterMoveSentAt = new Dictionary<uint, float>();
        private Dictionary<uint, PendingMonsterBehaviorUpdate> _pendingMonsterBehaviorUpdates = new Dictionary<uint, PendingMonsterBehaviorUpdate>();
        private InventoryHandler _inventoryHandler;
        private EquipmentHandler _equipmentHandler;
        private Dictionary<string, PlayerState> _playerStates = new Dictionary<string, PlayerState>();
        // Track equipped items per player - EXACT Go pattern (Equipment.Children)
        private Dictionary<string, Dictionary<uint, GCObject>> _playerEquippedItems = new Dictionary<string, Dictionary<uint, GCObject>>();
        private Dictionary<string, ushort> _playerManipulatorsIds = new Dictionary<string, ushort>();
        private Dictionary<string, float> _useTargetResponseTimes = new Dictionary<string, float>();
        private const float DROPPED_ITEM_CLEANUP_INTERVAL = 60f; // check every 60 seconds
        private const int DROPPED_ITEM_EXPIRE_MINUTES = 30;
        private Dictionary<string, Dictionary<uint, (GCObject item, byte x, byte y)>> _playerInventoryItems = new Dictionary<string, Dictionary<uint, (GCObject, byte, byte)>>();
        private Dictionary<string, HashSet<int>> _occupiedInventorySlots = new Dictionary<string, HashSet<int>>();
        private Dictionary<string, uint> _inventorySlotCounters = new Dictionary<string, uint>();
        private const float TICK_INTERVAL = 0.033f;
        private const float AUTO_SAVE_INTERVAL = 30f;

        private float _merchantRefreshTimer = 0f;
        private const float MERCHANT_REFRESH_INTERVAL = 0.10f;
        private const float MERCHANT_ACTIVATION_DISTANCE_SQ = 900f;

        private static bool IsBankContainer(byte containerId)
            => containerId == 0x0C || (containerId >= 0x0E && containerId <= 0x13);

        private static int ContainerHeight(byte containerId) => IsBankContainer(containerId) ? 14 : 8;
        private const int CONTAINER_WIDTH = 10;

        public void TrackInventoryItem(string connId, uint index, GCObject item, byte x, byte y, byte containerId = 0x0B)
        {
            string key = InvKey(connId, containerId);
            if (!_playerInventoryItems.ContainsKey(key))
                _playerInventoryItems[key] = new Dictionary<uint, (GCObject, byte, byte)>();
            _playerInventoryItems[key][index] = (item, x, y);
            Debug.LogError($"[INV-TRACK] Player {connId} container=0x{containerId:X2}: Index {index} = {item.GCClass} at ({x}, {y})");
        }

        public (int x, int y) FindNextFreeInventorySlot(string connId, int width, int height, byte containerId = 0x0B)
        {
            int invHeight = ContainerHeight(containerId);

            for (byte y = 0; y <= invHeight - height; y++)
            {
                for (byte x = 0; x <= CONTAINER_WIDTH - width; x++)
                {
                    if (!IsInventorySlotOccupied(connId, x, y, width, height, containerId))
                    {
                        return (x, y);
                    }
                }
            }
            return (-1, -1);  // Inventory full
        }


        private Dictionary<string, Dictionary<uint, int>> _inventoryStackCounts = new Dictionary<string, Dictionary<uint, int>>();

        public int GetStackCount(string connId, uint slot, byte containerId = 0x0B)
        {
            string key = InvKey(connId, containerId);
            if (_inventoryStackCounts.ContainsKey(key) && _inventoryStackCounts[key].ContainsKey(slot))
                return _inventoryStackCounts[key][slot];
            return 1;
        }

        public void SetStackCount(string connId, uint slot, int count, byte containerId = 0x0B)
        {
            string key = InvKey(connId, containerId);
            if (!_inventoryStackCounts.ContainsKey(key))
                _inventoryStackCounts[key] = new Dictionary<uint, int>();
            _inventoryStackCounts[key][slot] = count;
        }

        public void RemoveEquippedItem(string connId, uint slot)
        {
            if (_playerEquippedItems.ContainsKey(connId) && _playerEquippedItems[connId].ContainsKey(slot))
            {
                _playerEquippedItems[connId].Remove(slot);
                Debug.LogError($"[EQUIP-TRACK] Removed item from slot {slot}");
            }
        }

        public uint GetNextInventorySlot(string playerId)
        {
            if (!_inventorySlotCounters.ContainsKey(playerId))
            {
                _inventorySlotCounters[playerId] = 100; // Start at 100 to avoid equipment slot conflicts
            }

            uint slot = _inventorySlotCounters[playerId];
            _inventorySlotCounters[playerId]++;

            Debug.LogError($"[INV-SLOT] Generated new inventory slot {slot} for player {playerId}");
            return slot;
        }







        public (GCObject item, byte x, byte y)? GetAndRemoveInventoryItem(string connId, uint index, byte containerId = 0x0B)
        {
            string key = InvKey(connId, containerId);
            if (_playerInventoryItems.ContainsKey(key) && _playerInventoryItems[key].ContainsKey(index))
            {
                var data = _playerInventoryItems[key][index];
                _playerInventoryItems[key].Remove(index);

                // Get item dimensions from database
                ItemData itemData = DatabaseLoader.FindItem(data.item.GCClass);
                int width = itemData?.inventoryWidth ?? 1;
                int height = itemData?.inventoryHeight ?? 1;

                // Free ALL slots this item occupied
                FreeInventorySlots(connId, data.x, data.y, width, height, containerId);
                return data;
            }
            Debug.LogError($"[INV-TRACK] ❌ No item at index {index} in container 0x{containerId:X2}!");
            return null;
        }





        public bool IsInventorySlotOccupied(string connId, byte x, byte y, int width, int height, byte containerId = 0x0B)
        {
            int invHeight = ContainerHeight(containerId);
            // Bounds check
            if (x + width > CONTAINER_WIDTH || y + height > invHeight)
                return true;  // treat out-of-bounds as occupied

            string key = InvKey(connId, containerId);
            if (!_occupiedInventorySlots.ContainsKey(key))
                return false;

            // Check ALL cells the item would occupy
            for (int dx = 0; dx < width; dx++)
            {
                for (int dy = 0; dy < height; dy++)
                {
                    int slotIndex = (y + dy) * CONTAINER_WIDTH + (x + dx);
                    if (_occupiedInventorySlots[key].Contains(slotIndex))
                    {
                        Debug.LogError($"[INV-TRACK] ❌ Cell ({x + dx}, {y + dy}) in container 0x{containerId:X2} is already occupied!");
                        return true;
                    }
                }
            }
            return false;
        }

        public void OccupyInventorySlots(string connId, byte x, byte y, int width, int height, byte containerId = 0x0B)
        {
            string key = InvKey(connId, containerId);
            if (!_occupiedInventorySlots.ContainsKey(key))
                _occupiedInventorySlots[key] = new HashSet<int>();

            for (int dx = 0; dx < width; dx++)
            {
                for (int dy = 0; dy < height; dy++)
                {
                    int slotIndex = (y + dy) * CONTAINER_WIDTH + (x + dx);
                    _occupiedInventorySlots[key].Add(slotIndex);
                }
            }
            Debug.LogError($"[INV-TRACK] ✅ Occupied {width}x{height} slots starting at ({x}, {y}) in container 0x{containerId:X2}");
        }

        public void FreeInventorySlots(string connId, byte x, byte y, int width, int height, byte containerId = 0x0B)
        {
            string key = InvKey(connId, containerId);
            if (!_occupiedInventorySlots.ContainsKey(key))
                return;

            for (int dx = 0; dx < width; dx++)
            {
                for (int dy = 0; dy < height; dy++)
                {
                    int slotIndex = (y + dy) * CONTAINER_WIDTH + (x + dx);
                    _occupiedInventorySlots[key].Remove(slotIndex);
                }
            }
            Debug.LogError($"[INV-TRACK] ✅ Freed {width}x{height} slots starting at ({x}, {y}) in container 0x{containerId:X2}");
        }

        public (uint slot, GCObject item, byte x, byte y)? FindInventoryItemByGCClass(string connId, string gcClass, byte containerId = 0x0B)
        {
            string key = InvKey(connId, containerId);
            if (!_playerInventoryItems.ContainsKey(key)) return null;
            string gcLower = gcClass.ToLower();
            foreach (var kvp in _playerInventoryItems[key])
            {
                if (kvp.Value.Item1.GCClass.ToLower() == gcLower)
                    return (kvp.Key, kvp.Value.Item1, kvp.Value.Item2, kvp.Value.Item3);
            }
            return null;
        }
        private void SavePlayerInventory(RRConnection conn)
        {
            if (conn == null || string.IsNullOrEmpty(conn.LoginName)) return;
            string connId = conn.ConnId.ToString();
            if (!_selectedCharacter.ContainsKey(conn.LoginName)) return;
            var savedChar = CharacterRepository.GetCharacter(_selectedCharacter[conn.LoginName].Id);
            if (savedChar == null) return;
            savedChar.inventory.Clear();

            // Iterate every container: main inv (0x0B) and the 7 bank pages.
            // Items are tagged with containerId so LoadInventory restores them to the right container.
            byte[] saveContainers = { 0x0B, 0x0C, 0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13 };
            foreach (byte cid in saveContainers)
            {
                string dictKey = InvKey(connId, cid);
                if (!_playerInventoryItems.ContainsKey(dictKey)) continue;
                foreach (var kvp in _playerInventoryItems[dictKey])
                {
                    int count = GetStackCount(connId, kvp.Key, cid);
                    // Position conflicts only apply within the same container.
                    var posConflict = savedChar.inventory.Find(i =>
                        i.containerId == cid &&
                        i.x == kvp.Value.Item2 &&
                        i.y == kvp.Value.Item3);
                    if (posConflict != null)
                    {
                        if (count > posConflict.count)
                        {
                            posConflict.gcClass = kvp.Value.Item1.GCClass;
                            posConflict.count = count;
                        }
                        uint bpConflict = DungeonRunners.Managers.MerchantManager.GetBuyPrice(connId, kvp.Value.Item1.GCClass);
                        if (bpConflict > 0) posConflict.buyPrice = bpConflict;
                    }
                    else
                    {
                        string bpGcClass = kvp.Value.Item1.GCClass;
                        uint bp = DungeonRunners.Managers.MerchantManager.GetBuyPrice(connId, bpGcClass);
                        savedChar.inventory.Add(new SavedInventoryItem
                        {
                            gcClass = bpGcClass,
                            x = kvp.Value.Item2,
                            y = kvp.Value.Item3,
                            count = count,
                            buyPrice = bp,
                            rarity = kvp.Value.Item1.GetEffectiveRarity(),
                            storedLevel = kvp.Value.Item1.StoredLevel,
                            containerId = cid
                        });
                    }
                }
            }

            // Save active item (on cursor) back to inventory so it's not lost
            PlayerState ps = GetPlayerState(connId);
            if (ps?.ActiveItem != null)
            {
                byte ax = 0, ay = 0;
                bool foundActive = false;
                int aw = 1, ah = 1;
                for (byte ry = 0; ry < 8 && !foundActive; ry++)
                    for (byte cx = 0; cx < 10 && !foundActive; cx++)
                        if (!IsInventorySlotOccupied(connId, cx, ry, aw, ah))
                        { ax = cx; ay = ry; foundActive = true; }
                if (foundActive)
                {
                    savedChar.inventory.Add(new SavedInventoryItem
                    {
                        gcClass = ps.ActiveItem.GCClass,
                        x = ax,
                        y = ay,
                        count = 1,
                        rarity = ps.ActiveItem.GetEffectiveRarity(),
                        storedLevel = ps.ActiveItem.StoredLevel
                    });
                    Debug.LogError($"[SAVE] Saved cursor item {ps.ActiveItem.GCClass} to inventory at ({ax},{ay})");
                }
                ps.ActiveItem = null;
            }

            // Sync equipment from tracking to savedChar
            if (_playerEquippedItems.ContainsKey(connId))
            {
                var eq = _playerEquippedItems[connId];
                savedChar.equipment.weapon = eq.ContainsKey(10) ? eq[10].GCClass : "";
                savedChar.equipment.armor = eq.ContainsKey(6) ? eq[6].GCClass : "";
                savedChar.equipment.helmet = eq.ContainsKey(5) ? eq[5].GCClass : "";
                savedChar.equipment.gloves = eq.ContainsKey(2) ? eq[2].GCClass : "";
                savedChar.equipment.boots = eq.ContainsKey(7) ? eq[7].GCClass : "";
                savedChar.equipment.shoulders = eq.ContainsKey(8) ? eq[8].GCClass : "";
                savedChar.equipment.shield = eq.ContainsKey(11) ? eq[11].GCClass : "";
                savedChar.equipment.ring1 = eq.ContainsKey(3) ? eq[3].GCClass : "";
                savedChar.equipment.ring2 = eq.ContainsKey(4) ? eq[4].GCClass : "";
                savedChar.equipment.amulet = eq.ContainsKey(1) ? eq[1].GCClass : "";
                // Sync rarity from tracked GCObjects
                if (eq.ContainsKey(10)) savedChar.equipment.slotRarity["weapon"] = eq[10].GetEffectiveRarity();
                if (eq.ContainsKey(6)) savedChar.equipment.slotRarity["armor"] = eq[6].GetEffectiveRarity();
                if (eq.ContainsKey(5)) savedChar.equipment.slotRarity["helmet"] = eq[5].GetEffectiveRarity();
                if (eq.ContainsKey(2)) savedChar.equipment.slotRarity["gloves"] = eq[2].GetEffectiveRarity();
                if (eq.ContainsKey(7)) savedChar.equipment.slotRarity["boots"] = eq[7].GetEffectiveRarity();
                if (eq.ContainsKey(8)) savedChar.equipment.slotRarity["shoulders"] = eq[8].GetEffectiveRarity();
                if (eq.ContainsKey(11)) savedChar.equipment.slotRarity["shield"] = eq[11].GetEffectiveRarity();
                if (eq.ContainsKey(3)) savedChar.equipment.slotRarity["ring1"] = eq[3].GetEffectiveRarity();
                if (eq.ContainsKey(4)) savedChar.equipment.slotRarity["ring2"] = eq[4].GetEffectiveRarity();
                if (eq.ContainsKey(1)) savedChar.equipment.slotRarity["amulet"] = eq[1].GetEffectiveRarity();
                // Sync stored level from tracked GCObjects
                if (eq.ContainsKey(10)) savedChar.equipment.slotLevel["weapon"] = eq[10].StoredLevel;
                if (eq.ContainsKey(6)) savedChar.equipment.slotLevel["armor"] = eq[6].StoredLevel;
                if (eq.ContainsKey(5)) savedChar.equipment.slotLevel["helmet"] = eq[5].StoredLevel;
                if (eq.ContainsKey(2)) savedChar.equipment.slotLevel["gloves"] = eq[2].StoredLevel;
                if (eq.ContainsKey(7)) savedChar.equipment.slotLevel["boots"] = eq[7].StoredLevel;
                if (eq.ContainsKey(8)) savedChar.equipment.slotLevel["shoulders"] = eq[8].StoredLevel;
                if (eq.ContainsKey(11)) savedChar.equipment.slotLevel["shield"] = eq[11].StoredLevel;
                if (eq.ContainsKey(3)) savedChar.equipment.slotLevel["ring1"] = eq[3].StoredLevel;
                if (eq.ContainsKey(4)) savedChar.equipment.slotLevel["ring2"] = eq[4].StoredLevel;
                if (eq.ContainsKey(1)) savedChar.equipment.slotLevel["amulet"] = eq[1].StoredLevel;
                Debug.LogError($"[SAVE] Equipment synced: weapon={savedChar.equipment.weapon}, armor={savedChar.equipment.armor}");
            }

            savedChar.currentZoneName = conn.CurrentZoneName ?? "tutorial";
            savedChar.zoneId = (int)conn.CurrentZoneId;
            CharacterRepository.SaveCharacter(savedChar);
            Debug.LogError($"[SAVE] Saved {savedChar.inventory.Count} inventory items for {conn.LoginName}");
        }



        public void SavePlayerInventoryPublic(RRConnection conn)
        {
            SavePlayerInventory(conn);
        }

        public void SavePlayerLevelPublic(RRConnection conn)
        {
            SavePlayerLevel(conn);
        }

        /// <summary>
        /// Removes quest item objectives from inventory on turn-in.
        /// Stackable handling: decrements stack by obj.Required via 0x22 UpdateQuantity
        /// when the remaining count is > 0, falls back to 0x1F full-row remove when the
        /// stack hits zero (or for non-stackables). Crosses multiple stacks if the
        /// requirement exceeds any one stack's count. Call BEFORE HandleTurnInConfirmed
        /// (which drops the quest from the active list).
        ///
        /// Previously this used `GetAndRemoveInventoryItem` against the first matching
        /// slot, which nuked a 50-stack of QuestItemPAL.Token when the wishing-well
        /// objective only asked for 1 — see CHANGELOG_WISHING_WELL_2026-05-19.md.
        /// </summary>
        private void RemoveQuestItemsFromInventory(RRConnection conn, ActiveQuest completingQuest)
        {
            if (completingQuest == null) return;
            string connId = conn.ConnId.ToString();

            foreach (var obj in completingQuest.Objectives)
            {
                if (obj.Type == null || !obj.Type.Equals("item", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.IsNullOrEmpty(obj.Target))
                    continue;

                int remainingToRemove = Math.Max(1, obj.Required);
                Debug.LogError($"[QUEST-ITEM-REMOVE] Looking for: {obj.Target} ({obj.Label}) ×{remainingToRemove}");

                if (!_playerInventoryItems.ContainsKey(connId))
                {
                    Debug.LogError($"[QUEST-ITEM-REMOVE] No inventory tracked for {connId}");
                    continue;
                }

                // Snapshot slot ids first so we can mutate _playerInventoryItems during iteration.
                var matchingSlots = new List<uint>();
                foreach (var kvp in _playerInventoryItems[connId])
                {
                    if (kvp.Value.item != null &&
                        kvp.Value.item.GCClass != null &&
                        kvp.Value.item.GCClass.Equals(obj.Target, StringComparison.OrdinalIgnoreCase))
                    {
                        matchingSlots.Add(kvp.Key);
                    }
                }

                if (matchingSlots.Count == 0)
                {
                    Debug.LogError($"[QUEST-ITEM-REMOVE] NOT FOUND in inventory! Dumping all slots:");
                    foreach (var kvp in _playerInventoryItems[connId])
                        Debug.LogError($"  slot {kvp.Key}: {kvp.Value.item?.GCClass ?? "NULL"}");
                    continue;
                }

                foreach (uint slotId in matchingSlots)
                {
                    if (remainingToRemove <= 0) break;

                    int stack = GetStackCount(connId, slotId);
                    if (stack <= 0) stack = 1;

                    if (stack > remainingToRemove)
                    {
                        // Stack survives — decrement count, send 0x22 UpdateQuantity.
                        int newCount = stack - remainingToRemove;
                        SetStackCount(connId, slotId, newCount);
                        remainingToRemove = 0;

                        if (conn.UnitContainerId != 0)
                        {
                            var qWriter = new LEWriter();
                            qWriter.WriteByte(0x07);
                            qWriter.WriteByte(0x35);
                            qWriter.WriteUInt16(conn.UnitContainerId);
                            qWriter.WriteByte(0x22);
                            qWriter.WriteUInt32(slotId);
                            qWriter.WriteByte((byte)(newCount > 255 ? 255 : newCount));
                            WritePlayerEntitySynch(conn, qWriter);
                            qWriter.WriteByte(0x06);
                            SendCompressedA(conn, 0x01, 0x0F, qWriter.ToArray());
                        }
                        Debug.LogError($"[QUEST-ITEM-REMOVE] decremented slot {slotId}: {stack} → {newCount}");
                    }
                    else
                    {
                        // Stack consumed entirely — full-row remove via 0x1F.
                        remainingToRemove -= stack;
                        GetAndRemoveInventoryItem(connId, slotId);

                        if (conn.UnitContainerId != 0)
                        {
                            var writer = new LEWriter();
                            writer.WriteByte(0x07);
                            writer.WriteByte(0x35);
                            writer.WriteUInt16(conn.UnitContainerId);
                            writer.WriteByte(0x1F);
                            writer.WriteUInt32(slotId);
                            if (!TryWriteEntitySynchForComponent(conn, writer, conn.UnitContainerId, 0x1F, SyncContext.PlayerActionResponse, "QUEST-ITEM-REMOVE", true))
                                continue;
                            writer.WriteByte(0x06);
                            SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
                        }
                        Debug.LogError($"[QUEST-ITEM-REMOVE] consumed full stack at slot {slotId} (was {stack})");
                    }
                }

                if (remainingToRemove > 0)
                {
                    Debug.LogError($"[QUEST-ITEM-REMOVE] ⚠️ underflowed: {remainingToRemove}× {obj.Target} still needed but inventory exhausted");
                }
                SavePlayerInventory(conn);
                Debug.LogError($"[QUEST-ITEM-REMOVE] ✅ done for {obj.Target}");
            }
        }


        // Throttle map for goto proximity checks: connId -> next allowed check time
        private Dictionary<int, float> _gotoNextCheckTime = new Dictionary<int, float>();
        private int _udpPacketCount = 0;
        private static readonly byte[] NULL_PRIVKEY = System.Text.Encoding.ASCII.GetBytes("PRIVKEY!");
        private byte[] _udpBlowfishKey;

        private void SendUDPSynAck(IPEndPoint clientEndpoint, byte[] clientSyn)
        {
            try
            {
                byte[] generator = new byte[64];
                generator[0] = 0x02;

                byte[] prime = new byte[128];
                for (int i = 0; i < prime.Length; i++) prime[i] = 0xFF;

                var ms = new System.IO.MemoryStream();
                var bw = new System.IO.BinaryWriter(ms);

                bw.Write((byte)0x03);
                bw.Write(clientSyn.Length > 1 ? clientSyn[1] : (byte)0x01);
                bw.Write((ushort)0);
                bw.Write((uint)1);

                bw.Write((ushort)generator.Length);
                bw.Write(generator);

                bw.Write((ushort)prime.Length);
                bw.Write(prime);

                bw.Write((ushort)NULL_PUBKEY.Length);
                bw.Write(NULL_PUBKEY);

                byte[] packet = ms.ToArray();
                _udpListener.Send(packet, packet.Length, clientEndpoint);

                // Create UDP session with Blowfish
                string epKey = GetEndpointKey(clientEndpoint);
                var session = new UDPSession { Endpoint = clientEndpoint, IsEstablished = false };

                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    byte[] blowfishKey = md5.ComputeHash(NULL_PRIVKEY);
                    session.InitializeCipher(blowfishKey);
                    _udpBlowfishKey = blowfishKey;
                    Debug.LogError($"[UDP] Blowfish Key: {BitConverter.ToString(blowfishKey)}");
                }

                _udpSessions[epKey] = session;
                Debug.LogError($"[UDP] Sent SYN-ACK with PUBKEY12, session created for {epKey}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UDP] SendUDPSynAck error: {ex.Message}");
            }
        }

        private string GetLocalIPAddress()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    string ipStr = ip.ToString();
                    if (ipStr.StartsWith("10."))
                    {
                        return ipStr;
                    }
                }
            }
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip.ToString();
                }
            }
            return "127.0.0.1";
        }

        private void StopServer()
        {
            _isRunning = false;
            _listener?.Stop();
            _udpListener?.Close();  // 🔥 ADD THIS LINE
            foreach (var conn in _connections.Values)

            {
                conn.Disconnect();
            }
            _connections.Clear();
            Debug.Log("Game Server stopped");
        }

        // ═══════════════════════════════════════════════════════════
        // ADMIN PANEL: PENDING ITEM GRANTS
        // ═══════════════════════════════════════════════════════════
        private IEnumerator PollPendingItemGrants()
        {
            yield return new WaitForSeconds(5f);
            while (_isRunning)
            {
                try { ProcessPendingGrants(); }
                catch (Exception ex) { Debug.LogError($"[GRANTS] Error: {ex.Message}"); }
                try { ProcessPendingAdminActions(); }
                catch (Exception ex) { Debug.LogError($"[ADMIN-ACT] Error: {ex.Message}"); }
                yield return new WaitForSeconds(5f);
            }
        }

        private void ProcessPendingAdminActions()
        {
            using (var conn = GameDatabase.GetConnection())
            {
                // Check if table exists
                object tbl = GameDatabase.ExecuteScalar(conn, "SELECT name FROM sqlite_master WHERE type='table' AND name='pending_admin_actions'");
                if (tbl == null) return;

                var actions = new List<(int id, int charId, string actionType, int value)>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT id, character_id, action_type, value FROM pending_admin_actions ORDER BY id";
                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read())
                            actions.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt32(3)));
                }
                if (actions.Count == 0) return;

                foreach (var act in actions)
                {
                    try
                    {
                        // Find online connection
                        RRConnection onlineConn = null;
                        string loginName = null;
                        foreach (var kvp in _selectedCharacter)
                            if (kvp.Value.Id == act.charId) { loginName = kvp.Key; break; }
                        if (loginName != null)
                            foreach (var c in _connections)
                                if (c.Value.LoginName == loginName) { onlineConn = c.Value; break; }

                        if (act.actionType == "gold" && onlineConn != null && onlineConn.UnitContainerId != 0)
                        {
                            // Direct SQL update — don't use SaveCharacter which overwrites XP/level
                            using (var gdb = GameDatabase.GetConnection())
                            {
                                if (act.value > 0)
                                {
                                    GameDatabase.ExecuteNonQuery(gdb,
                                        "UPDATE characters SET gold = gold + @amt WHERE id = @id",
                                        ("@amt", act.value), ("@id", act.charId));
                                    // Send AddCurrency packet (0x20)
                                    var wr = new LEWriter();
                                    wr.WriteByte(0x07);
                                    wr.WriteByte(0x35);
                                    wr.WriteUInt16(onlineConn.UnitContainerId);
                                    wr.WriteByte(0x20);
                                    wr.WriteUInt32((uint)act.value);
                                    wr.WriteByte(0x00);
                                    wr.WriteUInt32(0x00000000);
                                    wr.WriteByte(0x01);
                                    wr.WriteByte(0x02);
                                    wr.WriteUInt32(0x00000000);
                                    wr.WriteByte(0x06);
                                    SendToClient(onlineConn, wr.ToArray());
                                }
                                else if (act.value < 0)
                                {
                                    GameDatabase.ExecuteNonQuery(gdb,
                                        "UPDATE characters SET gold = MAX(0, gold + @amt) WHERE id = @id",
                                        ("@amt", act.value), ("@id", act.charId));
                                    // Send AddCurrency (0x20) with negative value — 0x21 is gated behind merchant context
                                    var wr = new LEWriter();
                                    wr.WriteByte(0x07);
                                    wr.WriteByte(0x35);
                                    wr.WriteUInt16(onlineConn.UnitContainerId);
                                    wr.WriteByte(0x20);                  // AddCurrency
                                    wr.WriteInt32(act.value);            // already negative
                                    wr.WriteByte(0x00);                  // source
                                    wr.WriteUInt32(0x00000000);          // entityHandle
                                    wr.WriteByte(0x01);                  // notifyFlag (jingle)
                                    WritePlayerEntitySynch(onlineConn, wr);
                                    wr.WriteByte(0x06);
                                    SendCompressedA(onlineConn, 0x01, 0x0F, wr.ToArray());
                                }
                                // Update in-memory savedChar gold too
                                var sc = GetSavedCharacterForConn(onlineConn);
                                if (sc != null) sc.gold = (uint)Math.Max(0, (int)sc.gold + act.value);
                            }
                            Debug.LogError($"[ADMIN-ACT] ✅ Gold {(act.value > 0 ? "+" : "")}{act.value} for char {act.charId}");
                        }
                        else if (act.actionType == "level" && onlineConn != null)
                        {
                            string connId = onlineConn.ConnId.ToString();
                            PlayerState lvlPs = GetPlayerState(connId);
                            var savedChar = GetSavedCharacterForConn(onlineConn);
                            if (savedChar != null && lvlPs != null)
                            {
                                int oldLevel = lvlPs.Level;
                                int targetLevel = act.value;
                                byte persistedTargetLevel = SavedCharacterLevel.ResolvePersistedLevel(targetLevel);
                                // Direct SQL — only update level and experience
                                using (var ldb = GameDatabase.GetConnection())
                                    GameDatabase.ExecuteNonQuery(ldb,
                                        "UPDATE characters SET level=@lv, experience=0 WHERE id=@id",
                                        ("@lv", persistedTargetLevel), ("@id", act.charId));
                                savedChar.level = persistedTargetLevel;
                                savedChar.experience = 0;
                                lvlPs.InitializeStats(savedChar.className ?? "Fighter", targetLevel);
                                lvlPs.Experience = 0;
                                if (onlineConn.Avatar != null)
                                    CalculateEquipmentBonuses(connId, onlineConn.Avatar);
                                lvlPs.RestoreToFull();

                                if (targetLevel > oldLevel)
                                {
                                    for (int lv = oldLevel; lv < targetLevel; lv++)
                                    {
                                        uint threshold = PlayerState.GetClientThreshold(lv + 1);
                                        uint packetXP = threshold * 256 / 5 + 100;
                                        SendAdminXPUpdate(onlineConn, packetXP, (uint)lv);
                                    }
                                    // The DLL's XP hook on the client will echo each admin XP
                                    // packet back to the server and write _lastClientXP. Without
                                    // this reset, the next mob kill would consume that stale value
                                    // and add a giant chunk of XP on top of the level we just set.
                                    _lastClientXP = 0;
                                }
                                SendAdminHPSync(onlineConn, lvlPs);
                                Debug.LogError($"[ADMIN-ACT] ✅ Level {oldLevel} → {targetLevel} for char {act.charId}");
                            }
                        }
                        // XP: update in-memory state so zone save doesn't overwrite DB
                        if (act.actionType == "xp" && onlineConn != null)
                        {
                            var savedChar = GetSavedCharacterForConn(onlineConn);
                            if (savedChar != null)
                            {
                                savedChar.experience = (uint)act.value;
                                string connId = onlineConn.ConnId.ToString();
                                PlayerState xpPs = GetPlayerState(connId);
                                if (xpPs != null) xpPs.Experience = (uint)act.value;
                                Debug.LogError($"[ADMIN-ACT] ✅ XP memory updated to {act.value} for char {act.charId}");
                            }
                        }
                        else if (act.actionType == "xp" && onlineConn == null)
                        {
                            // Offline - DB already updated by admin panel, nothing to do
                        }

                        // Delete processed action
                        GameDatabase.ExecuteNonQuery(conn, "DELETE FROM pending_admin_actions WHERE id=@id", ("@id", act.id));
                    }
                    catch (Exception ex) { Debug.LogError($"[ADMIN-ACT] Failed action {act.id}: {ex.Message}"); }
                }
            }
        }

        private void ProcessPendingGrants()
        {
            using (var conn = GameDatabase.GetConnection())
            {
                var grants = new List<(int id, int charId, string gcClass, int count, int width, int height, int rarity)>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT id, character_id, gc_class, count, width, height, rarity FROM pending_item_grants ORDER BY id";
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            grants.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2),
                                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6)));
                    }
                }
                if (grants.Count == 0) return;

                foreach (var grant in grants)
                {
                    try
                    {
                        // Find if character is online
                        RRConnection onlineConn = null;
                        string loginName = null;
                        foreach (var kvp in _selectedCharacter)
                        {
                            if (kvp.Value.Id == grant.charId) { loginName = kvp.Key; break; }
                        }
                        if (loginName != null)
                        {
                            foreach (var c in _connections)
                            {
                                if (c.Value.LoginName == loginName) { onlineConn = c.Value; break; }
                            }
                        }

                        if (onlineConn != null && onlineConn.UnitContainerId != 0)
                        {
                            string connId = onlineConn.ConnId.ToString();
                            string gcTypeFull = grant.gcClass;
                            if (!gcTypeFull.StartsWith("items.", StringComparison.OrdinalIgnoreCase))
                                gcTypeFull = "items.pal." + gcTypeFull;
                            int itemWidth = grant.width, itemHeight = grant.height;
                            try
                            {
                                using (var db2 = GameDatabase.GetConnection())
                                using (var dimReader = GameDatabase.ExecuteReader(db2,
                                    "SELECT gc_type, width, height FROM item_dimensions WHERE LOWER(gc_type) = LOWER(@g)", ("@g", gcTypeFull)))
                                { if (dimReader.Read()) { gcTypeFull = dimReader.GetString(0); itemWidth = dimReader.GetInt32(1); itemHeight = dimReader.GetInt32(2); } }
                            }
                            catch { }

                            string packetGcType = gcTypeFull.ToLowerInvariant();
                            if (packetGcType.StartsWith("items.pal.")) packetGcType = packetGcType.Substring(10);

                            byte slotX = 0, slotY = 0; bool foundSlot = false;
                            for (byte row = 0; row < 8 && !foundSlot; row++)
                                for (byte col = 0; col < 10 && !foundSlot; col++)
                                    if (!IsInventorySlotOccupied(connId, col, row, itemWidth, itemHeight))
                                    { slotX = col; slotY = row; foundSlot = true; }
                            if (!foundSlot) { Debug.LogError($"[GRANTS] Inventory full char {grant.charId}"); continue; }

                            // CRITICAL: use authored GC child count for correct mod count (NOT weapons.mod_count!)
                            int modSlots = 1;
                            if (DungeonRunners.Managers.MerchantManager.TryGetAuthoredMerchantModSlots(packetGcType, out int ms))
                                modSlots = ms;

                            uint slot = GetNextInventorySlot(connId);
                            PlayerState ps = GetPlayerState(connId);

                            byte itemLevel = ps != null ? (byte)Math.Max(1, Math.Min(ps.Level, 100)) : (byte)1;

                            var wr = new LEWriter();
                            wr.WriteByte(0x07);
                            wr.WriteByte(0x35);
                            wr.WriteUInt16(onlineConn.UnitContainerId);
                            wr.WriteByte(0x1E);
                            wr.WriteByte(0x0B);
                            wr.WriteByte(0xFF);
                            wr.WriteCString(packetGcType);
                            wr.WriteUInt32(slot);
                            wr.WriteByte(slotX);
                            wr.WriteByte(slotY);
                            wr.WriteByte((byte)grant.count);
                            wr.WriteByte(itemLevel);
                            for (int mi = 0; mi < modSlots; mi++)
                                wr.WriteByte(0x00);
                            wr.WriteByte(0x01);
                            wr.WriteByte(0xFF);
                            wr.WriteCString("ScaleModPAL.Rare.Mod1");
                            wr.WriteByte(0x03);
                            wr.WriteByte(0x15);
                            wr.WriteUInt32(0x11111111);
                            WritePlayerEntitySynch(onlineConn, wr);
                            wr.WriteByte(0x06);

                            Debug.LogError($"[GRANTS] Sending: gc={packetGcType} modSlots={modSlots} slot={slot} pos=({slotX},{slotY})");
                            SendToClient(onlineConn, wr.ToArray());

                            // Set NativeClass correctly (same as merchant buy)
                            string gcLower2 = packetGcType.ToLowerInvariant();
                            string nativeClass = "Armor";
                            if (gcLower2.Contains("consumable") || gcLower2.Contains("questitem") || gcLower2.Contains("ring") || gcLower2.Contains("amulet") || gcLower2.Contains("scroll") || gcLower2.Contains("potion") || gcLower2.Contains("skillbook") || gcLower2.Contains("voucher"))
                                nativeClass = "Item";
                            else if (gcLower2.Contains("sword") || gcLower2.Contains("axe") || gcLower2.Contains("mace") || gcLower2.Contains("dagger") || gcLower2.Contains("hammer") || gcLower2.Contains("staff") || gcLower2.Contains("spear") || gcLower2.Contains("pick") || gcLower2.Contains("club") || gcLower2.Contains("scepter") || gcLower2.Contains("wand"))
                                nativeClass = "MeleeWeapon";
                            else if (gcLower2.Contains("bow") || gcLower2.Contains("cannon") || gcLower2.Contains("crossbow") || gcLower2.Contains("xbow") || gcLower2.Contains("gun"))
                                nativeClass = "RangedWeapon";

                            var newItem = new DungeonRunners.Data.GCObject { GCClass = packetGcType, NativeClass = nativeClass };
                            newItem.StoredRarity = grant.rarity;
                            newItem.StoredLevel = itemLevel;
                            TrackInventoryItem(connId, slot, newItem, slotX, slotY);
                            OccupyInventorySlots(connId, slotX, slotY, itemWidth, itemHeight);
                            SetStackCount(connId, slot, grant.count);
                            SavePlayerInventoryPublic(onlineConn);

                            Debug.LogError($"[GRANTS] ✅ LIVE delivered {packetGcType} x{grant.count} modSlots={modSlots}");
                            GameDatabase.ExecuteNonQuery(conn, "DELETE FROM pending_item_grants WHERE id=@id", ("@id", grant.id));
                            continue;
                        }

                        {
                            // OFFLINE: insert into DB with proper grid placement
                            var occupied = new HashSet<string>();
                            using (var cmd2 = conn.CreateCommand())
                            {
                                cmd2.CommandText = "SELECT slot_x, slot_y FROM character_inventory WHERE character_id=@cid";
                                cmd2.Parameters.AddWithValue("@cid", grant.charId);
                                using (var r2 = cmd2.ExecuteReader())
                                    while (r2.Read()) occupied.Add($"{r2.GetInt32(0)},{r2.GetInt32(1)}");
                            }

                            int fx = -1, fy = -1;
                            for (int y = 0; y <= 8 - grant.height && fy < 0; y++)
                                for (int x = 0; x <= 10 - grant.width && fx < 0; x++)
                                {
                                    bool fits = true;
                                    for (int dx = 0; dx < grant.width && fits; dx++)
                                        for (int dy = 0; dy < grant.height && fits; dy++)
                                            if (occupied.Contains($"{x + dx},{y + dy}")) fits = false;
                                    if (fits) { fx = x; fy = y; }
                                }

                            if (fx < 0) { Debug.LogError($"[GRANTS] Inventory full (offline) char {grant.charId}"); continue; }

                            GameDatabase.ExecuteNonQuery(conn,
                                "INSERT INTO character_inventory (character_id,gc_class,slot_x,slot_y,count,rarity,stored_level) VALUES(@cid,@gc,@x,@y,@n,@r,-1)",
                                ("@cid", grant.charId), ("@gc", grant.gcClass), ("@x", fx), ("@y", fy), ("@n", grant.count), ("@r", grant.rarity));
                            Debug.LogError($"[GRANTS] 📦 OFFLINE inserted {grant.gcClass} x{grant.count} to char {grant.charId} at ({fx},{fy})");
                        }

                        // Delete processed grant
                        GameDatabase.ExecuteNonQuery(conn, "DELETE FROM pending_item_grants WHERE id=@id", ("@id", grant.id));
                    }
                    catch (Exception ex) { Debug.LogError($"[GRANTS] Failed grant {grant.id}: {ex.Message}"); }
                }
            }
        }

        private IEnumerator AcceptClientsCoroutine()
        {
            while (_isRunning)
            {
                if (_listener.Pending())
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    var endpoint = (System.Net.IPEndPoint)client.Client.RemoteEndPoint;
                    string remoteIP = endpoint.Address.IsIPv4MappedToIPv6
                        ? endpoint.Address.MapToIPv4().ToString()
                        : endpoint.Address.ToString();
                    Debug.LogError($"[GAME-PORT] Connection from {remoteIP}:{endpoint.Port}");

                    // Check if this is a queue connection (flagged by AuthServer)
                    string queueUser = QueueConnectionBridge.CheckAndConsumeQueueIP(remoteIP);
                    if (queueUser != null)
                    {
                        Debug.LogError($"[QUEUE] ✅ Queue connection from {remoteIP} for {queueUser} on GAME port");
                        // Game port uses Go/Blowfish (same as PlayOk) — no crypto needed
                    }

                    int connId = _nextConnId++;
                    NetworkStream stream = client.GetStream();
                    var conn = new RRConnection(connId, client, stream);
                    if (queueUser != null)
                        conn.LoginName = queueUser;
                    _connections[connId] = conn;
                    Debug.Log($"Client {connId} connected from {client.Client.RemoteEndPoint}");
                    InitiateUDPToClient(conn);
                    StartCoroutine(HandleClientCoroutine(conn));
                }
                yield return null;
            }
        }

        private IEnumerator HandleClientCoroutine(RRConnection conn)
        {
            // Load account flags for queue users (LoginName already set)
            if (conn.LoginName != null)
            {
                LoadAccountFlags(conn.LoginName);
                conn.DllSessionToken = (uint)(new System.Random().Next(1, int.MaxValue));
                NativeRngLedger.LogSystemRandom("dll-session-token", (int)conn.DllSessionToken, conn.LoginName);
                Debug.LogError($"[DLL-TOKEN] Generated token 0x{conn.DllSessionToken:X8} for {conn.LoginName}");
            }

            byte[] buffer = new byte[8192];
            while (_isRunning && conn.Client.Connected)
            {
                try
                {
                    // Detect dead connections (client closed without sending data)
                    if (conn.Client.Client.Poll(0, System.Net.Sockets.SelectMode.SelectRead) && !conn.Stream.DataAvailable)
                    {
                        Debug.LogError($"[CLIENT] Connection {conn.ConnId} dead (poll detected)");
                        break;
                    }

                    if (conn.Stream.DataAvailable)
                    {
                        int bytesRead = conn.Stream.Read(buffer, 0, buffer.Length);
                        if (bytesRead > 0)
                        {
                            byte[] data = new byte[bytesRead];
                            Array.Copy(buffer, data, bytesRead);
                            ProcessMessage(conn, data);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error handling client {conn.ConnId}: {ex.Message}\n{ex.StackTrace}");
                    // DON'T break — one bad packet should not kill the connection
                }
                yield return null;
            }
            Debug.Log($"Client {conn.ConnId} disconnected");
            QueueConnectionBridge.PlayerDisconnected();
            SavePlayerInventory(conn);
            SavePlayerQuests(conn);
            SavePlayerLevel(conn);
            TouchSoloDungeonInstance(conn, "disconnect");

            // Clean up Bling Gnome on disconnect
            BlingGnomeManager.Instance.OnPlayerDisconnect(conn.ConnId);

            // Clear modifier tracker on disconnect (admin buffs are session-only)
            if (conn.LoginName != null)
                _modifierTracker.ClearPlayer(conn.LoginName);

            if (conn.LoginName != null)
                _debuffCooldowns.Remove(conn.LoginName);

            // Clean modifier sent guard so reconnect works
            if (conn.LoginName != null)
            {
                _freePlayerModifierSent.Remove(conn.LoginName);
                _freePlayerModifierSent.Remove(conn.LoginName + "_sent");
            }

            // Clear active character in DB for admin panel
            if (conn.LoginName != null)
            {
                try
                {
                    using (var dbConn = GameDatabase.GetConnection())
                        GameDatabase.ExecuteNonQuery(dbConn,
                            "UPDATE accounts SET current_character_id=0 WHERE username=@u COLLATE NOCASE",
                            ("@u", conn.LoginName));
                }
                catch (System.Exception ex) { Debug.LogError($"[SAVE] clear current_character_id failed for {conn.LoginName}: {ex.Message}"); }
            }

            // Clear town portal on normal logoff
            if (conn.LoginName != null && _selectedCharacter.ContainsKey(conn.LoginName))
            {
                var logoffChar = CharacterRepository.GetCharacter(_selectedCharacter[conn.LoginName].Id);
                if (logoffChar != null)
                {
                    logoffChar.tpZone = "";
                    logoffChar.tpZoneId = 0;
                    logoffChar.tpTargetZone = "";
                    logoffChar.tpPosX = 0;
                    logoffChar.tpPosY = 0;
                    logoffChar.tpPosZ = 0;
                    CharacterRepository.SaveCharacter(logoffChar);
                    Debug.LogError($"[DISCONNECT] Cleared town portal from DB");
                }
            }

            // ═══ DROPPED ITEMS: Remove all items dropped by this player ═══
            if (conn.LoginName != null)
            {
                string dropperName = conn.LoginName;
                List<ushort> toRemove = new List<ushort>();

                foreach (var kvp in _droppedItems)
                {
                    if (kvp.Value.DroppedBy == dropperName)
                        toRemove.Add(kvp.Key);
                }

                foreach (ushort entityId in toRemove)
                {
                    var info = _droppedItems[entityId];

                    // Despawn for all other players in same zone
                    foreach (var other in _connections.Values)
                    {
                        if (other == conn) continue;
                        if (!other.IsSpawned) continue;
                        if (other.CurrentZoneName != info.Zone) continue;
                        if (other.InstanceId != info.InstanceId) continue;
                        SendDespawnEntity(other, entityId);
                    }

                    // Remove from DB
                    if (info.DbId > 0)
                    {
                        try
                        {
                            using (var db2 = DungeonRunners.Database.GameDatabase.GetConnection())
                            {
                                DungeonRunners.Database.GameDatabase.ExecuteNonQuery(db2,
                                    "DELETE FROM dropped_items WHERE id = @id",
                                    ("@id", info.DbId));
                            }
                            _dbIdToEntityId.Remove(info.DbId);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[DISCONNECT] ❌ Failed to delete dropped item dbId={info.DbId}: {ex.Message}");
                        }
                    }

                    _droppedItems.Remove(entityId);
                }

                if (toRemove.Count > 0)
                    Debug.LogError($"[DISCONNECT] Removed {toRemove.Count} dropped items for {dropperName}");
            }

            // ═══ GROUP: Mark member offline, send 0x49 then 0x35 ═══
            // TTD-VERIFIED sequence:
            //   0x49 → sets member+0x18=0, +0x30=1, shows "disconnected" chat message
            //   0x35 → rebuilds member vector from packet data (isOnline=false for
            //          disconnected member via readMember@0x5FA6D0), fires event 0x121111
            //          → GroupHealth::refresh reads fresh member+0x18=0 → shows grey overlay
            // DO NOT send 0x44 — TTD trace 20 proved it resets member state.
            // 0x35 AFTER 0x49 works because 0x35 creates NEW member entries from packet
            // data where isOnline byte is already 0, so no state to "undo".
            var group = GroupManager.Instance.GetGroupForConn(conn.ConnId);
            if (group != null)
            {
                // Cache data BEFORE disconnect
                var gm = group.Members.Find(m => m.ConnId == conn.ConnId);
                if (gm != null)
                {
                    gm.CharSqlId = GetCharSqlId(conn);
                    gm.AvatarEntityId = GetPlayerAvatarId(conn.LoginName);
                    if (_selectedCharacter.TryGetValue(conn.LoginName, out var ch))
                        gm.Name = ch.Name ?? gm.Name;
                }

                uint disconnCharId = GetCharSqlId(conn);

                if (GroupManager.Instance.DisconnectMember(conn.ConnId))
                {
                    if (group.Members.Any(m => m.IsOnline))
                    {
                        // Step 1: 0x49 — shows "Group member '%s' has disconnected." chat message
                        byte[] disconnectPacket = GroupPackets.BuildMemberDisconnected(group.GroupId, disconnCharId);
                        foreach (var rm in group.Members)
                        {
                            if (!rm.IsOnline) continue;
                            var rmc = FindConnectionById(rm.ConnId);
                            if (rmc != null)
                                SendToClient(rmc, disconnectPacket);
                        }

                        // Step 2: 0x35 — rebuilds member vector with isOnline=false for
                        // disconnected member. readMember@0x5FA6D0 reads isOnline byte from
                        // packet → stores at member+0x18. Event 0x121111 fires →
                        // GroupHealth::refresh → reads +0x18=0 → grey overlay.
                        SendGroupConnectedToAll(group);

                        Debug.LogError($"[GROUP] Sent 0x49+0x35 for disconnect: {conn.LoginName} charId=0x{disconnCharId:X8}");
                    }
                    else
                    {
                        foreach (var m in group.Members.ToList())
                            GroupManager.Instance.LeaveGroup(m.ConnId);
                    }
                }
                conn.GroupConnectedSent = false;
            }

            // ═══ MULTIPLAYER: Remove avatar from other clients ═══
            if (conn.IsSpawned)
            {
                BroadcastEntityRemove(conn, conn.CurrentZoneGcType);
                conn.IsSpawned = false;
            }

            string disconnKey = conn.ConnId.ToString();
            ClearUseTarget(conn);
            Combat.WeaponCycleTracker.Instance.ClearConnection(disconnKey);
            if (conn.Avatar != null)
                CombatManager.Instance.UnregisterPlayer((uint)conn.Avatar.Id);
            else if (_playerAvatarEntityId.TryGetValue(disconnKey, out uint oldAvatarId))
                CombatManager.Instance.UnregisterPlayer(oldAvatarId);
            Debug.LogError($"[COMBAT-LIFECYCLE] disconnect cleared combat state conn={conn.ConnId} avatar={(conn.Avatar != null ? conn.Avatar.Id : 0)}");
            _playerAvatarEntityId.Remove(disconnKey);
            _playerNextSkillEntityId.Remove(disconnKey);
            _playerSkillsComponentId.Remove(disconnKey);
            _playerSkillSlots.Remove(disconnKey);
            _connections.Remove(conn.ConnId);
            if (conn.LoginName != null) { lock (_dllEndpoints) { _dllEndpoints.Remove(conn.LoginName); } }

            // PVP: clean up queue + forfeit active match on disconnect
            try
            {
                var forfeited = Managers.PVPMatchManager.Instance.HandleDisconnect(conn.LoginName);
                if (forfeited != null) FinalizeMatchResults(forfeited);
            }
            catch (Exception ex) { Debug.LogError($"[PVP] disconnect cleanup: {ex.Message}"); }

            // Social: notify friends this player went offline
            if (conn.LoginName != null && _selectedCharacter.TryGetValue(conn.LoginName, out var disconnChar))
            {
                SocialManager.Instance.PlayerOffline(conn.LoginName, disconnChar.Name, SendSocialViaAuth);
                // Posse: rebroadcast CachedPosseFull to other posse members so they see the offline state.
                try { PosseManager.Instance.NotifyMemberStateChange(disconnChar.Id, this); }
                catch (Exception ex) { Debug.LogError($"[POSSE] disconnect notify failed: {ex.Message}"); }
            }
            else if (conn.LoginName != null)
            {
                // Fallback: remove from online users even without character lookup
                SocialManager.Instance.ForceRemoveOnline(conn.LoginName);
            }
            QueueConnectionBridge.RemoveQueueStream(conn.LoginName);

            conn.Disconnect();
        }

        private void ProcessMessage(RRConnection conn, byte[] data)
        {
            int offset = 0;

            // Loop through all concatenated messages in the buffer (like Rainbow does)
            while (offset < data.Length)
            {
                if (data.Length - offset < 1) break;

                byte messageType = data[offset];
                int messageLen = CalculateMessageLength(data, offset, messageType);

                if (messageLen <= 0 || offset + messageLen > data.Length)
                {
                    Debug.LogError($"[MSG-LOOP] Invalid message length {messageLen} at offset {offset}, remaining {data.Length - offset}");
                    break;
                }

                // Extract this single message
                byte[] singleMessage = new byte[messageLen];
                Array.Copy(data, offset, singleMessage, 0, messageLen);

                ProcessSingleMessage(conn, singleMessage);

                offset += messageLen;
            }
        }

        private int CalculateMessageLength(byte[] data, int offset, byte messageType)
        {
            switch (messageType)
            {
                case 0x02: // Ping - variable, read from buffer
                    return data.Length - offset; // Consume all for ping
                case 0x03: // Connect - 4 bytes total
                    return 4;
                case 0x0A: // CompressedA - 1 + 3 + 4 + bodyLen
                case 0x0E: // CompressedE - 1 + 3 + 4 + bodyLen
                    if (data.Length - offset < 8) return -1;
                    uint bodyLen = BitConverter.ToUInt32(data, offset + 4);
                    return 8 + (int)bodyLen;
                case 0x10: // Direct - need to figure out length
                    if (data.Length - offset < 8) return -1;
                    uint directBodyLen = (uint)(data[offset + 4] | (data[offset + 5] << 8) | (data[offset + 6] << 16));
                    return 8 + (int)directBodyLen;
                default:
                    return data.Length - offset; // Unknown, consume all
            }
        }

        private void ProcessSingleMessage(RRConnection conn, byte[] data)
        {
            WirePacketTally.OnRawTCP(data);
            if (data.Length < 1) return;

            byte messageType = data[0];

            switch (messageType)
            {
                case 0x02: // Ping - don't log these
                    HandlePing(conn, data);
                    break;
                case 0x03:
                    HandleConnect(conn, data);
                    break;
                case 0x0A:
                    HandleCompressedA(conn, data);
                    break;
                case 0x0E:
                    HandleCompressedE(conn, data);
                    break;
                case 0x10:
                    HandleDirectMessage(conn, data);
                    break;
                default:
                    Debug.LogError($"[RAW-IN] UNKNOWN message type: 0x{messageType:X2}");
                    break;
            }
        }

        private void HandlePing(RRConnection conn, byte[] data)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x02);
            if (data.Length > 1)
            {
                byte[] pingData = new byte[data.Length - 1];
                Array.Copy(data, 1, pingData, 0, data.Length - 1);
                writer.WriteBytes(pingData);
            }
            byte[] response = writer.ToArray();
            lock (conn.SendLock)
            {
                conn.Stream.Write(response, 0, response.Length);
            }
        }

        private void HandleConnect(RRConnection conn, byte[] data)
        {
            Debug.Log($"Client {conn.ConnId} sent connect message");

            var reader = new LEReader(data);
            reader.ReadByte(); // Skip message type
            uint clientId = reader.ReadUInt24();
            _peerId24[conn.ConnId] = clientId;

            Debug.Log($"Client peer ID: 0x{clientId:X6}");

            var writer = new LEWriter();
            writer.WriteByte(0x04);
            writer.WriteUInt24((int)clientId);
            writer.WriteUInt32(0);

            byte[] response = writer.ToArray();
            lock (conn.SendLock)
            {
                conn.Stream.Write(response, 0, response.Length);
            }
            Debug.Log($"Sent connect response to client {conn.ConnId}");
        }

        private void HandleCompressedA(RRConnection conn, byte[] data)
        {
            try
            {
                var reader = new LEReader(data);
                reader.ReadByte(); // 0x0A
                uint peerId = reader.ReadUInt24();
                uint bodyLen = reader.ReadUInt32();
                byte channel = reader.ReadByte();
                byte messageType = reader.ReadByte();
                reader.ReadByte(); // 0x00
                uint uncompressedLen = reader.ReadUInt32();

                byte[] compressed = reader.ReadBytes((int)(bodyLen - 7));
                byte[] payload = ZlibUtil.Inflate(compressed);

                HandleChannelMessage(conn, channel, messageType, payload);
            }
            catch (Exception ex)
            {
                Debug.LogError($"HandleCompressedA error: {ex.Message}");
            }
        }

        private void HandleCompressedE(RRConnection conn, byte[] data)
        {
            try
            {
                var reader = new LEReader(data);
                reader.ReadByte(); // 0x0E
                uint dest = reader.ReadUInt24();
                uint bodyLen = reader.ReadUInt24();
                reader.ReadByte(); // 0x00
                uint source = reader.ReadUInt24();
                reader.ReadBytes(5); // Skip 5 bytes
                uint uncompressedLen = reader.ReadUInt32();

                byte[] compressed = reader.ReadBytes((int)(bodyLen - 12));

                byte[] payload = ZlibUtil.Inflate(compressed);

                if (payload.Length >= 2)
                {
                    byte channel = payload[0];
                    byte messageType = payload[1];
                    byte[] innerData = new byte[payload.Length - 2];
                    Array.Copy(payload, 2, innerData, 0, innerData.Length);

                    HandleChannelMessage(conn, channel, messageType, innerData);
                }
                else
                {
                    Debug.LogError($"[E-LANE] Payload too short: {payload.Length} bytes");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[E-LANE] ERROR: {ex.Message}");
            }
        }

        private void HandleDirectMessage(RRConnection conn, byte[] data)
        {
            try
            {
                var reader = new LEReader(data);
                reader.ReadByte(); // 0x10
                uint peerId = reader.ReadUInt24();
                uint bodyLen = reader.ReadUInt24();
                byte channel = reader.ReadByte();
                byte[] payload = reader.ReadBytes((int)bodyLen);

                Debug.Log($"Direct message: peer=0x{peerId:X6}, channel={channel}");
                HandleChannelMessage(conn, channel, 0, payload);
            }
            catch (Exception ex)
            {
                Debug.LogError($"HandleDirectMessage error: {ex.Message}");
            }
        }

        private void HandleChannelMessage(RRConnection conn, byte channel, byte messageType, byte[] data)
        {
            WirePacketTally.OnChannel(channel, messageType, data);

            switch (channel)
            {
                case 0: // Initial connection
                    HandleInitialConnection(conn, messageType, data);
                    break;
                case 3: // Social/UserManager channel (TChannelManager index 3)
                    // Binary-verified: UserManagerClient::start @ 0x601B10 registers at
                    // GatewayClient's TChannelManager slot 3 (push 3 @ 0x601B71).
                    // HandleCompressedE already extracted: channel=3, messageType=socialOp, data=body
                    // messageType IS the social operation (0x00=connect, 0x01=requestRosters,
                    // 0x03=addContact, 0x07=requestUserList, etc.)
                    SocialManager.Instance.HandleMessage(conn, messageType, data, SendSocialViaAuth);
                    break;
                case 4: // Character channel
                    HandleCharacterChannel(conn, messageType, data);
                    break;
                case 6:
                    // Check for admin commands first (Invulnerable, etc.)
                    if (_adminHandler == null)
                    {
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
                    }
                    {
                        PlayerState adminPs = GetPlayerState(conn.ConnId.ToString());
                        // /adminui commands only for admins
                        if (IsPlayerAdmin(conn.LoginName) && _adminHandler.TryHandleAdminCommand(conn, messageType, data, adminPs, SendSystemMessage, SendToClient))
                            break;
                    }
                    // ═══ Bling Gnome chat commands — available to ALL players ═══
                    // Must come BEFORE ChatCommandHandler which blocks all @ for non-admin
                    {
                        bool handledByGnome = false;
                        try
                        {
                            var peekReader = new LEReader(data);
                            string peekMsg = peekReader.ReadCString();
                            if (peekMsg.StartsWith("@"))
                            {
                                string cmd = peekMsg.Substring(1).Trim().ToLower();
                                if (cmd == "gnome" || cmd == "bling" || cmd == "blinggnome" || cmd == "gs" || cmd == "gnomestatus")
                                {
                                    BlingGnomeManager.Instance.SetServer(this);
                                    BlingGnomeManager.Instance.HandleChatMessage(conn, data, SendCompressedA, SendSystemMessage);
                                    handledByGnome = true;
                                }
                            }
                        }
                        catch { }
                        if (handledByGnome) break;
                    }
                    if (_chatHandler == null) _chatHandler = new ChatCommandHandler(this);
                    if (!_chatHandler.HandleChatMessage(conn, data, SendSystemMessage))
                    {
                        // Fallback — other non-admin @ commands
                    }
                    // ═══ MULTIPLAYER: Chat relay ═══
                    {
                        try
                        {
                            var chatReader = new LEReader(data);
                            string chatText = chatReader.ReadCString();
                            if (!string.IsNullOrEmpty(chatText) && !chatText.StartsWith("/") && !chatText.StartsWith("@"))
                            {
                                BroadcastChatToZone(conn, chatText);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[CHAT-RELAY] Error: {ex.Message}");
                        }
                    }
                    break;
                case 7: // ClientEntity channel
                    HandleClientEntityChannel(conn, messageType, data);
                    break;
                case 9: // Group channel
                    HandleGroupChannel(conn, messageType, data);
                    break;
                case 12: // Social/UserManager channel
                    SocialManager.Instance.HandleMessage(conn, messageType, data, SendSocialViaAuth);
                    break;
                case 13: // QuestManager channel
                    Debug.LogError($"[CH13] ████ type=0x{messageType:X2} dataLen={data?.Length ?? 0} hex={(data != null && data.Length > 0 ? BitConverter.ToString(data, 0, Math.Min(30, data.Length)) : "EMPTY")} ████");
                    if (messageType == 0x07)
                    {
                        Debug.LogError($"[CH13] → Routing to goToCheckpoint (0x07)");
                        HandleCheckpointTeleportRequest(conn, data);
                    }
                    else if (messageType == 0x0C)
                    {
                        Debug.LogError($"[CH13] → Routing to Obelisk (0x0C)");
                        HandleObeliskTeleport(conn);
                    }
                    else if (messageType == 0x0B)
                    {
                        Debug.LogError($"[CH13] → Routing to SavedPlace (0x0B)");
                        HandleSavedPlaceTeleport(conn);
                    }
                    else
                    {
                        Debug.LogError($"[CH13] → UNHANDLED type 0x{messageType:X2} — routing to HandleZoneChannel");
                        HandleZoneChannel(conn, data);
                    }
                    break;
                case 11: // GroupClient channel (0x0B) — per binary VA 0x458B9B
                    HandleGroupClientChannel(conn, messageType, data);
                    break;
                case 15: // PosseClient channel (0x0F) — binary-verified via Ghidra (2026-05-16)
                    // PosseClient::start @ 0x00610610 has PUSH 0xf @ 0x00610676 immediately
                    // before CALL TChannelManager<>::createChannel @ 0x0061067F. Dispatch
                    // jump table for incoming messages lives at 0x00611F60. See PosseManager.cs
                    // header for the full opcode map.
                    PosseManager.Instance.HandleMessage(conn, messageType, data, SendSocialViaAuth, this);
                    break;
                default:
                    {
                        // Unknown-channel canary. PosseClient (slot 15) is wired above; any other
                        // channel that surfaces here is a still-unmapped gateway client
                        // (TradeClient candidates etc.). Keep the [POSSE-PROBE] prefix so the
                        // existing RuntimeEvidenceManager.IsFocusedLog entry continues to surface
                        // it in server.log.
                        string hex = (data != null && data.Length > 0)
                            ? BitConverter.ToString(data, 0, Math.Min(48, data.Length))
                            : "EMPTY";
                        Debug.LogError($"[POSSE-PROBE] Unhandled channel={channel} type=0x{messageType:X2} dataLen={data?.Length ?? 0} hex={hex}");
                    }
                    break;
            }
        }

        private void HandleCheckpointTeleportRequest(RRConnection conn, byte[] data)
        {
            Debug.LogError($"[CP-TELEPORT] ═══════════════════════════════════════════════════");
            Debug.LogError($"[CP-TELEPORT] Data hex: {BitConverter.ToString(data ?? new byte[0])}");

            try
            {
                if (data == null || data.Length < 1)
                {
                    Debug.LogError($"[CP-TELEPORT] ❌ Empty data");
                    return;
                }

                // Parse GC type tag format (matches binary readType @ 0x5E3C40)
                // Tag 0xFF = [0xFF][cstring\0]  (full class name)
                // Tag 0x00 = null reference
                // Tag 0x01 = [0x01][byte index]
                byte tag = data[0];
                string checkpointName = null;

                if (tag == 0xFF && data.Length > 2)
                {
                    // Read null-terminated string
                    int end = System.Array.IndexOf(data, (byte)0x00, 1);
                    if (end > 1)
                        checkpointName = System.Text.Encoding.ASCII.GetString(data, 1, end - 1);
                    Debug.LogError($"[CP-TELEPORT] Tag 0xFF → checkpoint name: '{checkpointName}'");
                }
                else if (tag == 0x00)
                {
                    Debug.LogError($"[CP-TELEPORT] Tag 0x00 = null ref — using obelisk rotator");
                    HandleObeliskTeleport(conn);
                    return;
                }
                else
                {
                    Debug.LogError($"[CP-TELEPORT] ❌ Unknown tag: 0x{tag:X2}");
                    return;
                }

                if (string.IsNullOrEmpty(checkpointName))
                {
                    Debug.LogError($"[CP-TELEPORT] ❌ Empty checkpoint name");
                    return;
                }

                // Look up checkpoint directly by GC class name
                var checkpoint = DatabaseLoader.Checkpoints.FirstOrDefault(c =>
                    c.id.Equals(checkpointName, StringComparison.OrdinalIgnoreCase));

                if (checkpoint == null)
                {
                    Debug.LogError($"[CP-TELEPORT] ❌ Checkpoint not in database: '{checkpointName}'");
                    return;
                }

                string destinationZone = checkpoint.zone;
                Debug.LogError($"[CP-TELEPORT] 🚀 Teleporting to '{destinationZone}' via checkpoint '{checkpointName}'");

                // Get destination zone's spawn point
                var destZone = _zones.Values.FirstOrDefault(z =>
                    z.name.Equals(destinationZone, StringComparison.OrdinalIgnoreCase));

                if (destZone != null)
                {
                    conn.PendingSpawnX = destZone.spawnX;
                    conn.PendingSpawnY = destZone.spawnY;
                    conn.PendingSpawnZ = destZone.spawnZ;
                }

                ChangeZone(conn, destinationZone, "");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CP-TELEPORT] ❌ Error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // Track last obelisk click index per player for cycling through destinations
        private Dictionary<string, int> _obeliskClickIndex = new Dictionary<string, int>();
        private HashSet<uint> _finalizedMonsterKills = new HashSet<uint>();
        private uint _lastClientXP = 0; // XP reported by DLL from client — used as truth when available
        private volatile int _dllPreConfirmCount = 0;    // DEPRECATED — kept for compat, always 0
        private HashSet<uint> _dllPreConfirmedEntities = new HashSet<uint>();
        private int _e1PacketCount = 0;
        private Dictionary<string, float> _lastRelayPosX = new Dictionary<string, float>();
        private Dictionary<string, float> _lastRelayPosY = new Dictionary<string, float>();
        private Dictionary<int, float> _forceRelayUntil = new Dictionary<int, float>();
        private Dictionary<string, byte> _lastRawMoveCount = new Dictionary<string, byte>();
        private HashSet<string> _stopSignalSent = new HashSet<string>();
        private int _mpDiagCounter = 0;

        // ═══════════════════════════════════════════════════════════════
        // DIFFICULTY SYSTEM — Binary: GroupClient stores at GC+0xB6 (0-3)
        // processMonsterDifficultySet @ 0x5FA320 validates cmp al, 4
        // Format string: "Your personal difficulty setting is now %s.
        //   This setting will not apply until you are the group leader."
        // Original server scaled monster HP/damage/XP per difficulty.
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Get the active difficulty for a connection.
        /// Group: uses leader's MonsterDifficulty. Solo: uses server default.
        /// Binary: only group leader's setting applies (format string proves it).
        /// </summary>
        public int GetDifficultyForConn(RRConnection conn)
        {
            var group = GroupManager.Instance.GetGroupForConn(conn.ConnId);
            if (group != null)
                return Math.Max(0, Math.Min(3, (int)group.MonsterDifficulty));
            return 0;
        }

        /// <summary>
        /// Get HP multiplier for difficulty level. Configurable in server.cfg.
        /// Default: Normal=1.0, Hard=1.5, Insane=2.0, Extreme=3.0
        /// Binary: EncounterDifficulty curve scales 4→20 (5x) over 100 levels.
        /// </summary>
        public float GetDifficultyHPMult(int difficulty)
        {
            return 1.0f;
        }

        /// <summary>
        /// Get XP multiplier for difficulty level. Higher difficulty = more XP.
        /// </summary>
        public float GetDifficultyXPMult(int difficulty)
        {
            return 1.0f;
        }

        /// <summary>
        /// Apply difficulty scaling to all monsters in an instance.
        /// Called once after initial spawn, before sending to clients.
        /// Scales MaxHP and CurrentHP. Client sees tougher monsters.
        /// </summary>
        private void ApplyDifficultyToMonsters(RRConnection conn, string instanceKey)
        {
            int diff = GetDifficultyForConn(conn);
            if (diff == 0) return;
            Debug.LogError($"[DIFFICULTY] Native group difficulty scaling is not applied without authored evidence diff={diff} instance='{instanceKey}'");
            return;
        }

        /// <summary>
        /// Send monsters to ALL group members in the same zone+instance.
        /// Binary: ServerEntityManager::writeClientInitMessages sends to specific client.
        /// All clients get IDENTICAL spawn packets + FollowClient + same RNG seed.
        /// </summary>
        public void SendMonsterToGroupInZone(string zoneName, uint instanceId, Monster monster)
        {
            foreach (var conn in _connections.Values)
            {
                if (!conn.IsConnected || !conn.IsSpawned) continue;
                if (!string.Equals(conn.CurrentZoneName, zoneName, StringComparison.OrdinalIgnoreCase)) continue;
                if (conn.InstanceId != instanceId) continue;

                SendMonsterToClient(conn, monster);
                Debug.LogError($"[GROUP-SPAWN] Sent {monster.Name} to {conn.LoginName} in {zoneName} inst={instanceId}");
            }
        }

        /// <summary>
        /// Broadcast monster despawn (kill) to ALL players in the same zone+instance.
        /// Binary: ServerEntityManager::writeRemoveMessages sends to all clients.
        /// </summary>
        public void BroadcastMonsterDespawnToZone(string zoneName, uint instanceId, uint entityId)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);  // BeginStream
            writer.WriteByte(0x05);  // EntityDespawn
            writer.WriteUInt16((ushort)entityId);
            writer.WriteByte(0x06);  // EndStream
            byte[] despawnPacket = writer.ToArray();

            foreach (var conn in _connections.Values)
            {
                if (!conn.IsConnected || !conn.IsSpawned) continue;
                if (!string.Equals(conn.CurrentZoneName, zoneName, StringComparison.OrdinalIgnoreCase)) continue;
                if (conn.InstanceId != instanceId) continue;

                SendToClient(conn, despawnPacket);
                Debug.LogError($"[GROUP-KILL] Sent despawn for entity {entityId} to {conn.LoginName}");
            }
        }

        /// <summary>
        /// Send RNG reseed (opcode 0x0C) to ALL players in same zone+instance.
        /// Binary: processRandomSeed on ClientEntityManager — reseeds room RNG.
        /// All clients must get same seed at same time for deterministic AI.
        /// </summary>
        public void BroadcastRNGSeedToZone(string zoneName, uint instanceId, uint seed)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);  // BeginStream
            writer.WriteByte(0x0C);  // RandomSeed
            writer.WriteUInt32(seed);
            writer.WriteByte(0x06);  // EndStream
            byte[] seedPacket = writer.ToArray();

            foreach (var conn in _connections.Values)
            {
                if (!conn.IsConnected || !conn.IsSpawned) continue;
                if (!string.Equals(conn.CurrentZoneName, zoneName, StringComparison.OrdinalIgnoreCase)) continue;
                if (conn.InstanceId != instanceId) continue;

                SendToClient(conn, seedPacket);
            }
            Debug.LogError($"[RNG-BROADCAST] Seed 0x{seed:X8} sent to inst={instanceId} in {zoneName}");
        }

        /// <summary>
        /// Send RNG reseed to OTHER players in same zone (not the sender — they get it in their tick stream).
        /// Binary: ServerEntityManager writes 0x0C to ALL client streams, but each player's own tick
        /// already includes their seed. This sends to the OTHERS.
        /// </summary>
        private void BroadcastRNGSeedToOthersInZone(RRConnection sender, uint seed)
        {
            if (string.IsNullOrEmpty(sender.CurrentZoneName)) return;

            var writer = new LEWriter();
            writer.WriteByte(0x07);  // BeginStream
            writer.WriteByte(0x0C);  // RandomSeed
            writer.WriteUInt32(seed);
            writer.WriteByte(0x06);  // EndStream
            byte[] seedPacket = writer.ToArray();

            foreach (var conn in _connections.Values)
            {
                if (conn == sender) continue;
                if (!conn.IsConnected || !conn.IsSpawned) continue;
                if (!string.Equals(conn.CurrentZoneName, sender.CurrentZoneName, StringComparison.OrdinalIgnoreCase)) continue;

                SendToClient(conn, seedPacket);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // INSTANCE SYSTEM — per binary architecture
        // Binary: ZoneClient::GotoInstance(int) — per-group instances
        // Binary: "You can't goto that player from here -- you must be in the same instance."
        // Binary: GroupClient::resetInstances() — clears dungeon instances
        //
        // PUBLIC zones (town, tutorial/dew valley): InstanceId = 0, everyone sees everyone
        // DUNGEON instances: InstanceId = groupId, only group members see each other
        // Solo player in dungeon: InstanceId = unique per-player
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Check if zone is public (everyone sees everyone) vs instanced (group-only).
        /// Binary: Town is the shared hub — NPCs, merchants, trainers, no mobs.
        /// Binary: Tutorial/Dew Valley is shared starter area with mobs — everyone sees everyone.
        /// Binary: All dungeon## zones are INSTANCED per group — private, mobs, group-only.
        /// Binary: "You can't goto that player from here -- you must be in the same instance."
        /// </summary>
        public static bool IsPublicZone(string zoneName)
        {
            if (string.IsNullOrEmpty(zoneName)) return true;
            string lower = zoneName.ToLower();
            // Town = safe hub, no mobs, public
            if (lower.Contains("town")) return true;
            // Tutorial/Dew Valley = starter area, has mobs but shared (not instanced)
            if (lower.Contains("tutorial")) return true;
            if (lower.Contains("dew") || lower.Contains("valley")) return true;
            // PvP hubs: pvp_start (Pwnston), pvp_hub, test_pvp1/2 — public gathering zones
            // (Match zones like pvpgroupduelmatch / deathmatch## stay INSTANCED so each
            // match has only its own participants — handled by name not matching here.)
            if (lower == "pvp_start" || lower == "pvp_hub") return true;
            if (lower == "test_pvp1" || lower == "test_pvp2") return true;
            // Everything else (dungeon##_level##, pvpgroup*, deathmatch##) is instanced per group/match
            // Each group gets their OWN dungeon with their OWN mobs
            return false;
        }

        /// <summary>
        /// Assign instance ID for a connection entering a zone.
        /// Binary: ZoneClient::GotoInstance(int) — all group members get same instance.
        /// Call at every zone transition AFTER setting CurrentZoneName.
        /// </summary>
        private void AssignInstanceId(RRConnection conn)
        {
            if (IsPublicZone(conn.CurrentZoneName))
            {
                conn.InstanceId = 0;
                StampRuntimeInstanceKey(conn, "public-zone");
                Debug.LogError($"[INSTANCE] {conn.LoginName} → PUBLIC zone '{conn.CurrentZoneName}' (instance 0)");
                return;
            }

            // ─────────────────────────────────────────────────────────────
            // DUNGEON instance assignment — defensive multi-step lookup.
            //
            // BUG-FIX (group members in different instances of same dungeon):
            // If a player entered a dungeon SOLO first (instance = 0x8XXXXXXX),
            // then later joined a group, their InstanceId never updates. When
            // a fellow group member then enters the same dungeon, the naive
            // logic would assign them group.GroupId, which doesn't match the
            // first player's stale solo ID — they can't see each other.
            //
            // Fix: before assigning a fresh instance, scan group members already
            // present in this exact zone and join their instance. Also fall back
            // to a login-name scan in case _connToGroup is stale for our connId.
            // ─────────────────────────────────────────────────────────────

            var group = GroupManager.Instance.GetGroupForConn(conn.ConnId);

            // Step 1: if we have a group, look for any member already in this zone
            //         and inherit their instance id.
            if (group != null)
            {
                foreach (var member in group.Members)
                {
                    if (member.ConnId == conn.ConnId) continue;
                    var memberConn = FindConnectionById(member.ConnId);
                    if (memberConn == null) continue;
                    if (memberConn.CurrentZoneName != conn.CurrentZoneName) continue;
                    if (memberConn.InstanceId == 0) continue; // public, useless

                    conn.InstanceId = memberConn.InstanceId;
                    StampRuntimeInstanceKey(conn, "group-member");
                    Debug.LogError($"[INSTANCE] {conn.LoginName} → DUNGEON '{conn.CurrentZoneName}' (joined group member {memberConn.LoginName}'s instance {conn.InstanceId:X8})");
                    return;
                }

                // No group member here yet — use group id as the instance.
                conn.InstanceId = group.GroupId;
                StampRuntimeInstanceKey(conn, "group");
                Debug.LogError($"[INSTANCE] {conn.LoginName} → DUNGEON '{conn.CurrentZoneName}' (group instance {group.GroupId})");
                return;
            }

            // Step 2: not in a group via connId. Maybe _connToGroup is stale or
            //         the invite just landed. Scan online players in this zone for
            //         someone whose group includes us by login name.
            if (!string.IsNullOrEmpty(conn.LoginName))
            {
                foreach (var other in _connections.Values)
                {
                    if (other == conn) continue;
                    if (other.CurrentZoneName != conn.CurrentZoneName) continue;
                    if (other.InstanceId == 0) continue;

                    var otherGroup = GroupManager.Instance.GetGroupForConn(other.ConnId);
                    if (otherGroup == null) continue;

                    bool weAreMember = otherGroup.Members.Any(m =>
                        string.Equals(m.LoginName, conn.LoginName, System.StringComparison.OrdinalIgnoreCase));
                    if (!weAreMember) continue;

                    conn.InstanceId = other.InstanceId;
                    StampRuntimeInstanceKey(conn, "stale-group-recovery");
                    Debug.LogError($"[INSTANCE] {conn.LoginName} → DUNGEON '{conn.CurrentZoneName}' (joined via stale-group-recovery, latched onto {other.LoginName}'s instance {conn.InstanceId:X8})");
                    return;
                }
            }

            // Step 3: truly solo. Keep one active instance per selected character+zone.
            conn.InstanceId = AllocateSoloDungeonInstanceId(conn, conn.CurrentZoneName);
            StampRuntimeInstanceKey(conn, "solo");
            Debug.LogError($"[INSTANCE] {conn.LoginName} → DUNGEON '{conn.CurrentZoneName}' (SOLO instance {conn.InstanceId:X8}, owner {GetSoloDungeonInstanceOwnerKey(conn, conn.CurrentZoneName)})");
        }

        /// <summary>
        /// Check if two players can see each other.
        /// Binary: Public zones = everyone. Dungeons = same instance (same group) only.
        /// </summary>
        public bool CanSeePlayer(RRConnection a, RRConnection b)
        {
            if (a == b) return false;
            if (!a.IsConnected || !b.IsConnected) return false;
            if (!a.IsSpawned || !b.IsSpawned) return false;
            if (a.CurrentZoneGcType != b.CurrentZoneGcType) return false;
            // Instance check: in public zones both are 0 (always match).
            // In dungeons, must be same group instance.
            if (a.InstanceId != b.InstanceId) return false;
            return true;
        }

        /// <summary>
        /// Get the instance-qualified zone key for mob tracking.
        /// In public zones, zone name alone. In dungeons, zone + instance ID.
        /// This ensures each group gets their OWN set of mobs.
        /// </summary>
        public string GetInstanceZoneKey(RRConnection conn)
        {
            if (conn == null)
                return "";
            string computed = BuildRuntimeInstanceKey(conn);
            if (string.Equals(conn.RuntimeInstanceKey, computed, StringComparison.OrdinalIgnoreCase))
                return conn.RuntimeInstanceKey;
            conn.RuntimeInstanceKey = computed;
            return computed;
        }

        private string BuildRuntimeInstanceKey(RRConnection conn)
        {
            if (conn == null)
                return "";
            if (IsPublicZone(conn.CurrentZoneName))
                return conn.CurrentZoneName ?? "";
            return $"{conn.CurrentZoneName}_inst{conn.InstanceId}";
        }

        private void StampRuntimeInstanceKey(RRConnection conn, string source)
        {
            if (conn == null)
                return;
            conn.RuntimeInstanceKey = BuildRuntimeInstanceKey(conn);
            Debug.LogError($"[INSTANCE] runtimeKey='{conn.RuntimeInstanceKey}' source={source ?? "unknown"} zone='{conn.CurrentZoneName}' instance={conn.InstanceId:X8}");
        }

        // ═══════════════════════════════════════════════════════════════
        // GROUP PROTOCOL — Channel 0x0B per binary
        // Binary: Main dispatcher at VA 0x458B9B routes 0x0B to GroupClient
        // Binary: GroupClient::processMessage at VA 0x5F7DD0
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Build GroupMemberInfo from a connection for group packets.
        /// Binary-verified: readMember @ 0x5FA6D0 reads only:
        ///   uint32 charSQLID, string name, uint32 avatarEntityId, byte isOnline
        /// </summary>
        private GroupMemberInfo BuildGroupMemberInfo(RRConnection conn)
        {
            uint avatarId = GetPlayerAvatarId(conn.LoginName);
            string charName = conn.LoginName ?? "Unknown";
            uint charSqlId = GetCharSqlId(conn);

            if (_selectedCharacter.TryGetValue(conn.LoginName, out var ch))
                charName = ch.Name ?? charName;

            // Cache on GroupMember for offline access
            var group = GroupManager.Instance.GetGroupForConn(conn.ConnId);
            if (group != null)
            {
                var gm = group.Members.Find(m => m.ConnId == conn.ConnId);
                if (gm != null)
                {
                    gm.CharSqlId = charSqlId;
                    gm.AvatarEntityId = avatarId;
                    gm.Name = charName;
                }
            }

            return new GroupMemberInfo
            {
                CharSQLID = charSqlId,
                Name = charName,
                AvatarEntityId = avatarId,
                IsOnline = true
            };
        }

        /// <summary>Build GroupMemberInfo from cached GroupMember data (for offline members).</summary>
        private GroupMemberInfo BuildGroupMemberInfoFromCache(Managers.GroupMember gm)
        {
            return new GroupMemberInfo
            {
                CharSQLID = gm.CharSqlId,
                Name = gm.Name ?? "Unknown",
                AvatarEntityId = gm.AvatarEntityId,
                IsOnline = gm.IsOnline
            };
        }

        /// <summary>Get charSqlId from _selectedCharacter (reliable) or conn.CharSqlId (fallback).</summary>
        private uint GetCharSqlId(RRConnection conn)
        {
            if (conn == null) return 0;
            if (!string.IsNullOrEmpty(conn.LoginName)
                && _selectedCharacter.TryGetValue(conn.LoginName, out var ch)
                && ch.Id != 0)
                return (uint)ch.Id;
            if (conn.CharSqlId != 0) return conn.CharSqlId;
            return (uint)(conn.ConnId + 1);
        }

        /// <summary>Find a connection in a group by charSqlId.</summary>
        private RRConnection FindGroupMemberByCharSqlId(Managers.Group group, uint charSqlId)
        {
            foreach (var m in group.Members)
            {
                var mc = FindConnectionById(m.ConnId);
                if (mc != null && GetCharSqlId(mc) == charSqlId) return mc;
            }
            return null;
        }
        public void SpawnAdminShop(RRConnection conn, Action<RRConnection, string> sendMessage)
        {
            DespawnAdminShop(conn, sendMessage);
            string[] adminMerchants = {
                "world.town.npc.AdminWeaponVendor",
                "world.town.npc.AdminArmorVendor",
                "world.town.npc.AdminMiscVendor"
            };
            string[] displayTypes = {
                "world.town.npc.VendorWeapon1",
                "world.town.npc.VendorWeapon2",
                "world.town.npc.VendorWeapon3"
            };
            float baseX = conn.PlayerPosX + 3;
            float baseY = conn.PlayerPosY;
            float baseZ = conn.PlayerPosZ;
            var shopNPCs = new List<ZoneNPC>();
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            for (int n = 0; n < adminMerchants.Length; n++)
            {
                string gcClass = adminMerchants[n];
                string name = gcClass.Split('.')[gcClass.Split('.').Length - 1];
                float npcX = baseX + (n * 40);
                float npcY = baseY;
                ushort npcId = (ushort)_nextEntityId++;
                ushort behaviorId = (ushort)_nextEntityId++;
                ushort skillsId = (ushort)_nextEntityId++;
                ushort manipulatorsId = (ushort)_nextEntityId++;
                ushort modifiersId = (ushort)_nextEntityId++;
                ushort merchantId = (ushort)_nextEntityId++;
                uint npcHPWire = ResolveAuthoredUnitMaxHealthWire(displayTypes[n]);
                var npc = new ZoneNPC
                {
                    Id = npcId,
                    UnitBehaviorId = behaviorId,
                    GCClass = displayTypes[n],

                    Name = name,
                    PosX = npcX,
                    PosY = npcY,
                    PosZ = baseZ,
                    Heading = 0,
                    IsMerchant = true,
                    MerchantId = merchantId,
                    IsAdminMerchant = true,
                    IsTrainer = false,
                    TrainerId = 0
                };
                if (!_zoneNPCs.ContainsKey(conn.CurrentZoneId))
                    _zoneNPCs[conn.CurrentZoneId] = new List<ZoneNPC>();
                _zoneNPCs[conn.CurrentZoneId].Add(npc);
                shopNPCs.Add(npc);
                _npcPositions[npcId] = (npcX, npcY, baseZ);
                _allEntityPositions[npcId] = (npcX, npcY, baseZ);
                writer.WriteByte(0x01);
                writer.WriteUInt16(npcId);
                WriteGCType(writer, displayTypes[n], preserveCase: true);
                writer.WriteByte(0x32);
                writer.WriteUInt16(npcId);
                writer.WriteUInt16(behaviorId);
                WriteGCType(writer, "npc.base.behavior", preserveCase: false);
                writer.WriteByte(0x01);
                writer.WriteByte(0xFF); writer.WriteByte(0x00); writer.WriteByte(0x00); writer.WriteByte(0x01);
                writer.WriteByte(0x85); writer.WriteByte(0x00);
                for (int i = 0; i < 5; i++) writer.WriteUInt32(0);
                writer.WriteByte(0x00);
                writer.WriteByte(0xFF); writer.WriteByte(0x00); writer.WriteByte(0x00);
                writer.WriteByte(0x00); writer.WriteByte(0x00);
                writer.WriteUInt32(0); writer.WriteUInt32(0);
                writer.WriteByte(0x32);
                writer.WriteUInt16(npcId);
                writer.WriteUInt16(skillsId);
                WriteGCType(writer, "skills", preserveCase: false);
                writer.WriteByte(0x01);
                writer.WriteByte(0xFF); writer.WriteByte(0xFF); writer.WriteByte(0xFF); writer.WriteByte(0xFF);
                writer.WriteByte(0x00); writer.WriteByte(0x01);
                WriteGCType(writer, "skills.professions.Warrior", preserveCase: true);
                writer.WriteByte(0x32);
                writer.WriteUInt16(npcId);
                writer.WriteUInt16(manipulatorsId);
                WriteGCType(writer, "manipulators", preserveCase: false);
                writer.WriteByte(0x01); writer.WriteByte(0x00);
                writer.WriteByte(0x32);
                writer.WriteUInt16(npcId);
                writer.WriteUInt16(modifiersId);
                WriteGCType(writer, "modifiers", preserveCase: false);
                writer.WriteByte(0x01);
                writer.WriteUInt32(0); writer.WriteByte(0x00); writer.WriteUInt32(0);
                MerchantManager.EnsureInventoryForLevel(displayTypes[n], GetPlayerState(conn.ConnId.ToString()).Level);
                MerchantManager.WriteMerchantComponent(writer, displayTypes[n], npcId, merchantId);
                writer.WriteByte(0x02);
                writer.WriteUInt16(npcId);
                writer.WriteUInt32(0x06);
                writer.WriteInt32((int)(npcX * 256));
                writer.WriteInt32((int)(npcY * 256));
                writer.WriteInt32((int)(baseZ * 256));
                writer.WriteInt32(0);
                writer.WriteByte(0x00); writer.WriteByte(0x01);
                for (int i = 0; i < 8; i++) writer.WriteUInt32(0);
                writer.WriteByte(0x35);
                writer.WriteUInt16(behaviorId);
                writer.WriteByte(0x04); writer.WriteByte(0x11); writer.WriteByte(0x00);
                writer.WriteInt32((int)(npcX * 256));
                writer.WriteInt32((int)(npcY * 256));
                writer.WriteInt32((int)(baseZ * 256));
                writer.WriteByte(0x02);
                writer.WriteUInt32(npcHPWire);
            }
            writer.WriteByte(0x06);
            byte[] adminPacket = writer.ToArray();
            Debug.LogError($"[AdminShop] Total admin shop packet size: {adminPacket.Length} bytes");
            SendToClient(conn, adminPacket);
            _adminShopNPCs[conn.ConnId] = shopNPCs;
            sendMessage(conn, "[Shop] 3 admin vendors spawned near you. @shop close to despawn.");
        }

        public void DespawnAdminShop(RRConnection conn, Action<RRConnection, string> sendMessage)
        {
            if (!_adminShopNPCs.TryGetValue(conn.ConnId, out var npcs) || npcs.Count == 0)
                return;
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            foreach (var npc in npcs)
            {
                writer.WriteByte(0x05);
                writer.WriteUInt16((ushort)npc.Id);
                if (_zoneNPCs.TryGetValue(conn.CurrentZoneId, out var zoneList))
                    zoneList.Remove(npc);
            }
            writer.WriteByte(0x06);
            SendToClient(conn, writer.ToArray());
            _adminShopNPCs.Remove(conn.ConnId);
            sendMessage(conn, "[Shop] Admin vendors despawned.");
        }
        /// <summary>
        /// Send group remove packet when player leaves zone or disconnects.
        /// Binary: type 0x1B = processRemoveUser at 0x5FA510.
        /// </summary>
        private void SendGroupRemoveUser(RRConnection conn)
        {
            var group = GroupManager.Instance.GetGroupForConn(conn.ConnId);
            if (group == null) return;

            uint charSqlId = GetCharSqlId(conn);
            byte[] removePacket = GroupPackets.BuildProcessRemoveUser(group.GroupId, charSqlId);

            foreach (var m in group.Members)
            {
                if (m.ConnId == conn.ConnId) continue;
                var memberConn = FindConnectionById(m.ConnId);
                if (memberConn != null)
                    SendToClient(memberConn, removePacket);
            }
            Debug.LogError($"[GROUP] Sent processRemoveUser for {conn.LoginName}");
        }

    }

    /// <summary>
    /// Tracks active modifiers per player for persistence across zone transitions
    /// and login/logout. Binary-verified format from Modifier::readData @ 0x4FF390.
    /// </summary>
    public class ActiveModifier
    {
        public string GCType;       // e.g. "avatar.base.FreePlayerExperienceModifier"
        public uint Id;             // [+0x78] unique per modifier instance
        public byte Level;          // [+0x86]
        public uint PowerLevel;     // [+0x80] Fixed32
        public uint Duration;       // [+0x7C] remaining ms, 0=permanent
        public byte SourceIsSelf;   // [+0x87] bit 0 = permanent/self-sourced
        public DateTime AddedAt;    // for duration tracking
    }

    public class ModifierTracker
    {
        private readonly Dictionary<string, List<ActiveModifier>> _playerModifiers
            = new Dictionary<string, List<ActiveModifier>>(StringComparer.OrdinalIgnoreCase);

        private uint _nextModifierId = 1;

        public uint NextId() => _nextModifierId++;

        public void TrackModifier(string loginName, ActiveModifier mod)
        {
            if (!_playerModifiers.TryGetValue(loginName, out var list))
            {
                list = new List<ActiveModifier>();
                _playerModifiers[loginName] = list;
            }
            list.RemoveAll(m => string.Equals(m.GCType, mod.GCType, StringComparison.OrdinalIgnoreCase));
            list.Add(mod);
            Debug.LogError($"[MOD-TRACK] Tracked '{mod.GCType}' id={mod.Id} for {loginName} (total={list.Count})");
        }

        public bool RemoveModifier(string loginName, string gcType)
        {
            if (!_playerModifiers.TryGetValue(loginName, out var list)) return false;
            int removed = list.RemoveAll(m => string.Equals(m.GCType, gcType, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
                Debug.LogError($"[MOD-TRACK] Removed '{gcType}' from {loginName} (remaining={list.Count})");
            return removed > 0;
        }

        public bool RemoveModifierById(string loginName, uint modId)
        {
            if (!_playerModifiers.TryGetValue(loginName, out var list)) return false;
            int removed = list.RemoveAll(m => m.Id == modId);
            if (removed > 0)
                Debug.LogError($"[MOD-TRACK] Removed modId={modId} from {loginName} (remaining={list.Count})");
            return removed > 0;
        }

        /// <summary>
        /// Get active modifiers with remaining duration calculated.
        /// Returns COPIES — never mutates originals.
        /// Duration is in TICKS (1000/24 ≈ 41.667 ticks/sec) for wire format.
        /// Adds 3-second buffer to compensate for zone loading delay.
        /// </summary>
        public List<ActiveModifier> GetModifiers(string loginName)
        {
            if (!_playerModifiers.TryGetValue(loginName, out var list))
                return new List<ActiveModifier>();

            const double TICKS_PER_SEC = 1000.0 / 24.0;
            const double ZONE_BUFFER_TICKS = 3.0 * TICKS_PER_SEC;
            var result = new List<ActiveModifier>();
            var now = DateTime.UtcNow;
            foreach (var mod in list)
            {
                if (mod.Duration == 0)
                {
                    result.Add(mod); // permanent — return as-is
                }
                else
                {
                    // Timed modifier — calculate remaining duration in ticks
                    double elapsedTicks = (now - mod.AddedAt).TotalSeconds * TICKS_PER_SEC + ZONE_BUFFER_TICKS;
                    if (elapsedTicks < mod.Duration)
                    {
                        // Return a COPY with remaining duration — don't mutate original
                        result.Add(new ActiveModifier
                        {
                            GCType = mod.GCType,
                            Id = mod.Id,
                            Level = mod.Level,
                            PowerLevel = mod.PowerLevel,
                            Duration = (uint)(mod.Duration - elapsedTicks),
                            SourceIsSelf = mod.SourceIsSelf,
                            AddedAt = mod.AddedAt
                        });
                    }
                }
            }
            // Clean up expired modifiers
            list.RemoveAll(m => m.Duration > 0 && (DateTime.UtcNow - m.AddedAt).TotalSeconds * TICKS_PER_SEC >= m.Duration);
            return result;
        }

        public void ClearPlayer(string loginName)
        {
            _playerModifiers.Remove(loginName);
        }

        public void SaveToDb(string loginName, int characterId)
        {
            try
            {
                using (var conn = GameDatabase.GetConnection())
                using (var tx = conn.BeginTransaction())
                {
                    GameDatabase.ExecuteNonQuery(conn,
                        "DELETE FROM character_modifiers WHERE character_id=@cid",
                        ("@cid", characterId));

                    var mods = GetModifiers(loginName);
                    foreach (var mod in mods)
                    {
                        GameDatabase.ExecuteNonQuery(conn,
                            @"INSERT INTO character_modifiers
                              (character_id, gc_type, modifier_id, level, power_level, duration_remaining, source_is_self)
                              VALUES (@cid, @gc, @mid, @lv, @pl, @dur, @sis)",
                            ("@cid", characterId),
                            ("@gc", mod.GCType),
                            ("@mid", (int)mod.Id),
                            ("@lv", (int)mod.Level),
                            ("@pl", (int)mod.PowerLevel),
                            ("@dur", (int)mod.Duration),
                            ("@sis", (int)mod.SourceIsSelf));
                    }
                    tx.Commit();
                    Debug.LogError($"[MOD-TRACK] Saved {mods.Count} modifiers for charId={characterId}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MOD-TRACK] SaveToDb failed: {ex.Message}");
            }
        }

        public void LoadFromDb(string loginName, int characterId)
        {
            try
            {
                using (var conn = GameDatabase.GetConnection())
                {
                    using (var reader = GameDatabase.ExecuteReader(conn,
                        "SELECT gc_type, modifier_id, level, power_level, duration_remaining, source_is_self FROM character_modifiers WHERE character_id=@cid",
                        ("@cid", characterId)))
                    {
                        var list = new List<ActiveModifier>();
                        while (reader.Read())
                        {
                            var mod = new ActiveModifier
                            {
                                GCType = reader.GetString(0),
                                Id = (uint)reader.GetInt32(1),
                                Level = (byte)reader.GetInt32(2),
                                PowerLevel = (uint)reader.GetInt32(3),
                                Duration = (uint)reader.GetInt32(4),
                                SourceIsSelf = (byte)reader.GetInt32(5),
                                AddedAt = DateTime.UtcNow
                            };
                            list.Add(mod);
                            if (mod.Id >= _nextModifierId)
                                _nextModifierId = mod.Id + 1;
                        }

                        if (list.Count > 0)
                        {
                            _playerModifiers[loginName] = list;
                            Debug.LogError($"[MOD-TRACK] Loaded {list.Count} modifiers for {loginName} (charId={characterId})");
                        }
                    }

                    GameDatabase.ExecuteNonQuery(conn,
                        "DELETE FROM character_modifiers WHERE character_id=@cid",
                        ("@cid", characterId));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MOD-TRACK] LoadFromDb failed: {ex.Message}");
            }
        }
    }


}
