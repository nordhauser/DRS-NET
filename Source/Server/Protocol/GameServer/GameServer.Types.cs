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
    {  // Don't flush until spawn complete

        private enum EntitySynchInfoOwner
        {
            Unknown,
            NonUnit,
            Avatar,
            Monster
        }

        private struct EntitySynchInfoDecision
        {
            public bool Allow;
            public byte Flags;
            public uint HPWire;
            public EntitySynchInfoOwner Owner;
            public string Reason;
            public uint OwnerEntityId;
            public uint ComponentId;
            public byte Subtype;
            public float NativeNow;
            public uint ValidationCutoffTick;
            public float ValidationCutoffTime;
            public string Provenance;
            public string RuntimeInstanceKey;
            public uint SchedulerTick;
            public bool SubEntityPhase;
            public int RngPos;
            public string HpMutationSource;

            public static EntitySynchInfoDecision Empty(EntitySynchInfoOwner owner, string reason)
            {
                return new EntitySynchInfoDecision { Allow = true, Flags = 0x00, HPWire = 0, Owner = owner, Reason = reason };
            }

            public static EntitySynchInfoDecision HP(EntitySynchInfoOwner owner, uint hpWire, string reason, uint ownerEntityId = 0, uint componentId = 0, byte subtype = 0, float nativeNow = -1f, string provenance = null, uint validationCutoffTick = 0, float validationCutoffTime = -1f, string runtimeInstanceKey = null, uint schedulerTick = 0, bool subEntityPhase = false, int rngPos = -1, string hpMutationSource = null)
            {
                return new EntitySynchInfoDecision { Allow = true, Flags = 0x02, HPWire = hpWire, Owner = owner, Reason = reason, OwnerEntityId = ownerEntityId, ComponentId = componentId, Subtype = subtype, NativeNow = nativeNow, Provenance = provenance, ValidationCutoffTick = validationCutoffTick, ValidationCutoffTime = validationCutoffTime, RuntimeInstanceKey = runtimeInstanceKey, SchedulerTick = schedulerTick, SubEntityPhase = subEntityPhase, RngPos = rngPos, HpMutationSource = hpMutationSource };
            }

            public static EntitySynchInfoDecision Block(EntitySynchInfoOwner owner, string reason)
            {
                return new EntitySynchInfoDecision { Allow = false, Flags = 0x00, HPWire = 0, Owner = owner, Reason = reason };
            }

            public ResolvedEntitySynchInfo ToResolved(uint fallbackOwnerEntityId, uint fallbackComponentId, byte fallbackSubtype, float fallbackNativeNow, string fallbackProvenance)
            {
                uint ownerEntityId = OwnerEntityId != 0 ? OwnerEntityId : fallbackOwnerEntityId;
                uint componentId = ComponentId != 0 ? ComponentId : fallbackComponentId;
                byte resolvedSubtype = Subtype != 0 ? Subtype : fallbackSubtype;
                float nativeNow = NativeNow >= 0f ? NativeNow : fallbackNativeNow;
                string provenance = !string.IsNullOrWhiteSpace(Provenance) ? Provenance : fallbackProvenance;
                return new ResolvedEntitySynchInfo(new EntitySynchInfoPayload(Flags, HPWire), ownerEntityId, componentId, resolvedSubtype, nativeNow, Reason, provenance, ValidationCutoffTick, ValidationCutoffTime, RuntimeInstanceKey, SchedulerTick, SubEntityPhase, RngPos, HpMutationSource);
            }
        }

        private enum PendingMonsterBehaviorKind
        {
            Move,
            Attack
        }

        private struct PendingMonsterBehaviorUpdate
        {
            public PendingMonsterBehaviorKind Kind;
            public uint EntityId;
            public uint BehaviorId;
            public int ConnId;
            public uint TargetEntityId;
            public float TargetX;
            public float TargetY;
            public byte UseFlags;
            public byte SessionId;
            public bool UseTargetAction;
            public float QueuedAt;
        }

        private TcpListener _listener;
        // UDP Session tracking for encryption
        private class UDPSession
        {
            public IPEndPoint Endpoint;
            public byte[] BlowfishKey;
            public BlowfishEngine EncryptEngine;
            public BlowfishEngine DecryptEngine;
            public bool IsEstablished;
            public RRConnection Connection;

            public void InitializeCipher(byte[] key)
            {
                BlowfishKey = key;
                EncryptEngine = new BlowfishEngine();
                DecryptEngine = new BlowfishEngine();
                EncryptEngine.Init(true, new KeyParameter(key));
                DecryptEngine.Init(false, new KeyParameter(key));
            }
        }

        private Dictionary<string, UDPSession> _udpSessions = new Dictionary<string, UDPSession>();

        // ═══ DJB2 hash → GC class lookup for skill training (binary-verified) ═══
        // Entity ref in train request = DJB2(lowercase GC class path)
        // Verified: 0xA6CCC405=1HMeleeSpeedBuff, 0xBC568FC5=PoisonResistBuff,
        //           0x86501370=Sprint, 0x5E5B060A=Blight (4/4 captures matched)
        private static readonly Dictionary<uint, string> _skillHashToGcClass = new Dictionary<uint, string>
        {
            { 0xA6CCC405u, "skills.generic.1HMeleeSpeedBuff" },
            { 0xB301E9E6u, "skills.generic.2HMeleeSpeedBuff" },
            { 0xE70E75EDu, "skills.generic.AggroIncreaseModBuff" },
            { 0x5E5B060Au, "skills.generic.Blight" },
            { 0xCCA86938u, "skills.generic.BlockKnockdownProcPassive" },
            { 0x3F7F0F7Du, "skills.generic.Butcher" },
            { 0x6063983Au, "skills.generic.Charge" },
            { 0x60ADE560u, "skills.generic.Cleave" },
            { 0x393590D7u, "skills.generic.CleaveUpgradeProcPassive" },
            { 0x449319F1u, "skills.generic.DivineDamageBuff" },
            { 0xA86DE2F4u, "skills.generic.DivineIntervention" },
            { 0x31E9D9CFu, "skills.generic.DivineMeleeAttack" },
            { 0x99C3D77Bu, "skills.generic.DivineRay" },
            { 0x5D83082Cu, "skills.generic.DivineResistBuff" },
            { 0x1F1A7C64u, "skills.generic.DivineResistPassive" },
            { 0xAC80BBEEu, "skills.generic.FearMeleeAttack" },
            { 0xC4A217A3u, "skills.generic.FearResistModPassive" },
            { 0xE588072Cu, "skills.generic.FearShot" },
            { 0x997C6A0Au, "skills.generic.FighterClassPassive" },
            { 0x40243947u, "skills.generic.FireBolt" },
            { 0x4024C5DBu, "skills.generic.FireCone" },
            { 0x63425376u, "skills.generic.FireCurseShot" },
            { 0xB2A6D958u, "skills.generic.FireDamageBuff" },
            { 0x8C80D7DDu, "skills.generic.FireMeleeSummon" },
            { 0xCB96C793u, "skills.generic.FireResistBuff" },
            { 0xA142566Bu, "skills.generic.FireResistPassive" },
            { 0x402CE606u, "skills.generic.FireRing" },
            { 0x402D6E54u, "skills.generic.FireShot" },
            { 0xBD9F10B4u, "skills.generic.HealSelf" },
            { 0x2F4A0032u, "skills.generic.IceBolt" },
            { 0xDE3EE483u, "skills.generic.IceDamageBuff" },
            { 0x54B3785Du, "skills.generic.IceMultiBolt" },
            { 0xF72ED2BEu, "skills.generic.IceResistBuff" },
            { 0x4BFA15B6u, "skills.generic.IceResistPassive" },
            { 0x2F53353Fu, "skills.generic.IceShot" },
            { 0xF1B72961u, "skills.generic.IceTargetedBurst" },
            { 0x56823B18u, "skills.generic.IceTargetedBurstUpgradeProcPassive" },
            { 0x327BF3B8u, "skills.generic.InfectiousPoisonUpgradeProcPassive" },
            { 0x90E6BBFBu, "skills.generic.MageClassPassive" },
            { 0xF3EBCFABu, "skills.generic.MagicDamageModPassive" },
            { 0x448B48B7u, "skills.generic.ManaSelf" },
            { 0x94B500E6u, "skills.generic.ManaShield" },
            { 0x7502AD10u, "skills.generic.MeleeAttackRatingModPassive" },
            { 0x8E2E23BCu, "skills.generic.MeleeAttackSpeedModPassive" },
            { 0x9B9699A5u, "skills.generic.MeleeDamageReflectionBuff" },
            { 0x4AC3B99Fu, "skills.generic.MinMoveSpeedBuff" },
            { 0x82EEC8C9u, "skills.generic.MonsterBaitHealthModPassive" },
            { 0xC3578423u, "skills.generic.NoxiousShot" },
            { 0x45E3A604u, "skills.generic.PenetrateKnockdownShot" },
            { 0x5FAE1AA6u, "skills.generic.PoisonBlastRadius" },
            { 0xA366A18Au, "skills.generic.PoisonDamageBuff" },
            { 0xBC568FC5u, "skills.generic.PoisonResistBuff" },
            { 0xBB68895Du, "skills.generic.PoisonResistPassive" },
            { 0xC30F7906u, "skills.generic.PoisonShot" },
            { 0x2515F184u, "skills.generic.PoisonTrail" },
            { 0xF7B882A1u, "skills.generic.RangeAttackSpeedModPassive" },
            { 0x83AC0575u, "skills.generic.RangedSpeedBuff" },
            { 0x582F9DC0u, "skills.generic.RangerClassPassive" },
            { 0xBBA98687u, "skills.generic.ShadowBolt" },
            { 0x80C11698u, "skills.generic.ShadowDamageBuff" },
            { 0xAA5741BAu, "skills.generic.ShadowLightning" },
            { 0x5E4290C8u, "skills.generic.ShadowLightningKnockdown" },
            { 0xECA5DFB1u, "skills.generic.ShadowLightningUpgradeProcPassive" },
            { 0xBBB21055u, "skills.generic.ShadowRage" },
            { 0x99B104D3u, "skills.generic.ShadowResistBuff" },
            { 0x12B28BABu, "skills.generic.ShadowResistPassive" },
            { 0x892B23BBu, "skills.generic.ShadowTendrils" },
            { 0x14D911E1u, "skills.generic.SlowDeBuff" },
            { 0x3AA3DD1Du, "skills.generic.SnowmanFreezeAura" },
            { 0x756AB8F5u, "skills.generic.SnowmanHealthModAuraBuff" },
            { 0xF7D5A663u, "skills.generic.SnowManIceDamageProcAuraModBuff" },
            { 0x86501370u, "skills.generic.Sprint" },
            { 0x2ADDC8C3u, "skills.generic.Stomp" },
            { 0x7A0B94B7u, "skills.generic.StunResistBuff" },
            { 0xDFF88E97u, "skills.generic.SummonerClassPassive" },
            { 0x1F638F77u, "skills.generic.SummonMonsterBait" },
            { 0xDD957B31u, "skills.generic.SummonBlingGnome" },
            { 0x7E1353D2u, "skills.Generic.SummonSnowman" },
            { 0xE9DDA4DFu, "skills.generic.Teleport" },
        };
        // ═══ DROPPED ITEM PERSISTENCE ═══
        public class DroppedItemInfo
        {
            public GCObject Item;
            public long DbId;           // row ID in dropped_items table (0 = not yet saved)
            public string Zone;         // CurrentZoneGcType
            public uint ZoneId;         // CurrentZoneId
            public uint InstanceId;     // conn.InstanceId
            public float PosX, PosY, PosZ;
            public int PlayerLevel;
            public string DroppedBy;
            public int Quantity = 1;    // stack count when dropped
            public bool IsQuestItem;    // true = call NotifyQuestItemAcquired on pickup
            public bool IsGoldDrop;     // true = gold pile, credit AddCurrency on pickup
            public uint GoldAmount;     // gold value (only if IsGoldDrop)
        }
        private Dictionary<ushort, DroppedItemInfo> _droppedItems = new Dictionary<ushort, DroppedItemInfo>();

        /// <summary>Goto objective metadata from Q*.gc files.</summary>
        private struct GotoGCData
        {
            public readonly string TargetZone;
            public readonly string TargetEntity;
            public readonly int Range;
            public GotoGCData(string zone, string entity, int range)
            { TargetZone = zone; TargetEntity = entity; Range = range; }
        }

        /// <summary>
        /// Token Master reward picker. Maps (class, slot, tier) → a single
        /// curated gcType drawn from the WishingWell* and TokenReward* IG
        /// families authored in `Database/gc/`. Every gcType returned here
        /// is verified bare in `Database/GCDictionary.dict` (no `items.pal.`
        /// prefix — those are emitted by `GCObject.GetPacketGCClassFor`).
        /// Returns "" when no mapping exists — caller skips the reward.
        ///
        ///   classKey: "fi" | "ma" | "rg" | "jewelry"
        ///   slotKey:  "helm" | "boots" | "gloves" | "shoulders" | "shield"
        ///             | "body" | "ring" | "amulet" | "1hweapon" | "2hweapon"
        ///             | "jewelry"
        ///   tier:     "rare" | "unique" | "mythic"
        /// </summary>
        // Token/wishing-well quest rewards are resolved through authored RewardItemGenerator data.
        // Legacy hardcoded reward pickers were removed so unresolved SQL/import gaps stay visible.
        private void TryAutoCompleteWishingWellQuest(RRConnection conn, uint questHash)
        {
            if (!DatabaseLoader.QuestsByHash.TryGetValue(questHash, out var questData)) return;
            if (string.IsNullOrEmpty(questData.id)) return;
            bool isWell = questData.id.StartsWith("world.town.quest.well", StringComparison.OrdinalIgnoreCase);
            bool isToken = questData.id.StartsWith("world.town.quest.token.", StringComparison.OrdinalIgnoreCase)
                           && !questData.id.EndsWith(".Debug_TokenGive", StringComparison.OrdinalIgnoreCase);
            if (!isWell && !isToken)
                return;

            string connId = conn.ConnId.ToString();
            var playerState = QuestManager.Instance.GetPlayerState(connId);
            if (playerState == null) return;
            var activeQuest = playerState.ActiveQuests.FirstOrDefault(q =>
                q.QuestId.Equals(questData.id, StringComparison.OrdinalIgnoreCase));
            if (activeQuest == null)
            {
                Debug.LogError($"[WELL] AcceptConfirmed but no active quest entry for {questData.id} — skip auto-complete");
                return;
            }

            Debug.LogError($"[WELL] Auto-complete check for {questData.id} (objCount={activeQuest.Objectives?.Count ?? 0})");

            // Tick item objectives by scanning current inventory.
            if (activeQuest.Objectives != null && activeQuest.Objectives.Count > 0 &&
                _playerInventoryItems.ContainsKey(connId))
            {
                foreach (var obj in activeQuest.Objectives)
                {
                    if (obj.Type == null || !obj.Type.Equals("item", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (string.IsNullOrEmpty(obj.Target)) continue;
                    int found = 0;
                    foreach (var kvp in _playerInventoryItems[connId])
                    {
                        if (kvp.Value.item?.GCClass == null) continue;
                        if (!kvp.Value.item.GCClass.Equals(obj.Target, StringComparison.OrdinalIgnoreCase))
                            continue;
                        int sc = GetStackCount(connId, kvp.Key);
                        found += sc > 0 ? sc : 1;
                    }
                    obj.Current = Math.Min(obj.Required, found);
                    Debug.LogError($"[WELL]   objective '{obj.Label}' {obj.Current}/{obj.Required} (inventory has {found} of {obj.Target})");
                }
                QuestManager.Instance.SendProgressPacket(conn, activeQuest.InstanceId, activeQuest);
            }

            bool allDone = activeQuest.Objectives == null
                || activeQuest.Objectives.Count == 0
                || activeQuest.Objectives.All(o => o.IsComplete);

            if (!allDone)
            {
                // Can't complete (no King's Coin). Roll back the accept so the player
                // doesn't end up with a stuck quest in the log. Re-broadcast the
                // available list so the Well NPC's ! marker comes back — HandleAcceptConfirmed
                // already fired one SendAvailableQuestUpdateForZone with the quest in
                // ActiveQuests (so it was filtered out); we need to fire another now that
                // we've removed it from ActiveQuests.
                Debug.LogError($"[WELL] {questData.id} cannot auto-complete — removing from active");
                uint instId = activeQuest.InstanceId;
                QuestManager.Instance.RemoveQuestByInstanceId(connId, instId);
                QuestManager.Instance.SendRemovePacket(conn, instId);
                SavePlayerQuests(conn);
                QuestManager.Instance.SendAvailableQuestUpdateForZone(conn);
                return;
            }

            uint instanceId = activeQuest.InstanceId;
            RemoveQuestItemsFromInventory(conn, activeQuest);
            QuestManager.Instance.HandleTurnInConfirmed(conn, instanceId);
            // Repeatable: drop from CompletedQuests so the next click can re-accept.
            // HandleTurnInConfirmed already fired SendAvailableQuestUpdateForZone
            // INSIDE itself, but at that point the quest was still in CompletedQuests
            // (we hadn't removed it yet) so it got filtered out — that's why the
            // Well NPC's ! marker stayed hidden until a zone change. Re-fire the
            // available update AFTER the CompletedQuests cleanup so the client sees
            // the quest is available again immediately.
            if (questData.repeatable)
            {
                playerState.CompletedQuests.RemoveAll(c =>
                    c.Equals(questData.id, StringComparison.OrdinalIgnoreCase));
                QuestManager.Instance.SendAvailableQuestUpdateForZone(conn);
            }
            SavePlayerQuests(conn);
            ApplyQuestRewards(conn, questData);
            Debug.LogError($"[WELL] ✅ {questData.id} auto-completed");
        }

        /// <summary>
        /// Gives the onAcceptItem to the player when they accept a quest.
        /// Places directly into inventory using submsg 0x1E — same as merchant quest item buy.
        /// </summary>
        private void GiveOnAcceptItem(RRConnection conn, string onAcceptItem)
        {
            if (string.IsNullOrEmpty(onAcceptItem)) return;

            string connId = conn.ConnId.ToString();
            var playerState = GetPlayerState(connId);
            if (playerState == null || conn.UnitContainerId == 0) return;

            string gcClass = onAcceptItem;
            var generated = LootManager.Instance.GenerateAuthoredGeneratorLoot(onAcceptItem, 1, playerState.Level, !IsPlayerFree(conn.LoginName), "quest-onaccept");
            var generatedItem = generated.FirstOrDefault(d => d != null && d.IsItem);
            if (generatedItem != null && !string.IsNullOrWhiteSpace(generatedItem.GCType))
                gcClass = generatedItem.GCType;
            else if (!IsDirectAuthoredRewardItem(onAcceptItem))
            {
                RuntimeEvidenceManager.LogFallbackHit("loot-generator", "missing-generator", $"source=quest-onaccept generator={onAcceptItem}", 32);
                Debug.LogError($"[AUTHORED-COVERAGE] area=quest-onaccept status=blocked reason=missing-generator generator={onAcceptItem}");
                return;
            }

            Debug.LogError($"[QUEST-ACCEPT-ITEM] Giving item: {onAcceptItem} → GCClass={gcClass} source={(generatedItem != null ? "authored-generator" : "direct-item")}");

            // Look up item dimensions
            var itemData = DatabaseLoader.FindItem(gcClass);
            int itemWidth = itemData?.inventoryWidth ?? 1;
            int itemHeight = itemData?.inventoryHeight ?? 1;

            // Find empty inventory slot
            byte slotX = 0, slotY = 0;
            bool foundSlot = false;
            for (byte row = 0; row < 8 && !foundSlot; row++)
                for (byte col = 0; col < 10 && !foundSlot; col++)
                    if (!IsInventorySlotOccupied(connId, col, row, itemWidth, itemHeight))
                    { slotX = col; slotY = row; foundSlot = true; }

            if (!foundSlot)
            {
                Debug.LogError($"[QUEST-ACCEPT-ITEM] Inventory full! Cannot give {gcClass}");
                return;
            }

            uint slot = GetNextInventorySlot(connId);

            // Send item directly to inventory via UnitContainer 0x1E
            // Same format as MerchantManager quest item buy
            var writer = new LEWriter();
            writer.WriteByte(0x07);  // BeginStream

            writer.WriteByte(0x35);
            writer.WriteUInt16(conn.UnitContainerId);
            writer.WriteByte(0x1E);          // Add item to inventory
            writer.WriteByte(0x0B);          // inventory container ID
            writer.WriteByte(0xFF);          // marker
            writer.WriteCString(GCObject.GetPacketGCClassFor(gcClass));    // GC type
            writer.WriteUInt32(slot);        // slot index
            writer.WriteByte(slotX);         // grid X
            writer.WriteByte(slotY);         // grid Y
            writer.WriteByte(0x01);          // quantity
            writer.WriteByte(0x01);          // level
            writer.WriteByte(0x00);          // flags
            writer.WriteByte(0x00);          // modifier count
            WritePlayerEntitySynch(conn, writer);

            writer.WriteByte(0x06);  // EndStream

            byte[] packet = writer.ToArray();
            Debug.LogError($"[QUEST-ACCEPT-ITEM] Sending 0x1E packet ({packet.Length} bytes): {BitConverter.ToString(packet)}");
            SendCompressedA(conn, 0x01, 0x0F, packet);

            // Track in server inventory
            var newItem = new GCObject { GCClass = gcClass, NativeClass = "Item" };
            TrackInventoryItem(connId, slot, newItem, slotX, slotY);
            OccupyInventorySlots(connId, slotX, slotY, itemWidth, itemHeight);
            SetStackCount(connId, slot, 1);

            // Save to DB
            SavePlayerInventory(conn);
            NotifyQuestItemAcquired(conn, gcClass);

            Debug.LogError($"[QUEST-ACCEPT-ITEM] ✅ {gcClass} placed in inventory at ({slotX},{slotY}) slot {slot}");
        }

        /// <summary>
        /// Give the player N stackable items of a given gcType. Tops up existing stacks
        /// first via 0x22 UpdateQuantity, then creates new 1×1 stacks for the remainder
        /// via 0x1E AddItem. Used for King's Coin rewards (QuestItemPAL.Token, max stack 100)
        /// and any other stackable item.
        ///
        /// Per RE of DungeonRunners.exe: King's Coins are NOT a separate currency. The
        /// Container only has one currency (gold at +0x74). King's Coins are inventory
        /// items of type QuestItemPAL.Token and the bottom-left inventory counter is
        /// computed client-side via Inventory::getItemCountByType("QuestItemPAL.Token").
        /// To make the counter increment, just add Token items via 0x1E (same path as
        /// GiveOnAcceptItem).
        /// </summary>
        private void GiveStackedItem(RRConnection conn, string gcType, int totalCount, int maxStackSize = 100)
        {
            if (totalCount <= 0 || string.IsNullOrEmpty(gcType)) return;
            if (conn.UnitContainerId == 0)
            {
                Debug.LogError($"[GIVE-STACKED] No UnitContainerId — cannot give {gcType}");
                return;
            }

            string connId = conn.ConnId.ToString();
            int remaining = totalCount;

            Debug.LogError($"[GIVE-STACKED] {gcType} ×{totalCount} (max stack {maxStackSize})");

            // ── Step 1: Top up existing stacks via 0x22 UpdateQuantity ──
            if (_playerInventoryItems.ContainsKey(connId))
            {
                var slots = new List<uint>(_playerInventoryItems[connId].Keys);
                foreach (var slotId in slots)
                {
                    if (remaining <= 0) break;
                    var entry = _playerInventoryItems[connId][slotId];
                    if (entry.item == null) continue;
                    if (!string.Equals(entry.item.GCClass, gcType, StringComparison.OrdinalIgnoreCase))
                        continue;

                    int currentCount = GetStackCount(connId, slotId);
                    if (currentCount >= maxStackSize) continue;

                    int canAdd = System.Math.Min(maxStackSize - currentCount, remaining);
                    int newCount = currentCount + canAdd;
                    remaining -= canAdd;

                    // 0x22 processUpdateQuantity (0x57dc50 in client):
                    //   reads uint32 itemSlotId + byte newQuantity
                    //   stores newQuantity at item+0x82 (the cached stack count byte)
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

                    SetStackCount(connId, slotId, newCount);
                    Debug.LogError($"[GIVE-STACKED] Topped up slot {slotId} {currentCount}→{newCount} (+{canAdd})");
                }
            }

            // ── Step 2: Create new 1×1 stacks for the remainder via 0x1E AddItem ──
            var itemData = DatabaseLoader.FindItem(gcType);
            int itemWidth = itemData?.inventoryWidth ?? 1;
            int itemHeight = itemData?.inventoryHeight ?? 1;

            while (remaining > 0)
            {
                var (sx, sy) = FindNextFreeInventorySlot(connId, itemWidth, itemHeight);
                if (sx < 0)
                {
                    Debug.LogError($"[GIVE-STACKED] ⚠️ Inventory full! {remaining}× {gcType} not given");
                    break;
                }
                byte slotX = (byte)sx, slotY = (byte)sy;

                int stackSize = System.Math.Min(remaining, maxStackSize);
                remaining -= stackSize;
                uint slot = GetNextInventorySlot(connId);

                var writer = new LEWriter();
                writer.WriteByte(0x07);
                writer.WriteByte(0x35);
                writer.WriteUInt16(conn.UnitContainerId);
                writer.WriteByte(0x1E);
                writer.WriteByte(0x0B);
                writer.WriteByte(0xFF);
                writer.WriteCString(GCObject.GetPacketGCClassFor(gcType));
                writer.WriteUInt32(slot);
                writer.WriteByte(slotX);
                writer.WriteByte(slotY);
                writer.WriteByte((byte)(stackSize > 255 ? 255 : stackSize));
                writer.WriteByte(0x01);
                writer.WriteByte(0x00);
                writer.WriteByte(0x00);
                WritePlayerEntitySynch(conn, writer);
                writer.WriteByte(0x06);
                SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());

                var newItem = new GCObject { GCClass = gcType, NativeClass = "Item" };
                TrackInventoryItem(connId, slot, newItem, slotX, slotY);
                OccupyInventorySlots(connId, slotX, slotY, itemWidth, itemHeight);
                SetStackCount(connId, slot, stackSize);

                Debug.LogError($"[GIVE-STACKED] New stack: slot {slot} at ({slotX},{slotY}) ×{stackSize}");
            }

            SavePlayerInventory(conn);
            Debug.LogError($"[GIVE-STACKED] ✅ Done. {totalCount - remaining}/{totalCount} {gcType} placed");
        }

        /// <summary>
        /// Swap minor potion drops to their major equivalents for member players.
        /// Original DR enforced stack-of-10 by giving members DIFFERENT ITEMS (Major
        /// potions have MaxStackSize=10 in their GC data, minors have MaxStackSize=5).
        /// The client enforces those caps - we cannot make a minor potion stack to 10.
        ///
        /// Logs EVERY drop's gcType + final value so we can see exactly what is in
        /// the items DB and what got swapped. If members are still seeing stack-of-5
        /// after this, the log will tell us why (helper not called / helper called but
        /// no potions in drops / potion gcType doesn't match the patterns).
        /// </summary>
        private void UpgradePotionsForMembers(List<LootDrop> drops, RRConnection conn)
        {
            string who = conn?.LoginName ?? "<null>";
            if (drops == null) { Debug.LogError($"[LOOT-MEMBER] {who}: drops==null, skipping"); return; }
            if (conn == null) { Debug.LogError($"[LOOT-MEMBER] <null conn>: drops.Count={drops.Count}, skipping"); return; }

            bool isFree = IsPlayerFree(conn.LoginName);
            bool isAdmin = IsPlayerAdmin(conn.LoginName);
            Debug.LogError($"[LOOT-MEMBER] called for {who} isFree={isFree} isAdmin={isAdmin} drops.Count={drops.Count}");

            foreach (var drop in drops)
            {
                if (drop == null) { Debug.LogError("[LOOT-MEMBER]   drop=null"); continue; }
                if (drop.IsGold) { Debug.LogError($"[LOOT-MEMBER]   gold +{drop.GoldAmount}"); continue; }
                Debug.LogError($"[LOOT-MEMBER]   item gcType='{drop.GCType ?? "<null>"}' label='{drop.Label ?? "<null>"}'");
            }

            if (isFree) { Debug.LogError($"[LOOT-MEMBER] {who} is free, no upgrades"); return; }

            int swapped = 0;
            foreach (var drop in drops)
            {
                if (drop == null || drop.IsGold || string.IsNullOrEmpty(drop.GCType)) continue;
                string gc = drop.GCType;
                string lc = gc.ToLowerInvariant();
                string before = gc;

                // Pattern A: literal "Minor*Potion" anywhere (case-insensitive)
                if (lc.Contains("minorhealthpotion"))
                {
                    drop.GCType = System.Text.RegularExpressions.Regex.Replace(
                        gc, "minorhealthpotion", "MajorHealthPotion",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    drop.Label = "Major Health Potion";
                    Debug.LogError($"[LOOT-MEMBER]   SWAP A-H: '{before}' -> '{drop.GCType}'");
                    swapped++;
                    continue;
                }
                if (lc.Contains("minormanapotion"))
                {
                    drop.GCType = System.Text.RegularExpressions.Regex.Replace(
                        gc, "minormanapotion", "MajorManaPotion",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    drop.Label = "Major Mana Potion";
                    Debug.LogError($"[LOOT-MEMBER]   SWAP A-M: '{before}' -> '{drop.GCType}'");
                    swapped++;
                    continue;
                }

                // Pattern B: PotionPAL alternate naming "HealthPotion_Sm" / "ManaPotion_Sm"
                // (seen in FreePotionIG.gc comments) -> "_Lg" suffix
                if (lc.Contains("healthpotion_sm"))
                {
                    drop.GCType = System.Text.RegularExpressions.Regex.Replace(
                        gc, "healthpotion_sm", "HealthPotion_Lg",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    drop.Label = "Major Health Potion";
                    Debug.LogError($"[LOOT-MEMBER]   SWAP B-H: '{before}' -> '{drop.GCType}'");
                    swapped++;
                    continue;
                }
                if (lc.Contains("manapotion_sm"))
                {
                    drop.GCType = System.Text.RegularExpressions.Regex.Replace(
                        gc, "manapotion_sm", "ManaPotion_Lg",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    drop.Label = "Major Mana Potion";
                    Debug.LogError($"[LOOT-MEMBER]   SWAP B-M: '{before}' -> '{drop.GCType}'");
                    swapped++;
                    continue;
                }

                // Pattern C: any other "potion" item that might be a minor we don't
                // recognize - just log so we can add a swap rule next round.
                if (lc.Contains("potion"))
                {
                    Debug.LogError($"[LOOT-MEMBER]   POTION NOT MATCHED: '{gc}' (need a swap rule for this)");
                }
            }
            Debug.LogError($"[LOOT-MEMBER] {who}: {swapped} swap(s) total");
        }

        /// <summary>
        /// Checks player proximity to goto-objective targets. Throttled 1/sec/player.
        ///
        /// Covers the case where a goto objective targets an entity/waypoint IN the
        /// current zone and the player has to walk up to it (e.g. "Locate Entrance to
        /// Rattle Tooth's Lair" — boss_spawn waypoint in dungeon00_level03, range 200).
        ///
        /// Lookup strategy:
        ///  1. Match the objective Label against _gotoGCByLabel (from Q*.gc) to get
        ///     authoritative TargetZoneName / TargetEntityName / Range.
        ///  2. If the player is in TargetZoneName, check distance to a waypoint named
        ///     TargetEntityName — complete if within Range.
        ///  3. Fallback: legacy portal-proximity path for objectives without GC data.
        /// </summary>
        private void CheckGotoProximity(RRConnection conn)
        {
            // Throttle: at most once per second per connection
            int connKey = conn.ConnId;
            float now = Time.time;
            if (_gotoNextCheckTime.TryGetValue(connKey, out float next) && now < next)
                return;
            _gotoNextCheckTime[connKey] = now + 1.0f;

            string connId = conn.ConnId.ToString();
            var questState = QuestManager.Instance.GetPlayerState(connId);
            if (questState == null) return;

            // Quick bail: any incomplete goto objectives at all?
            bool hasGoto = false;
            foreach (var quest in questState.ActiveQuests)
            {
                foreach (var obj in quest.Objectives)
                {
                    if (!obj.IsComplete && obj.Type != null &&
                        obj.Type.Equals("goto", StringComparison.OrdinalIgnoreCase))
                    { hasGoto = true; break; }
                }
                if (hasGoto) break;
            }
            if (!hasGoto) return;

            string curZone = conn.CurrentZoneName;
            if (string.IsNullOrEmpty(curZone)) return;

            float px = conn.PlayerPosX;
            float py = conn.PlayerPosY;

            foreach (var quest in questState.ActiveQuests)
            {
                foreach (var obj in quest.Objectives)
                {
                    if (obj.IsComplete) continue;
                    if (obj.Type == null || !obj.Type.Equals("goto", StringComparison.OrdinalIgnoreCase)) continue;

                    // ── Path 1: GC-data-driven proximity ──
                    if (!string.IsNullOrEmpty(obj.Label) &&
                        _gotoGCByLabel.TryGetValue(obj.Label, out var gc))
                    {
                        // Must be in the right zone (prefix match so child zones like
                        // dungeon00_level03_boss count as being "in" dungeon00_level03).
                        if (!string.IsNullOrEmpty(gc.TargetZone) &&
                            !curZone.StartsWith(gc.TargetZone, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (!string.IsNullOrEmpty(gc.TargetEntity))
                        {
                            int rng = gc.Range > 0 ? gc.Range : 100;

                            // Step A: look for the waypoint by name in the CURRENT zone.
                            // Fires when the player is physically next to a named
                            // waypoint (e.g. inside dungeon00_level03_boss next to
                            // the boss_spawn waypoint).
                            var wps = DatabaseLoader.GetWaypointsForZone(curZone);
                            if (wps != null)
                            {
                                foreach (var wp in wps)
                                {
                                    if (!wp.name.Equals(gc.TargetEntity, StringComparison.OrdinalIgnoreCase))
                                        continue;
                                    float dx = px - wp.posX;
                                    float dy = py - wp.posY;
                                    float dist = (float)System.Math.Sqrt(dx * dx + dy * dy);
                                    if (dist <= rng)
                                    {
                                        Debug.LogError($"[GOTO-PROXIMITY] ✅ Waypoint match: player ({px:F0},{py:F0}) within {dist:F0} of '{wp.name}' in {curZone} (range={rng}) — completing '{obj.Label}'");
                                        CompleteGotoObjective(conn, quest, obj);
                                        return;
                                    }
                                }
                            }

                            // Step B: PORTAL fallback. Fires when the player is near a
                            // portal in the current zone whose TARGET zone contains a
                            // waypoint with the name we're looking for. This handles
                            // the "Find Entrance to Rattle Tooth's Lair" case:
                            // boss_spawn waypoint lives inside dungeon00_level03_boss,
                            // but the quest should tick when the player walks up to
                            // the entrance PORTAL in dungeon00_level03 — no need to
                            // actually step through.
                            var portalsStepB = DatabaseLoader.GetPortalsForZone(curZone);
                            if (portalsStepB != null)
                            {
                                foreach (var portal in portalsStepB)
                                {
                                    if (string.IsNullOrEmpty(portal.targetZone)) continue;
                                    var targetWps = DatabaseLoader.GetWaypointsForZone(portal.targetZone);
                                    if (targetWps == null) continue;

                                    bool targetHasEntity = false;
                                    foreach (var wp in targetWps)
                                        if (wp.name.Equals(gc.TargetEntity, StringComparison.OrdinalIgnoreCase))
                                        { targetHasEntity = true; break; }
                                    if (!targetHasEntity) continue;

                                    float dx = px - portal.posX;
                                    float dy = py - portal.posY;
                                    float dist = (float)System.Math.Sqrt(dx * dx + dy * dy);
                                    if (dist <= rng)
                                    {
                                        Debug.LogError($"[GOTO-PROXIMITY] ✅ Portal match: player ({px:F0},{py:F0}) within {dist:F0} of portal '{portal.name}'→{portal.targetZone} (range={rng}) — completing '{obj.Label}'");
                                        CompleteGotoObjective(conn, quest, obj);
                                        return;
                                    }
                                }
                            }
                        }
                        // No entity → zone-entry hook handles it
                        continue;
                    }

                    // ── Path 2: Legacy portal-proximity (no GC match or DB target only) ──
                    if (string.IsNullOrEmpty(obj.Target)) continue;
                    var portals = DatabaseLoader.GetPortalsForZone(curZone);
                    if (portals == null || portals.Count == 0) continue;

                    foreach (var portal in portals)
                    {
                        if (string.IsNullOrEmpty(portal.targetZone)) continue;

                        var targetWaypoints = DatabaseLoader.GetWaypointsForZone(portal.targetZone);
                        if (targetWaypoints == null) continue;

                        bool waypointMatch = false;
                        foreach (var wp in targetWaypoints)
                            if (wp.name.Equals(obj.Target, StringComparison.OrdinalIgnoreCase))
                            { waypointMatch = true; break; }
                        if (!waypointMatch) continue;

                        float dx = px - portal.posX;
                        float dy = py - portal.posY;
                        float dist = (float)System.Math.Sqrt(dx * dx + dy * dy);
                        float triggerRange = System.Math.Max(portal.width, portal.height);
                        if (triggerRange < 100) triggerRange = 100;

                        if (dist <= triggerRange)
                        {
                            Debug.LogError($"[GOTO-PROXIMITY] ✅ Portal path: player at ({px:F0},{py:F0}) within {dist:F0} of portal '{portal.name}' (range={triggerRange}) — completing '{obj.Label}'");
                            CompleteGotoObjective(conn, quest, obj);
                            return;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Called when a player enters a new zone (from ChangeZone). Completes any
        /// goto objectives whose TargetZoneName matches the new zone AND have no
        /// TargetEntityName (zone-entry alone is enough). Quests with an entity get
        /// handled by CheckGotoProximity on the next tick.
        /// </summary>
        private void CompleteGotoObjectivesOnZoneEntry(RRConnection conn)
        {
            string connId = conn.ConnId.ToString();
            var questState = QuestManager.Instance.GetPlayerState(connId);
            if (questState == null) return;

            string curZone = conn.CurrentZoneName;
            if (string.IsNullOrEmpty(curZone)) return;

            bool anyCompleted = false;
            foreach (var quest in questState.ActiveQuests)
            {
                foreach (var obj in quest.Objectives)
                {
                    if (obj.IsComplete) continue;
                    if (obj.Type == null || !obj.Type.Equals("goto", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrEmpty(obj.Label)) continue;

                    if (!_gotoGCByLabel.TryGetValue(obj.Label, out var gc)) continue;
                    if (string.IsNullOrEmpty(gc.TargetZone)) continue;
                    if (!curZone.StartsWith(gc.TargetZone, StringComparison.OrdinalIgnoreCase)) continue; // prefix: _boss/_off child zones

                    // Zone matches. Complete immediately if no entity required.
                    if (string.IsNullOrEmpty(gc.TargetEntity))
                    {
                        Debug.LogError($"[GOTO-ZONE-ENTRY] ✅ Player entered '{curZone}' — completing goto '{obj.Label}' for quest {quest.QuestId}");
                        CompleteGotoObjective(conn, quest, obj);
                        anyCompleted = true;
                    }
                    // Entity required: CheckGotoProximity handles it on next tick.
                }
            }

            if (anyCompleted)
                SavePlayerQuests(conn);
        }

        /// <summary>
        /// Marks a goto objective complete and pushes progress to the client.
        /// Shared by CheckGotoProximity and CompleteGotoObjectivesOnZoneEntry.
        /// </summary>
        private void CompleteGotoObjective(RRConnection conn, ActiveQuest quest, QuestProgress obj)
        {
            obj.Current = obj.Required;
            QuestManager.Instance.SendProgressPacket(conn, quest.InstanceId, quest);
            SavePlayerQuests(conn);
        }


        /// <summary>
        /// Apply quest completion rewards using the original Dungeon Runners formulas
        /// from GlobalKnobs.gc:
        ///   gold = max(1, level × QuestGoldPerLevel × cashReward × (member ? MemberGoldMod : 1))
        ///   xp   = max(1, level × QuestExperiencePerLevel × (expMod/5) × freeMult × diffMult)
        ///   tokens = TokenReward (King's Coin count, spent at relic vendors)
        ///   xp buff = QuestXPBonus modifier (15% EXPMOD, removed on death) if GrantXPBuff
        /// </summary>
        private void ApplyQuestRewards(RRConnection conn, QuestData questData)
        {
            if (questData == null) return;

            // ----- Look up constants from ServerSettings (mirrors GlobalKnobs.gc) -----
            float questGoldPerLevel = GCDatabase.Instance.GetKnob("QuestGoldPerLevel", 250f);
            float questXPPerLevel = GCDatabase.Instance.GetKnob("QuestExperiencePerLevel", 100f);
            float memberGoldMod = GCDatabase.Instance.GetKnob("MemberGoldMod", 1.15f);
            float freePlayerXPMult = GCDatabase.Instance.GetKnob("FreePlayerExperienceMult", 0.87f);
            float gcExperienceMod = GCDatabase.Instance.GetKnob("ExperienceMod", 5.0f);

            bool isFree = IsPlayerFree(conn.LoginName);
            int difficulty = GetDifficultyForConn(conn);
            float diffXPMult = GetDifficultyXPMult(difficulty);

            int qLevel = questData.level > 0 ? questData.level : 1;
            float cashMult = questData.cashReward > 0 ? questData.cashReward : 0.5f;

            // ----- Compute gold (matches in-client quest dialog: level × QuestGoldPerLevel × CashReward) -----
            // NO member bonus on quest rewards. The 1.15× MemberGoldMod applies to monster gold drops only.
            // Granting more than the dialog shows causes the "shows 125, gets 144" mismatch.
            uint goldReward = (uint)System.Math.Max(1, System.Math.Round(
                qLevel * questGoldPerLevel * cashMult));

            // ----- XP from quest turn-in: NONE. -----
            // Per RE of all Q*.gc files: zero quests have a RewardExperience field.
            // Quests grant XP ONLY indirectly via the QuestXPBonus AttributeModifier
            // ("15% More Cowbell" buff) when GrantXPBuff=true. The buff boosts XP from
            // KILLS until death. See block below for the buff dispatch.
            uint xpReward = 0;

            int tokenReward = questData.tokenReward;

            Debug.LogError($"[QUEST-REWARDS] {questData.id} (L{qLevel}) → " +
                           $"{goldReward} gold, {xpReward} XP, {tokenReward} King's Coin(s)" +
                           $" [free={isFree}, cash×{cashMult}, diff×{diffXPMult}, xpBuff={questData.grantXPBuff}]");

            // ----- Apply gold -----
            if (goldReward > 0 && _selectedCharacter.TryGetValue(conn.LoginName, out var rewardGcObj))
            {
                var rewardChar = DungeonRunners.Database.CharacterRepository.GetCharacter(rewardGcObj.Id);
                if (rewardChar != null)
                {
                    rewardChar.gold += goldReward;
                    DungeonRunners.Database.CharacterRepository.SaveCharacter(rewardChar);
                }
                if (conn.UnitContainerId != 0)
                {
                    var goldPkt = new LEWriter();
                    goldPkt.WriteByte(0x07);
                    goldPkt.WriteByte(0x35);
                    goldPkt.WriteUInt16(conn.UnitContainerId);
                    goldPkt.WriteByte(0x20);           // AddCurrency
                    goldPkt.WriteUInt32(goldReward);
                    goldPkt.WriteByte(0x00);           // source
                    goldPkt.WriteUInt32(0x00000000);   // entityHandle
                    goldPkt.WriteByte(0x01);           // notifyFlag
                    WritePlayerEntitySynch(conn, goldPkt);
                    goldPkt.WriteByte(0x06);
                    SendToClient(conn, goldPkt.ToArray());
                    Debug.LogError($"[QUEST-REWARDS] 💰 Sent +{goldReward} gold to client");
                }
            }

            // ----- Apply King's Coin reward as inventory items (QuestItemPAL.Token) -----
            // Per RE: King's Coins are NOT a separate currency. They are inventory items
            // of type QuestItemPAL.Token (1×1, max stack 100). The bottom-left inventory
            // counter is computed client-side via Inventory::getItemCountByType("QuestItemPAL.Token").
            if (tokenReward > 0)
            {
                GiveStackedItem(conn, "QuestItemPAL.Token", tokenReward, 100);
            }

            // ----- XP from quest turn-in is NONE. The buff below is the only quest XP. -----

            // ----- Apply XP buff if quest has GrantXPBuff -----
            if (questData.grantXPBuff)
            {
                SendQuestXPBonusModifier(conn);
            }

            // ----- Reward items via RewardItemGenerator -----
            if (!string.IsNullOrEmpty(questData.rewardItemGenerator) && questData.numRewardItems > 0)
            {
                Debug.LogError($"[QUEST-REWARDS] 📦 Rolling {questData.numRewardItems}× from {questData.rewardItemGenerator}");
                string gen = questData.rewardItemGenerator;
                var authoredRewardDrops = LootManager.Instance.GenerateAuthoredGeneratorLoot(
                    gen,
                    questData.numRewardItems,
                    qLevel,
                    !isFree,
                    "quest-reward");
                if (authoredRewardDrops.Count > 0)
                {
                    foreach (var drop in authoredRewardDrops)
                    {
                        if (drop == null) continue;
                        if (drop.IsGold && drop.GoldAmount > 0)
                        {
                            GiveGold(conn, (uint)drop.GoldAmount, "quest-reward-generator");
                            continue;
                        }
                        if (drop.IsKingsCoin && drop.KingsCoinCount > 0)
                        {
                            GiveStackedItem(conn, "QuestItemPAL.Token", drop.KingsCoinCount, 100);
                            continue;
                        }
                        if (drop.IsItem && !string.IsNullOrWhiteSpace(drop.GCType))
                        {
                            GiveStackedItem(conn, drop.GCType, 1, 1);
                        }
                    }
                    Debug.LogError($"[QUEST-REWARDS] Authored generator {gen} produced {authoredRewardDrops.Count} reward(s)");
                    return;
                }
                if (TryGiveDirectAuthoredQuestReward(conn, questData, gen))
                    return;

                if (LootManager.Instance.CanResolveAuthoredGenerator(gen))
                {
                    Debug.LogError($"[AUTHORED-COVERAGE] area=quest-reward status=blocked reason=authored-generator-empty quest={questData.id} generator={gen} count={questData.numRewardItems}");
                    return;
                }

                RuntimeEvidenceManager.LogFallbackHit(
                    "loot-generator",
                    "missing-generator",
                    $"source=quest-reward quest={questData.id} generator={gen} count={questData.numRewardItems}",
                    32);
                Debug.LogError($"[AUTHORED-COVERAGE] area=quest-reward status=blocked reason=missing-generator quest={questData.id} generator={gen} count={questData.numRewardItems}");
            }
        }

        private bool TryGiveDirectAuthoredQuestReward(RRConnection conn, QuestData questData, string gcType)
        {
            if (conn == null || string.IsNullOrWhiteSpace(gcType))
                return false;

            if (!IsDirectAuthoredRewardItem(gcType))
                return false;

            int count = Math.Max(1, questData?.numRewardItems ?? 1);
            GiveStackedItem(conn, gcType, count);
            Debug.LogError($"[QUEST-REWARDS] Direct authored item reward: quest={questData?.id ?? "<null>"} gcType={gcType} count={count}");
            return true;
        }

        private static bool IsDirectAuthoredRewardItem(string gcType)
        {
            if (string.IsNullOrWhiteSpace(gcType))
                return false;
            if (DatabaseLoader.FindItem(gcType) != null)
                return true;
            if (GCDatabase.Instance == null || !GCDatabase.Instance.IsLoaded)
                return false;

            var node = GCDatabase.Instance.ResolveWithInheritance(gcType);
            if (node == null)
                return false;
            string extends = node.Extends ?? string.Empty;
            if (extends.IndexOf("Generator", StringComparison.OrdinalIgnoreCase) >= 0 ||
                gcType.IndexOf("Generator", StringComparison.OrdinalIgnoreCase) >= 0 ||
                gcType.EndsWith("IG", StringComparison.OrdinalIgnoreCase))
                return false;

            return AuthoredExtendsNativeClass(gcType, "Item") ||
                   AuthoredExtendsNativeClass(gcType, "ActiveItem") ||
                   AuthoredExtendsNativeClass(gcType, "MeleeWeapon") ||
                   AuthoredExtendsNativeClass(gcType, "RangedWeapon") ||
                   AuthoredExtendsNativeClass(gcType, "Armor");
        }

        /// <summary>
        /// Send the QuestXPBonus modifier (15% EXPMOD buff, removed on death).
        /// Same packet pattern as SendFreePlayerModifier — both are AttributeModifiers.
        /// GC type: quests.base.QuestXPBonus, Label: "Quest Reward: 15% More Cowbell".
        /// </summary>
        public void SendQuestXPBonusModifier(RRConnection conn)
        {
            if (conn.ModifiersId == 0)
            {
                Debug.LogError("[QUEST-XP-BUFF] Cannot send QuestXPBonus — ModifiersId not set");
                return;
            }

            var writer = new LEWriter();
            writer.WriteByte(0x07);              // BeginStream
            writer.WriteByte(0x35);              // ComponentUpdate
            writer.WriteUInt16((ushort)conn.ModifiersId);
            writer.WriteByte(0x00);              // processAddModifier type
            WriteGCType(writer, "quests.base.QuestXPBonus", preserveCase: true);
            // Modifier::readData — 14 bytes (TTD-proven @ 0x4FF390):
            writer.WriteUInt32(2);               // [+0x78] ID (must differ from FreePlayerExperienceModifier=1)
            writer.WriteByte(0);                 // [+0x86] Level
            writer.WriteUInt32(0);               // [+0x80] PowerLevel
            writer.WriteUInt32(0x00000000);      // [+0x7C] Duration (0 = until removed by death)
            writer.WriteByte(0x01);              // [+0x87] SourceIsSelf
            WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);              // EndStream

            SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
            Debug.LogError($"[QUEST-XP-BUFF] ⭐ Sent QuestXPBonus modifier (+15% EXPMOD)");

            // Track the buff so it persists across zone transitions.
            // ResendAllModifiers() is called after every zone change and will re-apply it.
            TrackModifierSent(conn.LoginName, "quests.base.QuestXPBonus", 2,
                level: 0, powerLevel: 0, duration: 0, sourceIsSelf: 1);
            Debug.LogError($"[QUEST-XP-BUFF] 📌 Tracked for zone-change persistence");
        }

        /// <summary>
        /// Send the ZoneSpawnInvulnerabilityModifier — brief immunity to damage
        /// applied each time the player enters a new zone, removed on native
        /// RemoveOnAction events. Mirrors the binary's ZoneSpawnInvulnerability
        /// behavior — "prepare yourself for battle" buff.
        /// GC type: ZoneSpawnInvulnerabilityModifier.
        /// </summary>
        public void SendZoneSpawnInvulnerability(RRConnection conn)
        {
            if (!ShouldSendZoneSpawnInvulnerability(conn))
            {
                ClearZoneSpawnInvulnerability(conn, "ZONE-SKIP");
                Debug.LogError($"[ZONE-INVULN] Skipped ZoneSpawnInvulnerability for {conn?.LoginName ?? "<null>"} (zone={conn?.CurrentZoneName ?? ""})");
                return;
            }

            if (conn.ModifiersId == 0)
            {
                Debug.LogError("[ZONE-INVULN] Cannot send ZoneSpawnInvulnerability — ModifiersId not set");
                return;
            }

            SetZoneSpawnInvulnerability(conn);
            var writer = new LEWriter();
            writer.WriteByte(0x07);              // BeginStream
            writer.WriteByte(0x35);              // ComponentUpdate
            writer.WriteUInt16((ushort)conn.ModifiersId);
            writer.WriteByte(0x00);              // processAddModifier type
            WriteGCType(writer, "avatar.base.ZoneSpawnInvulnerabilityModifier", preserveCase: true);
            // Modifier::readData — 14 bytes (TTD-proven @ 0x4FF390):
            writer.WriteUInt32(3);               // [+0x78] ID (1=FreePlayer, 2=QuestXPBonus, 3=ZoneSpawnInvuln)
            writer.WriteByte(0);                 // [+0x86] Level
            writer.WriteUInt32(0);               // [+0x80] PowerLevel
            writer.WriteUInt32(ZoneSpawnInvulnerabilityDuration);            // [+0x7C] Duration (from .gc file)
            writer.WriteByte(0x01);              // [+0x87] SourceIsSelf
            WritePlayerEntitySynch(conn, writer);
            writer.WriteByte(0x06);              // EndStream

            SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
            Debug.LogError($"[ZONE-INVULN] Sent ZoneSpawnInvulnerability for {conn.LoginName} (zone={conn.CurrentZoneName})");
            // NOT tracked for persistence — must be re-applied on every zone change.
        }

        private const uint ZoneSpawnInvulnerabilityDuration = 1800;

        private struct PendingSpell
        {
            public long Sequence;
            public RRConnection Conn;
            public PlayerState State;
            public Combat.Monster Monster;
            public Combat.SpellData Spell;
            public byte ManipId;
            public byte UseFlags;
            public ushort ComponentId;
            public float StartX;
            public float StartY;
            public float AimX;
            public float AimY;
            public string InstanceKey;
            public float DueTime;
            public float ProjectileHitDistance;
            public float ProjectileDelay;
            public bool QueuedWithoutInitialTarget;
            public bool ProjectileRuntimeInitialized;
            public float FireTime;
            public int FireNativeTick;
            public int LastUpdateNativeTick;
            public int UpdatesCompleted;
            public int MaxLifetimeTicks;
            public float ProjectileSpeed;
            public float ProjectileSize;
            public float StepDistance;
            public float InitialDistance;
            public float CurrentDistance;
            public float MaxDistance;
        }

        private struct SpellPreSuffixFlushResult
        {
            public uint TargetEntityId;
            public int PendingBefore;
            public int PendingAfter;
            public int DueForTarget;
            public int DueOther;
            public int Applied;
            public int SkippedDead;
            public int RequeuedFuture;
            public int RequeuedOther;
            public uint BeforeHPWire;
            public uint AfterHPWire;
        }

        private struct SpellWeaponDamageEffectResult
        {
            public bool Attempted;
            public bool Landed;
            public bool Applied;
            public bool Died;
            public uint OldHPWire;
            public uint NewHPWire;
            public uint DamageWire;
            public uint HitRaw;
            public uint BlockRaw;
            public uint DamageRaw;
            public int HitRoll;
            public int BlockRoll;
            public int HitThreshold;
            public int AttackRating;
            public int DefenseRating;
            public int DamageMod;
            public int SkillDamageModRaw;
            public int ARMod;
            public int MinDamageWire;
            public int MaxDamageWire;
            public bool IsCritical;
            public string ResultName;
        }

        private RRConnection FindConnectionByAvatarEntityId(uint entityId)
        {
            if (entityId == 0) return null;
            foreach (var conn in _connections.Values)
            {
                if (conn?.Avatar == null) continue;
                if ((uint)conn.Avatar.Id == entityId)
                    return conn;
            }
            return null;
        }

        private void DrainPendingModifierKills()
        {
            while (CombatManager.Instance.HasPendingModifierKills)
            {
                var kill = CombatManager.Instance.DequeuePendingModifierKill();
                if (kill == null)
                    break;

                var monster = CombatManager.Instance.GetMonster(kill.TargetEntityId);
                var conn = FindConnectionByAvatarEntityId(kill.SourceEntityId);
                Debug.LogError($"[POISON-SHOT-MOD-KILL] drain target={kill.TargetEntityId} sourceEntity={kill.SourceEntityId} nativeDamageTime={kill.NativeDamageTime:F3} conn={(conn != null ? conn.ConnId.ToString() : "null")} source={kill.Source ?? "modifier-tick"}");
                if (monster == null)
                {
                    Debug.LogError($"[POISON-SHOT-MOD-KILL] missing monster target={kill.TargetEntityId}");
                    continue;
                }

                CombatManager.Instance.CancelMonsterPendingAttack(monster, "SPELL-MOD-tick-kill");
                try
                {
                    bool finalized = TryFinalizeMonsterKill(conn, monster, "SPELL-MOD-tick");
                    Debug.LogError($"[POISON-SHOT-MOD-KILL] finalize result={finalized} monster={monster.Name} eid={monster.EntityId}");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[KILL-ERROR] SPELL-MOD-tick finalize failed for {monster.Name}: {ex.Message}\n{ex.StackTrace}");
                    if (IsUseTargetingMonster(conn, monster))
                        ClearUseTargetAndReleaseControl(conn, "SPELL-MOD-tick-error");
                }

                if (conn != null && IsUseTargetingMonster(conn, monster))
                    ClearUseTargetAndReleaseControl(conn, "SPELL-MOD-tick-kill", sendClientControlReset: true, requireActiveUseTargetForReset: true);
            }
        }

        private void DrainWeaponCycleKills(string source)
        {
            while (Combat.WeaponCycleTracker.Instance.HasPendingKills)
            {
                var kill = Combat.WeaponCycleTracker.Instance.DequeueKill();
                if (kill == null || !kill.Killed)
                    continue;

                Debug.LogError($"[WEAPON-CYCLE] ★ KILL: {kill.Monster?.Name ?? "monster"} killed by {kill.ConnKey} ({kill.DamageDealt} final dmg) source={source ?? "unknown"}");
                if (kill.Monster != null)
                    CombatManager.Instance.CancelMonsterPendingAttack(kill.Monster, "WeaponCycle-kill");
                try
                {
                    bool finalized = TryFinalizeMonsterKill(kill.Connection, kill.Monster, source ?? "WeaponCycle-tick");
                    Debug.LogError($"[WEAPON-CYCLE] finalize result={finalized} monster={kill.Monster?.Name} eid={kill.Monster?.EntityId} source={source ?? "unknown"}");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[KILL-ERROR] WeaponCycle finalize failed for {kill.Monster?.Name}: {ex.Message}\n{ex.StackTrace}");
                    if (IsUseTargetingMonster(kill.Connection, kill.Monster))
                        ClearUseTargetAndReleaseControl(kill.Connection, "WeaponCycle-error");
                }
                if (kill.Connection != null)
                    ClearUseTargetAndReleaseControl(kill.Connection, "WeaponCycle-kill", sendClientControlReset: true, requireActiveUseTargetForReset: true);
            }
        }

        private void TickCombatDeterministicSystems(float tickNow, bool allowNewMonsterAttacks)
        {
            ProcessPendingSpellsForPlayerEntity(0, tickNow);
            Combat.WeaponCycleTracker.Instance.TickProjectileEntityPhase(null, tickNow, "ClientEntityManager.updateEntities-subentities");
            CombatManager.Instance.MarkNativeSubEntityUpdateCompleted(CombatManager.Instance.NativeCombatTick, tickNow, "GameServer.Update-subentities");

            foreach (uint entityId in CombatManager.Instance.GetNativeEntityOrderSnapshot())
            {
                if (CombatManager.Instance.IsNativeMonsterEntity(entityId))
                {
                    var monster = CombatManager.Instance.GetMonster(entityId);
                    var monsterRng = CombatManager.Instance.GetRoomRngForMonster(monster);
                    if (monsterRng != null)
                        Combat.WanderSimulator.Instance.TickEntity(entityId, monsterRng);
                    CombatManager.Instance.UpdateNativeMonsterEntity(entityId, COMBAT_TICK, allowNewMonsterAttacks, tickNow);
                }
                else if (CombatManager.Instance.IsNativePlayerEntity(entityId))
                {
                    var playerRng = CombatManager.Instance.GetRoomRngForPlayerEntity(entityId);
                    Combat.WeaponCycleTracker.Instance.TickPlayerEntity(entityId, playerRng, tickNow);
                }
            }

            CombatManager.Instance.UpdateNativeMaintenance(COMBAT_TICK);
            TickLazyEncounterSpawns();
            DrainPendingModifierKills();
            DrainWeaponCycleKills("WeaponCycle-tick");
            while (Combat.WeaponCycleTracker.Instance.HasPendingControlReleases)
            {
                var releaseConn = Combat.WeaponCycleTracker.Instance.DequeueControlRelease();
                if (releaseConn != null)
                    ClearUseTargetAndReleaseControl(releaseConn, "WeaponCycle-resolve-release", sendClientControlReset: true, requireActiveUseTargetForReset: true);
            }
        }

        private void ProcessPendingSpellsForPlayerEntity(uint playerEntityId, float now)
        {
            int pendingCount = _pendingSpells.Count;
            for (int i = 0; i < pendingCount; i++)
            {
                if (!_pendingSpells.TryDequeue(out var pending))
                    break;

                uint pendingPlayerId = pending.Conn?.Avatar != null ? (uint)pending.Conn.Avatar.Id : 0;
                if (playerEntityId != 0 && pendingPlayerId != playerEntityId)
                {
                    _pendingSpells.Enqueue(pending);
                    continue;
                }

                if (pending.ProjectileRuntimeInitialized)
                {
                    if (!UpdatePendingSpellProjectile(ref pending, now, "ProcessPendingSpells"))
                        _pendingSpells.Enqueue(pending);
                    continue;
                }

                if (!IsPendingSpellDue(pending, now))
                {
                    _pendingSpells.Enqueue(pending);
                    continue;
                }

                Combat.Monster target = ResolvePendingSpellTarget(ref pending, now, "ProcessPendingSpells");
                if (target != null && target.IsAlive)
                    HandleSpellAttack(pending.Conn, pending.State, target, pending.ManipId, pending.UseFlags, pending.AimX, pending.AimY, ResolvePendingSpellImpactTime(pending, now));
                else if (pending.QueuedWithoutInitialTarget)
                    Debug.LogError($"[SPELL-PROJECTILE] no-hit spell={pending.Spell?.DisplayName ?? pending.Spell?.SkillId ?? "UNKNOWN"} manip={pending.ManipId} aim=({pending.AimX:F1},{pending.AimY:F1}) due={pending.DueTime:F3} now={now:F3} source=ProcessPendingSpells");
            }
        }

        private PendingSpell CreatePendingSpellProjectile(
            RRConnection conn,
            PlayerState state,
            Combat.Monster monster,
            Combat.SpellData spell,
            byte manipId,
            byte useFlags,
            ushort componentId,
            float startX,
            float startY,
            float aimX,
            float aimY,
            bool queuedWithoutInitialTarget,
            float hitDistanceHint)
        {
            float dx = aimX - startX;
            float dy = aimY - startY;
            float aimDistance = Mathf.Sqrt((dx * dx) + (dy * dy));
            if (aimDistance <= 0.001f)
                aimDistance = Mathf.Max(0.001f, hitDistanceHint);

            float speed = spell != null ? Mathf.Max(1f, spell.ProjectileSpeed) : 0f;
            float size = spell != null ? Mathf.Max(0f, spell.ProjectileSize) : 0f;
            float maxDistance = spell != null && size > 0f && speed > 0f
                ? ResolveProjectileMaxDistance(spell, aimDistance)
                : Mathf.Max(0f, hitDistanceHint);
            if (maxDistance <= 0.001f)
                maxDistance = Mathf.Max(0.001f, aimDistance);

            float fireTime = GetNativeCombatNow();
            int fireTick = Combat.WeaponCycleTracker.NativeTickIndexFromTime(fireTime);
            int maxLifetimeTicks = spell != null && size > 0f && speed > 0f
                ? Math.Max(1, Combat.WeaponCycleTracker.NativeProjectileFlightTicks(maxDistance, speed))
                : 1;
            float stepDistance = spell != null && size > 0f && speed > 0f
                ? Combat.WeaponCycleTracker.NativeProjectileStepDistance(speed)
                : 0f;
            float initialDistance = spell != null && size > 0f && speed > 0f
                ? Combat.WeaponCycleTracker.NativeProjectileInitialDistance(speed, maxDistance)
                : 0f;
            float diagnosticDistance = hitDistanceHint > 0f
                ? Mathf.Min(hitDistanceHint, maxDistance)
                : maxDistance;
            float projectileDelay = ResolveProjectileImpactDelay(spell, diagnosticDistance);

            return new PendingSpell
            {
                Sequence = ++_nextPendingSpellProjectileSequence,
                Conn = conn,
                State = state,
                Monster = monster,
                Spell = spell,
                ManipId = manipId,
                UseFlags = useFlags,
                ComponentId = componentId,
                StartX = startX,
                StartY = startY,
                AimX = aimX,
                AimY = aimY,
                InstanceKey = conn != null ? GetInstanceZoneKey(conn) : null,
                DueTime = fireTime + projectileDelay,
                ProjectileHitDistance = diagnosticDistance,
                ProjectileDelay = projectileDelay,
                QueuedWithoutInitialTarget = queuedWithoutInitialTarget,
                ProjectileRuntimeInitialized = spell != null && size > 0f && speed > 0f,
                FireTime = fireTime,
                FireNativeTick = fireTick,
                LastUpdateNativeTick = fireTick,
                UpdatesCompleted = initialDistance > 0f ? 1 : 0,
                MaxLifetimeTicks = maxLifetimeTicks,
                ProjectileSpeed = speed,
                ProjectileSize = size,
                StepDistance = stepDistance,
                InitialDistance = initialDistance,
                CurrentDistance = initialDistance,
                MaxDistance = maxDistance
            };
        }

        private bool UpdatePendingSpellProjectile(ref PendingSpell pending, float now, string source)
        {
            Combat.SpellData spell = pending.Spell;
            if (spell == null)
            {
                Combat.SpellDatabase.Initialize();
                spell = ResolveSpellFromManip(pending.Conn, pending.ManipId);
                pending.Spell = spell;
            }

            if (spell == null || pending.ProjectileSpeed <= 0f || pending.ProjectileSize <= 0f)
                return IsPendingSpellDue(pending, now);

            int nowTick = Combat.WeaponCycleTracker.NativeTickIndexFromTime(now);
            if (nowTick <= pending.LastUpdateNativeTick)
                return false;

            for (int updateTick = pending.LastUpdateNativeTick + 1; updateTick <= nowTick; updateTick++)
            {
                float updateTime = updateTick * Combat.WeaponCycleTracker.NativeUpdateTickSeconds;
                if (pending.UpdatesCompleted >= pending.MaxLifetimeTicks)
                {
                    Debug.LogError($"[SPELL-PROJECTILE] expired no-hit spell={spell.DisplayName ?? spell.SkillId ?? "spell"} seq={pending.Sequence} manip={pending.ManipId} aim=({pending.AimX:F1},{pending.AimY:F1}) current={pending.CurrentDistance:F2}/{pending.MaxDistance:F2} updates={pending.UpdatesCompleted}/{pending.MaxLifetimeTicks} tick={updateTick} source={source ?? "unknown"} native=Projectile::update lifetime-zero-before-unit-check");
                    return true;
                }

                float beforeDistance = pending.CurrentDistance;
                float afterDistance = Mathf.Min(pending.MaxDistance, beforeDistance + Mathf.Max(0.001f, pending.StepDistance));
                pending.UpdatesCompleted++;
                pending.LastUpdateNativeTick = updateTick;

                if (TryResolvePendingSpellTargetAlongSegment(ref pending, beforeDistance, afterDistance, out Combat.Monster target, out float hitDistance, out bool worldBlocked))
                {
                    pending.Monster = target;
                    pending.ProjectileHitDistance = hitDistance;
                    pending.ProjectileDelay = Mathf.Max(0f, updateTime - pending.FireTime);
                    pending.DueTime = updateTime;
                    pending.CurrentDistance = hitDistance;
                    float hitRadius = WeaponCycleTracker.NativeProjectileCollisionRadius(target.CollisionRadius, pending.ProjectileSize);
                    Debug.LogError($"[SPELL-PROJECTILE] subentity impact spell={spell.DisplayName ?? spell.SkillId ?? "spell"} seq={pending.Sequence} target={target.Name}#{target.EntityId} segment={beforeDistance:F2}->{afterDistance:F2} hitDist={hitDistance:F2} tick={updateTick} updates={pending.UpdatesCompleted}/{pending.MaxLifetimeTicks} radius={hitRadius:F2} projectileRadius={WeaponCycleTracker.NativeProjectileRadiusFromAuthoredSize(pending.ProjectileSize):F2} worldBlocked={worldBlocked} source={source ?? "unknown"}");
                    if (target.IsAlive && CombatManager.Instance.PeekMonsterCurrentHPWire(target) != 0)
                        HandleSpellAttack(pending.Conn, pending.State, target, pending.ManipId, pending.UseFlags, pending.AimX, pending.AimY, updateTime);
                    return true;
                }

                pending.CurrentDistance = afterDistance;
                if (pending.CurrentDistance + 0.0001f >= pending.MaxDistance || pending.UpdatesCompleted >= pending.MaxLifetimeTicks)
                {
                    Debug.LogError($"[SPELL-PROJECTILE] expired no-hit spell={spell.DisplayName ?? spell.SkillId ?? "spell"} seq={pending.Sequence} manip={pending.ManipId} aim=({pending.AimX:F1},{pending.AimY:F1}) current={pending.CurrentDistance:F2}/{pending.MaxDistance:F2} updates={pending.UpdatesCompleted}/{pending.MaxLifetimeTicks} tick={updateTick} source={source ?? "unknown"} native=Projectile::update range-end");
                    return true;
                }
            }

            return false;
        }

        private bool TryResolvePendingSpellTargetAlongSegment(ref PendingSpell pending, float segmentStart, float segmentEnd, out Combat.Monster hitMonster, out float hitDistance, out bool worldBlocked)
        {
            hitMonster = null;
            hitDistance = segmentEnd;
            worldBlocked = false;
            Combat.SpellData spell = pending.Spell;
            if (spell == null || pending.Conn == null)
                return false;

            float dx = pending.AimX - pending.StartX;
            float dy = pending.AimY - pending.StartY;
            float lenSq = (dx * dx) + (dy * dy);
            if (lenSq <= 0.0001f)
                return false;

            float pathDistance = Mathf.Sqrt(lenSq);
            float dirX = dx / pathDistance;
            float dirY = dy / pathDistance;
            string zoneName = !string.IsNullOrWhiteSpace(pending.Monster?.ZoneName)
                ? pending.Monster.ZoneName
                : pending.Conn.CurrentZoneName;
            string instanceKey = !string.IsNullOrWhiteSpace(pending.InstanceKey)
                ? pending.InstanceKey
                : (pending.Conn != null ? GetInstanceZoneKey(pending.Conn) : null);
            float projectileSize = Mathf.Max(0f, pending.ProjectileSize);
            float projectileRadius = WeaponCycleTracker.NativeProjectileRadiusFromAuthoredSize(projectileSize);
            float scanRange = Mathf.Max(segmentEnd + projectileRadius + 20f, pending.MaxDistance + projectileRadius + 20f);

            Combat.Monster best = null;
            float bestAlong = float.MaxValue;
            float bestDistSq = float.MaxValue;
            bool bestBlocked = false;
            foreach (var candidate in CombatManager.Instance.GetActiveMonsters())
            {
                if (candidate == null || !candidate.IsAlive)
                    continue;
                if (CombatManager.Instance.PeekMonsterCurrentHPWire(candidate) == 0)
                    continue;
                if (!CombatManager.Instance.MatchesInstance(candidate, instanceKey))
                    continue;
                if (!string.IsNullOrWhiteSpace(zoneName)
                    && !string.IsNullOrWhiteSpace(candidate.ZoneName)
                    && !string.Equals(candidate.ZoneName, zoneName, StringComparison.OrdinalIgnoreCase))
                    continue;

                float clientVisibleNow = pending.LastUpdateNativeTick * WeaponCycleTracker.NativeUpdateTickSeconds;
                CombatManager.Instance.TryGetMonsterClientVisiblePosition(candidate, clientVisibleNow, out float candidateX, out float candidateY);
                if (((candidateX - pending.StartX) * (candidateX - pending.StartX)) + ((candidateY - pending.StartY) * (candidateY - pending.StartY)) > scanRange * scanRange)
                    continue;

                float cx = candidateX - pending.StartX;
                float cy = candidateY - pending.StartY;
                float projected = (cx * dirX) + (cy * dirY);
                float radius = WeaponCycleTracker.NativeProjectileCollisionRadius(candidate.CollisionRadius, projectileSize);
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
                return false;

            CombatManager.Instance.SyncMonsterWanderClientVisiblePosition(best, "SpellProjectileChecker-subentity-hit");
            hitMonster = best;
            hitDistance = Mathf.Max(0f, bestAlong);
            worldBlocked = bestBlocked;
            return true;
        }

        private Combat.Monster ResolvePendingSpellTarget(ref PendingSpell pending, float now, string source)
        {
            string instanceKey = !string.IsNullOrWhiteSpace(pending.InstanceKey)
                ? pending.InstanceKey
                : (pending.Conn != null ? GetInstanceZoneKey(pending.Conn) : null);
            if (pending.Monster != null &&
                pending.Monster.IsAlive &&
                CombatManager.Instance.PeekMonsterCurrentHPWire(pending.Monster) != 0 &&
                CombatManager.Instance.MatchesInstance(pending.Monster, instanceKey))
                return pending.Monster;

            Combat.SpellData spell = pending.Spell;
            if (spell == null)
            {
                Combat.SpellDatabase.Initialize();
                spell = ResolveSpellFromManip(pending.Conn, pending.ManipId);
                pending.Spell = spell;
            }

            if (spell == null || spell.ProjectileSize <= 0f)
                return null;

            float startX = pending.StartX;
            float startY = pending.StartY;
            if (pending.Conn != null && Math.Abs(startX) <= 0.0001f && Math.Abs(startY) <= 0.0001f)
            {
                startX = pending.Conn.PlayerPosX;
                startY = pending.Conn.PlayerPosY;
            }

            Combat.Monster target = ResolvePositionSpellTargetFromStart(pending.Conn, spell, startX, startY, pending.AimX, pending.AimY, out float hitDistance);
            if (target == null)
                return null;

            pending.Monster = target;
            pending.ProjectileHitDistance = hitDistance;
            pending.ProjectileDelay = ResolveProjectileImpactDelay(spell, hitDistance);
            Debug.LogError($"[SPELL-PROJECTILE] resolved spell={spell.DisplayName ?? spell.SkillId ?? "spell"} target={target.Name}#{target.EntityId} hitDist={hitDistance:F2} due={pending.DueTime:F3} now={now:F3} source={source ?? "unknown"} queuedWithoutInitialTarget={pending.QueuedWithoutInitialTarget}");
            return target;
        }

        private bool IsPendingSpellDue(PendingSpell pending, float now)
        {
            return pending.DueTime <= 0f || now + 0.0001f >= pending.DueTime;
        }

        private float ResolvePendingSpellImpactTime(PendingSpell pending, float now)
        {
            return pending.DueTime > 0f ? pending.DueTime : now;
        }

        private SpellPreSuffixFlushResult FlushPendingSpellsForMonsterBeforeSynch(Combat.Monster monster, float now, string source)
        {
            var result = new SpellPreSuffixFlushResult
            {
                TargetEntityId = monster != null ? monster.EntityId : 0,
                PendingBefore = _pendingSpells.Count,
                BeforeHPWire = monster != null ? CombatManager.Instance.PeekMonsterCurrentHPWire(monster) : 0u
            };

            if (monster == null || result.PendingBefore == 0)
            {
                result.PendingAfter = _pendingSpells.Count;
                result.AfterHPWire = result.BeforeHPWire;
                return result;
            }

            int pendingCount = _pendingSpells.Count;
            for (int i = 0; i < pendingCount; i++)
            {
                if (!_pendingSpells.TryDequeue(out var pending))
                    break;

                if (pending.ProjectileRuntimeInitialized)
                {
                    uint beforeProjectileHP = CombatManager.Instance.PeekMonsterCurrentHPWire(monster);
                    bool consumed = UpdatePendingSpellProjectile(ref pending, now, source);
                    if (!consumed)
                    {
                        result.RequeuedFuture++;
                        _pendingSpells.Enqueue(pending);
                        continue;
                    }

                    bool sameProjectileTarget = pending.Monster != null && pending.Monster.EntityId == monster.EntityId;
                    if (sameProjectileTarget)
                        result.DueForTarget++;
                    else
                        result.DueOther++;

                    uint afterProjectileHP = CombatManager.Instance.PeekMonsterCurrentHPWire(monster);
                    if (afterProjectileHP != beforeProjectileHP)
                        result.Applied++;
                    continue;
                }

                if (!IsPendingSpellDue(pending, now))
                {
                    result.RequeuedFuture++;
                    _pendingSpells.Enqueue(pending);
                    continue;
                }

                Combat.Monster resolvedTarget = ResolvePendingSpellTarget(ref pending, now, source);
                bool sameTarget = resolvedTarget != null && resolvedTarget.EntityId == monster.EntityId;
                if (sameTarget)
                    result.DueForTarget++;
                else
                    result.DueOther++;

                if (resolvedTarget == null || !resolvedTarget.IsAlive || CombatManager.Instance.PeekMonsterCurrentHPWire(resolvedTarget) == 0)
                {
                    result.SkippedDead++;
                    if (pending.QueuedWithoutInitialTarget)
                        Debug.LogError($"[SPELL-PRE-SUFFIX] source={source ?? "unknown"} spell={pending.Spell?.DisplayName ?? pending.Spell?.SkillId ?? "UNKNOWN"} no-hit after projectile runtime aim=({pending.AimX:F1},{pending.AimY:F1}) due={pending.DueTime:F3} now={now:F3}");
                    continue;
                }

                uint beforeHP = CombatManager.Instance.PeekMonsterCurrentHPWire(resolvedTarget);
                HandleSpellAttack(
                    pending.Conn,
                    pending.State,
                    resolvedTarget,
                    pending.ManipId,
                    pending.UseFlags,
                    pending.AimX,
                    pending.AimY,
                    ResolvePendingSpellImpactTime(pending, now));
                uint afterHP = CombatManager.Instance.PeekMonsterCurrentHPWire(resolvedTarget);
                result.Applied++;
                Debug.LogError($"[SPELL-PRE-SUFFIX] source={source ?? "unknown"} target={resolvedTarget.Name}#{resolvedTarget.EntityId} behavior={resolvedTarget.BehaviorId} due={pending.DueTime:F3} now={now:F3} delay={pending.ProjectileDelay:F3} hitDist={pending.ProjectileHitDistance:F2} hp={beforeHP}->{afterHP} sameTarget={sameTarget} applied=True");
            }

            result.PendingAfter = _pendingSpells.Count;
            result.AfterHPWire = CombatManager.Instance.PeekMonsterCurrentHPWire(monster);
            if (result.PendingBefore > 0 || result.DueForTarget > 0 || result.DueOther > 0 || result.Applied > 0)
                Debug.LogError($"[SPELL-PRE-SUFFIX] source={source ?? "unknown"} target={monster.Name}#{monster.EntityId} pending={result.PendingBefore}->{result.PendingAfter} due={result.DueForTarget} dueOther={result.DueOther} applied={result.Applied} skippedDead={result.SkippedDead} future={result.RequeuedFuture} other={result.RequeuedOther} hp={result.BeforeHPWire}->{result.AfterHPWire} now={now:F3}");
            return result;
        }

        // Pending kills: legacy queue drained by native server-side finalization.
        private Dictionary<uint, (RRConnection conn, Combat.Monster monster, string source, float time)> _pendingKills
            = new Dictionary<uint, (RRConnection, Combat.Monster, string, float)>();
    }
}
