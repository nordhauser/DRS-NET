using System;
using System.Collections.Generic;

namespace DungeonRunners.Combat
{
    /// <summary>
    /// Mersenne Twister RNG (MT19937) - Exact match to Dungeon Runners client implementation.
    /// 
    /// From Ghidra analysis:
    /// - State size: 0x270 (624) uint32 values
    /// - Magic constant: 0x6c078965
    /// - Native Dungeon Runners tempering masks at Random::generate(char const*) 0x0044B1F0
    /// 
    /// Server MUST send seed via opcode 0x0C and use this class for damage calculations
    /// to match client's Random::generate() output exactly.
    /// </summary>
    public class MersenneTwister
    {
        // MT19937 constants
        private const int N = 624;           // State size (0x270)
        private const int M = 397;           // Shift size (0x18D)
        private const uint MATRIX_A = 0x9908B0DF;   // Constant vector
        private const uint UPPER_MASK = 0x80000000; // Most significant bit
        private const uint LOWER_MASK = 0x7FFFFFFF; // Least significant 31 bits

        // Tempering constants (from Ghidra: generate function)
        private const uint TEMPERING_MASK_B = 0xFF3A58AD;  // From: uVar1 ^ (uVar1 & 0xff3a58ad) << 7
        private const uint TEMPERING_MASK_C = 0xFFFFDF8C;  // From: uVar1 ^ (uVar1 & 0xffffdf8c) << 0xf

        // State array and index
        private uint[] _mt = new uint[N];
        private int _mti = N + 1;  // mti == N+1 means uninitialized

        // Session 24: RNG diagnostics for sync tuning
        public uint LastSeed { get; private set; }
        public int CallsSinceReseed { get; private set; }
        public uint LastGeneratedValue { get; private set; }

        /// <summary>
        /// Create uninitialized MT - must call Seed() before Generate()
        /// </summary>
        public MersenneTwister()
        {
        }

        /// <summary>
        /// Create and seed MT
        /// </summary>
        public MersenneTwister(uint seed)
        {
            Seed(seed);
        }

        /// <summary>
        /// Seed the RNG - matches client's Random::seed() exactly
        /// 
        /// From Ghidra:
        /// *(Random **)(in_EAX + 0x14) = this;  // mt[0] = seed
        /// *(undefined4 *)(in_EAX + 0x10) = 1;  // start at index 1
        /// do {
        ///     iVar1 = *(int *)(in_EAX + 0x10);
        ///     uVar2 = *(uint *)(in_EAX + 0x10 + iVar1 * 4);
        ///     *(uint *)(in_EAX + 0x14 + iVar1 * 4) = (uVar2 >> 0x1e ^ uVar2) * 0x6c078965 + iVar1;
        ///     *(int *)(in_EAX + 0x10) = *(int *)(in_EAX + 0x10) + 1;
        /// } while (*(int *)(in_EAX + 0x10) < 0x270);
        /// </summary>
        public void Seed(uint seed)
        {
            LastSeed = seed;
            CallsSinceReseed = 0;
            _mt[0] = seed;
            for (int i = 1; i < N; i++)
            {
                // mt[i] = (mt[i-1] ^ (mt[i-1] >> 30)) * 0x6c078965 + i
                _mt[i] = (uint)(0x6c078965 * (_mt[i - 1] ^ (_mt[i - 1] >> 30)) + i);
            }
            _mti = N; // Force regeneration on first generate call
        }

        /// <summary>
        /// Generate random uint32 - matches client's Random::generate() exactly
        /// </summary>
        public uint Generate()
        {
            uint y;
            uint[] mag01 = { 0, MATRIX_A };

            // Generate N words at a time
            if (_mti >= N)
            {
                int kk;

                // If seed() hasn't been called, use default seed
                if (_mti == N + 1)
                {
                    Seed(0x1105); // Default seed from Ghidra: seed((Random *)0x1105,0)
                }

                // First loop: 0 to N-M-1 (0 to 226)
                // From Ghidra: iVar3 = 0xe3 (227 iterations)
                for (kk = 0; kk < N - M; kk++)
                {
                    y = (_mt[kk] & UPPER_MASK) | (_mt[kk + 1] & LOWER_MASK);
                    _mt[kk] = _mt[kk + M] ^ (y >> 1) ^ mag01[y & 1];
                }

                // Second loop: N-M to N-2 (227 to 622)  
                // From Ghidra: iVar3 = 0x18c (396 iterations)
                for (; kk < N - 1; kk++)
                {
                    y = (_mt[kk] & UPPER_MASK) | (_mt[kk + 1] & LOWER_MASK);
                    _mt[kk] = _mt[kk + (M - N)] ^ (y >> 1) ^ mag01[y & 1];
                }

                // Last element
                y = (_mt[N - 1] & UPPER_MASK) | (_mt[0] & LOWER_MASK);
                _mt[N - 1] = _mt[M - 1] ^ (y >> 1) ^ mag01[y & 1];

                _mti = 0;
            }

            // Get next value from state
            y = _mt[_mti++];

            // Tempering - matches Random::generate(char const*) at 0x0044B1F0:
            // uVar1 = uVar1 ^ uVar1 >> 0xb;
            // uVar1 = uVar1 ^ (uVar1 & 0xff3a58ad) << 7;
            // uVar1 = uVar1 ^ (uVar1 & 0xffffdf8c) << 0xf;
            // return uVar1 >> 0x12 ^ uVar1;
            y ^= (y >> 11);
            y ^= (y & TEMPERING_MASK_B) << 7;
            y ^= (y & TEMPERING_MASK_C) << 15;
            y ^= (y >> 18);

            CallsSinceReseed++;
            LastGeneratedValue = y;
            return y;
        }

        /// <summary>
        /// Generate random number in range [min, max] inclusive
        /// Matches client's Random::generate(ulong, ulong)
        /// 
        /// From Ghidra:
        /// uVar1 = generate(this,unaff_EDI);
        /// uVar2 = (in_EAX - unaff_EBX) + 1;  // range = max - min + 1
        /// if (uVar2 != 0) {
        ///     return uVar1 % uVar2 + unaff_EBX;  // result % range + min
        /// }
        /// return unaff_EBX;
        /// </summary>
        public uint Generate(uint min, uint max)
        {
            uint range = max - min + 1;
            if (range == 0)
                return min;
            return (Generate() % range) + min;
        }

        /// <summary>
        /// Generate random int in range [min, max] inclusive
        /// Convenience method for damage calculations
        /// </summary>
        public int GenerateInt(int min, int max)
        {
            if (max < min)
                return min;
            uint range = (uint)(max - min + 1);
            return (int)((Generate() % range) + (uint)min);
        }
    }

    public static class NativeRngLedger
    {
        public sealed class Entry
        {
            public bool IsSeed;
            public string Stream;
            public string Phase;
            public uint Seed;
            public int Before;
            public int After;
            public uint Raw;
            public string Owner;
            public uint? Value;
        }

        private static readonly List<Entry> _capture = new List<Entry>();

        public static bool CaptureEnabled { get; set; }

        public static void ClearCapture()
        {
            lock (_capture)
                _capture.Clear();
        }

        public static List<Entry> SnapshotCapture()
        {
            lock (_capture)
            {
                var snapshot = new List<Entry>(_capture.Count);
                foreach (var entry in _capture)
                    snapshot.Add(Clone(entry));
                return snapshot;
            }
        }

        private static Entry Clone(Entry entry)
        {
            return new Entry
            {
                IsSeed = entry.IsSeed,
                Stream = entry.Stream,
                Phase = entry.Phase,
                Seed = entry.Seed,
                Before = entry.Before,
                After = entry.After,
                Raw = entry.Raw,
                Owner = entry.Owner,
                Value = entry.Value
            };
        }

        private static void Capture(Entry entry)
        {
            if (!CaptureEnabled || entry == null)
                return;

            lock (_capture)
                _capture.Add(Clone(entry));
        }

        public static uint Generate(MersenneTwister rng, string stream, string phase, string owner = null)
        {
            if (rng == null)
                return 0;

            int before = rng.CallsSinceReseed;
            uint raw = rng.Generate();
            LogDraw(stream, phase, rng.LastSeed, before, rng.CallsSinceReseed, raw, owner);
            return raw;
        }

        public static uint Generate(MersenneTwister rng, string stream, string phase, uint min, uint max, string owner = null)
        {
            if (rng == null)
                return min;

            int before = rng.CallsSinceReseed;
            uint raw = rng.Generate();
            uint range = max - min + 1;
            uint value = range == 0 ? min : (raw % range) + min;
            LogDraw(stream, $"{phase ?? "draw"}[{min}..{max}]", rng.LastSeed, before, rng.CallsSinceReseed, raw, owner, value);
            return value;
        }

        public static void LogSeed(string stream, string phase, uint seed, string owner = null, int position = 0)
        {
            Capture(new Entry
            {
                IsSeed = true,
                Stream = stream ?? "unknown",
                Phase = phase ?? "seed",
                Seed = seed,
                Before = position,
                After = position,
                Owner = owner ?? string.Empty
            });
            DungeonRunners.Engine.Debug.LogError($"[RNG-LEDGER] stream={stream ?? "unknown"} phase={phase ?? "seed"} seed=0x{seed:X8} pos={position} owner='{owner ?? ""}'");
        }

        public static void LogDraw(string stream, string phase, uint seed, int before, int after, uint raw, string owner = null, uint? value = null)
        {
            Capture(new Entry
            {
                IsSeed = false,
                Stream = stream ?? "unknown",
                Phase = phase ?? "draw",
                Seed = seed,
                Before = before,
                After = after,
                Raw = raw,
                Owner = owner ?? string.Empty,
                Value = value
            });
            string valueText = value.HasValue ? $" value={value.Value}" : "";
            DungeonRunners.Engine.Debug.LogError($"[RNG-LEDGER] stream={stream ?? "unknown"} phase={phase ?? "draw"} seed=0x{seed:X8} pos={before}->{after} raw=0x{raw:X8}{valueText} owner='{owner ?? ""}'");
        }

        public static void LogSystemRandom(string phase, int value, string owner = null)
        {
            DungeonRunners.Engine.Debug.LogError($"[RNG-LEDGER] stream=serverNonNative/System.Random phase={phase ?? "draw"} value={value} owner='{owner ?? ""}' native=non-native-audit");
        }
    }

    public static class NativeRandomStreams
    {
        private static readonly MersenneTwister _globalStaticRng = new MersenneTwister();
        private static bool _globalStaticSeeded;

        public static void SeedGlobalStatic(uint seed, string source = null)
        {
            _globalStaticRng.Seed(seed);
            _globalStaticSeeded = true;
            NativeRngLedger.LogSeed("globalStatic", source ?? "_WinMain@16:time64", seed, "0x00932FF8", 0);
            DungeonRunners.Engine.Debug.LogError($"[RNG-SEED] stream=globalStatic nativeObject=0x00932FF8 seed=0x{seed:X8} source={source ?? "_WinMain@16:time64"} rngPos=0");
        }

        public static void EnsureGlobalStaticSeededFromTime64(string source = null)
        {
            if (_globalStaticSeeded)
                return;
            long seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SeedGlobalStatic(unchecked((uint)seconds), source ?? "_WinMain@16:time64");
        }

        public static uint GenerateGlobalStatic(string phase = null, string owner = null)
        {
            EnsureGlobalStaticSeededFromTime64("globalStatic:first-use");
            return NativeRngLedger.Generate(_globalStaticRng, "globalStatic", phase ?? "Random::generator", owner);
        }

        public static uint GenerateGlobalStaticRangeInclusive(uint min, uint max, string phase = null, string owner = null)
        {
            EnsureGlobalStaticSeededFromTime64("globalStatic:first-use");
            return NativeRngLedger.Generate(_globalStaticRng, "globalStatic", phase ?? "Random::generate(min,max)", min, max, owner);
        }

        public static uint GenerateGlobalSound(string phase = null, string owner = null)
        {
            return GenerateGlobalStatic(phase ?? "Weapon::playAttackSound", owner);
        }

        public static int GlobalStaticCalls => _globalStaticRng.CallsSinceReseed;
        public static uint GlobalStaticSeed => _globalStaticRng.LastSeed;
        public static int GlobalSoundCalls => GlobalStaticCalls;
        public static uint GlobalSoundSeed => GlobalStaticSeed;
    }
}
