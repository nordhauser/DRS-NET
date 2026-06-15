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
        private Dictionary<ushort, (float X, float Y, float Z)> _npcPositions = new Dictionary<ushort, (float, float, float)>();

        private void HandleObeliskTeleport(RRConnection conn)
        {
            try
            {
                string connId = conn.ConnId.ToString();
                var playerState = QuestManager.Instance.GetPlayerState(connId);
                if (playerState == null) { Debug.LogError("[OBELISK] No player state"); return; }

                string currentZone = "";
                if (_zones.TryGetValue(conn.CurrentZoneId, out Zone cz))
                    currentZone = cz.name;

                // Build sorted list of available obelisk checkpoints (matching client dialog order)
                var available = new List<(string id, int order, string zone)>();
                foreach (var cpId in playerState.UnlockedCheckpoints)
                {
                    var cp = DatabaseLoader.Checkpoints.FirstOrDefault(c =>
                        c.id.Equals(cpId, StringComparison.OrdinalIgnoreCase));
                    if (cp == null) continue;
                    if (cp.zone.Equals(currentZone, StringComparison.OrdinalIgnoreCase)) continue;
                    available.Add((cpId, cp.order, cp.zone));
                }
                available.Sort((a, b) => a.order.CompareTo(b.order));

                if (available.Count == 0)
                {
                    Debug.LogError("[OBELISK] No available checkpoints");
                    return;
                }

                // Cycle through checkpoints on each click
                if (!_obeliskClickIndex.ContainsKey(connId))
                    _obeliskClickIndex[connId] = 0;
                int idx = _obeliskClickIndex[connId] % available.Count;
                _obeliskClickIndex[connId] = idx + 1;

                var selected = available[idx];
                Debug.LogError($"[OBELISK] Teleporting to '{selected.zone}' (checkpoint {selected.id}, index {idx}/{available.Count})");

                var checkpoint = DatabaseLoader.Checkpoints.FirstOrDefault(c =>
                    c.id.Equals(selected.id, StringComparison.OrdinalIgnoreCase));
                if (checkpoint == null) return;

                var destZone = _zones.Values.FirstOrDefault(z =>
                    z.name.Equals(checkpoint.zone, StringComparison.OrdinalIgnoreCase));
                if (destZone != null)
                {
                    conn.PendingSpawnX = destZone.spawnX;
                    conn.PendingSpawnY = destZone.spawnY;
                    conn.PendingSpawnZ = destZone.spawnZ;
                }

                ChangeZone(conn, checkpoint.zone, "");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OBELISK] Error: {ex.Message}");
            }
        }

        private void HandleSavedPlaceTeleport(RRConnection conn)
        {
            try
            {
                // Saved Place = town portal return point
                if (conn.HasSavedTownPortal && !string.IsNullOrEmpty(conn.TownPortalZoneName))
                {
                    Debug.LogError($"[SAVED-PLACE] Teleporting to saved portal: {conn.TownPortalZoneName}");
                    ChangeZone(conn, conn.TownPortalZoneName, "");
                }
                else
                {
                    Debug.LogError("[SAVED-PLACE] No saved town portal — defaulting to town");
                    ChangeZone(conn, "town", "");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SAVED-PLACE] Error: {ex.Message}");
            }
        }




        public void SendSystemMessage(RRConnection conn, string message)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x06);
            writer.WriteByte(0x00);
            writer.WriteByte(0x0D);
            foreach (char c in message)
            {
                writer.WriteByte((byte)c);
            }
            writer.WriteByte(0x00);
            SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
        }

        // ===========================================================================
        // SendBossGatePopup - fires the on-screen popup via the proven QuestManager
        // wire path. TTD-verified call stack from a working wolf-tracking quest kill:
        //
        //   ClientEntityManager::processMessage
        //   -> processComponentUpdate (CEM 0x35)
        //   -> QuestManager::processUpdate (vtable lookup by component id)
        //   -> processUpdateQuest (submsg 0x03)
        //   -> Quest::processUpdate (submsg 0x01 = readObjectives)
        //   -> on objective state change, fires DFCEvent 0x4C400
        //   -> EventChannel::doEvent -> WorldUI::doEvent -> WorldUI::addInfo
        //   -> on-screen popup appears with the objective label as text
        //
        // Sequence: Add fake quest -> Progress packet (state change fires popup) ->
        // Remove fake quest. The fake quest uses Q02_a2 type hash so the client can
        // resolve the type, instanceId 0xFFFFFF00 to avoid conflicts with real quests.
        // Wire format mirrors QuestManager.SendAddPacket / SendProgressPacket /
        // SendRemovePacket exactly, but the objective label is written as raw text
        // (no ": current / required" suffix) so the popup shows clean text.
        // ===========================================================================
        private void SendBossGatePopup(RRConnection conn)
        {
            if (conn == null || !conn.IsConnected || conn.QuestManagerId == 0) return;

            const uint fakeInstanceId = 0xFFFFFF00u;
            // EXPERIMENT: try wrapping in chat-style font tags. If WorldUI::addInfo shares
            // the chat renderer, you get big white glowing text. If it uses a different
            // renderer, you will see literal "<font...>" text and we revert to plain.
            const string popupText = "<font color=white effect=glow>The boss gate is open!</font>";

            // Resolve the Q02_a2 type hash from the loaded quest database. The client
            // already knows this quest type from BossGate-related .gc files, so the
            // GCClassRegistry lookup inside processAddQuest will succeed.
            var q02a2 = DatabaseLoader.Quests.FirstOrDefault(q =>
                string.Equals(q.id, "world.dungeon00.quest.Q02_a2", StringComparison.OrdinalIgnoreCase));
            if (q02a2 == null)
            {
                Debug.LogError("[BOSS-POPUP] Q02_a2 quest not found in database; cannot send popup");
                return;
            }
            uint typeHash = q02a2.hash != 0 ? q02a2.hash : DatabaseLoader.ComputeDJB2Hash(q02a2.id);

            // ----- 1. AddPacket: create fake quest with one INCOMPLETE objective -----
            var addW = new LEWriter();
            addW.WriteByte(0x07);                       // BeginStream
            addW.WriteByte(0x35);                       // processComponentUpdate (CEM)
            addW.WriteUInt16(conn.QuestManagerId);      // QM component
            addW.WriteByte(0x01);                       // QM submsg = AddQuest
            addW.WriteByte(0x04);                       // type marker = uint32 hash
            addW.WriteUInt32(typeHash);                 // Q02_a2 hash
            addW.WriteUInt32(fakeInstanceId);           // synthetic instance id
            addW.WriteByte(0x00);                       // allComplete = false
            addW.WriteByte(0x01);                       // 1 objective
            addW.WriteByte(0x02);                       // flags = has-required (0x02), not-complete
            // Raw label, NO count suffix
            foreach (char c in popupText) addW.WriteByte((byte)c);
            addW.WriteByte(0x00);                       // cstring null terminator
            addW.WriteUInt16(0x0002);                   // required = 2 (Progress will use 1 for state change)
            if (!WritePlayerEntitySynch(conn, addW)) return;
            addW.WriteByte(0x06);                       // EndStream
            SendCompressedA(conn, 0x01, 0x0F, addW.ToArray());

            // ----- 2. ProgressPacket: state change WITHOUT marking complete -----
            // The Add packet wrote required=2; this Progress writes required=1 with the
            // SAME label. Quest::processUpdate sees the field change and fires the popup
            // event, but flags=0x02 (no 0x01 complete bit) means the client should NOT
            // append " (complete)" to the displayed label.
            var progW = new LEWriter();
            progW.WriteByte(0x07);
            progW.WriteByte(0x35);
            progW.WriteUInt16(conn.QuestManagerId);
            progW.WriteByte(0x03);                      // QM submsg = processUpdateQuest
            progW.WriteUInt32(fakeInstanceId);          // same instance
            progW.WriteByte(0x01);                      // Quest submsg = readObjectives
            progW.WriteByte(0x01);                      // 1 objective
            progW.WriteByte(0x02);                      // flags = has-required ONLY (no complete bit)
            foreach (char c in popupText) progW.WriteByte((byte)c);
            progW.WriteByte(0x00);                      // cstring terminator
            progW.WriteUInt16(0x0001);                  // required = 1 (Add had 2 so state changes)
            if (!WritePlayerEntitySynch(conn, progW)) return;
            progW.WriteByte(0x06);                      // EndStream
            SendCompressedA(conn, 0x01, 0x0F, progW.ToArray());

            // ----- 3. RemovePacket: clean up the fake quest from the client log -----
            var rmW = new LEWriter();
            rmW.WriteByte(0x07);
            rmW.WriteByte(0x35);
            rmW.WriteUInt16(conn.QuestManagerId);
            rmW.WriteByte(0x02);                        // QM submsg = RemoveQuest
            rmW.WriteUInt32(fakeInstanceId);
            if (!WritePlayerEntitySynch(conn, rmW)) return;
            rmW.WriteByte(0x06);                        // EndStream
            SendCompressedA(conn, 0x01, 0x0F, rmW.ToArray());

            Debug.LogError($"[BOSS-POPUP] Sent Add+Progress+Remove popup sequence to {conn.LoginName} (instance=0x{fakeInstanceId:X8} hash=0x{typeHash:X8})");
        }

        /// <summary>
        /// Wraps a message in font color/effect tags for the client's HTML renderer.
        /// Supported: color=#RRGGBB, effect=glow
        /// If the message already contains font tags, returns it unchanged.
        /// </summary>
        public static string WrapChatColor(string message, string color, string effect)
        {
            if (string.IsNullOrEmpty(message)) return message;
            // Don't double-wrap if message already has font tags
            if (message.Contains("<font")) return message;

            bool hasColor = !string.IsNullOrEmpty(color);
            bool hasEffect = !string.IsNullOrEmpty(effect) && effect != "none";

            if (!hasColor && !hasEffect) return message;

            string attrs = "";
            if (hasColor) attrs += $" color={color}";
            if (hasEffect) attrs += $" effect={effect}";

            return $"<font{attrs}>{message}</font>";
        }

        /// <summary>
        /// Send a UDP packet to DSOUND.dll. Uses NAT-punched endpoint if available (remote),
        /// otherwise falls back to direct send to tcpIP:2605 (localhost/LAN).
        /// </summary>
        private void SendToDll(RRConnection conn, byte[] packet)
        {
            System.Net.IPEndPoint natEP = null;
            if (conn.LoginName != null) { lock (_dllEndpoints) { _dllEndpoints.TryGetValue(conn.LoginName, out natEP); } }
            if (natEP != null)
            {
                _udpListener.Send(packet, packet.Length, natEP);
            }
            else
            {
                var tcpEP = conn.Client?.Client?.RemoteEndPoint as System.Net.IPEndPoint;
                if (tcpEP == null) return;
                var fallback = new System.Net.IPEndPoint(tcpEP.Address, 2605);
                using (var sender = new System.Net.Sockets.UdpClient()) { sender.Send(packet, packet.Length, fallback); }
            }
        }

        /// <summary>
        /// Send 0xE0 UDP packet to DSOUND.dll on port 2605 to trigger hotbar UI refresh.
        /// DLL fires 0x141F event from main thread which rebuilds Skills UI.
        /// </summary>
        private void SendHotbarRefreshToDll(RRConnection conn, string skillName, uint slotId)
        {
            try
            {
                var tcpEP = conn.Client.Client.RemoteEndPoint as System.Net.IPEndPoint;
                if (tcpEP == null) { Debug.LogError("[HOTBAR-DLL] Cannot get client IP"); return; }
                byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(skillName);
                byte[] packet = new byte[6 + nameBytes.Length];
                packet[0] = 0xE0;
                BitConverter.GetBytes(slotId).CopyTo(packet, 1);
                packet[5] = (byte)nameBytes.Length;
                System.Array.Copy(nameBytes, 0, packet, 6, nameBytes.Length);
                SendToDll(conn, packet);
                Debug.LogError($"[HOTBAR-DLL] Sent 0xE0 slot={slotId} '{skillName}'");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[HOTBAR-DLL] Send failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Send 0x10 UDP packet to DSOUND.dll on port 2605 with GlobalKnobs overrides.
        /// DLL patches RPGSettings in memory.
        /// Per-player: checks free_KEY or member_KEY prefix first, falls back to KEY.
        /// Protocol: [0x10][count:u8][{offset:u16, writeType:u8, value:u32}...]
        ///   writeType: 0=uint32, 1=uint16
        /// </summary>
        private void SendGlobalKnobsToDLL(RRConnection conn)
        {
            Debug.LogError("[KNOBS] RPGSettings override packet skipped; native GlobalKnobs stay package-authored");
        }

        /// <summary>
        /// Send 0x12 UDP to DSOUND.dll — controls membership mode.
        /// DLL calls setFreePlayer/clearFreePlayer + patches XP multiplier.
        /// Protocol: [0x12][u8 isFree][f32 xpMult]
        /// </summary>
        private void SendMembershipToDLL(RRConnection conn)
        {
            try
            {
                var tcpEP = conn.Client.Client.RemoteEndPoint as System.Net.IPEndPoint;
                if (tcpEP == null) return;

                bool isFree = IsPlayerFree(conn.LoginName);
                bool isAdmin = IsPlayerAdmin(conn.LoginName);
                float xpMult = GCDatabase.Instance.GetKnob("FreePlayerExperienceMult", 0.87f);

                byte[] packet = new byte[11];
                packet[0] = 0x12;
                packet[1] = isFree ? (byte)1 : (byte)0;
                System.BitConverter.GetBytes(xpMult).CopyTo(packet, 2);
                packet[6] = isAdmin ? (byte)1 : (byte)0;
                System.BitConverter.GetBytes(conn.DllSessionToken).CopyTo(packet, 7);

                SendToDll(conn, packet);
                Debug.LogError($"[MEMBER] Sent isFree={isFree} isAdmin={isAdmin} xpMult={xpMult} token=0x{conn.DllSessionToken:X8} to DLL");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[MEMBER] Send failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Load account flags from DB on login. Returns false if banned.
        /// Sets _playerIsAdmin and _playerIsFree caches, updates last_login.
        /// </summary>
        private bool LoadAccountFlags(string loginName)
        {
            try
            {
                using (var db = Database.GameDatabase.GetConnection())
                {
                    using (var reader = Database.GameDatabase.ExecuteReader(db,
                        "SELECT is_member, is_banned, is_admin FROM accounts WHERE username = @name",
                        ("@name", loginName)))
                    {
                        if (reader.Read())
                        {
                            int isMember = reader.GetInt32(0);
                            int isBanned = reader.GetInt32(1);
                            int isAdmin = reader.GetInt32(2);

                            _playerIsFree[loginName] = (isMember == 0);
                            _playerIsAdmin[loginName] = (isAdmin != 0);

                            Debug.LogError($"[ACCOUNT] {loginName}: member={isMember} banned={isBanned} admin={isAdmin}");

                            if (isBanned != 0)
                            {
                                Debug.LogError($"[ACCOUNT] ⛔ {loginName} is BANNED — rejecting connection");
                                return false;
                            }
                        }
                    }

                    // Update last_login
                    Database.GameDatabase.ExecuteNonQuery(db,
                        "UPDATE accounts SET last_login = datetime('now') WHERE username = @name",
                        ("@name", loginName));
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ACCOUNT] Error loading flags for {loginName}: {ex.Message}");
            }
            return true;
        }

        /// <summary>
        /// Check if player has admin role.
        /// </summary>
        public bool IsPlayerAdmin(string loginName)
        {
            if (_playerIsAdmin.TryGetValue(loginName, out bool cached))
                return cached;
            // Not cached — load from DB
            try
            {
                using (var db = Database.GameDatabase.GetConnection())
                {
                    object result = Database.GameDatabase.ExecuteScalar(db,
                        "SELECT is_admin FROM accounts WHERE username = @name",
                        ("@name", loginName));
                    if (result != null && result != System.DBNull.Value)
                    {
                        bool isAdmin = System.Convert.ToInt32(result) != 0;
                        _playerIsAdmin[loginName] = isAdmin;
                        return isAdmin;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ACCOUNT] Admin check error for {loginName}: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Set admin flag in DB.
        /// </summary>
        private void SetPlayerAdmin(string loginName, bool isAdmin)
        {
            _playerIsAdmin[loginName] = isAdmin;
            try
            {
                using (var db = Database.GameDatabase.GetConnection())
                {
                    Database.GameDatabase.ExecuteNonQuery(db,
                        "UPDATE accounts SET is_admin = @admin WHERE username = @name",
                        ("@name", loginName), ("@admin", isAdmin ? 1 : 0));
                    // Admins are always members — set is_member=1 in DB
                    if (isAdmin)
                    {
                        Database.GameDatabase.ExecuteNonQuery(db,
                            "UPDATE accounts SET is_member = 1 WHERE username = @name",
                            ("@name", loginName));
                        Debug.LogError($"[ACCOUNT] {loginName} admin=true, is_member=1 (admin→member)");
                    }
                    else
                        Debug.LogError($"[ACCOUNT] {loginName} admin={isAdmin}");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ACCOUNT] Set admin error: {ex.Message}");
            }
        }

        /// <summary>
        /// Set banned flag in DB.
        /// </summary>
        private void SetPlayerBanned(string loginName, bool isBanned)
        {
            try
            {
                using (var db = Database.GameDatabase.GetConnection())
                    Database.GameDatabase.ExecuteNonQuery(db,
                        "UPDATE accounts SET is_banned = @ban WHERE username = @name",
                        ("@name", loginName), ("@ban", isBanned ? 1 : 0));
                Debug.LogError($"[ACCOUNT] {loginName} banned={isBanned}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ACCOUNT] Set banned error: {ex.Message}");
            }
        }

        public void SetPlayerAdminPublic(string loginName, bool isAdmin) { SetPlayerAdmin(loginName, isAdmin); }
        public void SetPlayerBannedPublic(string loginName, bool isBanned) { SetPlayerBanned(loginName, isBanned); }

        private IEnumerator DelayedBanDisconnect(RRConnection conn)
        {
            yield return new DungeonRunners.Engine.WaitForSeconds(3.0f);
            try { SendSystemMessage(conn, "Your account has been banned. Contact an administrator."); } catch { }
            yield return new DungeonRunners.Engine.WaitForSeconds(2.0f);
            Debug.LogError($"[ACCOUNT] Disconnecting banned user: {conn.LoginName}");
            try { conn.Client.Close(); } catch { }
        }
        private bool IsPlayerFree(string loginName)
        {
            // Admins always get member settings
            if (IsPlayerAdmin(loginName)) return false;

            if (_playerIsFree.TryGetValue(loginName, out bool cached))
                return cached;

            // Check DB
            try
            {
                using (var db = Database.GameDatabase.GetConnection())
                {
                    object result = Database.GameDatabase.ExecuteScalar(db,
                        "SELECT is_member FROM accounts WHERE username = @name",
                        ("@name", loginName));
                    if (result != null && result != System.DBNull.Value)
                    {
                        bool isFree = System.Convert.ToInt32(result) == 0;  // is_member=0 → free
                        _playerIsFree[loginName] = isFree;
                        return isFree;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[MEMBER] DB read error for {loginName}: {ex.Message}");
            }

            _playerIsFree[loginName] = false;
            Debug.LogError($"[MEMBER] Missing DB membership for {loginName}; defaulting to MEMBER");
            return false;
        }

        /// <summary>
        /// Set a player's membership in DB + cache. true=free, false=member.
        /// </summary>
        private void SetPlayerMembership(string loginName, bool isFree)
        {
            _playerIsFree[loginName] = isFree;
            try
            {
                using (var db = Database.GameDatabase.GetConnection())
                    Database.GameDatabase.ExecuteNonQuery(db,
                        "UPDATE accounts SET is_member = @member WHERE username = @name",
                        ("@name", loginName), ("@member", isFree ? 0 : 1));
                Debug.LogError($"[MEMBER] Saved {loginName} = {(isFree ? "FREE" : "MEMBER")}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[MEMBER] DB save error: {ex.Message}");
            }
        }



        private void HandleInitialConnection(RRConnection conn, byte messageType, byte[] data)
        {
            if (VerbosePacketLogging)
                Debug.Log($"Initial connection message type: 0x{messageType:X2}, data length: {data.Length}");

            // 🔧 CRITICAL FIX: Ignore empty 0x02 messages (heartbeat/keep-alive)
            if (messageType == 0x02 && data.Length == 0)
            {
                if (VerbosePacketLogging)
                    Debug.Log("Ignoring empty channel 0 type 0x02 message (heartbeat/keep-alive)");
                return;
            }

            if (messageType == 0x00 && data.Length >= 5)
            {
                var reader = new LEReader(data);
                byte subtype = reader.ReadByte();
                uint oneTimeKey = reader.ReadUInt32();

                Debug.Log($"OneTimeKey: 0x{oneTimeKey:X8}");

                if (!GlobalSessions.TryConsume(oneTimeKey, out var user) || string.IsNullOrEmpty(user))
                {
                    if (conn.LoginName != null)
                    {
                        user = conn.LoginName;
                        Debug.LogError($"[QUEUE] Accepting queue connection for {user} (no OneTimeKey needed)");
                    }
                    else
                    {
                        Debug.LogError($"Invalid OneTimeKey 0x{oneTimeKey:X8}");
                        return;
                    }
                }

                conn.LoginName = user;
                _users[conn.ConnId] = user;
                LoadAccountFlags(user);  // Load flags, ban check at spawn time
                if (conn.DllSessionToken == 0)
                {
                    conn.DllSessionToken = (uint)(new System.Random().Next(1, int.MaxValue));
                    NativeRngLedger.LogSystemRandom("dll-session-token", (int)conn.DllSessionToken, user);
                    Debug.LogError($"[DLL-TOKEN] Generated token 0x{conn.DllSessionToken:X8} for {user}");
                }
                Debug.Log($"Initial login SUCCESS for user '{user}'");

                // Send 0x10 message with channel 0x0A
                var ack = new LEWriter();
                ack.WriteByte(0x03);
                SendMessage0x10(conn, 0x0A, ack.ToArray());

                // Send A-lane advance message
                var advance = new LEWriter();
                advance.WriteUInt24(0x00B2B3B4);
                advance.WriteByte(0x00);
                SendCompressedA(conn, 0x00, 0x03, advance.ToArray());
                Debug.Log($"Sent advance message");

                // Start character flow
                StartCharacterFlow(conn);
            }
            else
            {
                Debug.LogWarning($"Unhandled initial connection: type=0x{messageType:X2}, length={data.Length}");
            }
        }

        private void StartCharacterFlow(RRConnection conn)
        {
            Debug.LogError($"[STARTFLOW] StartCharacterFlow for {conn.LoginName}");

            // Force reload from JSON to get latest data
            /* DB always current — no reload needed */
            ;

            // Load characters from JSON into _persistentCharacters
            var savedCharacters = CharacterRepository.GetCharactersForAccount(conn.LoginName);
            Debug.LogError($"[STARTFLOW] GetCharactersForAccount('{conn.LoginName}') returned {savedCharacters?.Count ?? 0} characters");

            _persistentCharacters[conn.LoginName] = new List<GCObject>();

            foreach (var savedChar in savedCharacters)
            {
                var gcObj = new GCObject
                {
                    Id = savedChar.id,
                    NativeClass = "Player",
                    GCClass = "Player",
                    Name = savedChar.name
                };
                _persistentCharacters[conn.LoginName].Add(gcObj);
                Debug.LogError($"[STARTFLOW] Added character: {savedChar.name} (ID={savedChar.id})");
            }

            Debug.LogError($"[STARTFLOW] Total characters loaded: {_persistentCharacters[conn.LoginName].Count}");
        }

        private void SendMessage0x10(RRConnection conn, byte channel, byte[] payload)
        {
            try
            {
                uint clientId = GetClientId24(conn.ConnId);
                uint bodyLen = (uint)(payload?.Length ?? 0);

                var writer = new LEWriter();
                writer.WriteByte(0x10);
                writer.WriteUInt24((int)clientId);
                writer.WriteUInt24((int)bodyLen);
                writer.WriteByte(channel);
                if (bodyLen > 0) writer.WriteBytes(payload);

                byte[] data = writer.ToArray();
                lock (conn.SendLock)
                {
                    conn.Stream.Write(data, 0, data.Length);
                }

                Debug.Log($"Sent 0x10: peer=0x{clientId:X6}, bodyLen={bodyLen}, channel=0x{channel:X2}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"SendMessage0x10 error: {ex.Message}");
            }
        }

        private void HandleCharacterChannel(RRConnection conn, byte messageType, byte[] data)
        {
            Debug.LogError($"[CHARACTER-CHANNEL] Received message type: 0x{messageType:X2}, data length: {data?.Length ?? 0}");

            switch (messageType)
            {
                case 0: // CharacterConnected request
                    Debug.Log($"Client sent 4/0 request - sending character connected response");
                    var connMsg = new LEWriter();
                    connMsg.WriteByte(4);
                    connMsg.WriteByte(0);
                    SendCompressedA(conn, 0x01, 0x0F, connMsg.ToArray());
                    Debug.Log($"Sent 4/0 character connected response");
                    break;

                case 1: // UI nudge
                    Debug.Log($"Client sent UI nudge (4/1) - sending ack");
                    var ack = new LEWriter();
                    ack.WriteByte(4);
                    ack.WriteByte(1);
                    ack.WriteUInt32(0);
                    SendCompressedA(conn, 0x01, 0x0F, ack.ToArray());
                    Debug.Log($"Sent 4/1 ack");
                    break;

                case 2: // 🔥 CHARACTER CREATION REQUEST
                    Debug.LogError($"[CHAR-CREATE] *** RECEIVED CHARACTER CREATION REQUEST (4/2) ***");
                    Debug.LogError($"[CHAR-CREATE] Data: {BitConverter.ToString(data ?? new byte[0])}");
                    HandleCharacterCreate(conn, data);
                    break;

                case 3: // Get character list
                    Debug.LogError($"[CHAR-CHANNEL] Case 3 - calling SendCharacterList NOW");
                    try
                    {
                        SendCharacterList(conn);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[CHAR-CHANNEL] EXCEPTION in SendCharacterList: {ex.Message}\n{ex.StackTrace}");
                    }
                    Debug.LogError($"[CHAR-CHANNEL] Case 3 - SendCharacterList returned");
                    break;

                case 4: // 🔥 CHARACTER DELETE REQUEST
                    Debug.LogError($"[CHAR-DELETE] *** RECEIVED CHARACTER DELETE REQUEST (4/4) ***");
                    Debug.LogError($"[CHAR-DELETE] Data: {BitConverter.ToString(data ?? new byte[0])}");
                    HandleCharacterDelete(conn, data);
                    break;

                case 5: // Play character
                    HandleCharacterPlay(conn, data);
                    break;

                default:
                    Debug.LogWarning($"Unhandled character message: 0x{messageType:X2}");
                    Debug.LogWarning($"Data: {BitConverter.ToString(data ?? new byte[0])}");
                    break;
            }
        }
        private void HandleCharacterCreate(RRConnection conn, byte[] data)
        {
            Debug.LogError("[CHAR-CREATE] ═══════════════════════════════════════════════════════════");
            Debug.LogError($"[CHAR-CREATE] Data hex: {BitConverter.ToString(data)}");

            try
            {
                var reader = new LEReader(data);
                string characterName = reader.ReadCString();
                string avatarClass = reader.ReadCString();

                Debug.LogError($"[CHAR-CREATE] Name: '{characterName}', AvatarClass: '{avatarClass}'");

                // Read appearance bytes (5 bytes after avatar class)
                byte skin = 0, face = 0, faceFeature = 0, hair = 0, hairColor = 0;
                if (reader.Remaining >= 5)
                {
                    skin = reader.ReadByte();
                    face = reader.ReadByte();
                    faceFeature = reader.ReadByte();
                    hair = reader.ReadByte();
                    hairColor = reader.ReadByte();
                    Debug.LogError($"[CHAR-CREATE] Appearance: Skin={skin}, Face={face}, FaceFeature={faceFeature}, Hair={hair}, HairColor={hairColor}");
                }

                // Map avatar class to class name
                string className = "Fighter";
                if (avatarClass.Contains("Fighter")) className = "Fighter";
                else if (avatarClass.Contains("Warlock") || avatarClass.Contains("Mage")) className = "Mage";
                else if (avatarClass.Contains("Ranger")) className = "Ranger";

                // Create character in ClassConfig (this saves to JSON)
                var savedChar = CharacterRepository.CreateCharacter(characterName, className, AccountRepository.GetAccountId(conn.LoginName), conn.LoginName, avatarClass);
                if (savedChar == null)
                {
                    Debug.LogError("[CHAR-CREATE] ❌ CharacterRepository.CreateCharacter returned null!");
                    return;
                }

                // Save the full avatar class for gender AND appearance
                savedChar.avatarClass = avatarClass;
                savedChar.skin = skin;
                savedChar.face = face;
                savedChar.faceFeature = faceFeature;
                savedChar.hair = hair;
                savedChar.hairColor = hairColor;
                CharacterRepository.SaveCharacter(savedChar);

                Debug.LogError($"[CHAR-CREATE] ✅ Created saved character ID={savedChar.id}, AvatarClass={avatarClass}");

                // Create GCObject for persistent characters list
                var newCharacter = new GCObject
                {
                    Id = savedChar.id,
                    NativeClass = "Player",
                    GCClass = "Player",
                    Name = characterName
                };

                if (!_persistentCharacters.ContainsKey(conn.LoginName))
                {
                    _persistentCharacters[conn.LoginName] = new List<GCObject>();
                }
                _persistentCharacters[conn.LoginName].Add(newCharacter);

                // Build full Player object for response
                GCObject tempChar = GCObjectFactory.NewPlayer(characterName);
                tempChar.Id = savedChar.id;

                var avatar = GCObjectFactory.LoadAvatar(savedChar);
                avatar.Id = _nextEntityId++;

                // Assign IDs to avatar children
                foreach (var child in avatar.Children)
                {
                    child.Id = _nextEntityId++;
                    if (child.Children != null)
                    {
                        foreach (var grandchild in child.Children)
                        {
                            if (grandchild.Id == 0)
                                grandchild.Id = _nextEntityId++;
                        }
                    }
                }

                tempChar.AddChild(avatar);

                var procMod = GCObjectFactory.NewProcModifier();
                procMod.Id = _nextEntityId++;
                tempChar.AddChild(procMod);

                // Send create response: [channel][type][ID][name][Player object]
                var writer = new LEWriter();
                writer.WriteByte(4);
                writer.WriteByte(2);
                writer.WriteUInt32(savedChar.id);
                writer.WriteCString(characterName);
                tempChar.WriteFullGCObject(writer);

                SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
                Debug.LogError($"[CHAR-CREATE] ✅ Sent create response with full Player object");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CHAR-CREATE] ❌ Error: {ex.Message}\n{ex.StackTrace}");
            }
        }
        private void SendCharacterCreateResponse(RRConnection conn, bool success, string errorMessage)
        {
            var writer = new LEWriter();
            writer.WriteByte(4);
            writer.WriteByte(2);
            writer.WriteByte(success ? (byte)1 : (byte)0);
            SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
            Debug.LogError($"[CHAR-CREATE] Sent response: success={success}");
        }
        private void HandleCharacterDelete(RRConnection conn, byte[] data)
        {
            Debug.LogError($"[CHAR-DELETE] ═══════════════════════════════════════");
            try
            {
                if (data == null || data.Length == 0)
                {
                    Debug.LogError($"[CHAR-DELETE] ❌ No data received!");
                    return;
                }

                var reader = new LEReader(data);
                string characterName = reader.ReadCString();
                uint characterId = reader.ReadUInt32();
                Debug.LogError($"[CHAR-DELETE] Name: '{characterName}', ID: {characterId}");

                // ═══════════════════════════════════════════════════════════════
                // STEP 1: Remove from persistent list (server-side memory)
                // ═══════════════════════════════════════════════════════════════
                if (_persistentCharacters.TryGetValue(conn.LoginName, out var characters))
                {
                    var toRemove = characters.Find(c => c.Id == characterId);
                    if (toRemove != null)
                    {
                        characters.Remove(toRemove);
                        Debug.LogError($"[CHAR-DELETE] ✅ Removed from persistent list");
                    }
                }

                bool deleted = CharacterRepository.DeleteCharacter(characterId);
                if (!deleted)
                {
                    Debug.LogError($"[CHAR-DELETE] Delete failed for ID={characterId}");
                    SendCharacterList(conn);
                    return;
                }
                Debug.LogError($"[CHAR-DELETE] Deleted from SQLite");

                // ═══════════════════════════════════════════════════════════════
                // STEP 3: Send ONLY the delete ack - nothing else!
                // Client handles UI update locally from its cached character data
                // ═══════════════════════════════════════════════════════════════
                var writer = new LEWriter();
                writer.WriteByte(4);   // Channel
                writer.WriteByte(4);   // Delete ack type
                writer.WriteUInt32(characterId);
                SendCompressedA(conn, 0x01, 0x0F, writer.ToArray());
                Debug.LogError($"[CHAR-DELETE] ✅ Sent delete ack for ID={characterId}");
                Debug.LogError($"[CHAR-DELETE] ✅ Done - NO character list resend (client handles locally)");
                // 🔥 FIX: Send fresh character list so client rebuilds UI with correct data
                SendCharacterList(conn);
                // Debug.LogError($"[CHAR-DELETE] ✅ Sent fresh character list after delete");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CHAR-DELETE] ❌ ERROR: {ex.Message}");
            }
        }


        // ═══════════════════════════════════════════════════════════════════════════════
        // ENTITY CHANNEL HANDLER — Refactored: single handler per opcode, no duplication
        // ═══════════════════════════════════════════════════════════════════════════════

        private void HandleClientEntityChannel(RRConnection conn, byte messageType, byte[] data)
        {
            var reader = new LEReader(data);

            if (messageType == 0x07)
            {
                // BeginStream - contains wrapped sub-messages terminated by 0x06
                ParseEntityStream(conn, reader);
                return;
            }

            // Top-level entity channel message — dispatch by opcode
            switch (messageType)
            {
                // --- Handled opcodes (known wire format) ---
                case 0x03: HandleOpcode_SendUpdate(conn, reader); break;
                case 0x04: HandleEntityRequest(conn, reader, data); break;
                case 0x08: HandleOpcode_CombatTick(conn, reader); break;
                case 0x09: HandleOpcode_Aggro(conn, reader); break;
                case 0x0C: HandleOpcode_RngSeed(conn, reader); break;
                case 0x34:
                case 0x35: HandleOpcode_ComponentUpdate(conn, reader, messageType); break;
                case 0x36: HandleOpcode_EntitySyncHP(conn, reader); break;
                case 0x64: HandleOpcode_StateMachine(conn, reader); break;

                // --- Missing opcodes — log full payload for wire format analysis ---
                case 0x01: LogMissingOpcode(0x01, "BehaviorNotify", data, conn); break;
                // ...etc for all 11 cases (same pattern, just added conn)
                // ...etc for all 11 cases (same pattern, just added conn)
                case 0x0A: LogMissingOpcode(0x0A, "EntityChannel0A", data); break;
                case 0x0B: LogMissingOpcode(0x0B, "EntityChannel0B", data); break;
                case 0x23: LogMissingOpcode(0x23, "PathBehavior", data); break;
                case 0x25: LogMissingOpcode(0x25, "ActionCmd_25", data); break;
                case 0x26: LogMissingOpcode(0x26, "ActionCmd_26", data); break;
                case 0x27: LogMissingOpcode(0x27, "ActionCmd_27", data); break;
                case 0x28: LogMissingOpcode(0x28, "PathTarget_Simple", data); break;
                case 0x29: LogMissingOpcode(0x29, "PathTarget_Extended", data); break;
                case 0x32: HandleOpcode_ComponentUpdate(conn, reader, messageType); break;  // ComponentRequest (same format as 0x35)
                case 0x58: LogMissingOpcode(0x58, "EntityChannel58", data); break;

                default:
                    Debug.LogWarning($"[ENTITY-CH] Unknown type=0x{messageType:X2} len={data?.Length ?? 0}");
                    if (data != null && data.Length > 0)
                        LogMissingOpcode(messageType, $"Unknown_0x{messageType:X2}", data, conn);
                    break;
            }
        }

        /// <summary>
        /// Parses a BeginStream (0x07) sequence of sub-messages until EndStream (0x06).
        /// Only dispatches opcodes with known wire formats to avoid corrupting the stream.
        /// </summary>
        private void ParseEntityStream(RRConnection conn, LEReader reader)
        {
            int loopCount = 0;
            while (reader.Remaining > 0)
            {
                byte subType = reader.ReadByte();
                if (subType == 0x06) break; // EndStream
                loopCount++;

                switch (subType)
                {
                    case 0x03: HandleOpcode_SendUpdate(conn, reader); break;
                    case 0x04: HandleEntityRequest(conn, reader, null); break;
                    case 0x08: HandleOpcode_CombatTick(conn, reader); break;
                    case 0x09: HandleOpcode_Aggro(conn, reader); break;
                    case 0x0C: HandleOpcode_RngSeed(conn, reader); break;
                    case 0x32:
                    case 0x34:
                    case 0x35: HandleOpcode_ComponentUpdate(conn, reader, subType); break;
                    case 0x36: HandleOpcode_EntitySyncHP(conn, reader); break;
                    case 0x64: HandleOpcode_StateMachine(conn, reader); break;
                    default:
                        byte[] leftover = reader.Remaining > 0 ? reader.PeekRemaining() : new byte[0];
                        Debug.LogWarning($"[ENTITY-STREAM] Unknown sub=0x{subType:X2} remain={reader.Remaining}");
                        if (leftover.Length > 0 && leftover.Length < 128)
                        {
                            byte[] fakePacket = new byte[leftover.Length + 1];
                            fakePacket[0] = subType;
                            Array.Copy(leftover, 0, fakePacket, 1, leftover.Length);
                            LogMissingOpcode(subType, $"StreamSub_0x{subType:X2}", fakePacket, conn);
                        }
                        reader.Skip(reader.Remaining);
                        break;
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════════
        // ENTITY OPCODE HANDLERS — Called from both ParseEntityStream and top-level
        // ═══════════════════════════════════════════════════════════════════════════════

        private void HandleOpcode_RngSeed(RRConnection conn, LEReader reader)
        {
            if (reader.Remaining < 4) return;
            uint clientSeed = reader.ReadUInt32();
            string instanceKey = conn != null ? GetInstanceZoneKey(conn) : null;
            uint roomSeed = CombatManager.Instance.GetRoomSeedForInstance(instanceKey);
            bool ready = CombatManager.Instance.TryGetRoomRuntime(instanceKey, out var runtime) && runtime.Initialized;
            Debug.LogError($"[RNG-SEED] Ignored client seed: 0x{clientSeed:X8} instance='{instanceKey ?? ""}' current=0x{roomSeed:X8} ready={ready}");
        }

        private void HandleOpcode_EntitySyncHP(RRConnection conn, LEReader reader)
        {
            if (reader.Remaining < 3) return;
            ushort syncEntityId = reader.ReadUInt16();
            byte syncFlags = reader.ReadByte();
            Debug.LogError($"[HP-SYNC] 0x36: entity={syncEntityId} flags=0x{syncFlags:X2} remaining={reader.Remaining}");
            if ((syncFlags & 0x02) != 0)
            {
                if (reader.Remaining >= 4)
                {
                    uint clientHP = reader.ReadUInt32();
                    Debug.LogError($"[HP-SYNC] entity={syncEntityId} HP={clientHP} ({clientHP / 256} actual)");
                    // Try by EntityId first, then by ANY component ID (BehaviorId, SkillsId, etc.)
                    var monster = CombatManager.Instance.GetMonster(syncEntityId)
                                ?? CombatManager.Instance.GetMonsterByComponent(syncEntityId);
                    if (monster != null)
                    {
                        int serverActual = (int)(CombatManager.Instance.PeekMonsterCurrentHPWire(monster) / 256);
                        int clientActual = (int)(clientHP / 256);
                        int delta = serverActual - clientActual;
                        Debug.LogError($"[HP-SYNC] Monster {monster.Name} (eid={monster.EntityId}) SERVER_HP={serverActual} CLIENT_HP={clientActual} DELTA={delta}");
                        CombatManager.Instance.ObserveClientMonsterHP(monster, clientHP, "HP-SYNC-0x36");
                    }
                    else if (IsAvatarOrAvatarComponentId(conn, syncEntityId))
                    {
                        PlayerState state = GetPlayerState(conn.ConnId.ToString());
                        if (state != null)
                        {
                            int clientActual = (int)(clientHP / 256);
                            int serverMax = (int)(state.MaxHPWire / 256);
                            int serverCurrent = (int)(state.CurrentHPWire / 256);

                            // Verify: client HP should never exceed server MaxHP (+ small tolerance)
                            int tolerance = 5; // allow small rounding difference
                            if (clientActual > serverMax + tolerance)
                            {
                                Debug.LogError($"[HP-VERIFY] ⚠️ Client HP {clientActual} EXCEEDS server MaxHP {serverMax}! Delta=+{clientActual - serverMax}. Possible cheat or missing equipment bonus.");
                            }
                            else if (clientActual < 0)
                            {
                                Debug.LogError($"[HP-VERIFY] ⚠️ Client HP negative: {clientActual}. Ignoring.");
                                return;
                            }

                            Debug.LogError($"[HP-SYNC] Player HP: {serverCurrent} -> {clientActual} (serverMax={serverMax})");
                            ObserveClientPlayerHP(conn, clientHP, "HP-SYNC-0x36");

                            // Extra bytes: unknown format, skip
                            if (reader.Remaining > 0)
                                reader.Skip(reader.Remaining);
                        }
                    }
                    else
                    {
                        Debug.LogError($"[HP-SYNC] Ignored HP for non-avatar entity={syncEntityId} hp={clientHP}");
                        if (reader.Remaining > 0)
                            reader.Skip(reader.Remaining);
                    }
                }
                else
                {
                    Debug.LogError($"[HP-SYNC] BUG: flags=0x02 but only {reader.Remaining} bytes left!");
                    if (reader.Remaining > 0)
                    {
                        byte[] leftover = reader.PeekRemaining();
                        Debug.LogError($"[HP-SYNC] leftover={BitConverter.ToString(leftover)}");
                    }
                }
            }
        }


        private bool IsAvatarEntityId(RRConnection conn, uint entityId)
        {
            return conn?.Avatar != null && entityId == (uint)conn.Avatar.Id;
        }

        private bool IsAvatarHPSyncComponentId(RRConnection conn, uint entityId)
        {
            if (IsAvatarEntityId(conn, entityId))
                return true;

            if (conn == null || entityId == 0)
                return false;

            return entityId == conn.UnitBehaviorId
                || entityId == conn.BehaviorComponentId
                || entityId == conn.SkillsComponentId
                || entityId == conn.ManipulatorsComponentId
                || entityId == conn.ModifiersId
                || entityId == conn.ModifiersComponentId
                || entityId == conn.UnitContainerId;
        }

        private bool IsAvatarOrAvatarComponentId(RRConnection conn, uint entityId)
        {
            return IsAvatarHPSyncComponentId(conn, entityId);
        }

        private void GiveGold(RRConnection conn, uint amount, string source)
        {
            if (conn == null || amount == 0)
                return;

            if (_selectedCharacter.TryGetValue(conn.LoginName, out var rewardGcObj))
            {
                var rewardChar = DungeonRunners.Database.CharacterRepository.GetCharacter(rewardGcObj.Id);
                if (rewardChar != null)
                {
                    rewardChar.gold += amount;
                    DungeonRunners.Database.CharacterRepository.SaveCharacter(rewardChar);
                }
            }

            if (conn.UnitContainerId == 0)
                return;

            var goldPkt = new LEWriter();
            goldPkt.WriteByte(0x07);
            goldPkt.WriteByte(0x35);
            goldPkt.WriteUInt16(conn.UnitContainerId);
            goldPkt.WriteByte(0x20);
            goldPkt.WriteUInt32(amount);
            goldPkt.WriteByte(0x00);
            goldPkt.WriteUInt32(0x00000000);
            goldPkt.WriteByte(0x01);
            WritePlayerEntitySynch(conn, goldPkt);
            goldPkt.WriteByte(0x06);
            SendToClient(conn, goldPkt.ToArray());
            Debug.LogError($"[GIVE-GOLD] source={source ?? "unknown"} +{amount} gold sent to client");
        }

        private bool TryResolveDllReportConnection(IPEndPoint remoteEP, uint entityAddr, uint componentId, out RRConnection conn, out string reason)
        {
            conn = null;
            reason = "no-owner";
            var candidates = new List<RRConnection>();
            foreach (var candidate in SnapshotConnections())
            {
                if (candidate == null || !candidate.IsConnected)
                    continue;
                if (!IsAvatarOrAvatarComponentId(candidate, entityAddr) &&
                    !IsAvatarOrAvatarComponentId(candidate, componentId))
                    continue;
                candidates.Add(candidate);
            }

            if (candidates.Count == 0)
            {
                reason = "no-avatar-owner";
                return false;
            }

            var udpMatches = candidates.Where(c => IsRegisteredDllEndpointMatch(c, remoteEP)).ToList();
            if (udpMatches.Count == 1)
            {
                conn = udpMatches[0];
                reason = "registered-dll-endpoint+avatar-owner";
                return true;
            }
            if (udpMatches.Count > 1)
            {
                reason = "ambiguous-registered-dll-endpoint";
                return false;
            }

            var tcpMatches = candidates.Where(c => IsTcpAddressMatch(c, remoteEP)).ToList();
            if (tcpMatches.Count == 1)
            {
                conn = tcpMatches[0];
                reason = "tcp-address+avatar-owner";
                return true;
            }
            if (tcpMatches.Count > 1)
            {
                reason = "ambiguous-tcp-address+avatar-owner";
                return false;
            }

            if (candidates.Count == 1)
            {
                conn = candidates[0];
                reason = "unique-avatar-owner";
                return true;
            }

            reason = "ambiguous-avatar-owner";
            return false;
        }

        private bool TryResolveDllSenderConnection(IPEndPoint remoteEP, out RRConnection conn, out string reason)
        {
            conn = null;
            reason = "no-sender";
            var connections = SnapshotConnections();
            var udpMatches = connections.Where(c => c != null && c.IsConnected && IsRegisteredDllEndpointMatch(c, remoteEP)).ToList();
            if (udpMatches.Count == 1)
            {
                conn = udpMatches[0];
                reason = "registered-dll-endpoint";
                return true;
            }
            if (udpMatches.Count > 1)
            {
                reason = "ambiguous-registered-dll-endpoint";
                return false;
            }

            var tcpMatches = connections.Where(c => c != null && c.IsConnected && IsTcpAddressMatch(c, remoteEP)).ToList();
            if (tcpMatches.Count == 1)
            {
                conn = tcpMatches[0];
                reason = "tcp-address";
                return true;
            }
            if (tcpMatches.Count > 1)
            {
                reason = "ambiguous-tcp-address";
                return false;
            }

            reason = "no-endpoint-match";
            return false;
        }

        private bool TryResolveDllSessionConnection(IPEndPoint remoteEP, uint sessionToken, out RRConnection conn, out string reason)
        {
            conn = null;
            reason = "no-session";
            var tokenMatches = SnapshotConnections()
                .Where(c => c != null && c.IsConnected && c.DllSessionToken == sessionToken)
                .ToList();

            if (tokenMatches.Count == 0)
            {
                reason = "no-session-token-match";
                return false;
            }

            var udpMatches = tokenMatches.Where(c => IsRegisteredDllEndpointMatch(c, remoteEP)).ToList();
            if (udpMatches.Count == 1)
            {
                conn = udpMatches[0];
                reason = "registered-dll-endpoint+session-token";
                return true;
            }
            if (udpMatches.Count > 1)
            {
                reason = "ambiguous-registered-dll-endpoint+session-token";
                return false;
            }

            var tcpMatches = tokenMatches.Where(c => IsTcpAddressMatch(c, remoteEP)).ToList();
            if (tcpMatches.Count == 1)
            {
                conn = tcpMatches[0];
                reason = "tcp-address+session-token";
                return true;
            }
            if (tcpMatches.Count > 1)
            {
                reason = "ambiguous-tcp-address+session-token";
                return false;
            }

            if (tokenMatches.Count == 1)
            {
                conn = tokenMatches[0];
                reason = "unique-session-token";
                return true;
            }

            reason = "ambiguous-session-token";
            return false;
        }

        private List<RRConnection> SnapshotConnections()
        {
            lock (_connections)
            {
                return _connections.Values.ToList();
            }
        }

        private bool IsRegisteredDllEndpointMatch(RRConnection conn, IPEndPoint remoteEP)
        {
            if (conn == null || remoteEP == null || string.IsNullOrWhiteSpace(conn.LoginName))
                return false;
            lock (_dllEndpoints)
            {
                return _dllEndpoints.TryGetValue(conn.LoginName, out IPEndPoint registered) &&
                       SameEndpoint(registered, remoteEP);
            }
        }

        private static bool SameEndpoint(IPEndPoint left, IPEndPoint right)
        {
            return left != null && right != null &&
                   left.Port == right.Port &&
                   left.Address.Equals(right.Address);
        }

        private static bool IsTcpAddressMatch(RRConnection conn, IPEndPoint remoteEP)
        {
            if (conn == null || remoteEP == null)
                return false;
            try
            {
                var tcpEP = conn.Client?.Client?.RemoteEndPoint as IPEndPoint;
                return tcpEP != null && tcpEP.Address.Equals(remoteEP.Address);
            }
            catch
            {
                return false;
            }
        }

        private bool IsNonUnitPlayerComponentId(RRConnection conn, uint entityId)
        {
            if (conn == null || entityId == 0)
                return false;

            return (conn.Player != null && entityId == (uint)conn.Player.Id)
                || entityId == conn.DialogManagerId
                || entityId == conn.QuestManagerId;
        }

        private bool IsPlayerOwnedComponentId(RRConnection conn, uint entityId)
        {
            return IsNonUnitPlayerComponentId(conn, entityId);
        }

        private void LogPlayerHPVisibleEvent(RRConnection conn, string reason)
        {
            if (conn == null) return;
            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            Debug.LogError($"[PLAYER-HP-TRUTH] VISIBLE-EVENT player={conn.LoginName ?? conn.ConnId.ToString()} serverHP={(state != null ? state.CurrentHPWire / 256f : 0f):F2}/{(state != null ? state.MaxHPWire / 256f : 0f):F2} syncHP={(state != null ? state.SynchHP / 256f : 0f):F2} reason={reason ?? "native monster attack"}");
        }

        private void RecordPlayerHPKnown(RRConnection conn, string source, uint acceptedHPWire)
        {
            if (conn == null) return;
            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            uint playerEntityId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0;
            if (playerEntityId != 0 && state != null)
                HpSyncService.Instance.RecordPlayerOutboundHP(conn, state, playerEntityId, acceptedHPWire, source ?? "unknown");
            else
            {
                conn.LastOutboundHPWire = acceptedHPWire;
                conn.LastOutboundHPTime = Time.time;
                conn.LastOutboundHPSource = source ?? "unknown";
            }
            Debug.LogError($"[PLAYER-HP-TRUTH] KNOWN source={source} player={conn.LoginName ?? conn.ConnId.ToString()} hp={acceptedHPWire / 256f:F2}");
        }

        private void CommitPlayerHPTruth(RRConnection conn, PlayerState state, string source, uint hpWire, bool updateRuntimeHP, bool applyNativeDamageCooldown)
        {
            if (conn == null || state == null) return;
            uint beforeCurrent = state.CurrentHPWire;
            uint beforeSync = state.SynchHP;
            uint beforeMax = state.MaxHPWire;
            if (updateRuntimeHP)
                state.SetCurrentHP(hpWire, applyNativeDamageCooldown);
            uint playerEntityId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0;
            if (playerEntityId != 0)
                HpSyncService.Instance.RegisterPlayer(conn, state, playerEntityId);
            RecordPlayerHPKnown(conn, source, state.SynchHP);
            Debug.LogError($"[PLAYER-HP-TRUTH] COMMIT source={source} player={conn.LoginName ?? conn.ConnId.ToString()} currentHP={beforeCurrent}->{state.CurrentHPWire}/{state.MaxHPWire} syncHP={beforeSync}->{state.SynchHP} maxHP={beforeMax}->{state.MaxHPWire} runtimeUpdate={updateRuntimeHP}");
        }

        private static bool IsWithinHPWireTolerance(uint a, uint b, uint tolerance)
        {
            return a >= b ? a - b <= tolerance : b - a <= tolerance;
        }

        private bool CanSendPlayerSynchronizedHP(RRConnection conn, string packetName)
        {
            if (conn == null) return false;
            RefreshZoneSpawnInvulnerability(conn);
            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            if (state == null) return false;
            uint playerEntityId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0;
            GetNativeValidationCutoff(out _, out float validationCutoffTime);
            bool canAdvanceClientSync = CanAdvancePlayerClientSyncHP(playerEntityId);
            if (playerEntityId != 0 && !IsZoneSpawnInvulnerabilityBlockingCombat(conn))
            {
                SyncContext context = SyncContextFromTag(packetName);
                if (CanApplyPlayerHPBeforeSuffix(context, packetName))
                {
                    CombatManager.Instance.UpdatePlayerPosition(playerEntityId, conn.PlayerPosX, conn.PlayerPosY);
                    FlushPendingKills();
                    FlushWeaponCycleBeforeSynch(conn, $"CanSendPlayerSynchronizedHP:{packetName}", true, validationCutoffTime);
                    CombatManager.Instance.FlushPlayerCombatBeforeSync(playerEntityId, 0f, $"CanSendPlayerSynchronizedHP:{packetName}", validationCutoffTime);
                    if (!CombatManager.Instance.FlushPlayerHPRuntimeBeforeSync(playerEntityId, packetName, out uint runtimeHPWire, out bool unsafeAttack, validationCutoffTime))
                    {
                        Debug.LogError($"[{packetName}] player HP runtime flush incomplete for {conn.LoginName ?? conn.ConnId.ToString()}: serverHP={state.CurrentHPWire / 256f:F2}/{state.MaxHPWire / 256f:F2} syncHP={state.SynchHP / 256f:F2} runtimeHP={runtimeHPWire / 256f:F2} unsafeAttack={unsafeAttack}");
                    }
                }
            }
            if (canAdvanceClientSync)
                state.AdvanceClientSyncHP(validationCutoffTime, packetName);
            return true;
        }

        private bool TryFindPlayerComponentUpdate(RRConnection conn, byte[] innerData, out ushort componentId, out byte subtype)
        {
            return TryFindPlayerComponentUpdate(conn, innerData, out componentId, out subtype, out _);
        }

        private bool TryFindPlayerComponentUpdate(RRConnection conn, byte[] innerData, out ushort componentId, out byte subtype, out int componentOffset)
        {
            componentId = 0;
            subtype = 0;
            componentOffset = -1;
            if (conn == null || innerData == null) return false;
            for (int i = 0; i + 2 < innerData.Length; i++)
            {
                if (innerData[i] != 0x35 && innerData[i] != 0x36) continue;
                if (innerData[i] == 0x35 && i + 3 >= innerData.Length) continue;
                if (innerData[i] == 0x36 && i + 2 >= innerData.Length) continue;
                ushort cid = (ushort)(innerData[i + 1] | (innerData[i + 2] << 8));
                if (!IsAvatarHPSyncComponentId(conn, cid) && !IsNonUnitPlayerComponentId(conn, cid)) continue;
                componentId = cid;
                subtype = innerData[i] == 0x35 ? innerData[i + 3] : (byte)0;
                componentOffset = i;
                return true;
            }
            return false;
        }

        private bool TryFindMonsterComponentUpdate(byte[] innerData, out Monster monster, out ushort componentId, out byte subtype)
        {
            return TryFindMonsterComponentUpdate(innerData, out monster, out componentId, out subtype, out _);
        }

        private bool TryFindMonsterComponentUpdate(byte[] innerData, out Monster monster, out ushort componentId, out byte subtype, out int componentOffset)
        {
            monster = null;
            componentId = 0;
            subtype = 0;
            componentOffset = -1;
            if (innerData == null || CombatManager.Instance == null) return false;
            for (int i = 0; i + 2 < innerData.Length; i++)
            {
                byte opcode = innerData[i];
                if (opcode != 0x35 && opcode != 0x36) continue;
                if (opcode == 0x35 && i + 3 >= innerData.Length) continue;
                ushort cid = (ushort)(innerData[i + 1] | (innerData[i + 2] << 8));
                Monster candidate = ResolveMonsterForComponent(cid, 0);
                if (candidate == null) continue;
                monster = candidate;
                componentId = cid;
                subtype = opcode == 0x35 ? innerData[i + 3] : (byte)0;
                componentOffset = i;
                return true;
            }
            return false;
        }

        private static bool TryGetNativeComponentPayloadEnd(byte[] innerData, int componentOffset, byte subtype, out int payloadEnd)
        {
            payloadEnd = -1;
            if (innerData == null || componentOffset < 0 || componentOffset + 4 > innerData.Length || innerData[componentOffset] != 0x35)
                return false;

            int payloadStart = componentOffset + 4;
            switch (subtype)
            {
                case 0x64:
                    payloadEnd = payloadStart + 1;
                    return payloadEnd <= innerData.Length;
                case 0x65:
                    if (payloadStart + 2 > innerData.Length) return false;
                    int moveCount = innerData[payloadStart + 1];
                    long end = (long)payloadStart + 2L + moveCount * 13L;
                    if (end > int.MaxValue) return false;
                    payloadEnd = (int)end;
                    return payloadEnd <= innerData.Length;
                default:
                    return false;
            }
        }

        private bool TryReadTrailingEntitySynchHP(byte[] innerData, int componentOffset, byte subtype, out uint hpWire)
        {
            hpWire = 0;
            if (innerData == null || innerData.Length < 6) return false;
            int terminator = FindCH07Terminator(innerData);
            if (terminator <= 0) return false;
            int flagsOffset = -1;
            if (TryGetNativeComponentPayloadEnd(innerData, componentOffset, subtype, out int payloadEnd) && payloadEnd <= terminator)
                flagsOffset = payloadEnd;
            else if (terminator >= 5)
                flagsOffset = terminator - 5;
            if (flagsOffset < 0 || flagsOffset >= terminator) return false;
            if ((innerData[flagsOffset] & 0x02) == 0) return false;
            if (flagsOffset + 5 > terminator) return false;
            hpWire = (uint)(innerData[flagsOffset + 1]
                | (innerData[flagsOffset + 2] << 8)
                | (innerData[flagsOffset + 3] << 16)
                | (innerData[flagsOffset + 4] << 24));
            return true;
        }

        private bool TryReadTrailingEntitySynchHP(byte[] innerData, out uint hpWire)
        {
            hpWire = 0;
            if (innerData == null || innerData.Length < 6) return false;
            int terminator = FindCH07Terminator(innerData);
            if (terminator < 5) return false;
            int flagsOffset = terminator - 5;
            if ((innerData[flagsOffset] & 0x02) == 0) return false;
            hpWire = (uint)(innerData[flagsOffset + 1]
                | (innerData[flagsOffset + 2] << 8)
                | (innerData[flagsOffset + 3] << 16)
                | (innerData[flagsOffset + 4] << 24));
            return true;
        }

        private bool TryNormalizeCH07RuntimeStream(RRConnection conn, byte[] innerData, SyncContext context, string packetName, out byte[] normalizedData, out bool drop)
        {
            normalizedData = innerData;
            drop = false;
            return false;
        }

        private static int FindCH07Terminator(byte[] innerData)
        {
            if (innerData == null || innerData.Length == 0) return -1;
            int i = innerData.Length - 1;
            while (i > 0 && innerData[i] == 0x00)
                i--;
            return innerData[i] == 0x06 ? i : -1;
        }

        private bool ObserveClientPlayerHP(RRConnection conn, uint clientHP, string source)
        {
            if (conn == null) return false;
            PlayerState state = GetPlayerState(conn.ConnId.ToString());
            if (state == null) return false;
            uint playerEntityId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0;
            if (playerEntityId != 0)
                HpSyncService.Instance.RegisterPlayer(conn, state, playerEntityId);

            uint tolerance = 5u * 256u;
            if (clientHP > state.MaxHPWire + tolerance)
            {
                Debug.LogError($"[{source}] Client HP {clientHP / 256} exceeds server MaxHP {state.MaxHPWire / 256}; ignored");
                return false;
            }

            uint oldHP = state.SynchHP;
            HpReportDecision hpDecision = HpReportDecision.AcceptObserved("legacy");
            bool hpServiceAccepted = false;
            if (playerEntityId != 0 && HpSyncService.Instance.TryResolvePlayerOwner(conn, state, playerEntityId, out HpOwnerRef owner))
            {
                hpServiceAccepted = HpSyncService.Instance.ObserveClientHpReport(owner, clientHP, HpSyncService.ClassifyReportSource(source), source ?? "client-player-hp", true, out hpDecision);
                if (!hpServiceAccepted)
                {
                    Debug.LogError($"[{source}] Client player HP rejected by HpSyncService: hp={clientHP / 256f:F2}/{state.MaxHPWire / 256f:F2} reason={hpDecision.Reason}");
                    return false;
                }
            }
            uint observedClientHP = Math.Min(clientHP, state.MaxHPWire);
            state.ObserveClientHP(observedClientHP, source);
            conn.LastObservedClientHPWire = state.LastObservedClientHPWire;
            conn.LastObservedClientHPTime = state.LastObservedClientHPTime;
            conn.LastObservedClientHPSource = state.LastObservedClientHPSource;
            if (playerEntityId != 0)
                HpSyncService.Instance.RegisterPlayer(conn, state, playerEntityId);
            if (!hpServiceAccepted)
                RecordPlayerHPKnown(conn, source, state.SynchHP);
            if (observedClientHP < oldHP)
                Debug.LogError($"[{source}] CLIENT PLAYER HP lower observed: {(oldHP - observedClientHP) / 256f:F2} HP ({oldHP}->{observedClientHP}) serverHP={state.CurrentHPWire}");
            else if (observedClientHP > oldHP)
                Debug.LogError($"[{source}] CLIENT PLAYER HP higher observed: {(observedClientHP - oldHP) / 256f:F2} HP ({oldHP}->{observedClientHP}) serverHP={state.CurrentHPWire}");
            else
                Debug.LogError($"[{source}] Player HP unchanged: {observedClientHP / 256f:F2} wire={observedClientHP}");
            return true;
        }

        private bool TryConsumeClientSyncSuffix(RRConnection conn, LEReader reader, string source)
        {
            return TryConsumeClientSyncSuffix(conn, reader, source, null);
        }

        private bool TryConsumeClientSyncSuffix(RRConnection conn, LEReader reader, string source, Monster targetMonster)
        {
            return TryConsumeClientSyncSuffix(conn, reader, source, targetMonster, out _);
        }

        private bool TryConsumeClientSyncSuffix(RRConnection conn, LEReader reader, string source, Monster targetMonster, out bool acceptedMonsterHP)
        {
            acceptedMonsterHP = false;
            if (reader == null || reader.Remaining < 1) return false;

            byte syncFlags = reader.ReadByte();
            if ((syncFlags & 0x02) == 0)
            {
                Debug.LogError($"[{source}] syncFlags=0x{syncFlags:X2} (no HP)");
                return false;
            }

            if (reader.Remaining < 4)
            {
                Debug.LogError($"[{source}] syncFlags=0x{syncFlags:X2} but HP missing remaining={reader.Remaining}");
                return false;
            }

            uint syncHP = reader.ReadUInt32();
            Debug.LogError($"[{source}] syncFlags=0x{syncFlags:X2} HP={syncHP} ({syncHP / 256} actual)");
            if (ObserveClientMonsterHPFromActionSuffix(conn, targetMonster, syncHP, source))
            {
                acceptedMonsterHP = true;
                return true;
            }
            if (targetMonster != null && CombatManager.Instance != null && FitsMonsterHP(targetMonster, syncHP))
            {
                CombatManager.Instance.RecordMonsterHPObservation(targetMonster, syncHP, source);
                acceptedMonsterHP = true;
                return true;
            }
            return ObserveClientPlayerHP(conn, syncHP, source);
        }

        private bool ObserveClientMonsterHPFromActionSuffix(RRConnection conn, Monster targetMonster, uint syncHP, string source)
        {
            if (targetMonster == null || CombatManager.Instance == null) return false;
            if (string.IsNullOrEmpty(source) || !source.StartsWith("ACTION-0x50-SYNC", StringComparison.Ordinal)) return false;
            if (!FitsMonsterHP(targetMonster, syncHP)) return false;
            CombatManager.Instance.RecordMonsterHPObservation(targetMonster, syncHP, source);
            return true;
        }

        private static bool FitsMonsterHP(Monster monster, uint hpWire)
        {
            if (monster == null) return false;
            uint toleranceWire = 5u * 256u;
            return hpWire <= monster.MaxHPWire || hpWire - monster.MaxHPWire <= toleranceWire;
        }

        private void HandleOpcode_CombatTick(RRConnection conn, LEReader reader)
        {
            Debug.LogError($"[COMBAT-TICK] 0x08 remain={reader.Remaining}");
            if (reader.Remaining < 7) return;

            byte combatSub = reader.ReadByte();
            ushort combatSize = reader.ReadUInt16();
            int combatDamage = reader.ReadInt32();
            Debug.LogError($"[COMBAT-TICK] sub={combatSub} size={combatSize} damage={combatDamage}");

            if (combatDamage > 0)
            {
                foreach (var m in CombatManager.Instance.GetActiveMonsters())
                {
                    if (m.State == MonsterState.Combat && m.IsAlive)
                    {
                        Debug.LogError($"[COMBAT-TICK] CLIENT_DMG={combatDamage} on {m.Name} (target ambiguous; waiting for HP sync) SERVER_HP={CombatManager.Instance.PeekMonsterCurrentHPWire(m) / 256}");
                        break;
                    }
                }
            }

            if (reader.Remaining >= 4) reader.ReadUInt32();
        }

        private void HandleOpcode_Aggro(RRConnection conn, LEReader reader)
        {
            if (reader.Remaining < 3) return;
            ushort aggroEntityId = reader.ReadUInt16();
            byte aggroLevel = reader.ReadByte();
            Debug.LogError($"[AGGRO] 0x09: entityId={aggroEntityId} level={aggroLevel}");
            var monster = CombatManager.Instance.GetMonster(aggroEntityId)
                       ?? CombatManager.Instance.GetMonsterByComponent(aggroEntityId);
            if (monster != null && conn?.Avatar != null)
            {
                uint playerEntityId = (uint)conn.Avatar.Id;
                CombatManager.Instance.EngageMonsterFromClientAction(monster, playerEntityId);
            }
        }

        private void HandleOpcode_SendUpdate(RRConnection conn, LEReader reader)
        {
            if (reader.Remaining < 3) return;
            ushort entityId = reader.ReadUInt16();
            byte updateType = reader.ReadByte();
            Debug.LogError($"[SEND-UPDATE] 0x03: entity={entityId} updateType=0x{updateType:X2}");

            if (reader.Remaining < 1) return;
            byte flags = reader.ReadByte();

            if ((flags & 0x02) != 0 && reader.Remaining >= 4)
            {
                uint hp = reader.ReadUInt32();
                Debug.LogError($"[SEND-UPDATE] HP: entity={entityId} HP={hp} ({hp / 256} actual)");

                var monster = CombatManager.Instance.GetMonster(entityId)
                             ?? CombatManager.Instance.GetMonsterByComponent(entityId);
                if (monster != null)
                {
                    int serverActual = (int)(CombatManager.Instance.PeekMonsterCurrentHPWire(monster) / 256);
                    int clientActual = (int)(hp / 256);
                    Debug.LogError($"[SEND-UPDATE] Monster {monster.Name} SERVER_HP={serverActual} CLIENT_HP={clientActual} DELTA={serverActual - clientActual}");
                    CombatManager.Instance.ObserveClientMonsterHP(monster, hp, "SEND-UPDATE-0x03");
                }
                else if (IsAvatarOrAvatarComponentId(conn, entityId))
                {
                    PlayerState state = GetPlayerState(conn.ConnId.ToString());
                    if (state != null)
                    {
                        ObserveClientPlayerHP(conn, hp, "SEND-UPDATE");
                    }
                }
                else
                {
                    Debug.LogError($"[SEND-UPDATE] Ignored HP for non-avatar entity={entityId} HP={hp}");
                }
            }
        }

        private void HandleOpcode_StateMachine(RRConnection conn, LEReader reader)
        {
            if (reader.Remaining < 1) return;
            byte stateFlags = reader.ReadByte();

            ushort stateType = 0xFFFF;
            ushort scope = 0xFFFF;
            ushort target = 0;
            uint stateValue = 0;

            // Binary-verified flag format from StateMachine::ReadMessage @ 0x5F0C70
            if ((stateFlags & 0x02) != 0 && reader.Remaining >= 2)
                stateType = reader.ReadUInt16();
            if ((stateFlags & 0x04) != 0 && reader.Remaining >= 2)
                scope = reader.ReadUInt16();
            if ((stateFlags & 0x08) != 0 && reader.Remaining >= 2)
                target = reader.ReadUInt16();
            if ((stateFlags & 0x20) != 0 && reader.Remaining >= 4)
                stateValue = reader.ReadUInt32();
            if ((stateFlags & 0x10) != 0 && reader.Remaining >= 2)
            {
                ushort wCount = reader.ReadUInt16();
                for (int w = 0; w < wCount && reader.Remaining >= 2; w++)
                    reader.ReadUInt16();
            }

            Debug.LogError($"[STATE-MACHINE] 0x64: flags=0x{stateFlags:X2} type={stateType} scope={scope} target={target} value={stateValue}");
        }

        private void HandleOpcode_ComponentUpdate(RRConnection conn, LEReader reader, byte opcode)
        {
            if (reader.Remaining >= 2)
            {
                byte[] peek = reader.PeekRemaining();
                ushort cid = (ushort)(peek[0] | (peek[1] << 8));
                if (cid >= 50000 && cid < 60000)
                {
                    int posBefore = reader.Position;
                    HandleComponentUpdate(conn, reader);
                    int posAfter = reader.Position;
                    int consumed = posAfter - posBefore;
                    if (consumed > 0 && VerbosePacketLogging)
                        Debug.LogError($"[RELAY] Skipped client-origin monster component echo cid={cid} opcode=0x{opcode:X2} len={consumed}");
                    return;
                }
            }
            HandleComponentUpdate(conn, reader);
        }

        // ═══════════════════════════════════════════════════════════════════════════════
        // MISSING OPCODE LOGGER — Captures full payload for wire format analysis
        // These opcodes are sent by the client but had no server handler.
        // Once wire formats are decoded from captured payloads, replace with real handlers.
        // ═══════════════════════════════════════════════════════════════════════════════

        private void LogMissingOpcode(byte opcode, string name, byte[] data, RRConnection conn = null)
        {
            if (!VerbosePacketLogging) return;
            int len = data?.Length ?? 0;
            string hex = len > 0 ? BitConverter.ToString(data, 0, Math.Min(len, 256)) : "(empty)";
            Debug.LogError($"[DIAG-0x{opcode:X2}] ══════════════════════════════════════════════════════");
            string playerPos = conn != null ? $"player=({conn.PlayerPosX:F1},{conn.PlayerPosY:F1})" : "player=?";
            Debug.LogError($"[DIAG-0x{opcode:X2}] {name} len={len} {playerPos} raw={hex}");

            if (data == null || len < 2) return;

            // Try every uint16 as potential entity/component ID
            var sb = new System.Text.StringBuilder();
            sb.Append($"[DIAG-0x{opcode:X2}] uint16 scan: ");
            for (int i = 0; i <= len - 2; i += 2)
            {
                ushort val = (ushort)(data[i] | (data[i + 1] << 8));
                string tag = "";
                if (val >= 50000 && val < 60000)
                {
                    var mon = CombatManager.Instance.GetMonsterByComponent(val);
                    tag = mon != null ? $"=MON:{mon.Name}" : "=MON_RANGE";
                }
                sb.Append($"[{i}]=0x{val:X4}({val}){tag} ");
            }
            Debug.LogError(sb.ToString());

            // Try every uint32 as potential fixed-point coordinate (÷256)
            if (len >= 4)
            {
                var sb2 = new System.Text.StringBuilder();
                sb2.Append($"[DIAG-0x{opcode:X2}] int32/coord scan: ");
                for (int i = 0; i <= len - 4; i += 4)
                {
                    int val = data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24);
                    float coord = val / 256f;
                    string tag = (coord > -500 && coord < 1000 && coord != 0) ? " <COORD?>" : "";
                    sb2.Append($"[{i}]={val}({coord:F1}){tag} ");
                }
                Debug.LogError(sb2.ToString());
            }

            // Try as: entityId(2) + subType(1) + payload
            if (len >= 3)
            {
                ushort eid = (ushort)(data[0] | (data[1] << 8));
                byte sub = data[2];
                var mon = CombatManager.Instance.GetMonsterByComponent(eid);
                string monTag = mon != null ? $" MONSTER={mon.Name}(pos={mon.PosX:F1},{mon.PosY:F1})" : "";
                Debug.LogError($"[DIAG-0x{opcode:X2}] as [eid={eid}(0x{eid:X4}) sub=0x{sub:X2}]{monTag} remain={len - 3}b");
            }

            // Try as: componentId(2) + data
            if (len >= 5)
            {
                ushort cid = (ushort)(data[0] | (data[1] << 8));
                var mon = CombatManager.Instance.GetMonsterByComponent(cid);
                if (mon != null)
                {
                    Debug.LogError($"[DIAG-0x{opcode:X2}] *** MONSTER CID HIT *** cid={cid} -> {mon.Name} EntityId={mon.EntityId} BehaviorId={mon.BehaviorId}");
                }
            }

            Debug.LogError($"[DIAG-0x{opcode:X2}] ══════════════════════════════════════════════════════");
        }




        // ═══════════════════════════════════════════════════════════════════════════════
        // CENTRALIZED KILL HANDLER — ALL monster death events route through here.
        // This prevents double-XP, double-quest-credit, and level desync bugs.
        //
        // Session 24+: TryFinalizeMonsterKill is the single entry point.
        // HashSet _finalizedMonsterKills prevents any double processing.
        // Multiple paths may detect HP=0 (0x36, 0x03, 0x65, server damage) — first wins.
        // ═══════════════════════════════════════════════════════════════════════════════
        private static int _serverKillCount = 0;
    }
}
