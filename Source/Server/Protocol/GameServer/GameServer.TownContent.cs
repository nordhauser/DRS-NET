using System;
using System.Collections.Generic;
using System.Linq;
using DungeonRunners.Data;
using DungeonRunners.Database;
using DungeonRunners.Gameplay;
using DungeonRunners.Engine;
using DungeonRunners.Utilities;
using DungeonRunners.Core;

namespace DungeonRunners.Networking
{
    // Town King's-Coin trade content ported from the Unity-era UnityGameServer:
    //   * Wishing Well  (world.town.quest.well.*)   — "First Time is Free" / "Toss a King's Coin"
    //   * Token Masters (world.town.quest.token.*)  — TokenFI / TokenMA / TokenRG slot trades
    //
    // Both share the same shape: clicking the NPC ACCEPTS the quest (there is no separate
    // turn-in click), so on accept we scan inventory to satisfy the King's-Coin item
    // objective and immediately run turn-in + rewards — or roll the accept back if the
    // player can't pay. Rewards are rarity-correct curated pools dropped on the ground
    // (the equipment write path renders rarity + Path-B mods correctly; the minimal
    // GiveStackedItem path does not), matching the original verified-in-game behaviour.
    public partial class GameServer
    {
        // Server-authoritative reward RNG (not client-synced). Mirrors the old _lootRng usage.
        private static readonly System.Random _townRewardRng = new System.Random();

        // ===================================================================
        //  Auto-complete-on-accept
        // ===================================================================
        private void TryAutoCompleteWellTokenQuest(RRConnection conn, uint questHash)
        {
            if (!DatabaseLoader.QuestsByHash.TryGetValue(questHash, out var questData)) return;
            if (string.IsNullOrEmpty(questData.id)) return;

            bool isWell = questData.id.StartsWith("world.town.quest.well", StringComparison.OrdinalIgnoreCase);
            bool isToken = questData.id.StartsWith("world.town.quest.token.", StringComparison.OrdinalIgnoreCase)
                           && !questData.id.EndsWith(".Debug_TokenGive", StringComparison.OrdinalIgnoreCase);
            if (!isWell && !isToken) return;

            string connId = conn.ConnId.ToString();
            var questState = QuestManager.Instance.GetPlayerState(connId);
            if (questState == null) return;

            var activeQuest = questState.ActiveQuests.FirstOrDefault(q =>
                q.QuestId.Equals(questData.id, StringComparison.OrdinalIgnoreCase));
            if (activeQuest == null)
            {
                Debug.LogError($"[WELL] AcceptConfirmed but no active quest entry for {questData.id} - skip auto-complete");
                return;
            }

            Debug.LogError($"[WELL] Auto-complete check for {questData.id} (objCount={activeQuest.Objectives?.Count ?? 0})");

            // Tick item objectives by scanning current inventory for the target item (King's Coin).
            if (activeQuest.Objectives != null && activeQuest.Objectives.Count > 0 &&
                _playerInventoryItems.ContainsKey(connId))
            {
                foreach (var obj in activeQuest.Objectives)
                {
                    if (obj.Type == null || !obj.Type.Equals("item", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrEmpty(obj.Target)) continue;

                    int found = 0;
                    foreach (var kvp in _playerInventoryItems[connId])
                    {
                        if (kvp.Value.item?.GCClass == null) continue;
                        if (!kvp.Value.item.GCClass.Equals(obj.Target, StringComparison.OrdinalIgnoreCase)) continue;
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
                // Can't pay (no King's Coin). Roll back the accept so the player isn't left
                // with a stuck quest, and re-broadcast availability so the NPC's '!' returns.
                Debug.LogError($"[WELL] {questData.id} cannot auto-complete - removing from active");
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

            // Repeatable: drop from CompletedQuests so the next click re-accepts cleanly.
            if (questData.repeatable)
            {
                questState.CompletedQuests.RemoveAll(c =>
                    c.Equals(questData.id, StringComparison.OrdinalIgnoreCase));
                QuestManager.Instance.SendAvailableQuestUpdateForZone(conn);
            }

            SavePlayerQuests(conn);
            ApplyQuestRewards(conn, questData);
            Debug.LogError($"[WELL] {questData.id} auto-completed");
        }

        // ===================================================================
        //  Wishing Well rewards
        // ===================================================================
        private void ApplyWishingWellReward(RRConnection conn, QuestData questData)
        {
            int wwLvl = GetPlayerState(conn.ConnId.ToString())?.Level ?? 1;

            // Multi-tier reward pool. All gcTypes WITHOUT the items.pal. prefix (added on-wire
            // by GCObject.GetPacketGCClassFor). Mythics from dedicated *MythicPAL.* files;
            // Rare/Unique are -4/-5 dash-suffix weapons + named-Unique armor. StoredRarity is
            // set explicitly because DetectRarityFromGCClass can't read dash-suffix tiers.
            string[] wwMythics = new[] {
                "PlateMythicPAL.PlateMythicHelm5", "PlateMythicPAL.PlateMythicHelm6",
                "LeatherMythicPAL.LeatherMythicHelm3", "ScaleMythicPAL.ScaleMythicHelm2",
                "PlateMythicPAL.PlateMythicBoots5", "LeatherMythicPAL.LeatherMythicBoots2",
                "PlateMythicPAL.PlateMythicGloves5", "LeatherMythicPAL.LeatherMythicGloves4",
                "PlateMythicPAL.PlateMythicArmor5", "PlateMythicPAL.PlateMythicShoulders5",
                "PlateMythicPAL.PlateMythicShield3",
                "RingMythicPAL.RingMythic12", "RingMythicPAL.RingMythic13", "RingMythicPAL.RingMythic14",
                "RingMythicPAL.RingMythic15", "RingMythicPAL.RingMythic16", "RingMythicPAL.RingMythic17",
                "AmuletMythicPAL.AmuletMythic7",
                "2HStaffMythicPAL.2HStaffMythic3", "2HCrossbowMythicPAL.2HCrossbowMythic5",
                "2HCannonMythicPAL.2HCannonMythic3", "2HGunMythicPAL.2HGunMythic4", "2HGunMythicPAL.2HGunMythic6",
            };
            string[] wwUniques = new[] {
                "1HAxe2PAL.1HAxe2-5", "1HMace2PAL.1HMace2-5", "1HSword2PAL.1HSword2-5",
                "1HStaff2PAL.1HStaff2-5", "1HPick2PAL.1HPick2-5", "1HGun2PAL.1HGun2-5",
                "1HAxe3PAL.1HAxe3-5", "1HMace3PAL.1HMace3-5", "1HSword3PAL.1HSword3-5", "1HStaff3PAL.1HStaff3-5",
                "PlatePAL.PlateUniqueArmor5", "PlatePAL.PlateUniqueHelm5", "PlatePAL.PlateUniqueBoots5",
                "PlatePAL.PlateUniqueGloves5", "PlatePAL.PlateUniqueShoulders5", "PlatePAL.PlateUniqueShield4",
                "LeatherPAL.LeatherUniqueArmor3", "LeatherPAL.LeatherUniqueHelm3", "LeatherPAL.LeatherUniqueBoots3",
                "LeatherPAL.LeatherUniqueGloves3", "ScalePAL.ScaleUniqueHelm2",
            };
            string[] wwRares = new[] {
                "1HAxe2PAL.1HAxe2-4", "1HMace2PAL.1HMace2-4", "1HSword2PAL.1HSword2-4",
                "1HStaff2PAL.1HStaff2-4", "1HPick2PAL.1HPick2-4", "1HGun2PAL.1HGun2-4",
                "1HAxe3PAL.1HAxe3-4", "1HMace3PAL.1HMace3-4",
            };

            int rolls = Math.Max(1, questData.numRewardItems);
            // ~20% gold, ~32% Rare, ~32% Unique, ~16% Mythic.
            const double goldThreshold = 0.20;
            const double rareThreshold = 0.52;
            const double uniqueThreshold = 0.84;

            for (int wi = 0; wi < rolls; wi++)
            {
                double roll = _townRewardRng.NextDouble();
                if (roll < goldThreshold)
                {
                    uint wwGold = (uint)Math.Max(25, wwLvl * 50);
                    GiveGold(conn, wwGold, "wishing-well");
                    Debug.LogError($"[QUEST-REWARDS] Wishing Well gold: {wwGold}g");
                    continue;
                }

                string gcType; int storedRarity; string tierLabel;
                if (roll < rareThreshold) { gcType = wwRares[_townRewardRng.Next(wwRares.Length)]; storedRarity = 3; tierLabel = "rare"; }
                else if (roll < uniqueThreshold) { gcType = wwUniques[_townRewardRng.Next(wwUniques.Length)]; storedRarity = 4; tierLabel = "unique"; }
                else { gcType = wwMythics[_townRewardRng.Next(wwMythics.Length)]; storedRarity = 5; tierLabel = "mythic"; }

                // Mythics get player-level+3 (matches *MythicPAL convention); Rare/Unique leave
                // StoredLevel=-1 so the dash-suffix tier digit drives the level.
                int storedLevel = storedRarity == 5 ? (wwLvl + 3) : -1;
                DropTownReward(conn, gcType, storedRarity, storedLevel, wwLvl, $"wishingwell-{tierLabel}");
            }
        }

        // ===================================================================
        //  Token Master rewards
        // ===================================================================
        private void ApplyTokenMasterReward(RRConnection conn, QuestData questData)
        {
            // Quest id encodes class + slot:
            //   world.town.quest.token.fi.Helm     -> fi (Fighter), Helm
            //   world.town.quest.token.ma.2HWeapon -> ma (Mage),    2HWeapon
            //   world.town.quest.token.rg.Ring     -> rg (Ranger),  Ring
            //   world.town.quest.token.jewelry.Jewelry -> jewelry,  Jewelry
            // Tier weights: ~75% Rare / ~22.6% Unique / ~2.3% Mythic (Kubjas's preference).
            string qid = questData.id ?? "";
            string qLower = qid.ToLowerInvariant();
            string classKey = "fi";
            if (qLower.Contains(".ma.")) classKey = "ma";
            else if (qLower.Contains(".rg.")) classKey = "rg";
            else if (qLower.Contains(".jewelry.")) classKey = "jewelry";

            int lastDot = qid.LastIndexOf('.');
            string slotKey = lastDot > 0 ? qid.Substring(lastDot + 1).ToLowerInvariant() : "ring";

            double tierRoll = _townRewardRng.NextDouble() * 133.0;
            string tier; int storedRarity;
            if (tierRoll < 100.0) { tier = "rare"; storedRarity = 3; }
            else if (tierRoll < 130.0) { tier = "unique"; storedRarity = 4; }
            else { tier = "mythic"; storedRarity = 5; }

            string tokenGcType = PickTokenRewardGcType(classKey, slotKey, tier);
            if (string.IsNullOrEmpty(tokenGcType))
            {
                Debug.LogError($"[QUEST-REWARDS] TokenMaster: no item for class={classKey} slot={slotKey} tier={tier}, skipping");
                return;
            }

            int tokenLvl = GetPlayerState(conn.ConnId.ToString())?.Level ?? 1;
            // Mythic -> level+3; Rare/Unique -> player level (named items would otherwise fall
            // back to level 1 with tiny stat budgets).
            int storedLevel = storedRarity == 5 ? (tokenLvl + 3) : tokenLvl;
            DropTownReward(conn, tokenGcType, storedRarity, storedLevel, tokenLvl,
                $"tokenmaster-{classKey}-{slotKey}-{tier}");
        }

        // ===================================================================
        //  Reward delivery — drop a rarity-correct item on the ground near the player
        // ===================================================================
        private void DropTownReward(RRConnection conn, string gcType, int storedRarity, int storedLevel,
                                    int playerLevel, string tag)
        {
            var place = ResolveItemDropPlacement(conn, conn.CurrentZoneName, conn.InstanceId,
                conn.PlayerPosX, conn.PlayerPosY, conn.PlayerPosZ, conn.PlayerHeading, $"town-reward:{tag}");

            // The drop-write path routes on DFCClass: "Item" (or a ring/amulet name) takes the
            // JEWELRY branch; anything else takes the armor/weapon branch. So only jewelry may be
            // "Item" — armor/weapons must NOT be, or they get a ring-shaped wire layout and the
            // client desyncs ("Invalid type tag"). Jewelry is also caught by the name check.
            string gcLowerForDfc = gcType.ToLowerInvariant();
            bool isJewelry = gcLowerForDfc.Contains("ring") || gcLowerForDfc.Contains("amulet");
            var item = new GCObject
            {
                GCClass = gcType,
                DFCClass = isJewelry ? "Item" : "Armor",
                StoredRarity = storedRarity,
                StoredLevel = storedLevel,
            };

            ushort eid = GetNextLootEntityId();
            TrackDroppedItem(eid, item, conn, 1, place.X, place.Y, place.Z, playerLevel);
            if (_droppedItems.TryGetValue(eid, out var info))
                SendDroppedItemSpawnPacket(conn, eid, info);

            Debug.LogError($"[QUEST-REWARDS] {tag} drop: {gcType} rarity={storedRarity} lvl={storedLevel} at ({place.X:F0},{place.Y:F0})");
        }

        // ===================================================================
        //  Class+slot+tier item routing (ported verbatim from UnityGameServer)
        // ===================================================================
        private static string PickTokenRewardGcType(string classKey, string slotKey, string tier)
        {
            // Class-correct equipment per native TokenReward{Rare,Unique,Mythic}{Fighter,Mage,Ranger}IG.gc.
            // Fighter -> Plate/Scale/Crystal; Ranger -> Leather/Chain/Splint; Mage -> Mage*PAL.
            switch (slotKey)
            {
                case "ring":
                    if (tier == "mythic") return classKey switch {
                        "fi" => PickRandomGc("RingMythicPAL.RingMythic1","RingMythicPAL.RingMythic2","RingMythicPAL.RingMythic4","RingMythicPAL.RingMythic6","RingMythicPAL.RingMythic8","RingMythicPAL.RingMythic9","RingMythicPAL.RingMythic17"),
                        "ma" => PickRandomGc("RingMythicPAL.RingMythic3","RingMythicPAL.RingMythic5","RingMythicPAL.RingMythic11","RingMythicPAL.RingMythic12","RingMythicPAL.RingMythic13"),
                        "rg" => PickRandomGc("RingMythicPAL.RingMythic1","RingMythicPAL.RingMythic7","RingMythicPAL.RingMythic10","RingMythicPAL.RingMythic14","RingMythicPAL.RingMythic15","RingMythicPAL.RingMythic16"),
                        _    => "RingMythicPAL.RingMythic1",
                    };
                    if (tier == "unique") return PickRandomGc("RingPAL.RingUnique1","RingPAL.RingUnique2","RingPAL.RingUnique3","RingPAL.RingUnique4","RingPAL.RingUnique5");
                    return "RingPAL.Ring1";

                case "amulet":
                    if (tier == "mythic") return classKey switch {
                        "fi" => PickRandomGc("AmuletMythicPAL.AmuletMythic4","AmuletMythicPAL.AmuletMythic7"),
                        "ma" => PickRandomGc("AmuletMythicPAL.AmuletMythic2","AmuletMythicPAL.AmuletMythic6"),
                        "rg" => PickRandomGc("AmuletMythicPAL.AmuletMythic2","AmuletMythicPAL.AmuletMythic5"),
                        _    => "AmuletMythicPAL.AmuletMythic7",
                    };
                    if (tier == "unique") return PickRandomGc("AmuletPAL.AmuletUnique1","AmuletPAL.AmuletUnique2","AmuletPAL.AmuletUnique3","AmuletPAL.AmuletUnique4");
                    return "AmuletPAL.Amulet1";

                case "jewelry":
                    return _townRewardRng.NextDouble() < 0.5
                        ? PickTokenRewardGcType(classKey, "ring", tier)
                        : PickTokenRewardGcType(classKey, "amulet", tier);

                case "helm":
                    if (tier == "mythic") return classKey switch {
                        "fi" => PickRandomGc("PlateMythicPAL.PlateMythicHelm1","PlateMythicPAL.PlateMythicHelm3","PlateMythicPAL.PlateMythicHelm4","PlateMythicPAL.PlateMythicHelm5","PlateMythicPAL.PlateMythicHelm6","PlateMythicPAL.PlateMythicHelm7","ScaleMythicPAL.ScaleMythicHelm1","ScaleMythicPAL.ScaleMythicHelm2","CrystalMythicPAL.CrystalMythicHelm1","CrystalMythicPAL.CrystalMythicHelm2"),
                        "rg" => PickRandomGc("LeatherMythicPAL.LeatherMythicHelm1","LeatherMythicPAL.LeatherMythicHelm3","ChainMythicPAL.ChainMythicHelm1","SplintMythicPAL.SplintMythicHelm1"),
                        _    => PickRandomGc("MageHelmPAL.PrebuiltMythic001","MageHelmPAL.PrebuiltMythic002","MageHelmPAL.PrebuiltMythic003","MageHelmPAL.PrebuiltMythic004","MageHelmPAL.PrebuiltMythic005"),
                    };
                    if (tier == "unique") return classKey switch {
                        "fi" => PickRandomGc("PlatePAL.PlateUniqueHelm1","PlatePAL.PlateUniqueHelm2","PlatePAL.PlateUniqueHelm3","PlatePAL.PlateUniqueHelm4","PlatePAL.PlateUniqueHelm5","PlatePAL.PlateUniqueHelm6","PlatePAL.PlateUniqueHelm7","ScalePAL.ScaleUniqueHelm1","ScalePAL.ScaleUniqueHelm2","CrystalPAL.CrystalUniqueHelm1","CrystalPAL.CrystalUniqueHelm2","CrystalPAL.CrystalUniqueHelm3","CrystalPAL.CrystalUniqueHelm4"),
                        "rg" => PickRandomGc("LeatherPAL.LeatherUniqueHelm1","LeatherPAL.LeatherUniqueHelm3","ChainPAL.ChainUniqueHelm1","ChainPAL.ChainUniqueHelm3","SplintPAL.SplintUniqueHelm1","SplintPAL.SplintUniqueHelm2","SplintPAL.SplintUniqueHelm3"),
                        _    => PickRandomGc("MageHelmPAL.Unique001","MageHelmPAL.Unique002","MageHelmPAL.Unique003","MageHelmPAL.Unique004","MageHelmPAL.Unique005","MageHelmPAL.Unique006"),
                    };
                    return classKey switch {
                        "fi" => PickRandomGc("PlatePAL.PlateHelm0","PlatePAL.PlateHelm1","PlatePAL.PlateHelm2","PlatePAL.PlateHelm3","ScalePAL.ScaleHelm1","ScalePAL.ScaleHelm2","CrystalPAL.CrystalHelm1"),
                        "rg" => PickRandomGc("LeatherPAL.LeatherHelm1","LeatherPAL.LeatherHelm5","ChainPAL.ChainHelm1","ChainPAL.ChainHelm2","ChainPAL.ChainHelm3","SplintPAL.SplintHelm1","SplintPAL.SplintHelm2"),
                        _    => PickRandomGc("MageHelmPAL.Rare001","MageHelmPAL.Rare002","MageHelmPAL.Rare003"),
                    };

                case "boots":
                    if (tier == "mythic") return classKey switch {
                        "fi" => PickRandomGc("PlateMythicPAL.PlateMythicBoots1","PlateMythicPAL.PlateMythicBoots3","PlateMythicPAL.PlateMythicBoots5","ScaleMythicPAL.ScaleMythicBoots1","CrystalMythicPAL.CrystalMythicBoots1","CrystalMythicPAL.CrystalMythicBoots2"),
                        "rg" => PickRandomGc("LeatherMythicPAL.LeatherMythicBoots1","LeatherMythicPAL.LeatherMythicBoots2","LeatherMythicPAL.LeatherMythicBoots3","ChainMythicPAL.ChainMythicBoots1","SplintMythicPAL.SplintMythicBoots1"),
                        _    => PickRandomGc("MageBootsPAL.PrebuiltMythic001","MageBootsPAL.PrebuiltMythic002","MageBootsPAL.PrebuiltMythic003","MageBootsPAL.PrebuiltMythic004","MageBootsPAL.PrebuiltMythic005"),
                    };
                    if (tier == "unique") return classKey switch {
                        "fi" => PickRandomGc("PlatePAL.PlateUniqueBoots1","PlatePAL.PlateUniqueBoots2","PlatePAL.PlateUniqueBoots3","PlatePAL.PlateUniqueBoots4","PlatePAL.PlateUniqueBoots5","ScalePAL.ScaleUniqueBoots1","CrystalPAL.CrystalUniqueBoots1","CrystalPAL.CrystalUniqueBoots2","CrystalPAL.CrystalUniqueBoots3","CrystalPAL.CrystalUniqueBoots4"),
                        "rg" => PickRandomGc("LeatherPAL.LeatherUniqueBoots1","LeatherPAL.LeatherUniqueBoots3","ChainPAL.ChainUniqueBoots1","ChainPAL.ChainUniqueBoots3","SplintPAL.SplintUniqueBoots1","SplintPAL.SplintUniqueBoots2","SplintPAL.SplintUniqueBoots3"),
                        _    => PickRandomGc("MageBootsPAL.Unique001","MageBootsPAL.Unique002","MageBootsPAL.Unique003","MageBootsPAL.Unique004","MageBootsPAL.Unique005","MageBootsPAL.Unique006"),
                    };
                    return classKey switch {
                        "fi" => PickRandomGc("PlatePAL.PlateBoots0","PlatePAL.PlateBoots1","PlatePAL.PlateBoots2","PlatePAL.PlateBoots3","ScalePAL.ScaleBoots1","ScalePAL.ScaleBoots2","CrystalPAL.CrystalBoots1"),
                        "rg" => PickRandomGc("LeatherPAL.LeatherBoots1","LeatherPAL.LeatherBoots5","ChainPAL.ChainBoots1","ChainPAL.ChainBoots2","ChainPAL.ChainBoots3","SplintPAL.SplintBoots1","SplintPAL.SplintBoots2"),
                        _    => PickRandomGc("MageBootsPAL.Rare001","MageBootsPAL.Rare002","MageBootsPAL.Rare003"),
                    };

                case "gloves":
                    if (tier == "mythic") return classKey switch {
                        "fi" => PickRandomGc("PlateMythicPAL.PlateMythicGloves1","PlateMythicPAL.PlateMythicGloves3","PlateMythicPAL.PlateMythicGloves5","ScaleMythicPAL.ScaleMythicGloves1","CrystalMythicPAL.CrystalMythicGloves1","CrystalMythicPAL.CrystalMythicGloves2"),
                        "rg" => PickRandomGc("LeatherMythicPAL.LeatherMythicGloves1","LeatherMythicPAL.LeatherMythicGloves3","LeatherMythicPAL.LeatherMythicGloves4","ChainMythicPAL.ChainMythicGloves1","SplintMythicPAL.SplintMythicGloves1"),
                        _    => PickRandomGc("MageGlovesPAL.PrebuiltMythic001","MageGlovesPAL.PrebuiltMythic002","MageGlovesPAL.PrebuiltMythic003","MageGlovesPAL.PrebuiltMythic004"),
                    };
                    if (tier == "unique") return classKey switch {
                        "fi" => PickRandomGc("PlatePAL.PlateUniqueGloves1","PlatePAL.PlateUniqueGloves3","PlatePAL.PlateUniqueGloves4","PlatePAL.PlateUniqueGloves5","ScalePAL.ScaleUniqueGloves1","CrystalPAL.CrystalUniqueGloves1","CrystalPAL.CrystalUniqueGloves2","CrystalPAL.CrystalUniqueGloves3","CrystalPAL.CrystalUniqueGloves4"),
                        "rg" => PickRandomGc("LeatherPAL.LeatherUniqueGloves1","LeatherPAL.LeatherUniqueGloves3","ChainPAL.ChainUniqueGloves1","ChainPAL.ChainUniqueGloves3","SplintPAL.SplintUniqueGloves1","SplintPAL.SplintUniqueGloves2","SplintPAL.SplintUniqueGloves3"),
                        _    => PickRandomGc("MageGlovesPAL.Unique001","MageGlovesPAL.Unique002","MageGlovesPAL.Unique003","MageGlovesPAL.Unique004","MageGlovesPAL.Unique005","MageGlovesPAL.Unique006","MageGlovesPAL.Unique007"),
                    };
                    return classKey switch {
                        "fi" => PickRandomGc("PlatePAL.PlateGloves0","PlatePAL.PlateGloves1","PlatePAL.PlateGloves2","PlatePAL.PlateGloves3","ScalePAL.ScaleGloves1","ScalePAL.ScaleGloves2","CrystalPAL.CrystalGloves1"),
                        "rg" => PickRandomGc("LeatherPAL.LeatherGloves1","LeatherPAL.LeatherGloves5","ChainPAL.ChainGloves1","ChainPAL.ChainGloves2","ChainPAL.ChainGloves3","SplintPAL.SplintGloves1","SplintPAL.SplintGloves2"),
                        _    => PickRandomGc("MageGlovesPAL.Rare001","MageGlovesPAL.Rare002","MageGlovesPAL.Rare003"),
                    };

                case "shoulders":
                    if (tier == "mythic") return classKey switch {
                        "fi" => PickRandomGc("PlateMythicPAL.PlateMythicShoulders1","PlateMythicPAL.PlateMythicShoulders3","PlateMythicPAL.PlateMythicShoulders5","ScaleMythicPAL.ScaleMythicShoulders1","CrystalMythicPAL.CrystalMythicShoulders1","CrystalMythicPAL.CrystalMythicShoulders2"),
                        "rg" => PickRandomGc("LeatherMythicPAL.LeatherMythicShoulders1","LeatherMythicPAL.LeatherMythicShoulders3","ChainMythicPAL.ChainMythicShoulders1","SplintMythicPAL.SplintMythicShoulders1"),
                        _    => PickRandomGc("MageShouldersPAL.PrebuiltMythic001","MageShouldersPAL.PrebuiltMythic002","MageShouldersPAL.PrebuiltMythic003"),
                    };
                    if (tier == "unique") return classKey switch {
                        "fi" => PickRandomGc("PlatePAL.PlateUniqueShoulders1","PlatePAL.PlateUniqueShoulders2","PlatePAL.PlateUniqueShoulders3","PlatePAL.PlateUniqueShoulders4","PlatePAL.PlateUniqueShoulders5","ScalePAL.ScaleUniqueShoulders1","CrystalPAL.CrystalUniqueShoulders1","CrystalPAL.CrystalUniqueShoulders2","CrystalPAL.CrystalUniqueShoulders3","CrystalPAL.CrystalUniqueShoulders4"),
                        "rg" => PickRandomGc("LeatherPAL.LeatherUniqueShoulders1","LeatherPAL.LeatherUniqueShoulders3","ChainPAL.ChainUniqueShoulders1","ChainPAL.ChainUniqueShoulders3","SplintPAL.SplintUniqueShoulders1","SplintPAL.SplintUniqueShoulders2","SplintPAL.SplintUniqueShoulders3"),
                        _    => PickRandomGc("MageShouldersPAL.Unique001","MageShouldersPAL.Unique002","MageShouldersPAL.Unique003","MageShouldersPAL.Unique004","MageShouldersPAL.Unique005"),
                    };
                    return classKey switch {
                        "fi" => PickRandomGc("PlatePAL.PlateShoulders0","PlatePAL.PlateShoulders1","PlatePAL.PlateShoulders2","PlatePAL.PlateShoulders3","ScalePAL.ScaleShoulders1","ScalePAL.ScaleShoulders2","CrystalPAL.CrystalShoulders1"),
                        "rg" => PickRandomGc("LeatherPAL.LeatherShoulders1","LeatherPAL.LeatherShoulders5","ChainPAL.ChainShoulders1","ChainPAL.ChainShoulders2","ChainPAL.ChainShoulders3","SplintPAL.SplintShoulders1","SplintPAL.SplintShoulders2"),
                        _    => PickRandomGc("MageShouldersPAL.Rare001","MageShouldersPAL.Rare002"),
                    };

                case "shield":
                    if (tier == "mythic") return classKey switch {
                        "fi" => PickRandomGc("PlateMythicPAL.PlateMythicShield1","PlateMythicPAL.PlateMythicShield2","PlateMythicPAL.PlateMythicShield3","ScaleMythicPAL.ScaleMythicShield1","CrystalMythicPAL.CrystalMythicShield1"),
                        "rg" => PickRandomGc("LeatherMythicPAL.LeatherMythicShield1","ChainMythicPAL.ChainMythicShield1","SplintMythicPAL.SplintMythicShield1"),
                        _    => PickRandomGc("MageShieldPAL.PrebuiltMythic001","MageShieldPAL.PrebuiltMythic002","MageShieldPAL.PrebuiltMythic003"),
                    };
                    if (tier == "unique") return classKey switch {
                        "fi" => PickRandomGc("PlatePAL.PlateUniqueShield1","PlatePAL.PlateUniqueShield2","PlatePAL.PlateUniqueShield3","PlatePAL.PlateUniqueShield4","ScalePAL.ScaleUniqueShield1","CrystalPAL.CrystalUniqueShield1","CrystalPAL.CrystalUniqueShield2","CrystalPAL.CrystalUniqueShield3"),
                        "rg" => PickRandomGc("LeatherPAL.LeatherUniqueShield1","ChainPAL.ChainUniqueShield1","SplintPAL.SplintUniqueShield1"),
                        _    => PickRandomGc("MageShieldPAL.Unique001","MageShieldPAL.Unique002","MageShieldPAL.Unique003"),
                    };
                    return classKey switch {
                        "fi" => PickRandomGc("PlatePAL.PlateShield1","ScalePAL.ScaleShield1","CrystalPAL.CrystalShield1"),
                        "rg" => PickRandomGc("LeatherPAL.LeatherShield1","ChainPAL.ChainShield1","ChainPAL.ChainShield2","ChainPAL.ChainShield3","ChainPAL.ChainShield4","SplintPAL.SplintShield1"),
                        _    => PickRandomGc("MageShieldPAL.Normal001","MageShieldPAL.Normal002","MageShieldPAL.Normal003"),
                    };

                case "body":
                    if (tier == "mythic") return classKey switch {
                        "fi" => PickRandomGc("PlateMythicPAL.PlateMythicArmor1","PlateMythicPAL.PlateMythicArmor3","PlateMythicPAL.PlateMythicArmor4","PlateMythicPAL.PlateMythicArmor5","ScaleMythicPAL.ScaleMythicArmor1","CrystalMythicPAL.CrystalMythicArmor1","CrystalMythicPAL.CrystalMythicArmor2"),
                        "rg" => PickRandomGc("LeatherMythicPAL.LeatherMythicArmor1","LeatherMythicPAL.LeatherMythicArmor3","ChainMythicPAL.ChainMythicArmor1","SplintMythicPAL.SplintMythicArmor1"),
                        _    => PickRandomGc("MageBodyPAL.PrebuiltMythic001","MageBodyPAL.PrebuiltMythic002","MageBodyPAL.PrebuiltMythic003","MageBodyPAL.PrebuiltMythic004"),
                    };
                    if (tier == "unique") return classKey switch {
                        "fi" => PickRandomGc("PlatePAL.PlateUniqueArmor1","PlatePAL.PlateUniqueArmor3","PlatePAL.PlateUniqueArmor4","PlatePAL.PlateUniqueArmor5","ScalePAL.ScaleUniqueArmor1","CrystalPAL.CrystalUniqueArmor1","CrystalPAL.CrystalUniqueArmor2","CrystalPAL.CrystalUniqueArmor3","CrystalPAL.CrystalUniqueArmor4"),
                        "rg" => PickRandomGc("LeatherPAL.LeatherUniqueArmor1","LeatherPAL.LeatherUniqueArmor2","LeatherPAL.LeatherUniqueArmor3","ChainPAL.ChainUniqueArmor1","ChainPAL.ChainUniqueArmor2","ChainPAL.ChainUniqueArmor3","SplintPAL.SplintUniqueArmor1","SplintPAL.SplintUniqueArmor2","SplintPAL.SplintUniqueArmor3"),
                        _    => PickRandomGc("MageBodyPAL.Unique001","MageBodyPAL.Unique002","MageBodyPAL.Unique003","MageBodyPAL.Unique004","MageBodyPAL.Unique005","MageBodyPAL.Unique006"),
                    };
                    return classKey switch {
                        "fi" => PickRandomGc("PlatePAL.PlateArmor0","PlatePAL.PlateArmor1","PlatePAL.PlateArmor2","PlatePAL.PlateArmor3","ScalePAL.ScaleArmor1","ScalePAL.ScaleArmor2","CrystalPAL.CrystalArmor1"),
                        "rg" => PickRandomGc("LeatherPAL.LeatherArmor1","LeatherPAL.LeatherArmor2","LeatherPAL.LeatherArmor3","LeatherPAL.LeatherArmor5","ChainPAL.ChainArmor1","ChainPAL.ChainArmor2","ChainPAL.ChainArmor3","SplintPAL.SplintArmor1","SplintPAL.SplintArmor2"),
                        _    => PickRandomGc("MageBodyPAL.Rare001","MageBodyPAL.Rare002","MageBodyPAL.Rare003"),
                    };

                case "1hweapon":
                    if (tier == "mythic") return classKey switch {
                        "fi" => PickRandomGc("1HAxeMythicPAL.1HAxeMythic1","1HAxeMythicPAL.1HAxeMythic2","1HAxeMythicPAL.1HAxeMythic3","1HAxeMythicPAL.1HAxeMythic4","1HAxeMythicPAL.1HAxeMythic5","1HMaceMythicPAL.1HMaceMythic1","1HMaceMythicPAL.1HMaceMythic2","1HMaceMythicPAL.1HMaceMythic3","1HMaceMythicPAL.1HMaceMythic4","1HMaceMythicPAL.1HMaceMythic5","1HMaceMythicPAL.1HMaceMythic6","1HMaceMythicPAL.1HMaceMythic7","1HMaceMythicPAL.1HMaceMythic8","1HSwordMythicPAL.1HSwordMythic1","1HSwordMythicPAL.1HSwordMythic2","1HSwordMythicPAL.1HSwordMythic3","1HSwordMythicPAL.1HSwordMythic4","1HSwordMythicPAL.1HSwordMythic5","1HSwordMythicPAL.1HSwordMythic6","1HSwordMythicPAL.1HSwordMythic7","1HSwordMythicPAL.1HSwordMythic8","1HPickMythicPAL.1HPickMythic1","1HPickMythicPAL.1HPickMythic2","1HPickMythicPAL.1HPickMythic3","1HPickMythicPAL.1HPickMythic4"),
                        "ma" => PickRandomGc("1HStaffMythicPAL.1HStaffMythic1","1HStaffMythicPAL.1HStaffMythic2","1HStaffMythicPAL.1HStaffMythic3","1HStaffMythicPAL.1HStaffMythic4","1HStaffMythicPAL.1HStaffMythic5","1HStaffMythicPAL.1HStaffMythic6"),
                        "rg" => PickRandomGc("2HCrossbowMythicPAL.2HCrossbowMythic1","2HCrossbowMythicPAL.2HCrossbowMythic2","2HCrossbowMythicPAL.2HCrossbowMythic3","2HCrossbowMythicPAL.2HCrossbowMythic4","2HCrossbowMythicPAL.2HCrossbowMythic5","2HGunMythicPAL.2HGunMythic1","2HGunMythicPAL.2HGunMythic2","2HGunMythicPAL.2HGunMythic3","2HGunMythicPAL.2HGunMythic4","2HGunMythicPAL.2HGunMythic5","2HGunMythicPAL.2HGunMythic6","2HCannonMythicPAL.2HCannonMythic1","2HCannonMythicPAL.2HCannonMythic2","2HCannonMythicPAL.2HCannonMythic3"),
                        _    => "1HSwordMythicPAL.1HSwordMythic1",
                    };
                    if (tier == "unique") return classKey switch {
                        "fi" => PickRandomGc("1HAxe3PAL.1HAxe3-5","1HMace3PAL.1HMace3-5","1HSword3PAL.1HSword3-5"),
                        "ma" => "1HStaff3PAL.1HStaff3-5",
                        "rg" => PickRandomGc("2HCrossbow3PAL.2HCrossbow3-5","2HGun2PAL.2HGun2-5","2HCannon3PAL.2HCannon3-5"),
                        _    => "1HSword3PAL.1HSword3-5",
                    };
                    return classKey switch {
                        "fi" => PickRandomGc("1HAxe3PAL.1HAxe3-4","1HMace3PAL.1HMace3-4","1HSword3PAL.1HSword3-4"),
                        "ma" => "1HStaff3PAL.1HStaff3-4",
                        "rg" => PickRandomGc("2HCrossbow3PAL.2HCrossbow3-4","2HGun2PAL.2HGun2-4","2HCannon3PAL.2HCannon3-4"),
                        _    => "1HSword3PAL.1HSword3-4",
                    };

                case "2hweapon":
                    if (tier == "mythic") return classKey switch {
                        "fi" => PickRandomGc("2HAxeMythicPAL.2HAxeMythic1","2HAxeMythicPAL.2HAxeMythic2","2HAxeMythicPAL.2HAxeMythic3","2HAxeMythicPAL.2HAxeMythic4","2HAxeMythicPAL.2HAxeMythic5","2HAxeMythicPAL.2HAxeMythic6","2HMaceMythicPAL.2HMaceMythic1","2HMaceMythicPAL.2HMaceMythic2","2HMaceMythicPAL.2HMaceMythic3","2HMaceMythicPAL.2HMaceMythic4","2HMaceMythicPAL.2HMaceMythic5","2HMaceMythicPAL.2HMaceMythic6","2HSwordMythicPAL.2HSwordMythic1","2HSwordMythicPAL.2HSwordMythic2","2HSwordMythicPAL.2HSwordMythic3","2HSwordMythicPAL.2HSwordMythic4","2HPickMythicPAL.2HPickMythic1","2HPickMythicPAL.2HPickMythic2","2HPickMythicPAL.2HPickMythic3"),
                        "ma" => PickRandomGc("2HStaffMythicPAL.2HStaffMythic1","2HStaffMythicPAL.2HStaffMythic2","2HStaffMythicPAL.2HStaffMythic3"),
                        "rg" => PickRandomGc("2HCrossbowMythicPAL.2HCrossbowMythic1","2HCrossbowMythicPAL.2HCrossbowMythic2","2HCrossbowMythicPAL.2HCrossbowMythic3","2HCrossbowMythicPAL.2HCrossbowMythic4","2HCrossbowMythicPAL.2HCrossbowMythic5","2HGunMythicPAL.2HGunMythic1","2HGunMythicPAL.2HGunMythic2","2HGunMythicPAL.2HGunMythic3","2HGunMythicPAL.2HGunMythic4","2HGunMythicPAL.2HGunMythic5","2HGunMythicPAL.2HGunMythic6","2HCannonMythicPAL.2HCannonMythic1","2HCannonMythicPAL.2HCannonMythic2","2HCannonMythicPAL.2HCannonMythic3"),
                        _    => "2HSwordMythicPAL.2HSwordMythic1",
                    };
                    if (tier == "unique") return classKey switch {
                        "fi" => PickRandomGc("2HAxe3PAL.2HAxe3-5","2HMace3PAL.2HMace3-5","2HSword3PAL.2HSword3-5","2HPick3PAL.2HPick3-5"),
                        "ma" => "2HStaff3PAL.2HStaff3-5",
                        "rg" => PickRandomGc("2HCrossbow3PAL.2HCrossbow3-5","2HGun2PAL.2HGun2-5","2HCannon3PAL.2HCannon3-5"),
                        _    => "2HSword3PAL.2HSword3-5",
                    };
                    return classKey switch {
                        "fi" => PickRandomGc("2HAxe3PAL.2HAxe3-4","2HMace3PAL.2HMace3-4","2HSword3PAL.2HSword3-4","2HPick3PAL.2HPick3-4"),
                        "ma" => "2HStaff3PAL.2HStaff3-4",
                        "rg" => PickRandomGc("2HCrossbow3PAL.2HCrossbow3-4","2HGun2PAL.2HGun2-4","2HCannon3PAL.2HCannon3-4"),
                        _    => "2HSword3PAL.2HSword3-4",
                    };
            }
            return "";
        }

        private static string PickRandomGc(params string[] items)
        {
            if (items == null || items.Length == 0) return "";
            return items[_townRewardRng.Next(items.Length)];
        }
    }
}
