namespace DungeonRunners.Gameplay
{
    // Native PvP balance ("Pumped" / PVPBalanceModifier), reverse-engineered from the 666 client + authored
    // gc data (2026-06-17). PvP arenas declare `EntryModifier = pvp.Modifiers.Pumped` in their ZoneDef
    // (verified in DeathMatch01.zone + DeathMatchUnrated01.zone); on entry every participant is "remapped to
    // level 101" so PvP is level-independent ("magically rebalanced to make PvP more fair"). The client
    // applies this locally from the ZoneDef; arenas are Private=true, which ENFORCES EntitySynchInfo::Validate
    // (hubs like pvp_start aren't Private and skip it). So the server MUST report the same remapped HP the
    // client computes, or Validate fails with the "Oops, sync error" dialog on arena entry.
    //
    // SCAFFOLD: we set every PvP participant's max HP to the flat MaxHealth-curve value at the remap level.
    // The modifier object itself carries only DamageMod/ImmunityMod (no HP field, confirmed via the ctor +
    // vtable) — the HP comes from the level remap feeding the MaxHealth CurveTable below.
    //
    // CALIBRATION: the exact remapped value isn't fully pinned statically (the curve is DISPLAY HP and the
    // wire conversion / remap level 100-vs-101 needs confirming). The client's own crash log gives the truth:
    // if a sync error still fires, its "[Local] HP = N" line is the value the client computed — set
    // RemappedMaxHpWireOverride to N (wire) and it converges. An x32dbg read of a live Pumped player
    // (Unit max HP + Unit[0x314] level) confirms it definitively.
    public static class PvpBalance
    {
        // pvp RPGSettings PVPRemapLevel (the "remapped to level 101" the Pumped description states).
        public const int RemapLevel = 101;

        // Tables.gc -> MaxHealth CurveTable. Authored DISPLAY HP values ("for PvP balancing to determine
        // where on the curve players are for re-mapping to lvl 100"). Wire HP = display << 8.
        private static readonly (int Level, int Hp)[] MaxHealthCurve =
        {
            (1, 3000), (5, 6059), (10, 9879), (50, 40246), (75, 59531), (100, 78816), (110, 84910),
        };

        // Set non-zero to force the remapped wire HP (calibration hook: plug in the client's "[Local] HP"
        // from a crash log, or the x32dbg-read value). 0 = derive from the curve below.
        public static uint RemappedMaxHpWireOverride = 0;

        // Flat remapped DISPLAY max HP at the remap level (linear interp on the curve, ~79425 at L101).
        public static int RemappedMaxHpDisplay => InterpolateCurve(RemapLevel);

        // Flat remapped WIRE max HP (display << 8) — what the server stamps as the player's PvP max HP.
        public static uint RemappedMaxHpWire =>
            RemappedMaxHpWireOverride > 0 ? RemappedMaxHpWireOverride : (uint)RemappedMaxHpDisplay << 8;

        private static int InterpolateCurve(int level)
        {
            var c = MaxHealthCurve;
            if (level <= c[0].Level) return c[0].Hp;
            if (level >= c[c.Length - 1].Level) return c[c.Length - 1].Hp;
            for (int i = 1; i < c.Length; i++)
            {
                if (level <= c[i].Level)
                {
                    var lo = c[i - 1];
                    var hi = c[i];
                    long span = hi.Level - lo.Level;
                    if (span <= 0) return lo.Hp;
                    // Client CurveTable interp is linear with double-truncation; integer math mirrors it.
                    return lo.Hp + (int)(((long)(hi.Hp - lo.Hp) * (level - lo.Level)) / span);
                }
            }
            return c[c.Length - 1].Hp;
        }
    }
}
