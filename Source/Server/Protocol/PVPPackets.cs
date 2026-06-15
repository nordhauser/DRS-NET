using System;
using DungeonRunners.Engine;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    public static class PVPPackets
    {

        public enum DuelStatusType : byte
        {
            None = 0,
            Challenged = 1,
            Accepted = 2,
            InProgress = 3,
            Won = 4,
            Lost = 5,
            Declined = 6,
            Cancelled = 7,
        }

        public static byte[] BuildDuelStatus(DuelStatusType status,
            uint opponentCharSqlId, uint opponentEntityHandle, int ratingChange)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x09);
            writer.WriteByte(0x4F);
            writer.WriteUInt32((uint)status);
            writer.WriteUInt32(opponentCharSqlId);
            writer.WriteUInt32(opponentEntityHandle);
            writer.WriteInt32(ratingChange);
            return writer.ToArray();
        }

        public static byte[] BuildPVPStatusChanged(byte pvpState, uint matchId)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x09);
            writer.WriteByte(0x4E);
            writer.WriteUInt32((uint)pvpState);
            writer.WriteUInt32(matchId);
            return writer.ToArray();
        }

        // PvP match/queue status in the exact shape GroupClient::ReadPVPStatus parses:
        //   0x09 0x4E [u32 a][u32 b][PVPMatch via writeType][PVPTeam via writeType]
        // b==0 -> still in queue; b transitions 0->nonzero -> "Your %s session is ready. Joining..."
        // (client calls enterPVPZone); match==null -> "You are no longer in the queue for: %s".
        // writeType numeric form is tag 0x04 + little-endian DJB2 TypeID; a null type is a single 0x00.
        public static byte[] BuildPVPMatchStatus(uint groupId, uint a, uint b, string matchGcPath)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x09);
            writer.WriteByte(0x4E);
            // Leading group id: processChangedPVPStatus drops the whole packet unless this == the client's
            // GroupClient+0xD0 (the player's group). Live-verified: solo group id = 1 (== GroupDirectory id).
            writer.WriteUInt32(groupId);
            writer.WriteUInt32(a);
            writer.WriteUInt32(b);
            // PVPMatch via writeType. ReadPVPStatus resolves it through getObjectDef; the 0x04+TypeID form
            // did not resolve there, so use the name form: tag 0xFF + C-string gc path. null/empty = left queue.
            if (!string.IsNullOrEmpty(matchGcPath)) { writer.WriteByte(0xFF); writer.WriteCString(matchGcPath); }
            else { writer.WriteByte(0x00); }
            writer.WriteByte(0x00); // PVPTeam: null (no team assigned while queuing)
            return writer.ToArray();
        }


    }
}
