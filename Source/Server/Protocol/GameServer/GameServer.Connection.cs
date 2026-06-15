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
        private Dictionary<int, RRConnection> _connections = new Dictionary<int, RRConnection>();

        // ═══ DEBUFF COOLDOWNS: loginName → (modGcType → last applied time) ═══
        private Dictionary<string, Dictionary<string, DateTime>> _debuffCooldowns
            = new Dictionary<string, Dictionary<string, DateTime>>(StringComparer.OrdinalIgnoreCase);

        private void HandleDecryptedUDPPacket(UDPSession session, byte[] data)
        {
            if (data == null || data.Length < 1) return;

            byte msgType = data[0];
            _udpPacketCount++;

            if (VerbosePacketLogging)
            {
                string fingerprint = BitConverter.ToString(data, 0, Math.Min(data.Length, 12));
                if (_seenPacketPatterns.Add(fingerprint))
                {
                    string fullHex = BitConverter.ToString(data, 0, Math.Min(data.Length, 40));
                    Debug.LogError($"[NEW-PKT] #{_udpPacketCount} type=0x{msgType:X2} len={data.Length} hex={fullHex}");
                }

                if (_udpPacketCount % 500 == 0)
                    Debug.LogError($"[UDP-COUNT] {_udpPacketCount} packets, {_seenPacketPatterns.Count} unique patterns");
            }

            WirePacketTally.OnUDP(data);

            if (session.Connection == null)
            {
                Debug.LogError("[UDP-RX] WARNING: No connection linked to UDP session!");
                return;
            }

            byte[] payload = data.Length > 1 ? data.Skip(1).ToArray() : new byte[0];
            HandleClientEntityChannel(session.Connection, msgType, payload);
        }

        private void SendEncryptedUDP(UDPSession session, byte[] data)
        {
            if (session == null || !session.IsEstablished) return;
            byte[] encrypted = EncryptUDP(session, data);
            if (encrypted != null)
                _udpListener.Send(encrypted, encrypted.Length, session.Endpoint);
        }

        public void SendRNGSeedUDP(RRConnection conn, uint seed)
        {
            try
            {
                Debug.LogError($"[UDP-RNG] Starting send, conn={(conn != null ? conn.LoginName : "NULL")}");

                var session = GetUDPSessionForConnection(conn);
                if (session != null && session.IsEstablished)
                {
                    var writer = new LEWriter();
                    writer.WriteByte(0x07);           // BeginStream
                    writer.WriteByte(0x0C);           // processRandomSeed opcode
                    writer.WriteUInt32(seed);         // 4-byte seed
                    writer.WriteByte(0x06);           // EndStream

                    byte[] packet = writer.ToArray();
                    Debug.LogError($"[UDP-RNG] Packet: {BitConverter.ToString(packet)}");

                    int padLen = (8 - (packet.Length % 8)) % 8;
                    if (padLen > 0)
                    {
                        byte[] padded = new byte[packet.Length + padLen];
                        Array.Copy(packet, padded, packet.Length);
                        packet = padded;
                    }

                    SendEncryptedUDP(session, packet);
                    Debug.LogError($"[UDP-RNG] ✅ Sent seed 0x{seed:X8} to {conn.LoginName}");
                }
                else
                {
                    Debug.LogError($"[UDP-RNG] ❌ NO UDP SESSION for {conn?.LoginName ?? "NULL"}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UDP-RNG] EXCEPTION: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void SendUDPHPSync(UDPSession session, uint currentHPWire)
        {
            byte[] packet = new byte[8];
            packet[0] = 0x36;
            BitConverter.GetBytes(currentHPWire).CopyTo(packet, 1);
            SendEncryptedUDP(session, packet);
        }

        private UDPSession GetUDPSessionForConnection(RRConnection conn)
        {
            foreach (var session in _udpSessions.Values)
                if (session.Connection == conn) return session;

            var tcpEP = conn?.Client?.Client?.RemoteEndPoint as IPEndPoint;
            if (tcpEP != null)
                foreach (var session in _udpSessions.Values)
                    if (session.Endpoint.Address.Equals(tcpEP.Address)) return session;

            return null;
        }





        private void OnUDPReceive(IAsyncResult ar)
        {
            try
            {
                IPEndPoint remoteEP = null;
                byte[] data = _udpListener.EndReceive(ar, ref remoteEP);
                if (data.Length == 0)
                {
                    _udpListener.BeginReceive(OnUDPReceive, null);
                    return;
                }
                string epKey = GetEndpointKey(remoteEP);
                byte flags = data[0];

                // DIAGNOSTIC: log every unencrypted DLL packet (0xFE, 0xDD, 0x13, 0xDE, 0xE1)
                if (flags == 0xFE || flags == 0xDD || flags == 0x13 || flags == 0xDE || flags == 0xE1)
                    Debug.LogError($"[UDP-RAW] flags=0x{flags:X2} len={data.Length} from={remoteEP}");

                // === DLL NAT PUNCH: hello from DSOUND.dll via game port ===
                if (flags == 0xFE && data.Length >= 2)
                {
                    string matchedLogin = null;
                    RRConnection matchedConn = null;
                    lock (_connections)
                    {
                        foreach (var kvp in _connections)
                        {
                            try
                            {
                                var tcpEP = kvp.Value.Client?.Client?.RemoteEndPoint as System.Net.IPEndPoint;
                                if (tcpEP != null && tcpEP.Address.Equals(remoteEP.Address))
                                {
                                    matchedLogin = kvp.Value.LoginName;
                                    matchedConn = kvp.Value;
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                    if (matchedLogin != null)
                    {
                        bool isNew;
                        lock (_dllEndpoints) { isNew = !_dllEndpoints.ContainsKey(matchedLogin); _dllEndpoints[matchedLogin] = remoteEP; }
                        byte[] ack = new byte[] { 0xFE, 0x01 };
                        _udpListener.Send(ack, ack.Length, remoteEP);
                        if (isNew)
                        {
                            Debug.LogError($"[DLL-UDP] Registered DLL endpoint for '{matchedLogin}': {remoteEP}");
                            try { SendGlobalKnobsToDLL(matchedConn); } catch { }
                            try { SendMembershipToDLL(matchedConn); } catch { }
                        }
                    }
                    else
                    {
                        string tcpIPs = "";
                        lock (_connections) { foreach (var kvp in _connections) { try { var t = kvp.Value.Client?.Client?.RemoteEndPoint as System.Net.IPEndPoint; if (t != null) tcpIPs += $"{kvp.Value.LoginName}={t.Address} "; } catch { } } }
                        Debug.LogError($"[DLL-UDP] Hello from {remoteEP} but NO IP match. TCP endpoints: [{tcpIPs.Trim()}]");
                    }
                    _udpListener.BeginReceive(OnUDPReceive, null);
                    return;
                }

                // === DLL KILL HOOK: Death notification from injected killhook.dll ===
                // V3 (0x03): DSOUND combined kill confirm + XP via NAT-punched socket
                // Packet: [0xDD][0x03][uint32 clientXP][uint32 token] = 10 bytes
                if (flags == 0xDD && data.Length >= 10 && data[1] == 0x03)
                {
                    uint clientXP = BitConverter.ToUInt32(data, 2);
                    uint token = BitConverter.ToUInt32(data, 6);

                    RRConnection v3Conn = null;
                    if (!TryResolveDllSessionConnection(remoteEP, token, out v3Conn, out string v3ConnReason))
                    {
                        RuntimeEvidenceManager.LogFallbackHit(
                            "hp-dll",
                            "death-unresolved",
                            $"kind=0x03 remote={remoteEP} token=0x{token:X8} reason='{v3ConnReason}'",
                            1);
                        Debug.LogError($"[DLL-DEATH-UNRESOLVED] kind=0x03 remote={remoteEP} token=0x{token:X8} reason={v3ConnReason}");
                        _udpListener.BeginReceive(OnUDPReceive, null);
                        return;
                    }

                    if (clientXP > 0 && clientXP < 100000)
                    {
                        _lastClientXP = clientXP;
                        Debug.LogError($"[DLL-DEATH-V3] observed client XP={clientXP}; server kill path remains authoritative");
                    }

                    // NEW: If packet is 26 bytes, DLL included candidate entity IDs from mob context.
                    // Match them against known alive mobs. First match wins.
                    if (data.Length >= 26)
                    {
                        ushort[] candidates = new ushort[] {
                            BitConverter.ToUInt16(data, 18),
                            BitConverter.ToUInt16(data, 20),
                            BitConverter.ToUInt16(data, 22),
                            BitConverter.ToUInt16(data, 24),
                        };
                        Debug.LogError($"[DLL-DEATH-V3] Candidates: {candidates[0]},{candidates[1]},{candidates[2]},{candidates[3]}");
                        foreach (ushort candEid in candidates)
                        {
                            if (candEid == 0) continue;
                            var m = CombatManager.Instance.GetMonster(candEid)
                                 ?? CombatManager.Instance.GetMonsterByComponent(candEid);
                            if (m != null && (m.IsAlive || _pendingKills.ContainsKey(m.EntityId)))
                            {
                                Debug.LogError($"[DLL-DEATH-V3] Matched candidate eid={candEid} → {m.Name}");
                                TryFinalizeMonsterKill(v3Conn, m, $"DLL-Death-DSOUND-{candEid}");
                                _udpListener.BeginReceive(OnUDPReceive, null);
                                return;
                            }
                        }
                    }

                    // V3 candidates didn't match — but DLL confirmed kill (token OK, XP valid).
                    // Use pending kill queue: the server already knows which monster this player
                    // is fighting. This is NOT a fake fallback — DLL proved the kill happened.
                    lock (_pendingKills)
                    {
                        foreach (var kvp in _pendingKills)
                        {
                            var pk = kvp.Value;
                            if (pk.conn == v3Conn)
                            {
                                Debug.LogError($"[DLL-DEATH-V3] Candidates didn't match server IDs — using pending kill: {pk.monster.Name} eid={pk.monster.EntityId}");
                                TryFinalizeMonsterKill(v3Conn, pk.monster, "DLL-Death-V3-Pending");
                                _udpListener.BeginReceive(OnUDPReceive, null);
                                return;
                            }
                        }
                    }
                    Debug.LogError($"[DLL-DEATH-V3] No pending kill found for {v3Conn.LoginName}");

                    _udpListener.BeginReceive(OnUDPReceive, null);
                    return;
                }
                // V1/V2: Original DINPUT8.dll kill hook (works on localhost)
                if (flags == 0xDD && data.Length >= 10 && (data[1] == 0x01 || data[1] == 0x02))
                {
                    uint entityAddr = BitConverter.ToUInt32(data, 2);
                    uint hpWire = BitConverter.ToUInt32(data, 6);
                    int hpActual = (int)(hpWire / 256);
                    RRConnection killConn = null;
                    if (!TryResolveDllSenderConnection(remoteEP, out killConn, out string killConnReason))
                    {
                        RuntimeEvidenceManager.LogFallbackHit(
                            "hp-dll",
                            "death-unresolved",
                            $"remote={remoteEP} entity=0x{entityAddr:X8} hp={hpWire} reason='{killConnReason}'",
                            1);
                        Debug.LogError($"[DLL-DEATH-UNRESOLVED] kind=0x{data[1]:X2} remote={remoteEP} entity=0x{entityAddr:X8} hp={hpWire} ({hpActual}) reason={killConnReason}");
                    }

                    if (data[1] == 0x02 && data.Length >= 11)
                    {
                        // V2 packet: scanned component IDs from entity memory
                        int count = data[10];
                        int pos = 11;
                        Debug.LogError($"[DLL-DEATH-V2] entity=0x{entityAddr:X8} HP={hpActual} foundIDs={count}");

                        bool matched = false;
                        for (int i = 0; i < count && pos + 6 <= data.Length; i++)
                        {
                            ushort offset = BitConverter.ToUInt16(data, pos); pos += 2;
                            uint val = BitConverter.ToUInt32(data, pos); pos += 4;
                            Debug.LogError($"[DLL-DEATH-V2]   entity+0x{offset:X3} = {val}");

                            if (!matched && killConn != null)
                            {
                                var monster = CombatManager.Instance?.GetMonsterByComponent((ushort)val)
                                           ?? CombatManager.Instance?.GetMonsterByBehaviorId((ushort)val)
                                           ?? CombatManager.Instance?.GetMonsterBySkillsId((ushort)val)
                                           ?? CombatManager.Instance?.GetMonsterByManipulatorsId((ushort)val);
                                if (monster != null)
                                {
                                    Debug.LogError($"[DLL-DEATH-V2] KILL MATCHED: {monster.Name} via cid={val} at entity+0x{offset:X3}");
                                    TryFinalizeMonsterKill(killConn, monster, $"DLL-Death-{val}");
                                    matched = true;
                                }
                            }
                        }

                        // XP comes via separate 0x13 packet from DLL hook, not embedded in death packet
                    }
                    else
                    {
                        // V1 fallback
                        Debug.LogError($"[DLL-DEATH] entity=0x{entityAddr:X8} HP={hpActual}");
                    }

                    _udpListener.BeginReceive(OnUDPReceive, null);
                    return;
                }

                // === DLL XP REPORT: Client's actual XP from addExperience hook ===
                // This packet arrives via DSOUND's NAT-punched socket and proves a kill
                // happened client-side. Acts as kill confirm when DINPUT8's 0xDD V2 is
                // blocked by NAT (remote/VPS play).
                if (flags == 0x13 && data.Length >= 6 && data[1] == 0x01)
                {
                    uint clientXP = BitConverter.ToUInt32(data, 2);
                    uint xpToken = data.Length >= 10 ? BitConverter.ToUInt32(data, 6) : 0;
                    // Validate by IP match
                    RRConnection xpConn = null;
                    lock (_connections)
                    {
                        foreach (var kvp in _connections)
                        {
                            try
                            {
                                var tcpEP = kvp.Value.Client?.Client?.RemoteEndPoint as System.Net.IPEndPoint;
                                if (tcpEP != null && tcpEP.Address.Equals(remoteEP.Address))
                                { xpConn = kvp.Value; break; }
                            }
                            catch { }
                        }
                    }
                    if (xpConn == null)
                    {
                        Debug.LogError($"[DLL-XP] REJECTED — no matching connection from {remoteEP}");
                        _udpListener.BeginReceive(OnUDPReceive, null);
                        return;
                    }
                    if (clientXP > 0 && clientXP < 100000)
                    {
                        _lastClientXP = clientXP;
                        Debug.LogError($"[DLL-XP] observed client XP={clientXP}; server kill path remains authoritative");
                    }
                    else
                    {
                        Debug.LogError($"[DLL-XP] Ignored garbage XP: {clientXP}");
                    }
                    _udpListener.BeginReceive(OnUDPReceive, null);
                    return;
                }

                // === DLL HP HOOK: Real-time HP updates from damage callback ===
                if (flags == 0xDE && data.Length >= 14 && (data[1] == 0x01 || data[1] == 0x02 || data[1] == 0x03))
                {
                    uint entityAddr = BitConverter.ToUInt32(data, 2);
                    uint newHPWire = BitConverter.ToUInt32(data, 6);
                    uint componentId = BitConverter.ToUInt32(data, 10);
                    int hpActual = (int)(newHPWire / 256);
                    Debug.LogError($"[DLL-HP-RAW] kind=0x{data[1]:X2} entityAddr=0x{entityAddr:X8} hp={newHPWire} ({hpActual}) cid={componentId}");

                    // Try to match as monster first
                    var monster = CombatManager.Instance?.GetMonsterByComponent((ushort)componentId)
                               ?? CombatManager.Instance?.GetMonsterByBehaviorId((ushort)componentId)
                               ?? CombatManager.Instance?.GetMonsterBySkillsId((ushort)componentId)
                               ?? CombatManager.Instance?.GetMonsterByManipulatorsId((ushort)componentId);

                    if (monster != null)
                    {
                        CombatManager.Instance.ObserveClientMonsterHP(monster, newHPWire, $"DLL-HP cid={componentId}");
                    }
                    else
                    {
                        // Player — match to correct connection by UDP sender IP
                        if (!TryResolveDllReportConnection(remoteEP, entityAddr, componentId, out RRConnection hpConn, out string hpConnReason))
                        {
                            RuntimeEvidenceManager.LogFallbackHit(
                                "hp-dll",
                                "hp-owner-unresolved",
                                $"remote={remoteEP} entity=0x{entityAddr:X8} cid={componentId} hp={newHPWire} reason='{hpConnReason}'",
                                1);
                            Debug.LogError($"[DLL-HP-UNRESOLVED] kind=0x{data[1]:X2} remote={remoteEP} entity=0x{entityAddr:X8} cid={componentId} hp={newHPWire} ({hpActual}) reason={hpConnReason}");
                        }
                        else
                        {
                            bool matchesAvatar = IsAvatarOrAvatarComponentId(hpConn, entityAddr) ||
                                                 IsAvatarOrAvatarComponentId(hpConn, componentId);
                            if (!matchesAvatar)
                            {
                                Debug.LogError($"[DLL-HP] Ignored non-avatar HP entity=0x{entityAddr:X8} cid={componentId} hp={newHPWire}");
                            }
                            else
                            {
                                PlayerState state = GetPlayerState(hpConn.ConnId.ToString());
                                if (state != null)
                                {
                                    int oldHP = (int)(state.CurrentHPWire / 256);
                                    if (hpActual != oldHP)
                                    {
                                        int serverMax = (int)(state.MaxHPWire / 256);

                                        // Verify client HP against server MaxHP
                                        if (hpActual > serverMax + 5)
                                        {
                                            Debug.LogError($"[DLL-HP] ⚠️ Client HP {hpActual} > server MaxHP {serverMax}! Delta=+{hpActual - serverMax}");
                                        }

                                        bool observedHP = ObserveClientPlayerHP(hpConn, newHPWire, "DLL-HP");
                                        if (!observedHP)
                                        {
                                            Debug.LogError($"[DLL-HP] Player HP observation rejected conn={hpConn.ConnId} entity=0x{entityAddr:X8} cid={componentId} hp={newHPWire}");
                                        }
                                        else
                                        {
                                            Debug.LogError($"[DLL-HP] PLAYER HP observed: {oldHP} -> {hpActual} (max={serverMax})");
                                        }
                                    }

                                    // V3: dump all fields +0x2F0 to +0x320 for mana discovery
                                    if (data[1] == 0x03 && data.Length >= 66)
                                    {
                                        var sb = new System.Text.StringBuilder();
                                        sb.Append($"[DLL-FIELDS] ");
                                        for (int i = 0; i < 13; i++)
                                        {
                                            uint val = BitConverter.ToUInt32(data, 14 + i * 4);
                                            int offset = 0x2F0 + i * 4;
                                            sb.Append($"+0x{offset:X3}={val}({val / 256}) ");
                                        }
                                        Debug.LogError(sb.ToString());
                                    }
                                }
                            }
                        }
                    }

                    _udpListener.BeginReceive(OnUDPReceive, null);
                    return;
                }

                // === DLL POSITION REPORT: Fixed32 entity position at +0x90/+0x94/+0x98 ===
                // 0xE1: marker(1) + entityPtr(4) + componentId(4) + posX(4) + posY(4) + posZ(4) = 21 bytes
                // Position is Fixed32: int32 / 256 = world coordinate
                if (flags == 0xE1 && data.Length >= 21)
                {
                    _e1PacketCount++;
                    if (_e1PacketCount <= 3 || _e1PacketCount % 100 == 0)
                        Debug.LogError($"[DLL-E1] Packet #{_e1PacketCount} received");

                    uint entityAddr = BitConverter.ToUInt32(data, 1);
                    uint componentId = BitConverter.ToUInt32(data, 5);
                    int rawX = BitConverter.ToInt32(data, 9);
                    int rawY = BitConverter.ToInt32(data, 13);
                    int rawZ = BitConverter.ToInt32(data, 17);
                    float posX = rawX / 256f;
                    float posY = rawY / 256f;
                    float posZ = rawZ / 256f;

                    var monster = CombatManager.Instance?.GetMonsterByComponent((ushort)componentId)
                               ?? CombatManager.Instance?.GetMonsterByBehaviorId((ushort)componentId)
                               ?? CombatManager.Instance?.GetMonsterBySkillsId((ushort)componentId)
                               ?? CombatManager.Instance?.GetMonsterByManipulatorsId((ushort)componentId);

                    if (monster != null)
                    {
                        float oldX = monster.PosX;
                        float oldY = monster.PosY;
                        monster.PosX = posX;
                        monster.PosY = posY;  // Game Y axis = server PosY (ground plane)
                        monster.PosZ = posZ;  // Height
                        float dx = posX - oldX;
                        float dy = posY - oldY;
                        float moved = (float)Math.Sqrt(dx * dx + dy * dy);
                        if (VerbosePacketLogging && moved > 1f)
                        {
                            Debug.LogError($"[DLL-POS] {monster.Name} pos=({posX:F1}, {posY:F1}, {posZ:F1}) moved={moved:F1}");
                        }

                        // First position report for this monster — log regardless of distance
                        if (!_positionReportedMonsters.Contains(monster.EntityId))
                        {
                            _positionReportedMonsters.Add(monster.EntityId);
                            if (VerbosePacketLogging) Debug.LogError($"[DLL-POS-INIT] {monster.Name} pos=({posX:F1}, {posY:F1}, {posZ:F1}) cid={componentId}");
                        }
                    }

                    _udpListener.BeginReceive(OnUDPReceive, null);
                    return;
                }

                // Handshake packets (unencrypted)
                if (flags == 0x01)
                {
                    Debug.LogError("[UDP] Got SYN from client! Sending SYN-ACK...");
                    SendUDPSynAck(remoteEP, data);
                }
                else if (flags == 0x03)
                {
                    Debug.LogError("[UDP] Got client ACK response - handshake completing...");
                    SendUDPAck(remoteEP);
                }
                else if (flags == 0x06)
                {
                    Debug.LogError("[UDP] Got ACKKey (0x06) from client");
                    if (_udpSessions.TryGetValue(epKey, out var session))
                    {
                        session.IsEstablished = true;
                        Debug.LogError("[UDP] Session established via ACKKey");
                    }
                }
                else if (flags == 0x02)
                {
                    Debug.LogError("[UDP] Final ACK received! UDP ENCRYPTION ESTABLISHED!");
                    if (_udpSessions.TryGetValue(epKey, out var session))
                    {
                        session.IsEstablished = true;
                        _clientUDPEndpoint = remoteEP;
                        foreach (var kvp in _connections)
                        {
                            var tcpEP = kvp.Value.Client.Client.RemoteEndPoint as IPEndPoint;
                            if (tcpEP != null && tcpEP.Address.Equals(remoteEP.Address))
                            {
                                session.Connection = kvp.Value;
                                Debug.LogError($"[UDP] Linked to TCP connection {kvp.Key} ({kvp.Value.LoginName})");
                                break;
                            }
                        }

                        // ACTIVATION PING
                        byte[] activate = new byte[8] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
                        byte[] encrypted = EncryptUDP(session, activate);
                        _udpListener.Send(encrypted, encrypted.Length, remoteEP);
                        Debug.LogError($"[UDP] Sent activation ping");

                        // ENTITY CHANNEL OPENER
                        byte[] entityOpen = new byte[8];
                        entityOpen[0] = 0x07;  // BeginStream
                        entityOpen[1] = 0x06;  // EndStream
                        byte[] encEntity = EncryptUDP(session, entityOpen);
                        _udpListener.Send(encEntity, encEntity.Length, remoteEP);
                        Debug.LogError("[UDP] Sent entity channel opener 0x07/0x06");

                        Debug.LogError("[UDP] Skipped raw player HP sync bootstrap");
                    }
                }
                else
                {
                    if (VerbosePacketLogging) Debug.LogError($"[UDP] ENCRYPTED PACKET: {data.Length} bytes, first=0x{flags:X2}, data={BitConverter.ToString(data, 0, Math.Min(data.Length, 20))}");
                    if (VerbosePacketLogging) Debug.LogError($"[UDP] Looking for session key: {epKey}, Sessions count: {_udpSessions.Count}");

                    if (_udpSessions.TryGetValue(epKey, out var session))
                    {
                        if (VerbosePacketLogging) Debug.LogError($"[UDP] Found session, IsEstablished={session.IsEstablished}");
                        if (session.IsEstablished)
                        {
                            if (VerbosePacketLogging) Debug.LogError($"[UDP] Calling DecryptUDP...");
                            byte[] decrypted = DecryptUDP(session, data);
                            if (VerbosePacketLogging) Debug.LogError($"[UDP] Decrypt returned: {(decrypted != null ? decrypted.Length + " bytes" : "NULL")}");

                            if (decrypted != null && decrypted.Length > 0)
                            {
                                if (VerbosePacketLogging) Debug.LogError($"[UDP] DECRYPTED: {BitConverter.ToString(decrypted, 0, Math.Min(decrypted.Length, 30))}");
                                HandleDecryptedUDPPacket(session, decrypted);
                            }
                            else
                            {
                                if (VerbosePacketLogging) Debug.LogError($"[UDP] DECRYPTION FAILED or empty result");
                            }
                        }
                        else
                        {
                            if (VerbosePacketLogging) Debug.LogError($"[UDP] Session NOT established yet");
                        }
                    }
                    else
                    {
                        if (VerbosePacketLogging) Debug.LogError($"[UDP] NO SESSION FOUND for {epKey}");
                        if (VerbosePacketLogging) Debug.LogError($"[UDP] Available sessions: {string.Join(", ", _udpSessions.Keys)}");
                    }
                }
                _udpListener.BeginReceive(OnUDPReceive, null);
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Debug.LogError($"[UDP] Receive error: {ex.Message}");
                try { _udpListener.BeginReceive(OnUDPReceive, null); } catch { }
            }
        }
        /// <summary>
        /// remove after testing below TestUDPConnection///////////////////////////////////////////////////////////////////////////////////////////////////////////////
        /// </summary>
        /// <param name="clientEndpoint"></param>
        /* private void TestUDPConnection(IPEndPoint clientEndpoint)
         {
             Debug.LogError("[UDP-TEST] ========== TESTING UDP CONNECTION ==========");

             // Test 1: Send a simple 0x00 packet (keepalive-style)
             byte[] test1 = new byte[] { 0x00, 0x00, 0x00, 0x00 };
             _udpListener.Send(test1, test1.Length, clientEndpoint);
             Debug.LogError($"[UDP-TEST] Sent test1 (4 bytes): {BitConverter.ToString(test1)}");

             // Test 2: Send something that looks like a combat header
             byte[] test2 = new byte[] { 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
             _udpListener.Send(test2, test2.Length, clientEndpoint);
             Debug.LogError($"[UDP-TEST] Sent test2 - 0x08 header (8 bytes): {BitConverter.ToString(test2)}");

             // Test 3: Try 0x36 style packet  
             byte[] test3 = new byte[] { 0x36, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
             _udpListener.Send(test3, test3.Length, clientEndpoint);
             Debug.LogError($"[UDP-TEST] Sent test3 - 0x36 header (8 bytes): {BitConverter.ToString(test3)}");

             Debug.LogError("[UDP-TEST] ========== WAITING FOR RESPONSES ==========");
         }*/




        // ═══════════════════════════════════════════════════════════════════
        // STATIC KEYS FROM cryNullKeyExchanger (HARDCODED IN CLIENT BINARY)
        // ═══════════════════════════════════════════════════════════════════
        private static readonly byte[] NULL_PUBKEY = System.Text.Encoding.ASCII.GetBytes("PUBKEY12");

        private void HandleDuelRequest(RRConnection conn, uint targetCharSqlId)
        {
            // Find target connection by CharSQLID
            RRConnection target = null;
            foreach (var kvp in _selectedCharacter)
            {
                if (kvp.Value.Id == (int)targetCharSqlId)
                {
                    target = FindConnectionByLogin(kvp.Key);
                    break;
                }
            }

            if (target == null)
            {
                Debug.LogError($"[PVP-DUEL] Target CharSQLID {targetCharSqlId} not online");
                return;
            }

            IssueDuelChallenge(conn, target);
        }

        /// <summary>
        /// Shared duel-challenge path used by both opcode 0x2D and the @duel chat command.
        /// Returns null on success, or a human-readable rejection reason.
        /// </summary>
        public string IssueDuelChallenge(RRConnection challengerConn, RRConnection targetConn)
        {
            if (challengerConn == null || targetConn == null) return "Connection not found.";

            // Look up both characters for level check + CharSqlId
            var challengerChar = _selectedCharacter.TryGetValue(challengerConn.LoginName, out var cs)
                ? CharacterRepository.GetCharacter(cs.Id) : null;
            var targetChar = _selectedCharacter.TryGetValue(targetConn.LoginName, out var ts)
                ? CharacterRepository.GetCharacter(ts.Id) : null;
            if (challengerChar == null || targetChar == null)
            {
                Debug.LogError("[PVP-DUEL] Character lookup failed");
                return "Character lookup failed.";
            }

            uint challengerCharSqlId = (uint)challengerChar.id;
            uint targetCharSqlId     = (uint)targetChar.id;
            string err = _duelManager.TryChallenge(challengerConn.LoginName, challengerCharSqlId,
                targetConn.LoginName, targetCharSqlId,
                challengerChar.level, targetChar.level);
            if (err != null)
            {
                Debug.LogError($"[PVP-DUEL] Challenge rejected: {err}");
                return err;
            }

            // Notify target: Challenged
            byte[] targetPkt = PVPPackets.BuildDuelStatus(
                PVPPackets.DuelStatusType.Challenged, challengerCharSqlId, 0, 0);
            SendToClient(targetConn, targetPkt);
            Debug.LogError($"[PVP-DUEL] Sent Challenged to {targetConn.LoginName}");
            return null;
        }

        /// <summary>Chat-driven duel accept: same effect as opcode 0x2E.</summary>
        public bool AcceptDuel(RRConnection conn)
        {
            HandleDuelAccept(conn);
            return _duelManager.IsInDuel(conn.LoginName);
        }

        /// <summary>Chat-driven duel decline: same effect as opcode 0x2F.</summary>
        public bool DeclineDuel(RRConnection conn)
        {
            var hadDuel = _duelManager.IsInDuel(conn.LoginName);
            HandleDuelDecline(conn);
            return hadDuel;
        }

        /// <summary>Human-readable duel state for @duel status.</summary>
        public string GetDuelStatusFor(string loginName)
        {
            var d = _duelManager.GetDuel(loginName);
            if (d == null) return "No active duel.";
            string other = string.Equals(d.ChallengerLogin, loginName, StringComparison.OrdinalIgnoreCase)
                ? d.TargetLogin : d.ChallengerLogin;
            return $"{d.State} vs {other}";
        }

        private void HandleDuelAccept(RRConnection conn)
        {
            var duel = _duelManager.TryAccept(conn.LoginName);
            if (duel == null) { Debug.LogError($"[PVP-DUEL] No pending duel for {conn.LoginName}"); return; }

            var challenger = FindConnectionByLogin(duel.ChallengerLogin);
            var target = FindConnectionByLogin(duel.TargetLogin);

            // Notify both: Accepted (countdown begins)
            if (challenger != null)
                SendToClient(challenger, PVPPackets.BuildDuelStatus(
                    PVPPackets.DuelStatusType.Accepted, duel.TargetCharSqlId, 0, 0));
            if (target != null)
                SendToClient(target, PVPPackets.BuildDuelStatus(
                    PVPPackets.DuelStatusType.Accepted, duel.ChallengerCharSqlId, 0, 0));

            // Schedule Countdown→Active transition. Drained from ProcessMatchmakingTick.
            _pendingDuelActivations.Add((
                DateTime.UtcNow.AddSeconds(Managers.DuelManager.DuelInfo.CountdownSec),
                duel));
            Debug.LogError($"[PVP-DUEL] Activation scheduled for {duel.ChallengerLogin} vs {duel.TargetLogin} in {Managers.DuelManager.DuelInfo.CountdownSec}s");
        }

        private void HandleDuelDecline(RRConnection conn)
        {
            var duel = _duelManager.TryDecline(conn.LoginName);
            if (duel == null) { Debug.LogError($"[PVP-DUEL] No pending duel for {conn.LoginName}"); return; }

            var challenger = FindConnectionByLogin(duel.ChallengerLogin);
            if (challenger != null)
                SendToClient(challenger, PVPPackets.BuildDuelStatus(
                    PVPPackets.DuelStatusType.Declined, duel.TargetCharSqlId, 0, 0));
        }

        // Phase 1: pending Countdown→Active activations, drained from ProcessMatchmakingTick.
        private readonly List<(DateTime dueAt, Managers.DuelManager.DuelInfo duel)> _pendingDuelActivations
            = new List<(DateTime, Managers.DuelManager.DuelInfo)>(); // connId → deadline
        private void BroadcastPlayerMovement(RRConnection conn, byte sessionId, byte moveCount, byte[] rawMoveData)
        {
            uint avatarId = GetPlayerAvatarId(conn.LoginName);
            if (avatarId == 0) return;

            float now = Time.time;

            bool forceRelay = false;
            if (_forceRelayUntil.TryGetValue(conn.ConnId, out float deadline))
            {
                if (now < deadline)
                    forceRelay = true;
                else
                    _forceRelayUntil.Remove(conn.ConnId);
            }

            if (!forceRelay && now - conn.LastPositionUpdateTime < 0.033f) return;

            conn.LastPositionUpdateTime = now;
            _lastRelayPosX[conn.LoginName] = conn.PlayerPosX;
            _lastRelayPosY[conn.LoginName] = conn.PlayerPosY;

            // Determine what to send: real moves, or last-known-position stop signal
            byte relayMoveCount = moveCount;
            byte[] relayData = rawMoveData;

            if (moveCount > 0 && rawMoveData != null && rawMoveData.Length > 0)
            {
                // Player is moving — save last raw data for stop signal
                _lastRawMoveData[conn.LoginName] = rawMoveData;
                _lastRawMoveCount[conn.LoginName] = moveCount;
                _stopSignalSent.Remove(conn.LoginName);
            }
            else
            {
                // Player stopped (moveCount=0 from tick) — send last position ONCE
                if (_stopSignalSent.Contains(conn.LoginName)) return;
                if (!_lastRawMoveData.TryGetValue(conn.LoginName, out byte[] lastData)) return;
                relayMoveCount = _lastRawMoveCount.GetValueOrDefault(conn.LoginName, (byte)1);
                relayData = lastData;
                _stopSignalSent.Add(conn.LoginName);
            }

            if (!TryNormalizeUnitMoverUpdateData(relayMoveCount, relayData, out relayMoveCount, out relayData)) return;

            foreach (var other in _connections.Values)
            {
                if (other == conn) continue;
                if (!other.IsSpawned) continue;
                if (other.CurrentZoneGcType != conn.CurrentZoneGcType) continue;
                if (other.InstanceId != conn.InstanceId) continue;

                if (!_remoteBehaviorIds.TryGetValue(other.LoginName, out var playerMap)) continue;
                if (!playerMap.TryGetValue(conn.LoginName, out ushort remoteBehaviorId)) continue;

                var w = new LEWriter();
                w.WriteByte(0x07);
                w.WriteByte(0x35);
                w.WriteUInt16(remoteBehaviorId);
                w.WriteByte(0x65);
                w.WriteByte(0xFF);
                w.WriteByte(relayMoveCount);
                w.WriteBytes(relayData);
                if (!TryWriteRemoteAvatarEntitySynchInfo(conn, w, remoteBehaviorId, 0x65, "MP-MOVE"))
                    continue;
                w.WriteByte(0x06);
                SendToClient(other, w.ToArray());
            }
        }
        private Dictionary<string, byte[]> _lastRawMoveData = new Dictionary<string, byte[]>();
        public void SendCompressedPublic(RRConnection conn, byte dest, byte messageType, byte[] data)
            => SendCompressedA(conn, dest, messageType, data);
    }
}
