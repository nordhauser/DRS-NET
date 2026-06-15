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


    }
}
